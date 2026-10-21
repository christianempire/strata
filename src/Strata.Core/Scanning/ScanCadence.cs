namespace Strata.Core.Scanning;

/// <summary>What kind of remedy a deferred tick needs, if any.</summary>
/// <remarks>
/// Coarser than <see cref="DeferralReason"/> on purpose. The gate's enum says what Windows was
/// doing; this says what the scheduler can do about it, and several distinct gate reasons call for
/// exactly the same answer.
/// </remarks>
public enum DeferralKind
{
    /// <summary>The tick ran.</summary>
    None = 0,

    /// <summary>Suspended by a person. Never escalated: a decision is not a condition to recover from.</summary>
    Paused,

    /// <summary>A game or a presentation is on screen. Never escalated — see <see cref="ScanCadence"/>.</summary>
    Presentation,

    /// <summary>Busy CPU or recent input. The tick could have run; now was a poor moment for it.</summary>
    Busy,

    /// <summary>The journal cannot bridge the gap. Only a baseline fixes it.</summary>
    NeedsBaseline,

    /// <summary>Nothing automatic fixes it: no journal at all, no first baseline, no permission.</summary>
    NeedsPerson,
}

/// <summary>What one attempt on one volume amounted to, in the terms the cadence decides from.</summary>
public readonly record struct TickReport(DeferralKind Deferral, bool CatchingUp)
{
    /// <summary>A tick that ran and drained everything the journal held.</summary>
    public static readonly TickReport Complete = new(DeferralKind.None, CatchingUp: false);

    /// <summary>A tick that ran, hit its record cap, and left more behind.</summary>
    public static readonly TickReport Behind = new(DeferralKind.None, CatchingUp: true);

    public static TickReport Deferred(DeferralKind kind) => new(kind, CatchingUp: false);

    public bool Ran => Deferral == DeferralKind.None;
}

/// <summary>What the scheduler should do about a volume that keeps not ticking.</summary>
public enum Escalation
{
    None = 0,

    /// <summary>Run the tick, ignoring the gate that keeps refusing it. Milliseconds of work.</summary>
    TickAnyway,

    /// <summary>Queue a full baseline. Minutes of work, so the caller still puts it through the gate.</summary>
    Baseline,
}

/// <summary>Tunables for how often a volume is ticked and when a stuck one is forced.</summary>
public sealed record ScanCadenceOptions
{
    /// <summary>The ordinary gap between ticks on a volume that is keeping up.</summary>
    public TimeSpan Interval { get; init; } = TimeSpan.FromSeconds(900);

    /// <summary>
    /// The gap after a tick that hit its record cap.
    /// </summary>
    /// <remarks>
    /// Catching up at the ordinary interval is rate-limited below the change rate by construction:
    /// 50,000 records every 900 s is 55 records a second, and a development volume generates more
    /// than that. A volume that is behind is ticked until it is not.
    /// </remarks>
    public TimeSpan CatchUpInterval { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Accelerated ticks in a row before one ordinary interval is taken anyway.
    /// </summary>
    /// <remarks>
    /// The bound on the accelerated path. A volume that is permanently behind — because it churns
    /// faster than any cap can drain, or because something is wrong — settles into bursts with a
    /// pause between them rather than holding the scheduler at the short interval forever.
    /// </remarks>
    public int CatchUpBurst { get; init; } = 30;

    /// <summary>Consecutive deferrals with the same remedy before the scheduler forces the issue.</summary>
    /// <remarks>Four, from the Settings mockup. At the default interval that is an hour.</remarks>
    public int DeferralsBeforeEscalating { get; init; } = 4;

    /// <summary>
    /// The shortest gap between two automatic baselines of the same volume.
    /// </summary>
    /// <remarks>
    /// The loop guard. A baseline that does not fix the problem — because the journal is invalidated
    /// again before the next tick, or because the scan itself fails — would otherwise re-arm the
    /// escalation an hour later and keep re-arming it. Six hours bounds an automatic recovery to
    /// four baselines a day, which on the target machine is fourteen minutes of disk, taken only in
    /// idle moments.
    /// </remarks>
    public TimeSpan BaselineCooldown { get; init; } = TimeSpan.FromHours(6);
}

/// <summary>Where one volume stands, for a caller reporting it rather than deciding from it.</summary>
public sealed record VolumeCadenceState
{
    public required string Volume { get; init; }

    /// <summary>When this volume last ticked successfully. Null until it has.</summary>
    public DateTime? LastSuccessUtc { get; init; }

    public DateTime NextDueUtc { get; init; }

    /// <summary>What stopped the last attempt, or None when it ran.</summary>
    public DeferralKind Deferral { get; init; }

    /// <summary>Attempts in a row that did not run. Zero after any tick that did.</summary>
    public int ConsecutiveDeferrals { get; init; }

