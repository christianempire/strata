using Strata.Core.Model;
using Strata.Core.Scanning;
using Strata.Tests.Fakes;

namespace Strata.Tests;

/// <summary>
/// The incident, reproduced deliberately rather than waited for.
/// </summary>
/// <remarks>
/// On 19 Aug 2026 C: stopped ticking at 16:26 and was not noticed for 27 hours; E: stopped at 17:56
/// and was not noticed for 25. Both went the same way: ticks that read exactly
/// <c>MaxRecordsPerTick</c> records and reported "catching up", losing ground every time, until
/// Windows trimmed the journal past the stored cursor — after which every tick reported
/// <c>JournalInvalidated</c>, whose remedy is a baseline, and nothing ever ran one.
/// <para>
/// These tests wire the real <see cref="Tier2Scanner"/> and the real <see cref="ScanCadence"/> to a
/// journal that is finite, and run the clock forward. The scale is reduced — a 10,000-record buffer
/// rather than 32 MiB — because the failure is about the ratio between the drain rate and the churn
/// rate, and that ratio is the same at any size.
/// </para>
/// </remarks>
public sealed class JournalRecoveryTests
{
    private static readonly DateTime Start = new(2026, 8, 19, 16, 26, 0, DateTimeKind.Utc);

    /// <summary>Records the journal holds. Stands in for C:'s 32 MiB.</summary>
    private const int Capacity = 10_000;

    /// <summary>The tick cap. Stands in for the default 50,000.</summary>
    private const int Cap = 500;

    /// <summary>Records the volume writes each second. A development machine outruns its scanner.</summary>
    private const int ChurnPerSecond = 2;

    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(900);

    // --- What actually happened. ---

    [Fact]
    public void At_the_ordinary_interval_a_busy_volume_loses_its_cursor()
    {
        // 500 records every 900 s is 0.55 a second against 2 a second written. The scanner cannot
        // win this race at any buffer size; the buffer only decides how long it takes to lose.
        var run = Simulate(new ScanCadenceOptions
        {
            Interval = Interval,
            CatchUpInterval = Interval,
        });

        Assert.True(run.Invalidated, "the journal should have been trimmed past the cursor");
        Assert.True(run.Lost > 0, "records should have gone unread");

        // And the shape of the log the incident actually left: tick after tick at exactly the cap.
        // The first reads nothing, because a cursor taken at the head has nothing behind it yet.
        Assert.All(run.RecordsPerTick.Skip(1), records => Assert.Equal(Cap, records));
    }

    [Fact]
    public void Losing_the_cursor_is_not_something_a_tick_can_recover_from()
    {
        var journal = new FakeCircularJournal { Capacity = Capacity };

        journal.Churn(Capacity * 2);

        // A cursor from before the buffer wrapped.
        var stale = new UsnCursor(0x1DB931EBE461434, 0);

        for (var attempt = 0; attempt < 10; attempt++)
        {
            var result = Scan(journal).Run(stale, new FakeFileStateStore());

            Assert.Equal(UsnReadStatus.JournalInvalidated, result.Status);
            Assert.True(result.RequiresBaseline);
            Assert.Equal(DeferralKind.NeedsBaseline, result.Deferral);
        }

        // Ten identical failures, which is what 101 of them looked like in the log.
        Assert.Equal(10, journal.Invalidations);
    }

    // --- Fix 2: catching up must be able to win. ---

    [Fact]
    public void At_the_catch_up_interval_the_same_volume_keeps_its_cursor()
    {
        // 500 records every 60 s is 8.3 a second against the same 2 a second written.
        var run = Simulate(new ScanCadenceOptions
        {
            Interval = Interval,
            CatchUpInterval = TimeSpan.FromSeconds(60),
        });

        Assert.False(run.Invalidated, "the cursor should have stayed inside the journal");
        Assert.Equal(0, run.Lost);
    }

    [Fact]
    public void Catching_up_returns_to_the_ordinary_interval_once_it_is_caught_up()
    {
        var options = new ScanCadenceOptions
        {
            Interval = Interval,
            CatchUpInterval = TimeSpan.FromSeconds(60),
        };

        var journal = new FakeCircularJournal { Capacity = Capacity };
        var cadence = new ScanCadence();
        var store = new FakeFileStateStore();
        var now = Start;

        // Take a cursor at the head, then drop a game install on the volume behind it.
        var cursor = Scan(journal).Run(UsnCursor.None, store).Cursor;

        journal.Churn(Cap * 4);

        var gaps = new List<TimeSpan>();

        for (var tick = 0; tick < 8; tick++)
        {
            var result = Scan(journal).Run(cursor, store);

            cursor = result.Cursor;
            cadence.Record(
                "C:", result.CatchingUp ? TickReport.Behind : TickReport.Complete, now, options);

            var next = cadence.EarliestDueUtc()!.Value;

            gaps.Add(next - now);
            now = next;
        }

        // Three ticks come back in a minute and the rest at the ordinary interval. Three rather than
        // four because the fourth tick drains the last of the backlog and has nothing left over —
        // the accelerated cadence lasts exactly as long as there is something to catch up on, and
        // not one tick longer.
        Assert.Equal(
            [.. Enumerable.Repeat(options.CatchUpInterval, 3), .. Enumerable.Repeat(Interval, 5)],
            gaps);
    }

    // --- Fix 1: recovering from an invalidated journal, automatically. ---

