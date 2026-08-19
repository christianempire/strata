using System.Diagnostics;
using Strata.Core.Model;
using Strata.Core.Scanning;
using Strata.Platform;
using Strata.Storage;

namespace Strata.Host.Scanning;

/// <summary>What a full baseline scan produced.</summary>
internal sealed record BaselineOutcome(
    ScanResult Result,
    TimeSpan WriteTime,
    long RowsWritten,
    long SnapshotId);

/// <summary>
/// Runs a Tier 1 baseline and persists it.
/// </summary>
/// <remarks>
/// Extracted from the <c>scan</c> verb in Phase 3 so a scan requested through the API is the same
/// code path as a scan requested from the command line, rather than a second one that drifts.
/// Everything the CLI prints stays in the CLI; this returns numbers.
/// </remarks>
internal static class BaselineScan
{
    /// <summary>
    /// Workers used when none is configured.
    /// </summary>
    /// <remarks>
    /// The brief specified min(4, cores / 2). Measured on the target machine, four workers is well
    /// short of the knee: a warm scan of C: takes 19.3 s at two workers, 11.7 s at four, 8.9 s at
    /// eight and 7.7 s at sixteen, with nothing gained past that. Directory enumeration is
    /// latency-bound rather than bandwidth-bound, so it hides latency with concurrency.
    /// <para>
    /// Eight rather than sixteen because the last 15% costs double the concurrent I/O, and staying
    /// invisible matters more than finishing a rare scan a second sooner.
    /// </para>
    /// </remarks>
    internal static int DefaultWorkerCount { get; } =
        Math.Max(2, Math.Min(8, Environment.ProcessorCount / 4));

    /// <summary>
    /// Scans a volume, and writes it if a database is supplied.
    /// </summary>
    /// <param name="database">Null to scan and measure without persisting anything.</param>
    internal static BaselineOutcome Run(
        VolumeIdentity volume,
        StrataDatabase? database,
        ScanOptions scanOptions,
        SnapshotOptions snapshotOptions,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(volume);
        ArgumentNullException.ThrowIfNull(scanOptions);
        ArgumentNullException.ThrowIfNull(snapshotOptions);

        var scanner = new Tier1Scanner(new Win32DirectoryEnumerator(), scanOptions);

        if (database is null)
        {
            return new BaselineOutcome(
                scanner.Scan(volume, NullScanSink.Instance, cancellationToken),
                TimeSpan.Zero,
                RowsWritten: 0,
                SnapshotId: 0);
        }

        using var connection = database.Open();
        var volumeId = VolumeRepository.Upsert(connection, volume);

        using var sink = new SqliteFileSink(connection, volumeId);

        var result = scanner.Scan(volume, sink, cancellationToken);

        // Enumeration is finished but the writer may still be draining its queue. The time that
        // takes is reported separately, because it is storage cost rather than scan cost and
        // conflating them would make the scan look slower than it is.
        var writeClock = Stopwatch.StartNew();

        sink.Complete();

        var snapshotId = SnapshotWriter.Write(connection, volumeId, result, snapshotOptions);

        // Tier 2 refuses to run against a volume with no baseline, because an incremental delta
        // applied to nothing is meaningless. This is what unblocks it.
        IncrementalRepository.MarkBaseline(connection, volumeId, result.StartedUtc);

        writeClock.Stop();

        return new BaselineOutcome(result, writeClock.Elapsed, sink.RowsWritten, snapshotId);
    }

    /// <summary>Turns stored settings into scan options.</summary>
    internal static ScanOptions OptionsFrom(StrataSettings settings, Action<ScanProgress>? onProgress = null)
    {
        ArgumentNullException.ThrowIfNull(settings);

        return new ScanOptions
        {
            DegreeOfParallelism = settings.ScanWorkers ?? DefaultWorkerCount,
            UseBackgroundIoPriority = settings.BackgroundIo,
            ExcludedDirectoryNames = settings.ExcludedDirectoryNames,
            OnProgress = onProgress,
        };
    }

    internal static SnapshotOptions SnapshotOptionsFrom(StrataSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        return new SnapshotOptions
        {
            MaxDepth = settings.SnapshotMaxDepth,
            SizeFloorBytes = settings.SnapshotSizeFloorBytes,
        };
    }
}
