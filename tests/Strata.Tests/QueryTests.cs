using Microsoft.Data.Sqlite;
using Strata.Core.Model;
using Strata.Core.Rollup;
using Strata.Core.Scanning;
using Strata.Storage;

namespace Strata.Tests;

/// <summary>
/// The read side the API is built on.
/// </summary>
/// <remarks>
/// Phase 3 is the first thing to query this database rather than fill it, and a read that quietly
/// returns the wrong rows is worse than one that fails: it becomes a chart. These use a real SQLite
/// file rather than a fake, because half of what is being asserted is how SQLite treats a prefix
/// range and a case-insensitive comparison.
/// </remarks>
public sealed class QueryTests : IDisposable
{
    private const long MB = 1024 * 1024;
    private const long GB = 1024 * MB;

    private readonly string _directory;
    private readonly StrataDatabase _database;
    private readonly SqliteConnection _connection;
    private readonly long _volumeId;

    public QueryTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "strata-query-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);

        _database = new StrataDatabase(Path.Combine(_directory, "test.db"));
        _database.Migrate();

        _connection = _database.Open();
        _volumeId = VolumeRepository.Upsert(_connection, Volume());
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

    // --- settings -----------------------------------------------------------------------------

    [Fact]
    public void SettingsFallBackToDefaultsWhenNothingIsStored()
    {
        var settings = SettingsRepository.Load(_connection);

        Assert.Equal(new StrataSettings().IntervalSeconds, settings.IntervalSeconds);
        Assert.Empty(settings.ExcludedEventPaths);
        Assert.Null(settings.ScanWorkers);
    }

    [Fact]
    public void SettingsRoundTrip()
    {
        var written = new StrataSettings
        {
            IntervalSeconds = 300,
            CpuThresholdPercent = 55.5,
            MaxRecordsPerTick = 20_000,
            EventFloorBytes = 4 * MB,
            SnapshotMaxDepth = 6,
            SnapshotSizeFloorBytes = 20 * MB,
            ScanWorkers = 16,
            BackgroundIo = false,
            DetectBorderlessFullscreen = false,
            IncludeStrataDatabase = true,
            ExcludedEventPaths = [@"C:\Temp", @"D:\Games"],
            ExcludedDirectoryNames = ["node_modules"],
            Paused = true,
        };

        SettingsRepository.Save(_connection, written);

        var read = SettingsRepository.Load(_connection);

        // Compared field by field rather than as records: a positional record compares its list
        // properties by reference, so record equality here would pass on two empty lists and fail on
        // two identical ones.
        Assert.Equal(written.IntervalSeconds, read.IntervalSeconds);
        Assert.Equal(written.CpuThresholdPercent, read.CpuThresholdPercent);
        Assert.Equal(written.MaxRecordsPerTick, read.MaxRecordsPerTick);
        Assert.Equal(written.EventFloorBytes, read.EventFloorBytes);
        Assert.Equal(written.SnapshotMaxDepth, read.SnapshotMaxDepth);
        Assert.Equal(written.SnapshotSizeFloorBytes, read.SnapshotSizeFloorBytes);
        Assert.Equal(written.ScanWorkers, read.ScanWorkers);
        Assert.Equal(written.BackgroundIo, read.BackgroundIo);
        Assert.Equal(written.DetectBorderlessFullscreen, read.DetectBorderlessFullscreen);
        Assert.Equal(written.IncludeStrataDatabase, read.IncludeStrataDatabase);
        Assert.Equal(written.ExcludedEventPaths, read.ExcludedEventPaths);
        Assert.Equal(written.ExcludedDirectoryNames, read.ExcludedDirectoryNames);
        Assert.Equal(written.Paused, read.Paused);
    }

    /// <summary>
    /// An interval of zero would turn the background scanner into a busy loop against the disk,
    /// which the project treats as a failure rather than a misconfiguration.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    [InlineData(-900)]
    public void AnIntervalThatWouldBusyLoopIsRejected(int seconds)
    {
        var settings = new StrataSettings { IntervalSeconds = seconds };

        Assert.NotEmpty(settings.Validate());
        Assert.Throws<ArgumentException>(() => SettingsRepository.Save(_connection, settings));
    }

    /// <summary>
    /// The table outlives the build that wrote it, so a value it no longer recognises has to be
    /// ignored rather than throwing on startup.
    /// </summary>
    [Fact]
    public void AnUnknownSettingIsIgnored()
    {
        using (var command = _connection.CreateCommand())
        {
            command.CommandText =
                "INSERT INTO settings (key, value, updated_utc) VALUES ('somethingElse', 'x', '');";
            command.ExecuteNonQuery();
        }

        Assert.Equal(new StrataSettings().IntervalSeconds, SettingsRepository.Load(_connection).IntervalSeconds);
    }

