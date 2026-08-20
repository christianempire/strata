using Microsoft.Data.Sqlite;
using Strata.Core.Retention;

namespace Strata.Storage;

/// <summary>What one retention pass did.</summary>
public sealed record RetentionResult
{
    /// <summary>Measurements thinned out of <c>dir_history</c>.</summary>
    public int HistoryRemoved { get; init; }

    /// <summary>Rows dropped from the audit log.</summary>
    public int ScanLogRemoved { get; init; }

    /// <summary>Rows <c>dir_history</c> held before the pass.</summary>
    public long HistoryBefore { get; init; }

    public TimeSpan Duration { get; init; }

    /// <summary>Nothing was written; the counts are what a real pass would have removed.</summary>
    public bool DryRun { get; init; }

    public int TotalRemoved => HistoryRemoved + ScanLogRemoved;
}

/// <summary>
/// Thins recorded history down to the configured resolutions.
/// </summary>
/// <remarks>
/// The one statement that matters is the <c>dir_history</c> downsample, and its shape is dictated by
/// the storage model rather than by convenience. Sizes there are absolute and sparse, so "the size
/// of X at T" is the latest row at or before T. Deleting a directory's last row before the horizon
/// therefore does not lose a measurement, it makes every query in that period answer with a newer
/// size — silently, and indistinguishably from a correct answer. So the rule is <b>keep the newest
/// row per directory per bucket</b>, and it is expressed as one window function rather than as a
/// date range.
/// <para>
/// <c>snapshots</c> and <c>events</c> are deliberately left alone. <c>events</c> is the table the
/// most valuable screen reads and it is tiny — 197 rows against 3.9M of history on the target
/// machine. <c>snapshots</c> is smaller still, is what the Timeline's used-space series is drawn
/// from, and carries <c>ON DELETE CASCADE</c> to <c>events</c>: thinning it would silently take the
/// change log with it, which is the opposite of what the architecture asks for.
/// </para>
/// <para>
/// No <c>VACUUM</c>. Deleting rows returns their pages to SQLite's free list, where the next weeks
/// of history are written instead of growing the file; rewriting a 680 MB database nightly to hand
/// the space back to Windows would be exactly the kind of background disk work this product exists
/// to not do. <c>strata prune --compact</c> is there for when somebody actually wants the file
/// smaller.
/// </para>
/// </remarks>
public static class RetentionRepository
{
    /// <summary>
    /// Thins history and the audit log to the configured tiers.
    /// </summary>
    /// <param name="nowUtc">
    /// What to treat as the present. A parameter rather than <c>DateTime.UtcNow</c> so a pass can be
    /// exercised against real data without waiting a week for the data to age into the tiers.
    /// </param>
    public static RetentionResult Run(
        SqliteConnection connection,
        RetentionTiers tiers,
        DateTime nowUtc,
        bool dryRun = false)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(tiers);

        var clock = System.Diagnostics.Stopwatch.StartNew();
        var horizons = RetentionHorizons.From(nowUtc, tiers);
        var before = HistoryRows(connection);

        // One transaction for both, so a pass either happened or did not. It is a single writer on
        // the scheduler thread and the statements are seconds apart at worst.
        using var transaction = connection.BeginTransaction();

        var history = Downsample(connection, transaction, horizons, dryRun);
        var log = PruneScanLog(connection, transaction, nowUtc.AddDays(-tiers.ScanLogDays), dryRun);

        if (dryRun)
        {
            transaction.Rollback();
        }
        else
        {
            transaction.Commit();
        }

        clock.Stop();

