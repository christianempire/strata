using Microsoft.Data.Sqlite;
using Strata.Core.Model;
using Strata.Core.Rollup;
using Strata.Core.Scanning;
using Strata.Storage;

namespace Strata.Tests;

/// <summary>
/// The Timeline's series query.
/// </summary>
/// <remarks>
/// Worth real coverage because every failure here is a plausible chart rather than an error. A
/// directory sampled without its opening row draws as though it appeared mid-window; a bucket walk
/// that is off by one draws a step on the wrong day. Both look like data.
/// </remarks>
public sealed class SeriesQueryTests : IDisposable
{
    private const long GB = 1024L * 1024 * 1024;

    private static readonly DateTime Monday = new(2026, 8, 10, 0, 0, 0, DateTimeKind.Utc);

    private readonly string _directory;
    private readonly StrataDatabase _database;
    private readonly SqliteConnection _connection;
    private readonly long _volumeId;

    public SeriesQueryTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "strata-series-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);

        _database = new StrataDatabase(Path.Combine(_directory, "test.db"));
        _database.Migrate();

        _connection = _database.Open();
        _volumeId = VolumeRepository.Upsert(_connection, Volume());

        // A volume root with three top-level directories, as the name dimension sees them.
        Name(1, 1, "", 0);
        Name(10, 1, "Users", 1);
        Name(20, 1, "Program Files", 1);
        Name(30, 1, "WINDOWS", 1);
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
            // Leaving a temp database behind is not worth failing a run over.
        }
    }

    /// <summary>
    /// A directory that did not change inside the window still draws at its real size.
    /// </summary>
    /// <remarks>
    /// The one that matters most. History is sparse: a directory gets a row only when its total
    /// moves, so a quiet week has no rows in the window at all. Sampling without reading the last
    /// row *before* the window would draw Program Files at zero until its first change — "Program
    /// Files appeared on Thursday" rather than "Program Files was 92 GB all week".
    /// </remarks>
    [Fact]
    public void AQuietDirectoryHoldsItsOpeningValueAcrossTheWholeWindow()
    {
        History(20, Monday.AddDays(-30), 92 * GB);

        var series = Load(Monday, Monday.AddDays(7), buckets: 8);
        var programFiles = Assert.Single(series.Series, s => s.Name == "Program Files");

        Assert.All(programFiles.Bytes, value => Assert.Equal(92 * GB, value));
    }

    /// <summary>A change lands in the bucket it happened in, and holds from there.</summary>
    [Fact]
    public void AStepChangeLandsInItsOwnBucketAndHolds()
    {
        History(10, Monday.AddDays(-1), 100 * GB);
        History(10, Monday.AddDays(3).AddHours(2), 132 * GB);

        // Eight edges over seven days is one a day: Monday, Tuesday, ... Monday again.
        var series = Load(Monday, Monday.AddDays(7), buckets: 8);
        var users = Assert.Single(series.Series, s => s.Name == "Users");

        Assert.Equal(100 * GB, users.Bytes[0]);
        Assert.Equal(100 * GB, users.Bytes[3]);

        // The change is two hours into day three, so it is visible at the day-four edge onwards.
        Assert.Equal(132 * GB, users.Bytes[4]);
        Assert.Equal(132 * GB, users.Bytes[7]);
        Assert.Equal(132 * GB, users.Latest);
    }

    [Fact]
    public void EveryBucketArrayIsTheSameLengthAsTheBucketList()
    {
        History(10, Monday, 100 * GB);
        History(20, Monday, 92 * GB);

        var series = Load(Monday, Monday.AddDays(7), buckets: 24);

        Assert.Equal(24, series.Buckets.Length);
        Assert.Equal(24, series.Other.Length);
        Assert.Equal(24, series.Used.Length);
        Assert.All(series.Series, s => Assert.Equal(24, s.Bytes.Length));
    }

    /// <summary>
    /// Past the ramp's twelve hues, the tail is summed rather than dropped.
    /// </summary>
    /// <remarks>
    /// Folding rather than truncating is what keeps the stack adding up to the volume. The one
    /// desaturated band is also why the ramp stops at twelve: past that, hues stop being
    /// distinguishable on a dark ground.
    /// </remarks>
    [Fact]
    public void DirectoriesPastTheTopCountFoldIntoOther()
    {
        for (var i = 0; i < 5; i++)
        {
            Name(100 + i, 1, "d" + i, 1);
            History(100 + i, Monday, (i + 1) * GB);
        }

        var series = Load(Monday, Monday.AddDays(1), buckets: 2, topCount: 2);

        Assert.Equal(2, series.Series.Count);

        // The two largest are named; 3 + 2 + 1 GB is what is left.
        Assert.Equal(5 * GB, series.Series[0].Latest);
        Assert.Equal(4 * GB, series.Series[1].Latest);
        Assert.All(series.Other, value => Assert.Equal(6 * GB, value));
    }

    [Fact]
    public void SeriesAreRankedByTheirSizeAtTheEndOfTheWindow()
    {
        History(10, Monday, 10 * GB);
        History(20, Monday, 90 * GB);
        History(30, Monday, 50 * GB);

        // Users overtakes everything on the last day, which is the ordering the chart should use.
        History(10, Monday.AddDays(6), 200 * GB);

        var series = Load(Monday, Monday.AddDays(7), buckets: 8);

        Assert.Equal(["Users", "Program Files", "WINDOWS"], series.Series.Select(s => s.Name));
    }

    /// <summary>A directory that held nothing for the whole window is not given a band.</summary>
    [Fact]
    public void EmptyDirectoriesDoNotTakeAHue()
    {
        History(10, Monday, 100 * GB);
        History(20, Monday, 0);

        var series = Load(Monday, Monday.AddDays(1), buckets: 4);

        Assert.Single(series.Series);
        Assert.Equal("Users", series.Series[0].Name);
    }

    /// <summary>
    /// Used space comes from what the OS reported, so the gap above the bands is drawable.
    /// </summary>
    /// <remarks>
    /// The point of carrying it: the difference between this and the sum of the bands is what
    /// Strata could not account for, and a stack that quietly added up to less than the disk would
    /// read as a bug rather than as an unelevated scan.
    /// </remarks>
    [Fact]
    public void UsedSpaceIsCapacityMinusWhatTheSnapshotRecordedFree()
    {
        History(10, Monday, 100 * GB);
        Snapshot(Monday.AddHours(1), freeBytes: 400 * GB, totalBytes: 500 * GB);

        var series = Load(Monday, Monday.AddDays(1), buckets: 4);

        Assert.True(series.Measured);
        Assert.Equal(600 * GB, series.Used[^1]);
    }

    [Fact]
    public void AWindowWithNoSnapshotsReportsItselfUnmeasured()
    {
        History(10, Monday, 100 * GB);

        var series = Load(Monday, Monday.AddDays(1), buckets: 4);

        Assert.False(series.Measured);
        Assert.All(series.Used, value => Assert.Equal(0, value));
    }

    // --- helpers ------------------------------------------------------------------------------

    private VolumeSeries Load(DateTime from, DateTime to, int buckets, int topCount = 12)
        => SeriesQueries.Load(_connection, _volumeId, 1000 * GB, from, to, buckets, topCount);

    private void Name(long dirId, long parentId, string name, int depth)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = """
            INSERT OR REPLACE INTO dir_names (volume_id, dir_id, parent_id, name, depth, first_seen_utc)
            VALUES ($v, $d, $p, $n, $depth, '');
            """;

        command.Parameters.AddWithValue("$v", _volumeId);
        command.Parameters.AddWithValue("$d", dirId);
        command.Parameters.AddWithValue("$p", parentId);
        command.Parameters.AddWithValue("$n", name);
        command.Parameters.AddWithValue("$depth", depth);
        command.ExecuteNonQuery();
    }

    private void History(long dirId, DateTime whenUtc, long subtreeBytes)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = """
            INSERT OR REPLACE INTO dir_history (volume_id, dir_id, taken_unix, subtree_bytes, subtree_files)
            VALUES ($v, $d, $t, $b, 1);
            """;

        command.Parameters.AddWithValue("$v", _volumeId);
        command.Parameters.AddWithValue("$d", dirId);
        command.Parameters.AddWithValue("$t", new DateTimeOffset(whenUtc).ToUnixTimeSeconds());
        command.Parameters.AddWithValue("$b", subtreeBytes);
        command.ExecuteNonQuery();
    }

    private void Snapshot(DateTime takenUtc, long freeBytes, long totalBytes)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = """
            INSERT INTO snapshots
                (volume_id, taken_utc, scan_tier, duration_ms, total_bytes, total_on_disk,
                 free_bytes, file_count, dir_count)
            VALUES ($v, $t, 1, 0, $total, $total, $free, 1, 1);
            """;

        command.Parameters.AddWithValue("$v", _volumeId);
        command.Parameters.AddWithValue("$t", takenUtc.ToString("O"));
        command.Parameters.AddWithValue("$total", totalBytes);
        command.Parameters.AddWithValue("$free", freeBytes);
        command.ExecuteNonQuery();
    }

    private static VolumeIdentity Volume() => new()
    {
        GuidPath = @"\\?\Volume{aaaaaaaa-0000-0000-0000-000000000001}\",
        DriveLetter = "C:",
        Label = "Test",
        FileSystem = "NTFS",
        Kind = VolumeKind.Fixed,
        TotalBytes = 1000L * GB,
        FreeBytes = 100L * GB,
    };
}
