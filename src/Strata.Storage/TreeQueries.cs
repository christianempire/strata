using System.Globalization;
using System.Runtime.CompilerServices;
using Microsoft.Data.Sqlite;

namespace Strata.Storage;

/// <summary>
/// Current-state directory tree reads.
/// </summary>
/// <remarks>
/// Current state, from <c>dir_totals</c>, which holds every directory on the volume at full depth
/// and is maintained incrementally by Tier 2. History lives in <c>dir_history</c> and is walked by
/// <see cref="HistoryTreeWalk"/>; since migration 005 the two answer the same question at different
/// times, where before this walk was unbounded and the historical one was capped by depth and by a
/// size floor.
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

    private static long? ToId(object? value)
        => value is null or DBNull ? null : Convert.ToInt64(value, CultureInfo.InvariantCulture);
}
