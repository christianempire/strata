using Microsoft.Data.Sqlite;
using Strata.Core.Diffing;
using Strata.Core.Model;
using Strata.Core.Rollup;
using Strata.Core.Scanning;
using Strata.Storage;
using Strata.Tests.Fakes;

namespace Strata.Tests;

/// <summary>
/// Keeping the directory rollup right between baselines, against a real database.
/// </summary>
/// <remarks>
/// The rollup is maintained by deltas for weeks at a time, so a delta applied to the wrong column or
/// lost on the way up the tree is not a rounding error: it accumulates until the next baseline.
/// </remarks>
public sealed class IncrementalRepositoryTests : IDisposable
{
    private const long MB = 1024 * 1024;
    private const long RootDirId = 5;

    private readonly string _directory;
    private readonly StrataDatabase _database;
    private readonly SqliteConnection _connection;
    private readonly long _volumeId;

    public IncrementalRepositoryTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "strata-incr-" + Guid.NewGuid().ToString("N"));
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

    [Fact]
    public void ADeltaMovesEachTotalByItsOwnAmountAllTheWayUp()
    {
        WriteBaseline();

        Apply(new Dictionary<long, DirectoryDelta>
        {
            [22] = new(40 * MB, 40 * MB, 1, 0, 3 * MB),
        });

        foreach (var dirId in new[] { 22L, 21, 20, RootDirId })
        {
            var (bytes, onDisk, files, _) = Totals(dirId);
            var (baseBytes, baseOnDisk, baseFiles, _) = BaselineTotals(dirId);

            Assert.Equal(baseBytes + (40 * MB), bytes);
            Assert.Equal(baseOnDisk + (3 * MB), onDisk);
            Assert.Equal(baseFiles + 1, files);
        }
    }

    /// <summary>
    /// A directory moved by a real tick must leave every total exactly where a fresh baseline of the
    /// moved tree would put it: bytes, size on disk, files, directories beneath, and depth.
    /// </summary>
    [Fact]
    public void AMoveLeavesTheSameTotalsAsABaselineOfTheMovedTree()
    {
        WriteBaseline();

        // Documents (with Notes beneath it) moves from Users\chris into Games.
        var journal = new FakeUsnJournal()
            .Add(Change(22, 21, "Documents", UsnChangeKind.RenamedFrom))
            .Add(Change(22, 24, "Documents", UsnChangeKind.RenamedTo));

        RunTick(journal);

        var moved = Tree();
        moved[22] = new(22, 24, "Documents") { OwnBytes = 600 * MB, OwnSizeOnDisk = 500 * MB, OwnFileCount = 60 };
        var expected = RollupEngine.Compute(moved, RootDirId).Nodes;

        foreach (var (dirId, node) in expected)
        {
            Assert.Equal(
                (node.SubtreeBytes, node.SubtreeSizeOnDisk, node.SubtreeFileCount, (long)node.SubtreeDirCount),
                Totals(dirId));
            Assert.Equal(node.Depth, Depth("dir_totals", dirId));
            Assert.Equal(node.Depth, Depth("dir_names", dirId));
        }
    }

    /// <summary>
    /// A deleted directory leaves the tree, and so does anything still recorded beneath it. NTFS
    /// will not delete a directory that holds anything, so whatever is left there is drift from
    /// earlier ticks, and it comes off every ancestor rather than staying in the totals for good.
    /// </summary>
    [Fact]
    public void ARemovedDirectoryTakesWhateverWasLeftBeneathItOffEveryAncestor()
    {
        WriteBaseline();
        InsertFile(23, "stale.bin", 10 * MB);

        var fanOut = Remove(22);

        Assert.Equal(0, Rows("SELECT COUNT(*) FROM dir_totals WHERE dir_id IN (22, 23);"));
        Assert.Equal(0, Rows("SELECT COUNT(*) FROM files WHERE parent_id = 23;"));

        foreach (var dirId in new[] { 21L, 20, RootDirId })
        {
            var (bytes, onDisk, files, dirs) = Totals(dirId);
            var (baseBytes, baseOnDisk, baseFiles, baseDirs) = BaselineTotals(dirId);

            Assert.Equal(baseBytes - (610 * MB), bytes);
            Assert.Equal(baseOnDisk - (510 * MB), onDisk);
            Assert.Equal(baseFiles - 70, files);
            Assert.Equal(baseDirs - 2, dirs);
        }

        Assert.Contains(21L, fanOut.Touched.Keys);
    }

    /// <summary>
    /// The Explorer reads a directory's size at an instant as its latest row at or before it, so a
    /// removed directory needs a last row of zero or it shows at its old size forever after.
    /// </summary>
    [Fact]
    public void ARemovedDirectoryEndsItsHistoryAtZero()
    {
        WriteBaseline();

        Remove(22);

        Assert.Equal(0, Rows($"SELECT subtree_bytes FROM dir_history WHERE dir_id = 22 AND taken_unix = {Unix(At(5))};"));
        Assert.Equal(0, Rows($"SELECT subtree_bytes FROM dir_history WHERE dir_id = 23 AND taken_unix = {Unix(At(5))};"));
    }

    /// <summary>A directory removed along with its parent is counted once, inside the parent.</summary>
    [Fact]
    public void NestedRemovalsAreCountedOnce()
    {
        WriteBaseline();

        Remove(23, 22);

        var (bytes, _, files, dirs) = Totals(RootDirId);
        var (baseBytes, _, baseFiles, baseDirs) = BaselineTotals(RootDirId);

        Assert.Equal(baseBytes - (610 * MB), bytes);
        Assert.Equal(baseFiles - 70, files);
        Assert.Equal(baseDirs - 2, dirs);
    }

    // --- fixtures ---------------------------------------------------------------------------

    private static Dictionary<long, DirectoryNode> Tree() => new()
    {
        [RootDirId] = new(RootDirId, RootDirId, ""),
        [20] = new(20, RootDirId, "Users"),
        [21] = new(21, 20, "chris"),
        [22] = new(22, 21, "Documents") { OwnBytes = 600 * MB, OwnSizeOnDisk = 500 * MB, OwnFileCount = 60 },
        [23] = new(23, 22, "Notes") { OwnBytes = 10 * MB, OwnSizeOnDisk = 10 * MB, OwnFileCount = 10 },
        [24] = new(24, RootDirId, "Games") { OwnBytes = 200 * MB, OwnSizeOnDisk = 200 * MB, OwnFileCount = 20 },
    };

    private Dictionary<long, (long Bytes, long OnDisk, long Files, long Dirs)> _baseline = [];

    private void WriteBaseline()
    {
        var rollup = RollupEngine.Compute(Tree(), RootDirId);

        SnapshotWriter.Write(_connection, _volumeId, new ScanResult
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
        });

        DirectoryHistoryRepository.MergeNames(_connection, null, _volumeId, At(0));
        DirectoryHistoryRepository.WriteAnchor(_connection, null, _volumeId, At(0));

        _baseline = Tree().Keys.ToDictionary(id => id, Totals);
    }

    private (long Bytes, long OnDisk, long Files, long Dirs) BaselineTotals(long dirId) => _baseline[dirId];

    private RollupFanOut Apply(IReadOnlyDictionary<long, DirectoryDelta> deltas)
    {
        using var transaction = _connection.BeginTransaction();
        var fanOut = IncrementalRepository.ApplyDeltas(_connection, transaction, _volumeId, deltas);
        transaction.Commit();
        return fanOut;
    }

    /// <summary>One tick through the real store and rollup, as VolumeWatcher runs it.</summary>
    private void RunTick(FakeUsnJournal journal)
    {
        using var transaction = _connection.BeginTransaction();

        Tier2Result result;

        using (var store = new SqliteFileStateStore(_connection, _volumeId, transaction))
        {
            result = new Tier2Scanner(journal, new FakeMetadataReader()).Run(new UsnCursor(42, 1), store);
        }

        IncrementalRepository.ApplyDeltas(_connection, transaction, _volumeId, result.Deltas);
        IncrementalRepository.RemoveDirectories(
            _connection, transaction, _volumeId, result.RemovedDirectories, At(5));
        transaction.Commit();
    }

    private RollupFanOut Remove(params long[] dirIds)
    {
        using var transaction = _connection.BeginTransaction();
        var fanOut = IncrementalRepository.RemoveDirectories(_connection, transaction, _volumeId, dirIds, At(5));
        transaction.Commit();
        return fanOut;
    }

    private void InsertFile(long parentId, string name, long size)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = """
            INSERT INTO files (volume_id, parent_id, name, file_id, logical_size, size_on_disk,
                               attributes, modified_unix, is_hard_link)
            VALUES ($v, $p, $n, $f, $s, $s, 0, 0, 0);
            """;
        command.Parameters.AddWithValue("$v", _volumeId);
        command.Parameters.AddWithValue("$p", parentId);
        command.Parameters.AddWithValue("$n", name);
        command.Parameters.AddWithValue("$f", 9000 + parentId);
        command.Parameters.AddWithValue("$s", size);
        command.ExecuteNonQuery();
    }

    private long Depth(string table, long dirId)
        => Rows($"SELECT depth FROM {table} WHERE volume_id = {_volumeId} AND dir_id = {dirId};");

    private static UsnChange Change(long dirId, long parentId, string name, UsnChangeKind kind)
        => new(0, dirId, parentId, name, kind, 0x10u, DateTime.UnixEpoch);

    private static long Unix(DateTime utc) => new DateTimeOffset(utc).ToUnixTimeSeconds();

    private (long Bytes, long OnDisk, long Files, long Dirs) Totals(long dirId)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = """
            SELECT subtree_bytes, subtree_on_disk, subtree_files, subtree_dirs
            FROM dir_totals WHERE volume_id = $v AND dir_id = $d;
            """;
        command.Parameters.AddWithValue("$v", _volumeId);
        command.Parameters.AddWithValue("$d", dirId);

        using var reader = command.ExecuteReader();
        Assert.True(reader.Read(), $"directory {dirId} is not in dir_totals");

        return (reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3));
    }

    private long Rows(string sql)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static DateTime At(int minutes)
        => new DateTime(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc).AddMinutes(minutes);

    private static VolumeIdentity Volume() => new()
    {
        GuidPath = @"\\?\Volume{33333333-3333-3333-3333-333333333333}\",
        DriveLetter = "T:",
        Label = "Test",
        FileSystem = "NTFS",
        Kind = VolumeKind.Fixed,
        TotalBytes = 10_000 * MB,
        FreeBytes = 9_000 * MB,
    };
}