    /// <summary>The last tick ran but left records behind, so the next one comes sooner.</summary>
    public bool CatchingUp { get; init; }
}

/// <summary>
/// When each volume is next due a tick, and when a volume that keeps not ticking gets forced.
/// </summary>
/// <remarks>
/// Extracted from the scheduler so both decisions can be tested without a disk, a database or a
/// clock. It holds per-volume counters and nothing else; the caller supplies the time and acts on
/// the answer.
/// <para>
/// <b>Why a deferral is not simply overridden after four tries.</b> The Settings mockup says a tick
/// deferred more than four times in a row escalates to a low-priority scan anyway, so history does
/// not develop a hole. Taken literally that would run a scan while a game is fullscreen, which the
/// project treats as a failure rather than a bug. So the escalation is split by what the deferral
/// actually needs:
/// </para>
/// <list type="bullet">
/// <item>Busy CPU or recent input escalates to a tick that ignores the gate. A tick is milliseconds
/// at very low I/O priority, and the thing it protects against — a cursor drifting off the back of a
/// finite journal — is permanent data loss.</item>
/// <item>A game or a presentation on screen never escalates. That is the first non-negotiable, and
/// no amount of missing history outranks it.</item>
/// <item>An invalidated journal escalates to a full baseline, which the caller still puts through
/// the gate: it is minutes of sustained I/O and must wait for the machine to be idle.</item>
/// </list>
/// <para>
/// The tension that leaves — deferring through a long gaming session is exactly how a journal wraps
/// — is resolved by admitting we cannot prevent the wrap without risking frames, and guaranteeing
/// instead that we notice and recover from it. That guarantee is what was missing when C: went 27
/// hours without a tick and E: went 25.
/// </para>
/// </remarks>
public sealed class ScanCadence
{
    private readonly Dictionary<string, VolumeCadence> _volumes = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Whether this volume should be ticked now.
    /// </summary>
    /// <remarks>
    /// A volume never seen before is due immediately, which is what makes a newly attached disk tick
    /// on the cycle it appears rather than after an interval of nothing.
    /// </remarks>
    public bool IsDue(string volume, DateTime utcNow)
        => !_volumes.TryGetValue(volume, out var state) || utcNow >= state.NextDueUtc;

    /// <summary>When the first tracked volume next falls due, or null when none is tracked yet.</summary>
    public DateTime? EarliestDueUtc()
    {
        DateTime? earliest = null;

        foreach (var state in _volumes.Values)
        {
            if (earliest is null || state.NextDueUtc < earliest)
            {
                earliest = state.NextDueUtc;
            }
        }

        return earliest;
    }

    /// <summary>Every tracked volume's standing, for the status endpoint.</summary>
    public IReadOnlyList<VolumeCadenceState> Snapshot()
    {
        var rows = new List<VolumeCadenceState>(_volumes.Count);

        foreach (var (volume, state) in _volumes)
        {
            rows.Add(new VolumeCadenceState
            {
                Volume = volume,
                LastSuccessUtc = state.LastSuccessUtc,
                NextDueUtc = state.NextDueUtc,
                Deferral = state.Deferral,
                ConsecutiveDeferrals = state.Run,
                CatchingUp = state.CatchingUp,
            });
        }

        return rows;
    }

    /// <summary>
    /// Records one attempt, sets when the volume is next due, and says whether to force the issue.
    /// </summary>
    /// <remarks>
    /// Returning the escalation rather than performing it keeps this side of the decision free of
    /// everything that makes the scheduler hard to test. The caller reports back through
    /// <see cref="Escalated"/> only if the escalated work actually started — a baseline the gate
    /// refuses has not happened, and must not start its own cooldown.
    /// </remarks>
    public Escalation Record(
        string volume, TickReport report, DateTime utcNow, ScanCadenceOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (!_volumes.TryGetValue(volume, out var state))
        {
            state = new VolumeCadence();
            _volumes[volume] = state;
        }

        state.Deferral = report.Deferral;
        state.CatchingUp = report.CatchingUp;

        if (report.Ran)
        {
            // A tick that ran is the only thing that proves a condition has lifted, so it is the
            // only thing that clears the counters.
            state.BusyRun = 0;
            state.BaselineRun = 0;
            state.LastSuccessUtc = utcNow;
            state.NextDueUtc = utcNow + PaceFor(state, report, options);

            return Escalation.None;
        }

        state.NextDueUtc = utcNow + options.Interval;
        state.CatchUpBurst = 0;

        // Counted per remedy, and one kind of deferral never clears another kind's count. A gate
        // that refused to start the tick carries no information about whether the journal is still
        // unreadable — it means we did not look. Resetting on it is how E: would have had its
        // recovery pushed back four more cycles by a game that started after the journal was
        // already lost.
        var wanted = RemedyFor(report.Deferral);

        var run = wanted switch
        {
            Escalation.TickAnyway => ++state.BusyRun,
            Escalation.Baseline => ++state.BaselineRun,
            _ => 0,
        };

        state.Run = run;

        if (wanted == Escalation.None || run < options.DeferralsBeforeEscalating)
        {
            return Escalation.None;
        }

        if (wanted == Escalation.Baseline && state.LastBaselineUtc is { } last
            && utcNow - last < options.BaselineCooldown)
        {
            // Already tried within the cooldown. Something is wrong that a second baseline will not
            // fix, and repeating it every hour would make the scanner the problem.
            return Escalation.None;
        }

        return wanted;
    }

