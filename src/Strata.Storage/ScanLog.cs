using Microsoft.Data.Sqlite;
using Strata.Core.Scanning;

namespace Strata.Storage;

/// <summary>What a scheduler tick did.</summary>
public enum ScanOutcome
{
    /// <summary>The tick ran and its result is in the database.</summary>
    Ran = 1,

    /// <summary>The gate said no. <see cref="ScanLogRow.Reason"/> says why.</summary>
    Deferred = 2,

    /// <summary>It tried and threw.</summary>
    Failed = 3,
}

/// <summary>One attempted scan, whether or not it happened.</summary>
public sealed record ScanLogRow
{
    public long Id { get; init; }

    /// <summary>When the scheduler cycle that produced this row began. Rows of one tick share it.</summary>
    public required string CycleUtc { get; init; }

    public required string WhenUtc { get; init; }

    public long? VolumeId { get; init; }

    /// <summary>Display name, kept alongside the id so a row survives the volume being forgotten.</summary>
    public required string Volume { get; init; }

    public ScanTier Tier { get; init; }

    public ScanOutcome Result { get; init; }

    /// <summary>Why it deferred, or what it did. Shown verbatim.</summary>
    public string Reason { get; init; } = "";

    public long DurationMs { get; init; }

    /// <summary>Journal records read, for an incremental tick.</summary>
    public long Records { get; init; }

    /// <summary>Rows this tick added to the change feed.</summary>
    public long Events { get; init; }
}

/// <summary>How the day has gone, for the line under the log.</summary>
public sealed record ScanLogSummary
{
    public long Ran { get; init; }

    public long Deferred { get; init; }

    public long Failed { get; init; }

    /// <summary>Median of the ticks that ran. Median rather than mean: one 208 s recovery walk
    /// drags a mean far enough to misrepresent every other tick in the window.</summary>
    public long MedianMs { get; init; }

    public long WorstMs { get; init; }

    /// <summary>Which volume owned the worst tick, so the outlier explains itself.</summary>
    public string WorstVolume { get; init; } = "";

    /// <summary>Total time spent scanning in the window.</summary>
    public long TotalMs { get; init; }
}

/// <summary>
/// Records what the scheduler decided, including the decisions to do nothing.
/// </summary>
/// <remarks>
/// The project's first non-negotiable is that the scanner is invisible during work and gaming, and
/// until this table existed there was no evidence either way after the fact — a deferral went to the
/// console at Debug and then nowhere. A skipped tick is the row that proves the constraint is being
/// honoured, so it is written rather than being the absence of a write.
/// <para>
/// Writes take the caller's connection. A tick already has one open inside its own transaction, and
/// opening a second writer against the same file is how a busy timeout gets earned for nothing.
/// </para>
/// </remarks>
public static class ScanLogRepository
{
    public static long Append(SqliteConnection connection, ScanLogRow row)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(row);

        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO scan_log
                (cycle_utc, when_utc, volume_id, volume, tier, result, reason, duration_ms,
                 records, events)
            VALUES ($cycle, $when, $vid, $vol, $tier, $result, $reason, $ms, $records, $events)
            RETURNING id;
            """;

        command.Parameters.AddWithValue("$cycle", row.CycleUtc);
        command.Parameters.AddWithValue("$when", row.WhenUtc);
        command.Parameters.AddWithValue("$vid", (object?)row.VolumeId ?? DBNull.Value);
        command.Parameters.AddWithValue("$vol", row.Volume);
        command.Parameters.AddWithValue("$tier", (int)row.Tier);
        command.Parameters.AddWithValue("$result", (int)row.Result);
        command.Parameters.AddWithValue("$reason", row.Reason);
        command.Parameters.AddWithValue("$ms", row.DurationMs);
        command.Parameters.AddWithValue("$records", row.Records);
        command.Parameters.AddWithValue("$events", row.Events);

        return (long)(command.ExecuteScalar() ?? 0L);
    }
}

/// <summary>Read side of <c>scan_log</c>, which is what the Settings screen's audit table shows.</summary>
public static class ScanLogQueries
{
    public static List<ScanLogRow> List(
        SqliteConnection connection,
        long? volumeId,
        DateTime? fromUtc,
        DateTime? toUtc,
        ScanOutcome? result,
        int limit)
    {
        ArgumentNullException.ThrowIfNull(connection);

        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, cycle_utc, when_utc, volume_id, volume, tier, result, reason, duration_ms,
                   records, events
            FROM scan_log
            WHERE ($v IS NULL OR volume_id = $v)
              AND ($f IS NULL OR when_utc >= $f)
              AND ($t IS NULL OR when_utc <= $t)
              AND ($r IS NULL OR result = $r)
            ORDER BY when_utc DESC, id DESC
            LIMIT $limit;
            """;

