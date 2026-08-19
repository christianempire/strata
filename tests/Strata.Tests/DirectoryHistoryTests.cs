using Microsoft.Data.Sqlite;
using Strata.Core.Model;
using Strata.Core.Rollup;
using Strata.Core.Scanning;
using Strata.Storage;

namespace Strata.Tests;

/// <summary>
/// The sparse directory history that replaces the per-snapshot rollup.
/// </summary>
/// <remarks>
/// What is at stake is that history stays truthful while being written far less often. The rollup it
/// replaces was self-evidently complete because it wrote everything every time; this one writes only
/// what moved, so the properties that used to be free — every directory has a size at every moment,
/// nothing is double counted, names survive their directory — have to be asserted.
/// </remarks>
public sealed class DirectoryHistoryTests : IDisposable
{
    private const long MB = 1024 * 1024;
    private const long GB = 1024 * MB;
    private const long RootDirId = 5;

    private readonly string _directory;
    private readonly StrataDatabase _database;
    private readonly SqliteConnection _connection;
    private readonly long _volumeId;

    public DirectoryHistoryTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "strata-hist-" + Guid.NewGuid().ToString("N"));
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

    /// <summary>
    /// The anchor covers every directory, not only the large ones.
    /// </summary>
    /// <remarks>
    /// The Explorer drills down and it can drill anywhere. A small directory that never changes
    /// would otherwise have no row at all, and "no row at or before T" is indistinguishable from
    /// "did not exist" — which for a folder that was simply quiet is a lie.
    /// </remarks>
    [Fact]
    public void TheBaselineAnchorCoversEveryDirectoryIncludingTheTinyOnes()
    {
        WriteBaseline();

        Assert.Equal(Tree().Count, Rows("SELECT COUNT(*) FROM dir_history;"));
        Assert.Equal(Tree().Count, Rows("SELECT COUNT(*) FROM dir_names;"));

        // The 1 MB directory is far below the 10 MB floor that bounds snapshot_dirs, and is
        // recorded here regardless.
        Assert.Equal(1, Rows("SELECT COUNT(*) FROM dir_history WHERE dir_id = 25;"));
    }

    [Fact]
    public void ADirectoryThatHasNotMovedEarnsNoRow()
    {
        WriteBaseline();
        var afterAnchor = Rows("SELECT COUNT(*) FROM dir_history;");

        var written = DirectoryHistoryRepository.Record(
            _connection, null, _volumeId, At(1), [RootDirId, 20, 21, 22], bandBytes: 0);

        Assert.Equal(0, written);
        Assert.Equal(afterAnchor, Rows("SELECT COUNT(*) FROM dir_history;"));
    }

    [Fact]
    public void ADirectoryThatMovedEarnsExactlyOneRow()
    {
        WriteBaseline();

        Grow(22, 5 * GB, 50);

        var written = DirectoryHistoryRepository.Record(
            _connection, null, _volumeId, At(1), [22], bandBytes: 0);

        Assert.Equal(1, written);
        Assert.Equal(65 * GB, Latest(22));
    }

    /// <summary>
    /// The dead-band is measured against the last value written, not against the last tick, so a
    /// directory creeping upward is recorded once its accumulated drift crosses the threshold.
    /// </summary>
    /// <remarks>
    /// This is the property that makes a band safe. Comparing against the previous tick instead
    /// would let a directory growing steadily by just under the band never be recorded at all,
    /// which is the exact shape of a log directory or a download in progress.
    /// </remarks>
    [Fact]
    public void DriftAccumulatesUntilItCrossesTheBand()
    {
        WriteBaseline();

        var band = 10 * MB;

        // Four nudges of 3 MB. Individually under the band; together over it.
        for (var tick = 1; tick <= 3; tick++)
        {
            Grow(22, 3 * MB, 1);
            Assert.Equal(0, DirectoryHistoryRepository.Record(
                _connection, null, _volumeId, At(tick), [22], band));
        }

        Grow(22, 3 * MB, 1);

        Assert.Equal(1, DirectoryHistoryRepository.Record(
            _connection, null, _volumeId, At(4), [22], band));

        // And what was recorded is the current total, not the increment.
        Assert.Equal((60 * GB) + (12 * MB), Latest(22));
    }

    /// <summary>Shrinking counts as drift too. A band is a magnitude, not a floor.</summary>
    [Fact]
    public void ShrinkingCrossesTheBandAsReadilyAsGrowing()
    {
        WriteBaseline();
        Grow(22, -20 * MB, -5);

        Assert.Equal(1, DirectoryHistoryRepository.Record(
            _connection, null, _volumeId, At(1), [22], 10 * MB));
    }

    /// <summary>
    /// The size of a directory at a moment is the latest row at or before it, which has to hold for
    /// a directory that has not been written since long before that moment.
    /// </summary>
    [Fact]
    public void StateAtReadsTheLatestMeasurementAtOrBeforeTheInstant()
    {
        WriteBaseline();

        Grow(22, 5 * GB, 50);
        DirectoryHistoryRepository.Record(_connection, null, _volumeId, At(10), [22], 0);

        Grow(22, 5 * GB, 50);
        DirectoryHistoryRepository.Record(_connection, null, _volumeId, At(20), [22], 0);

        // Before either change: the anchor.
        Assert.Equal(60 * GB, DirectoryHistoryRepository.StateAt(_connection, _volumeId, At(5))[22].SubtreeBytes);

        // Between them, and exactly on the second one.
        Assert.Equal(65 * GB, DirectoryHistoryRepository.StateAt(_connection, _volumeId, At(15))[22].SubtreeBytes);
        Assert.Equal(70 * GB, DirectoryHistoryRepository.StateAt(_connection, _volumeId, At(20))[22].SubtreeBytes);

        // Long after, with nothing written since.
        Assert.Equal(70 * GB, DirectoryHistoryRepository.StateAt(_connection, _volumeId, At(9_999))[22].SubtreeBytes);

        // A directory that never moved still has its anchor, which is the whole point of covering
        // every directory rather than only the ones over a floor.
        Assert.Equal(1 * MB, DirectoryHistoryRepository.StateAt(_connection, _volumeId, At(9_999))[25].SubtreeBytes);
    }

    /// <summary>A moment before the volume was ever scanned has nothing to report.</summary>
    [Fact]
    public void StateBeforeAnyMeasurementIsEmpty()
    {
        WriteBaseline();

        Assert.Empty(DirectoryHistoryRepository.StateAt(
            _connection, _volumeId, new DateTime(1990, 1, 1, 0, 0, 0, DateTimeKind.Utc)));
    }

    /// <summary>
    /// Names outlive a baseline, which truncates dir_totals and rebuilds it.
    /// </summary>
    /// <remarks>
    /// This is the hazard the separate table exists for. Without it, every history row older than
    /// the most recent baseline would lose the path it refers to — history that is still there and
    /// no longer readable, which is worse than history that was never kept.
    /// </remarks>
    [Fact]
    public void NamesSurviveABaselineThatRebuildsTheDirectoryTotals()
    {
        WriteBaseline();

        // A directory that existed at the first baseline and is gone by the second.
        Assert.Equal("Documents", Text("SELECT name FROM dir_names WHERE dir_id = 22;"));

        var withoutDocuments = Tree();
        withoutDocuments.Remove(22);
        WriteBaseline(withoutDocuments);

        Assert.Equal(0, Rows("SELECT COUNT(*) FROM dir_totals WHERE dir_id = 22;"));
        Assert.Equal("Documents", Text("SELECT name FROM dir_names WHERE dir_id = 22;"));

        // And its measurements are still there, still attributable to a path.
        var names = DirectoryHistoryRepository.LoadNames(_connection, _volumeId);
        Assert.Equal(@"T:\Users\chris\Documents", PathBuilder.BuildPath(names, 22, "T:"));
    }

    /// <summary>
    /// A directory Tier 2 discovers is added to the name dimension as it is added to the totals.
    /// </summary>
    /// <remarks>
    /// Reconciling later would not do: a directory created and deleted between two baselines would
    /// never be seen by either, and its bytes are exactly the kind a person goes looking for.
    /// </remarks>
    [Fact]
    public void ADirectoryDiscoveredByTheIncrementalScanIsNamedImmediately()
    {
        WriteBaseline();

        using (var store = new SqliteFileStateStore(_connection, _volumeId))
        {
            store.AddDirectory(90, RootDirId, "Games");
            store.AddDirectory(91, 90, "Foo");
        }

        var names = DirectoryHistoryRepository.LoadNames(_connection, _volumeId);

        Assert.Equal(@"T:\Games\Foo", PathBuilder.BuildPath(names, 91, "T:"));
    }

    // --- fixtures ---------------------------------------------------------------------------

    private static Dictionary<long, DirectoryNode> Tree() => new()
    {
        [RootDirId] = new(RootDirId, RootDirId, ""),
        [20] = new(20, RootDirId, "Users"),
        [21] = new(21, 20, "chris"),
        [22] = new(22, 21, "Documents") { OwnBytes = 60 * GB, OwnFileCount = 600 },
        [24] = new(24, RootDirId, "Windows") { OwnBytes = 20 * GB, OwnFileCount = 200 },
        [25] = new(25, 24, "tiny") { OwnBytes = 1 * MB, OwnFileCount = 1 },
    };

    private void WriteBaseline(Dictionary<long, DirectoryNode>? nodes = null)
    {
        var rollup = RollupEngine.Compute(nodes ?? Tree(), RootDirId);

        var result = new ScanResult
        {
            Volume = Volume(),
            Tier = ScanTier.Full,
            StartedUtc = At(0),
            Duration = TimeSpan.FromSeconds(3),
            Rollup = rollup,
            FileCount = rollup.TotalFileCount,
            DirectoryCount = rollup.TotalDirCount,
            TotalBytes = rollup.TotalBytes,
            TotalSizeOnDisk = rollup.TotalSizeOnDisk,
        };

        SnapshotWriter.Write(_connection, _volumeId, result);
        DirectoryHistoryRepository.MergeNames(_connection, null, _volumeId, result.StartedUtc);
        DirectoryHistoryRepository.WriteAnchor(_connection, null, _volumeId, result.StartedUtc);
    }

    /// <summary>Moves a directory's maintained total, as an incremental tick would.</summary>
    private void Grow(long dirId, long bytes, int files)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = """
            UPDATE dir_totals
            SET subtree_bytes = subtree_bytes + $b, subtree_files = subtree_files + $f
            WHERE volume_id = $v AND dir_id = $d;
            """;

        command.Parameters.AddWithValue("$v", _volumeId);
        command.Parameters.AddWithValue("$d", dirId);
        command.Parameters.AddWithValue("$b", bytes);
        command.Parameters.AddWithValue("$f", files);
        command.ExecuteNonQuery();
    }

    private static DateTime At(int minutes)
        => new DateTime(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc).AddMinutes(minutes);

    /// <summary>The most recent measurement recorded for a directory.</summary>
    private long Latest(long dirId) => Rows(
        $"SELECT subtree_bytes FROM dir_history WHERE dir_id = {dirId} " +
        "ORDER BY taken_unix DESC LIMIT 1;");

    private long Rows(string sql)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        var value = command.ExecuteScalar();
        return value is null or DBNull ? 0 : Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    private string Text(string sql)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar() as string ?? "";
    }

    private static VolumeIdentity Volume() => new()
    {
        GuidPath = @"\\?\Volume{33333333-3333-3333-3333-333333333333}\",
        DriveLetter = "T:",
        Label = "Test",
        FileSystem = "NTFS",
        Kind = VolumeKind.Fixed,
        TotalBytes = 200 * GB,
        FreeBytes = 100 * GB,
    };
}
