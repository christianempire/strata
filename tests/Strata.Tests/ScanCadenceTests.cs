using Strata.Core.Scanning;

namespace Strata.Tests;

/// <summary>
/// The escalation and pacing rules, driven by hand.
/// </summary>
/// <remarks>
/// These exist because the failure they guard against took 27 hours to appear on a real machine and
/// left no error behind: every tick reported the same deferral, and nothing ever acted on it.
/// Waiting for it again is not a test strategy.
/// </remarks>
public sealed class ScanCadenceTests
{
    private static readonly DateTime Start = new(2026, 8, 20, 12, 0, 0, DateTimeKind.Utc);

    private static readonly ScanCadenceOptions Defaults = new();

    [Fact]
    public void A_volume_never_seen_is_due_immediately()
    {
        var cadence = new ScanCadence();

        Assert.True(cadence.IsDue("C:", Start));
    }

    [Fact]
    public void A_volume_that_just_ticked_is_not_due_again_until_the_interval_passes()
    {
        var cadence = new ScanCadence();

        cadence.Record("C:", TickReport.Complete, Start, Defaults);

        Assert.False(cadence.IsDue("C:", Start.AddSeconds(899)));
        Assert.True(cadence.IsDue("C:", Start.AddSeconds(900)));
    }

    // --- The incident: an invalidated journal that nothing ever acted on. ---

    [Fact]
    public void Four_journal_invalidations_in_a_row_escalate_to_a_baseline()
    {
        var cadence = new ScanCadence();
        var now = Start;

        for (var attempt = 1; attempt <= 3; attempt++)
        {
            var early = cadence.Record(
                "C:", TickReport.Deferred(DeferralKind.NeedsBaseline), now, Defaults);

            Assert.Equal(Escalation.None, early);
            now = now.AddSeconds(900);
        }

        var fourth = cadence.Record(
            "C:", TickReport.Deferred(DeferralKind.NeedsBaseline), now, Defaults);

        Assert.Equal(Escalation.Baseline, fourth);
    }

    [Fact]
    public void The_hundredth_invalidation_still_escalates_when_none_has_fired()
    {
        // The state the machine was actually found in: 101 deferrals, no escalation. A gate that
        // refuses every attempt must not exhaust the scheduler's willingness to try again.
        var cadence = new ScanCadence();
        var now = Start;
        var escalations = 0;

        for (var attempt = 0; attempt < 101; attempt++)
        {
            if (cadence.Record("C:", TickReport.Deferred(DeferralKind.NeedsBaseline), now, Defaults)
                == Escalation.Baseline)
            {
                escalations++;
            }

            now = now.AddSeconds(900);
        }

        // Never fired, because the caller never reported one starting — so it keeps offering.
        Assert.Equal(101 - 3, escalations);
    }

    [Fact]
    public void An_escalation_that_fired_does_not_fire_again_inside_the_cooldown()
    {
        var cadence = new ScanCadence();
        var now = Start;

        for (var attempt = 0; attempt < 4; attempt++)
        {
            cadence.Record("C:", TickReport.Deferred(DeferralKind.NeedsBaseline), now, Defaults);
            now = now.AddSeconds(900);
        }

        cadence.Escalated("C:", Escalation.Baseline, now);

        // The baseline did not fix it: the journal is invalidated again on every subsequent tick.
        var fired = 0;

        for (var attempt = 0; attempt < 40; attempt++)
        {
            if (cadence.Record("C:", TickReport.Deferred(DeferralKind.NeedsBaseline), now, Defaults)
                == Escalation.Baseline)
            {
                fired++;
                cadence.Escalated("C:", Escalation.Baseline, now);
            }

            now = now.AddSeconds(900);
        }

        // Forty ticks is ten hours, which contains exactly one more six-hour window.
        Assert.Equal(1, fired);
    }

