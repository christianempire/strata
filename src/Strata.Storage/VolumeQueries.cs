using Microsoft.Data.Sqlite;
using Strata.Core.Model;

namespace Strata.Storage;

/// <summary>Read side of <c>volumes</c>.</summary>
public static class VolumeQueries
{
    /// <summary>
    /// Every volume Strata has recorded, with enough state for the Overview screen to say what is
    /// happening to it.
    /// </summary>
    /// <remarks>
    /// The snapshot counts and the accounted total are correlated subqueries rather than joins,
    /// because there are three volumes and clarity is worth more than a join plan here. Both feed
    /// off indexes: ix_snapshots_volume_taken and the partial ix_dir_totals_root.
    /// </remarks>
    public static List<VolumeRow> List(SqliteConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);

        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT v.id, v.guid_path, v.drive_letter, v.label, v.file_system, v.kind,
                   v.total_bytes, v.free_bytes, v.disk_number, v.first_seen_utc, v.last_seen_utc,
                   v.baseline_utc,
                   (SELECT MAX(taken_utc) FROM snapshots s WHERE s.volume_id = v.id),
                   (SELECT COUNT(*)       FROM snapshots s WHERE s.volume_id = v.id),
                   (SELECT d.subtree_bytes FROM dir_totals d
                     WHERE d.volume_id = v.id AND d.depth = 0)
            FROM volumes v
            ORDER BY v.drive_letter IS NULL, v.drive_letter, v.guid_path;
            """;

        var rows = new List<VolumeRow>();
        using var reader = command.ExecuteReader();

        while (reader.Read())
        {
            rows.Add(new VolumeRow
            {
                Id = reader.GetInt64(0),
                GuidPath = reader.GetString(1),
                DriveLetter = reader.IsDBNull(2) ? null : reader.GetString(2),
                Label = reader.IsDBNull(3) ? "" : reader.GetString(3),
                FileSystem = reader.IsDBNull(4) ? "" : reader.GetString(4),
                Kind = (VolumeKind)reader.GetInt32(5),
                TotalBytes = reader.GetInt64(6),
                FreeBytes = reader.GetInt64(7),
                DiskNumber = reader.IsDBNull(8) ? null : reader.GetInt32(8),
                FirstSeenUtc = reader.GetString(9),
                LastSeenUtc = reader.GetString(10),
                BaselineUtc = reader.IsDBNull(11) ? null : reader.GetString(11),
                LastSnapshotUtc = reader.IsDBNull(12) ? null : reader.GetString(12),
                SnapshotCount = reader.GetInt64(13),
                AccountedBytes = reader.IsDBNull(14) ? 0 : reader.GetInt64(14),
            });
        }

        return rows;
    }

    /// <summary>
    /// Resolves what a caller wrote — a row id, a drive letter, or a GUID path — to a volume row id.
    /// </summary>
    /// <remarks>
    /// The GUID path is the identity and the letter is a mutable display attribute, so a URL built
    /// from a letter is answering "whatever is D: right now", which is what someone typing D: means.
    /// A caller that needs stability across a letter reassignment passes the id or the GUID.
    /// </remarks>
    public static long? Resolve(SqliteConnection connection, string? volume)
    {
        ArgumentNullException.ThrowIfNull(connection);

        if (string.IsNullOrWhiteSpace(volume))
        {
            return null;
        }

        var target = volume.Trim().TrimEnd('\\');

        // "d" means the D: drive. "1" means row 1, and must not be turned into a drive called "1:".
        if (target.Length == 1 && char.IsAsciiLetter(target[0]))
        {
            target += ":";
        }

        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id FROM volumes
            WHERE CAST(id AS TEXT) = $t
               OR drive_letter = $t COLLATE NOCASE
               OR guid_path = $t COLLATE NOCASE
               OR RTRIM(guid_path, '\') = $t COLLATE NOCASE
            LIMIT 1;
            """;

        command.Parameters.AddWithValue("$t", target);

        var value = command.ExecuteScalar();

        return value is null or DBNull
            ? null
            : Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture);
    }
}
