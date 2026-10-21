using Microsoft.Data.Sqlite;
using Strata.Core.Diffing;
using Strata.Core.Model;
using Strata.Core.Rollup;
using Strata.Core.Scanning;
using Strata.Storage;

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
