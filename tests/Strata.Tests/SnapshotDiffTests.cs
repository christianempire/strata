using Strata.Core.Diffing;

namespace Strata.Tests;

/// <summary>
/// Covers the translation from two stored rollups into something the diff engine understands.
/// </summary>
/// <remarks>
/// The engine itself is tested in <see cref="DiffEngineTests"/>. What is at stake here is the
/// conversion: subtree totals into own totals, materialised paths into a tree, and presence in each
/// side into appeared and vanished. Getting any of those wrong produces a diff that looks plausible
/// and double-counts, which is the failure mode worth spending tests on.
/// </remarks>
public sealed class SnapshotDiffTests
{
    private const long MB = 1024 * 1024;
    private const long GB = 1024 * MB;

    /// <summary>
    /// The headline case: a game installed between two snapshots reports as the game's directory.
    /// </summary>
    [Fact]
    public void AGameInstallReportsAsOneRow()
    {
        var before = new[]
        {
            Dir(@"D:", 0, 100 * GB, 1000),
            Dir(@"D:\Steam", 1, 60 * GB, 500),
            Dir(@"D:\Steam\steamapps", 2, 60 * GB, 500),
            Dir(@"D:\Steam\steamapps\common", 3, 60 * GB, 500),
        };

        var after = new[]
        {
            Dir(@"D:", 0, 132 * GB, 1200),
            Dir(@"D:\Steam", 1, 92 * GB, 700),
            Dir(@"D:\Steam\steamapps", 2, 92 * GB, 700),
            Dir(@"D:\Steam\steamapps\common", 3, 92 * GB, 700),
            Dir(@"D:\Steam\steamapps\common\Foo", 4, 32 * GB, 200),
        };

        var changes = SnapshotDiff.Compare(before, after);

        var change = Assert.Single(changes);
        Assert.Equal(@"D:\Steam\steamapps\common\Foo", change.Path);
        Assert.Equal(32 * GB, change.DeltaBytes);
        Assert.Equal(200, change.FileCountDelta);
        Assert.Equal(ChangeKind.Appeared, change.Kind);
    }

    /// <summary>
    /// Subtree totals must become own totals before the engine sees them. Feeding subtree totals
    /// straight in counts the same bytes once per level of ancestry, which is the single easiest
    /// mistake to make here and the hardest to notice: every number is plausible and every total
    /// is wrong.
    /// </summary>
    [Fact]
    public void SubtreeTotalsAreNotCountedOncePerAncestor()
    {
        var before = new[]
        {
            Dir(@"C:", 0, 10 * GB, 100),
            Dir(@"C:\a", 1, 10 * GB, 100),
            Dir(@"C:\a\b", 2, 10 * GB, 100),
        };

        var after = new[]
        {
            Dir(@"C:", 0, 15 * GB, 150),
            Dir(@"C:\a", 1, 15 * GB, 150),
            Dir(@"C:\a\b", 2, 15 * GB, 150),
        };

        var changes = SnapshotDiff.Compare(before, after);

        var change = Assert.Single(changes);
        Assert.Equal(@"C:\a\b", change.Path);
        Assert.Equal(5 * GB, change.DeltaBytes);
    }

    /// <summary>
    /// Bytes held directly in a directory, and bytes in children too small to have been recorded,
    /// both land on that directory rather than disappearing.
    /// </summary>
    [Fact]
    public void GrowthBelowTheSizeFloorIsAttributedToTheNearestRecordedDirectory()
    {
        var before = new[]
        {
            Dir(@"C:", 0, 10 * GB, 100),
            Dir(@"C:\logs", 1, 1 * GB, 50),
        };

        // The 4 GB arrived inside C:\logs, spread across subdirectories that never cleared the
        // floor and so were never recorded.
        var after = new[]
        {
            Dir(@"C:", 0, 14 * GB, 140),
            Dir(@"C:\logs", 1, 5 * GB, 90),
        };

        var changes = SnapshotDiff.Compare(before, after);

        var change = Assert.Single(changes);
        Assert.Equal(@"C:\logs", change.Path);
        Assert.Equal(4 * GB, change.DeltaBytes);
        Assert.Equal(40, change.FileCountDelta);
        Assert.Equal(ChangeKind.Grew, change.Kind);
    }

    /// <summary>
    /// A directory absent from the later snapshot is gone, not merely smaller.
    /// </summary>
    /// <remarks>
    /// The engine infers vanished from a delete-only file count, which two snapshots cannot supply,
    /// so this is the case that would silently be reported as an ordinary shrink.
    /// </remarks>
    [Fact]
    public void ADirectoryMissingFromTheLaterSnapshotVanished()
    {
        var before = new[]
        {
            Dir(@"C:", 0, 20 * GB, 200),
            Dir(@"C:\cache", 1, 8 * GB, 80),
            Dir(@"C:\build", 1, 8 * GB, 80),
        };

        var after = new[]
        {
            Dir(@"C:", 0, 12 * GB, 120),
            Dir(@"C:\cache", 1, 8 * GB, 80),
        };

        var change = Assert.Single(SnapshotDiff.Compare(before, after));

        Assert.Equal(@"C:\build", change.Path);
        Assert.Equal(ChangeKind.Vanished, change.Kind);
        Assert.Equal(-8 * GB, change.DeltaBytes);
    }

    /// <summary>The other half of that: still present, just smaller.</summary>
    [Fact]
    public void ADirectoryThatOnlyShrankIsNotReportedAsVanished()
    {
        var before = new[]
        {
            Dir(@"C:", 0, 20 * GB, 200),
            Dir(@"C:\cache", 1, 8 * GB, 80),
        };

        var after = new[]
        {
            Dir(@"C:", 0, 14 * GB, 140),
            Dir(@"C:\cache", 1, 2 * GB, 20),
        };

        var change = Assert.Single(SnapshotDiff.Compare(before, after));

        Assert.Equal(@"C:\cache", change.Path);
        Assert.Equal(ChangeKind.Shrank, change.Kind);
        Assert.Equal(-6 * GB, change.DeltaBytes);
    }

