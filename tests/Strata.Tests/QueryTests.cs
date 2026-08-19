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

    // --- the incremental cursor -----------------------------------------------------------------

    /// <summary>
    /// A baseline forgets the journal cursor, so a wrapped journal can actually recover.
    /// </summary>
    /// <remarks>
    /// The bug this pins was a trap with no way out. A journal that has been reset or has wrapped
    /// past the stored cursor makes the tick report JournalInvalidated, whose stated remedy is "a
    /// full baseline is needed" — but the baseline only set baseline_utc and left the dead cursor in
    /// place, so the next tick failed identically and asked for the same baseline again. Observed on
    /// the real C: after a successful 125 s rescan.
    /// </remarks>
    [Fact]
    public void ResettingTheCursorLetsTheNextTickStartFromWhereverTheJournalIsNow()
    {
        using (var transaction = _connection.BeginTransaction())
        {
            IncrementalRepository.SaveCursor(
                _connection, transaction, _volumeId, new UsnCursor(0xDEAD, 9_000_000));
            transaction.Commit();
        }

        Assert.True(IncrementalRepository.GetCursor(_connection, _volumeId).IsSet);

        IncrementalRepository.ResetCursor(_connection, _volumeId);

        // UsnCursor.None is what Win32UsnJournal.Read treats as "begin at the current position".
        Assert.False(IncrementalRepository.GetCursor(_connection, _volumeId).IsSet);
        Assert.Equal(UsnCursor.None, IncrementalRepository.GetCursor(_connection, _volumeId));
    }

    [Fact]
    public void ResettingACursorThatWasNeverWrittenIsHarmless()
    {
        IncrementalRepository.ResetCursor(_connection, _volumeId);

        Assert.Equal(UsnCursor.None, IncrementalRepository.GetCursor(_connection, _volumeId));
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

    [Fact]
    public void ASnapshotThatDoesNotExistIsNull()
    {
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

    /// <summary>
    /// The historical walk shows every directory, including ones the old rollup could not record.
    /// </summary>
    /// <remarks>
    /// <c>snapshot_dirs</c> was bounded by a depth cap and a 10 MB size floor, so the 1 MB directory
    /// here was simply absent from history at any price. The sparse history has no floor: it records
    /// whatever moved, wherever it sits.
    /// </remarks>
    [Fact]
    public async Task TheHistoricalTreeShowsDirectoriesBelowTheOldSizeFloor()
    {
        WriteBaseline();

        var nodes = await WalkHistory(RootDirId, At(1), depth: 10);

        Assert.Equal(Tree().Count, nodes.Count);
        Assert.Contains(nodes, n => n.Name == "Documents");
        Assert.Contains(nodes, n => n.Name == "tiny");
    }

    /// <summary>
    /// A subtree is bounded by ancestry, not by a string prefix.
    /// </summary>
    /// <remarks>
    /// Under the old model a subtree was a range over materialised paths, so <c>T:\Users</c> could
    /// pull in a sibling called <c>T:\UsersBackup</c> unless the range stopped at the separator.
    /// Keyed on parent ids, that class of bug cannot be expressed.
    /// </remarks>
    [Fact]
    public async Task AHistoricalSubtreeIsBoundedByAncestryNotByStringPrefix()
    {
        WriteBaseline();

        var users = DirectoryHistoryQueries.ResolvePath(_connection, _volumeId, @"T:\Users");

        Assert.NotNull(users);

        var nodes = await WalkHistory(users!.Value, At(1), depth: 10);

        Assert.Equal(3, nodes.Count);
        Assert.DoesNotContain(nodes, n => n.Name.Contains("Backup", StringComparison.Ordinal));
    }

    /// <summary>
    /// The historical tree shows what was there then, not what is there now.
    /// </summary>
    /// <remarks>
    /// The point of keeping history at all. A directory that has since grown reports its size at the
    /// moment asked for, and one created afterwards does not appear.
    /// </remarks>
    [Fact]
    public async Task TheHistoricalTreeReportsSizesAsTheyStoodAtTheInstant()
    {
        WriteBaseline();

        using (var command = _connection.CreateCommand())
        {
            command.CommandText = """
                UPDATE dir_totals SET subtree_bytes = subtree_bytes + 5368709120
                WHERE volume_id = $v AND dir_id = 22;
                """;
            command.Parameters.AddWithValue("$v", _volumeId);
            command.ExecuteNonQuery();
        }

        using (var store = new SqliteFileStateStore(_connection, _volumeId))
        {
            store.AddDirectory(90, RootDirId, "Later");
        }

        DirectoryHistoryRepository.Record(_connection, null, _volumeId, At(10), [22, 90], 0);

        var then = await WalkHistory(RootDirId, At(5), depth: 10);
        var now = await WalkHistory(RootDirId, At(20), depth: 10);

        Assert.DoesNotContain(then, n => n.Name == "Later");
        Assert.Equal(60 * GB, Assert.Single(then, n => n.Name == "Documents").SubtreeBytes);
        Assert.Equal(65 * GB, Assert.Single(now, n => n.Name == "Documents").SubtreeBytes);
    }

    // --- the cached directory tree --------------------------------------------------------------

    /// <summary>
    /// A directory created since the tree was cached must be stitched on, ancestors and all.
    /// </summary>
    /// <remarks>
    /// This is the bug the product's own headline example would have hit. A game installing into a
    /// new folder puts every one of its bytes into a directory the cached tree has never heard of,
    /// and the attribution engine drops a delta it cannot place — so the bytes landed in the totals
    /// and never reached the change feed. Reproduced on the real machine with a 1.44 GB tree: the
    /// rollup was exactly right and the feed said nothing at all.
    /// </remarks>
    [Fact]
    public void DirectoriesCreatedSinceTheTreeWasCachedAreStitchedOn()
    {
        WriteBaseline();

        var tree = IncrementalRepository.LoadDirectoryTree(_connection, _volumeId);
        var before = tree.Count;

        // A tick discovers three new levels at once, which is what unpacking an installer looks
        // like. Only the deepest is named, so the walk has to find its ancestors on its own.
        using (var store = new SqliteFileStateStore(_connection, _volumeId))
        {
            store.AddDirectory(90, RootDirId, "Games");
            store.AddDirectory(91, 90, "Foo");
            store.AddDirectory(92, 91, "Content");
        }

        var added = IncrementalRepository.AddMissingNodes(
            _connection, null, _volumeId, tree, [92]);

        Assert.Equal(3, added);
        Assert.Equal(before + 3, tree.Count);
        Assert.Equal("Content", tree[92].Name);
        Assert.Equal(91, tree[92].ParentId);

        // And the path it now builds is the one a person would recognise.
        Assert.Equal(@"T:\Games\Foo\Content", PathBuilder.BuildPath(tree, 92, "T:"));
    }

    /// <summary>Nothing to do is not an error, and costs no queries worth caring about.</summary>
    [Fact]
    public void StitchingOnAKnownDirectoryChangesNothing()
    {
        WriteBaseline();

        var tree = IncrementalRepository.LoadDirectoryTree(_connection, _volumeId);
        var before = tree.Count;

        Assert.Equal(0, IncrementalRepository.AddMissingNodes(
            _connection, null, _volumeId, tree, [RootDirId, 20, 21]));

        Assert.Equal(before, tree.Count);
    }

    /// <summary>
    /// A directory created and removed inside one tick has no row to find. That is an ordinary
    /// outcome on a live filesystem, not a failure.
    /// </summary>
    [Fact]
    public void AVanishedDirectoryIsSkippedRatherThanThrowing()
    {
        WriteBaseline();

        var tree = IncrementalRepository.LoadDirectoryTree(_connection, _volumeId);

        Assert.Equal(0, IncrementalRepository.AddMissingNodes(
            _connection, null, _volumeId, tree, [123_456_789]));
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

        var snapshotId = SnapshotWriter.Write(_connection, _volumeId, result, new SnapshotOptions
        {
            MaxDepth = 8,
            SizeFloorBytes = 10 * MB,
        });

        DirectoryHistoryRepository.MergeNames(_connection, null, _volumeId, result.StartedUtc);
        DirectoryHistoryRepository.WriteAnchor(_connection, null, _volumeId, result.StartedUtc);

        return snapshotId;
    }

    private async Task<List<TreeNode>> WalkHistory(long rootDirId, DateTime instant, int depth)
    {
        var nodes = new List<TreeNode>();

        await foreach (var node in HistoryTreeWalk.WalkAsync(
            _connection, _volumeId, rootDirId, instant, depth, limit: 10_000, CancellationToken.None))
        {
            nodes.Add(node);
        }

        return nodes;
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

    private static DateTime At(int minutes)
        => new DateTime(2026, 3, 1, 9, 0, 0, DateTimeKind.Utc).AddMinutes(minutes);

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