    [Fact]
    public void A_volume_whose_journal_was_trimmed_recovers_without_anyone_noticing()
    {
        var options = new ScanCadenceOptions { Interval = Interval };
        var journal = new FakeCircularJournal { Capacity = Capacity };
        var cadence = new ScanCadence();
        var store = new FakeFileStateStore();
        var now = Start;

        // A cursor left behind by the buffer wrapping, which is the state C: was found in.
        journal.Churn(Capacity * 2);
        var cursor = new UsnCursor(0x1DB931EBE461434, 0);

        var baselines = 0;

        for (var tick = 0; tick < 8; tick++)
        {
            var result = Scan(journal).Run(cursor, store);

            var report = result.Status == UsnReadStatus.Ok
                ? result.CatchingUp ? TickReport.Behind : TickReport.Complete
                : TickReport.Deferred(result.Deferral);

            if (result.Status == UsnReadStatus.Ok)
            {
                cursor = result.Cursor;
            }

            if (cadence.Record("C:", report, now, options) == Escalation.Baseline)
            {
                baselines++;

                // What a baseline does to the incremental path: it clears the cursor, so the next
                // read starts from the head with nothing to bridge.
                cadence.Escalated("C:", Escalation.Baseline, now);
                cursor = UsnCursor.None;
                cadence.Reset("C:", now, options);
            }

            now += Interval;
        }

        Assert.Equal(1, baselines);

        // And it is genuinely ticking again afterwards, not merely reporting differently.
        journal.Churn(50);

        var resumed = Scan(journal).Run(cursor, store);

        Assert.Equal(UsnReadStatus.Ok, resumed.Status);
        Assert.Equal(50, resumed.RecordsRead);
    }

    [Fact]
    public void A_baseline_that_does_not_help_is_not_retried_every_hour()
    {
        // The loop guard. If the journal is invalidated again immediately — a journal being deleted
        // and recreated in a loop would do it — the recovery must not become the problem.
        var options = new ScanCadenceOptions { Interval = Interval };
        var cadence = new ScanCadence();
        var now = Start;
        var baselines = 0;

        // Twenty-four hours at the default interval.
        for (var tick = 0; tick < 96; tick++)
        {
            if (cadence.Record("C:", TickReport.Deferred(DeferralKind.NeedsBaseline), now, options)
                == Escalation.Baseline)
            {
                baselines++;
                cadence.Escalated("C:", Escalation.Baseline, now);
            }

            now += Interval;
        }

        // Four in a day, at six hours apart, rather than ninety-three.
        Assert.Equal(4, baselines);
    }

    [Fact]
    public void A_game_running_all_day_delays_the_recovery_and_never_skips_it()
    {
        // The resource decision, stated as a test. The scheduler gates the escalated baseline at
        // Tier 1, so a fullscreen game defers it — for as long as the game runs, and not one cycle
        // longer.
        var options = new ScanCadenceOptions { Interval = Interval };
        var cadence = new ScanCadence();
        var gate = new ScriptedGate();
        var now = Start;
        var baselines = 0;

        gate.Allow = false;

        for (var tick = 0; tick < 32; tick++)
        {
            // Eight hours of gaming, then the machine goes idle.
            if (tick == 24)
            {
                gate.Allow = true;
            }

            if (cadence.Record("C:", TickReport.Deferred(DeferralKind.NeedsBaseline), now, options)
                == Escalation.Baseline)
            {
                if (gate.Allow)
                {
                    baselines++;
                    cadence.Escalated("C:", Escalation.Baseline, now);
                }

                // Refused. Deliberately does not report Escalated, so no cooldown starts and the
                // very next cycle asks again — which is what makes the first idle moment the one
                // that is taken.
            }

            now += Interval;
        }

        Assert.Equal(1, baselines);
    }

    // --- Machinery. ---

    private static Tier2Scanner Scan(IUsnJournal journal)
        => new(journal, new AllFilesSameSize(), new Tier2Options { MaxRecordsPerTick = Cap });

    private static Run Simulate(ScanCadenceOptions options)
    {
        var journal = new FakeCircularJournal { Capacity = Capacity };
        var cadence = new ScanCadence();
        var store = new FakeFileStateStore();
        var cursor = UsnCursor.None;
        var now = Start;

        var records = new List<int>();
        var ticksBeforeLoss = 0;
        var invalidated = false;

        // Twenty-seven hours of it, the length of the real outage.
        var end = Start.AddHours(27);

        while (now < end)
        {
            var next = cadence.EarliestDueUtc() ?? now;
            var elapsed = next > now ? next - now : TimeSpan.Zero;

            journal.Churn((int)(elapsed.TotalSeconds * ChurnPerSecond));
            now = next > now ? next : now;

            var result = Scan(journal).Run(cursor, store);

            if (result.Status != UsnReadStatus.Ok)
            {
                invalidated = true;
                break;
            }

            cursor = result.Cursor;
            records.Add(result.RecordsRead);
            ticksBeforeLoss++;

            cadence.Record(
                "C:", result.CatchingUp ? TickReport.Behind : TickReport.Complete, now, options);
        }

        return new Run(invalidated, journal.Lost, records, ticksBeforeLoss);
    }

    private sealed record Run(
        bool Invalidated, int Lost, IReadOnlyList<int> RecordsPerTick, int TicksBeforeLoss);

    /// <summary>Every file exists and is a kilobyte. The sizes are not what these tests are about.</summary>
    private sealed class AllFilesSameSize : IFileMetadataReader
    {
        public bool TryReadById(long fileId, out FileMetadata metadata)
        {
            metadata = new FileMetadata(1024, 1024, 0x80, DateTime.UnixEpoch, 1);
            return true;
        }
    }

    private sealed class ScriptedGate
    {
        internal bool Allow { get; set; } = true;
    }
}