    /// <summary>
    /// Growth in one branch cancelling deletion in another must report both halves. The net is a
    /// misleading zero, and a report that stopped at the parent would say nothing happened.
    /// </summary>
    [Fact]
    public void OpposingBranchesAreBothReported()
    {
        var before = new[]
        {
            Dir(@"C:", 0, 20 * GB, 200),
            Dir(@"C:\grew", 1, 10 * GB, 100),
            Dir(@"C:\shrank", 1, 10 * GB, 100),
        };

        var after = new[]
        {
            Dir(@"C:", 0, 20 * GB, 200),
            Dir(@"C:\grew", 1, 15 * GB, 150),
            Dir(@"C:\shrank", 1, 5 * GB, 50),
        };

        var changes = SnapshotDiff.Compare(before, after);

        Assert.Equal(2, changes.Count);
        Assert.Equal(5 * GB, Assert.Single(changes, c => c.Path == @"C:\grew").DeltaBytes);
        Assert.Equal(-5 * GB, Assert.Single(changes, c => c.Path == @"C:\shrank").DeltaBytes);
    }

    /// <summary>Scoping to a subtree answers about that subtree and ignores everything else.</summary>
    [Fact]
    public void ScopingToAPathIgnoresChangesOutsideIt()
    {
        var before = new[]
        {
            Dir(@"C:", 0, 20 * GB, 200),
            Dir(@"C:\Users", 1, 10 * GB, 100),
            Dir(@"C:\Windows", 1, 10 * GB, 100),
        };

        var after = new[]
        {
            Dir(@"C:", 0, 40 * GB, 400),
            Dir(@"C:\Users", 1, 25 * GB, 250),
            Dir(@"C:\Windows", 1, 15 * GB, 150),
        };

        var changes = SnapshotDiff.Compare(before, after, @"C:\Users");

        var change = Assert.Single(changes);
        Assert.Equal(@"C:\Users", change.Path);
        Assert.Equal(15 * GB, change.DeltaBytes);
    }

    /// <summary>
    /// A prefix match is not a path match. "C:\Program Files" and "C:\Program Files (x86)" are
    /// siblings, and scoping to the first must not drag in the second.
    /// </summary>
    [Fact]
    public void ScopingDoesNotSwallowASiblingWithASharedPrefix()
    {
        var before = new[]
        {
            Dir(@"C:", 0, 20 * GB, 200),
            Dir(@"C:\Program Files", 1, 10 * GB, 100),
            Dir(@"C:\Program Files (x86)", 1, 10 * GB, 100),
        };

        var after = new[]
        {
            Dir(@"C:", 0, 30 * GB, 300),
            Dir(@"C:\Program Files", 1, 10 * GB, 100),
            Dir(@"C:\Program Files (x86)", 1, 20 * GB, 200),
        };

        Assert.Empty(SnapshotDiff.Compare(before, after, @"C:\Program Files"));
    }

    /// <summary>Two identical snapshots have nothing to say.</summary>
    [Fact]
    public void IdenticalSnapshotsProduceNoChanges()
    {
        var rows = new[]
        {
            Dir(@"C:", 0, 20 * GB, 200),
            Dir(@"C:\Users", 1, 10 * GB, 100),
        };

        Assert.Empty(SnapshotDiff.Compare(rows, rows));
    }

    /// <summary>
    /// A snapshot that recorded no directories is the normal state of an incremental tick that
    /// predates the rollup being materialised, so it has to be a quiet empty answer rather than an
    /// exception.
    /// </summary>
    [Fact]
    public void AnEmptySideIsNotAnError()
    {
        var rows = new[] { Dir(@"C:", 0, 20 * GB, 200) };

        Assert.Empty(SnapshotDiff.Compare([], []));
        Assert.NotEmpty(SnapshotDiff.Compare([], rows));
        Assert.NotEmpty(SnapshotDiff.Compare(rows, []));
    }

    /// <summary>
    /// Changes under the reporting floor are noise. A background service touching a few hundred
    /// kilobytes should not be a row in a feed about what ate the disk.
    /// </summary>
    [Fact]
    public void ChangesUnderTheFloorAreNotReported()
    {
        var before = new[] { Dir(@"C:", 0, 20 * GB, 200), Dir(@"C:\Users", 1, 10 * GB, 100) };
        var after = new[] { Dir(@"C:", 0, 20 * GB + 512, 200), Dir(@"C:\Users", 1, 10 * GB + 512, 100) };

        Assert.Empty(SnapshotDiff.Compare(before, after, null, new DiffOptions
        {
            MinimumDeltaBytes = MB,
        }));
    }

    /// <summary>
    /// NTFS is case-insensitive, so a rollup written with one casing and read against another is the
    /// same directory, not a vanished one and a new one.
    /// </summary>
    [Fact]
    public void PathsMatchCaseInsensitively()
    {
        var before = new[] { Dir(@"C:", 0, 20 * GB, 200), Dir(@"C:\Users", 1, 10 * GB, 100) };
        var after = new[] { Dir(@"C:", 0, 25 * GB, 250), Dir(@"C:\USERS", 1, 15 * GB, 150) };

        var change = Assert.Single(SnapshotDiff.Compare(before, after));

        Assert.Equal(5 * GB, change.DeltaBytes);
        Assert.Equal(ChangeKind.Grew, change.Kind);
    }

    private static SnapshotDirectory Dir(string path, int depth, long bytes, long files)
        => new(path, depth, bytes, files);
}
