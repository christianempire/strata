using Microsoft.Data.Sqlite;
using Strata.Core.Diffing;
using Strata.Core.Rollup;
using Strata.Core.Scanning;

namespace Strata.Storage;

/// <summary>
/// What one tick's deltas did to the directory rollup.
/// </summary>
/// <remarks>
/// <see cref="Touched"/> is the accumulated per-ancestor delta, which is exactly the set a sparse
/// history model would have to consider writing a row for. It is computed anyway to do the update,
/// so returning it costs nothing and is the only way to size that model against real activity rather
/// than against an estimate.
/// </remarks>
public readonly record struct RollupFanOut(
    int RowsUpdated,
    IReadOnlyDictionary<long, DirectoryDelta> Touched)
{
    public static readonly RollupFanOut Empty =
        new(0, new Dictionary<long, DirectoryDelta>());
}

/// <summary>Persistence for the incremental path: cursors, rollup maintenance, and the events feed.</summary>
public static class IncrementalRepository
{
    public static UsnCursor GetCursor(SqliteConnection connection, long volumeId)
    {
        ArgumentNullException.ThrowIfNull(connection);

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT journal_id, next_usn FROM usn_cursors WHERE volume_id = $v;";
        command.Parameters.AddWithValue("$v", volumeId);

        using var reader = command.ExecuteReader();

        return reader.Read()
            ? new UsnCursor((ulong)reader.GetInt64(0), reader.GetInt64(1))
            : UsnCursor.None;
    }

    /// <summary>
    /// Records where the journal has been read up to.
    /// </summary>
    /// <remarks>
    /// Must be written inside the same transaction as the changes it describes. A cursor committed
    /// ahead of its data would silently skip those records forever, which is the one failure mode
    /// that corrupts history without any error to notice.
    /// </remarks>
    public static void SaveCursor(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long volumeId,
        UsnCursor cursor)
    {
        ArgumentNullException.ThrowIfNull(connection);

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO usn_cursors (volume_id, journal_id, next_usn, updated_utc)
            VALUES ($v, $j, $n, $t)
            ON CONFLICT (volume_id) DO UPDATE SET
                journal_id  = excluded.journal_id,
                next_usn    = excluded.next_usn,
                updated_utc = excluded.updated_utc;
            """;

        command.Parameters.AddWithValue("$v", volumeId);
        command.Parameters.AddWithValue("$j", (long)cursor.JournalId);
        command.Parameters.AddWithValue("$n", cursor.NextUsn);
        command.Parameters.AddWithValue("$t", DateTime.UtcNow.ToString("O"));
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// Applies per-directory deltas up every ancestor chain.
    /// </summary>
    /// <remarks>
    /// This is what keeps directory totals correct without recomputing them. Recomputing would mean
    /// a recursive aggregation over every row in <c>files</c> - millions - on every tick, which
    /// would make the incremental scan fast at reading the journal and then slow at everything after.
    /// <para>
    /// Ancestors are resolved in memory first so the walk costs one query per changed directory
    /// rather than one per level, and the whole update lands in a single statement batch.
    /// </para>
    /// </remarks>
    public static RollupFanOut ApplyDeltas(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long volumeId,
        IReadOnlyDictionary<long, DirectoryDelta> deltas)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(deltas);

        if (deltas.Count == 0)
        {
            return RollupFanOut.Empty;
        }

        var parents = LoadParentChain(connection, transaction, volumeId, deltas.Keys);

        // Accumulate in memory: many changed directories share ancestors, and a shared ancestor
        // should be written once with the summed delta rather than once per descendant.
        var totals = new Dictionary<long, DirectoryDelta>();

        foreach (var (dirId, delta) in deltas)
        {
            var current = dirId;
            var guard = 0;

            while (parents.TryGetValue(current, out var parent))
            {
                totals[current] = totals.TryGetValue(current, out var existing) ? existing + delta : delta;

                if (parent == current || guard++ > 4096)
                {
                    break;
                }

                current = parent;
            }
        }

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE dir_totals
            SET subtree_bytes   = subtree_bytes + $b,
                subtree_on_disk = subtree_on_disk + $o,
                subtree_files   = subtree_files + $f
            WHERE volume_id = $v AND dir_id = $d;
            """;

        command.Parameters.AddWithValue("$v", volumeId);
        command.Parameters.Add("$d", SqliteType.Integer);
        command.Parameters.Add("$b", SqliteType.Integer);
        command.Parameters.Add("$o", SqliteType.Integer);
        command.Parameters.Add("$f", SqliteType.Integer);
        command.Prepare();

        var updated = 0;

        foreach (var (dirId, delta) in totals)
        {
            command.Parameters["$d"].Value = dirId;
            command.Parameters["$b"].Value = delta.OwnBytesDelta;
            command.Parameters["$o"].Value = delta.OwnOnDiskDelta;
            command.Parameters["$f"].Value = delta.FileCountDelta;
            updated += command.ExecuteNonQuery();
        }

        return new RollupFanOut(updated, totals);
    }

