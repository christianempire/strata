using System.Diagnostics;
using System.Globalization;
using Strata.Core.Formatting;
using Strata.Core.Model;
using Strata.Core.Rollup;
using Strata.Core.Scanning;
using Strata.Host.Scanning;
using Strata.Platform;
using Strata.Storage;

namespace Strata.Host;

/// <summary>
/// <c>strata scan</c> — runs a full baseline scan and reports what it cost.
/// </summary>
/// <remarks>
/// The reporting is the point of this command, not a nicety. Phase 1 exists to establish how long a
/// real scan takes, how large the resulting database is, and how many directories clear the size
/// floor, before committing to the rest of the design. Every number printed here answers a question
/// in docs/DESIGN-REVIEW.md.
/// </remarks>
internal static class ScanCommand
{
    internal static int Run(CliOptions options)
    {
        var volumes = SelectVolumes(options);

        if (volumes.Count == 0)
        {
            Console.Error.WriteLine("strata: no matching volumes. Try 'strata volumes'.");
            return 1;
        }

        StrataDatabase? database = null;

        if (options.Persist)
        {
            database = new StrataDatabase(options.DatabasePath);
            database.Migrate();
            Console.WriteLine($"Database: {database.DatabasePath}");
        }
        else
        {
            Console.WriteLine("Database: (not writing; --no-store)");
        }

        Console.WriteLine($"Elevated: {(IsElevated() ? "yes" : "no")}");
        Console.WriteLine();

        var results = new List<ScanReport>();

        foreach (var volume in volumes)
        {
            results.Add(ScanOne(volume, options, database));
        }

        if (results.Count > 1)
        {
            PrintCombinedSummary(results);
        }

        if (database is not null)
        {
            // A baseline rewrites every row for the volume, so the file is left full of free pages.
            // Reclaiming them is only worth doing after a full scan, never on the Tier 2 path.
            if (!options.SkipCompact)
            {
                var compactClock = Stopwatch.StartNew();
                database.Compact();
                compactClock.Stop();
                Console.WriteLine($"Compacted in {compactClock.Elapsed.TotalSeconds:F2} s");
                Console.WriteLine();
            }

            PrintDatabaseFootprint(database);
        }

        return 0;
    }

    private static ScanReport ScanOne(VolumeIdentity volume, CliOptions options, StrataDatabase? database)
    {
        Console.WriteLine($"=== {volume.Display}  [{volume.FileSystem}, {ByteSize.Format(volume.TotalBytes)}] ===");

        var scanOptions = new ScanOptions
        {
            DegreeOfParallelism = options.Threads ?? BaselineScan.DefaultWorkerCount,
            DeduplicateHardLinks = options.DeduplicateHardLinks,
            UseBackgroundIoPriority = options.BackgroundIo,
            ExcludedDirectoryNames = options.Excluded,
            OnProgress = options.Quiet ? null : ReportProgress,
        };

        Console.WriteLine(
            $"Workers: {scanOptions.DegreeOfParallelism}   " +
            $"Background I/O: {(scanOptions.UseBackgroundIoPriority ? "on" : "off")}   " +
            $"Hardlink dedup: {(scanOptions.DeduplicateHardLinks ? "on" : "off")}");
        Console.WriteLine();

        var outcome = BaselineScan.Run(
            volume,
            database,
            scanOptions,
            new SnapshotOptions
            {
                MaxDepth = options.MaxDepth,
                SizeFloorBytes = options.SizeFloorBytes,
            },
            CancellationToken.None);

        ClearProgressLine();
        PrintResult(outcome, options);

        return new ScanReport(outcome.Result, outcome.WriteTime);
    }

