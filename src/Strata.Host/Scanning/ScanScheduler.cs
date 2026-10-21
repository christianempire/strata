using System.Collections.Concurrent;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Strata.Core.Formatting;
using Strata.Core.Model;
using Strata.Core.Retention;
using Strata.Core.Scanning;
using Strata.Platform;
using Strata.Storage;

namespace Strata.Host.Scanning;

/// <summary>A scan asked for by hand rather than by the clock.</summary>
internal sealed record ScanRequest(long Id, VolumeIdentity Volume, ScanTier Tier);

/// <summary>What the resident scanner is doing.</summary>
public enum ScannerState
{
    /// <summary>Waiting for the next tick. The normal state, and the one the product aims for.</summary>
    Idle = 0,

    Scanning = 1,

    /// <summary>The gate said no on the last attempt, and will keep saying it until the cause goes.</summary>
    Deferred = 2,

    /// <summary>Suspended from the UI or the tray. A decision, not a condition.</summary>
    Paused = 3,
}

/// <summary>A scan waiting its turn.</summary>
public sealed record QueuedScan(string Volume, ScanTier Tier);

/// <summary>
/// Where one volume's incremental scanning stands.
/// </summary>
/// <remarks>
/// Added after C: went 27 hours without a tick behind a single scheduler-wide "deferred". One
/// deferral string for the whole machine cannot say that C: is stuck while D: and E: are fine, and a
/// screen showing where the bytes are needs to know how old the answer for *this* volume is.
/// </remarks>
public sealed record VolumeScanStatus
{
    /// <summary>How the volume is named on screen.</summary>
    public required string Volume { get; init; }

    /// <summary>Drive letter or GUID path — what the rest of the API takes as <c>volume</c>.</summary>
    public required string Key { get; init; }

    /// <summary>
    /// When this volume last ticked successfully, which is how old its recorded state is.
    /// </summary>
    /// <remarks>
    /// The scheduler-wide <see cref="SchedulerStatus.LastTickUtc"/> is when anything last happened
    /// anywhere. This is the one a caller reporting a volume's freshness wants.
    /// </remarks>
    public string? LastTickUtc { get; init; }

    public string? NextTickUtc { get; init; }

    /// <summary>Why the last attempt on this volume did not run. Empty when it did.</summary>
    public string Deferral { get; init; } = "";

    /// <summary>The journal cannot be resumed, so nothing will tick until a baseline runs.</summary>
    public bool NeedsBaseline { get; init; }

    /// <summary>The last tick hit its record cap, so this volume is on the short interval.</summary>
    public bool CatchingUp { get; init; }

    /// <summary>Attempts in a row that did not run.</summary>
    public int ConsecutiveDeferrals { get; init; }
}

/// <summary>The scanner's state, as a caller polling it sees it.</summary>
public sealed record SchedulerStatus
{
    public required ScannerState State { get; init; }

    /// <summary>Null unless something is scanning right now.</summary>
    public ScanActivitySnapshot? Activity { get; init; }

    public IReadOnlyList<QueuedScan> Queued { get; init; } = [];

    public string? LastTickUtc { get; init; }

    /// <summary>Null while a scan is running or the scanner is paused, when there is no such time.</summary>
    public string? NextTickUtc { get; init; }

    /// <summary>Why the last attempt did not run. Empty when it did.</summary>
    public string Deferral { get; init; } = "";

