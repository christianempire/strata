using System.Diagnostics;
using Microsoft.Data.Sqlite;
using Strata.Core.Diffing;
using Strata.Core.Formatting;
using Strata.Core.Model;
using Strata.Core.Rollup;
using Strata.Core.Scanning;
using Strata.Platform;
using Strata.Storage;

namespace Strata.Host;

/// <summary>
/// <c>strata watch</c> - the background agent, run in the foreground.
/// </summary>
/// <remarks>
/// This is the scheduler that Phase 4 will host behind a tray icon. Running it as a console verb
/// first is deliberate: the resource behaviour is the thing most likely to be wrong, and it is far
/// easier to judge when every deferral and every tick duration is printed as it happens.
/// </remarks>
internal static class WatchCommand
{
    internal static int Run(CliOptions options)
    {
        var database = new StrataDatabase(options.DatabasePath);
        database.Migrate();

        var gate = new WindowsScanGate(new ScanGateOptions
        {
            CpuThresholdPercent = options.CpuThreshold,
            DetectBorderlessFullscreen = !options.NoFullscreenDetection,
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

        Console.WriteLine($"Watching {volumes.Count} volume(s) every {options.IntervalSeconds}s. Ctrl+C to stop.");
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

                    watcher.Tick(gate, options);
                }

                if (options.RunOnce)
                {
                    break;
                }

                Wait(TimeSpan.FromSeconds(options.IntervalSeconds), stopping.Token);
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

    /// <summary>Keeps one volume up to date.</summary>
    private sealed class VolumeWatcher : IDisposable
    {
        private readonly VolumeIdentity _volume;
        private readonly StrataDatabase _database;
        private readonly Win32UsnJournal _journal;
        private readonly Win32FileMetadataReader _metadata;

        /// <summary>
        /// The directory tree, loaded once and kept.
        /// </summary>
        /// <remarks>
        /// Half a million rows on a system volume. Re-reading it every tick would cost more than the
        /// journal read the tick exists to perform, and the tree only changes when a directory is
        /// created or removed - which Tier 2 sees and can apply in place.
        /// </remarks>
        private Dictionary<long, DirectoryNode>? _tree;

        internal VolumeWatcher(VolumeIdentity volume, StrataDatabase database)
        {
            _volume = volume;
            _database = database;
            _journal = new Win32UsnJournal(volume);
            _metadata = new Win32FileMetadataReader(volume);
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage(
            "Performance",
            "CA1859:Use concrete types when possible",
            Justification = "The gate is an abstraction boundary on purpose: it is what lets the " +
                            "scheduler's behaviour be tested without a game running. A virtual call " +
                            "once per volume per fifteen minutes is not a cost worth the coupling.")]
        internal void Tick(IScanGate gate, CliOptions options)
        {
            using var connection = _database.Open();
            var volumeId = VolumeRepository.Upsert(connection, _volume);

            if (!IncrementalRepository.HasBaseline(connection, volumeId))
            {
                Report(ScanTier.Full, "no baseline yet; run 'strata scan' first");
                return;
            }

            var decision = options.IgnoreGate ? GateDecision.Allow : gate.CanScan(ScanTier.Incremental);

            if (!decision.Allowed)
            {
                Report(ScanTier.Incremental, $"deferred: {decision.Reason}{Detail(decision)}");
                return;
            }

            var cursor = IncrementalRepository.GetCursor(connection, volumeId);
            var scanner = new Tier2Scanner(_journal, _metadata, new Tier2Options
            {
                MaxRecordsPerTick = options.MaxRecordsPerTick,
            });

            var clock = Stopwatch.StartNew();

            using var transaction = connection.BeginTransaction();
            using var store = new SqliteFileStateStore(connection, volumeId, transaction);

            var phase = Stopwatch.StartNew();
            var result = scanner.Run(cursor, store);

            if (result.Status != UsnReadStatus.Ok)
            {
                transaction.Rollback();
                Report(ScanTier.Incremental, Explain(result));
                return;
            }

            phase.Restart();
            IncrementalRepository.ApplyDeltas(connection, transaction, volumeId, result.Deltas);
            var deltaMs = phase.ElapsedMilliseconds;

            // Every tick is a snapshot, so the timeline is a continuous series rather than a few
            // points wherever a baseline happened to run. Events hang off it.
            var snapshotId = IncrementalRepository.CreateIncrementalSnapshot(
                connection, transaction, volumeId, DateTime.UtcNow,
                (long)clock.Elapsed.TotalMilliseconds, _volume.FreeBytes);

            phase.Restart();
            var events = WriteEvents(connection, transaction, volumeId, snapshotId, result, options);
            var eventMs = phase.ElapsedMilliseconds;

            // The cursor is committed with the data it describes. Committing it separately would let
            // a crash between the two skip those records permanently, which corrupts history with no
            // error to notice.
            IncrementalRepository.SaveCursor(connection, transaction, volumeId, result.Cursor);
            phase.Restart();
            transaction.Commit();
            var commitMs = phase.ElapsedMilliseconds;

            clock.Stop();

            var overBudget = clock.Elapsed > TimeSpan.FromSeconds(2);

            var breakdown = options.Verbose
                ? $"   [read {result.JournalReadDuration.TotalMilliseconds:F0} / " +
                  $"apply {result.ApplyDuration.TotalMilliseconds:F0} " +
                  $"(meta {result.MetadataDuration.TotalMilliseconds:F0}, " +
                  $"store {result.StoreDuration.TotalMilliseconds:F0}, " +
                  $"{result.DirectoryChanges} dirs changed) / " +
                  $"rollup {deltaMs} / events {eventMs} / commit {commitMs} ms]"
                : "";

            Report(ScanTier.Incremental,
                $"{result.RecordsRead,6} records  {result.MetadataReads,5} stats  {events,3} events  " +
                $"{clock.Elapsed.TotalMilliseconds,7:F0} ms" +
                (result.CatchingUp ? "  [catching up]" : "") +
                (overBudget ? "  [OVER BUDGET]" : "") +
                breakdown);
        }

        private int WriteEvents(
            SqliteConnection connection,
            SqliteTransaction transaction,
            long volumeId,
            long snapshotId,
            Tier2Result result,
            CliOptions options)
        {
            if (result.Deltas.Count == 0)
            {
                return 0;
            }

            var tree = _tree ??= IncrementalRepository.LoadDirectoryTree(connection, volumeId);

            if (tree.Count == 0)
            {
                return 0;
            }

            var rootId = tree.Values.FirstOrDefault(n => n.Depth == 0)?.Id ?? 5;
            var prefix = _volume.DriveLetter ?? _volume.GuidPath;

            // Strata's own database sits on a watched volume and is rewritten by every tick, so
            // without this it is permanently the loudest thing in its own feed - a 435 MB file
            // churning on a fifteen-minute cycle drowns out whatever the user actually did.
            //
            // Filtered from attribution only, not from the rollup: the database occupies real disk
            // and should still appear in totals and in the treemap. It is noise in the change log,
            // not a lie in the accounting.
            var reportable = PathExclusions.Filter(result.Deltas, tree, prefix, options.ExcludedEventPaths);

            if (reportable.Count == 0)
            {
                return 0;
            }

            var changes = DiffEngine.Attribute(
                tree,
                rootId,
                reportable,
                _volume.DriveLetter ?? _volume.GuidPath,
                new DiffOptions { MinimumDeltaBytes = options.EventFloorBytes });

            if (changes.Count == 0)
            {
                return 0;
            }

            foreach (var change in changes)
            {
                Console.WriteLine(
                    $"    {ByteSize.FormatDelta(change.DeltaBytes),12}  " +
                    $"{change.FileCountDelta,+5} files  {change.Path}");
            }

            return IncrementalRepository.WriteEvents(
                connection, transaction, volumeId, snapshotId, DateTime.UtcNow, changes);
        }

        private static string Detail(GateDecision decision)
            => string.IsNullOrEmpty(decision.Detail) ? "" : $" ({decision.Detail})";

        private static string Explain(Tier2Result result)
            => result.Status switch
            {
                UsnReadStatus.JournalNotActive =>
                    "no change journal; run 'strata enable-journal' elevated to create one",
                UsnReadStatus.JournalInvalidated =>
                    "journal reset or cursor trimmed; a full baseline is needed",
                UsnReadStatus.AccessDenied => "journal not readable",
                _ => $"journal read failed ({result.Status})",
            };

        private void Report(ScanTier tier, string message)
            => Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {_volume.Display,-24} {tier,-11} {message}");

        public void Dispose()
        {
            _journal.Dispose();
            _metadata.Dispose();
        }
    }
}
