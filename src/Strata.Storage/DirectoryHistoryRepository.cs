using Microsoft.Data.Sqlite;
using Strata.Core.Diffing;
using Strata.Core.Rollup;

namespace Strata.Storage;

/// <summary>
/// The sparse directory history: what each directory's subtree measured, whenever it moved.
/// </summary>
/// <remarks>
/// Replaces <c>snapshot_dirs</c>, which recorded every qualifying directory on every tick regardless
/// of whether anything happened to it. See docs/STORAGE-MODEL.md for the measurements; the short
/// version is 24× less storage for complete depth coverage, because a mean of 68 directories per
/// tick actually move and that table was writing 5,740.
/// <para>
/// Values are absolute rather than deltas. "The size of X at time T" is then the latest row at or
/// before T — one seek, no replay, and no drift accumulating over a year.
/// </para>
/// </remarks>
public static class DirectoryHistoryRepository
{
    /// <summary>
    /// Records the directories whose totals have drifted past <paramref name="bandBytes"/> since
    /// they were last written.
    /// </summary>
    /// <param name="candidates">
    /// Directories worth considering — the fan-out the rollup has just applied. Passing the whole
    /// volume would work and would cost a scan of every directory on it; passing what changed costs
    /// a seek each, and nothing else can have drifted.
    /// </param>
    /// <param name="bandBytes">
    /// How far a directory may drift before it earns a row. Zero records every change, which
    /// measured at 18.2 MiB a week on C: and is the default: exactness is worth more than the
    /// megabytes, and this is the number the product exists to be trusted on.
    /// </param>
    /// <returns>How many rows were written.</returns>
    public static int Record(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        long volumeId,
        DateTime takenUtc,
        IEnumerable<long> candidates,
        long bandBytes)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(candidates);

        var taken = ToUnixSeconds(takenUtc);

        // One statement rather than read-then-decide-then-write. The predicate is the dead-band, so
        // a directory that has not moved far enough costs one seek and writes nothing.
        //
        // The inequality is separate from the band rather than folded into it: a band of zero means
        // "record every change", not "record everything every tick", and ABS(x) >= 0 is true of a
        // directory that has not moved at all.
        using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = """
            INSERT OR REPLACE INTO dir_history
                (volume_id, dir_id, taken_unix, subtree_bytes, subtree_files)
            SELECT volume_id, dir_id, $t, subtree_bytes, subtree_files
            FROM dir_totals
            WHERE volume_id = $v AND dir_id = $d
              AND subtree_bytes <> recorded_bytes
              AND ABS(subtree_bytes - recorded_bytes) >= $band;
            """;

        insert.Parameters.AddWithValue("$v", volumeId);
        insert.Parameters.AddWithValue("$t", taken);
        insert.Parameters.AddWithValue("$band", bandBytes);
        insert.Parameters.Add("$d", SqliteType.Integer);
        insert.Prepare();

        using var mark = connection.CreateCommand();
        mark.Transaction = transaction;
        mark.CommandText = """
            UPDATE dir_totals SET recorded_bytes = subtree_bytes
            WHERE volume_id = $v AND dir_id = $d AND subtree_bytes <> recorded_bytes;
            """;

        mark.Parameters.AddWithValue("$v", volumeId);
        mark.Parameters.Add("$d", SqliteType.Integer);
        mark.Prepare();

        var written = 0;

        foreach (var dirId in candidates)
        {
            insert.Parameters["$d"].Value = dirId;
            var rows = insert.ExecuteNonQuery();

            if (rows > 0)
            {
                mark.Parameters["$d"].Value = dirId;
                mark.ExecuteNonQuery();
                written += rows;
            }
        }