    /// <summary>One row per volume the scheduler is ticking.</summary>
    public IReadOnlyList<VolumeScanStatus> Volumes { get; init; } = [];
}

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

    /// <summary>
    /// When each volume is next due, and when one that keeps not ticking gets forced.
    /// </summary>
    /// <remarks>
    /// Scheduler thread only. The decisions it makes live in <c>Strata.Core</c> so they can be
    /// driven by hand — the failure they exist to prevent took 27 hours to appear on a real machine
    /// and left nothing behind but a repeated log line.
    /// </remarks>
    private readonly ScanCadence _cadence = new();

    /// <summary>The last thing said about each volume, keyed by GUID path. Scheduler thread only.</summary>
    private readonly Dictionary<string, VolumeNote> _notes = new(StringComparer.OrdinalIgnoreCase);

    private Thread? _thread;
    private WindowsScanGate _gate = new();
    private ScanGateOptions _gateOptions = new();
    private long _nextRequestId;

    /// <summary>
    /// When the cycle currently running began. Every log row of one tick carries it, so the rows
    /// belonging to a single tick group exactly rather than by timestamp proximity.
    /// </summary>
    private DateTime _cycleUtc = DateTime.UtcNow;

    /// <summary>The scan happening right now, or null. Written by the scheduler thread, read by the API.</summary>
    private ScanActivity? _current;

    private string _lastDeferral = "";
    private DateTime? _lastTickUtc;
    private DateTime _nextTickUtc = DateTime.UtcNow;
    private bool _paused;

    /// <summary>
    /// Per-volume status, republished as a whole list after each cycle.
    /// </summary>
    /// <remarks>
    /// Published by reference rather than locked, for the reason given on <see cref="Status"/>: an
    /// immutable list swapped in one write is safe to read from an HTTP thread, and a dictionary
    /// being mutated on the scheduler thread is not.
    /// </remarks>
    private IReadOnlyList<VolumeScanStatus> _volumeStatus = [];

    /// <summary>
    /// The shortest the loop will ever sleep.
    /// </summary>
    /// <remarks>
    /// A floor rather than a cadence. Due times are at least fifteen seconds apart by validation, so
    /// this only bites when a due time is already in the past — which must not turn into a spin
    /// against the settings table and the volume enumeration.
    /// </remarks>
    private static readonly TimeSpan MinimumWait = TimeSpan.FromSeconds(5);

    /// <summary>
    /// When retention last ran, so it runs once a day rather than once a cycle.
    /// </summary>
    /// <remarks>
    /// Not persisted. A restart therefore prunes on its first idle moment, which is a few seconds of
    /// work on a table it has to open anyway, and is a great deal simpler than a stored timestamp
    /// that could be in the future after a clock change.
    /// </remarks>
    private DateTime _lastPruneUtc = DateTime.MinValue;

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

    /// <summary>
    /// What the scanner is doing, for a caller that has just asked for a scan and wants to watch it.
    /// </summary>
    /// <remarks>
    /// Read from an HTTP thread while the scheduler thread writes. Nothing is locked: every field is
    /// either a reference or a value small enough to be written atomically, and a status that mixes
    /// a fresh activity with a stale next-tick time is describing a process that moved between the
    /// two reads. That is what polling a running scan looks like, and pretending otherwise would
    /// mean taking a lock on the scan's own thread to serve a poll.
    /// </remarks>
    internal SchedulerStatus Status()
    {
        var activity = Volatile.Read(ref _current);
        var queued = _requests.Select(r => new QueuedScan(r.Volume.Display, r.Tier)).ToList();
        var volumes = Volatile.Read(ref _volumeStatus);

        var state = activity is not null ? ScannerState.Scanning
            : _paused ? ScannerState.Paused
            : _lastDeferral.Length > 0 ? ScannerState.Deferred
            : ScannerState.Idle;

        return new SchedulerStatus
        {
            Volumes = volumes,
            State = state,
            Activity = activity?.Snapshot(),
            Queued = queued,
            LastTickUtc = _lastTickUtc?.ToString("O"),

            // Meaningless while a scan is running — the next tick is whenever this one finishes —
            // and worse than meaningless if reported as a time that has already passed.
            NextTickUtc = activity is null && !_paused ? _nextTickUtc.ToString("O") : null,
            Deferral = _lastDeferral,
        };
    }

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

            _paused = settings.Paused;

            var cadence = CadenceFrom(settings);

            RefreshGate(settings);
            DrainRequests(settings, cadence);

            if (settings.Paused)
            {
                // One row for the cycle rather than one per volume: pausing is a decision about the
                // scanner, not about a disk, and a row per volume would treble the log for a state
                // that can last days.
                Record(null, "—", ScanTier.Incremental, ScanOutcome.Deferred, "paused from the UI");
            }
            else
            {
                RunScheduledTicks(settings, cadence);
            }

            PublishVolumeStatus();

            if (!settings.Paused)
            {
                Prune(settings);
            }

            // Paused takes the whole interval regardless of what is due: due times stop advancing
            // when nothing ticks, so pacing off them would log "paused from the UI" every five
            // seconds for as long as the pause lasted.
            var wait = settings.Paused ? cadence.Interval : WaitFor(cadence);

            _nextTickUtc = DateTime.UtcNow + wait;
            _wake.WaitOne(wait);
        }
    }

    /// <summary>
    /// Thins recorded history, at most once a day.
    /// </summary>
    /// <remarks>
    /// On the scheduler thread, deliberately, and after the cycle's ticks rather than before. It
    /// takes a write lock on the one database a tick also writes to, and the busy timeout is five
    /// seconds — a prune on its own thread would eventually collide with a tick and fail one of
    /// them. Here they are serialised by construction and neither can wait on the other.
    /// <para>
    /// Not gated. It is a bounded amount of work against a table already in the page cache, once a
    /// day, and unlike a baseline there is no version of it that runs for minutes. Gating it would
    /// mean a machine in use every evening never pruning at all.
    /// </para>
    /// </remarks>
    private void Prune(StrataSettings settings)
    {
        var now = DateTime.UtcNow;

        if (!settings.RetentionEnabled || now - _lastPruneUtc < TimeSpan.FromDays(1))
        {
            return;
        }

        var tiers = settings.Retention;

        if (tiers.Validate().Count > 0)
        {
            // Stamped anyway, so a configuration that cannot be applied is complained about once a
            // day rather than once a cycle.
            _lastPruneUtc = now;
            RetentionMisconfigured(string.Join(" ", tiers.Validate()));

            return;
        }

        _lastPruneUtc = now;

        try
        {
            using var connection = _database.Open();

            var result = RetentionRepository.Run(connection, tiers, now);

            RetentionFinished(
                result.HistoryRemoved, result.ScanLogRemoved, result.Duration.TotalMilliseconds);

            // Only when it actually did something. A nightly row saying "removed nothing" for the
            // first week of a fresh install is noise in the one log that exists to be read.
            if (result.TotalRemoved > 0)
            {
                Record(
                    null,
                    "—",
                    ScanTier.Incremental,
                    ScanOutcome.Ran,
                    $"retention: thinned {result.HistoryRemoved:N0} history rows, " +
                    $"dropped {result.ScanLogRemoved:N0} log rows",
                    durationMs: (long)result.Duration.TotalMilliseconds,
                    records: result.TotalRemoved);
            }
        }
        catch (Microsoft.Data.Sqlite.SqliteException ex)
        {
            // A failed prune costs disk space, not correctness. It is not a reason to stop scanning.
            RetentionFailed(ex);
        }
    }

    /// <summary>The pacing and escalation rules, as this cycle's settings describe them.</summary>
    /// <remarks>
    /// Rebuilt per cycle rather than held, so a settings change takes effect on the next tick like
    /// everything else the loop reads. Clamped as well as validated: settings can be written
    /// straight into the database by something that never ran <see cref="StrataSettings.Validate"/>,
    /// and a catch-up interval of zero would be a busy loop against the disk.
    /// </remarks>
    private static ScanCadenceOptions CadenceFrom(StrataSettings settings)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(30, settings.IntervalSeconds));

        return new ScanCadenceOptions
        {
            Interval = interval,
            CatchUpInterval = TimeSpan.FromSeconds(
                Math.Clamp(settings.CatchUpIntervalSeconds, 15, interval.TotalSeconds)),
        };
    }

    /// <summary>How long to sleep before the next cycle.</summary>
    /// <remarks>
    /// The earliest volume's due time, not a fixed interval. That is what lets one volume that is
    /// behind be ticked every minute while the quiet ones keep their fifteen — ticking those
    /// fifteen times as often would multiply their snapshot rows by fifteen to learn nothing.
    /// </remarks>
    private TimeSpan WaitFor(ScanCadenceOptions cadence)
    {
        var due = _cadence.EarliestDueUtc();

        if (due is null)
        {
            return cadence.Interval;
        }

        var wait = due.Value - DateTime.UtcNow;

        // Capped at one interval so a settings change, a newly attached volume, or a pause lifted
        // from the tray is never further away than it used to be.
        return wait < MinimumWait ? MinimumWait
            : wait > cadence.Interval ? cadence.Interval
            : wait;
    }

    /// <summary>Republishes the per-volume status the API serves.</summary>
    private void PublishVolumeStatus()
    {
        var rows = new List<VolumeScanStatus>(_notes.Count);

        foreach (var state in _cadence.Snapshot())
        {
            if (!_notes.TryGetValue(state.Volume, out var note))
            {
                continue;
            }

            rows.Add(new VolumeScanStatus
            {
                Volume = note.Display,
                Key = note.Key,
                LastTickUtc = state.LastSuccessUtc?.ToString("O"),
                NextTickUtc = state.NextDueUtc.ToString("O"),
                Deferral = note.Reason,
                NeedsBaseline = state.Deferral == DeferralKind.NeedsBaseline,
                CatchingUp = state.CatchingUp,
                ConsecutiveDeferrals = state.ConsecutiveDeferrals,
            });
        }

        Volatile.Write(ref _volumeStatus, rows);
    }

    private void Remember(VolumeIdentity volume, string reason)
        => _notes[volume.GuidPath] = new VolumeNote(
            volume.Display, volume.DriveLetter ?? volume.GuidPath, reason);

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

    private void DrainRequests(StrataSettings settings, ScanCadenceOptions cadence)
    {
        while (!_stopping.IsCancellationRequested && _requests.TryDequeue(out var request))
        {
            try
            {
                if (request.Tier == ScanTier.Full)
                {
                    RunBaseline(request.Volume, settings, cadence, automaticCause: null);
                }
                else
                {
                    // Asked for by hand, so the gate is not consulted — but the cadence still hears
                    // about it, because a tick that ran is a tick that reset the volume's run of
                    // deferrals whoever asked for it.
                    var outcome = RunTick(WatcherFor(request.Volume), settings, ignoreGate: true);

                    LogTick(request.Volume, outcome, forced: false);
                    _cadence.Record(
                        request.Volume.GuidPath, ReportOf(outcome), DateTime.UtcNow, cadence);
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

    /// <param name="automaticCause">
    /// Why Strata started this baseline of its own accord, in words for the log; null when a person
    /// asked for it.
    /// </param>
    private void RunBaseline(
        VolumeIdentity volume, StrataSettings settings, ScanCadenceOptions cadence, string? automaticCause)
    {
        BaselineStarting(volume.Display);

        var activity = new ScanActivity(
            volume.Display,
            ScanTier.Full,
            LastEntryCount(volume),
            volume.UsedBytes,

            // A baseline's first seconds are spent clearing the previous scan, so it starts here
            // rather than in the reading phase whose counters would all read zero.
            ScanPhase.Preparing);

        Volatile.Write(ref _current, activity);

        BaselineOutcome outcome;

        try
        {
            outcome = BaselineScan.Run(
                volume,
                _database,
                BaselineScan.OptionsFrom(settings, activity.Record),
                _stopping.Token,
                activity.SetPhase);
        }
        finally
        {
            // In a finally because a scan that threw is a scan that is no longer running, and
            // leaving the activity in place would report a frozen 62% for the life of the process.
            Volatile.Write(ref _current, null);
        }

        BaselineFinished(
            volume.Display,
            outcome.Result.FileCount,
            outcome.Result.DirectoryCount,
            outcome.Result.Duration.TotalSeconds,
            outcome.WriteTime.TotalSeconds,
            outcome.SnapshotId);

        // "automatic" is in the reason rather than in a column because the log is read by a person
        // asking why their disk was busy, and the answer "Strata decided to" needs to be in the
        // sentence rather than inferred from the absence of a request.
        var what = automaticCause is null
            ? "full baseline, "
            : $"automatic recovery baseline, {automaticCause} — ";

        // Scan plus write, because what the log is asked to justify is how long the machine was busy
        // on Strata's behalf, and the write is not free at three million rows.
        Record(
            volume,
            ScanTier.Full,
            ScanOutcome.Ran,
            $"{what}{outcome.Result.FileCount:N0} files in {outcome.Result.DirectoryCount:N0} directories",
            durationMs: (long)(outcome.Result.Duration + outcome.WriteTime).TotalMilliseconds,
            records: outcome.RowsWritten);

        // A baseline rewrote every row for the volume, so the directory tree the watcher was holding
        // describes the previous scan.
        Forget(volume);

        // And it cleared the journal cursor, so the volume is no longer stuck. Its next tick is one
        // ordinary interval away rather than immediate: nothing has changed in the seconds since the
        // scan that finished reading the whole volume.
        Remember(volume, "");
        _cadence.Reset(volume.GuidPath, DateTime.UtcNow, cadence);
    }

    /// <summary>Ticks every volume that has fallen due, and only those.</summary>
    private void RunScheduledTicks(StrataSettings settings, ScanCadenceOptions cadence)
    {
        foreach (var volume in Watchable())
        {
            if (_stopping.IsCancellationRequested)
            {
                return;
            }

            if (!_cadence.IsDue(volume.GuidPath, DateTime.UtcNow))
            {
                continue;
            }

            try
            {
                TickVolume(WatcherFor(volume), settings, cadence);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                       or Microsoft.Data.Sqlite.SqliteException)
            {
                TickFailed(ex, volume.Display);
                Record(volume, ScanTier.Incremental, ScanOutcome.Failed, ex.Message);
                Remember(volume, ex.Message);

                // A volume that threw takes an ordinary interval before being tried again, and does
                // not escalate: no amount of forcing fixes an exception, and a volume that was on
                // the accelerated cadence must not stay on it while failing.
                _cadence.Record(
                    volume.GuidPath,
                    TickReport.Deferred(DeferralKind.NeedsPerson),
                    DateTime.UtcNow,
                    cadence);
            }
        }
    }

    /// <summary>
    /// One volume’s turn: tick it, record what happened, and act if it has stopped ticking.
    /// </summary>
    /// <remarks>
    /// The acting is the part that was missing. Before this, a tick that reported it needed a
    /// baseline was written to the log and forgotten, so the remedy was named on every one of 101
    /// consecutive deferrals and applied on none of them.
    /// </remarks>
    private void TickVolume(VolumeWatcher watcher, StrataSettings settings, ScanCadenceOptions cadence)
    {
        var volume = watcher.Volume;
        var outcome = RunTick(watcher, settings, ignoreGate: false);

        LogTick(volume, outcome, forced: false);

        switch (_cadence.Record(volume.GuidPath, ReportOf(outcome), DateTime.UtcNow, cadence))
        {
            case Escalation.TickAnyway:
                ForceTick(watcher, settings, cadence);
                break;

            case Escalation.Baseline:
                RecoverWithBaseline(volume, settings, cadence, JournalLost);
                return;
        }

        // A tick that ran can still be wrong. The rollup is kept by deltas between baselines, and a
        // delta lost to a bug stays lost: E: reported 1,446 GB on a disk with 939 GB used while every
        // tick succeeded. Drift above the volume is the one symptom that cannot be legitimate, so it
        // gets the same remedy as a lost journal, through the same gate and the same cooldown.
        if (outcome.OvercountBytes is { } over
            && RollupDrift.NeedsBaseline(over, volume.TotalBytes)
            && _cadence.MayRebaseline(volume.GuidPath, DateTime.UtcNow, cadence))
        {
            RecoverWithBaseline(
                volume,
                settings,
                cadence,
                $"the recorded total had drifted {ByteSize.Format(over)} above the volume since the last baseline");
        }
    }

    private const string JournalLost = "the journal could not be resumed";

    /// <summary>Runs one incremental tick, keeping the reported activity honest either way.</summary>
    private TickOutcome RunTick(VolumeWatcher watcher, StrataSettings settings, bool ignoreGate)
    {
        var activity = new ScanActivity(
            watcher.Volume.Display, ScanTier.Incremental, null, watcher.Volume.UsedBytes);

        Volatile.Write(ref _current, activity);

        try
        {
            return watcher.Tick(_gate, settings, ignoreGate);
        }
        finally
        {
            // In a finally because a tick that threw is a tick that is no longer running.
            Volatile.Write(ref _current, null);
        }
    }

    /// <summary>
    /// Ticks a volume whose gate has said no once too often.
    /// </summary>
    /// <remarks>
    /// Only ever reached for a busy machine, never for a game on screen — <see cref="ScanCadence"/>
    /// will not offer this escalation for a presentation-like state, because the first
    /// non-negotiable outranks the history it costs. What it buys is that a machine merely under
    /// load for hours does not let its journal cursor drift off the back of a 32 MiB circular
    /// buffer, which is unrecoverable, in order to avoid a millisecond of journal reading, which is
    /// not.
    /// </remarks>
    private void ForceTick(VolumeWatcher watcher, StrataSettings settings, ScanCadenceOptions cadence)
    {
        var volume = watcher.Volume;

        _cadence.Escalated(volume.GuidPath, Escalation.TickAnyway, DateTime.UtcNow);
        TickEscalated(volume.Display, cadence.DeferralsBeforeEscalating);

        var outcome = RunTick(watcher, settings, ignoreGate: true);

        LogTick(volume, outcome, forced: true);
        _cadence.Record(volume.GuidPath, ReportOf(outcome), DateTime.UtcNow, cadence);
    }

    /// <summary>
    /// Runs a baseline for a volume whose journal can no longer be resumed, or whose recorded total
    /// has drifted away from the volume.
    /// </summary>
    /// <remarks>
    /// Put through the <see cref="ScanTier.Full"/> gate rather than forced, and that is the whole of
    /// the resource decision. A baseline is minutes of sustained I/O — 142 s on C: during this
    /// incident — so it must not start over a game, a presentation, or a machine being typed at,
    /// all of which the Full-tier gate already refuses. Deferring the recovery forever is how the
    /// volume got 27 hours stale in the first place; waiting for the gate to say yes is the line
    /// between those two failures, and the gate is asked every cycle so the first idle moment is
    /// taken.
    /// <para>
    /// The cooldown starts only if the scan actually begins. A refusal has not used the recovery up.
    /// </para>
    /// </remarks>
    private void RecoverWithBaseline(
        VolumeIdentity volume, StrataSettings settings, ScanCadenceOptions cadence, string cause)
    {
        var decision = _gate.CanScan(ScanTier.Full);

        if (!decision.Allowed)
        {
            var detail = string.IsNullOrEmpty(decision.Detail) ? "" : $" ({decision.Detail})";
            var waiting = $"{cause}; automatic baseline is waiting for an idle " +
                          $"moment — {decision.Reason}{detail}";

            RecoveryWaiting(volume.Display, decision.Reason);
            Record(volume, ScanTier.Full, ScanOutcome.Deferred, waiting);
            Remember(volume, waiting);

            return;
        }

        _cadence.Escalated(volume.GuidPath, Escalation.Baseline, DateTime.UtcNow);
        RecoveryStarting(volume.Display, cause);

        RunBaseline(volume, settings, cadence, automaticCause: cause);
    }

    /// <summary>Writes one tick to the log, the console, and the per-volume status.</summary>
    private void LogTick(VolumeIdentity volume, TickOutcome outcome, bool forced)
    {
        _lastTickUtc = DateTime.UtcNow;

        if (!outcome.Ran)
        {
            TickSkipped(volume.Display, outcome.Deferral);
            Record(volume, outcome.Tier, ScanOutcome.Deferred, outcome.Deferral);
            Remember(volume, outcome.Deferral);

            // Held rather than cleared on the next tick’s start, because the state the UI has to
            // render is "paused because a game is running" and that outlives the tick that decided
            // it — the next tick will decide the same thing for the same reason.
            _lastDeferral = outcome.Deferral;
            return;
        }

        _lastDeferral = "";
        Remember(volume, "");

        // Over-budget ticks are logged rather than absorbed. The 2 s budget is the whole reason the
        // incremental path exists in the shape it does.
        var level = outcome.OverBudget ? LogLevel.Warning : LogLevel.Information;

        TickCompleted(
            level,
            volume.Display,
            outcome.RecordsRead,
            outcome.MetadataReads,
            outcome.Changes.Count,
            outcome.Duration.TotalMilliseconds,
            outcome.CatchingUp ? " (catching up)" : "");

        var note = forced ? "escalated after repeated deferrals · " : "";

        Record(
            volume,
            outcome.Tier,
            ScanOutcome.Ran,
            note + Describe(outcome),
            durationMs: (long)outcome.Duration.TotalMilliseconds,
            records: outcome.RecordsRead,
            events: outcome.Changes.Count);
    }

    /// <summary>A tick’s outcome, in the terms the cadence decides from.</summary>
    private static TickReport ReportOf(TickOutcome outcome)
        => outcome.Ran
            ? outcome.CatchingUp ? TickReport.Behind : TickReport.Complete
            : TickReport.Deferred(outcome.Kind);

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

    /// <summary>
    /// How many entries the last scan of this volume found, or null if there has not been one.
    /// </summary>
    /// <remarks>
    /// The denominator that turns a running baseline into a percentage worth showing. Its absence is
    /// exactly the first-run case, which is why <see cref="ScanActivity"/> falls back to bytes
    /// rather than to no progress at all.
    /// </remarks>
    private long? LastEntryCount(VolumeIdentity volume)
    {
        try
        {
            using var connection = _database.OpenRead();

            var volumeId = VolumeQueries.Resolve(connection, volume.GuidPath);

            if (volumeId is null)
            {
                return null;
            }

            var recent = SnapshotQueries.List(connection, volumeId, null, null, limit: 1);

            return recent.Count == 0 ? null : recent[0].FileCount + recent[0].DirCount;
        }
        catch (Microsoft.Data.Sqlite.SqliteException)
        {
            // A missing denominator costs a percentage. It is not worth failing a scan over.
            return null;
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
                // The enumeration is fresh every cycle and the watcher is not, so it takes the
                // current free space with it into the snapshot this tick writes.
                existing.Volume = volume;
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

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Retention: thinned {history} history rows and dropped {log} log rows in {ms:F0} ms.")]
    private partial void RetentionFinished(int history, int log, double ms);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Retention is misconfigured: {reason}")]
    private partial void RetentionMisconfigured(string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Retention pass failed; history was not thinned.")]
    private partial void RetentionFailed(Exception exception);

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

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "{volume}: deferred {count} times running; ticking anyway rather than letting " +
                  "the journal cursor be trimmed away.")]
    private partial void TickEscalated(string volume, int count);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "{volume}: {cause}; running an automatic baseline to recover.")]
    private partial void RecoveryStarting(string volume, string cause);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "{volume}: an automatic recovery baseline is due, waiting for an idle moment " +
                  "({reason}).")]
    private partial void RecoveryWaiting(string volume, DeferralReason reason);

    /// <summary>Level is a parameter because an over-budget tick is a warning and a normal one is not.</summary>
    [LoggerMessage(
        Message = "{volume}: {records} records, {stats} stats, {events} events in {ms:F0} ms{note}")]
    private partial void TickCompleted(
        LogLevel level, string volume, int records, int stats, int events, double ms, string note);

    /// <summary>The last thing said about a volume, for the status endpoint to report.</summary>
    /// <remarks>
    /// Kept beside the cadence rather than inside it, because the cadence lives in <c>Core</c> and
    /// decides from a volume’s history — it has no business holding a display name or a sentence
    /// written for a person.
    /// </remarks>
    private readonly record struct VolumeNote(string Display, string Key, string Reason);

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
