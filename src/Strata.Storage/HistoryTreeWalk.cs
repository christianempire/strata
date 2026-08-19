using System.Runtime.CompilerServices;
using Microsoft.Data.Sqlite;

namespace Strata.Storage;

/// <summary>
/// Walks a directory tree as it stood at a moment.
/// </summary>
/// <remarks>
/// The historical counterpart to <see cref="TreeQueries.WalkCurrentAsync"/>, and the same shape: a
/// level at a time, driven by indexed child lookups rather than a recursive CTE, because SQLite
/// plans the recursive term as a range scan over every directory on the volume and no amount of
/// <c>INDEXED BY</c> moves it.
/// <para>
/// What it replaces read a snapshot's stored rollup, bounded by a depth cap and a size floor, so it
/// could not show a directory below depth 8 or under 10 MB at any price. This reads the sparse
/// history, which records everything that moved at any depth, and takes each directory's size from
/// the latest measurement at or before the instant asked for.
/// </para>
/// </remarks>
public static class HistoryTreeWalk
{
    public static async IAsyncEnumerable<TreeNode> WalkAsync(
        SqliteConnection connection,
        long volumeId,
        long rootDirId,
        DateTime instantUtc,
        int depth,
        int limit,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        var root = Measure(connection, volumeId, instantUtc, [rootDirId]);

        if (!root.TryGetValue(rootDirId, out var rootMeasurement))
        {
            yield break;
        }

        var rootNode = DirectoryHistoryQueries.LoadNodes(connection, volumeId, [rootDirId]);

        if (!rootNode.TryGetValue(rootDirId, out var rootInfo))
        {
            yield break;
        }

        var rootChildren = DirectoryHistoryQueries.ChildrenOf(connection, volumeId, rootDirId);

        yield return new TreeNode(
            rootDirId,
            rootInfo.ParentId,
            rootInfo.Name,
            rootInfo.Depth,
            rootMeasurement.SubtreeBytes,
            0,
            rootMeasurement.SubtreeFiles,
            rootChildren.Count,
            rootChildren.Count > 0);

        var emitted = 1;
        var parents = new List<long> { rootDirId };

        for (var level = 1; level <= depth && parents.Count > 0 && emitted < limit; level++)
        {
            var next = new List<long>();

            foreach (var parent in parents)
            {
                if (emitted >= limit)
                {
                    break;
                }

                var children = DirectoryHistoryQueries.ChildrenOf(connection, volumeId, parent);

                if (children.Count == 0)
                {
                    continue;
                }

                var measured = Measure(connection, volumeId, instantUtc, children.Select(c => c.Id));

                // Largest first, and a directory with no measurement at or before the instant did
                // not exist yet — dropped rather than shown as empty, which would be a different
                // and wrong claim.
                foreach (var child in children
                    .Where(c => measured.ContainsKey(c.Id))
                    .OrderByDescending(c => measured[c.Id].SubtreeBytes))
                {
                    if (emitted >= limit)
                    {
                        break;
                    }

                    var value = measured[child.Id];
                    var grandchildren = level < depth
                        ? DirectoryHistoryQueries.ChildrenOf(connection, volumeId, child.Id).Count
                        : 0;

                    emitted++;

                    yield return new TreeNode(
                        child.Id,
                        child.ParentId,
                        child.Name,
                        child.Depth,
                        value.SubtreeBytes,
                        0,
                        value.SubtreeFiles,
                        grandchildren,
                        grandchildren > 0);

                    if (grandchildren > 0)
                    {
                        next.Add(child.Id);
                    }
                }
            }

            parents = next;

            await Task.Yield();

            cancellationToken.ThrowIfCancellationRequested();
        }
    }

    private static Dictionary<long, Strata.Core.Diffing.HistoryDiff.Measurement> Measure(
        SqliteConnection connection,
        long volumeId,
        DateTime instantUtc,
        IEnumerable<long> ids)
        => DirectoryHistoryQueries.MeasureAt(connection, volumeId, instantUtc, ids);
}
