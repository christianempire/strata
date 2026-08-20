namespace Strata.Core.Retention;

/// <summary>The resolution a measurement is kept at, once it is old enough to be thinned.</summary>
public enum RetentionResolution
{
    /// <summary>Every measurement, exactly as recorded. The window the product is judged on.</summary>
    Full = 0,

    Hourly = 1,

    Daily = 2,

    /// <summary>The floor. Nothing is ever thinned past one row per directory per week.</summary>
    Weekly = 3,
}

/// <summary>
/// How long each resolution is kept.
/// </summary>
/// <remarks>
/// The tiers from the architecture: every measurement for 7 days, hourly for 30, daily for a year,
/// weekly forever. Configurable because the right answer depends on how much history somebody
/// actually wants, and because a tier of zero is how you turn one off.
/// </remarks>
public sealed record RetentionTiers
{
    /// <summary>Days during which nothing is thinned at all.</summary>
    public int FullResolutionDays { get; init; } = 7;

    /// <summary>Days during which at most one measurement an hour is kept.</summary>
    public int HourlyDays { get; init; } = 30;

    /// <summary>Days during which at most one measurement a day is kept. Weekly after this.</summary>
    public int DailyDays { get; init; } = 365;

    /// <summary>
    /// Days of scan log kept.
    /// </summary>
    /// <remarks>
    /// Deleted outright rather than thinned, because the scan log is an audit trail rather than a
    /// series: no query asks "what was the log at time T", so no row is load-bearing for a later
    /// one. Its own migration puts it at 105k rows a year, which is worth bounding and not worth
    /// downsampling.
    /// </remarks>
    public int ScanLogDays { get; init; } = 90;

    /// <summary>Off means nothing is ever deleted, and the database grows without bound.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>
    /// Reasons this configuration would not work, empty when it is fine.
    /// </summary>
    /// <remarks>
    /// The ordering rule is the one that matters: tiers that overlap would put a row in two
    /// resolutions at once, and a full-resolution window longer than the hourly window would mean
    /// the hourly tier never applied to anything.
    /// </remarks>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();

        if (FullResolutionDays < 1)
        {
            errors.Add("fullResolutionDays must be at least 1.");
        }

        if (HourlyDays < FullResolutionDays)
        {
            errors.Add("hourlyDays must be at least fullResolutionDays.");
        }

        if (DailyDays < HourlyDays)
        {
            errors.Add("dailyDays must be at least hourlyDays.");
        }

        if (ScanLogDays < 1)
        {
            errors.Add("scanLogDays must be at least 1.");
        }

        return errors;
    }
}

/// <summary>
/// The instants the tiers begin at, resolved against a particular "now".
/// </summary>
/// <remarks>
/// Resolved once per run rather than per row. Everything downstream — including the SQL that does
/// the work — takes these three numbers, so the policy is decided in one place and the storage layer
/// only applies it.
/// </remarks>
public readonly record struct RetentionHorizons(long FullFromUnix, long HourlyFromUnix, long DailyFromUnix)
{
    /// <summary>Where the tiers fall for a given moment.</summary>
    public static RetentionHorizons From(DateTime nowUtc, RetentionTiers tiers)
    {
        ArgumentNullException.ThrowIfNull(tiers);

        return new RetentionHorizons(
            Unix(nowUtc.AddDays(-tiers.FullResolutionDays)),
            Unix(nowUtc.AddDays(-tiers.HourlyDays)),
            Unix(nowUtc.AddDays(-tiers.DailyDays)));
    }

    private static long Unix(DateTime utc)
        => (long)(DateTime.SpecifyKind(utc, DateTimeKind.Utc) - DateTime.UnixEpoch).TotalSeconds;
}

/// <summary>
/// Which measurements survive a downsample, and why.
/// </summary>
/// <remarks>
/// <b>Downsampling here is "keep the newest row per directory per bucket", never "delete rows older
/// than X".</b> The distinction is the whole reason this is a named policy rather than a DELETE.
/// <c>dir_history</c> stores absolute sizes sparsely, so "the size of X at time T" is the latest row
/// at or before T. Deleting a directory's last row before the horizon does not lose one measurement,
/// it makes every query in that entire period answer with a *newer* size — silently, and looking
/// exactly like a correct answer. See docs/STORAGE-MODEL.md.
/// <para>
/// Two invariants follow, and both are tested rather than asserted: a directory's newest row is
/// never deleted, and a bucket that had any rows still has exactly one.
/// </para>
/// </remarks>
public static class RetentionPolicy
{
    private const long Hour = 3600;
    private const long Day = 24 * Hour;
    private const long Week = 7 * Day;

    /// <summary>What resolution a measurement taken at this instant is kept at.</summary>
    public static RetentionResolution ResolutionFor(long takenUnix, RetentionHorizons horizons)
        => takenUnix >= horizons.FullFromUnix ? RetentionResolution.Full
            : takenUnix >= horizons.HourlyFromUnix ? RetentionResolution.Hourly
            : takenUnix >= horizons.DailyFromUnix ? RetentionResolution.Daily
            : RetentionResolution.Weekly;

    /// <summary>Seconds spanned by one bucket at this resolution. Zero for full resolution.</summary>
    public static long BucketSeconds(RetentionResolution resolution) => resolution switch
    {
        RetentionResolution.Hourly => Hour,
        RetentionResolution.Daily => Day,
        RetentionResolution.Weekly => Week,
        _ => 0,
    };

    /// <summary>
    /// The instant the bucket containing this measurement begins at.
    /// </summary>
    /// <remarks>
    /// Buckets are aligned to the Unix epoch rather than to the run, so the same measurement lands
    /// in the same bucket whenever retention happens to run. A bucket that moved with the clock
    /// would thin a different set of rows every night and eventually thin all of them.
    /// </remarks>
    public static long BucketStart(long takenUnix, RetentionResolution resolution)
    {
        var span = BucketSeconds(resolution);

        if (span == 0)
        {
            return takenUnix;
        }

        // Floor division, so timestamps before the epoch — the opening seed rows are dated 0, and
        // nothing should ever be dated below it — still land in a single bucket rather than
        // straddling one either side of zero.
        var floored = takenUnix >= 0 ? takenUnix / span : ((takenUnix + 1) / span) - 1;

        return floored * span;
    }

    /// <summary>
    /// The reference implementation: which of one directory's measurements survive.
    /// </summary>
    /// <remarks>
    /// Exists to be the thing the SQL is checked against. The storage layer does this in one
    /// statement over millions of rows, which is fast and unreadable; this is readable and slow, and
    /// a test that disagrees between them is a test that has found something.
    /// </remarks>
    public static IReadOnlyList<long> Survivors(IEnumerable<long> takenUnix, RetentionHorizons horizons)
    {
        ArgumentNullException.ThrowIfNull(takenUnix);

        // Newest per (resolution, bucket). Full resolution buckets each hold one measurement, so
        // everything inside the full-resolution window survives by construction rather than by
        // special case.
        var newest = new Dictionary<(RetentionResolution, long), long>();

        foreach (var taken in takenUnix)
        {
            var resolution = ResolutionFor(taken, horizons);
            var key = (resolution, BucketStart(taken, resolution));

            if (!newest.TryGetValue(key, out var held) || taken > held)
            {
                newest[key] = taken;
            }
        }

        var survivors = new List<long>(newest.Values);
        survivors.Sort();

        return survivors;
    }
}
