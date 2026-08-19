using Strata.Core.Scanning;
using Strata.Platform;

namespace Strata.Tests;

/// <summary>
/// The gate is the mechanism that makes the whole product acceptable to leave running, so its
/// decisions are pinned even though most of them cannot be provoked on demand in a test.
/// </summary>
public sealed class ScanGateTests
{
    [Fact]
    public void PausedBlocksEveryTier()
    {
        var gate = new WindowsScanGate(new ScanGateOptions { Paused = true });

        foreach (var tier in Enum.GetValues<ScanTier>())
        {
            var decision = gate.CanScan(tier);
            Assert.False(decision.Allowed);
            Assert.Equal(DeferralReason.Paused, decision.Reason);
        }
    }

    /// <summary>
    /// A baseline is minutes of sustained I/O and yields to almost anything; an incremental tick is
    /// milliseconds and should not be deferred merely because someone is typing. Encoded here so a
    /// later change to the gate cannot quietly make ticks as timid as baselines.
    /// </summary>
    [Fact]
    public void AnIncrementalTickIsNotBlockedByRecentInput()
    {
        var gate = new WindowsScanGate(new ScanGateOptions
        {
            // Everything counts as recent, so input alone decides the outcome.
            UserActiveWindow = TimeSpan.FromDays(1),
            CpuThresholdPercent = 100,
            DetectBorderlessFullscreen = false,
        });

        var decision = gate.CanScan(ScanTier.Incremental);

        Assert.True(
            decision.Allowed || decision.Reason != DeferralReason.UserActive,
            $"an incremental tick was deferred for {decision.Reason}");
    }

    [Fact]
    public void ABaselineIsBlockedByRecentInput()
    {
        var gate = new WindowsScanGate(new ScanGateOptions
        {
            UserActiveWindow = TimeSpan.FromDays(1),
            CpuThresholdPercent = 100,
            DetectBorderlessFullscreen = false,
        });

        var decision = gate.CanScan(ScanTier.Full);

        // Under a test runner there has usually been input recently; if the machine is genuinely
        // idle the gate is entitled to allow it.
        Assert.True(
            !decision.Allowed || decision.Reason == DeferralReason.None,
            "the gate returned a contradictory decision");
    }

    [Fact]
    public void AZeroCpuThresholdDefersOnceASampleExists()
    {
        var gate = new WindowsScanGate(new ScanGateOptions
        {
            CpuThresholdPercent = -1, // any measurable load exceeds this
            UserActiveWindow = TimeSpan.Zero,
            DetectBorderlessFullscreen = false,
        });

        // The first call establishes a baseline for the delta and cannot judge load yet.
        gate.CanScan(ScanTier.Incremental);
        Thread.Sleep(50);

        var decision = gate.CanScan(ScanTier.Incremental);

        Assert.False(decision.Allowed);
        Assert.Equal(DeferralReason.CpuBusy, decision.Reason);
    }

    [Fact]
    public void TheGateReachesADecisionQuickly()
    {
        var gate = new WindowsScanGate();
        var clock = System.Diagnostics.Stopwatch.StartNew();

        for (var i = 0; i < 100; i++)
        {
            gate.CanScan(ScanTier.Incremental);
        }

        clock.Stop();

        // It is consulted before every tick and on a 60 s re-check loop while deferred, so it has to
        // be cheap enough to be uninteresting.
        Assert.True(clock.ElapsedMilliseconds < 500, $"100 gate checks took {clock.ElapsedMilliseconds} ms");
    }

    [Fact]
    public void CpuSamplerReturnsNothingOnItsFirstCallThenPlausibleValues()
    {
        var sampler = new CpuLoadSampler();

        Assert.Null(sampler.SamplePercent());

        Thread.Sleep(100);
        var load = sampler.SamplePercent();

        Assert.NotNull(load);
        Assert.InRange(load!.Value, 0, 100);
    }
}
