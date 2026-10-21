using Strata.Core.Scanning;

namespace Strata.Tests;

/// <summary>
/// When drift between the recorded total and the volume is enough to ask for a baseline.
/// </summary>
/// <remarks>
/// The figures are E: as it was found, rounded to the gigabyte: 1,447 GB recorded on disk against
/// 939 GB used, from a baseline on 10 Sep that recorded 594 GB on disk with 596 GB used.
/// </remarks>
public sealed class RollupDriftTests
{
    private const long GB = 1_000_000_000;
    private const long Capacity = 1_000 * GB;

    private static readonly DateTime Start = new(2026, 10, 21, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void TheDriftFoundOnEIsFarPastTheTolerance()
    {
        var over = RollupDrift.Overcount(1_447 * GB, 939 * GB, 594 * GB, 596 * GB);

        Assert.Equal(510 * GB, over);
        Assert.True(RollupDrift.NeedsBaseline(over, Capacity));
    }

    /// <summary>
    /// A volume whose recorded total sits well below used space, because a scan cannot see the MFT
    /// or shadow copies, is not drifting. Only movement since the baseline counts.
    /// </summary>
    [Fact]
    public void AGapThatWasAlreadyThereAtTheBaselineIsNotDrift()
    {
        var over = RollupDrift.Overcount(800 * GB, 900 * GB, 800 * GB, 900 * GB);

        Assert.Equal(0, over);
        Assert.False(RollupDrift.NeedsBaseline(over, Capacity));
    }

    [Fact]
    public void GrowthOnBothSidesTogetherIsNotDrift()
    {
        var over = RollupDrift.Overcount(950 * GB, 960 * GB, 850 * GB, 860 * GB);

        Assert.False(RollupDrift.NeedsBaseline(over, Capacity));
    }

    /// <summary>
    /// Used space outgrowing the recorded total is what a volume filling with unreadable things looks
    /// like. It is never acted on.
    /// </summary>
    [Fact]
    public void DriftDownwardIsNeverActedOn()
    {
        var over = RollupDrift.Overcount(850 * GB, 990 * GB, 850 * GB, 860 * GB);

        Assert.True(over < 0);
        Assert.False(RollupDrift.NeedsBaseline(over, Capacity));
    }

    [Theory]
    [InlineData(1_000L * GB, 10L * GB)]
    [InlineData(50L * GB, 1L << 30)] // 1% would be 500 MB; the floor holds
    public void TheToleranceIsOnePercentWithAFloor(long capacity, long tolerance)
    {
        Assert.Equal(tolerance, RollupDrift.Tolerance(capacity));
        Assert.False(RollupDrift.NeedsBaseline(tolerance, capacity));
        Assert.True(RollupDrift.NeedsBaseline(tolerance + 1, capacity));
    }

    [Fact]
    public void AVolumeNeverBaselinedAutomaticallyMayBe()
    {
        Assert.True(new ScanCadence().MayRebaseline("E:", Start, new ScanCadenceOptions()));
    }

    /// <summary>
    /// The cooldown is shared with the journal recovery: a baseline that did not cure the drift is
    /// not repeated within it, whichever reason started it.
    /// </summary>
    [Fact]
    public void ADriftBaselineWaitsOutTheSameCooldownAsAJournalRecovery()
    {
        var options = new ScanCadenceOptions();
        var cadence = new ScanCadence();

        cadence.Record("E:", TickReport.Complete, Start, options);
        cadence.Escalated("E:", Escalation.Baseline, Start);

        Assert.False(cadence.MayRebaseline("E:", Start + options.BaselineCooldown - TimeSpan.FromMinutes(1), options));
        Assert.True(cadence.MayRebaseline("E:", Start + options.BaselineCooldown, options));
    }
}
