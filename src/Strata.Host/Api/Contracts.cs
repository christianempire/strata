using Strata.Core.Model;
using Strata.Core.Scanning;
using Strata.Storage;

namespace Strata.Host.Api;

/// <summary>What went wrong. The only shape the API wraps anything in.</summary>
internal sealed record ApiError(string Error);

/// <summary>
/// A volume, merging what the OS reports now with what Strata has recorded about it.
/// </summary>
/// <remarks>
/// Merged rather than served from either source alone. The stored row only exists once a volume has
/// been scanned, so a database-only answer is empty on first run and the Overview screen has nothing
/// to offer a scan of. The live enumeration has no history, so it cannot say whether Tier 2 is
/// running. Both halves are needed, and which volumes are attached right now can differ from which
/// ones have history.
/// </remarks>
internal sealed record VolumeResponse
{
    /// <summary>Row id, or null for a volume Strata has never recorded.</summary>
    public long? Id { get; init; }

    public required string GuidPath { get; init; }

    public string? DriveLetter { get; init; }

    public string Label { get; init; } = "";

    public string FileSystem { get; init; } = "";

    public VolumeKind Kind { get; init; }

    public int? DiskNumber { get; init; }

    public long TotalBytes { get; init; }

    public long FreeBytes { get; init; }

    public long UsedBytes { get; init; }

    /// <summary>Bytes Strata accounts for, from the maintained rollup.</summary>
    /// <remarks>
    /// Expected to sit below <see cref="UsedBytes"/>. Unreadable directories, filesystem metadata
    /// and hardlinks counted once all account for part of the gap — measured at −29.6 GB on C:
    /// unelevated. Both are reported so the difference is visible rather than looking like a bug.
    /// </remarks>
    public long AccountedBytes { get; init; }

    /// <summary>Whether the volume is present on the machine right now.</summary>
    public bool Attached { get; init; }

    /// <summary>Whether Strata would scan this volume unprompted.</summary>
    public bool Scannable { get; init; }

    /// <summary>NTFS, so the change journal is a candidate and Tier 2 can run.</summary>
    public bool SupportsUsnJournal { get; init; }

    /// <summary>Null means no baseline, and Tier 2 cannot run until there is one.</summary>
    public string? BaselineUtc { get; init; }

    public string? LastSnapshotUtc { get; init; }

    public long SnapshotCount { get; init; }
}

/// <summary>
/// The audit log, with the totals for the same window.
/// </summary>
/// <remarks>
/// The one endpoint that wraps its list, against the rule the rest of the API follows. The totals
/// cannot be derived from the rows — they cover the whole window, not the returned page, and they
/// count outcomes the caller may have filtered out — so returning them separately would mean two
/// requests that can disagree with each other about the same moment.
/// </remarks>
internal sealed record ScanLogResponse
{
    public required ScanLogSummary Summary { get; init; }

    public required IReadOnlyList<ScanLogRow> Ticks { get; init; }
}

/// <summary>A scan asked for through the API.</summary>
internal sealed record ScanRequestBody
{
    /// <summary>Drive letter, GUID path, or row id. Required.</summary>
    public string? Volume { get; init; }

    /// <summary>
    /// <c>full</c> for a Tier 1 baseline, <c>incremental</c> for a journal tick. Defaults to
    /// incremental, which is the cheap one.
    /// </summary>
    public string? Tier { get; init; }
}

/// <summary>Confirmation that a scan was queued. It has not run yet.</summary>
internal sealed record ScanAccepted
{
    public required long Id { get; init; }

    public required string Volume { get; init; }

    public required ScanTier Tier { get; init; }

    /// <summary>
    /// Roughly how long this will take, so a caller knows whether to wait or to walk away.
    /// </summary>
    /// <remarks>
    /// A cold baseline of a system volume measured 211 s on the target machine, and cold is the case
    /// a baseline runs in. Saying "queued" without saying that invites someone to sit and watch.
    /// </remarks>
    public required string Expect { get; init; }
}

/// <summary>
/// A settings update. Every field is optional and absent means unchanged.
/// </summary>
/// <remarks>
/// Not a strict PUT replacement, on purpose. A replacement would mean a client that omits a field it
/// does not know about silently resets it to a default, and this record gains fields every phase.
/// The cost is that a field cannot be cleared by omitting it, which for a list means sending an
/// empty array rather than leaving it out.
/// </remarks>
internal sealed record SettingsPatch
{
    public int? IntervalSeconds { get; init; }

    public double? CpuThresholdPercent { get; init; }

    public int? MaxRecordsPerTick { get; init; }

    public long? EventFloorBytes { get; init; }

    public int? SnapshotMaxDepth { get; init; }

    public long? SnapshotSizeFloorBytes { get; init; }

    public long? HistoryBandBytes { get; init; }

    public int? ScanWorkers { get; init; }

    public bool? BackgroundIo { get; init; }

    public bool? DetectBorderlessFullscreen { get; init; }

    public bool? IncludeStrataDatabase { get; init; }

    public string[]? ExcludedEventPaths { get; init; }

    public string[]? ExcludedDirectoryNames { get; init; }

    public bool? Paused { get; init; }

    internal StrataSettings Apply(StrataSettings current)
    {
        ArgumentNullException.ThrowIfNull(current);

        return current with
        {
            IntervalSeconds = IntervalSeconds ?? current.IntervalSeconds,
            CpuThresholdPercent = CpuThresholdPercent ?? current.CpuThresholdPercent,
            MaxRecordsPerTick = MaxRecordsPerTick ?? current.MaxRecordsPerTick,
            EventFloorBytes = EventFloorBytes ?? current.EventFloorBytes,
            SnapshotMaxDepth = SnapshotMaxDepth ?? current.SnapshotMaxDepth,
            SnapshotSizeFloorBytes = SnapshotSizeFloorBytes ?? current.SnapshotSizeFloorBytes,
            HistoryBandBytes = HistoryBandBytes ?? current.HistoryBandBytes,
            ScanWorkers = ScanWorkers ?? current.ScanWorkers,
            BackgroundIo = BackgroundIo ?? current.BackgroundIo,
            DetectBorderlessFullscreen = DetectBorderlessFullscreen ?? current.DetectBorderlessFullscreen,
            IncludeStrataDatabase = IncludeStrataDatabase ?? current.IncludeStrataDatabase,
            ExcludedEventPaths = ExcludedEventPaths ?? current.ExcludedEventPaths,
            ExcludedDirectoryNames = ExcludedDirectoryNames ?? current.ExcludedDirectoryNames,
            Paused = Paused ?? current.Paused,
        };
    }
}