        command.Parameters.AddWithValue("$v", (object?)volumeId ?? DBNull.Value);
        command.Parameters.AddWithValue("$f", Timestamps.Bound(fromUtc));
        command.Parameters.AddWithValue("$t", Timestamps.Bound(toUtc));
        command.Parameters.AddWithValue("$r", result is null ? DBNull.Value : (int)result.Value);
        command.Parameters.AddWithValue("$limit", limit);

        var rows = new List<ScanLogRow>();
        using var reader = command.ExecuteReader();

        while (reader.Read())
        {
            rows.Add(new ScanLogRow
            {
                Id = reader.GetInt64(0),
                CycleUtc = reader.GetString(1),
                WhenUtc = reader.GetString(2),
                VolumeId = reader.IsDBNull(3) ? null : reader.GetInt64(3),
                Volume = reader.GetString(4),
                Tier = (ScanTier)reader.GetInt32(5),
                Result = (ScanOutcome)reader.GetInt32(6),
                Reason = reader.GetString(7),
                DurationMs = reader.GetInt64(8),
                Records = reader.GetInt64(9),
                Events = reader.GetInt64(10),
            });
        }

        return rows;
    }

    /// <summary>
    /// Counts and durations over a window.
    /// </summary>
    /// <remarks>
    /// The median is computed in SQL rather than by pulling the durations back, because the window
    /// the Settings screen asks for is "today" and this runs on every load of that screen. It is a
    /// small table either way; doing it here keeps the endpoint a single round trip.
    /// </remarks>
    public static ScanLogSummary Summarise(
        SqliteConnection connection,
        long? volumeId,
        DateTime? fromUtc,
        DateTime? toUtc)
    {
        ArgumentNullException.ThrowIfNull(connection);

        using var command = connection.CreateCommand();
        command.CommandText = """
            WITH scoped AS (
                SELECT result, duration_ms, volume
                FROM scan_log
                WHERE ($v IS NULL OR volume_id = $v)
                  AND ($f IS NULL OR when_utc >= $f)
                  AND ($t IS NULL OR when_utc <= $t)
            ),
            ran AS (SELECT duration_ms FROM scoped WHERE result = 1 ORDER BY duration_ms)
            SELECT
                (SELECT COUNT(*) FROM scoped WHERE result = 1),
                (SELECT COUNT(*) FROM scoped WHERE result = 2),
                (SELECT COUNT(*) FROM scoped WHERE result = 3),
                (SELECT AVG(duration_ms) FROM (
                    SELECT duration_ms FROM ran
                    LIMIT 2 - (SELECT COUNT(*) FROM ran) % 2
                    OFFSET (SELECT (COUNT(*) - 1) / 2 FROM ran))),
                (SELECT COALESCE(MAX(duration_ms), 0) FROM scoped),

                -- Only when something actually took time. A window of nothing but deferrals has a
                -- maximum duration of zero, and naming a "worst volume" for it would invent a
                -- culprit out of whichever row the ordering happened to reach first.
                (SELECT volume FROM scoped WHERE duration_ms > 0 ORDER BY duration_ms DESC LIMIT 1),
                (SELECT COALESCE(SUM(duration_ms), 0) FROM scoped);
            """;

        command.Parameters.AddWithValue("$v", (object?)volumeId ?? DBNull.Value);
        command.Parameters.AddWithValue("$f", Timestamps.Bound(fromUtc));
        command.Parameters.AddWithValue("$t", Timestamps.Bound(toUtc));

        using var reader = command.ExecuteReader();

        if (!reader.Read())
        {
            return new ScanLogSummary();
        }

        return new ScanLogSummary
        {
            Ran = reader.GetInt64(0),
            Deferred = reader.GetInt64(1),
            Failed = reader.GetInt64(2),
            MedianMs = reader.IsDBNull(3) ? 0 : (long)Math.Round(reader.GetDouble(3)),
            WorstMs = reader.GetInt64(4),
            WorstVolume = reader.IsDBNull(5) ? "" : reader.GetString(5),
            TotalMs = reader.GetInt64(6),
        };
    }
}
