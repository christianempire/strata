using System.Globalization;
using System.Runtime.CompilerServices;
using Microsoft.Data.Sqlite;

namespace Strata.Storage;

/// <summary>
/// Directory tree reads, in two modes.
/// </summary>
/// <remarks>
/// Current state comes from <c>dir_totals</c>, which holds every directory on the volume at full
/// depth and is maintained incrementally by Tier 2. History comes from <c>snapshot_dirs</c>, which
/// is capped by depth and by a size floor, and stores paths rather than parent links because file
/// IDs churn and history has to stay readable after the directory is gone.
/// <para>
/// The two are not interchangeable and the API does not pretend they are: asking for a snapshot
/// gets that snapshot's bounded rollup, and asking for a volume gets the unbounded present.
/// </para>
/// </remarks>
public static class TreeQueries
{
    /// <summary>The volume's root directory id.</summary>
    /// <remarks>Served by the partial index ix_dir_totals_root, which holds one row per volume.</remarks>
    public static long? RootId(SqliteConnection connection, long volumeId)
    {
        ArgumentNullException.ThrowIfNull(connection);

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT dir_id FROM dir_totals WHERE volume_id = $v AND depth = 0;";
        command.Parameters.AddWithValue("$v", volumeId);

        return ToId(command.ExecuteScalar());
    }

    /// <summary>
    /// Resolves a path to a directory id by walking it segment by segment from the root.
    /// </summary>
    /// <remarks>
    /// One query per segment, each a seek into one directory's children. There is deliberately no
    /// index on name: it would cost tens of megabytes across 571k rows to speed up a lookup that
    /// already reads only one directory's children, and the name is compared against the handful of
    /// rows the parent lookup returns.
    /// <para>
    /// INDEXED BY, not by preference: without it the planner takes the primary key and reads the
    /// whole volume, which measured at about 9 ms a segment — so 37 ms to resolve a four-segment
    /// path against 2 ms with the index.
    /// </para>
    /// </remarks>
    public static long? ResolvePath(SqliteConnection connection, long volumeId, string? path)
    {
        ArgumentNullException.ThrowIfNull(connection);

        var current = RootId(connection, volumeId);

        if (current is null)
        {
            return null;
        }

        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT dir_id FROM dir_totals INDEXED BY ix_dir_totals_parent
            WHERE volume_id = $v AND parent_id = $p AND name = $n COLLATE NOCASE
            LIMIT 1;
            """;

        command.Parameters.AddWithValue("$v", volumeId);
        command.Parameters.Add("$p", SqliteType.Integer);
        command.Parameters.Add("$n", SqliteType.Text);

        foreach (var segment in PathSegments.Split(path))
        {
            command.Parameters["$p"].Value = current.Value;
            command.Parameters["$n"].Value = segment;

            current = ToId(command.ExecuteScalar());

            if (current is null)
            {
                return null;
            }
        }

        return current;
    }

    /// <summary>
    /// Streams the subtree under a directory, breadth first, a directory's children largest first.
    /// </summary>
    /// <remarks>
    /// A level-at-a-time walk in code rather than the recursive CTE this obviously wants to be, and
    /// the reason is the one this project keeps relearning: SQLite plans the recursive term as
    /// SEARCH dir_totals USING PRIMARY KEY (volume_id=?), a range scan over every directory on the
    /// volume once per level, and INDEXED BY does not move it. Timed on the real database, one level
    /// under the root of C: took 1.5 s that way and 20 ms as a direct child lookup. The plan looked
    /// reasonable; only the clock said otherwise.
    /// <para>
    /// Streaming is not decoration here. The honest worst case is the root of a system volume at
    /// unbounded depth, 571k directories, and yielding as the walk goes means a caller that stops
    /// reading stops the walk — the limit is reached without having queried the rest of the tree,
    /// which the CTE could not do because it had to finish before returning its first row.
    /// </para>
    /// </remarks>
    public static async IAsyncEnumerable<TreeNode> WalkCurrentAsync(
        SqliteConnection connection,
        long volumeId,
        long rootDirId,
        int depth,
        int limit,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        var root = await ReadDirectoryAsync(connection, volumeId, rootDirId, cancellationToken)
            .ConfigureAwait(false);

        if (root is null)
        {
            yield break;
        }

        yield return root.Value;

        var emitted = 1;
        var parents = new List<long> { rootDirId };

        using var command = connection.CreateCommand();

        // INDEXED BY rather than trust: this project measured the same predicate planned as a
        // 442 ms range scan in one shape and a 0.010 ms seek in another, and stopped reading plans.
        command.CommandText = """
            SELECT dir_id, parent_id, name, depth, subtree_bytes, subtree_on_disk,
                   subtree_files, subtree_dirs
            FROM dir_totals INDEXED BY ix_dir_totals_parent
            WHERE volume_id = $v AND parent_id = $p AND dir_id <> parent_id
            ORDER BY subtree_bytes DESC;
            """;

        command.Parameters.AddWithValue("$v", volumeId);
        command.Parameters.Add("$p", SqliteType.Integer);

        for (var level = 1; level <= depth && parents.Count > 0 && emitted < limit; level++)
        {
            var next = new List<long>();

            foreach (var parent in parents)
            {
                if (emitted >= limit)
                {
                    break;
                }

                command.Parameters["$p"].Value = parent;

                var children = new List<TreeNode>();

                using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
                {
                    while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    {
                        children.Add(Read(reader));
                    }
                }

                foreach (var child in children)
                {
                    if (emitted >= limit)
                    {
                        break;
                    }

                    emitted++;
                    yield return child;

                    if (child.HasChildren && level < depth)
                    {
                        next.Add(child.DirId);
                    }
                }
            }

            parents = next;
        }
    }

    /// <summary>One directory by id. A primary key seek.</summary>
    private static async Task<TreeNode?> ReadDirectoryAsync(
        SqliteConnection connection,
        long volumeId,
        long dirId,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT dir_id, parent_id, name, depth, subtree_bytes, subtree_on_disk,
                   subtree_files, subtree_dirs
            FROM dir_totals WHERE volume_id = $v AND dir_id = $d;
            """;

        command.Parameters.AddWithValue("$v", volumeId);
        command.Parameters.AddWithValue("$d", dirId);

        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? Read(reader) : null;
    }

