using Microsoft.Data.Sqlite;

using Strata.Core.Scanning;

namespace Strata.Storage;

/// <summary>Read side of <c>snapshots</c>.</summary>
/// <remarks>
/// Timestamps are ISO-8601 with a fixed seven-digit fraction, so a lexicographic range is a
/// chronological range and the index on (volume_id, taken_utc) serves both the filter and the sort.
/// That property is the reason the format is fixed rather than round-tripped loosely.
/// </remarks>
public static class SnapshotQueries
{
    public static List<SnapshotRow> List(
        SqliteConnection connection,
        long? volumeId,
        DateTime? fromUtc,
        DateTime? toUtc,
        int limit)
    {
        ArgumentNullException.ThrowIfNull(connection);

        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT s.id, s.volume_id, s.taken_utc, s.scan_tier, s.duration_ms, s.total_bytes,
                   s.total_on_disk, s.free_bytes, s.file_count, s.dir_count, s.inaccessible_count,
                   s.hard_link_count, s.hard_link_bytes, s.reparse_count
            FROM snapshots s
            WHERE ($v IS NULL OR s.volume_id = $v)
              AND ($f IS NULL OR s.taken_utc >= $f)
              AND ($t IS NULL OR s.taken_utc <= $t)
            ORDER BY s.taken_utc DESC, s.id DESC
            LIMIT $limit;
            """;

        command.Parameters.AddWithValue("$v", (object?)volumeId ?? DBNull.Value);
        command.Parameters.AddWithValue("$f", Timestamps.Bound(fromUtc));
        command.Parameters.AddWithValue("$t", Timestamps.Bound(toUtc));
        command.Parameters.AddWithValue("$limit", limit);

        var rows = new List<SnapshotRow>();
        using var reader = command.ExecuteReader();

        while (reader.Read())
        {
            rows.Add(Read(reader));
        }

        return rows;
    }

    public static SnapshotRow? ById(SqliteConnection connection, long snapshotId)
    {
        ArgumentNullException.ThrowIfNull(connection);

        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT s.id, s.volume_id, s.taken_utc, s.scan_tier, s.duration_ms, s.total_bytes,
                   s.total_on_disk, s.free_bytes, s.file_count, s.dir_count, s.inaccessible_count,
                   s.hard_link_count, s.hard_link_bytes, s.reparse_count
            FROM snapshots s WHERE s.id = $id;
            """;

        command.Parameters.AddWithValue("$id", snapshotId);

        using var reader = command.ExecuteReader();
        return reader.Read() ? Read(reader) : null;
    }

    private static SnapshotRow Read(SqliteDataReader reader) => new()
    {
        Id = reader.GetInt64(0),
        VolumeId = reader.GetInt64(1),
        TakenUtc = reader.GetString(2),
        Tier = (ScanTier)reader.GetInt32(3),
        DurationMs = reader.GetInt64(4),
        TotalBytes = reader.GetInt64(5),
        TotalOnDisk = reader.GetInt64(6),
        FreeBytes = reader.GetInt64(7),
        FileCount = reader.GetInt64(8),
        DirCount = reader.GetInt64(9),
        InaccessibleCount = reader.GetInt64(10),
        HardLinkCount = reader.GetInt64(11),
        HardLinkBytes = reader.GetInt64(12),
        ReparseCount = reader.GetInt64(13),
    };
}

/// <summary>Turns optional time bounds into the exact text form stored in the database.</summary>
internal static class Timestamps
{
    /// <summary>
    /// The "O" round-trip format, which is what every timestamp column in this schema holds.
    /// </summary>
    /// <remarks>
    /// A bound has to be formatted identically to the stored values or the comparison is a string
    /// comparison against a differently-shaped string, which silently returns the wrong rows rather
    /// than failing.
    /// </remarks>
    internal static object Bound(DateTime? value)
        => value is null ? DBNull.Value : value.Value.ToUniversalTime().ToString("O");
}
