namespace Strata.Core.Model;

/// <summary>
/// Platform-neutral view of a filesystem entry's attributes.
/// <para>
/// Strata.Core deliberately does not know about Windows' FILE_ATTRIBUTE_* constants; Strata.Platform
/// maps them onto these. The raw attribute word is carried separately in <see cref="DirectoryEntry"/>
/// so storage can persist it verbatim without Core having to interpret it.
/// </para>
/// </summary>
[Flags]
public enum EntryTraits
{
    None = 0,

    /// <summary>The entry is a directory rather than a file.</summary>
    Directory = 1 << 0,

    /// <summary>
    /// A junction, symlink, or other reparse point. Directories carrying this are recorded but never
    /// traversed: following them risks both infinite recursion and double-counting bytes that were
    /// already attributed elsewhere on the volume.
    /// </summary>
    ReparsePoint = 1 << 1,

    Hidden = 1 << 2,
    System = 1 << 3,

    /// <summary>NTFS-compressed. Size on disk will be materially below logical size.</summary>
    Compressed = 1 << 4,

    /// <summary>Sparse file. Size on disk may be a small fraction of logical size.</summary>
    Sparse = 1 << 5,

    /// <summary>
    /// Offline / recall-on-open. Cloud placeholders (OneDrive and similar) look like large files
    /// that occupy almost no disk, which is exactly the discrepancy Strata should surface rather
    /// than hide.
    /// </summary>
    Offline = 1 << 6,
}