    // --- volumes ------------------------------------------------------------------------------

    [Fact]
    public void AVolumeResolvesByLetterIdAndGuid()
    {
        Assert.Equal(_volumeId, VolumeQueries.Resolve(_connection, "T:"));
        Assert.Equal(_volumeId, VolumeQueries.Resolve(_connection, "t"));
        Assert.Equal(_volumeId, VolumeQueries.Resolve(_connection, _volumeId.ToString()));
        Assert.Equal(_volumeId, VolumeQueries.Resolve(_connection, Volume().GuidPath));
        Assert.Null(VolumeQueries.Resolve(_connection, "Z:"));
        Assert.Null(VolumeQueries.Resolve(_connection, null));
    }

    [Fact]
    public void VolumeListingCarriesBaselineAndSnapshotState()
    {
        Assert.Null(Assert.Single(VolumeQueries.List(_connection)).BaselineUtc);

        WriteBaseline();
        IncrementalRepository.MarkBaseline(_connection, _volumeId, DateTime.UtcNow);

        var volume = Assert.Single(VolumeQueries.List(_connection));

        Assert.NotNull(volume.BaselineUtc);
        Assert.Equal(1, volume.SnapshotCount);
        Assert.NotNull(volume.LastSnapshotUtc);

        // Read from the maintained rollup, not from the snapshot, so it stays current between scans.
        Assert.Equal((100 * GB) + MB, volume.AccountedBytes);
    }

    // --- snapshots ----------------------------------------------------------------------------

    /// <summary>
    /// Timestamps are ISO-8601 text, so a range filter is a string comparison. It only agrees with a
    /// chronological one because the format is fixed-width; this is the test that would catch it if
    /// that ever stopped being true.
    /// </summary>
    [Fact]
    public void SnapshotsFilterByTimeRange()
    {
        var baseline = new DateTime(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc);

        for (var i = 0; i < 5; i++)
        {
            InsertSnapshot(baseline.AddHours(i));
        }

        Assert.Equal(5, SnapshotQueries.List(_connection, _volumeId, null, null, 100).Count);
        Assert.Equal(3, SnapshotQueries.List(_connection, _volumeId, baseline.AddHours(2), null, 100).Count);
        Assert.Equal(3, SnapshotQueries.List(_connection, _volumeId, null, baseline.AddHours(2), 100).Count);
        Assert.Single(SnapshotQueries.List(
            _connection, _volumeId, baseline.AddHours(2), baseline.AddHours(2), 100));

        Assert.Empty(SnapshotQueries.List(_connection, _volumeId + 99, null, null, 100));
    }

    [Fact]
    public void SnapshotsComeBackNewestFirstAndRespectTheLimit()
    {
        var baseline = new DateTime(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc);

        for (var i = 0; i < 5; i++)
        {
            InsertSnapshot(baseline.AddHours(i));
        }

        var rows = SnapshotQueries.List(_connection, null, null, null, 2);

        Assert.Equal(2, rows.Count);
        Assert.True(string.CompareOrdinal(rows[0].TakenUtc, rows[1].TakenUtc) > 0);
    }

    /// <summary>
    /// A snapshot with no directory rows cannot take part in a diff, and the UI has to be able to
    /// see that before offering it as a comparison endpoint.
    /// </summary>
    [Fact]
    public void ASnapshotReportsHowManyDirectoryRowsItRecorded()
    {
        var snapshotId = WriteBaseline();
        var incremental = InsertSnapshot(DateTime.UtcNow);

        Assert.True(SnapshotQueries.ById(_connection, snapshotId)!.RolledUpDirCount > 0);
        Assert.Equal(0, SnapshotQueries.ById(_connection, incremental)!.RolledUpDirCount);
        Assert.Null(SnapshotQueries.ById(_connection, 9999));
    }

    // --- current-state tree -------------------------------------------------------------------

    [Fact]
    public void TheVolumeRootIsFound()
    {
        WriteBaseline();

        Assert.Equal(RootDirId, TreeQueries.RootId(_connection, _volumeId));
    }

