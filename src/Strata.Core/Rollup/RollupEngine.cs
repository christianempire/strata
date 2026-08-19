namespace Strata.Core.Rollup;

/// <summary>
/// Turns per-directory own-bytes into subtree totals.
/// </summary>
/// <remarks>
/// Pure: data structures in, data structures out, no I/O. This is the piece that makes a scan
/// answerable ("how big is D:\Steam?") and the piece Phase 2's incremental maintenance has to stay
/// consistent with, so it is tested directly rather than only through a real scan.
/// </remarks>
public static class RollupEngine
{
    /// <summary>
    /// Computes depth and subtree totals for every node, in place.
    /// </summary>
    /// <param name="nodes">All directories, keyed by file ID. Must contain <paramref name="rootId"/>.</param>
    /// <param name="rootId">File ID of the volume root, which is depth 0 and its own parent.</param>
    public static RollupResult Compute(Dictionary<long, DirectoryNode> nodes, long rootId)
    {
        ArgumentNullException.ThrowIfNull(nodes);

        if (!nodes.TryGetValue(rootId, out var root))
        {
            throw new ArgumentException($"Root directory {rootId} is not present in the node set.", nameof(rootId));
        }

        var orphans = AssignDepths(nodes, root);

        // Deepest first, so every node's children are fully accumulated before it is folded into its
        // own parent. A single ordered pass beats recursion here: real volumes nest deeply enough
        // (long npm and NuGet cache paths especially) that recursion is a genuine stack risk.
        var ordered = new List<DirectoryNode>(nodes.Values);
        ordered.Sort(static (a, b) => b.Depth.CompareTo(a.Depth));

        foreach (var node in ordered)
        {
            node.SubtreeBytes += node.OwnBytes;
            node.SubtreeSizeOnDisk += node.OwnSizeOnDisk;
            node.SubtreeFileCount += node.OwnFileCount;

            if (node.Id == rootId)
            {
                continue;
            }

            // A missing parent means the directory was deleted between this node being discovered
            // and its ancestors being enumerated. Depth assignment has already treated the node as
            // a child of the root, and the fold has to agree, or its bytes would vanish from the
            // volume total. A node that is its own parent gets the same treatment: only the root is
            // legitimately self-parented, and it was skipped above.
            if (!nodes.TryGetValue(node.ParentId, out var parent) || ReferenceEquals(parent, node))
            {
                parent = root;
            }
            parent.SubtreeBytes += node.SubtreeBytes;
            parent.SubtreeSizeOnDisk += node.SubtreeSizeOnDisk;
            parent.SubtreeFileCount += node.SubtreeFileCount;
            parent.SubtreeDirCount += node.SubtreeDirCount + 1;
        }

        return new RollupResult(nodes, root, ordered, orphans);
    }

    /// <summary>
    /// Walks each node up to the root to establish its depth, memoising as it goes so the whole set
    /// costs O(n) rather than O(n * depth).
    /// </summary>
    /// <returns>Count of nodes that had to be re-parented to the root because their parent was missing.</returns>
    private static int AssignDepths(Dictionary<long, DirectoryNode> nodes, DirectoryNode root)
    {
        root.Depth = 0;

        var orphans = 0;
        var chain = new List<DirectoryNode>();

        foreach (var start in nodes.Values)
        {
            if (start.Depth >= 0)
            {
                continue;
            }

            chain.Clear();
            var current = start;
            var baseDepth = -1;

            while (true)
            {
                if (current.Depth >= 0)
                {
                    baseDepth = current.Depth;
                    break;
                }

                chain.Add(current);

                if (!nodes.TryGetValue(current.ParentId, out var parent))
                {
                    // Parent vanished mid-scan. Attach to the root so its bytes are still counted
                    // somewhere rather than being silently dropped from the volume total.
                    orphans += chain.Count;
                    baseDepth = 0;
                    break;
                }

                // A node that is its own parent, or a cycle, would otherwise spin here forever.
                // Only the root is legitimately its own parent.
                if (ReferenceEquals(parent, current))
                {
                    baseDepth = 0;
                    break;
                }

                current = parent;
            }

            // chain is leaf-to-root, so walk it backwards to number from the known depth downward.
            for (var i = chain.Count - 1; i >= 0; i--)
            {
                chain[i].Depth = ++baseDepth;
            }
        }

        return orphans;
    }
}
