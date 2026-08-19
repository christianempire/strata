namespace Strata.Core.Model;

/// <summary>
/// One entry as yielded by a directory enumeration.
/// </summary>
/// <remarks>
/// This mirrors what a single native directory read returns per entry, and is deliberately a struct:
/// a full baseline scan produces millions of these, and boxing one per file would dominate the
/// scan's allocation budget. The only allocation per entry is <paramref name="Name"/>.
/// </remarks>
public readonly record struct DirectoryEntry(
    long FileId,
    string Name,
    long LogicalSize,
    long SizeOnDisk,
    EntryTraits Flags,
    uint RawAttributes,
    DateTime LastWriteUtc)
{
    public bool IsDirectory => (Flags & EntryTraits.Directory) != 0;

    public bool IsReparsePoint => (Flags & EntryTraits.ReparsePoint) != 0;

    /// <summary>
    /// True when this entry should be descended into: a real directory, not a link to one somewhere
    /// else on the volume.
    /// </summary>
    public bool IsTraversableDirectory => IsDirectory && !IsReparsePoint;
}
