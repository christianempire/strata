using Strata.Core.Diffing;
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

    /// <summary>Allocated bytes this entry contributes to rollups, on the same terms.</summary>
    public long CountedOnDisk => IsHardLink ? 0 : SizeOnDisk;

    /// <summary>What taking this entry off the totals does to its directory.</summary>
    public DirectoryDelta Removal => new(-CountedBytes, CountedBytes, 0, 1, -CountedOnDisk);
}

/// <summary>One recorded directory and what is currently totalled beneath it.</summary>
public readonly record struct StoredDirectory(
    long ParentId,
    string Name,
    long SubtreeBytes,
    long SubtreeOnDisk,
    long SubtreeFiles,
    int SubtreeDirs)
{
    /// <summary>
    /// What carrying this directory and everything beneath it into a parent does to that parent.
    /// </summary>
    /// <remarks>
    /// Negated, it is what taking it out does. Not churn: nothing was written or freed, the bytes
    /// changed address.
    /// </remarks>
    public DirectoryDelta Arrival => new(
        SubtreeBytes,
        0,
        (int)Math.Max(0, SubtreeFiles),
        (int)Math.Max(0, -SubtreeFiles),
        SubtreeOnDisk,
        SubtreeDirs + 1);

    public DirectoryDelta Departure => new(
        -SubtreeBytes,
        0,
        (int)Math.Max(0, -SubtreeFiles),
        (int)Math.Max(0, SubtreeFiles),
        -SubtreeOnDisk,
        -(SubtreeDirs + 1));
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

    /// <summary>A recorded directory, with the totals beneath it as they stood before this tick.</summary>
    bool TryGetDirectory(long directoryId, out StoredDirectory directory);

    /// <summary>
    /// Re-parents or renames a recorded directory, keeping the depth of everything beneath it true.
    /// </summary>
    /// <remarks>
    /// Changes structure only. The totals it carries are moved by the caller, as deltas, so they
    /// travel up both ancestor chains with everything else in the tick.
    /// </remarks>
    void MoveDirectory(long directoryId, long parentId, string name);
}
