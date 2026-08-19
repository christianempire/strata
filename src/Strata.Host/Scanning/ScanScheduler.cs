using System.Collections.Concurrent;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Strata.Core.Formatting;
using Strata.Core.Model;
using Strata.Core.Scanning;
using Strata.Platform;
using Strata.Storage;

namespace Strata.Host.Scanning;

/// <summary>A scan asked for by hand rather than by the clock.</summary>
internal sealed record ScanRequest(long Id, VolumeIdentity Volume, ScanTier Tier);

/// <summary>
/// The resident scanner: incremental ticks on a timer, plus whatever the API asks for.
/// </summary>
/// <remarks>
/// Runs on a dedicated thread rather than the thread pool. A Tier 1 baseline blocks its caller for
/// minutes — 211 s cold on the target machine — and doing that on a pool thread would take a thread
/// the API needs away from it for the whole scan. The enumeration workers the scan starts are also
/// dedicated threads, for the separate reason that they put themselves into background I/O mode for
/// their lifetime and a pooled thread left in that state would silently throttle unrelated work.
/// <para>
/// Settings are re-read each cycle, so a change saved through the API takes effect on the next tick
/// without a restart.
/// </para>
/// </remarks>
internal sealed partial class ScanScheduler : IHostedService, IDisposable
{
    private readonly StrataDatabase _database;
    private readonly ILogger<ScanScheduler> _logger;
    private readonly CancellationTokenSource _stopping = new();
    private readonly AutoResetEvent _wake = new(initialState: false);
    private readonly ConcurrentQueue<ScanRequest> _requests = new();
    private readonly List<VolumeWatcher> _watchers = [];

    private Thread? _thread;
    private WindowsScanGate _gate = new();
    private ScanGateOptions _gateOptions = new();
    private long _nextRequestId;

    /// <summary>
    /// When the cycle currently running began. Every log row of one tick carries it, so the rows
    /// belonging to a single tick group exactly rather than by timestamp proximity.
    /// </summary>
    private DateTime _cycleUtc = DateTime.UtcNow;

    internal ScanScheduler(StrataDatabase database, ILogger<ScanScheduler> logger)
    {
        _database = database;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _thread = new Thread(Loop)
        {
            IsBackground = true,
            Name = "strata-scheduler",
        };

        _thread.Start();

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _stopping.Cancel();
        _wake.Set();

        // Bounded: the loop checks for cancellation between volumes, but a baseline already under
        // way is not interruptible mid-directory and there is no point pretending otherwise.
        _thread?.Join(TimeSpan.FromSeconds(5));

        return Task.CompletedTask;
    }

    /// <summary>
    /// Queues a scan and returns immediately.
    /// </summary>
    /// <remarks>
    /// Never runs the scan on the caller's thread. A baseline takes minutes, and an HTTP request
    /// that blocks for minutes is a request that times out somewhere between here and the browser.
    /// </remarks>
    internal ScanRequest Enqueue(VolumeIdentity volume, ScanTier tier)
    {
        var request = new ScanRequest(Interlocked.Increment(ref _nextRequestId), volume, tier);

        _requests.Enqueue(request);
        _wake.Set();

        return request;
    }

    /// <summary>Wakes the loop, so a settings change takes effect without waiting out the interval.</summary>
    internal void Poke() => _wake.Set();

    private void Loop()
    {
        while (!_stopping.IsCancellationRequested)
        {
            _cycleUtc = DateTime.UtcNow;

            StrataSettings settings;

            try
            {
                using var connection = _database.OpenRead();
                settings = SettingsRepository.Load(connection);
            }
            catch (Microsoft.Data.Sqlite.SqliteException ex)
            {
                // The database being momentarily unavailable is not a reason to kill the scheduler
                // for the lifetime of the process.
                SettingsUnreadable(ex);
                settings = new StrataSettings();
            }

            RefreshGate(settings);
            DrainRequests(settings);

            if (settings.Paused)
            {
                // One row for the cycle rather than one per volume: pausing is a decision about the
                // scanner, not about a disk, and a row per volume would treble the log for a state
                // that can last days.
                Record(null, "—", ScanTier.Incremental, ScanOutcome.Deferred, "paused from the UI");
            }
            else
            {
                RunScheduledTicks(settings);
            }

            _wake.WaitOne(TimeSpan.FromSeconds(Math.Max(30, settings.IntervalSeconds)));
        }
    }

    private void RefreshGate(StrataSettings settings)
    {
        var wanted = new ScanGateOptions
        {
            CpuThresholdPercent = settings.CpuThresholdPercent,
            DetectBorderlessFullscreen = settings.DetectBorderlessFullscreen,
        };

        if (wanted == _gateOptions)
        {
            return;
        }

        // Replaced only when it actually changed: the gate holds the CPU sampler, whose reading is a
        // delta against the previous sample, and throwing that away every cycle would mean never
        // having a usable one.
        _gateOptions = wanted;
        _gate = new WindowsScanGate(wanted);
    }

