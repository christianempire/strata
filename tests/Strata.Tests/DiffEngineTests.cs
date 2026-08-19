using Strata.Core.Diffing;
using Strata.Core.Rollup;

namespace Strata.Tests;

public sealed class DiffEngineTests
{
    private const long RootId = 5;
    private const long MB = 1024 * 1024;
    private const long GB = 1024 * MB;

    /// <summary>
    /// The headline case from the brief. Two hundred files appearing under one game directory must
    /// report as that directory, not as two hundred rows.
    /// </summary>
    [Fact]
    public void ChangeConcentratedInOneDirectoryIsAttributedToIt()
    {
        var nodes = SteamTree();

        var changes = DiffEngine.Attribute(
            nodes,
            RootId,
            new Dictionary<long, DirectoryDelta>
            {
                [50] = new(32 * GB, 32 * GB, FilesAdded: 200, FilesRemoved: 0),
            },
            "D:");

        var change = Assert.Single(changes);
        Assert.Equal(@"D:\Steam\steamapps\common\Foo", change.Path);
        Assert.Equal(32 * GB, change.DeltaBytes);
        Assert.Equal(200, change.FileCountDelta);
    }

    /// <summary>
    /// The other half of the rule: once change is spread across siblings, the parent is the honest
    /// answer and descending further tells the story worse.
    /// </summary>
    [Fact]
    public void ChangeSpreadAcrossSiblingsStopsAtTheirParent()
    {
        var nodes = SteamTree();

        var changes = DiffEngine.Attribute(
            nodes,
            RootId,
            new Dictionary<long, DirectoryDelta>
            {
                [50] = new(10 * GB, 10 * GB, 10, 0),
                [51] = new(10 * GB, 10 * GB, 10, 0),
                [52] = new(10 * GB, 10 * GB, 10, 0),
            },
            "D:");

        var change = Assert.Single(changes);
        Assert.Equal(@"D:\Steam\steamapps\common", change.Path);
        Assert.Equal(30 * GB, change.DeltaBytes);
        Assert.Equal(30, change.FileCountDelta);
    }

    /// <summary>
    /// A dominant child still wins when a sibling changed slightly, since the sibling does not
    /// change the answer to "what grew".
    /// </summary>
    [Fact]
    public void ANearlyDominantChildStillWins()
    {
        var nodes = SteamTree();

        var changes = DiffEngine.Attribute(
            nodes,
            RootId,
            new Dictionary<long, DirectoryDelta>
            {
                [50] = new(38 * GB, 38 * GB, 100, 0),
                [51] = new(1 * GB, 1 * GB, 2, 0),
            },
            "D:");

        Assert.Equal(@"D:\Steam\steamapps\common\Foo", changes[0].Path);
    }

    [Fact]
    public void DominanceThresholdIsConfigurable()
    {
        var nodes = SteamTree();

        var deltas = new Dictionary<long, DirectoryDelta>
        {
            [50] = new(7 * GB, 7 * GB, 10, 0),
            [51] = new(3 * GB, 3 * GB, 10, 0),
        };

        // At 0.9, seven of ten GB is not enough to blame one child.
        var strict = DiffEngine.Attribute(nodes, RootId, deltas, "D:", new DiffOptions { DominanceThreshold = 0.9 });
        Assert.Equal(@"D:\Steam\steamapps\common", strict[0].Path);

        // At 0.6 it is.
        var loose = DiffEngine.Attribute(nodes, RootId, deltas, "D:", new DiffOptions { DominanceThreshold = 0.6 });
        Assert.Equal(@"D:\Steam\steamapps\common\Foo", loose[0].Path);
    }

    [Fact]
    public void DeletionsAreReportedAsNegativeChanges()
    {
        var nodes = SteamTree();

        var changes = DiffEngine.Attribute(
            nodes,
            RootId,
            new Dictionary<long, DirectoryDelta> { [50] = new(-12 * GB, 12 * GB, 0, 300) },
            "D:");

        var change = Assert.Single(changes);
        Assert.Equal(-12 * GB, change.DeltaBytes);
        Assert.Equal(-300, change.FileCountDelta);
        Assert.Equal(ChangeKind.Vanished, change.Kind);
    }

