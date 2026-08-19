using Microsoft.Data.Sqlite;
using Strata.Core.Diffing;

namespace Strata.Storage;

/// <summary>Loads the rows the diff engine compares.</summary>
public static class DiffQueries
{
    /// <summary>
    /// One snapshot's directory rollup, optionally scoped to a subtree.
    /// </summary>
    /// <remarks>
    /// The scope is applied here rather than after loading, because comparing two whole snapshots to
    /// answer a question about one directory would read both rollups in full for no reason. The
    /// prefix range is a seek on ix_snapshot_dirs_path.
    /// </remarks>
    public static List<SnapshotDirectory> LoadSnapshotDirs(
        SqliteConnection connection,
        long snapshotId,
        string? path)
    {
        ArgumentNullException.ThrowIfNull(connection);

        using var command = connection.CreateCommand();
        command.Parameters.AddWithValue("$s", snapshotId);

        if (string.IsNullOrWhiteSpace(path))
        {
            command.CommandText = """
                SELECT path, depth, subtree_bytes, subtree_files
                FROM snapshot_dirs WHERE snapshot_id = $s ORDER BY path;
                """;
        }
        else
        {
            var root = path.Trim().TrimEnd('\\');

            command.CommandText = """
                SELECT path, depth, subtree_bytes, subtree_files
                FROM snapshot_dirs
                WHERE snapshot_id = $s AND (path = $root OR (path >= $lo AND path < $hi))
                ORDER BY path;
                """;

            command.Parameters.AddWithValue("$root", root);
            command.Parameters.AddWithValue("$lo", root + "\\");
            command.Parameters.AddWithValue("$hi", root + "]");
        }

        var rows = new List<SnapshotDirectory>();

        using var reader = command.ExecuteReader();

        while (reader.Read())
        {
            rows.Add(new SnapshotDirectory(
                reader.GetString(0),
                reader.GetInt32(1),
                reader.GetInt64(2),
                reader.GetInt64(3)));
        }

        return rows;
    }
}
