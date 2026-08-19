namespace Strata.Core.Rollup;

/// <summary>Outcome of rolling subtree totals up a directory tree.</summary>
public sealed class RollupResult
{
    internal RollupResult(
        IReadOnlyDictionary<long, DirectoryNode> nodes,
        DirectoryNode root,
        IReadOnlyList<DirectoryNode> byDepthDescending,
        int orphanCount)
    {
        Nodes = nodes;
        Root = root;
        ByDepthDescending = byDepthDescending;
        OrphanCount = orphanCount;
    }

    public IReadOnlyDictionary<long, DirectoryNode> Nodes { get; }

    public DirectoryNode Root { get; }

    /// <summary>All nodes, deepest first. The order the rollup accumulated in.</summary>
    public IReadOnlyList<DirectoryNode> ByDepthDescending { get; }

    /// <summary>
    /// Nodes whose parent was missing from the input and which were therefore re-parented to the
    /// root. Non-zero means part of the tree was enumerated without its ancestors, which on a live
    /// filesystem happens when a directory is deleted mid-scan. Worth reporting, not worth failing.
    /// </summary>
    public int OrphanCount { get; }

    public long TotalBytes => Root.SubtreeBytes;

    public long TotalSizeOnDisk => Root.SubtreeSizeOnDisk;

    public long TotalFileCount => Root.SubtreeFileCount;

    public int TotalDirCount => Root.SubtreeDirCount + 1;

    public int MaxDepth
    {
        get
        {
            var max = 0;
            foreach (var node in Nodes.Values)
            {
                if (node.Depth > max)
                {
                    max = node.Depth;
                }
            }

            return max;
        }
    }
}
