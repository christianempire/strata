using Strata.Core.Retention;

namespace Strata.Tests;

/// <summary>
/// The downsampling rule, and the invariant that makes it safe to run against real history.
/// </summary>
/// <remarks>
/// <c>dir_history</c> is sparse and absolute: "the size of X at T" is the latest row at or before T.
/// A retention pass that deletes a directory's last row before the horizon therefore does not lose
/// one measurement — it makes every query in that whole period answer with a newer size, silently,
/// looking exactly like a correct answer. That is the failure these tests exist to prevent, and it
/// is unobservable from the outside, so it has to be pinned from the inside.
/// </remarks>
public sealed class RetentionPolicyTests
{
    private static readonly DateTime Now = new(2026, 8, 20, 12, 0, 0, DateTimeKind.Utc);

    private static readonly RetentionTiers Defaults = new();

    private static readonly RetentionHorizons Horizons = RetentionHorizons.From(Now, Defaults);

    private static long At(double daysAgo)
        => (long)(Now.AddDays(-daysAgo) - DateTime.UnixEpoch).TotalSeconds;

    // --- Which tier a measurement falls into. ---

    [Theory]
    [InlineData(0, RetentionResolution.Full)]
    [InlineData(6.9, RetentionResolution.Full)]
    [InlineData(7.1, RetentionResolution.Hourly)]
    [InlineData(29.9, RetentionResolution.Hourly)]
    [InlineData(30.1, RetentionResolution.Daily)]
    [InlineData(364.9, RetentionResolution.Daily)]
    [InlineData(365.1, RetentionResolution.Weekly)]
    [InlineData(4000, RetentionResolution.Weekly)]
    public void Age_decides_the_resolution(double daysAgo, RetentionResolution expected)
    {
        Assert.Equal(expected, RetentionPolicy.ResolutionFor(At(daysAgo), Horizons));
    }

    [Fact]
    public void The_opening_seed_rows_are_weekly_rather_than_undefined()
    {
        // Migration 005 dated its opening row per directory at the Unix epoch, so retention meets
        // timestamps of exactly zero on a real database and must have an answer for them.
        Assert.Equal(RetentionResolution.Weekly, RetentionPolicy.ResolutionFor(0, Horizons));
        Assert.Equal(0, RetentionPolicy.BucketStart(0, RetentionResolution.Weekly));
    }

    // --- Buckets. ---

    [Fact]
    public void Buckets_are_aligned_to_the_epoch_not_to_the_run()
    {
        // If a bucket moved with the clock, a different set of rows would be thinned every night
        // and eventually all of them would be. Two runs an hour apart must agree.
        var taken = At(10);

        var early = RetentionHorizons.From(Now, Defaults);
        var late = RetentionHorizons.From(Now.AddHours(3), Defaults);

        Assert.Equal(
            RetentionPolicy.BucketStart(taken, RetentionPolicy.ResolutionFor(taken, early)),
            RetentionPolicy.BucketStart(taken, RetentionPolicy.ResolutionFor(taken, late)));
    }

    [Fact]
    public void A_bucket_start_is_divisible_by_its_own_span()
    {
        foreach (var resolution in new[]
                 {
                     RetentionResolution.Hourly, RetentionResolution.Daily, RetentionResolution.Weekly,
                 })
        {
            var span = RetentionPolicy.BucketSeconds(resolution);
            var start = RetentionPolicy.BucketStart(At(100) + 1234, resolution);

            Assert.Equal(0, start % span);
        }
    }

    [Fact]
    public void A_full_resolution_measurement_is_its_own_bucket()
    {
        var taken = At(1);

        Assert.Equal(0, RetentionPolicy.BucketSeconds(RetentionResolution.Full));
        Assert.Equal(taken, RetentionPolicy.BucketStart(taken, RetentionResolution.Full));
    }

    // --- What survives. ---

    [Fact]
    public void Nothing_inside_the_full_resolution_window_is_touched()
    {
        // 96 measurements a day for six days, which is what a 15-minute cadence produces.
        var taken = new List<long>();

        for (var i = 0; i < 96 * 6; i++)
        {
            taken.Add(At(6) + i * 900);
        }

        Assert.Equal(taken.Count, RetentionPolicy.Survivors(taken, Horizons).Count);
    }

    [Fact]
    public void An_hour_of_measurements_at_ten_days_old_becomes_one()
    {
        var hour = At(10);
        var taken = new List<long>();

        // Four ticks in the same hour.
        for (var i = 0; i < 4; i++)
        {
            taken.Add(hour + i * 900);
        }

        var survivors = RetentionPolicy.Survivors(taken, Horizons);

        Assert.Single(survivors);

        // The newest, so a query anywhere in the hour reads the hour's final state rather than its
        // first — which is what "the size at the end of that hour" means.
        Assert.Equal(taken[^1], survivors[0]);
    }

