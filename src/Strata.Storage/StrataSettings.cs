using Strata.Core.Retention;

namespace Strata.Storage;

/// <summary>
/// Everything the resident scanner is configured by.
/// </summary>
/// <remarks>
/// Through Phases 1 and 2 these were command line flags, which suits a console verb and does not
/// suit a process that starts at logon and is configured from a browser tab. The defaults here are
/// the defaults those flags had, and several of them were chosen from measurement rather than taste
/// — see docs/MEASUREMENTS.md before changing one.
/// </remarks>
public sealed record StrataSettings
{
    /// <summary>Seconds between incremental ticks.</summary>
    public int IntervalSeconds { get; init; } = 900;

    /// <summary>Defer when system-wide CPU is above this percentage.</summary>
    public double CpuThresholdPercent { get; init; } = 70;

    /// <summary>
    /// Journal records processed in one tick before the cursor is persisted and the rest left for
    /// the next one.
    /// </summary>
    /// <remarks>
    /// Draining a several-hundred-thousand record backlog in one go is the stall the whole design
    /// exists to prevent. Measured: 50,000 records is about a 1 s tick.
    /// <para>
    /// This used to say the journal is a durable queue, so falling behind is safe and self-healing.
    /// It is durable but <b>finite</b>, and real use disproved the rest of that sentence. C:'s
    /// journal is a 32 MiB circular buffer; a cap of 50,000 records every 900 s drains it at 55
    /// records a second, which is less than a development machine generates. The scanner lost ground
    /// every tick until Windows trimmed the journal past its cursor, and then there was nothing to
    /// resume from. See the incident in docs/MEASUREMENTS.md.
    /// </para>
    /// <para>
    /// Two things now stop that: a tick that hits this cap comes back at
    /// <see cref="CatchUpIntervalSeconds"/> instead of the full interval, so catching up can
    /// actually win; and a journal that has been trimmed past us escalates to an automatic baseline
    /// rather than deferring forever.
    /// </para>
    /// </remarks>
    public int MaxRecordsPerTick { get; init; } = 50_000;

    /// <summary>
    /// Seconds between ticks on a volume whose last tick hit <see cref="MaxRecordsPerTick"/>.
    /// </summary>
    /// <remarks>
    /// Only this volume is accelerated, and only while it is behind. A quiet volume keeps its
    /// ordinary interval, because ticking it fifteen times as often would multiply its snapshot rows
    /// by fifteen to learn nothing.
    /// </remarks>
    public int CatchUpIntervalSeconds { get; init; } = 60;

    /// <summary>Smallest change worth a row in the events feed.</summary>
    public long EventFloorBytes { get; init; } = 1024 * 1024;

    /// <summary>
    /// How far a directory's total may drift before history records it again.
    /// </summary>
    /// <remarks>
    /// Zero records every change at any depth and any size, which is what makes growth in a deep
    /// folder traceable. Measured on the target machine that costs 18.2 MiB a week on C:, against
    /// 428.6 MiB for the per-snapshot rollup it replaces, so there is nothing to buy by raising it
    /// and accuracy to lose. Raising it to 1 MiB would cost 4.0 MiB a week if that ever matters.
    /// </remarks>
    public long HistoryBandBytes { get; init; }

    /// <summary>Enumeration workers for a baseline scan. Null means the measured default.</summary>
    public int? ScanWorkers { get; init; }

    /// <summary>Run scan threads at very low I/O priority. Off only to measure what it costs.</summary>
    public bool BackgroundIo { get; init; } = true;

    /// <summary>Supplement the notification-state check with a foreground-window-covers-monitor test.</summary>
    public bool DetectBorderlessFullscreen { get; init; } = true;

    /// <summary>Stop excluding Strata's own data directory from the change feed.</summary>
    public bool IncludeStrataDatabase { get; init; }

    /// <summary>Paths kept out of the events feed, but not out of totals or the treemap.</summary>
    public IReadOnlyList<string> ExcludedEventPaths { get; init; } = [];

    /// <summary>Directory names a baseline scan skips entirely, matched on the leaf at any depth.</summary>
    public IReadOnlyList<string> ExcludedDirectoryNames { get; init; } = [];

    /// <summary>Suspends scanning without stopping the process. The tray's Pause.</summary>
    public bool Paused { get; init; }

    // --- Retention. Flat rather than a nested record, because the settings table is key/value and
    // a nested object would have to be serialised into one cell, which is neither greppable nor
    // editable in the SQLite browser this table exists to be readable in.

    /// <summary>Off means nothing is ever deleted and the database grows without bound.</summary>
    public bool RetentionEnabled { get; init; } = true;

    /// <summary>Days during which every measurement is kept exactly as recorded.</summary>
    public int FullResolutionDays { get; init; } = 7;

    /// <summary>Days during which at most one measurement an hour is kept.</summary>
    public int HourlyDays { get; init; } = 30;

    /// <summary>Days during which at most one measurement a day is kept. Weekly beyond it, forever.</summary>
    public int DailyDays { get; init; } = 365;

    /// <summary>Days of scan log kept. Dropped by age rather than thinned; it is not a series.</summary>
    public int ScanLogDays { get; init; } = 90;

    /// <summary>The retention tiers, in the shape the policy takes them.</summary>
    public RetentionTiers Retention => new()
    {
        Enabled = RetentionEnabled,
        FullResolutionDays = FullResolutionDays,
        HourlyDays = HourlyDays,
        DailyDays = DailyDays,
        ScanLogDays = ScanLogDays,
    };

    /// <summary>
    /// Reasons this configuration would not work, empty when it is fine.
    /// </summary>
    /// <remarks>
    /// Rejecting bad input matters more here than it looks: an interval of zero would turn the
    /// background scanner into a busy loop against the disk, which is precisely the failure the
    /// project treats as fatal rather than cosmetic.
    /// </remarks>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();

        if (IntervalSeconds < 30)
        {
            errors.Add("intervalSeconds must be at least 30.");
        }

        if (CpuThresholdPercent is <= 0 or > 100)
        {
            errors.Add("cpuThresholdPercent must be above 0 and at most 100.");
        }

        if (MaxRecordsPerTick < 1000)
        {
            errors.Add("maxRecordsPerTick must be at least 1000.");
        }

        // The floor stops the accelerated path becoming a busy loop against the disk; the ceiling
        // stops "catching up" being slower than not catching up.
        if (CatchUpIntervalSeconds is < 15 || CatchUpIntervalSeconds > IntervalSeconds)
        {
            errors.Add("catchUpIntervalSeconds must be at least 15 and no more than intervalSeconds.");
        }

        if (EventFloorBytes < 0)
        {
            errors.Add("eventFloorBytes cannot be negative.");
        }

        if (HistoryBandBytes < 0)
        {
            errors.Add("historyBandBytes cannot be negative.");
        }

        if (ScanWorkers is < 1 or > 64)
        {
            errors.Add("scanWorkers must be between 1 and 64.");
        }

        // Delegated rather than restated. The ordering rule between the tiers is the policy's to
        // own, and two copies of it would drift.
        errors.AddRange(Retention.Validate());

        return errors;
    }
}