    [Fact]
    public void The_cooldown_expires_and_recovery_is_attempted_again()
    {
        var cadence = new ScanCadence();

        for (var attempt = 0; attempt < 4; attempt++)
        {
            cadence.Record("C:", TickReport.Deferred(DeferralKind.NeedsBaseline), Start, Defaults);
        }

        cadence.Escalated("C:", Escalation.Baseline, Start);

        var later = Start.AddHours(6).AddMinutes(1);

        for (var attempt = 0; attempt < 3; attempt++)
        {
            cadence.Record("C:", TickReport.Deferred(DeferralKind.NeedsBaseline), later, Defaults);
        }

        Assert.Equal(
            Escalation.Baseline,
            cadence.Record("C:", TickReport.Deferred(DeferralKind.NeedsBaseline), later, Defaults));
    }

    [Fact]
    public void A_successful_tick_clears_the_run_so_the_next_deferral_starts_from_one()
    {
        var cadence = new ScanCadence();
        var now = Start;

        for (var attempt = 0; attempt < 3; attempt++)
        {
            cadence.Record("C:", TickReport.Deferred(DeferralKind.NeedsBaseline), now, Defaults);
            now = now.AddSeconds(900);
        }

        cadence.Record("C:", TickReport.Complete, now, Defaults);
        now = now.AddSeconds(900);

        Assert.Equal(
            Escalation.None,
            cadence.Record("C:", TickReport.Deferred(DeferralKind.NeedsBaseline), now, Defaults));
    }

    // --- The first non-negotiable: a game on screen is never overridden. ---

    [Fact]
    public void A_game_on_screen_never_escalates_however_long_it_runs()
    {
        var cadence = new ScanCadence();
        var now = Start;

        // Twenty-four hours of fullscreen, at the default interval.
        for (var attempt = 0; attempt < 96; attempt++)
        {
            Assert.Equal(
                Escalation.None,
                cadence.Record("C:", TickReport.Deferred(DeferralKind.Presentation), now, Defaults));

            now = now.AddSeconds(900);
        }
    }

    [Fact]
    public void Being_paused_never_escalates()
    {
        var cadence = new ScanCadence();

        for (var attempt = 0; attempt < 20; attempt++)
        {
            Assert.Equal(
                Escalation.None,
                cadence.Record("C:", TickReport.Deferred(DeferralKind.Paused), Start, Defaults));
        }
    }

    [Fact]
    public void A_volume_with_no_journal_at_all_waits_for_a_person()
    {
        var cadence = new ScanCadence();

        for (var attempt = 0; attempt < 20; attempt++)
        {
            Assert.Equal(
                Escalation.None,
                cadence.Record("D:", TickReport.Deferred(DeferralKind.NeedsPerson), Start, Defaults));
        }
    }

    [Fact]
    public void A_busy_machine_escalates_to_a_tick_that_ignores_the_gate()
    {
        var cadence = new ScanCadence();
        var now = Start;
        Escalation last = Escalation.None;

        for (var attempt = 0; attempt < 4; attempt++)
        {
            last = cadence.Record("C:", TickReport.Deferred(DeferralKind.Busy), now, Defaults);
            now = now.AddSeconds(900);
        }

        Assert.Equal(Escalation.TickAnyway, last);
    }

    [Fact]
    public void A_game_interrupting_a_busy_run_restarts_it()
    {
        var cadence = new ScanCadence();
        var now = Start;

        cadence.Record("C:", TickReport.Deferred(DeferralKind.Busy), now, Defaults);
        cadence.Record("C:", TickReport.Deferred(DeferralKind.Busy), now, Defaults);
        cadence.Record("C:", TickReport.Deferred(DeferralKind.Busy), now, Defaults);

        // A game appears. The situation changed, so the run does not simply continue.
        cadence.Record("C:", TickReport.Deferred(DeferralKind.Presentation), now, Defaults);

        Assert.Equal(
            Escalation.None,
            cadence.Record("C:", TickReport.Deferred(DeferralKind.Busy), now, Defaults));
    }

