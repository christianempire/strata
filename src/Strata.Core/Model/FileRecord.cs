namespace Strata.Core.Model;

/// <summary>
/// A file as it is persisted: identified by its file ID and its parent's, never by a path string.
/// </summary>
/// <remarks>
/// Storing parent ID plus name rather than a full path is what keeps the current-state table small
/// (a directory prefix is stored once, not once per descendant) and makes a directory rename an
/// O(1) update instead of a subtree rewrite. Paths are reconstructed on demand by walking parents.
/// </remarks>
public readonly record struct FileRecord(
    long FileId,
    long ParentId,
    string Name,
    long LogicalSize,
    long SizeOnDisk,
    uint RawAttributes,
    DateTime LastWriteUtc,
    bool IsHardLink)
{
    /// <summary>
    /// Bytes this record contributes to directory rollups.
    /// <para>
    /// Zero for a hardlink whose bytes were already counted at the first path we encountered, so
    /// that rollups sum exactly to the volume total rather than double-counting shared extents.
    /// This matters most for C:\Windows\WinSxS, which is largely hardlinks into System32.
    /// </para>
    /// </summary>
    public long CountedBytes => IsHardLink ? 0 : LogicalSize;

    public long CountedSizeOnDisk => IsHardLink ? 0 : SizeOnDisk;
}
