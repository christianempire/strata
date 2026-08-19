using Strata.Core.Scanning;
using Strata.Host.Scanning;

namespace Strata.Tests;

/// <summary>
/// The arithmetic behind a running scan's progress display.
/// </summary>
/// <remarks>
/// Tested because the first-run state is the one the whole product is judged on before anything else
/// works, and because every failure mode here is a lie rather than a crash: a percentage against a
/// denominator that does not exist, a time remaining that grows, a bar that sticks at 62% because
/// the scan that owned it threw.
/// </remarks>
public sealed class ScanActivityTests
{
    private const long GB = 1024L * 1024 * 1024;

    [Fact]
    public void AFirstRunHasNoEntryDenominatorAndFallsBackToBytes()
    {
        var activity = new ScanActivity("C:", ScanTier.Full, expectedEntries: null, expectedBytes: 800 * GB);

        activity.Record(new ScanProgress(1000, 500_000, 400 * GB, @"\\?\C:\Users"));

        var snapshot = activity.Snapshot();

        Assert.Equal(ProgressBasis.Bytes, snapshot.Basis);
        Assert.Equal(50.0, snapshot.Percent);
        Assert.Equal(800 * GB, snapshot.Expected);
    }

    [Fact]
    public void ARescanCountsEntriesAgainstTheLastScan()
    {
        var activity = new ScanActivity("C:", ScanTier.Full, expectedEntries: 3_053_173, expectedBytes: 800 * GB);

        // Directories and files together, because that is what the last scan's denominator counts.
        activity.Record(new ScanProgress(400_000, 1_126_586, 300 * GB, @"\\?\C:\WINDOWS"));

        var snapshot = activity.Snapshot();

        Assert.Equal(ProgressBasis.Entries, snapshot.Basis);
        Assert.Equal(50.0, snapshot.Percent);
        Assert.Equal(3_053_173, snapshot.Expected);
    }

    /// <summary>
    /// A scan that overruns its denominator reports 100%, never 118%.
    /// </summary>
    /// <remarks>
    /// Guaranteed to happen: the denominator is the previous scan, and a volume that grew since then
    /// has more entries than it did. Overshooting is normal, and a bar past its own end is the
    /// clearest possible signal that a number is not trustworthy.
    /// </remarks>
    [Fact]
    public void ProgressIsClampedAtWhole()
    {
        var activity = new ScanActivity("C:", ScanTier.Full, expectedEntries: 1_000_000, expectedBytes: 800 * GB);

        activity.Record(new ScanProgress(200_000, 1_000_000, 10 * GB, @"\\?\C:\src"));

        Assert.Equal(100.0, activity.Snapshot().Percent);
    }

