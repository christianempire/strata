using Strata.Core.Rollup;

namespace Strata.Core.Diffing;

/// <summary>Tunables for how changes are attributed and reported.</summary>
public sealed record DiffOptions
{
    /// <summary>
    /// How much of a directory's net change one child must account for before the report descends
    /// into that child instead of stopping.
    /// </summary>
    /// <remarks>
    /// This threshold is the whole difference between a useful answer and a wall of rows. At 0.9,
    /// 200 files appearing under one game directory report as that directory; a Windows update
    /// touching a dozen unrelated places reports at the level where they diverge.
    /// </remarks>
    public double DominanceThreshold { get; init; } = 0.9;

    /// <summary>Changes smaller than this are not reported at all.</summary>
    public long MinimumDeltaBytes { get; init; } = 1024 * 1024;

    /// <summary>Never descend past this depth, however dominant a child is.</summary>
    public int MaxDepth { get; init; } = 12;

    /// <summary>Cap on reported changes, largest first.</summary>
    public int MaxResults { get; init; } = 200;

    /// <summary>
    /// How many individually-notable children a directory may have before the report expands into
    /// them rather than summarising them.
    /// </summary>
    /// <remarks>
    /// Summarising is right when a handful of siblings moved together - three folders under
    /// <c>steamapps\common</c> growing is one event, "common grew". It stops being right once the
    /// node is just a corridor: thirty unrelated directories under the volume root all growing is
    /// thirty events, and reporting "D: grew 465 GB" is technically true and useless.
    /// </remarks>
    public int MaxSiblingsToSummarise { get; init; } = 4;
}

/// <summary>
/// Turns per-directory deltas into a ranked, human-readable set of changes.
/// </summary>
/// <remarks>
/// This is the intelligence the product is actually for. A naive diff of two snapshots reports every
/// directory that changed, which for a game install is hundreds of rows all saying the same thing.
/// What a person wants is one row: "D:\Steam\steamapps\common\Foo, +32 GB, 200 new files".
/// <para>
/// Pure: data structures in, data structures out, no I/O. Both callers use the same engine - Tier 2
/// feeding it deltas accumulated from the change journal, and the Compare screen feeding it the
/// difference between two stored snapshots.
/// </para>
/// </remarks>
public static class DiffEngine
{
    /// <summary>
    /// Attributes a set of per-directory deltas to the shallowest directories that explain them.
    /// </summary>
    /// <param name="nodes">The directory tree the deltas refer to.</param>
    /// <param name="rootId">Volume root.</param>
    /// <param name="deltas">Per-directory own-deltas, keyed by directory ID. Not subtree totals.</param>
    /// <param name="rootPrefix">Path prefix for display, typically the drive letter.</param>
    public static IReadOnlyList<AttributedChange> Attribute(
        IReadOnlyDictionary<long, DirectoryNode> nodes,
        long rootId,
        IReadOnlyDictionary<long, DirectoryDelta> deltas,
        string rootPrefix,
        DiffOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        ArgumentNullException.ThrowIfNull(deltas);

        options ??= new DiffOptions();

        if (deltas.Count == 0 || !nodes.ContainsKey(rootId))
        {
            return [];
        }

        var subtree = AccumulateToSubtrees(nodes, deltas);

        var results = new List<AttributedChange>();
        Descend(nodes, rootId, subtree, rootPrefix, options, results);

        // Largest absolute change first: the point of the feed is that the top row is the answer.
        results.Sort(static (a, b) => b.AbsoluteDelta.CompareTo(a.AbsoluteDelta));

        return results.Count > options.MaxResults
            ? results.GetRange(0, options.MaxResults)
            : results;
    }

    /// <summary>
    /// Rolls own-deltas up so every ancestor carries the net change of everything beneath it.
    /// </summary>
    /// <remarks>
    /// Only ancestors of directories that actually changed are visited. On a quiet tick that is a
    /// few dozen nodes out of half a million, which is what keeps this affordable at a 15-minute
    /// cadence.
    /// </remarks>
    private static Dictionary<long, DirectoryDelta> AccumulateToSubtrees(
        IReadOnlyDictionary<long, DirectoryNode> nodes,
        IReadOnlyDictionary<long, DirectoryDelta> deltas)
    {
        var subtree = new Dictionary<long, DirectoryDelta>(deltas.Count * 4);

        foreach (var (dirId, delta) in deltas)
        {
            if (delta.IsEmpty)
            {
                continue;
            }

            var current = dirId;
            var guard = 0;

            while (nodes.TryGetValue(current, out var node))
            {
                subtree[current] = subtree.TryGetValue(current, out var existing)
                    ? existing + delta
                    : delta;

                if (node.ParentId == current || guard++ > 4096)
                {
                    break;
                }

                current = node.ParentId;
            }
        }

        return subtree;
    }