    /// <summary>
    /// A growth and an equal deletion in different places must not cancel into silence. Both are
    /// real answers to "what happened".
    /// </summary>
    [Fact]
    public void GrowthAndShrinkageInDifferentPlacesAreBothReported()
    {
        var nodes = SteamTree();

        var changes = DiffEngine.Attribute(
            nodes,
            RootId,
            new Dictionary<long, DirectoryDelta>
            {
                [50] = new(20 * GB, 20 * GB, 100, 0),
                [60] = new(-20 * GB, 20 * GB, 0, 50),
            },
            "D:");

        Assert.Equal(2, changes.Count);
        Assert.Contains(changes, c => c.DeltaBytes > 0 && c.Path.Contains("Foo", StringComparison.Ordinal));
        Assert.Contains(changes, c => c.DeltaBytes < 0 && c.Path.Contains("Downloads", StringComparison.Ordinal));
    }

    /// <summary>
    /// Churn is what separates "nothing happened" from "50 GB was written and deleted", which is
    /// invisible to a net-only view and is the interesting number for SSD wear.
    /// </summary>
    [Fact]
    public void ChurnIsReportedEvenWhenNetChangeIsZero()
    {
        var nodes = SteamTree();

        var changes = DiffEngine.Attribute(
            nodes,
            RootId,
            new Dictionary<long, DirectoryDelta> { [50] = new(0, 50 * GB, 500, 500) },
            "D:");

        var change = Assert.Single(changes);
        Assert.Equal(0, change.DeltaBytes);
        Assert.Equal(50 * GB, change.ChurnBytes);
    }

    [Fact]
    public void ChangesBelowTheFloorAreNotReported()
    {
        var nodes = SteamTree();

        var changes = DiffEngine.Attribute(
            nodes,
            RootId,
            new Dictionary<long, DirectoryDelta> { [50] = new(4096, 4096, 1, 0) },
            "D:",
            new DiffOptions { MinimumDeltaBytes = 1 * MB });

        Assert.Empty(changes);
    }

    [Fact]
    public void ResultsAreRankedBySizeAndCapped()
    {
        var nodes = new Dictionary<long, DirectoryNode> { [RootId] = new(RootId, RootId, "") };
        var deltas = new Dictionary<long, DirectoryDelta>();

        for (long i = 1; i <= 30; i++)
        {
            nodes[100 + i] = new DirectoryNode(100 + i, RootId, "d" + i);
            deltas[100 + i] = new DirectoryDelta(i * GB, i * GB, 1, 0);
        }

        RollupEngine.Compute(nodes, RootId);

        var changes = DiffEngine.Attribute(nodes, RootId, deltas, "D:", new DiffOptions { MaxResults = 5 });

        Assert.Equal(5, changes.Count);
        Assert.Equal(30 * GB, changes[0].DeltaBytes);
        Assert.True(changes[0].AbsoluteDelta >= changes[4].AbsoluteDelta);
    }

    [Fact]
    public void DescentStopsAtTheConfiguredMaximumDepth()
    {
        var nodes = SteamTree();

        var changes = DiffEngine.Attribute(
            nodes,
            RootId,
            new Dictionary<long, DirectoryDelta> { [50] = new(9 * GB, 9 * GB, 5, 0) },
            "D:",
            new DiffOptions { MaxDepth = 2 });

        // Foo is at depth 4; the report is cut off at 2.
        Assert.Equal(@"D:\Steam\steamapps", changes[0].Path);
    }

    [Fact]
    public void NoDeltasProducesNoChanges()
    {
        var nodes = SteamTree();
        Assert.Empty(DiffEngine.Attribute(nodes, RootId, new Dictionary<long, DirectoryDelta>(), "D:"));
    }

    [Fact]
    public void AnEmptyDeltaIsIgnored()
    {
        var nodes = SteamTree();

        var changes = DiffEngine.Attribute(
            nodes,
            RootId,
            new Dictionary<long, DirectoryDelta> { [50] = default },
            "D:");

        Assert.Empty(changes);
    }

    /// <summary>
    /// A delta for a directory that is no longer in the tree must not throw. Directories are deleted
    /// between a change being journalled and a snapshot being taken all the time.
    /// </summary>
    [Fact]
    public void DeltasForUnknownDirectoriesAreSurvivable()
    {
        var nodes = SteamTree();

        var changes = DiffEngine.Attribute(
            nodes,
            RootId,
            new Dictionary<long, DirectoryDelta>
            {
                [99999] = new(5 * GB, 5 * GB, 1, 0),
                [50] = new(6 * GB, 6 * GB, 1, 0),
            },
            "D:");

        Assert.Contains(changes, c => c.Path.Contains("Foo", StringComparison.Ordinal));
    }

    // --- the unattributable remainder -----------------------------------------------------------