    [Fact]
    public void Cpu_busy_and_recent_input_count_as_one_run_because_the_remedy_is_the_same()
    {
        // Both arrive as Busy; the point of the test is that the run survives the alternation the
        // real gate produces, where a machine under load is also a machine being typed at.
        var cadence = new ScanCadence();

        cadence.Record("C:", TickReport.Deferred(DeferralKind.Busy), Start, Defaults);
        cadence.Record("C:", TickReport.Deferred(DeferralKind.Busy), Start, Defaults);
        cadence.Record("C:", TickReport.Deferred(DeferralKind.Busy), Start, Defaults);

        Assert.Equal(
            Escalation.TickAnyway,
            cadence.Record("C:", TickReport.Deferred(DeferralKind.Busy), Start, Defaults));
    }

    [Fact]
    public void A_forced_tick_does_not_immediately_force_another()
    {
        var cadence = new ScanCadence();

        for (var attempt = 0; attempt < 4; attempt++)
        {
            cadence.Record("C:", TickReport.Deferred(DeferralKind.Busy), Start, Defaults);
        }

        cadence.Escalated("C:", Escalation.TickAnyway, Start);

        Assert.Equal(
            Escalation.None,
            cadence.Record("C:", TickReport.Deferred(DeferralKind.Busy), Start, Defaults));
    }

    // --- Catching up. ---

    [Fact]
    public void A_tick_that_left_records_behind_comes_back_at_the_short_interval()
    {
        var cadence = new ScanCadence();

        cadence.Record("C:", TickReport.Behind, Start, Defaults);

        Assert.False(cadence.IsDue("C:", Start.AddSeconds(59)));
        Assert.True(cadence.IsDue("C:", Start.AddSeconds(60)));
    }

    [Fact]
    public void Catching_up_drains_faster_than_the_volume_can_fill()
    {
        // The arithmetic the incident turned on. At 50,000 records every 900 s the scanner drains
        // 55 records a second; C: was measured generating more. At 60 s it drains 833.
        var options = Defaults;

        Assert.True(
            options.CatchUpInterval < options.Interval,
            "catching up at the ordinary interval can only ever lose");

        var ordinary = 50_000 / options.Interval.TotalSeconds;
        var accelerated = 50_000 / options.CatchUpInterval.TotalSeconds;

        Assert.Equal(55.6, ordinary, 1);
        Assert.Equal(833.3, accelerated, 1);
    }

    [Fact]
    public void The_accelerated_burst_is_bounded()
    {
        var cadence = new ScanCadence();
        var now = Start;
        var accelerated = 0;

        // Permanently behind: every tick hits the cap.
        for (var attempt = 0; attempt < 31; attempt++)
        {
            cadence.Record("C:", TickReport.Behind, now, Defaults);

            var next = cadence.EarliestDueUtc()!.Value;

            if (next - now < Defaults.Interval)
            {
                accelerated++;
            }

            now = next;
        }

        Assert.Equal(Defaults.CatchUpBurst, accelerated);
    }

    [Fact]
    public void A_spent_burst_starts_a_fresh_one_rather_than_giving_up()
    {
        var cadence = new ScanCadence();
        var now = Start;

        for (var attempt = 0; attempt < Defaults.CatchUpBurst; attempt++)
        {
            cadence.Record("C:", TickReport.Behind, now, Defaults);
            now = cadence.EarliestDueUtc()!.Value;
        }

        // The breather.
        cadence.Record("C:", TickReport.Behind, now, Defaults);
        Assert.Equal(now + Defaults.Interval, cadence.EarliestDueUtc());
        now = cadence.EarliestDueUtc()!.Value;

        // And then accelerated again.
        cadence.Record("C:", TickReport.Behind, now, Defaults);
        Assert.Equal(now + Defaults.CatchUpInterval, cadence.EarliestDueUtc());
    }