    private void DrainRequests(StrataSettings settings)
    {
        while (!_stopping.IsCancellationRequested && _requests.TryDequeue(out var request))
        {
            try
            {
                if (request.Tier == ScanTier.Full)
                {
                    RunBaseline(request, settings);
                }
                else
                {
                    Tick(WatcherFor(request.Volume), settings, ignoreGate: true);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                       or Microsoft.Data.Sqlite.SqliteException)
            {
                RequestedScanFailed(ex, request.Volume.Display);
                Record(request.Volume, request.Tier, ScanOutcome.Failed, ex.Message);
            }
        }
    }

    private void RunBaseline(ScanRequest request, StrataSettings settings)
    {
        BaselineStarting(request.Volume.Display);

        var outcome = BaselineScan.Run(
            request.Volume,
            _database,
            BaselineScan.OptionsFrom(settings),
            BaselineScan.SnapshotOptionsFrom(settings),
            _stopping.Token);

        BaselineFinished(
            request.Volume.Display,
            outcome.Result.FileCount,
            outcome.Result.DirectoryCount,
            outcome.Result.Duration.TotalSeconds,
            outcome.WriteTime.TotalSeconds,
            outcome.SnapshotId);

        // Scan plus write, because what the log is asked to justify is how long the machine was busy
        // on Strata's behalf, and the write is not free at three million rows.
        Record(
            request.Volume,
            ScanTier.Full,
            ScanOutcome.Ran,
            $"full baseline, {outcome.Result.FileCount:N0} files in {outcome.Result.DirectoryCount:N0} directories",
            durationMs: (long)(outcome.Result.Duration + outcome.WriteTime).TotalMilliseconds,
            records: outcome.RowsWritten);

        // A baseline rewrote every row for the volume, so the directory tree the watcher was holding
        // describes the previous scan.
        Forget(request.Volume);
    }

    private void RunScheduledTicks(StrataSettings settings)
    {
        foreach (var volume in Watchable())
        {
            if (_stopping.IsCancellationRequested)
            {
                return;
            }

            try
            {
                Tick(WatcherFor(volume), settings);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                       or Microsoft.Data.Sqlite.SqliteException)
            {
                TickFailed(ex, volume.Display);
                Record(volume, ScanTier.Incremental, ScanOutcome.Failed, ex.Message);
            }
        }
    }

    private void Tick(VolumeWatcher watcher, StrataSettings settings, bool ignoreGate = false)
    {
        var outcome = watcher.Tick(_gate, settings, ignoreGate);

        if (!outcome.Ran)
        {
            TickSkipped(watcher.Volume.Display, outcome.Deferral);
            Record(watcher.Volume, outcome.Tier, ScanOutcome.Deferred, outcome.Deferral);
            return;
        }

        // Over-budget ticks are logged rather than absorbed. The 2 s budget is the whole reason the
        // incremental path exists in the shape it does.
        var level = outcome.OverBudget ? LogLevel.Warning : LogLevel.Information;

        TickCompleted(
            level,
            watcher.Volume.Display,
            outcome.RecordsRead,
            outcome.MetadataReads,
            outcome.Changes.Count,
            outcome.Duration.TotalMilliseconds,
            outcome.CatchingUp ? " (catching up)" : "");

        Record(
            watcher.Volume,
            outcome.Tier,
            ScanOutcome.Ran,
            Describe(outcome),
            durationMs: (long)outcome.Duration.TotalMilliseconds,
            records: outcome.RecordsRead,
            events: outcome.Changes.Count);
    }

    /// <summary>
    /// What a tick did, in one line, for someone auditing the log rather than debugging it.
    /// </summary>
    /// <remarks>
    /// Names the largest change when there was one. "incremental, 1,204 records" says the scanner
    /// worked; "16.78 GB deleted under C:\Users" says what it found, and that is the line that makes
    /// a tick in the log line up with a row in the events feed.
    /// </remarks>
    private static string Describe(TickOutcome outcome)
    {
        var note = outcome.CatchingUp ? ", catching up" : "";

        if (outcome.Changes.Count == 0)
        {
            return $"incremental, {outcome.RecordsRead:N0} journal records{note}";
        }

        var largest = outcome.Changes[0];

        foreach (var change in outcome.Changes)
        {
            if (change.AbsoluteDelta > largest.AbsoluteDelta)
            {
                largest = change;
            }
        }

        return $"incremental, {outcome.RecordsRead:N0} journal records{note} · " +
               $"{ByteSize.FormatDelta(largest.DeltaBytes)} {largest.Path}";
    }

    /// <summary>Records one attempt against a live volume.</summary>
    private void Record(
        VolumeIdentity volume,
        ScanTier tier,
        ScanOutcome result,
        string reason,
        long durationMs = 0,
        long records = 0,
        long events = 0)
        => Record(volume.GuidPath, volume.Display, tier, result, reason, durationMs, records, events);

    /// <summary>
    /// Writes one row of the audit log, and never lets failing to do so break the scan.
    /// </summary>
    /// <remarks>
    /// Its own connection rather than the tick's, because the tick has already committed and closed
    /// by the time this runs. A row costs one insert into a table of a few hundred rows a day, which
    /// is not a cost worth batching against the risk of losing the log on a crash — the log's whole
    /// purpose is to survive one.
    /// </remarks>
    private void Record(
        string? volumeKey,
        string display,
        ScanTier tier,
        ScanOutcome result,
        string reason,
        long durationMs = 0,
        long records = 0,
        long events = 0)
    {
        var now = DateTime.UtcNow;

        try
        {
            using var connection = _database.Open();

            ScanLogRepository.Append(connection, new ScanLogRow
            {
                CycleUtc = _cycleUtc.ToString("O"),
                WhenUtc = now.ToString("O"),
                VolumeId = volumeKey is null ? null : VolumeQueries.Resolve(connection, volumeKey),
                Volume = display,
                Tier = tier,
                Result = result,
                Reason = reason,
                DurationMs = durationMs,
                Records = records,
                Events = events,
            });
        }
        catch (Microsoft.Data.Sqlite.SqliteException ex)
        {
            ScanLogUnwritable(ex);
        }
    }

    private static List<VolumeIdentity> Watchable()
        => VolumeEnumerator.Enumerate()
            .Where(VolumesCommand.IsScannable)
            .Where(v => v.SupportsUsnJournal)
            .ToList();

    /// <summary>
    /// The watcher for a volume, created on first use and kept.
    /// </summary>
    /// <remarks>
    /// Kept because it holds the volume's directory tree, which is half a million rows on a system
    /// volume and would cost more to reload each tick than the journal read the tick exists for.
    /// Only ever touched from the scheduler thread.
    /// </remarks>
    private VolumeWatcher WatcherFor(VolumeIdentity volume)
    {
        foreach (var existing in _watchers)
        {
            if (string.Equals(existing.Volume.GuidPath, volume.GuidPath, StringComparison.OrdinalIgnoreCase))
            {
                return existing;
            }
        }

        var watcher = new VolumeWatcher(volume, _database);
        _watchers.Add(watcher);

        return watcher;
    }

    private void Forget(VolumeIdentity volume)
    {
        for (var i = _watchers.Count - 1; i >= 0; i--)
        {
            if (string.Equals(_watchers[i].Volume.GuidPath, volume.GuidPath, StringComparison.OrdinalIgnoreCase))
            {
                _watchers[i].Dispose();
                _watchers.RemoveAt(i);
            }
        }
    }

    // Source-generated logging. The analyser insists, and it is right to: these run on the
    // scheduler thread every tick, and the interpolation behind a disabled log level is work the
    // scanner should not be doing while trying to stay invisible.

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Could not read settings; using defaults for this cycle.")]
    private partial void SettingsUnreadable(Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not write the scan log for this tick.")]
    private partial void ScanLogUnwritable(Exception exception);

    [LoggerMessage(Level = LogLevel.Information, Message = "Baseline scan of {volume} starting.")]
    private partial void BaselineStarting(string volume);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Baseline scan of {volume} finished: {files} files, {dirs} directories, " +
                  "{seconds:F1} s scan + {writeSeconds:F1} s write, snapshot #{snapshot}.")]
    private partial void BaselineFinished(
        string volume, long files, int dirs, double seconds, double writeSeconds, long snapshot);

    [LoggerMessage(Level = LogLevel.Error, Message = "Requested scan of {volume} failed.")]
    private partial void RequestedScanFailed(Exception exception, string volume);

    [LoggerMessage(Level = LogLevel.Error, Message = "Tick on {volume} failed.")]
    private partial void TickFailed(Exception exception, string volume);

    [LoggerMessage(Level = LogLevel.Debug, Message = "{volume}: {reason}")]
    private partial void TickSkipped(string volume, string reason);

    /// <summary>Level is a parameter because an over-budget tick is a warning and a normal one is not.</summary>
    [LoggerMessage(
        Message = "{volume}: {records} records, {stats} stats, {events} events in {ms:F0} ms{note}")]
    private partial void TickCompleted(
        LogLevel level, string volume, int records, int stats, int events, double ms, string note);

    public void Dispose()
    {
        _stopping.Dispose();
        _wake.Dispose();

        foreach (var watcher in _watchers)
        {
            watcher.Dispose();
        }
    }
}
