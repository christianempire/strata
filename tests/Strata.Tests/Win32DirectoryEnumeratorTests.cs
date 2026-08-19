using Strata.Core.Model;
using Strata.Core.Scanning;
using Strata.Platform;

namespace Strata.Tests;

/// <summary>
/// Exercises the real Win32 enumeration against a temporary tree on disk.
/// </summary>
/// <remarks>
/// These tests earn their keep by validating the FILE_ID_BOTH_DIR_INFO field offsets. That layout is
/// the one place in the scanner where a wrong constant yields plausible-looking garbage — shifted
/// names, nonsense sizes — rather than an error, so it is checked against a directory whose exact
/// contents are known.
/// </remarks>
public sealed class Win32DirectoryEnumeratorTests : IDisposable
{
    private readonly string _root;

    public Win32DirectoryEnumeratorTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "strata-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // A test leaving a temp directory behind is not worth failing a run over.
        }
    }

    [Fact]
    public void ReadsNamesAndSizesExactly()
    {
        File.WriteAllBytes(Path.Combine(_root, "alpha.bin"), new byte[1234]);
        File.WriteAllBytes(Path.Combine(_root, "beta.bin"), new byte[5678]);
        Directory.CreateDirectory(Path.Combine(_root, "childdir"));

        var entries = Enumerate(_root);

        Assert.Equal(3, entries.Count);

        var alpha = entries.Single(e => e.Name == "alpha.bin");
        Assert.Equal(1234, alpha.LogicalSize);
        Assert.False(alpha.IsDirectory);

        var beta = entries.Single(e => e.Name == "beta.bin");
        Assert.Equal(5678, beta.LogicalSize);

        var child = entries.Single(e => e.Name == "childdir");
        Assert.True(child.IsDirectory);
        Assert.Equal(0, child.LogicalSize);
    }

    [Fact]
    public void DotEntriesAreNotReturned()
    {
        Directory.CreateDirectory(Path.Combine(_root, "sub"));

        var entries = Enumerate(_root);

        Assert.DoesNotContain(entries, e => e.Name is "." or "..");
    }

    /// <summary>
    /// Size on disk comes from AllocationSize in the same record. If that offset were wrong this
    /// would come back as a timestamp-sized number rather than a plausible cluster multiple.
    /// </summary>
    [Fact]
    public void SizeOnDiskIsAPlausibleClusterMultiple()
    {
        File.WriteAllBytes(Path.Combine(_root, "small.bin"), new byte[100]);

        var entry = Enumerate(_root).Single();

        Assert.Equal(100, entry.LogicalSize);
        Assert.True(entry.SizeOnDisk >= entry.LogicalSize, $"size on disk was {entry.SizeOnDisk}");
        Assert.True(entry.SizeOnDisk <= 1024 * 1024, $"size on disk was implausibly large: {entry.SizeOnDisk}");
    }

    /// <summary>
    /// File IDs are the join key for the whole storage model, so a wrong offset here would break
    /// Tier 2 in a way that would be very hard to diagnose later.
    /// </summary>
    [Fact]
    public void FileIdsAreUniqueAndNonZero()
    {
        for (var i = 0; i < 8; i++)
        {
            File.WriteAllBytes(Path.Combine(_root, $"f{i}.bin"), new byte[10]);
        }

        var entries = Enumerate(_root);

        Assert.All(entries, e => Assert.NotEqual(0, e.FileId));
        Assert.Equal(entries.Count, entries.Select(e => e.FileId).Distinct().Count());
    }

    [Fact]
    public void LastWriteTimeMatchesWhatWasSet()
    {
        var path = Path.Combine(_root, "dated.bin");
        File.WriteAllBytes(path, new byte[1]);

        var expected = new DateTime(2021, 6, 15, 12, 30, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(path, expected);

        var entry = Enumerate(_root).Single();

        Assert.Equal(expected, entry.LastWriteUtc, TimeSpan.FromSeconds(2));
    }

    [Fact]
    public void LongNamesAreReadWithoutTruncation()
    {
        var longName = new string('x', 200) + ".bin";
        File.WriteAllBytes(Path.Combine(_root, longName), new byte[5]);

        var entry = Enumerate(_root).Single();

        Assert.Equal(longName, entry.Name);
    }

    /// <summary>
    /// Records are variable-length and chained by NextEntryOffset, and the buffer refills partway
    /// through a large directory. Enough entries to force several refills is what catches an
    /// off-by-one in the chain walk.
    /// </summary>
    [Fact]
    public void LargeDirectoriesSpanningManyBufferFillsReturnEverything()
    {
        const int count = 3_000;

        for (var i = 0; i < count; i++)
        {
            File.WriteAllBytes(Path.Combine(_root, $"file-{i:D5}.bin"), new byte[1]);
        }

        var entries = Enumerate(_root);

        Assert.Equal(count, entries.Count);
        Assert.Equal(count, entries.Select(e => e.Name).Distinct().Count());
    }

    [Fact]
    public void MissingDirectoryIsReportedNotThrown()
    {
        var enumerator = new Win32DirectoryEnumerator();
        var buffer = new List<DirectoryEntry>();

        var ok = enumerator.TryEnumerate(
            Path.Combine(_root, "does-not-exist"),
            buffer,
            out var failure,
            out _);

        Assert.False(ok);
        Assert.Equal(DirectoryEnumerationError.NotFound, failure);
    }

    [Fact]
    public void RootDirectoryIdIsResolvable()
    {
        var enumerator = new Win32DirectoryEnumerator();

        Assert.True(enumerator.TryGetDirectoryId(_root, out var id));
        Assert.NotEqual(0, id);
    }

    /// <summary>
    /// The IDs a directory reports for its children must be the same IDs those children report for
    /// themselves, or the parent links in the storage model would not join up.
    /// </summary>
    /// <remarks>
    /// This is also the independent cross-check on the FileId offset. The two sides come from
    /// different APIs and different structs — FILE_ID_BOTH_DIR_INFO read at a hand-written offset on
    /// one side, BY_HANDLE_FILE_INFORMATION's marshalled index pair on the other — so a wrong
    /// constant would make them disagree rather than silently agreeing on garbage.
    /// </remarks>
    [Fact]
    public void ChildDirectoryIdMatchesItsOwnResolvedId()
    {
        var childPath = Path.Combine(_root, "child");
        Directory.CreateDirectory(childPath);

        var fromParent = Enumerate(_root).Single(e => e.Name == "child").FileId;

        var enumerator = new Win32DirectoryEnumerator();
        Assert.True(enumerator.TryGetDirectoryId(childPath, out var fromItself));

        Assert.Equal(fromItself, fromParent);
    }

    [Fact]
    public void BackgroundIoScopeEntersAndLeavesCleanly()
    {
        var thread = new Thread(() =>
        {
            using var scope = new BackgroundIoScope();
            Assert.True(scope.IsActive, $"entering background mode failed with error {scope.LastError}");
        });

        thread.Start();
        thread.Join();
    }

    private static List<DirectoryEntry> Enumerate(string path)
    {
        var enumerator = new Win32DirectoryEnumerator();
        var buffer = new List<DirectoryEntry>();

        Assert.True(
            enumerator.TryEnumerate(path, buffer, out var failure, out var native),
            $"enumeration failed: {failure} (native {native})");

        return buffer;
    }
}