    /// <summary>Loads the directory tree above a set of changed directories.</summary>
    private static Dictionary<long, long> LoadParentChain(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long volumeId,
        IEnumerable<long> seeds)
    {
        var parents = new Dictionary<long, long>();
        var pending = new Queue<long>(seeds);

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT parent_id FROM dir_totals WHERE volume_id = $v AND dir_id = $d;";
        command.Parameters.AddWithValue("$v", volumeId);
        command.Parameters.Add("$d", SqliteType.Integer);

        while (pending.Count > 0)
        {
            var dirId = pending.Dequeue();

            if (parents.ContainsKey(dirId))
            {
                continue;
            }

            command.Parameters["$d"].Value = dirId;
            var value = command.ExecuteScalar();

            if (value is null)
            {
                continue;
            }

            var parentId = Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture);
            parents[dirId] = parentId;

            if (parentId != dirId)
            {
                pending.Enqueue(parentId);
            }
        }

        return parents;
    }

    /// <summary>
    /// Brings a cached directory tree up to date with directories created since it was loaded.
    /// </summary>
    /// <remarks>
    /// The tree is loaded once and kept because it is half a million rows on a system volume, and
    /// re-reading it every tick would cost more than the journal read the tick exists to perform.
    /// The flaw in that is directories the tick has just discovered: the rollup handles them, because
    /// it resolves ancestors from <c>dir_totals</c> on every tick, but attribution works off the
    /// cached tree and silently drops a delta whose directory it has never heard of.
    /// <para>
    /// That is not a small gap. A game installing into a new folder is the product's headline
    /// example, and a new folder is exactly the case the cache did not have. Its bytes landed in the
    /// totals and never appeared in the change feed.
    /// </para>
    /// <para>
    /// Walks up from each unknown directory until it reaches one already cached, so a tree created
    /// twenty levels deep in a single tick is stitched on in one pass.
    /// </para>
    /// </remarks>
    public static int AddMissingNodes(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        long volumeId,
        Dictionary<long, DirectoryNode> tree,
        IEnumerable<long> directoryIds)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(tree);
        ArgumentNullException.ThrowIfNull(directoryIds);

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT parent_id, name, depth FROM dir_totals
            WHERE volume_id = $v AND dir_id = $d;
            """;

        command.Parameters.AddWithValue("$v", volumeId);
        command.Parameters.Add("$d", SqliteType.Integer);

        var added = 0;
        var pending = new Queue<long>(directoryIds);

        while (pending.Count > 0)
        {
            var dirId = pending.Dequeue();

            if (tree.ContainsKey(dirId))
            {
                continue;
            }

            command.Parameters["$d"].Value = dirId;

            using (var reader = command.ExecuteReader())
            {
                if (!reader.Read())
                {
                    // Created and removed inside the same tick, or otherwise gone. Nothing to
                    // attribute it to and nothing to fix.
                    continue;
                }

                var parentId = reader.GetInt64(0);

                tree[dirId] = new DirectoryNode(dirId, parentId, reader.GetString(1))
                {
                    Depth = reader.GetInt32(2),
                };

                added++;

                if (parentId != dirId)
                {
                    pending.Enqueue(parentId);
                }
            }
        }

        return added;
    }

    /// <summary>Loads the directory tree for a volume, for path building and attribution.</summary>
    public static Dictionary<long, DirectoryNode> LoadDirectoryTree(SqliteConnection connection, long volumeId)
    {
        ArgumentNullException.ThrowIfNull(connection);

        var nodes = new Dictionary<long, DirectoryNode>();

        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT dir_id, parent_id, name, depth FROM dir_totals WHERE volume_id = $v;";
        command.Parameters.AddWithValue("$v", volumeId);

        using var reader = command.ExecuteReader();

        while (reader.Read())
        {
            var node = new DirectoryNode(reader.GetInt64(0), reader.GetInt64(1), reader.GetString(2))
            {
                Depth = reader.GetInt32(3),
            };

            nodes[node.Id] = node;
        }

        return nodes;
    }

    /// <summary>
    /// Records an incremental tick as a snapshot.
    /// </summary>
    /// <remarks>
    /// Every tick is a snapshot: that is what makes the timeline a continuous series rather than a
    /// handful of points whenever a baseline happened to run. Totals are read from the root of the
    /// maintained rollup, which is exactly why that rollup is kept incrementally - recomputing them
    /// would mean aggregating millions of rows on every tick.
    /// </remarks>
    public static long CreateIncrementalSnapshot(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long volumeId,
        DateTime takenUtc,
        long durationMs,
        long freeBytes)
    {
        ArgumentNullException.ThrowIfNull(connection);

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO snapshots
                (volume_id, taken_utc, scan_tier, duration_ms, total_bytes, total_on_disk,
                 free_bytes, file_count, dir_count)
            SELECT $v, $t, 2, $d,
                   COALESCE(MAX(CASE WHEN depth = 0 THEN subtree_bytes END), 0),
                   COALESCE(MAX(CASE WHEN depth = 0 THEN subtree_on_disk END), 0),
                   $f,
                   COALESCE(MAX(CASE WHEN depth = 0 THEN subtree_files END), 0),
                   COUNT(*)
            FROM dir_totals WHERE volume_id = $v
            RETURNING id;
            """;

        command.Parameters.AddWithValue("$v", volumeId);
        command.Parameters.AddWithValue("$t", takenUtc.ToString("O"));
        command.Parameters.AddWithValue("$d", durationMs);
        command.Parameters.AddWithValue("$f", freeBytes);

        return Convert.ToInt64(
            command.ExecuteScalar() ?? throw new InvalidOperationException("Failed to record the tick."),
            System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>Writes the human-readable change log that the timeline screen reads from.</summary>
    public static int WriteEvents(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long volumeId,
        long snapshotId,
        DateTime whenUtc,
        IReadOnlyList<AttributedChange> changes)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(changes);

        if (changes.Count == 0)
        {
            return 0;
        }

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO events
                (volume_id, snapshot_id, when_utc, path, delta_bytes, churn_bytes, file_delta, kind)
            VALUES ($v, $s, $w, $p, $d, $c, $f, $k);
            """;

        command.Parameters.AddWithValue("$v", volumeId);
        command.Parameters.AddWithValue("$s", snapshotId);
        command.Parameters.AddWithValue("$w", whenUtc.ToString("O"));
        command.Parameters.Add("$p", SqliteType.Text);
        command.Parameters.Add("$d", SqliteType.Integer);
        command.Parameters.Add("$c", SqliteType.Integer);
        command.Parameters.Add("$f", SqliteType.Integer);
        command.Parameters.Add("$k", SqliteType.Integer);
        command.Prepare();

        foreach (var change in changes)
        {
            command.Parameters["$p"].Value = change.Path;
            command.Parameters["$d"].Value = change.DeltaBytes;
            command.Parameters["$c"].Value = change.ChurnBytes;
            command.Parameters["$f"].Value = change.FileCountDelta;
            command.Parameters["$k"].Value = (int)change.Kind;
            command.ExecuteNonQuery();
        }

        return changes.Count;
    }

    /// <summary>
    /// Forgets where the journal had been read up to, so the next tick starts from wherever it is now.
    /// </summary>
    /// <remarks>
    /// Belongs with every baseline, and its absence was a trap with no way out. A journal that has
    /// been reset or has wrapped past the stored cursor makes the tick report
    /// <c>JournalInvalidated</c>, whose remedy is "a full baseline is needed" — but a baseline only
    /// set <c>baseline_utc</c> and left the dead cursor in place, so the next tick failed the same
    /// way and asked for the same baseline again. A volume in that state could never resume
    /// incremental scanning, however many times it was rescanned.
    /// <para>
    /// Deleting the row rather than writing a new one is deliberate: an absent cursor is already the
    /// documented "start fresh" signal, and <c>Win32UsnJournal.Read</c> begins at the journal's
    /// current position when it sees one. The cost is that changes made between the baseline and the
    /// first tick are not in the change feed — they are in the baseline's own totals, and the
    /// alternative is a hole in history, which this project treats as the worse failure.
    /// </para>
    /// </remarks>
    public static void ResetCursor(SqliteConnection connection, long volumeId)
    {
        ArgumentNullException.ThrowIfNull(connection);

        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM usn_cursors WHERE volume_id = $v;";
        command.Parameters.AddWithValue("$v", volumeId);
        command.ExecuteNonQuery();
    }

    /// <summary>Marks a volume as having a usable baseline, which Tier 2 requires before it can run.</summary>
    public static void MarkBaseline(SqliteConnection connection, long volumeId, DateTime whenUtc)
    {
        ArgumentNullException.ThrowIfNull(connection);

        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE volumes SET baseline_utc = $t WHERE id = $v;";
        command.Parameters.AddWithValue("$t", whenUtc.ToString("O"));
        command.Parameters.AddWithValue("$v", volumeId);
        command.ExecuteNonQuery();
    }

    public static bool HasBaseline(SqliteConnection connection, long volumeId)
    {
        ArgumentNullException.ThrowIfNull(connection);

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT baseline_utc FROM volumes WHERE id = $v;";
        command.Parameters.AddWithValue("$v", volumeId);

        return command.ExecuteScalar() is string;
    }
}