        return new RetentionResult
        {
            HistoryRemoved = history,
            ScanLogRemoved = log,
            HistoryBefore = before,
            Duration = clock.Elapsed,
            DryRun = dryRun,
        };
    }

    /// <summary>
    /// Keeps the newest measurement per directory per bucket, for everything past the full-resolution
    /// window.
    /// </summary>
    /// <remarks>
    /// Buckets are aligned to the epoch by the integer division, not to the moment this runs, so a
    /// row lands in the same bucket whenever the pass happens. A bucket that moved with the clock
    /// would thin a different set every night and walk every directory down to one row over a few
    /// months.
    /// <para>
    /// The tier is in the partition alongside the bucket start. Without it, an hourly bucket
    /// starting at midnight and a daily bucket starting at the same midnight — which happens to rows
    /// either side of a tier boundary — would be treated as one bucket and thin a row that belongs
    /// to neither.
    /// </para>
    /// <para>
    /// The scan is deliberate. The predicate is on <c>taken_unix</c>, which the primary key
    /// <c>(volume_id, dir_id, taken_unix)</c> does not lead with, so this reads the table. An index
    /// on time would cost 19.1 bytes a row — a 72% overhead on the fastest-growing table in the
    /// schema, measured in docs/STORAGE-MODEL.md — to speed up something that runs once a night.
    /// </para>
    /// </remarks>
    private static int Downsample(
        SqliteConnection connection,
        SqliteTransaction transaction,
        RetentionHorizons horizons,
        bool dryRun)
    {
        const string Doomed = """
            SELECT volume_id, dir_id, taken_unix FROM (
                SELECT volume_id, dir_id, taken_unix,
                       ROW_NUMBER() OVER (
                           PARTITION BY volume_id, dir_id, tier, bucket
                           ORDER BY taken_unix DESC
                       ) AS rank
                FROM (
                    SELECT volume_id, dir_id, taken_unix,
                           CASE WHEN taken_unix >= $hourly THEN 1
                                WHEN taken_unix >= $daily  THEN 2
                                ELSE 3 END AS tier,
                           CASE WHEN taken_unix >= $hourly THEN taken_unix / 3600 * 3600
                                WHEN taken_unix >= $daily  THEN taken_unix / 86400 * 86400
                                ELSE taken_unix / 604800 * 604800 END AS bucket
                    FROM dir_history
                    WHERE taken_unix < $full AND taken_unix >= 0
                )
            ) WHERE rank > 1
            """;

        using var command = connection.CreateCommand();
        command.Transaction = transaction;

        command.CommandText = dryRun
            ? $"SELECT COUNT(*) FROM ({Doomed});"
            : $"DELETE FROM dir_history WHERE (volume_id, dir_id, taken_unix) IN ({Doomed});";

        command.Parameters.AddWithValue("$full", horizons.FullFromUnix);
        command.Parameters.AddWithValue("$hourly", horizons.HourlyFromUnix);
        command.Parameters.AddWithValue("$daily", horizons.DailyFromUnix);

        return dryRun
            ? Convert.ToInt32(command.ExecuteScalar() ?? 0, System.Globalization.CultureInfo.InvariantCulture)
            : command.ExecuteNonQuery();
    }

    /// <summary>
    /// Drops audit rows past their horizon outright.
    /// </summary>
    /// <remarks>
    /// Deleted rather than thinned, because nothing reads the scan log as a series: no query asks
    /// what the log said at time T, so no row is load-bearing for a later one. That is the property
    /// <c>dir_history</c> does not have, and the reason these are two different statements rather
    /// than one shared helper.
    /// </remarks>
    private static int PruneScanLog(
        SqliteConnection connection,
        SqliteTransaction transaction,
        DateTime beforeUtc,
        bool dryRun)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;

        command.CommandText = dryRun
            ? "SELECT COUNT(*) FROM scan_log WHERE when_utc < $before;"
            : "DELETE FROM scan_log WHERE when_utc < $before;";

        command.Parameters.AddWithValue("$before", beforeUtc.ToString("O"));

        return dryRun
            ? Convert.ToInt32(command.ExecuteScalar() ?? 0, System.Globalization.CultureInfo.InvariantCulture)
            : command.ExecuteNonQuery();
    }

    /// <summary>What one volume's recorded history amounts to.</summary>
    public sealed record VolumeHistory
    {
        public required long VolumeId { get; init; }

        public long Rows { get; init; }

        /// <summary>The oldest real measurement still held, or null when there is none.</summary>
        public string? OldestUtc { get; init; }

        public string? NewestUtc { get; init; }
    }

    /// <summary>
    /// How much history each volume is holding.
    /// </summary>
    /// <remarks>
    /// A full scan of <c>dir_history</c> — measured at 272 ms over 3.9M rows on the target machine,
    /// including process startup — because nothing indexes the row count, and adding an index to
    /// speed up a Settings panel is exactly the trade migration 004 exists to warn about. Fine for a
    /// screen somebody opens on purpose; deliberately not on any polled path.
    /// </remarks>
    public static IReadOnlyList<VolumeHistory> PerVolume(SqliteConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);

        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT volume_id, COUNT(*), MIN(taken_unix), MAX(taken_unix)
            FROM dir_history GROUP BY volume_id;
            """;

        var rows = new List<VolumeHistory>();
        using var reader = command.ExecuteReader();

        while (reader.Read())
        {
            rows.Add(new VolumeHistory
            {
                VolumeId = reader.GetInt64(0),
                Rows = reader.GetInt64(1),

                // The epoch-dated opening rows from migration 005 are placeholders rather than
                // measurements, and reporting "history since 1970" would claim coverage that is not
                // there. Stamp answers null for them.
                OldestUtc = Stamp(reader.IsDBNull(2) ? 0 : reader.GetInt64(2)),
                NewestUtc = Stamp(reader.IsDBNull(3) ? 0 : reader.GetInt64(3)),
            });
        }

        return rows;
    }

    private static string? Stamp(long unix)
        => unix <= 0 ? null : DateTime.UnixEpoch.AddSeconds(unix).ToString("O");

    /// <summary>Rows in the one table that grows with time.</summary>
    public static long HistoryRows(SqliteConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM dir_history;";

        return Convert.ToInt64(command.ExecuteScalar() ?? 0L, System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Rebuilds the file so freed pages go back to Windows rather than to SQLite's free list.
    /// </summary>
    /// <remarks>
    /// Never part of the nightly pass. It rewrites the whole database — 680 MB on the target
    /// machine — which is precisely the sustained background disk work the product promises not to
    /// do, in exchange for a smaller file that the next few weeks of history would have reused
    /// anyway.
    /// </remarks>
    public static void Compact(SqliteConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);

        using var vacuum = connection.CreateCommand();
        vacuum.CommandText = "VACUUM;";
        vacuum.ExecuteNonQuery();

        // Folded back into the database file and the log truncated, because otherwise the whole
        // rebuild sits in the write-ahead log and the file on disk looks *larger* than before. The
        // first version of this reported "648.8 MB -> 1 GB (+377.5 MB)" for a compaction that had in
        // fact reclaimed 137 MB, which is the sort of number that gets believed.
        using var checkpoint = connection.CreateCommand();
        checkpoint.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
        checkpoint.ExecuteNonQuery();
    }
}