    [Fact]
    public void The_newest_measurement_is_never_deleted()
    {
        // The invariant everything else rests on. A directory whose newest row went would answer
        // every future query from whatever came before it.
        var taken = new List<long> { 0, At(2000), At(400), At(100), At(20), At(3) };

        Assert.Contains(taken.Max(), RetentionPolicy.Survivors(taken, Horizons));
    }

    [Fact]
    public void A_directory_with_one_measurement_keeps_it_however_old()
    {
        // The case that would quietly erase a folder from history: written once, never touched
        // again, and old enough to be thinned. Nothing about "one row per bucket" may round to zero.
        foreach (var age in new double[] { 8, 40, 400, 5000 })
        {
            var only = At(age);

            Assert.Equal([only], RetentionPolicy.Survivors([only], Horizons));
        }
    }

    [Fact]
    public void Every_bucket_that_had_a_measurement_still_has_exactly_one()
    {
        // Two years of a busy directory: four measurements an hour, every hour.
        var taken = new List<long>();

        for (var hours = 0; hours < 24 * 730; hours++)
        {
            for (var i = 0; i < 4; i++)
            {
                taken.Add(At(730) + hours * 3600 + i * 900);
            }
        }

        var survivors = RetentionPolicy.Survivors(taken, Horizons);
        var buckets = new HashSet<(RetentionResolution, long)>();

        foreach (var t in taken)
        {
            var resolution = RetentionPolicy.ResolutionFor(t, Horizons);
            buckets.Add((resolution, RetentionPolicy.BucketStart(t, resolution)));
        }

        Assert.Equal(buckets.Count, survivors.Count);
    }

    [Fact]
    public void Two_years_of_a_busy_directory_thins_by_two_orders_of_magnitude()
    {
        var taken = new List<long>();

        for (var hours = 0; hours < 24 * 730; hours++)
        {
            for (var i = 0; i < 4; i++)
            {
                taken.Add(At(730) + hours * 3600 + i * 900);
            }
        }

        var survivors = RetentionPolicy.Survivors(taken, Horizons);

        // 70,080 measurements down to: 672 full + 552 hourly + 335 daily + 52 weekly, near enough.
        Assert.Equal(70_080, taken.Count);
        Assert.InRange(survivors.Count, 1_500, 1_800);
    }

    [Fact]
    public void Running_it_twice_changes_nothing_the_second_time()
    {
        // Retention runs nightly forever. A pass that kept thinning its own output would walk every
        // directory down to a single row over a few months.
        var taken = new List<long>();

        for (var i = 0; i < 2000; i++)
        {
            taken.Add(At(600) + i * 900);
        }

        var once = RetentionPolicy.Survivors(taken, Horizons);
        var twice = RetentionPolicy.Survivors(once, Horizons);

        Assert.Equal(once, twice);
    }

    [Fact]
    public void A_measurement_only_ever_moves_to_a_coarser_tier_as_it_ages()
    {
        var taken = At(6);

        var today = RetentionPolicy.ResolutionFor(taken, Horizons);
        var later = RetentionPolicy.ResolutionFor(taken, RetentionHorizons.From(Now.AddDays(30), Defaults));

        Assert.Equal(RetentionResolution.Full, today);
        Assert.True(later > today);
    }

    // --- Configuration. ---

    [Fact]
    public void The_defaults_are_the_tiers_the_architecture_specifies()
    {
        Assert.Equal(7, Defaults.FullResolutionDays);
        Assert.Equal(30, Defaults.HourlyDays);
        Assert.Equal(365, Defaults.DailyDays);
        Assert.True(Defaults.Enabled);
        Assert.Empty(Defaults.Validate());
    }

    [Theory]
    [InlineData(0, 30, 365)]
    [InlineData(31, 30, 365)]
    [InlineData(7, 400, 365)]
    public void Tiers_that_overlap_or_invert_are_rejected(int full, int hourly, int daily)
    {
        var tiers = new RetentionTiers
        {
            FullResolutionDays = full,
            HourlyDays = hourly,
            DailyDays = daily,
        };

        Assert.NotEmpty(tiers.Validate());
    }

    [Fact]
    public void Equal_tiers_are_allowed_because_that_is_how_a_tier_is_turned_off()
    {
        // hourly == full means nothing is ever kept hourly: a measurement leaving the full window
        // lands straight in the daily tier. That is a legitimate choice, not a misconfiguration.
        var tiers = new RetentionTiers { FullResolutionDays = 7, HourlyDays = 7, DailyDays = 7 };

        Assert.Empty(tiers.Validate());

        var horizons = RetentionHorizons.From(Now, tiers);

        Assert.Equal(RetentionResolution.Weekly, RetentionPolicy.ResolutionFor(At(8), horizons));
    }
}