    [Fact]
    public void NoDenominatorMeansNoPercentageRatherThanZero()
    {
        var activity = new ScanActivity("R:", ScanTier.Full, expectedEntries: null, expectedBytes: 0);

        activity.Record(new ScanProgress(10, 100, 4096, @"\\?\R:\"));

        var snapshot = activity.Snapshot();

        Assert.Equal(ProgressBasis.None, snapshot.Basis);
        Assert.Null(snapshot.Percent);
        Assert.Null(snapshot.RemainingMs);

        // The raw counts are still real, and are what the display falls back to.
        Assert.Equal(100, snapshot.FilesSeen);
        Assert.Equal(10, snapshot.DirectoriesCompleted);
    }

    /// <summary>
    /// Nothing is claimed about time remaining until enough of the scan is behind it.
    /// </summary>
    /// <remarks>
    /// Extrapolating from 0.2% of a 211-second scan gives a number in the hours that then collapses.
    /// A first impression of "this will take four hours" is not recovered by being right thirty
    /// seconds later, so below 2% the field is null and the UI shows elapsed instead.
    /// </remarks>
    [Fact]
    public void TimeRemainingIsWithheldUntilTheSampleIsWorthDividingBy()
    {
        var activity = new ScanActivity("C:", ScanTier.Full, expectedEntries: 3_000_000, expectedBytes: 800 * GB);

        activity.Record(new ScanProgress(1_000, 2_000, GB, @"\\?\C:\Users"));

        Assert.Null(activity.Snapshot().RemainingMs);
    }

    [Fact]
    public void AFinishedScanHasNoTimeRemaining()
    {
        var activity = new ScanActivity("C:", ScanTier.Full, expectedEntries: 1_000, expectedBytes: 800 * GB);

        activity.Record(new ScanProgress(0, 1_000, GB, @"\\?\C:\Users"));

        var snapshot = activity.Snapshot();

        Assert.Equal(100.0, snapshot.Percent);
        Assert.Null(snapshot.RemainingMs);
    }

    /// <summary>
    /// Depth is counted from the path the scan is actually holding.
    /// </summary>
    /// <remarks>
    /// Which is the extended-length form, <c>\\?\C:\...</c>. Counting its separators naively would
    /// report every path three levels deeper than it is, and "depth 14 of 27" reads as plausible
    /// enough that nobody would catch it.
    /// </remarks>
    [Theory]
    [InlineData(@"\\?\C:\", 0)]
    [InlineData(@"\\?\C:\Users", 1)]
    [InlineData(@"\\?\C:\Users\chris\Documents", 3)]
    [InlineData(@"C:\Users\chris", 2)]
    [InlineData("", 0)]
    public void DepthIsCountedFromTheExtendedLengthPath(string path, int expected)
    {
        var activity = new ScanActivity("C:", ScanTier.Full, null, 800 * GB);

        activity.Record(new ScanProgress(1, 1, 1, path));

        Assert.Equal(expected, activity.Snapshot().CurrentDepth);
    }

    /// <summary>
    /// An incremental tick reads; it does not prepare or write.
    /// </summary>
    /// <remarks>
    /// The phase split exists for a baseline, which spends its first seconds deleting the previous
    /// scan of the volume. A Tier 2 tick has no such split, so defaulting it into the preparing
    /// phase would both mislabel it and suppress figures that are perfectly real.
    /// </remarks>
    [Fact]
    public void AnIncrementalTickIsInTheReadingPhaseFromTheStart()
    {
        var activity = new ScanActivity("C:", ScanTier.Incremental, 1_000, 800 * GB);

        activity.Record(new ScanProgress(100, 400, GB, @"\\?\C:\Users"));

        var snapshot = activity.Snapshot();

        Assert.Equal(ScanPhase.Reading, snapshot.Phase);
        Assert.Equal(ProgressBasis.Entries, snapshot.Basis);
        Assert.Equal(50.0, snapshot.Percent);
    }

    /// <summary>
    /// While a baseline is clearing the previous scan there is no percentage, and none is invented.
    /// </summary>
    /// <remarks>
    /// Measured: <c>DELETE FROM files WHERE volume_id = ?</c> on C: is 3.1 million rows and twelve
    /// to nineteen seconds, during which no directory has been enumerated. Reporting that as 0% is
    /// what made a working scan look hung for the first sixth of its life.
    /// </remarks>
    [Fact]
    public void APreparingBaselineReportsNoProgressRatherThanZero()
    {
        var activity = new ScanActivity(
            "C:", ScanTier.Full, 3_000_000, 800 * GB, ScanPhase.Preparing);

        var snapshot = activity.Snapshot();

        Assert.Equal(ScanPhase.Preparing, snapshot.Phase);
        Assert.Null(snapshot.Percent);
        Assert.Null(snapshot.RemainingMs);
        Assert.Equal(ProgressBasis.None, snapshot.Basis);

        // And once the walk starts, the same activity reports normally.
        activity.SetPhase(ScanPhase.Reading);
        activity.Record(new ScanProgress(500_000, 1_000_000, 100 * GB, @"\\?\C:\Users"));

        var reading = activity.Snapshot();

        Assert.Equal(ScanPhase.Reading, reading.Phase);
        Assert.Equal(50.0, reading.Percent);
    }

    [Fact]
    public void AWritingBaselineAlsoWithholdsAPercentage()
    {
        var activity = new ScanActivity("C:", ScanTier.Full, 1_000, 800 * GB);

        activity.Record(new ScanProgress(0, 1_000, GB, @"\\?\C:\Users"));
        activity.SetPhase(ScanPhase.Writing);

        var snapshot = activity.Snapshot();

        Assert.Equal(ScanPhase.Writing, snapshot.Phase);
        Assert.Null(snapshot.Percent);

        // The raw counts are still what the walk found; it is the fraction that stops meaning
        // anything once the walk is over.
        Assert.Equal(1_000, snapshot.FilesSeen);
    }

    [Fact]
    public void TheLatestSampleReplacesTheOneBeforeIt()
    {
        var activity = new ScanActivity("C:", ScanTier.Full, null, 800 * GB);

        activity.Record(new ScanProgress(1_000, 40_000, GB, @"\\?\C:\Users"));
        activity.Record(new ScanProgress(2_000, 90_000, 2 * GB, @"\\?\C:\WINDOWS\WinSxS"));

        var snapshot = activity.Snapshot();

        Assert.Equal(2_000, snapshot.DirectoriesCompleted);
        Assert.Equal(90_000, snapshot.FilesSeen);
        Assert.Equal(@"\\?\C:\WINDOWS\WinSxS", snapshot.CurrentPath);
    }
}