    /// <summary>
    /// Whether an automatic baseline may start now, for a caller that has found its own reason for
    /// one rather than been offered it by <see cref="Record"/>.
    /// </summary>
    /// <remarks>
    /// The same cooldown as the journal recovery, shared with it: whatever the reason, a baseline
    /// that did not help is not repeated within <see cref="ScanCadenceOptions.BaselineCooldown"/>.
    /// </remarks>
    public bool MayRebaseline(string volume, DateTime utcNow, ScanCadenceOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        return !_volumes.TryGetValue(volume, out var state)
               || state.LastBaselineUtc is not { } last
               || utcNow - last >= options.BaselineCooldown;
    }

    /// <summary>Tells the cadence that an escalation actually started, which is what rate-limits it.</summary>
    public void Escalated(string volume, Escalation escalation, DateTime utcNow)
    {
        if (!_volumes.TryGetValue(volume, out var state))
        {
            return;
        }

        // The run resets: the escalation is the response to it, and letting it keep climbing would
        // fire again on the very next deferral. Only the one that fired, though — a forced tick says
        // nothing about whether the journal can be resumed.
        if (escalation == Escalation.Baseline)
        {
            state.BaselineRun = 0;
            state.Run = 0;
            state.LastBaselineUtc = utcNow;
        }
        else if (escalation == Escalation.TickAnyway)
        {
            state.BusyRun = 0;
            state.Run = 0;
        }
    }

    /// <summary>Forgets a volume's counters, as a baseline asked for by hand does.</summary>
    /// <remarks>
    /// Deliberately does not clear the automatic-baseline cooldown. A person running
    /// <c>strata scan</c> resets the situation; it does not license the scheduler to start running
    /// baselines of its own accord again straight away.
    /// </remarks>
    public void Reset(string volume, DateTime utcNow, ScanCadenceOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (!_volumes.TryGetValue(volume, out var state))
        {
            state = new VolumeCadence();
            _volumes[volume] = state;
        }

        state.Run = 0;
        state.BusyRun = 0;
        state.BaselineRun = 0;
        state.Deferral = DeferralKind.None;
        state.CatchingUp = false;
        state.CatchUpBurst = 0;
        state.LastSuccessUtc = utcNow;
        state.NextDueUtc = utcNow + options.Interval;
    }

    /// <summary>Drops a volume entirely, as one that has gone away should be.</summary>
    public void Forget(string volume) => _volumes.Remove(volume);

    /// <summary>How long until the next tick on a volume that just ran.</summary>
    private static TimeSpan PaceFor(VolumeCadence state, TickReport report, ScanCadenceOptions options)
    {
        if (!report.CatchingUp)
        {
            state.CatchUpBurst = 0;
            return options.Interval;
        }

        if (state.CatchUpBurst >= options.CatchUpBurst)
        {
            // The burst is spent. One ordinary interval, then the next accelerated run starts fresh.
            state.CatchUpBurst = 0;
            return options.Interval;
        }

        state.CatchUpBurst++;
        return options.CatchUpInterval;
    }

    private static Escalation RemedyFor(DeferralKind kind) => kind switch
    {
        DeferralKind.Busy => Escalation.TickAnyway,
        DeferralKind.NeedsBaseline => Escalation.Baseline,
        _ => Escalation.None,
    };

    /// <summary>Mutable per-volume counters. Private, so the public surface stays immutable records.</summary>
    private sealed class VolumeCadence
    {
        internal DateTime NextDueUtc { get; set; } = DateTime.MinValue;

        internal DateTime? LastSuccessUtc { get; set; }

        internal DateTime? LastBaselineUtc { get; set; }

        internal DeferralKind Deferral { get; set; }

        /// <summary>Deferrals in a row that a forced tick would answer.</summary>
        internal int BusyRun { get; set; }

        /// <summary>Deferrals in a row that only a baseline would answer.</summary>
        internal int BaselineRun { get; set; }

        /// <summary>Whichever of the two the last deferral belonged to. For reporting only.</summary>
        internal int Run { get; set; }

        internal int CatchUpBurst { get; set; }

        internal bool CatchingUp { get; set; }
    }
}
