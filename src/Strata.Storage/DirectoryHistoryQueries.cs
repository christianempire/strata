using System.Globalization;
using Microsoft.Data.Sqlite;
using Strata.Core.Diffing;
using Strata.Core.Rollup;

namespace Strata.Storage;

/// <summary>
/// Read side of the sparse directory history.
/// </summary>
/// <remarks>
/// Every query here is scoped to directories the caller already cares about, which is the property
/// that keeps a read proportional to what changed rather than to how many directories exist. A day
/// of activity on C: touches a few thousand of 571k.
/// </remarks>
public static class DirectoryHistoryQueries
{
    /// <summary>
    /// The directories whose totals moved between two moments.
    /// </summary>
    /// <remarks>
    /// This is the set a diff is about, and reading it directly is what a per-snapshot rollup could
    /// never do: there, every directory had a row at every moment, so "what changed" had to be
    /// derived by comparing two complete copies.
    /// <para>
    /// Half-open at the start. A row exactly at <paramref name="fromUtc"/> is the state being
    /// compared against, not a change since it.
    /// </para>
    /// </remarks>
    public static List<long> ChangedBetween(
        SqliteConnection connection,
        long volumeId,
        DateTime fromUtc,
        DateTime toUtc)
    {
        ArgumentNullException.ThrowIfNull(connection);

        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT DISTINCT dir_id FROM dir_history
            WHERE volume_id = $v AND taken_unix > $from AND taken_unix <= $to;
            """;

        command.Parameters.AddWithValue("$v", volumeId);
        command.Parameters.AddWithValue("$from", Unix(fromUtc));
        command.Parameters.AddWithValue("$to", Unix(toUtc));

        var ids = new List<long>();

        using var reader = command.ExecuteReader();

        while (reader.Read())
        {
            ids.Add(reader.GetInt64(0));
        }

        return ids;
    }

    /// <summary>
    /// The subtree totals of named directories as they stood at an instant.
    /// </summary>
    /// <remarks>
    /// A seek per directory. The primary key is (volume, directory, time), so the latest row at or
    /// before the instant is the first one the seek reads — which is what makes an absolute-valued
    /// history cheaper to query than a delta-valued one, where the same question would mean
    /// replaying from an anchor.
    /// </remarks>
    public static Dictionary<long, HistoryDiff.Measurement> MeasureAt(
        SqliteConnection connection,
        long volumeId,
        DateTime instantUtc,
        IEnumerable<long> directoryIds)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(directoryIds);

        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT subtree_bytes, subtree_files FROM dir_history
            WHERE volume_id = $v AND dir_id = $d AND taken_unix <= $t
            ORDER BY taken_unix DESC LIMIT 1;
            """;

        command.Parameters.AddWithValue("$v", volumeId);
        command.Parameters.AddWithValue("$t", Unix(instantUtc));
        command.Parameters.Add("$d", SqliteType.Integer);
        command.Prepare();

        var measured = new Dictionary<long, HistoryDiff.Measurement>();

        foreach (var dirId in directoryIds)
        {
            command.Parameters["$d"].Value = dirId;

            using var reader = command.ExecuteReader();

            if (reader.Read())
            {
                measured[dirId] = new HistoryDiff.Measurement(reader.GetInt64(0), reader.GetInt64(1));
            }
        }

        return measured;
    }

    /// <summary>
    /// Loads named directories and every ancestor above them.
    /// </summary>
    /// <remarks>
    /// Enough tree to attribute a change and to build the path of whatever gets reported, without
    /// reading the whole name dimension. Ancestry is heavily shared, so the closure of a few
    /// thousand changed directories is not much larger than the set itself.
    /// </remarks>
    public static Dictionary<long, DirectoryNode> LoadNodes(
        SqliteConnection connection,
        long volumeId,
        IEnumerable<long> directoryIds)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(directoryIds);

        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT parent_id, name, depth FROM dir_names WHERE volume_id = $v AND dir_id = $d;";

        command.Parameters.AddWithValue("$v", volumeId);
        command.Parameters.Add("$d", SqliteType.Integer);
        command.Prepare();

        var nodes = new Dictionary<long, DirectoryNode>();
        var pending = new Queue<long>(directoryIds);

        while (pending.Count > 0)
        {
            var dirId = pending.Dequeue();

            if (nodes.ContainsKey(dirId))
            {
                continue;
            }

            command.Parameters["$d"].Value = dirId;

            using var reader = command.ExecuteReader();

            if (!reader.Read())
            {
                continue;
            }

            var parentId = reader.GetInt64(0);

            nodes[dirId] = new DirectoryNode(dirId, parentId, reader.GetString(1))
            {
                Depth = reader.GetInt32(2),
            };

            if (parentId != dirId)
            {
                pending.Enqueue(parentId);
            }
        }

        return nodes;
    }

    /// <summary>The volume root as the name dimension knows it.</summary>
    public static long? RootId(SqliteConnection connection, long volumeId)
    {
        ArgumentNullException.ThrowIfNull(connection);

        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT dir_id FROM dir_names WHERE volume_id = $v AND depth = 0 LIMIT 1;";
        command.Parameters.AddWithValue("$v", volumeId);

        return ToId(command.ExecuteScalar());
    }

    /// <summary>
    /// Resolves a path to a directory id by walking the name dimension segment by segment.
    /// </summary>
    /// <remarks>
    /// Reads the same way as the current-state resolver but against <c>dir_names</c>, so a path that
    /// no longer exists still resolves — which is the point of a history that outlives its
    /// directories.
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
            SELECT dir_id FROM dir_names INDEXED BY ix_dir_names_parent
            WHERE volume_id = $v AND parent_id = $p AND name = $n COLLATE NOCASE
              AND dir_id <> parent_id
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

    /// <summary>The children of a directory, as the name dimension knows them.</summary>
    /// <remarks>
    /// Includes directories that have since been deleted. A historical tree is supposed to show what
    /// was there, and their measurements at that moment will say whether they held anything.
    /// </remarks>
    public static List<DirectoryNode> ChildrenOf(SqliteConnection connection, long volumeId, long parentId)
    {
        ArgumentNullException.ThrowIfNull(connection);

        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT dir_id, parent_id, name, depth FROM dir_names INDEXED BY ix_dir_names_parent
            WHERE volume_id = $v AND parent_id = $p AND dir_id <> parent_id;
            """;

        command.Parameters.AddWithValue("$v", volumeId);
        command.Parameters.AddWithValue("$p", parentId);

        var children = new List<DirectoryNode>();

        using var reader = command.ExecuteReader();

        while (reader.Read())
        {
            children.Add(new DirectoryNode(reader.GetInt64(0), reader.GetInt64(1), reader.GetString(2))
            {
                Depth = reader.GetInt32(3),
            });
        }

        return children;
    }

    private static long? ToId(object? value)
        => value is null or DBNull ? null : Convert.ToInt64(value, CultureInfo.InvariantCulture);

    /// <summary>
    /// Seconds since the epoch, treating an unmarked value as UTC.
    /// </summary>
    /// <remarks>
    /// Every timestamp in this schema is UTC by convention, so an unmarked value is UTC rather than
    /// local. Converting it as local would shift history by the timezone offset, which is the kind
    /// of error that produces a plausible chart.
    /// </remarks>
    private static long Unix(DateTime value)
        => new DateTimeOffset(
            value.Kind == DateTimeKind.Local
                ? value.ToUniversalTime()
                : DateTime.SpecifyKind(value, DateTimeKind.Utc)).ToUnixTimeSeconds();
}
