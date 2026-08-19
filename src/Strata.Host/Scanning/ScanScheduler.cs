using System.Collections.Concurrent;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
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

            if (!settings.Paused)
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
            }
        }
    }

    private void Tick(VolumeWatcher watcher, StrataSettings settings, bool ignoreGate = false)
    {
        var outcome = watcher.Tick(_gate, settings, ignoreGate);

        if (!outcome.Ran)
        {
            TickSkipped(watcher.Volume.Display, outcome.Deferral);
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
