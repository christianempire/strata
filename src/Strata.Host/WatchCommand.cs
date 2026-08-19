using Strata.Core.Formatting;
using Strata.Core.Model;
using Strata.Core.Scanning;
using Strata.Host.Scanning;
using Strata.Platform;
using Strata.Storage;

namespace Strata.Host;

/// <summary>
/// <c>strata watch</c> — the background agent, run in the foreground.
/// </summary>
/// <remarks>
/// Running the scheduler as a console verb is deliberate: the resource behaviour is the thing most
/// likely to be wrong, and it is far easier to judge when every deferral and every tick duration is
/// printed as it happens. <c>strata serve</c> runs the same watcher behind the API, which is the
/// shape the product ships in; this stays as the diagnostic.
/// <para>
/// Configuration comes from the command line here rather than from the settings table, so a
/// measurement run is not silently affected by whatever the UI last saved.
/// </para>
/// </remarks>
internal static class WatchCommand
{
    internal static int Run(CliOptions options)
    {
        var database = new StrataDatabase(options.DatabasePath);
        database.Migrate();

        var settings = options.ToSettings();

        var gate = new WindowsScanGate(new ScanGateOptions
        {
            CpuThresholdPercent = settings.CpuThresholdPercent,
            DetectBorderlessFullscreen = settings.DetectBorderlessFullscreen,
        });

        var volumes = VolumeEnumerator.Enumerate()
            .Where(VolumesCommand.IsScannable)
            .Where(v => v.SupportsUsnJournal)
            .Where(v => options.Targets.Count == 0 || options.Targets.Any(t => CliOptions.Matches(v, t)))
            .ToList();

        if (volumes.Count == 0)
        {
            Console.Error.WriteLine("strata: no volumes with a change journal to watch.");
            return 1;
        }

        Console.WriteLine($"Watching {volumes.Count} volume(s) every {settings.IntervalSeconds}s. Ctrl+C to stop.");
        Console.WriteLine($"Elevated: {(Elevation.IsElevated ? "yes" : "no")}");
        Console.WriteLine();

        using var stopping = new CancellationTokenSource();

        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            stopping.Cancel();
        };

        var watchers = volumes.Select(v => new VolumeWatcher(v, database)).ToList();

        try
        {
            while (!stopping.IsCancellationRequested)
            {
                foreach (var watcher in watchers)
                {
                    if (stopping.IsCancellationRequested)
                    {
                        break;
                    }

                    Report(watcher.Volume, watcher.Tick(gate, settings, options.IgnoreGate), options.Verbose);
                }

                if (options.RunOnce)
                {
                    break;
                }

                Wait(TimeSpan.FromSeconds(settings.IntervalSeconds), stopping.Token);
            }
        }
        finally
        {
            foreach (var watcher in watchers)
            {
                watcher.Dispose();
            }
        }

        Console.WriteLine();
        Console.WriteLine("Stopped.");
        return 0;
    }

    private static void Wait(TimeSpan duration, CancellationToken token)
    {
        try
        {
            token.WaitHandle.WaitOne(duration);
        }
        catch (ObjectDisposedException)
        {
            // Shutting down.
        }
    }

    private static void Report(VolumeIdentity volume, TickOutcome outcome, bool verbose)
    {
        if (!outcome.Ran)
        {
            Line(volume, outcome.Tier, outcome.Deferral);
            return;
        }

        var breakdown = verbose
            ? $"   [read {outcome.JournalRead.TotalMilliseconds:F0} / " +
              $"apply {outcome.Apply.TotalMilliseconds:F0} " +
              $"(meta {outcome.Metadata.TotalMilliseconds:F0}, " +
              $"store {outcome.Store.TotalMilliseconds:F0}, " +
              $"{outcome.DirectoryChanges} dirs changed) / " +
              $"rollup {outcome.RollupMs} / events {outcome.EventMs} / commit {outcome.CommitMs} ms]"
            : "";

        Line(
            volume,
            outcome.Tier,
            $"{outcome.RecordsRead,6} records  {outcome.MetadataReads,5} stats  " +
            $"{outcome.Changes.Count,3} events  {outcome.Duration.TotalMilliseconds,7:F0} ms" +
            (outcome.CatchingUp ? "  [catching up]" : "") +
            (outcome.OverBudget ? "  [OVER BUDGET]" : "") +
            breakdown);

        foreach (var change in outcome.Changes)
        {
            Console.WriteLine(
                $"    {ByteSize.FormatDelta(change.DeltaBytes),12}  " +
                $"{change.FileCountDelta,+5} files  {change.Path}");
        }
    }

    private static void Line(VolumeIdentity volume, ScanTier tier, string message)
        => Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {volume.Display,-24} {tier,-11} {message}");
}
