using Strata.Core.Model;
using Strata.Core.Scanning;

namespace Strata.Tests.Fakes;

/// <summary>A scripted change journal.</summary>
internal sealed class FakeUsnJournal : IUsnJournal
{
    private readonly List<UsnChange> _changes = [];

    internal UsnReadStatus Status { get; set; } = UsnReadStatus.Ok;

    internal ulong JournalId { get; set; } = 42;

    internal long NextUsn { get; set; } = 1000;

    internal int LastMaxRecords { get; private set; }

    internal FakeUsnJournal Add(UsnChange change)
    {
        _changes.Add(change);
        return this;
    }

    public UsnJournalState? TryGetState() => new(JournalId, 0, NextUsn);

    public UsnReadResult Read(UsnCursor cursor, int maxRecords)
    {
        LastMaxRecords = maxRecords;

        if (Status != UsnReadStatus.Ok)
        {
            return new UsnReadResult { Status = Status };
        }

        var taken = _changes.Count <= maxRecords ? _changes : _changes.GetRange(0, maxRecords);

        return new UsnReadResult
        {
            Status = UsnReadStatus.Ok,
            Changes = taken,
            NextUsn = NextUsn,
            JournalId = JournalId,
            MoreAvailable = _changes.Count > maxRecords,
        };
    }
}

/// <summary>An in-memory filesystem answering "how big is this now".</summary>
internal sealed class FakeMetadataReader : IFileMetadataReader, IFilePathResolver
{
    private readonly Dictionary<long, FileMetadata> _files = [];
    private readonly Dictionary<long, string> _paths = [];

    internal int ReadCount { get; private set; }

    internal FakeMetadataReader Set(
        long fileId, long size, int linkCount = 1, string? path = null, long? onDisk = null)
    {
        _files[fileId] = new FileMetadata(size, onDisk ?? size, 0x80, DateTime.UnixEpoch, linkCount);

        if (path is not null)
        {
            _paths[fileId] = path;
        }

        return this;
    }

    /// <summary>Removes a file, as a deletion between the journal record and our read would.</summary>
    internal FakeMetadataReader Remove(long fileId)
    {
        _files.Remove(fileId);
        _paths.Remove(fileId);
        return this;
    }

    public bool TryReadById(long fileId, out FileMetadata metadata)
    {
        ReadCount++;
        return _files.TryGetValue(fileId, out metadata);
    }

    public bool TryGetPath(long fileId, out string volumeRelativePath)
        => _paths.TryGetValue(fileId, out volumeRelativePath!) || Fail(out volumeRelativePath);

    private static bool Fail(out string value)
    {
        value = "";
        return false;
    }
}

/// <summary>An in-memory implementation of the recorded volume state.</summary>
internal sealed class FakeFileStateStore : IFileStateStore
{
    private readonly Dictionary<(long Parent, string Name), StoredEntry> _entries = [];
    private readonly Dictionary<long, StoredDirectory> _directories = [];

    internal IReadOnlyCollection<StoredEntry> Entries => _entries.Values;

    internal IReadOnlyDictionary<long, StoredDirectory> Directories => _directories;

    internal FakeFileStateStore WithDirectory(long id)
        => WithDirectory(id, new StoredDirectory(5, $"dir{id}", 0, 0, 0, 0));

    internal FakeFileStateStore WithDirectory(long id, StoredDirectory directory)
    {
        _directories[id] = directory;
        return this;
    }

    internal FakeFileStateStore WithEntry(StoredEntry entry)
    {
        _entries[(entry.ParentId, entry.Name)] = entry;
        return this;
    }

    public bool TryGetEntry(long parentId, string name, out StoredEntry entry)
        => _entries.TryGetValue((parentId, name), out entry);

    public IReadOnlyList<StoredEntry> GetEntriesByFileId(long fileId)
        => _entries.Values.Where(e => e.FileId == fileId).ToList();

    public void Upsert(in StoredEntry entry) => _entries[(entry.ParentId, entry.Name)] = entry;

    public void Delete(long parentId, string name) => _entries.Remove((parentId, name));

    public bool DirectoryExists(long directoryId) => _directories.ContainsKey(directoryId);

    public void AddDirectory(long directoryId, long parentId, string name)
        => _directories.TryAdd(directoryId, new StoredDirectory(parentId, name, 0, 0, 0, 0));

    public bool TryGetDirectory(long directoryId, out StoredDirectory directory)
        => _directories.TryGetValue(directoryId, out directory);

    public void MoveDirectory(long directoryId, long parentId, string name)
        => _directories[directoryId] = _directories[directoryId] with { ParentId = parentId, Name = name };
}
