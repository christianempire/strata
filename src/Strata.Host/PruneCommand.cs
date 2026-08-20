using System.Globalization;
using Strata.Core.Formatting;
using Strata.Core.Retention;
using Strata.Storage;

namespace Strata.Host;

/// <summary>
/// <c>strata prune</c> — thins recorded history down to the configured resolutions, by hand.
/// </summary>
/// <remarks>
/// The resident scanner does this nightly on its own, so this verb exists for the two things the
/// nightly pass cannot be: a dry run that says what would go without going, and an <c>--as-of</c>
/// that lets a pass be measured against real data today rather than in a week when the data has
/// aged into the tiers. Phase 1's CLI exists to produce numbers; this is the retention equivalent.
/// </remarks>
internal static class PruneCommand
{
    internal static int Run(CliOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var database = new StrataDatabase(options.DatabasePath);
        database.Migrate();

        using var connection = database.Open();

        var settings = SettingsRepository.Load(connection);
        var tiers = settings.Retention;
        var now = options.AsOfUtc ?? DateTime.UtcNow;

        var errors = tiers.Validate();

        if (errors.Count > 0)
        {
            Console.Error.WriteLine($"strata: {string.Join(" ", errors)}");
            return 1;
        }

        var horizons = RetentionHorizons.From(now, tiers);

        Console.WriteLine();
        Console.WriteLine($"Database:  {options.DatabasePath}");
        Console.WriteLine($"As of:     {now:yyyy-MM-dd HH:mm:ss}Z{(options.AsOfUtc is null ? "" : "   (overridden)")}");
        Console.WriteLine();
        Console.WriteLine($"  every measurement   newer than {Stamp(horizons.FullFromUnix)}");
        Console.WriteLine($"  one an hour         back to    {Stamp(horizons.HourlyFromUnix)}");
        Console.WriteLine($"  one a day           back to    {Stamp(horizons.DailyFromUnix)}");
        Console.WriteLine($"  one a week          before that, forever");
        Console.WriteLine($"  scan log            {tiers.ScanLogDays} days, dropped rather than thinned");
        Console.WriteLine();

        var sizeBefore = FileSize(options.DatabasePath);
        var result = RetentionRepository.Run(connection, tiers, now, options.DryRun);

        var verb = result.DryRun ? "would remove" : "removed";

        Console.WriteLine(
            $"dir_history  {result.HistoryBefore:N0} rows, {verb} {result.HistoryRemoved:N0} " +
            $"({Share(result.HistoryRemoved, result.HistoryBefore)})");
        Console.WriteLine($"scan_log     {verb} {result.ScanLogRemoved:N0} rows");
        Console.WriteLine($"took         {result.Duration.TotalMilliseconds:N0} ms");

        if (options.Compact && !options.DryRun)
        {
            Console.WriteLine();
            Console.WriteLine("compacting (rewrites the whole file, so this is not what the nightly pass does)...");

            var clock = System.Diagnostics.Stopwatch.StartNew();
            RetentionRepository.Compact(connection);
            clock.Stop();

            var sizeAfter = FileSize(options.DatabasePath);

            Console.WriteLine(
                $"  {ByteSize.Format(sizeBefore)} -> {ByteSize.Format(sizeAfter)} " +
                $"({ByteSize.FormatDelta(sizeAfter - sizeBefore)}) in {clock.Elapsed.TotalSeconds:N1} s");
        }
        else if (!options.DryRun)
        {
            Console.WriteLine();
            Console.WriteLine(
                "The file is the same size: freed pages go on SQLite's free list and are reused by\n" +
                "the next weeks of history. Pass --compact to hand the space back to Windows.");
        }

        Console.WriteLine();

        return 0;
    }

    private static string Stamp(long unix)
        => DateTime.UnixEpoch.AddSeconds(unix).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    private static string Share(long part, long whole)
        => whole == 0 ? "0%" : (part / (double)whole).ToString("P1", CultureInfo.InvariantCulture);

    /// <summary>The database plus its write-ahead log, which is where recent writes actually are.</summary>
    private static long FileSize(string path)
    {
        long total = 0;

        foreach (var candidate in new[] { path, path + "-wal" })
        {
            var file = new FileInfo(candidate);

            if (file.Exists)
            {
                total += file.Length;
            }
        }

        return total;
    }
}