    [Fact]
    public void Catching_up_stops_the_moment_the_journal_is_drained()
    {
        var cadence = new ScanCadence();

        cadence.Record("C:", TickReport.Behind, Start, Defaults);
        cadence.Record("C:", TickReport.Complete, Start, Defaults);

        Assert.Equal(Start + Defaults.Interval, cadence.EarliestDueUtc());
    }

    // --- Per-volume independence: fixing C: must not disturb D: and E:. ---

    [Fact]
    public void One_volume_catching_up_does_not_accelerate_the_others()
    {
        var cadence = new ScanCadence();

        cadence.Record("C:", TickReport.Behind, Start, Defaults);
        cadence.Record("D:", TickReport.Complete, Start, Defaults);
        cadence.Record("E:", TickReport.Complete, Start, Defaults);

        var soon = Start.AddSeconds(60);

        Assert.True(cadence.IsDue("C:", soon));
        Assert.False(cadence.IsDue("D:", soon));
        Assert.False(cadence.IsDue("E:", soon));
    }

    [Fact]
    public void One_volume_escalating_does_not_escalate_the_others()
    {
        var cadence = new ScanCadence();
        var now = Start;

        for (var attempt = 0; attempt < 4; attempt++)
        {
            cadence.Record("E:", TickReport.Deferred(DeferralKind.NeedsBaseline), now, Defaults);
            cadence.Record("D:", TickReport.Complete, now, Defaults);
            now = now.AddSeconds(900);
        }

        Assert.Equal(
            Escalation.None,
            cadence.Record("D:", TickReport.Deferred(DeferralKind.NeedsBaseline), now, Defaults));
    }

    [Fact]
    public void The_earliest_due_volume_is_what_the_loop_waits_for()
    {
        var cadence = new ScanCadence();

        cadence.Record("D:", TickReport.Complete, Start, Defaults);
        cadence.Record("C:", TickReport.Behind, Start, Defaults);

        Assert.Equal(Start + Defaults.CatchUpInterval, cadence.EarliestDueUtc());
    }

    // --- Reporting. ---

    [Fact]
    public void A_snapshot_reports_the_last_success_and_the_standing_deferral()
    {
        var cadence = new ScanCadence();

        cadence.Record("C:", TickReport.Complete, Start, Defaults);
        cadence.Record("C:", TickReport.Deferred(DeferralKind.NeedsBaseline), Start.AddHours(1), Defaults);
        cadence.Record("C:", TickReport.Deferred(DeferralKind.NeedsBaseline), Start.AddHours(2), Defaults);

        var row = Assert.Single(cadence.Snapshot());

        Assert.Equal("C:", row.Volume);
        Assert.Equal(Start, row.LastSuccessUtc);
        Assert.Equal(DeferralKind.NeedsBaseline, row.Deferral);
        Assert.Equal(2, row.ConsecutiveDeferrals);
        Assert.False(row.CatchingUp);
    }

    [Fact]
    public void A_reset_clears_the_run_but_not_the_baseline_cooldown()
    {
        var cadence = new ScanCadence();

        for (var attempt = 0; attempt < 4; attempt++)
        {
            cadence.Record("C:", TickReport.Deferred(DeferralKind.NeedsBaseline), Start, Defaults);
        }

        cadence.Escalated("C:", Escalation.Baseline, Start);
        cadence.Reset("C:", Start, Defaults);

        for (var attempt = 0; attempt < 4; attempt++)
        {
            Assert.Equal(
                Escalation.None,
                cadence.Record("C:", TickReport.Deferred(DeferralKind.NeedsBaseline), Start, Defaults));
        }
    }

    [Fact]
    public void A_forgotten_volume_is_due_again_from_scratch()
    {
        var cadence = new ScanCadence();

        cadence.Record("F:", TickReport.Complete, Start, Defaults);
        Assert.False(cadence.IsDue("F:", Start));

        cadence.Forget("F:");
        Assert.True(cadence.IsDue("F:", Start));
        Assert.Empty(cadence.Snapshot());
    }
}
