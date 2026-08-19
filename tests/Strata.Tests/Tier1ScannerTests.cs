using Strata.Core.Model;
using Strata.Core.Scanning;
using Strata.Tests.Fakes;

namespace Strata.Tests;

public sealed class Tier1ScannerTests
{
    [Fact]
    public void ScansAWholeTreeAndTotalsIt()
    {
        var fs = new FakeDirectoryEnumerator();
        fs.AddDirectory("Steam");
        fs.AddDirectory(@"Steam\steamapps");
        fs.AddDirectory(@"Steam\steamapps\common");
        fs.AddFile(@"Steam\steamapps\common\game.pak", 32_000);
        fs.AddFile(@"Steam\readme.txt", 1_000);
        fs.AddFile("boot.ini", 500);

        var result = Scan(fs);

        Assert.Equal(33_500, result.TotalBytes);
        Assert.Equal(3, result.FileCount);
        Assert.Equal(4, result.DirectoryCount); // root, Steam, steamapps, common
    }

    [Fact]
    public void EveryFileReachesTheSink()
    {
        var fs = new FakeDirectoryEnumerator();
        fs.AddDirectory("a");
        fs.AddDirectory(@"a\b");

        for (var i = 0; i < 250; i++)
        {
            fs.AddFile($@"a\b\f{i}.bin", 10);
        }

        var sink = new RecordingSink();
        Scan(fs, sink: sink);

        Assert.Equal(250, sink.Records.Count);
        Assert.Equal(2500, sink.Records.Sum(static r => r.LogicalSize));
    }

    /// <summary>
    /// The batch buffer is pooled and reused, so a sink that retains the span would see garbage.
    /// Exercising a file count well past the batch size is what catches that mistake.
    /// </summary>
    [Fact]
    public void BatchingAcrossManyFilesLosesNothing()
    {
        var fs = new FakeDirectoryEnumerator();
        const int fileCount = 5_000;

        for (var i = 0; i < fileCount; i++)
        {
            fs.AddDirectory($"d{i}");
            fs.AddFile($@"d{i}\f.bin", 7);
        }

        var sink = new RecordingSink();
        var result = Scan(fs, new ScanOptions { WriteBatchSize = 64, DegreeOfParallelism = 4 }, sink);

        Assert.Equal(fileCount, sink.Records.Count);
        Assert.Equal(fileCount * 7, result.TotalBytes);
    }

    /// <summary>
    /// A junction into a tree that has already been counted must not have its bytes counted again,
    /// and must not be walked. Following one is how a scanner ends up either looping forever or
    /// reporting a volume as larger than it is.
    /// </summary>
    [Fact]
    public void ReparsePointsAreRecordedButNotTraversed()
    {
        var fs = new FakeDirectoryEnumerator();
        fs.AddDirectory("real");
        fs.AddFile(@"real\big.bin", 1_000_000);
        fs.AddDirectory("link", reparsePoint: true);
        fs.AddFile(@"link\big.bin", 1_000_000);

        var result = Scan(fs);

        Assert.Equal(1, result.ReparsePointCount);
        Assert.Equal(1_000_000, result.TotalBytes);
        Assert.Equal(1, result.FileCount);
    }

    [Fact]
    public void ADirectoryReachedTwiceIsNotWalkedTwice()
    {
        // Two names for the same directory ID, which is the shape a cycle takes.
        var fs = new FakeDirectoryEnumerator();
        var sharedId = fs.AddDirectory("first");
        fs.AddDirectory("second", explicitId: sharedId);
        fs.AddFile(@"first\payload.bin", 4_096);

        var result = Scan(fs);

        Assert.Equal(4_096, result.TotalBytes);
        Assert.Equal(1, result.FileCount);
    }

    [Fact]
    public void HardLinkedBytesAreCountedOnce()
    {
        var fs = new FakeDirectoryEnumerator();
        fs.AddDirectory("System32");
        fs.AddDirectory("WinSxS");

        var sharedId = fs.AddFile(@"System32\shared.dll", 5_000_000);
        fs.AddFile(@"WinSxS\shared.dll", 5_000_000, explicitId: sharedId);

        var result = Scan(fs, new ScanOptions { DegreeOfParallelism = 1 });

        Assert.Equal(5_000_000, result.TotalBytes);
        Assert.Equal(2, result.FileCount);
        Assert.Equal(1, result.HardLinkCount);
        Assert.Equal(5_000_000, result.HardLinkBytesSaved);
    }

    [Fact]
    public void HardLinkDeduplicationCanBeTurnedOff()
    {
        var fs = new FakeDirectoryEnumerator();
        fs.AddDirectory("a");
        fs.AddDirectory("b");

        var sharedId = fs.AddFile(@"a\shared.dll", 1_000);
        fs.AddFile(@"b\shared.dll", 1_000, explicitId: sharedId);

        var result = Scan(fs, new ScanOptions { DeduplicateHardLinks = false });

        Assert.Equal(2_000, result.TotalBytes);
        Assert.Equal(0, result.HardLinkCount);
    }

    /// <summary>
    /// Directory rollups summing exactly to the volume total is the property the hardlink rule
    /// exists to preserve, so it is asserted directly rather than inferred.
    /// </summary>
    [Fact]
    public void RollupsSumToTheVolumeTotalWhenHardLinksArePresent()
    {
        var fs = new FakeDirectoryEnumerator();
        fs.AddDirectory("one");
        fs.AddDirectory("two");

        var sharedId = fs.AddFile(@"one\shared.bin", 900);
        fs.AddFile(@"two\shared.bin", 900, explicitId: sharedId);
        fs.AddFile(@"two\own.bin", 100);

        var result = Scan(fs, new ScanOptions { DegreeOfParallelism = 1 });

        var childSum = result.Rollup.Nodes.Values
            .Where(n => n.Depth == 1)
            .Sum(n => n.SubtreeBytes);

        Assert.Equal(result.TotalBytes, childSum + result.Rollup.Root.OwnBytes);
        Assert.Equal(1_000, result.TotalBytes);
    }