    private static TreeNode Read(SqliteDataReader reader)
    {
        var subtreeDirs = reader.GetInt64(7);

        return new TreeNode(
            reader.GetInt64(0),
            reader.GetInt64(1),
            reader.GetString(2),
            reader.GetInt32(3),
            reader.GetInt64(4),
            reader.GetInt64(5),
            reader.GetInt64(6),
            subtreeDirs,
            subtreeDirs > 0);
    }

    /// <summary>
    /// Reads a historical subtree out of one snapshot's rollup.
    /// </summary>
    /// <remarks>
    /// Materialised rather than streamed, which is the opposite of the current-state walk and for a
    /// reason rather than by accident: <c>snapshot_dirs</c> is bounded by a depth cap and a size
    /// floor, so a whole snapshot is a few thousand rows — 5,738 for C: on the target machine. It
    /// also stores paths rather than parent links, so parents have to be recovered by matching each
    /// row's containing path back to a row in the same snapshot, and a row whose immediate parent
    /// fell below the floor is attached to the nearest ancestor that did qualify rather than
    /// dropped. That needs the whole set in hand.
    /// </remarks>
    public static async IAsyncEnumerable<TreeNode> WalkSnapshotAsync(
        SqliteConnection connection,
        long snapshotId,
        string? path,
        int depth,
        int limit,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        var root = await SnapshotRootAsync(connection, snapshotId, path, cancellationToken)
            .ConfigureAwait(false);

        if (root is null)
        {
            yield break;
        }

        var rows = await SnapshotSubtreeAsync(connection, snapshotId, root.Path, cancellationToken)
            .ConfigureAwait(false);

        var idByPath = new Dictionary<string, long>(rows.Count, StringComparer.OrdinalIgnoreCase);

        foreach (var row in rows)
        {
            idByPath[row.Path] = row.DirId;
        }

        var withChildren = new HashSet<long>();

        foreach (var row in rows)
        {
            var parent = NearestRecordedAncestor(idByPath, row.Path);

            if (parent is not null)
            {
                withChildren.Add(parent.Value);
            }
        }

        var emitted = 0;

        foreach (var row in rows)
        {
            if (emitted >= limit)
            {
                yield break;
            }

            var relativeDepth = PathSegments.DepthBelow(root.Path, row.Path);

            if (relativeDepth > depth)
            {
                continue;
            }

            emitted++;

            yield return new TreeNode(
                row.DirId,
                NearestRecordedAncestor(idByPath, row.Path) ?? row.DirId,
                PathSegments.Leaf(row.Path),
                row.Depth,
                row.SubtreeBytes,
                row.SubtreeOnDisk,
                row.SubtreeFiles,
                // Not recorded per row in the historical rollup, so this reports what this snapshot
                // kept rather than what was on disk. Bounded by the same depth cap and size floor.
                SubtreeDirs: 0,
                HasChildren: withChildren.Contains(row.DirId));
        }
    }

