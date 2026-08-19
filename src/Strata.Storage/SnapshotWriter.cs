using Microsoft.Data.Sqlite;
using Strata.Core.Rollup;
using Strata.Core.Scanning;

namespace Strata.Storage;

/// <summary>How much of the directory tree a snapshot records.</summary>
/// <remarks>
/// Both defaults come from the brief. Whether they keep the database affordable at 96 snapshots a
/// day is the open question Phase 1 exists to answer; see docs/DESIGN-REVIEW.md section A5.
/// </remarks>
public sealed record SnapshotOptions
{
    /// <summary>Directories deeper than this are not recorded in the historical rollup.</summary>
    public int MaxDepth { get; init; } = 6;

    /// <summary>Directories whose subtree is smaller than this are not recorded.</summary>
    public long SizeFloorBytes { get; init; } = 10L * 1024 * 1024;
}

/// <summary>Persists a completed scan: the snapshot row, the current rollup, and the history rollup.</summary>
public static class SnapshotWriter
{
    /// <summary>
    /// Writes everything a finished scan produces, in one transaction.
    /// </summary>
    /// <returns>The new snapshot's row id.</returns>
    public static long Write(
        SqliteConnection connection,
        long volumeId,
        ScanResult result,
        SnapshotOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(result);

        options ??= new SnapshotOptions();

        using var transaction = connection.BeginTransaction();

        var snapshotId = InsertSnapshot(connection, transaction, volumeId, result);

        WriteDirTotals(connection, transaction, volumeId, result.Rollup);
        WriteSnapshotDirs(connection, transaction, snapshotId, result, options);

        transaction.Commit();
        return snapshotId;
    }

    private static long InsertSnapshot(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long volumeId,
        ScanResult result)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO snapshots
                (volume_id, taken_utc, scan_tier, duration_ms, total_bytes, total_on_disk,
                 free_bytes, file_count, dir_count, inaccessible_count, hard_link_count,
                 hard_link_bytes, reparse_count)
            VALUES ($volume, $taken, $tier, $duration, $bytes, $ondisk, $free, $files, $dirs,
                    $inaccessible, $links, $linkbytes, $reparse)
            RETURNING id;
            """;

        command.Parameters.AddWithValue("$volume", volumeId);
        command.Parameters.AddWithValue("$taken", result.StartedUtc.ToString("O"));
        command.Parameters.AddWithValue("$tier", (int)result.Tier);
        command.Parameters.AddWithValue("$duration", (long)result.Duration.TotalMilliseconds);
        command.Parameters.AddWithValue("$bytes", result.TotalBytes);
        command.Parameters.AddWithValue("$ondisk", result.TotalSizeOnDisk);
        command.Parameters.AddWithValue("$free", result.Volume.FreeBytes);
        command.Parameters.AddWithValue("$files", result.FileCount);
        command.Parameters.AddWithValue("$dirs", result.DirectoryCount);
        command.Parameters.AddWithValue("$inaccessible", result.InaccessibleTotal);
        command.Parameters.AddWithValue("$links", result.HardLinkCount);
        command.Parameters.AddWithValue("$linkbytes", result.HardLinkBytesSaved);
        command.Parameters.AddWithValue("$reparse", result.ReparsePointCount);

        var id = command.ExecuteScalar()
            ?? throw new InvalidOperationException("Failed to record the snapshot.");

        return Convert.ToInt64(id, System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Replaces the current directory rollup for this volume.
    /// </summary>
    /// <remarks>
    /// Every directory is recorded here regardless of size, unlike the historical rollup, because
    /// Phase 2 maintains these incrementally and needs somewhere to add a delta for any ancestor of
    /// any changed file.
    /// </remarks>
    private static void WriteDirTotals(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long volumeId,
        RollupResult rollup)
    {
        using (var clear = connection.CreateCommand())
        {
            clear.Transaction = transaction;
            clear.CommandText = "DELETE FROM dir_totals WHERE volume_id = $volume;";
            clear.Parameters.AddWithValue("$volume", volumeId);
            clear.ExecuteNonQuery();
        }

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT OR REPLACE INTO dir_totals
                (volume_id, dir_id, parent_id, name, depth, subtree_bytes, subtree_on_disk,
                 subtree_files, subtree_dirs)
            VALUES ($volume, $dir, $parent, $name, $depth, $bytes, $ondisk, $files, $dirs);
            """;

        command.Parameters.Add("$volume", SqliteType.Integer).Value = volumeId;
        command.Parameters.Add("$dir", SqliteType.Integer);
        command.Parameters.Add("$parent", SqliteType.Integer);
        command.Parameters.Add("$name", SqliteType.Text);
        command.Parameters.Add("$depth", SqliteType.Integer);
        command.Parameters.Add("$bytes", SqliteType.Integer);
        command.Parameters.Add("$ondisk", SqliteType.Integer);
        command.Parameters.Add("$files", SqliteType.Integer);
        command.Parameters.Add("$dirs", SqliteType.Integer);
        command.Prepare();

        foreach (var node in rollup.Nodes.Values)
        {
            command.Parameters[1].Value = node.Id;
            command.Parameters[2].Value = node.ParentId;
            command.Parameters[3].Value = node.Name;
            command.Parameters[4].Value = node.Depth;
            command.Parameters[5].Value = node.SubtreeBytes;
            command.Parameters[6].Value = node.SubtreeSizeOnDisk;
            command.Parameters[7].Value = node.SubtreeFileCount;
            command.Parameters[8].Value = node.SubtreeDirCount;
            command.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Writes the historical rollup: directories above the size floor and within the depth cap,
    /// with their paths materialised so history stays readable after the directory is gone.
    /// </summary>
    private static void WriteSnapshotDirs(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long snapshotId,
        ScanResult result,
        SnapshotOptions options)
    {
        var rootPrefix = result.Volume.DriveLetter ?? result.Volume.GuidPath;

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT OR REPLACE INTO snapshot_dirs
                (snapshot_id, dir_id, path, depth, subtree_bytes, subtree_on_disk, subtree_files)
            VALUES ($snapshot, $dir, $path, $depth, $bytes, $ondisk, $files);
            """;

        command.Parameters.Add("$snapshot", SqliteType.Integer).Value = snapshotId;
        command.Parameters.Add("$dir", SqliteType.Integer);
        command.Parameters.Add("$path", SqliteType.Text);
        command.Parameters.Add("$depth", SqliteType.Integer);
        command.Parameters.Add("$bytes", SqliteType.Integer);
        command.Parameters.Add("$ondisk", SqliteType.Integer);
        command.Parameters.Add("$files", SqliteType.Integer);
        command.Prepare();

        foreach (var node in result.Rollup.Nodes.Values)
        {
            if (node.Depth > options.MaxDepth || node.SubtreeBytes < options.SizeFloorBytes)
            {
                continue;
            }

            command.Parameters[1].Value = node.Id;
            command.Parameters[2].Value = PathBuilder.BuildPath(result.Rollup.Nodes, node.Id, rootPrefix);
            command.Parameters[3].Value = node.Depth;
            command.Parameters[4].Value = node.SubtreeBytes;
            command.Parameters[5].Value = node.SubtreeSizeOnDisk;
            command.Parameters[6].Value = node.SubtreeFileCount;
            command.ExecuteNonQuery();
        }
    }
}