    private static void PrintResult(BaselineOutcome outcome, CliOptions options)
    {
        var result = outcome.Result;
        var writeTime = outcome.WriteTime;
        var seconds = result.Duration.TotalSeconds;

        Console.WriteLine($"  Scan duration      {seconds,10:F2} s");

        if (writeTime > TimeSpan.Zero)
        {
            Console.WriteLine($"  Storage flush      {writeTime.TotalSeconds,10:F2} s");
        }

        Console.WriteLine($"  Files            {result.FileCount,12:N0}");
        Console.WriteLine($"  Directories      {result.DirectoryCount,12:N0}");
        Console.WriteLine($"  Throughput       {result.FilesPerSecond,12:N0} files/s");
        Console.WriteLine();
        Console.WriteLine($"  Logical size     {ByteSize.Format(result.TotalBytes),12}");
        Console.WriteLine($"  Size on disk     {ByteSize.Format(result.TotalSizeOnDisk),12}");
        Console.WriteLine($"  Volume used      {ByteSize.Format(result.Volume.UsedBytes),12}  (as reported by the OS)");
        Console.WriteLine($"  Difference       {ByteSize.FormatDelta(result.TotalSizeOnDisk - result.Volume.UsedBytes),12}");
        Console.WriteLine();
        Console.WriteLine($"  Max depth        {result.Rollup.MaxDepth,12:N0}");
        Console.WriteLine($"  Hardlinks        {result.HardLinkCount,12:N0}  " +
                          $"({ByteSize.Format(result.HardLinkBytesSaved)} counted once)");
        Console.WriteLine($"  Reparse points   {result.ReparsePointCount,12:N0}  (recorded, not traversed)");

        if (result.Rollup.OrphanCount > 0)
        {
            Console.WriteLine($"  Orphaned dirs    {result.Rollup.OrphanCount,12:N0}  (parent vanished mid-scan)");
        }

        if (result.InaccessibleTotal > 0)
        {
            Console.WriteLine($"  Unreadable dirs  {result.InaccessibleTotal,12:N0}  " +
                              $"({DescribeInaccessible(result)})");
        }

        if (outcome.RowsWritten > 0)
        {
            Console.WriteLine();
            Console.WriteLine(
                $"  Rows written     {outcome.RowsWritten,12:N0}   snapshot #{outcome.SnapshotId}");
        }

        Console.WriteLine();

        PrintTopDirectories(result, options.TopDirectories);
        PrintSnapshotDirEstimate(result, options);

        if (options.ShowHistogram)
        {
            PrintHistogram(result);
        }
    }

    /// <summary>Where the bytes actually are, which is the first thing anyone wants to know.</summary>
    private static void PrintTopDirectories(ScanResult result, int count)
    {
        if (count <= 0)
        {
            return;
        }

        var prefix = result.Volume.DriveLetter ?? result.Volume.GuidPath;

        var top = result.Rollup.Nodes.Values
            .Where(n => n.Depth is > 0 and <= 3)
            .OrderByDescending(n => n.SubtreeBytes)
            .Take(count)
            .ToList();

        if (top.Count == 0)
        {
            return;
        }

        Console.WriteLine($"  Largest directories (to depth 3)");

        foreach (var node in top)
        {
            var path = PathBuilder.BuildPath(result.Rollup.Nodes, node.Id, prefix);
            var share = result.TotalBytes > 0 ? (double)node.SubtreeBytes / result.TotalBytes * 100 : 0;

            Console.WriteLine(
                $"    {ByteSize.Format(node.SubtreeBytes),10}  {share,5:F1}%  " +
                $"{node.SubtreeFileCount,10:N0} files  {path}");
        }

        Console.WriteLine();
    }

    /// <summary>
    /// The number the storage design turns on: how many directories a single snapshot would record,
    /// and therefore what the database costs per week.
    /// </summary>
    /// <remarks>
    /// Retention keeps every snapshot for seven days. At a 15-minute cadence that is 672 snapshots,
    /// so the per-snapshot row count multiplies by nearly seven hundred. See
    /// docs/DESIGN-REVIEW.md section A5.
    /// </remarks>
    private static void PrintSnapshotDirEstimate(ScanResult result, CliOptions options)
    {
        var qualifying = result.Rollup.Nodes.Values.Count(
            n => n.Depth <= options.MaxDepth && n.SubtreeBytes >= options.SizeFloorBytes);

        const int snapshotsPerWeekAt15Min = 96 * 7;
        var bytesPerSnapshot = (long)qualifying * HistogramCell.EstimatedBytesPerRow;

        Console.WriteLine(
            $"  snapshot_dirs at depth<={options.MaxDepth}, floor>={ByteSize.Format(options.SizeFloorBytes)}: " +
            $"{qualifying:N0} directories");
        Console.WriteLine(
            $"    -> ~{ByteSize.Format(bytesPerSnapshot)} per snapshot, " +
            $"~{ByteSize.Format(bytesPerSnapshot * snapshotsPerWeekAt15Min)} per week at full resolution");
        Console.WriteLine();
    }

    /// <summary>
    /// The full grid of depth caps against size floors, so the retention parameters can be chosen
    /// against measurements rather than estimates.
    /// </summary>
    private static void PrintHistogram(ScanResult result)
    {
        var cells = DirectoryHistogram.Compute(result.Rollup);

        Console.WriteLine("  Directories qualifying for snapshot_dirs");
        Console.Write($"    {"depth",-8}");

        foreach (var floor in DirectoryHistogram.DefaultFloors)
        {
            Console.Write($"{">= " + ByteSize.Format(floor),14}");
        }

        Console.WriteLine();

        foreach (var cap in DirectoryHistogram.DefaultDepthCaps)
        {
            var label = cap == int.MaxValue ? "any" : "<= " + cap.ToString(CultureInfo.InvariantCulture);
            Console.Write($"    {label,-8}");

            foreach (var floor in DirectoryHistogram.DefaultFloors)
            {
                var cell = cells.First(c => c.DepthCap == cap && c.SizeFloorBytes == floor);
                Console.Write($"{cell.DirectoryCount,14:N0}");
            }

            Console.WriteLine();
        }

        Console.WriteLine();
    }

