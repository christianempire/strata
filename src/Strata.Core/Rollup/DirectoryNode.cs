namespace Strata.Core.Rollup;

/// <summary>
/// A directory during and after a scan.
/// </summary>
/// <remarks>
/// Directories are the only thing a Tier 1 scan holds in memory for the whole run. Files stream
/// straight through to storage, but directories have to be retained because their subtree totals
/// cannot be computed until every descendant has been seen. That trade works because directories
/// are a small fraction of entries on a real volume.
/// <para>
/// A mutable class rather than a record struct on purpose: these live in a dictionary and are
/// accumulated into during the rollup, and copying a struct out of the dictionary to mutate it is
/// exactly the bug this avoids.
/// </para>
/// </remarks>
public sealed class DirectoryNode
{
    public DirectoryNode(long id, long parentId, string name)
    {
        Id = id;
        ParentId = parentId;
        Name = name;
    }

    public long Id { get; }

    /// <summary>Parent's file ID. Equal to <see cref="Id"/> for the volume root, which is its own parent.</summary>
    public long ParentId { get; }

    public string Name { get; }

    /// <summary>Bytes of files held directly in this directory, excluding subdirectories.</summary>
    public long OwnBytes { get; set; }

    public long OwnSizeOnDisk { get; set; }

    public int OwnFileCount { get; set; }

    /// <summary>True if this directory is a reparse point, recorded but never traversed.</summary>
    public bool IsReparsePoint { get; init; }

    /// <summary>Bytes of this directory and everything beneath it. Valid only after a rollup.</summary>
    public long SubtreeBytes { get; set; }

    public long SubtreeSizeOnDisk { get; set; }

    public long SubtreeFileCount { get; set; }

    /// <summary>Directories beneath this one, excluding itself. Valid only after a rollup.</summary>
    public int SubtreeDirCount { get; set; }

    /// <summary>Distance from the volume root, which is depth 0. Valid only after a rollup.</summary>
    public int Depth { get; set; } = -1;
}
