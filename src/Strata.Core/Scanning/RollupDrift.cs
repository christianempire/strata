namespace Strata.Core.Scanning;

/// <summary>
/// Whether a volume's maintained rollup has drifted far enough above the volume to need a baseline.
/// </summary>
/// <remarks>
/// The rollup is kept by deltas for weeks between baselines, so any delta that goes missing stays
/// missing. That is how E: came to report 1,446 GB on a disk with 939 GB used, and nothing noticed:
/// the number was wrong by half a terabyte and every tick reported success.
/// <para>
/// What is compared is the gap between size on disk as recorded and space used as the volume reports
/// it, against the same gap at the last baseline. The gap itself is never zero and need not be
/// small: used space includes what a scan cannot see (the MFT, shadow copies, directories it may not
/// open), and the recorded total includes nothing it should not. But a baseline measures both at
/// once, and nothing legitimate makes the recorded side outgrow the used side after it. Comparing
/// against the baseline's own gap is also what stops this from asking for a baseline every few hours
/// on a volume whose gap is simply large: a fresh baseline resets it to zero.
/// </para>
/// <para>
/// Only drift upward is acted on. Drift downward is indistinguishable from the volume legitimately
/// filling with things a scan cannot read.
/// </para>
/// </remarks>
public static class RollupDrift
{
    /// <summary>The smallest drift ever acted on, whatever the capacity.</summary>
    public const long MinimumToleranceBytes = 1L << 30;

    /// <summary>How far the recorded total has grown past used space since the last baseline.</summary>
    public static long Overcount(
        long recordedOnDisk,
        long usedBytes,
        long baselineRecordedOnDisk,
        long baselineUsedBytes)
        => (recordedOnDisk - usedBytes) - (baselineRecordedOnDisk - baselineUsedBytes);

    /// <summary>
    /// How much drift is tolerated before a baseline is asked for: 1% of the volume, at least 1 GiB.
    /// </summary>
    /// <remarks>
    /// 10 GB on the terabyte volumes of the target machine, against drift that measured 40 to 500 GB
    /// once it had been allowed to accumulate. Small enough to catch that within days, large enough
    /// that ordinary churn in what a scan cannot see never reaches it.
    /// </remarks>
    public static long Tolerance(long capacityBytes)
        => Math.Max(capacityBytes / 100, MinimumToleranceBytes);

    public static bool NeedsBaseline(long overcountBytes, long capacityBytes)
        => overcountBytes > Tolerance(capacityBytes);
}