    private static string DescribeInaccessible(ScanResult result)
        => string.Join(", ", result.Inaccessible.Select(kv => $"{kv.Value:N0} {Humanise(kv.Key)}"));

    private static string Humanise(DirectoryEnumerationError error)
        => error switch
        {
            DirectoryEnumerationError.AccessDenied => "access denied",
            DirectoryEnumerationError.NotFound => "vanished mid-scan",
            DirectoryEnumerationError.PathTooLong => "path too long",
            _ => "other",
        };

    private static void PrintCombinedSummary(List<ScanReport> reports)
    {
        Console.WriteLine("=== All volumes ===");
        Console.WriteLine($"  Total files      {reports.Sum(r => r.Result.FileCount),12:N0}");
        Console.WriteLine($"  Total logical    {ByteSize.Format(reports.Sum(r => r.Result.TotalBytes)),12}");
        Console.WriteLine($"  Total scan time  {reports.Sum(r => r.Result.Duration.TotalSeconds),12:F2} s");
        Console.WriteLine($"  Total write time {reports.Sum(r => r.WriteTime.TotalSeconds),12:F2} s");
        Console.WriteLine();
    }

    /// <summary>
    /// Reports what the database actually costs on disk, including the write-ahead log.
    /// </summary>
    private static void PrintDatabaseFootprint(StrataDatabase database)
    {
        var main = new FileInfo(database.DatabasePath);

        if (!main.Exists)
        {
            return;
        }

        var wal = new FileInfo(database.DatabasePath + "-wal");
        var shm = new FileInfo(database.DatabasePath + "-shm");

        var total = main.Length
            + (wal.Exists ? wal.Length : 0)
            + (shm.Exists ? shm.Length : 0);

        Console.WriteLine("=== Database ===");
        Console.WriteLine($"  {database.DatabasePath}");
        Console.WriteLine($"  Main             {ByteSize.Format(main.Length),12}");

        if (wal.Exists && wal.Length > 0)
        {
            Console.WriteLine($"  WAL              {ByteSize.Format(wal.Length),12}");
        }

        Console.WriteLine($"  Total            {ByteSize.Format(total),12}");
        Console.WriteLine();
    }

    private static List<VolumeIdentity> SelectVolumes(CliOptions options)
    {
        var all = VolumeEnumerator.Enumerate();

        if (options.AllVolumes || options.Targets.Count == 0)
        {
            return all.Where(VolumesCommand.IsScannable).ToList();
        }

        var selected = new List<VolumeIdentity>();

        foreach (var target in options.Targets)
        {
            var match = all.FirstOrDefault(v => CliOptions.Matches(v, target));

            if (match is null)
            {
                Console.Error.WriteLine($"strata: no volume matches '{target}'");
                continue;
            }

            selected.Add(match);
        }

        return selected;
    }

    private static int _progressWidth;

    private static void ReportProgress(ScanProgress progress)
    {
        var text =
            $"    {progress.DirectoriesCompleted,10:N0} dirs  " +
            $"{progress.FilesSeen,12:N0} files  " +
            $"{ByteSize.Format(progress.BytesSeen),10}  " +
            Truncate(progress.CurrentPath, 60);

        // Redirected output has no cursor to rewind, so carriage returns would run every update
        // together on one unreadable line. Piped output gets no live progress at all.
        if (Console.IsOutputRedirected)
        {
            return;
        }

        // Overwrites in place rather than scrolling. A full scan visits enough directories that
        // a line per update would bury the result.
        _progressWidth = Math.Max(_progressWidth, text.Length);
        Console.Write('\r' + text.PadRight(_progressWidth));
    }

    private static void ClearProgressLine()
    {
        if (_progressWidth > 0 && !Console.IsOutputRedirected)
        {
            Console.Write('\r' + new string(' ', _progressWidth) + '\r');
        }

        _progressWidth = 0;
    }

    private static string Truncate(string value, int width)
        => value.Length <= width ? value : "…" + value[^(width - 1)..];

    /// <summary>
    /// Reported because it changes what a scan can see: unelevated, parts of C: are unreadable, and
    /// a total that disagrees with Explorer's should have a visible reason.
    /// </summary>
    private static bool IsElevated()
    {
        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        var principal = new System.Security.Principal.WindowsPrincipal(identity);
        return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
    }

    private sealed record ScanReport(ScanResult Result, TimeSpan WriteTime);
}
