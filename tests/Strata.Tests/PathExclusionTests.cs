using Strata.Core.Diffing;
using Strata.Core.Rollup;

namespace Strata.Tests;

public sealed class PathExclusionTests
{
    [Theory]
    [InlineData(@"C:\Data", @"C:\Data", true)]
    [InlineData(@"C:\Data\sub\deep", @"C:\Data", true)]
    [InlineData(@"c:\data\sub", @"C:\Data", true)]
    [InlineData(@"C:\Data", @"C:\Data\", true)]
    [InlineData(@"C:\Other", @"C:\Data", false)]
    public void PrefixMatchingHandlesTheObviousCases(string path, string excluded, bool expected)
        => Assert.Equal(expected, PathExclusions.IsExcluded(path, [excluded]));

    /// <summary>
    /// The match has to end on a separator. Excluding "C:\Data" must not also silence a sibling
    /// whose name merely starts with the same characters.
    /// </summary>
    [Fact]
    public void ASiblingSharingAPrefixIsNotExcluded()
    {
        Assert.False(PathExclusions.IsExcluded(@"C:\DataOfSomethingElse", [@"C:\Data"]));
        Assert.False(PathExclusions.IsExcluded(@"C:\Database\file", [@"C:\Data"]));
    }

    [Fact]
    public void AnEmptyExclusionListKeepsEverything()
    {
        var tree = Tree();
        var deltas = new Dictionary<long, DirectoryDelta> { [10] = new(100, 100, 1, 0) };

        Assert.Single(PathExclusions.Filter(deltas, tree, "C:", []));
    }

    [Fact]
    public void ExcludedDirectoriesAreDroppedAndOthersKept()
    {
        var tree = Tree();

        var deltas = new Dictionary<long, DirectoryDelta>
        {
            [10] = new(100, 100, 1, 0), // C:\AppData\Strata  - excluded
            [11] = new(200, 200, 1, 0), // C:\Games           - kept
        };

        var kept = PathExclusions.Filter(deltas, tree, "C:", [@"C:\AppData\Strata"]);

        Assert.Single(kept);
        Assert.True(kept.ContainsKey(11));
    }

    [Fact]
    public void DescendantsOfAnExcludedDirectoryAreAlsoDropped()
    {
        var tree = Tree();
        var deltas = new Dictionary<long, DirectoryDelta> { [12] = new(500, 500, 1, 0) };

        Assert.Empty(PathExclusions.Filter(deltas, tree, "C:", [@"C:\AppData\Strata"]));
    }

    private static Dictionary<long, DirectoryNode> Tree()
    {
        var nodes = new Dictionary<long, DirectoryNode>
        {
            [5] = new(5, 5, ""),
            [9] = new(9, 5, "AppData"),
            [10] = new(10, 9, "Strata"),
            [11] = new(11, 5, "Games"),
            [12] = new(12, 10, "cache"),
        };

        RollupEngine.Compute(nodes, 5);
        return nodes;
    }
}
