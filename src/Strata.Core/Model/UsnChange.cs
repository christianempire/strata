namespace Strata.Core.Model;

/// <summary>What happened to a directory entry, reduced from the journal's reason flags.</summary>
/// <remarks>
/// The journal reports many more reasons than these, but most cannot move a byte. Reducing to this
/// set at the boundary keeps the incremental scanner reasoning about disk usage rather than about
/// NTFS trivia.
/// </remarks>
[Flags]
public enum UsnChangeKind
{
    None = 0,

    /// <summary>A new directory entry appeared.</summary>
    Created = 1 << 0,

    /// <summary>A directory entry was removed. Its size cannot be read back; use the recorded one.</summary>
    Deleted = 1 << 1,

    /// <summary>File data was written, extended or truncated. The size needs re-reading.</summary>
    DataChanged = 1 << 2,

    /// <summary>The entry disappeared from its old location as part of a rename or move.</summary>
    RenamedFrom = 1 << 3,

    /// <summary>The entry appeared at its new location as part of a rename or move.</summary>
    RenamedTo = 1 << 4,

    /// <summary>A hardlink to this file was added or removed elsewhere.</summary>
    HardLinkChanged = 1 << 5,
}

/// <summary>
/// One change to one directory entry, as read from the NTFS change journal.
/// </summary>
/// <remarks>
/// Deliberately carries no size. The journal does not record sizes, which is exactly why the
/// incremental scan is cheap: it learns *which* files to look at, and only those are read. That is
/// the expensive question a full walk spends all its time rediscovering.
/// </remarks>
public readonly record struct UsnChange(
    long Usn,
    long FileId,
    long ParentId,
    string Name,
    UsnChangeKind Kind,
    uint RawAttributes,
    DateTime TimestampUtc)
{
    private const uint FILE_ATTRIBUTE_DIRECTORY = 0x00000010;

    public bool IsDirectory => (RawAttributes & FILE_ATTRIBUTE_DIRECTORY) != 0;
}

/// <summary>Why an attempt to read the journal could not proceed.</summary>
public enum UsnReadStatus
{
    Ok = 0,

    /// <summary>No journal on this volume. Creating one needs elevation.</summary>
    JournalNotActive,

    /// <summary>
    /// The journal was deleted and recreated, or our cursor has been trimmed away. Either way the
    /// stored position is meaningless and only a full baseline can recover.
    /// </summary>
    JournalInvalidated,

    /// <summary>The handle we hold is not permitted to read. Distinct from the journal being absent.</summary>
    AccessDenied,

    Failed,
}
