using Microsoft.Data.Sqlite;

namespace Strata.Storage;

/// <summary>
/// Read side of <c>events</c>, which is what the Timeline screen is built on.
/// </summary>
/// <remarks>
/// This is the smallest table that answers the question the product exists for, and it stays at full
/// resolution when the directory rollups get downsampled. Newest first, because the top row of the
/// feed is meant to be the answer.
/// </remarks>
public static class EventQueries
{
    public static List<EventRow> List(
        SqliteConnection connection,
        long? volumeId,
        DateTime? fromUtc,
        DateTime? toUtc,
        long minimumAbsoluteBytes,
        int limit)
    {
        ArgumentNullException.ThrowIfNull(connection);

        using var command = connection.CreateCommand();

        // Filtered on when_utc first so ix_events_when (or ix_events_volume_when when a volume is
        // named) carries both the range and the ordering.
        command.CommandText = """
            SELECT e.id, e.volume_id, v.drive_letter, e.snapshot_id, e.when_utc, e.path,
                   e.delta_bytes, e.churn_bytes, e.file_delta, e.kind
            FROM events e
            LEFT JOIN volumes v ON v.id = e.volume_id
            WHERE ($v IS NULL OR e.volume_id = $v)
              AND ($f IS NULL OR e.when_utc >= $f)
              AND ($t IS NULL OR e.when_utc <= $t)
              AND (ABS(e.delta_bytes) >= $min OR e.churn_bytes >= $min)
            ORDER BY e.when_utc DESC, e.id DESC
            LIMIT $limit;
            """;

        command.Parameters.AddWithValue("$v", (object?)volumeId ?? DBNull.Value);
        command.Parameters.AddWithValue("$f", Timestamps.Bound(fromUtc));
        command.Parameters.AddWithValue("$t", Timestamps.Bound(toUtc));
        command.Parameters.AddWithValue("$min", minimumAbsoluteBytes);
        command.Parameters.AddWithValue("$limit", limit);

        var rows = new List<EventRow>();

        using var reader = command.ExecuteReader();

        while (reader.Read())
        {
            rows.Add(new EventRow
            {
                Id = reader.GetInt64(0),
                VolumeId = reader.GetInt64(1),
                DriveLetter = reader.IsDBNull(2) ? null : reader.GetString(2),
                SnapshotId = reader.GetInt64(3),
                WhenUtc = reader.GetString(4),
                Path = reader.GetString(5),
                DeltaBytes = reader.GetInt64(6),
                ChurnBytes = reader.GetInt64(7),
                FileDelta = reader.GetInt32(8),
                Kind = reader.GetInt32(9),
            });
        }

        return rows;
    }
}