    [Fact]
    public void APathResolvesToItsDirectory()
    {
        WriteBaseline();

        Assert.Equal(RootDirId, TreeQueries.ResolvePath(_connection, _volumeId, null));
        Assert.Equal(RootDirId, TreeQueries.ResolvePath(_connection, _volumeId, "T:"));
        Assert.Equal(20, TreeQueries.ResolvePath(_connection, _volumeId, @"T:\Users"));
        Assert.Equal(21, TreeQueries.ResolvePath(_connection, _volumeId, @"T:\Users\chris"));

        // NTFS is case-insensitive and forward slashes are what a URL will carry.
        Assert.Equal(21, TreeQueries.ResolvePath(_connection, _volumeId, @"t:\users\CHRIS"));
        Assert.Equal(21, TreeQueries.ResolvePath(_connection, _volumeId, "Users/chris"));

        Assert.Null(TreeQueries.ResolvePath(_connection, _volumeId, @"T:\Nope"));
    }

    /// <summary>
    /// The volume root is its own parent. Without that being excluded from the recursive term, the
    /// walk joins the root to itself and never terminates.
    /// </summary>
    [Fact]
    public async Task WalkingFromTheRootTerminates()
    {
        WriteBaseline();

        var nodes = await Walk(RootDirId, depth: 10);

        Assert.Equal(Tree().Count, nodes.Count);
        Assert.Equal(RootDirId, nodes[0].DirId);
    }

    [Fact]
    public async Task WalkDepthBoundsHowFarItDescends()
    {
        WriteBaseline();

        var oneLevel = await Walk(RootDirId, depth: 1);

        Assert.Equal(4, oneLevel.Count); // the root, Users, UsersBackup, Windows
        Assert.All(oneLevel, n => Assert.True(n.Depth <= 1));

        // Adds chris under Users and tiny under Windows.
        Assert.Equal(6, (await Walk(RootDirId, depth: 2)).Count);
    }

    [Fact]
    public async Task WalkOrdersEachLevelLargestFirstAndFlagsDrillableNodes()
    {
        WriteBaseline();

        var nodes = await Walk(RootDirId, depth: 1);

        Assert.Equal(["Users", "Windows", "UsersBackup"], nodes.Skip(1).Select(n => n.Name));
        Assert.True(nodes[1].HasChildren);
        Assert.False(nodes[3].HasChildren);
    }

    [Fact]
    public async Task WalkingASubtreeReturnsOnlyThatSubtree()
    {
        WriteBaseline();

        var nodes = await Walk(21, depth: 10);

        Assert.Equal(2, nodes.Count);
        Assert.Equal("chris", nodes[0].Name);
        Assert.Equal("Documents", nodes[1].Name);
    }

    // --- historical tree ----------------------------------------------------------------------

    [Fact]
    public async Task ASnapshotTreeReadsFromTheHistoricalRollup()
    {
        var snapshotId = WriteBaseline();

        var nodes = new List<TreeNode>();

        await foreach (var node in TreeQueries.WalkSnapshotAsync(
            _connection, snapshotId, null, depth: 10, limit: 1000, CancellationToken.None))
        {
            nodes.Add(node);
        }

        // Every directory except the 1 MB one, which falls under this snapshot's size floor.
        Assert.Equal(Tree().Count - 1, nodes.Count);
        Assert.Equal("T:", nodes[0].Name);
        Assert.Contains(nodes, n => n.Name == "Documents");
        Assert.DoesNotContain(nodes, n => n.Name == "tiny");
    }

    /// <summary>
    /// The historical rollup is keyed on paths, so a subtree is a string range. "T:\Users" must not
    /// pull in a sibling called "T:\UsersBackup" — the range has to stop at the separator.
    /// </summary>
    [Fact]
    public async Task ASnapshotSubtreeDoesNotSwallowASiblingWithASharedPrefix()
    {
        var snapshotId = WriteBaseline();

        var nodes = new List<TreeNode>();

        await foreach (var node in TreeQueries.WalkSnapshotAsync(
            _connection, snapshotId, @"T:\Users", depth: 10, limit: 1000, CancellationToken.None))
        {
            nodes.Add(node);
        }

        Assert.Equal(3, nodes.Count);
        Assert.DoesNotContain(nodes, n => n.Name.Contains("Backup", StringComparison.Ordinal));
    }

    // --- events -------------------------------------------------------------------------------