    /// <summary>
    /// The case measured on the real database, which used to produce the one row this engine exists
    /// to avoid.
    /// </summary>
    /// <remarks>
    /// Baseline against a recent tick on C:: the volume lost 17.5 GB, C:\Users accounted for 15.0 GB
    /// of it — 86%, under the 90% dominance threshold — and the other 2.2 GB was files deleted
    /// straight out of the volume root. Own bytes were not modelled as a child, so they diluted
    /// Users below the threshold without ever being nameable, and the answer came back "C: -17.5 GB".
    /// Lowering the reporting floor could not help: the remainder was at the root rather than in a
    /// child. See docs/MEASUREMENTS.md, "Diffing bounded rollups leaves an unattributable remainder".
    /// </remarks>
    [Fact]
    public void OwnBytesNoLongerDiluteAChildIntoAUselessRootRow()
    {
        var nodes = SteamTree();

        var changes = DiffEngine.Attribute(
            nodes,
            RootId,
            new Dictionary<long, DirectoryDelta>
            {
                [10] = new(-15 * GB, 15 * GB, 0, 8_827),
                [RootId] = new(-2 * GB, 2 * GB, 0, 9),
            },
            "D:");

        Assert.Equal(2, changes.Count);

        var named = Assert.Single(changes, c => c.Kind != ChangeKind.Remainder);
        Assert.Equal(@"D:\Steam", named.Path);
        Assert.Equal(-15 * GB, named.DeltaBytes);

        var remainder = Assert.Single(changes, c => c.Kind == ChangeKind.Remainder);
        Assert.Equal("D:", remainder.Path);
        Assert.Equal(-2 * GB, remainder.DeltaBytes);
    }

    /// <summary>
    /// The rows still add up to the change they describe.
    /// </summary>
    /// <remarks>
    /// The whole point of the remainder. Without it the list either understates its own total or
    /// collapses into a single root row, and a Compare screen that cannot reconcile to the volume's
    /// net change is a Compare screen nobody can trust.
    /// </remarks>
    [Fact]
    public void ReportedRowsReconcileToTheTotal()
    {
        var nodes = SteamTree();

        var changes = DiffEngine.Attribute(
            nodes,
            RootId,
            new Dictionary<long, DirectoryDelta>
            {
                [50] = new(20 * GB, 20 * GB, 400, 0),
                [60] = new(6 * GB, 6 * GB, 100, 0),
                [RootId] = new(4 * GB, 4 * GB, 12, 0),
            },
            "D:");

        Assert.Equal(30 * GB, changes.Sum(c => c.DeltaBytes));
        Assert.Contains(changes, c => c.Kind == ChangeKind.Remainder && c.DeltaBytes == 4 * GB);
    }

    /// <summary>
    /// A remainder too small to matter is not drawn.
    /// </summary>
    /// <remarks>
    /// It is subject to the same reporting floor as everything else. A row saying "and 40 KB
    /// elsewhere" is noise, and the alternative to dropping it — a list that always reconciles to
    /// the byte — is not worth a row per parent.
    /// </remarks>
    [Fact]
    public void ARemainderUnderTheFloorIsNotReported()
    {
        var nodes = new Dictionary<long, DirectoryNode> { [RootId] = new(RootId, RootId, "") };
        var deltas = new Dictionary<long, DirectoryDelta>
        {
            // Sixty-four kilobytes written straight into the root, against five directories that
            // each moved gigabytes. Real, and not worth a row.
            [RootId] = new(64 * 1024, 64 * 1024, 1, 0),
        };

        for (long i = 1; i <= 5; i++)
        {
            nodes[100 + i] = new DirectoryNode(100 + i, RootId, "d" + i);
            deltas[100 + i] = new DirectoryDelta(i * GB, i * GB, 1, 0);
        }

        RollupEngine.Compute(nodes, RootId);

        var changes = DiffEngine.Attribute(nodes, RootId, deltas, "D:");

        Assert.Equal(5, changes.Count);
        Assert.DoesNotContain(changes, c => c.Kind == ChangeKind.Remainder);
    }

    /// <summary>
    /// When the bytes moved here rather than beneath here, this directory is the answer and gets an
    /// ordinary row.
    /// </summary>
    /// <remarks>
    /// The pagefile case: something large written straight into a directory, with a trivial change
    /// in a child. Reporting "elsewhere in D:\ +9 GB" alongside "D:\Downloads +1 GB" would be
    /// technically true and needlessly coy about a change that has a perfectly good name.
    /// </remarks>
    [Fact]
    public void ADominantRemainderKeepsTheDirectoryAsTheAnswer()
    {
        var nodes = SteamTree();

        var changes = DiffEngine.Attribute(
            nodes,
            RootId,
            new Dictionary<long, DirectoryDelta>
            {
                [RootId] = new(20 * GB, 20 * GB, 3, 0),
                [60] = new(1 * GB, 1 * GB, 4, 0),
            },
            "D:");

        var change = Assert.Single(changes);
        Assert.Equal("D:", change.Path);
        Assert.NotEqual(ChangeKind.Remainder, change.Kind);
        Assert.Equal(21 * GB, change.DeltaBytes);
    }