    /// <summary>The id of the closest ancestor this snapshot actually recorded.</summary>
    private static long? NearestRecordedAncestor(Dictionary<string, long> idByPath, string path)
    {
        var current = PathSegments.Parent(path);

        while (current is not null)
        {
            if (idByPath.TryGetValue(current, out var id))
            {
                return id;
            }

            current = PathSegments.Parent(current);
        }

        return null;
    }

    private static async Task<List<SnapshotDirRow>> SnapshotSubtreeAsync(
        SqliteConnection connection,
        long snapshotId,
        string rootPath,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();

        // A range scan over this snapshot's rows, which the primary key already clusters together:
        // 5,740 of them, measured at 21 ms. An index on the path was measured to be no faster while
        // costing 87% of the table's own size, so it was dropped again in migration 004.
        //
        // ']' is the code point immediately after '\', so [rootPath + '\', rootPath + ']') is
        // exactly the set of descendants and nothing else — a sibling named "Programs" cannot fall
        // inside the range for "Program".
        command.CommandText = """
            SELECT dir_id, path, depth, subtree_bytes, subtree_on_disk, subtree_files
            FROM snapshot_dirs
            WHERE snapshot_id = $s AND (path = $root OR (path >= $lo AND path < $hi))
            ORDER BY path;
            """;

        command.Parameters.AddWithValue("$s", snapshotId);
        command.Parameters.AddWithValue("$root", rootPath);
        command.Parameters.AddWithValue("$lo", rootPath + "\\");
        command.Parameters.AddWithValue("$hi", rootPath + "]");

        var rows = new List<SnapshotDirRow>();

        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(new SnapshotDirRow(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetInt32(2),
                reader.GetInt64(3),
                reader.GetInt64(4),
                reader.GetInt64(5)));
        }

        return rows;
    }

    private static async Task<SnapshotDirRow?> SnapshotRootAsync(
        SqliteConnection connection,
        long snapshotId,
        string? path,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Parameters.AddWithValue("$s", snapshotId);

        if (string.IsNullOrWhiteSpace(path))
        {
            command.CommandText = """
                SELECT dir_id, path, depth, subtree_bytes, subtree_on_disk, subtree_files
                FROM snapshot_dirs WHERE snapshot_id = $s ORDER BY depth, path LIMIT 1;
                """;
        }
        else
        {
            command.CommandText = """
                SELECT dir_id, path, depth, subtree_bytes, subtree_on_disk, subtree_files
                FROM snapshot_dirs WHERE snapshot_id = $s AND path = $p COLLATE NOCASE LIMIT 1;
                """;
            command.Parameters.AddWithValue("$p", path.Trim().TrimEnd('\\'));
        }

        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new SnapshotDirRow(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetInt32(2),
                reader.GetInt64(3),
                reader.GetInt64(4),
                reader.GetInt64(5))
            : null;
    }

    private static long? ToId(object? value)
        => value is null or DBNull ? null : Convert.ToInt64(value, CultureInfo.InvariantCulture);

    private sealed record SnapshotDirRow(
        long DirId,
        string Path,
        int Depth,
        long SubtreeBytes,
        long SubtreeOnDisk,
        long SubtreeFiles);
}
