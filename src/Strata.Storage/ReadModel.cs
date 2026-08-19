using Strata.Core.Diffing;
using Strata.Core.Model;
using Strata.Core.Scanning;

namespace Strata.Storage;

/// <summary>A volume as recorded, with what the read side needs to describe its state.</summary>
public sealed record VolumeRow
{
    public required long Id { get; init; }

    public required string GuidPath { get; init; }

    public string? DriveLetter { get; init; }

    public string Label { get; init; } = "";

    public string FileSystem { get; init; } = "";

    public VolumeKind Kind { get; init; }

    public long TotalBytes { get; init; }

    public long FreeBytes { get; init; }

    public int? DiskNumber { get; init; }

    public string FirstSeenUtc { get; init; } = "";

    public string LastSeenUtc { get; init; } = "";

    /// <summary>When the volume last had a full baseline. Null means Tier 2 cannot run on it yet.</summary>
    public string? BaselineUtc { get; init; }

    /// <summary>Most recent snapshot of any tier, or null if none has been taken.</summary>
    public string? LastSnapshotUtc { get; init; }

    public long SnapshotCount { get; init; }

    /// <summary>
    /// Bytes Strata accounts for, from the maintained rollup rather than from the last snapshot.
    /// </summary>
    /// <remarks>
    /// This is expected to sit under what the OS reports free space against: unreadable directories,
    /// filesystem metadata, and hardlinks counted once all account for part of the gap. Measured at
    /// -29.6 GB on C: unelevated. Reporting both is how that difference stays visible rather than
    /// looking like a bug.
    /// </remarks>
    public long AccountedBytes { get; init; }

    public long UsedBytes => TotalBytes - FreeBytes;
}

/// <summary>One completed scan.</summary>
public sealed record SnapshotRow
{
    public required long Id { get; init; }

    public required long VolumeId { get; init; }

    public required string TakenUtc { get; init; }

    /// <summary>Which strategy produced this snapshot.</summary>
    public ScanTier Tier { get; init; }

    public long DurationMs { get; init; }

    public long TotalBytes { get; init; }

    public long TotalOnDisk { get; init; }

    public long FreeBytes { get; init; }

    public long FileCount { get; init; }

    public long DirCount { get; init; }

    public long InaccessibleCount { get; init; }

    public long HardLinkCount { get; init; }

    public long HardLinkBytes { get; init; }

    public long ReparseCount { get; init; }
}

/// <summary>
/// One directory in a tree walk.
/// </summary>
/// <remarks>
/// Carries parent links and a leaf name rather than a full path, which is the same trade the storage
/// model makes and for the same reason: the caller already knows the path it asked for, and every
/// other path in the response is that path plus the names on the way down. Sending a materialised
/// path per node would have cost roughly 90 MB of strings on a full walk of C:, to send the caller
/// something it can assemble from what it already has.
/// </remarks>
public readonly record struct TreeNode(
    long DirId,
    long ParentId,
    string Name,
    int Depth,
    long SubtreeBytes,
    long SubtreeOnDisk,
    long SubtreeFiles,
    long SubtreeDirs,
    bool HasChildren);

/// <summary>One row of the change log.</summary>
public sealed record EventRow
{
    public required long Id { get; init; }

    public required long VolumeId { get; init; }

    public string? DriveLetter { get; init; }

    public long SnapshotId { get; init; }

    public required string WhenUtc { get; init; }

    public required string Path { get; init; }

    public long DeltaBytes { get; init; }

    /// <summary>Bytes written and removed regardless of net effect. A build directory's real story.</summary>
    public long ChurnBytes { get; init; }

    public int FileDelta { get; init; }

    /// <summary>How the change is described to a human.</summary>
    public ChangeKind Kind { get; init; }
}
