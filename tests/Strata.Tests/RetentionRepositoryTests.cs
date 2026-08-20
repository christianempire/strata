using Microsoft.Data.Sqlite;
using Strata.Core.Retention;
using Strata.Storage;

namespace Strata.Tests;

/// <summary>
/// The retention SQL, checked against the policy it is supposed to implement.
/// </summary>
/// <remarks>
/// The downsample is one window function over millions of rows: fast, and unreadable enough that
/// nobody can eyeball whether it is right. <see cref="RetentionPolicy.Survivors"/> is the readable,
/// slow statement of the same rule. These tests run both over the same data and require them to
/// agree, so the SQL is pinned to something a person can actually check.
/// </remarks>
public sealed class RetentionRepositoryTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 8, 20, 12, 0, 0, DateTimeKind.Utc);

    private readonly string _directory;
    private readonly StrataDatabase _database;
    private readonly SqliteConnection _connection;

    public RetentionRepositoryTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "strata-retention-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);

        _database = new StrataDatabase(Path.Combine(_directory, "test.db"));
        _database.Migrate();
        _connection = _database.Open();

        using var volume = _connection.CreateCommand();
        volume.CommandText =
            "INSERT INTO volumes (id, guid_path, drive_letter, label, file_system, kind, " +
            "total_bytes, free_bytes, first_seen_utc, last_seen_utc) " +
            "VALUES (1, '\\\\?\\Volume{test}\\', 'C:', 'Test', 'NTFS', 3, 0, 0, '', '');";
        volume.ExecuteNonQuery();
    }

    public void Dispose()
    {
        _connection.Dispose();
        SqliteConnection.ClearAllPools();

        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp database is not worth failing a run over.
        }
    }

    private static long At(double daysAgo)
        => (long)(Now.AddDays(-daysAgo) - DateTime.UnixEpoch).TotalSeconds;

    private void Write(long dirId, params long[] taken)
    {
        using var insert = _connection.CreateCommand();
        insert.CommandText = """
            INSERT OR REPLACE INTO dir_history
                (volume_id, dir_id, taken_unix, subtree_bytes, subtree_files)
            VALUES (1, $d, $t, $b, 1);
            """;

        var dir = insert.Parameters.Add("$d", SqliteType.Integer);
        var time = insert.Parameters.Add("$t", SqliteType.Integer);
        var bytes = insert.Parameters.Add("$b", SqliteType.Integer);

        foreach (var t in taken)
        {
            dir.Value = dirId;
            time.Value = t;
            bytes.Value = t;
            insert.ExecuteNonQuery();
        }
    }

    private List<long> Remaining(long dirId)
    {
        using var read = _connection.CreateCommand();
        read.CommandText =
            "SELECT taken_unix FROM dir_history WHERE volume_id = 1 AND dir_id = $d ORDER BY taken_unix;";
        read.Parameters.AddWithValue("$d", dirId);

        var rows = new List<long>();
        using var reader = read.ExecuteReader();

        while (reader.Read())
        {
            rows.Add(reader.GetInt64(0));
        }

        return rows;
    }

    private RetentionResult Run(bool dryRun = false)
        => RetentionRepository.Run(_connection, new RetentionTiers(), Now, dryRun);

    // --- The SQL against the policy. ---

    [Fact]
    public void The_statement_keeps_exactly_what_the_policy_says_it_should()
    {
        var taken = new List<long>();

        // Two years of a directory measured four times an hour, which is what the default cadence
        // produces, plus the epoch-dated opening seed a migration leaves behind.
        taken.Add(0);

        for (var hours = 0; hours < 24 * 500; hours += 3)
        {
            for (var i = 0; i < 4; i++)
            {
                taken.Add(At(500) + hours * 3600 + i * 900);
            }
        }

        Write(42, [.. taken]);
        Run();

        var expected = RetentionPolicy.Survivors(taken, RetentionHorizons.From(Now, new RetentionTiers()));

        Assert.Equal(expected, Remaining(42));
    }

    [Fact]
    public void Nothing_inside_the_full_resolution_window_is_touched()
    {
        var taken = new List<long>();

        for (var i = 0; i < 96 * 6; i++)
        {
            taken.Add(At(6) + i * 900);
        }

        Write(1, [.. taken]);
        Run();

        Assert.Equal(taken, Remaining(1));
    }

    [Fact]
    public void An_hour_of_measurements_past_the_window_becomes_its_newest()
    {
        var hour = At(10);
        long[] taken = [hour, hour + 900, hour + 1800, hour + 2700];

        Write(2, taken);
        Run();

        Assert.Equal([taken[^1]], Remaining(2));
    }

    // --- The invariant that makes it safe. ---

    [Fact]
    public void A_directory_measured_once_and_never_again_keeps_that_measurement()
    {
        // The silent-corruption case. This directory's only row is years old; deleting it would make
        // every query about it answer from whatever came next, which is nothing.
        Write(3, At(2000));
        Run();

        Assert.Equal([At(2000)], Remaining(3));
    }

    [Fact]
    public void The_newest_measurement_survives_at_every_age()
    {
        // Containment rather than equality, and the distinction is the point: three measurements a
        // minute apart can straddle a bucket boundary, in which case more than one correctly
        // survives. What must be true at every age is that the newest is among them.
        var ages = new[] { (10L, 8.0), (11L, 40.0), (12L, 400.0), (13L, 5000.0) };

        foreach (var (dir, age) in ages)
        {
            Write(dir, At(age), At(age) - 60, At(age) - 120);
        }

        Run();

        foreach (var (dir, age) in ages)
        {
            var remaining = Remaining(dir);

            Assert.Contains(At(age), remaining);
            Assert.Equal(
                RetentionPolicy.Survivors(
                    [At(age), At(age) - 60, At(age) - 120],
                    RetentionHorizons.From(Now, new RetentionTiers())),
                remaining);
        }
    }

    [Fact]
    public void Measurements_either_side_of_a_bucket_boundary_both_survive()
    {
        // The case that broke the first version of the test above, kept because it is the one that
        // shows downsampling is bucketing rather than deduplication: two measurements a minute apart
        // are not redundant if a clock hour ran between them.
        var boundary = At(10);

        Assert.Equal(0, boundary % 3600);

        Write(14, boundary, boundary - 60);
        Run();

        Assert.Equal([boundary - 60, boundary], Remaining(14));
    }

    [Fact]
    public void The_opening_seed_row_is_never_thinned_away()
    {
        // Migration 005 dated one row per directory at the epoch so every directory has a size at
        // every moment. It is the oldest row in the table by decades and the only one many
        // directories have.
        Write(4, 0);
        Run();

        Assert.Equal([0L], Remaining(4));
    }

    [Fact]
    public void Directories_are_thinned_independently_of_each_other()
    {
        var hour = At(10);

        Write(5, hour, hour + 900, hour + 1800);
        Write(6, At(3));

        Run();

        Assert.Equal([hour + 1800], Remaining(5));
        Assert.Equal([At(3)], Remaining(6));
    }

    [Fact]
    public void Running_it_again_the_next_night_removes_nothing_more()
    {
        var taken = new List<long>();

        for (var i = 0; i < 500; i++)
        {
            taken.Add(At(200) + i * 900);
        }

        Write(7, [.. taken]);

        var first = Run();
        var after = Remaining(7);
        var second = Run();

        Assert.True(first.HistoryRemoved > 0);
        Assert.Equal(0, second.HistoryRemoved);
        Assert.Equal(after, Remaining(7));
    }

    // --- What it must not touch. ---

    [Fact]
    public void Snapshots_and_their_events_are_left_alone()
    {
        // events cascades from snapshots, so thinning the one would silently take the change log
        // with it — and the change log is what the most valuable screen in the product reads.
        using var seed = _connection.CreateCommand();
        seed.CommandText = """
            INSERT INTO snapshots (id, volume_id, taken_utc, scan_tier, duration_ms, total_bytes,
                                   total_on_disk, free_bytes, file_count, dir_count)
            VALUES (1, 1, '2020-01-01T00:00:00.0000000Z', 2, 0, 0, 0, 0, 0, 0);
            INSERT INTO events (volume_id, snapshot_id, when_utc, path, delta_bytes, kind)
            VALUES (1, 1, '2020-01-01T00:00:00.0000000Z', 'C:\\old', 1024, 1);
            """;
        seed.ExecuteNonQuery();

        Run();

        Assert.Equal(1, Count("SELECT COUNT(*) FROM snapshots;"));
        Assert.Equal(1, Count("SELECT COUNT(*) FROM events;"));
    }

    [Fact]
    public void The_audit_log_is_dropped_by_age_rather_than_thinned()
    {
        using var seed = _connection.CreateCommand();
        seed.CommandText = """
            INSERT INTO scan_log (cycle_utc, when_utc, volume, tier, result, reason)
            VALUES ('', $old, 'C:', 2, 1, 'ancient'), ('', $recent, 'C:', 2, 1, 'recent');
            """;
        seed.Parameters.AddWithValue("$old", Now.AddDays(-200).ToString("O"));
        seed.Parameters.AddWithValue("$recent", Now.AddDays(-2).ToString("O"));
        seed.ExecuteNonQuery();

        var result = Run();

        Assert.Equal(1, result.ScanLogRemoved);
        Assert.Equal(1, Count("SELECT COUNT(*) FROM scan_log;"));
        Assert.Equal(1, Count("SELECT COUNT(*) FROM scan_log WHERE reason = 'recent';"));
    }

    // --- Reporting. ---

    [Fact]
    public void A_dry_run_counts_without_deleting()
    {
        var hour = At(10);

        Write(8, hour, hour + 900, hour + 1800);

        var dry = Run(dryRun: true);

        Assert.True(dry.DryRun);
        Assert.Equal(2, dry.HistoryRemoved);
        Assert.Equal(3, Remaining(8).Count);

        var real = Run();

        Assert.Equal(dry.HistoryRemoved, real.HistoryRemoved);
        Assert.Single(Remaining(8));
    }

    [Fact]
    public void A_pass_over_nothing_is_not_an_error()
    {
        var result = Run();

        Assert.Equal(0, result.TotalRemoved);
        Assert.Equal(0, result.HistoryBefore);
    }

    [Fact]
    public void Disabled_tiers_that_keep_everything_remove_nothing()
    {
        // hourlyDays and dailyDays equal to a very large fullResolutionDays is how somebody keeps
        // every measurement forever, and it must be a no-op rather than a slow no-op that deletes.
        var hour = At(10);

        Write(9, hour, hour + 900, hour + 1800);

        var keepAll = new RetentionTiers
        {
            FullResolutionDays = 10_000,
            HourlyDays = 10_000,
            DailyDays = 10_000,
            ScanLogDays = 10_000,
        };

        var result = RetentionRepository.Run(_connection, keepAll, Now);

        Assert.Equal(0, result.TotalRemoved);
        Assert.Equal(3, Remaining(9).Count);
    }

    private long Count(string sql)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = sql;

        return Convert.ToInt64(command.ExecuteScalar()!, System.Globalization.CultureInfo.InvariantCulture);
    }
}