    [Fact]
    public void EventsFilterByRangeVolumeAndSize()
    {
        var snapshotId = InsertSnapshot(DateTime.UtcNow);
        var baseline = new DateTime(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc);

        InsertEvent(snapshotId, baseline, @"T:\a", 4 * GB);
        InsertEvent(snapshotId, baseline.AddHours(1), @"T:\b", -2 * GB);
        InsertEvent(snapshotId, baseline.AddHours(2), @"T:\c", 512);

        Assert.Equal(3, EventQueries.List(_connection, null, null, null, 0, 100).Count);
        Assert.Equal(2, EventQueries.List(_connection, null, null, null, MB, 100).Count);
        Assert.Equal(2, EventQueries.List(_connection, _volumeId, baseline.AddHours(1), null, 0, 100).Count);
        Assert.Empty(EventQueries.List(_connection, _volumeId + 99, null, null, 0, 100));

        var newestFirst = EventQueries.List(_connection, null, null, null, 0, 100);

        Assert.Equal(@"T:\c", newestFirst[0].Path);
        Assert.Equal("T:", newestFirst[0].DriveLetter);
    }

    // --- fixtures -----------------------------------------------------------------------------

    private const long RootDirId = 5;

    /// <summary>
    /// A small volume with the shapes that matter: nesting, a sibling sharing a name prefix, and a
    /// directory under the size floor.
    /// </summary>
    private static Dictionary<long, DirectoryNode> Tree() => new()
    {
        [RootDirId] = new(RootDirId, RootDirId, ""),
        [20] = new(20, RootDirId, "Users"),
        [21] = new(21, 20, "chris"),
        [22] = new(22, 21, "Documents") { OwnBytes = 60 * GB, OwnFileCount = 600 },
        [23] = new(23, RootDirId, "UsersBackup") { OwnBytes = 20 * GB, OwnFileCount = 200 },
        [24] = new(24, RootDirId, "Windows") { OwnBytes = 20 * GB, OwnFileCount = 200 },
        [25] = new(25, 24, "tiny") { OwnBytes = 1 * MB, OwnFileCount = 1 },
    };

    private long WriteBaseline()
    {
        var rollup = RollupEngine.Compute(Tree(), RootDirId);

        var result = new ScanResult
        {
            Volume = Volume(),
            Tier = ScanTier.Full,
            StartedUtc = new DateTime(2026, 3, 1, 9, 0, 0, DateTimeKind.Utc),
            Duration = TimeSpan.FromSeconds(3),
            Rollup = rollup,
            FileCount = rollup.TotalFileCount,
            DirectoryCount = rollup.TotalDirCount,
            TotalBytes = rollup.TotalBytes,
            TotalSizeOnDisk = rollup.TotalSizeOnDisk,
        };

        return SnapshotWriter.Write(_connection, _volumeId, result, new SnapshotOptions
        {
            MaxDepth = 8,
            SizeFloorBytes = 10 * MB,
        });
    }

    private async Task<List<TreeNode>> Walk(long rootDirId, int depth)
    {
        var nodes = new List<TreeNode>();

        await foreach (var node in TreeQueries.WalkCurrentAsync(
            _connection, _volumeId, rootDirId, depth, limit: 10_000, CancellationToken.None))
        {
            nodes.Add(node);
        }

        return nodes;
    }

    private long InsertSnapshot(DateTime takenUtc)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = """
            INSERT INTO snapshots
                (volume_id, taken_utc, scan_tier, duration_ms, total_bytes, total_on_disk,
                 free_bytes, file_count, dir_count)
            VALUES ($v, $t, 2, 100, 1, 1, 1, 1, 1) RETURNING id;
            """;

        command.Parameters.AddWithValue("$v", _volumeId);
        command.Parameters.AddWithValue("$t", takenUtc.ToString("O"));

        return Convert.ToInt64(command.ExecuteScalar()!, System.Globalization.CultureInfo.InvariantCulture);
    }

    private void InsertEvent(long snapshotId, DateTime whenUtc, string path, long delta)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = """
            INSERT INTO events
                (volume_id, snapshot_id, when_utc, path, delta_bytes, churn_bytes, file_delta, kind)
            VALUES ($v, $s, $w, $p, $d, $c, 1, 1);
            """;

        command.Parameters.AddWithValue("$v", _volumeId);
        command.Parameters.AddWithValue("$s", snapshotId);
        command.Parameters.AddWithValue("$w", whenUtc.ToString("O"));
        command.Parameters.AddWithValue("$p", path);
        command.Parameters.AddWithValue("$d", delta);
        command.Parameters.AddWithValue("$c", Math.Abs(delta));
        command.ExecuteNonQuery();
    }

    private static VolumeIdentity Volume() => new()
    {
        GuidPath = @"\\?\Volume{22222222-2222-2222-2222-222222222222}\",
        DriveLetter = "T:",
        Label = "Test",
        FileSystem = "NTFS",
        Kind = VolumeKind.Fixed,
        TotalBytes = 200 * GB,
        FreeBytes = 100 * GB,
    };
}
