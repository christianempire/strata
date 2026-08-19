using Strata.Core.Model;

namespace Strata.Core.Scanning;

/// <summary>One recorded directory entry, as currently stored.</summary>
public readonly record struct StoredEntry(
    long ParentId,
    string Name,
    long FileId,
    long LogicalSize,
    long SizeOnDisk,
    bool IsHardLink)
{
    /// <summary>Bytes this entry contributes to rollups. Zero for a link counted elsewhere.</summary>
    public long CountedBytes => IsHardLink ? 0 : LogicalSize;
}

/// <summary>
/// The current recorded state of a volume, as Tier 2 needs to see it.
/// </summary>
/// <remarks>
/// Deliberately narrow, and deliberately point-lookup shaped. Every change journal record addresses
/// exactly one directory entry by (parent, name), which is the storage primary key, so the common
/// operation is a single keyed hit rather than a query.
/// <para>
/// This exists so the incremental scanner can be tested against a dictionary. Getting rename,
/// delete and hardlink handling right matters more than it is convenient to verify against SQLite.
/// </para>
/// </remarks>
public interface IFileStateStore
{
    bool TryGetEntry(long parentId, string name, out StoredEntry entry);

    /// <summary>
    /// Every entry pointing at a given file. Usually one; more when the file has hardlinks.
    /// </summary>
    /// <remarks>
    /// Needed for two things a name-keyed lookup cannot do: fanning a size change out across all of
    /// a file's links, and finding an entry whose name we do not know - which is the normal case
    /// under the unprivileged journal read, where names are redacted.
    /// </remarks>
    IReadOnlyList<StoredEntry> GetEntriesByFileId(long fileId);

    void Upsert(in StoredEntry entry);

    void Delete(long parentId, string name);

    /// <summary>True when this directory is known, so a delta against it can be attributed.</summary>
    bool DirectoryExists(long directoryId);

    /// <summary>Records a directory discovered by Tier 2 that no baseline has seen.</summary>
    void AddDirectory(long directoryId, long parentId, string name);
}
