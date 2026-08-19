using Microsoft.Data.Sqlite;
using Strata.Core.Model;

namespace Strata.Storage;

/// <summary>Reads and writes the <c>volumes</c> table.</summary>
public static class VolumeRepository
{
    /// <summary>
    /// Records a volume, or refreshes what is already recorded, and returns its row id.
    /// </summary>
    /// <remarks>
    /// Matched on the GUID path. Everything else — letter, label, capacity — is refreshed on each
    /// sighting, because all of it can change while the volume stays the same volume.
    /// </remarks>
    public static long Upsert(SqliteConnection connection, VolumeIdentity volume)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(volume);

        var now = DateTime.UtcNow.ToString("O");

        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO volumes
                (guid_path, drive_letter, label, file_system, kind, serial_number, disk_number,
                 total_bytes, free_bytes, first_seen_utc, last_seen_utc)
            VALUES ($guid, $letter, $label, $fs, $kind, $serial, $disk, $total, $free, $now, $now)
            ON CONFLICT (guid_path) DO UPDATE SET
                drive_letter  = excluded.drive_letter,
                label         = excluded.label,
                file_system   = excluded.file_system,
                kind          = excluded.kind,
                serial_number = excluded.serial_number,
                disk_number   = excluded.disk_number,
                total_bytes   = excluded.total_bytes,
                free_bytes    = excluded.free_bytes,
                last_seen_utc = excluded.last_seen_utc
            RETURNING id;
            """;

        command.Parameters.AddWithValue("$guid", volume.GuidPath);
        command.Parameters.AddWithValue("$letter", (object?)volume.DriveLetter ?? DBNull.Value);
        command.Parameters.AddWithValue("$label", volume.Label ?? "");
        command.Parameters.AddWithValue("$fs", volume.FileSystem);
        command.Parameters.AddWithValue("$kind", (int)volume.Kind);
        command.Parameters.AddWithValue("$serial", volume.SerialNumber);
        command.Parameters.AddWithValue("$disk", (object?)volume.DiskNumber ?? DBNull.Value);
        command.Parameters.AddWithValue("$total", volume.TotalBytes);
        command.Parameters.AddWithValue("$free", volume.FreeBytes);
        command.Parameters.AddWithValue("$now", now);

        var id = command.ExecuteScalar()
            ?? throw new InvalidOperationException($"Failed to record volume {volume.Display}.");

        return Convert.ToInt64(id, System.Globalization.CultureInfo.InvariantCulture);
    }
}
