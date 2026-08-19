using Strata.Core.Rollup;

namespace Strata.Tests;

public sealed class RollupEngineTests
{
    private const long RootId = 5;

    [Fact]
    public void RootOfAnEmptyVolumeIsZero()
    {
        var nodes = Tree(Node(RootId, RootId, ""));

        var result = RollupEngine.Compute(nodes, RootId);

        Assert.Equal(0, result.TotalBytes);
        Assert.Equal(0, result.TotalFileCount);
        Assert.Equal(1, result.TotalDirCount);
        Assert.Equal(0, result.Root.Depth);
    }

    [Fact]
    public void SubtreeBytesRollUpThroughEveryAncestor()
    {
        // T:\ -> Steam -> steamapps -> common -> Foo, with bytes at three different levels.
        var nodes = Tree(
            Node(RootId, RootId, ""),
            Node(10, RootId, "Steam", ownBytes: 100),
            Node(20, 10, "steamapps", ownBytes: 200),
            Node(30, 20, "common", ownBytes: 400),
            Node(40, 30, "Foo", ownBytes: 800));

        var result = RollupEngine.Compute(nodes, RootId);

        Assert.Equal(800, nodes[40].SubtreeBytes);
        Assert.Equal(1200, nodes[30].SubtreeBytes);
        Assert.Equal(1400, nodes[20].SubtreeBytes);
        Assert.Equal(1500, nodes[10].SubtreeBytes);
        Assert.Equal(1500, result.TotalBytes);
    }

    [Fact]
    public void SiblingsAreSummedNotOverwritten()
    {
        var nodes = Tree(
            Node(RootId, RootId, ""),
            Node(10, RootId, "a", ownBytes: 5),
            Node(11, RootId, "b", ownBytes: 7),
            Node(12, RootId, "c", ownBytes: 11));

        var result = RollupEngine.Compute(nodes, RootId);

        Assert.Equal(23, result.TotalBytes);
    }

    [Fact]
    public void DepthIsDistanceFromTheRoot()
    {
        var nodes = Tree(
            Node(RootId, RootId, ""),
            Node(10, RootId, "one"),
            Node(20, 10, "two"),
            Node(30, 20, "three"));

        var result = RollupEngine.Compute(nodes, RootId);

        Assert.Equal(0, nodes[RootId].Depth);
        Assert.Equal(1, nodes[10].Depth);
        Assert.Equal(2, nodes[20].Depth);
        Assert.Equal(3, nodes[30].Depth);
        Assert.Equal(3, result.MaxDepth);
    }

    [Fact]
    public void DirectoryCountsExcludeSelfAndIncludeTheWholeSubtree()
    {
        var nodes = Tree(
            Node(RootId, RootId, ""),
            Node(10, RootId, "a"),
            Node(20, 10, "b"),
            Node(21, 10, "c"),
            Node(30, 20, "d"));

        var result = RollupEngine.Compute(nodes, RootId);

        Assert.Equal(0, nodes[30].SubtreeDirCount);
        Assert.Equal(1, nodes[20].SubtreeDirCount);
        Assert.Equal(3, nodes[10].SubtreeDirCount);
        Assert.Equal(5, result.TotalDirCount); // four directories plus the root
    }

    [Fact]
    public void FileCountsAndSizeOnDiskRollUpIndependently()
    {
        var nodes = Tree(
            Node(RootId, RootId, ""),
            Node(10, RootId, "compressed", ownBytes: 1000, ownSizeOnDisk: 250, ownFileCount: 3),
            Node(11, RootId, "plain", ownBytes: 500, ownSizeOnDisk: 512, ownFileCount: 2));

        var result = RollupEngine.Compute(nodes, RootId);

        Assert.Equal(1500, result.TotalBytes);
        Assert.Equal(762, result.TotalSizeOnDisk);
        Assert.Equal(5, result.TotalFileCount);
    }

    /// <summary>
    /// Deep nesting is not hypothetical: npm and NuGet caches routinely produce paths hundreds of
    /// levels down, which is why the rollup is an ordered pass rather than recursion.
    /// </summary>
    [Fact]
    public void VeryDeepNestingDoesNotOverflowTheStack()
    {
        const int depth = 20_000;
        const long firstChildId = 100; // clear of RootId, so the chain cannot loop back onto the root
        var nodes = new Dictionary<long, DirectoryNode> { [RootId] = Node(RootId, RootId, "") };

        for (long i = 0; i < depth; i++)
        {
            var id = firstChildId + i;
            var parent = i == 0 ? RootId : id - 1;
            nodes[id] = Node(id, parent, "d" + i, ownBytes: 1);
        }

        var result = RollupEngine.Compute(nodes, RootId);

        Assert.Equal(depth, result.TotalBytes);
        Assert.Equal(depth, result.MaxDepth);
    }

    /// <summary>
    /// A directory deleted between being discovered and being enumerated leaves children whose
    /// parent never made it into the node set. Their bytes still exist on the volume, so they are
    /// re-parented to the root rather than dropped from the total.
    /// </summary>
    [Fact]
    public void OrphansAreReparentedToTheRootAndCounted()
    {
        var nodes = Tree(
            Node(RootId, RootId, ""),
            Node(10, RootId, "kept", ownBytes: 100),
            Node(20, 999, "orphan", ownBytes: 50));

        var result = RollupEngine.Compute(nodes, RootId);

        Assert.Equal(1, result.OrphanCount);
        Assert.Equal(150, result.TotalBytes);
    }

    [Fact]
    public void MissingRootIsRejected()
    {
        var nodes = Tree(Node(10, 10, "not-the-root"));

        Assert.Throws<ArgumentException>(() => RollupEngine.Compute(nodes, RootId));
    }

    [Fact]
    public void EmptyDirectoriesSurviveWithZeroTotals()
    {
        var nodes = Tree(
            Node(RootId, RootId, ""),
            Node(10, RootId, "empty"));

        RollupEngine.Compute(nodes, RootId);

        Assert.Equal(0, nodes[10].SubtreeBytes);
        Assert.Equal(0, nodes[10].SubtreeFileCount);
    }

    private static Dictionary<long, DirectoryNode> Tree(params DirectoryNode[] nodes)
        => nodes.ToDictionary(static n => n.Id);

    private static DirectoryNode Node(
        long id,
        long parentId,
        string name,
        long ownBytes = 0,
        long ownSizeOnDisk = 0,
        int ownFileCount = 0)
        => new(id, parentId, name)
        {
            OwnBytes = ownBytes,
            OwnSizeOnDisk = ownSizeOnDisk,
            OwnFileCount = ownFileCount,
        };
}