    /// <summary>
    /// A remainder appears wherever it occurs, not only at the root.
    /// </summary>
    [Fact]
    public void ARemainderIsReportedAtTheDepthItOccursAt()
    {
        var nodes = SteamTree();

        var changes = DiffEngine.Attribute(
            nodes,
            RootId,
            new Dictionary<long, DirectoryDelta>
            {
                [50] = new(20 * GB, 20 * GB, 400, 0),
                [30] = new(5 * GB, 5 * GB, 9, 0),
            },
            "D:");

        var remainder = Assert.Single(changes, c => c.Kind == ChangeKind.Remainder);
        Assert.Equal(@"D:\Steam\steamapps\common", remainder.Path);
        Assert.Equal(5 * GB, remainder.DeltaBytes);
    }

    /// <summary>
    /// Expanding many siblings reports the leftovers too.
    /// </summary>
    /// <remarks>
    /// This path already expanded before the remainder existed, and dropped whatever the siblings
    /// did not account for on the floor. Two directions in one node is the case that matters: the
    /// net is a misleading near-zero, and the remainder is the part with no name at all.
    /// </remarks>
    [Fact]
    public void ExpandingOpposingBranchesStillNamesTheLeftovers()
    {
        var nodes = SteamTree();

        var changes = DiffEngine.Attribute(
            nodes,
            RootId,
            new Dictionary<long, DirectoryDelta>
            {
                [50] = new(20 * GB, 20 * GB, 400, 0),
                [60] = new(-23 * GB, 23 * GB, 0, 380),
                [RootId] = new(3 * GB, 3 * GB, 7, 0),
            },
            "D:");

        Assert.Equal(3, changes.Count);
        Assert.Contains(changes, c => c.Path.EndsWith("Foo", StringComparison.Ordinal));
        Assert.Contains(changes, c => c.Path.EndsWith("Downloads", StringComparison.Ordinal));

        // The volume nets to nothing. Two branches and the leftovers are what actually happened.
        var remainder = Assert.Single(changes, c => c.Kind == ChangeKind.Remainder);
        Assert.Equal(3 * GB, remainder.DeltaBytes);
        Assert.Equal("D:", remainder.Path);
    }

    /// <summary>
    /// A child under the reporting floor is folded into the remainder rather than dropped.
    /// </summary>
    [Fact]
    public void ChildrenBelowTheFloorAreFoldedIntoTheRemainder()
    {
        var nodes = SteamTree();

        var changes = DiffEngine.Attribute(
            nodes,
            RootId,
            new Dictionary<long, DirectoryDelta>
            {
                [50] = new(20 * GB, 20 * GB, 400, 0),
                [52] = new(400 * MB, 400 * MB, 3, 0),
                [30] = new(3 * GB, 3 * GB, 9, 0),
            },
            "D:",
            new DiffOptions { MinimumDeltaBytes = GB });

        Assert.Equal(2, changes.Count);
        Assert.Contains(changes, c => c.Path.EndsWith("Foo", StringComparison.Ordinal));

        // Baz is under the floor and common's own bytes have no name of their own. Both are here,
        // added together, rather than either being dropped.
        var remainder = Assert.Single(changes, c => c.Kind == ChangeKind.Remainder);
        Assert.Equal(3 * GB + (400 * MB), remainder.DeltaBytes);
        Assert.Equal(@"D:\Steam\steamapps\common", remainder.Path);
    }

    /// <summary>
    /// D:\ -> Steam -> steamapps -> common -> {Foo, Bar, Baz}, plus a separate Downloads tree.
    /// </summary>
    private static Dictionary<long, DirectoryNode> SteamTree()
    {
        var nodes = new Dictionary<long, DirectoryNode>
        {
            [RootId] = new(RootId, RootId, ""),
            [10] = new(10, RootId, "Steam"),
            [20] = new(20, 10, "steamapps"),
            [30] = new(30, 20, "common"),
            [50] = new(50, 30, "Foo"),
            [51] = new(51, 30, "Bar"),
            [52] = new(52, 30, "Baz"),
            [60] = new(60, RootId, "Downloads"),
        };

        RollupEngine.Compute(nodes, RootId);
        return nodes;
    }
}