        return written;
    }

    /// <summary>
    /// Writes an opening row for every directory on the volume.
    /// </summary>
    /// <remarks>
    /// Run after a baseline, which rebuilds <c>dir_totals</c> wholesale and so invalidates every
    /// <c>recorded_bytes</c> along with it.
    /// <para>
    /// Covers every directory rather than only the large ones, because the Explorer drills down and
    /// it can drill anywhere: a directory with no row at or before the time being viewed looks as
    /// though it did not exist, which for a small folder that simply never changed would be a lie.
    /// On C: that is 571k rows and about 15 MiB, once per baseline rather than once per tick.
    /// </para>
    /// </remarks>
    public static int WriteAnchor(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        long volumeId,
        DateTime takenUtc)
    {
        ArgumentNullException.ThrowIfNull(connection);

        using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = """
            INSERT OR REPLACE INTO dir_history
                (volume_id, dir_id, taken_unix, subtree_bytes, subtree_files)
            SELECT volume_id, dir_id, $t, subtree_bytes, subtree_files
            FROM dir_totals WHERE volume_id = $v;
            """;

        insert.Parameters.AddWithValue("$v", volumeId);
        insert.Parameters.AddWithValue("$t", ToUnixSeconds(takenUtc));

        var written = insert.ExecuteNonQuery();

        using var mark = connection.CreateCommand();
        mark.Transaction = transaction;
        mark.CommandText =
            "UPDATE dir_totals SET recorded_bytes = subtree_bytes WHERE volume_id = $v;";
        mark.Parameters.AddWithValue("$v", volumeId);
        mark.ExecuteNonQuery();

        return written;
    }

    /// <summary>
    /// Merges the current directory tree into the name dimension.
    /// </summary>
    /// <remarks>
    /// Merges rather than replaces, which is the whole reason this table exists separately from
    /// <c>dir_totals</c>: a baseline truncates that one, and history older than the last baseline
    /// would lose its names with it.
    /// <para>
    /// A rename keeps the directory's id, so its history follows it to the new name. That is the
    /// right answer — it is the same directory — but it does mean a path shown against an old
    /// measurement is the path that directory has now, not the one it had then.
    /// </para>
    /// </remarks>
    public static int MergeNames(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        long volumeId,
        DateTime seenUtc)
    {
        ArgumentNullException.ThrowIfNull(connection);

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO dir_names (volume_id, dir_id, parent_id, name, depth, first_seen_utc)
            SELECT volume_id, dir_id, parent_id, name, depth, $t FROM dir_totals
            WHERE volume_id = $v
            ON CONFLICT (volume_id, dir_id) DO UPDATE SET
                parent_id = excluded.parent_id,
                name      = excluded.name,
                depth     = excluded.depth;
            """;

        command.Parameters.AddWithValue("$v", volumeId);
        command.Parameters.AddWithValue("$t", seenUtc.ToString("O"));

        return command.ExecuteNonQuery();
    }

    /// <summary>Loads the whole name dimension for a volume, for path building and attribution.</summary>
    /// <remarks>
    /// A sequential read of one table. On C: that is 571k rows; measured, it is worth doing once per
    /// request rather than resolving ancestors one seek at a time.
    /// </remarks>
    public static Dictionary<long, DirectoryNode> LoadNames(SqliteConnection connection, long volumeId)
    {
        ArgumentNullException.ThrowIfNull(connection);

        var nodes = new Dictionary<long, DirectoryNode>();

        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT dir_id, parent_id, name, depth FROM dir_names WHERE volume_id = $v;";
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
    /// Every directory's subtree totals as they stood at an instant, for the directories that have
    /// any history at or before it.
    /// </summary>
    /// <remarks>
    /// The latest row at or before the instant, per directory. Expressed as a group-by rather than a
    /// correlated subquery per directory: one pass over the volume's history instead of one seek per
    /// directory, which matters because the caller wants thousands of them at once.
    /// </remarks>
    public static Dictionary<long, HistoryDiff.Measurement> StateAt(
        SqliteConnection connection,
        long volumeId,
        DateTime instantUtc)
    {
        ArgumentNullException.ThrowIfNull(connection);

        var state = new Dictionary<long, HistoryDiff.Measurement>();

        using var command = connection.CreateCommand();
        // The bare columns take their values from the row that produced MAX(taken_unix). That is a
        // documented SQLite guarantee for a single min/max aggregate, and it is what turns "latest
        // row at or before T, per directory" into one pass instead of a seek per directory.
        command.CommandText = """
            SELECT dir_id, MAX(taken_unix), subtree_bytes, subtree_files
            FROM dir_history
            WHERE volume_id = $v AND taken_unix <= $t
            GROUP BY dir_id;
            """;

        command.Parameters.AddWithValue("$v", volumeId);
        command.Parameters.AddWithValue("$t", ToUnixSeconds(instantUtc));

        using var reader = command.ExecuteReader();

        while (reader.Read())
        {
            state[reader.GetInt64(0)] = new HistoryDiff.Measurement(reader.GetInt64(2), reader.GetInt64(3));
        }

        return state;
    }

    /// <summary>
    /// Seconds since the epoch, treating an unmarked value as UTC.
    /// </summary>
    /// <remarks>
    /// Every timestamp in this schema is UTC by convention, so a value arriving unmarked is UTC
    /// rather than local. Converting it as local would shift history by the timezone offset, which
    /// is the kind of error that produces a plausible chart.
    /// </remarks>
    private static long ToUnixSeconds(DateTime value)
        => new DateTimeOffset(
            value.Kind == DateTimeKind.Local
                ? value.ToUniversalTime()
                : DateTime.SpecifyKind(value, DateTimeKind.Utc)).ToUnixTimeSeconds();
}