    [Fact]
    public void InaccessibleDirectoriesAreCountedNotThrown()
    {
        var fs = new FakeDirectoryEnumerator();
        fs.AddDirectory("readable");
        fs.AddFile(@"readable\ok.bin", 42);
        fs.AddDirectory("locked");
        fs.FailOn("locked", DirectoryEnumerationError.AccessDenied);

        var result = Scan(fs);

        Assert.Equal(42, result.TotalBytes);
        Assert.Equal(1, result.InaccessibleTotal);
        Assert.Equal(1, result.Inaccessible[DirectoryEnumerationError.AccessDenied]);
    }

    [Fact]
    public void ADirectoryDeletedMidScanIsSurvivable()
    {
        var fs = new FakeDirectoryEnumerator();
        fs.AddDirectory("vanishing");
        fs.FailOn("vanishing", DirectoryEnumerationError.NotFound);
        fs.AddFile("kept.bin", 8);

        var result = Scan(fs);

        Assert.Equal(8, result.TotalBytes);
        Assert.Equal(1, result.Inaccessible[DirectoryEnumerationError.NotFound]);
    }

    [Fact]
    public void ExcludedDirectoryNamesAreSkippedEntirely()
    {
        var fs = new FakeDirectoryEnumerator();
        fs.AddDirectory("keep");
        fs.AddFile(@"keep\a.bin", 10);
        fs.AddDirectory("node_modules");
        fs.AddFile(@"node_modules\huge.bin", 999_999);

        var result = Scan(fs, new ScanOptions { ExcludedDirectoryNames = ["node_modules"] });

        Assert.Equal(10, result.TotalBytes);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(8)]
    public void ResultsAreIdenticalRegardlessOfWorkerCount(int workers)
    {
        var fs = new FakeDirectoryEnumerator();

        for (var i = 0; i < 40; i++)
        {
            fs.AddDirectory($"d{i}");

            for (var j = 0; j < 25; j++)
            {
                fs.AddDirectory($@"d{i}\s{j}");
                fs.AddFile($@"d{i}\s{j}\f.bin", 3);
            }
        }

        var result = Scan(fs, new ScanOptions { DegreeOfParallelism = workers });

        Assert.Equal(40 * 25 * 3, result.TotalBytes);
        Assert.Equal(40 * 25, result.FileCount);
    }

    [Fact]
    public void BackgroundIoScopeIsEnteredOncePerWorker()
    {
        var fs = new FakeDirectoryEnumerator();
        fs.AddFile("a.bin", 1);

        Scan(fs, new ScanOptions { DegreeOfParallelism = 3 });

        Assert.Equal(3, fs.BackgroundScopesEntered);
    }

    [Fact]
    public void BackgroundIoScopeIsSkippedWhenDisabled()
    {
        var fs = new FakeDirectoryEnumerator();
        fs.AddFile("a.bin", 1);

        Scan(fs, new ScanOptions { DegreeOfParallelism = 2, UseBackgroundIoPriority = false });

        Assert.Equal(0, fs.BackgroundScopesEntered);
    }

    [Fact]
    public void AnUnreadableRootIsAnError()
    {
        var fs = new FakeDirectoryEnumerator();
        var scanner = new Tier1Scanner(fs);

        var volume = Volume() with { DriveLetter = "Z:" };

        Assert.Throws<IOException>(() => scanner.Scan(volume, NullScanSink.Instance, CancellationToken.None));
    }

    [Fact]
    public void SizeOnDiskIsTrackedSeparatelyFromLogicalSize()
    {
        var fs = new FakeDirectoryEnumerator();
        fs.AddFile("sparse.vhd", logicalSize: 10_000_000, sizeOnDisk: 65_536);

        var result = Scan(fs);

        Assert.Equal(10_000_000, result.TotalBytes);
        Assert.Equal(65_536, result.TotalSizeOnDisk);
    }

    [Fact]
    public void FilesRecordTheirParentDirectoryId()
    {
        var fs = new FakeDirectoryEnumerator();
        var parentId = fs.AddDirectory("games");
        fs.AddFile(@"games\a.bin", 1);

        var sink = new RecordingSink();
        Scan(fs, sink: sink);

        Assert.Equal(parentId, sink.Records.Single().ParentId);
    }

    private static ScanResult Scan(
        FakeDirectoryEnumerator fs,
        ScanOptions? options = null,
        IScanSink? sink = null)
    {
        var scanner = new Tier1Scanner(fs, options ?? new ScanOptions { DegreeOfParallelism = 2 });
        return scanner.Scan(Volume(), sink ?? NullScanSink.Instance, CancellationToken.None);
    }

    private static VolumeIdentity Volume() => new()
    {
        GuidPath = @"\\?\Volume{00000000-0000-0000-0000-000000000000}\",
        DriveLetter = "T:",
        FileSystem = "NTFS",
        Kind = VolumeKind.Fixed,
        TotalBytes = 1_000_000_000,
        FreeBytes = 500_000_000,
    };

    private sealed class RecordingSink : IScanSink
    {
        private readonly Lock _gate = new();

        internal List<FileRecord> Records { get; } = [];

        public void Write(ReadOnlySpan<FileRecord> batch)
        {
            lock (_gate)
            {
                foreach (var record in batch)
                {
                    Records.Add(record);
                }
            }
        }
    }
}
