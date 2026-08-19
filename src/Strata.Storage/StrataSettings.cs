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
    /// The journal is a durable queue, so falling behind is safe and self-healing. Draining a
    /// several-hundred-thousand record backlog in one go is the stall the whole design exists to
    /// prevent. Measured: 50,000 records is about a 1 s tick.
    /// </remarks>
    public int MaxRecordsPerTick { get; init; } = 50_000;

    /// <summary>Smallest change worth a row in the events feed.</summary>
    public long EventFloorBytes { get; init; } = 1024 * 1024;

    /// <summary>Depth cap for the historical directory rollup.</summary>
    public int SnapshotMaxDepth { get; init; } = 8;

    /// <summary>Size floor for the historical directory rollup.</summary>
    public long SnapshotSizeFloorBytes { get; init; } = 10L * 1024 * 1024;

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

        if (EventFloorBytes < 0)
        {
            errors.Add("eventFloorBytes cannot be negative.");
        }

        if (SnapshotMaxDepth is < 1 or > 32)
        {
            errors.Add("snapshotMaxDepth must be between 1 and 32.");
        }

        if (SnapshotSizeFloorBytes < 0)
        {
            errors.Add("snapshotSizeFloorBytes cannot be negative.");
        }

        if (ScanWorkers is < 1 or > 64)
        {
            errors.Add("scanWorkers must be between 1 and 64.");
        }

        return errors;
    }
}
