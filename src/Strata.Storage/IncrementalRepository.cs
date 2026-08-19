using Microsoft.Data.Sqlite;
using Strata.Core.Diffing;
using Strata.Core.Rollup;
using Strata.Core.Scanning;

namespace Strata.Storage;

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
    public static int ApplyDeltas(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long volumeId,
        IReadOnlyDictionary<long, DirectoryDelta> deltas)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(deltas);

        if (deltas.Count == 0)
        {
            return 0;
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
                subtree_on_disk = subtree_on_disk + $b,
                subtree_files   = subtree_files + $f
            WHERE volume_id = $v AND dir_id = $d;
            """;

        command.Parameters.AddWithValue("$v", volumeId);
        command.Parameters.Add("$d", SqliteType.Integer);
        command.Parameters.Add("$b", SqliteType.Integer);
        command.Parameters.Add("$f", SqliteType.Integer);
        command.Prepare();

        var updated = 0;

        foreach (var (dirId, delta) in totals)
        {
            command.Parameters["$d"].Value = dirId;
            command.Parameters["$b"].Value = delta.OwnBytesDelta;
            command.Parameters["$f"].Value = delta.FileCountDelta;
            updated += command.ExecuteNonQuery();
        }

        return updated;
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
