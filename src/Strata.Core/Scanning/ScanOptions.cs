namespace Strata.Core.Scanning;

/// <summary>Tunables for a full baseline scan.</summary>
public sealed record ScanOptions
{
    /// <summary>
    /// Worker threads enumerating directories concurrently.
    /// <para>
    /// The brief specifies min(4, ProcessorCount / 2). Kept as the default but made configurable,
    /// because directory enumeration on NVMe is latency-bound rather than bandwidth-bound and may
    /// scale well past four. Phase 1 reports throughput so this can be tuned against a measurement
    /// instead of an argument. See docs/DESIGN-REVIEW.md section C3.
    /// </para>
    /// </summary>
    public int DegreeOfParallelism { get; init; } = Math.Max(1, Math.Min(4, Environment.ProcessorCount / 2));

    /// <summary>
    /// Deduplicate hardlinks so shared extents are counted once. Costs a hash set holding every file
    /// ID on the volume. Disable to measure what that costs.
    /// </summary>
    public bool DeduplicateHardLinks { get; init; } = true;

    /// <summary>
    /// Run scan threads at very low I/O priority. On by default because it is the whole point;
    /// disable only to measure the difference it makes.
    /// </summary>
    public bool UseBackgroundIoPriority { get; init; } = true;

    /// <summary>Files buffered per worker before being handed to the sink.</summary>
    public int WriteBatchSize { get; init; } = 4096;

    /// <summary>
    /// Directory names skipped entirely, matched case-insensitively against the leaf name at any
    /// depth. Empty by default: Phase 1 is deliberately measuring the unfiltered cost.
    /// </summary>
    public IReadOnlyList<string> ExcludedDirectoryNames { get; init; } = [];

    /// <summary>Invoked periodically with progress. Called from scan threads, so must be cheap and thread-safe.</summary>
    public Action<ScanProgress>? OnProgress { get; init; }

    /// <summary>How often progress is reported, counted in directories completed.</summary>
    public int ProgressIntervalDirectories { get; init; } = 2000;
}

/// <summary>A progress sample taken during a scan.</summary>
public readonly record struct ScanProgress(
    long DirectoriesCompleted,
    long FilesSeen,
    long BytesSeen,
    string CurrentPath);