    /// <summary>
    /// Walks down from the root, stopping at the shallowest node that explains its own change.
    /// </summary>
    /// <remarks>
    /// Descends while a single child accounts for essentially all of this node's net change, because
    /// then this node is just a corridor and the child is the real answer. Stops as soon as the
    /// change is genuinely spread across siblings, because that is the point at which naming this
    /// directory tells the whole story and naming its children tells it worse.
    /// </remarks>
    private static void Descend(
        IReadOnlyDictionary<long, DirectoryNode> nodes,
        long dirId,
        Dictionary<long, DirectoryDelta> subtree,
        string rootPrefix,
        DiffOptions options,
        List<AttributedChange> results)
    {
        if (!subtree.TryGetValue(dirId, out var delta) || !nodes.TryGetValue(dirId, out var node))
        {
            return;
        }

        if (!IsNotable(delta, options))
        {
            return;
        }

        if (node.Depth < options.MaxDepth)
        {
            var children = NotableChildren(nodes, dirId, subtree, options);

            if (children.Count > 0)
            {
                // One child carries essentially all of it: this node is a corridor, keep going.
                var dominant = FindDominantChild(children, subtree, delta, options);

                if (dominant is not null)
                {
                    Descend(nodes, dominant.Value, subtree, rootPrefix, options, results);
                    return;
                }

                // No single child explains it. Summarising here is the right answer for a few
                // siblings that moved together, and the wrong answer for a corridor with many
                // independent movers, or for growth in one place cancelling deletion in another -
                // where the net is a misleading zero and the parts are the real story.
                if (children.Count > options.MaxSiblingsToSummarise || HasOpposingBranches(children, subtree, options))
                {
                    foreach (var child in children)
                    {
                        Descend(nodes, child, subtree, rootPrefix, options, results);
                    }

                    return;
                }
            }
        }

        results.Add(new AttributedChange
        {
            DirectoryId = dirId,
            Path = PathBuilder.BuildPath(nodes, dirId, rootPrefix),
            Kind = ClassifyKind(delta),
            DeltaBytes = delta.OwnBytesDelta,
            ChurnBytes = delta.OwnChurnBytes,
            FileCountDelta = delta.FileCountDelta,
            Depth = node.Depth,
        });
    }

    /// <summary>
    /// True when this node's children move in both directions by amounts that both matter, so the
    /// node's net change is a cancellation rather than a summary.
    /// </summary>
    private static bool HasOpposingBranches(
        List<long> children,
        Dictionary<long, DirectoryDelta> subtree,
        DiffOptions options)
    {
        long grew = 0;
        long shrank = 0;

        foreach (var child in children)
        {
            var delta = subtree[child].OwnBytesDelta;

            if (delta > 0)
            {
                grew += delta;
            }
            else
            {
                shrank -= delta;
            }
        }

        return grew >= options.MinimumDeltaBytes && shrank >= options.MinimumDeltaBytes;
    }

    private static bool IsNotable(DirectoryDelta delta, DiffOptions options)
        => Math.Abs(delta.OwnBytesDelta) >= options.MinimumDeltaBytes ||
           delta.OwnChurnBytes >= options.MinimumDeltaBytes;

    private static long? FindDominantChild(
        List<long> children,
        Dictionary<long, DirectoryDelta> subtree,
        DirectoryDelta parentDelta,
        DiffOptions options)
    {
        if (parentDelta.OwnBytesDelta == 0)
        {
            // No net change to attribute; only descend when exactly one child is responsible for
            // whatever churn there was, otherwise this node is the honest answer.
            return children.Count == 1 ? children[0] : null;
        }

        var parentMagnitude = (double)Math.Abs(parentDelta.OwnBytesDelta);

        foreach (var child in children)
        {
            var childDelta = subtree[child];

            // Same direction, and large enough to account for the parent on its own.
            if (Math.Sign(childDelta.OwnBytesDelta) == Math.Sign(parentDelta.OwnBytesDelta) &&
                Math.Abs(childDelta.OwnBytesDelta) >= parentMagnitude * options.DominanceThreshold)
            {
                return child;
            }
        }

        return null;
    }

    /// <summary>Changed children of this node that are individually worth reporting.</summary>
    /// <remarks>
    /// Only nodes that changed appear in <paramref name="subtree"/>, so this scans the changed set
    /// rather than the whole tree. On a quiet tick that is a few dozen nodes out of half a million.
    /// </remarks>
    private static List<long> NotableChildren(
        IReadOnlyDictionary<long, DirectoryNode> nodes,
        long dirId,
        Dictionary<long, DirectoryDelta> subtree,
        DiffOptions options)
    {
        var children = new List<long>();

        foreach (var (candidate, delta) in subtree)
        {
            if (candidate != dirId &&
                IsNotable(delta, options) &&
                nodes.TryGetValue(candidate, out var node) &&
                node.ParentId == dirId)
            {
                children.Add(candidate);
            }
        }

        return children;
    }

    private static ChangeKind ClassifyKind(DirectoryDelta delta)
    {
        if (delta.OwnBytesDelta > 0)
        {
            return delta.FilesRemoved == 0 && delta.FilesAdded > 0 && delta.OwnChurnBytes == delta.OwnBytesDelta
                ? ChangeKind.Appeared
                : ChangeKind.Grew;
        }

        if (delta.OwnBytesDelta < 0)
        {
            return delta.FilesAdded == 0 && delta.FilesRemoved > 0
                ? ChangeKind.Vanished
                : ChangeKind.Shrank;
        }

        return ChangeKind.Grew;
    }
}
