using Microsoft.Data.Sqlite;
using Strata.Core.Model;
using Strata.Core.Rollup;
using Strata.Core.Scanning;
using Strata.Storage;

namespace Strata.Tests;

public sealed class StorageTests : IDisposable
{
    private readonly string _directory;
    private readonly StrataDatabase _database;

    public StorageTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "strata-db-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        _database = new StrataDatabase(Path.Combine(_directory, "test.db"));
        _database.Migrate();
    }

    public void Dispose()
    {
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
    public void MigrationCreatesEveryTable()
    {
        using var connection = _database.Open();

        var tables = QueryStrings(connection, "SELECT name FROM sqlite_master WHERE type = 'table';");

        Assert.Contains("volumes", tables);
        Assert.Contains("files", tables);
        Assert.Contains("dir_totals", tables);
        Assert.Contains("snapshots", tables);
        Assert.Contains("dir_history", tables);
        Assert.Contains("dir_names", tables);
        Assert.Contains("snapshot_top_files", tables);
        Assert.Contains("events", tables);
        Assert.Contains("usn_cursors", tables);
    }

    [Fact]
    public void MigrationIsIdempotent()
    {
        using var connection = _database.Open();
        var afterFirst = QueryLong(connection, "SELECT COUNT(*) FROM schema_migrations;");

        _database.Migrate();
        _database.Migrate();

        var afterThird = QueryLong(connection, "SELECT COUNT(*) FROM schema_migrations;");

        Assert.True(afterFirst > 0, "no migrations were applied at all");
        Assert.Equal(afterFirst, afterThird);
    }

    /// <summary>
    /// Migrations are applied in numeric order, not in whatever order the assembly happens to list
    /// its embedded resources. 002 rebuilds a table 001 created, so the order is load-bearing.
    /// </summary>
    [Fact]
    public void MigrationsAreAppliedInOrder()
    {
        using var connection = _database.Open();

        var versions = QueryStrings(connection, "SELECT version FROM schema_migrations ORDER BY version;")
            .Select(int.Parse)
            .ToList();

        Assert.Equal(versions.OrderBy(v => v), versions);
        Assert.Contains(1, versions);
        Assert.Contains(2, versions);
    }

    [Fact]
    public void WalModeIsOn()
    {
        using var connection = _database.Open();
        var mode = QueryStrings(connection, "PRAGMA journal_mode;").Single();

        Assert.Equal("wal", mode, ignoreCase: true);
    }

    [Fact]
    public void VolumeUpsertIsStableOnGuidAndRefreshesTheRest()
    {
        using var connection = _database.Open();

        var first = VolumeRepository.Upsert(connection, Volume());

        // Same volume, new letter and less free space: the same row, updated.
        var second = VolumeRepository.Upsert(connection, Volume() with
        {
            DriveLetter = "X:",
            FreeBytes = 1234,
        });

        Assert.Equal(first, second);
        Assert.Equal(1, QueryLong(connection, "SELECT COUNT(*) FROM volumes;"));
        Assert.Equal("X:", QueryStrings(connection, "SELECT drive_letter FROM volumes;").Single());
        Assert.Equal(1234, QueryLong(connection, "SELECT free_bytes FROM volumes;"));
    }

    [Fact]
    public void FileSinkWritesEveryRecord()
    {
        using var connection = _database.Open();
        var volumeId = VolumeRepository.Upsert(connection, Volume());

        using (var sink = new SqliteFileSink(connection, volumeId))
        {
            var batch = new FileRecord[500];

            for (var i = 0; i < batch.Length; i++)
            {
                batch[i] = Record(fileId: 1000 + i, parentId: 5, name: $"f{i}.bin", size: i);
            }

            sink.Write(batch);
            sink.Complete();

            Assert.Equal(500, sink.RowsWritten);
        }

        Assert.Equal(500, QueryLong(connection, "SELECT COUNT(*) FROM files;"));
        Assert.Equal(124_750, QueryLong(connection, "SELECT SUM(logical_size) FROM files;"));
    }

    /// <summary>
    /// A full baseline scan is the authoritative view of a volume, so a rescan must replace what was
    /// there rather than merging with it, or deleted files would live on forever.
    /// </summary>
    [Fact]
    public void RescanReplacesTheVolumeRatherThanAccumulating()
    {
        using var connection = _database.Open();
        var volumeId = VolumeRepository.Upsert(connection, Volume());

        using (var first = new SqliteFileSink(connection, volumeId))
        {
            first.Write([Record(1, 5, "old.bin", 100), Record(2, 5, "gone.bin", 200)]);
            first.Complete();
        }

        using (var second = new SqliteFileSink(connection, volumeId))
        {
            second.Write([Record(1, 5, "old.bin", 100)]);
            second.Complete();
        }

        Assert.Equal(1, QueryLong(connection, "SELECT COUNT(*) FROM files;"));
    }

    [Fact]
    public void SinkHandlesManyBatchesFromConcurrentWriters()
    {
        using var connection = _database.Open();
        var volumeId = VolumeRepository.Upsert(connection, Volume());

        const int writers = 4;
        const int batchesPerWriter = 25;
        const int batchSize = 100;

        using (var sink = new SqliteFileSink(connection, volumeId))
        {
            Parallel.For(0, writers, writer =>
            {
                var batch = new FileRecord[batchSize];

                for (var b = 0; b < batchesPerWriter; b++)
                {
                    for (var i = 0; i < batchSize; i++)
                    {
                        var id = (writer * batchesPerWriter * batchSize) + (b * batchSize) + i + 1;
                        batch[i] = Record(id, 5, $"f{id}.bin", 1);
                    }

                    sink.Write(batch);
                }
            });

            sink.Complete();
        }

        Assert.Equal(
            writers * batchesPerWriter * batchSize,
            QueryLong(connection, "SELECT COUNT(*) FROM files;"));
    }

    /// <summary>
    /// Hardlinks share a file ID but are distinct directory entries, so each must get its own row.
    /// Under the original key of (volume_id, file_id) they collided and all but one path was lost —
    /// 135,520 of 3.67M entries on the target machine.
    /// </summary>
    [Theory]
    [InlineData(false)] // owner written first
    [InlineData(true)]  // link written first
    public void EveryHardLinkGetsItsOwnRow(bool linkFirst)
    {
        using var connection = _database.Open();
        var volumeId = VolumeRepository.Upsert(connection, Volume());

        var owner = new FileRecord(77, 5, "owner.dll", 4_096, 4_096, 0x80, DateTime.UnixEpoch, IsHardLink: false);
        var link = new FileRecord(77, 9, "link.dll", 4_096, 4_096, 0x80, DateTime.UnixEpoch, IsHardLink: true);

        using (var sink = new SqliteFileSink(connection, volumeId))
        {
            sink.Write(linkFirst ? [link, owner] : [owner, link]);
            sink.Complete();
        }

        Assert.Equal(2, QueryLong(connection, "SELECT COUNT(*) FROM files;"));

        // Both entries point at the same file...
        Assert.Equal(1, QueryLong(connection, "SELECT COUNT(DISTINCT file_id) FROM files;"));

        // ...but only one of them owns the bytes, so rollups cannot double-count.
        Assert.Equal(1, QueryLong(connection, "SELECT COUNT(*) FROM files WHERE is_hard_link = 0;"));
        Assert.Equal(
            "owner.dll",
            QueryStrings(connection, "SELECT name FROM files WHERE is_hard_link = 0;").Single());
    }

    /// <summary>
    /// The same name in two different directories is two entries; the same name in one directory is
    /// impossible. The primary key has to encode both facts.
    /// </summary>
    [Fact]
    public void SameNameInDifferentDirectoriesCoexists()
    {
        using var connection = _database.Open();
        var volumeId = VolumeRepository.Upsert(connection, Volume());

        using (var sink = new SqliteFileSink(connection, volumeId))
        {
            sink.Write([
                new FileRecord(1, 10, "readme.md", 5, 5, 0x80, DateTime.UnixEpoch, false),
                new FileRecord(2, 11, "readme.md", 7, 7, 0x80, DateTime.UnixEpoch, false),
            ]);
            sink.Complete();
        }

        Assert.Equal(2, QueryLong(connection, "SELECT COUNT(*) FROM files;"));
    }

    /// <summary>
    /// A file can have two links in the same directory under different names. Rare, but legal, and
    /// the key must not conflate them.
    /// </summary>
    [Fact]
    public void TwoLinksToOneFileInTheSameDirectoryCoexist()
    {
        using var connection = _database.Open();
        var volumeId = VolumeRepository.Upsert(connection, Volume());

        using (var sink = new SqliteFileSink(connection, volumeId))
        {
            sink.Write([
                new FileRecord(42, 10, "first.dll", 100, 100, 0x80, DateTime.UnixEpoch, false),
                new FileRecord(42, 10, "second.dll", 100, 100, 0x80, DateTime.UnixEpoch, true),
            ]);
            sink.Complete();
        }

        Assert.Equal(2, QueryLong(connection, "SELECT COUNT(*) FROM files;"));
    }

    [Fact]
    public void TimestampsRoundTripAsUnixSeconds()
    {
        using var connection = _database.Open();
        var volumeId = VolumeRepository.Upsert(connection, Volume());

        var when = new DateTime(2024, 3, 1, 8, 15, 30, DateTimeKind.Utc);

        using (var sink = new SqliteFileSink(connection, volumeId))
        {
            sink.Write([new FileRecord(1, 5, "a.bin", 1, 1, 0x80, when, IsHardLink: false)]);
            sink.Complete();
        }

        var stored = QueryLong(connection, "SELECT modified_unix FROM files;");

        Assert.Equal(new DateTimeOffset(when, TimeSpan.Zero).ToUnixTimeSeconds(), stored);
    }

    [Fact]
    public void SnapshotWriterRecordsTheScanAndItsRollup()
    {
        using var connection = _database.Open();
        var volumeId = VolumeRepository.Upsert(connection, Volume());

        var result = ScanOf(BuildRollup());
        var snapshotId = SnapshotWriter.Write(connection, volumeId, result);

        Assert.True(snapshotId > 0);
        Assert.Equal(1, QueryLong(connection, "SELECT COUNT(*) FROM snapshots;"));
        Assert.Equal(
            result.TotalBytes,
            QueryLong(connection, "SELECT total_bytes FROM snapshots;"));

        // dir_totals holds every directory regardless of size, because Phase 2 maintains it
        // incrementally and needs a row for any ancestor of any changed file.
        Assert.Equal(4, QueryLong(connection, "SELECT COUNT(*) FROM dir_totals;"));
    }

    private static Dictionary<long, DirectoryNode> BuildRollup()
    {
        var nodes = new Dictionary<long, DirectoryNode>
        {
            [5] = new(5, 5, ""),
            [10] = new(10, 5, "deep") { OwnBytes = 5_000, OwnFileCount = 1 },
            [11] = new(11, 10, "deeper") { OwnBytes = 4_000, OwnFileCount = 1 },
            [12] = new(12, 5, "tiny") { OwnBytes = 10, OwnFileCount = 1 },
        };

        return nodes;
    }

    private static ScanResult ScanOf(Dictionary<long, DirectoryNode> nodes)
    {
        var rollup = RollupEngine.Compute(nodes, 5);

        return new ScanResult
        {
            Volume = Volume(),
            Tier = ScanTier.Full,
            StartedUtc = DateTime.UtcNow,
            Duration = TimeSpan.FromSeconds(3),
            Rollup = rollup,
            FileCount = rollup.TotalFileCount,
            DirectoryCount = rollup.TotalDirCount,
            TotalBytes = rollup.TotalBytes,
            TotalSizeOnDisk = rollup.TotalSizeOnDisk,
        };
    }

    private static FileRecord Record(long fileId, long parentId, string name, long size)
        => new(fileId, parentId, name, size, size, 0x80, DateTime.UnixEpoch, IsHardLink: false);

    private static VolumeIdentity Volume() => new()
    {
        GuidPath = @"\\?\Volume{11111111-1111-1111-1111-111111111111}\",
        DriveLetter = "T:",
        Label = "Test",
        FileSystem = "NTFS",
        Kind = VolumeKind.Fixed,
        TotalBytes = 1_000_000,
        FreeBytes = 500_000,
    };

    private static long QueryLong(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        var value = command.ExecuteScalar();
        return value is null or DBNull ? 0 : Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static List<string> QueryStrings(SqliteConnection connection, string sql)
    {
        var results = new List<string>();

        using var command = connection.CreateCommand();
        command.CommandText = sql;

        using var reader = command.ExecuteReader();

        while (reader.Read())
        {
            results.Add(reader.IsDBNull(0) ? "" : reader.GetString(0));
        }

        return results;
    }
}
