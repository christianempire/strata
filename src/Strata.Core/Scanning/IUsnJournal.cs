using Strata.Core.Model;

namespace Strata.Core.Scanning;

/// <summary>Where a volume's change journal currently sits.</summary>
public readonly record struct UsnJournalState(ulong JournalId, long FirstUsn, long NextUsn)
{
    /// <summary>
    /// Whether a stored cursor can still be resumed from.
    /// </summary>
    /// <remarks>
    /// Two ways it cannot: the journal was deleted and recreated, giving it a new ID, or the journal
    /// wrapped and trimmed away records we had not read yet. Both mean the incremental history has a
    /// hole in it, and a hole is worse than a delay - the only honest recovery is a full baseline.
    /// </remarks>
    public bool CanResumeFrom(UsnCursor cursor)
        => cursor.JournalId == JournalId && cursor.NextUsn >= FirstUsn && cursor.NextUsn <= NextUsn;
}

/// <summary>A persisted position in a volume's change journal.</summary>
public readonly record struct UsnCursor(ulong JournalId, long NextUsn)
{
    public static readonly UsnCursor None = new(0, 0);

    public bool IsSet => JournalId != 0;
}

/// <summary>One batch of journal records plus the position to resume from.</summary>
public sealed class UsnReadResult
{
    public required UsnReadStatus Status { get; init; }

    public IReadOnlyList<UsnChange> Changes { get; init; } = [];

    /// <summary>Where the next read should start. Only meaningful when <see cref="Status"/> is Ok.</summary>
    public long NextUsn { get; init; }

    /// <summary>
    /// The journal this was read from.
    /// </summary>
    /// <remarks>
    /// Carried out rather than assumed from the request, because the very first read has no cursor
    /// and therefore no journal ID to echo back. Persisting a zero here produces a cursor that never
    /// registers as set, so every tick silently restarts from the head and no change is ever seen.
    /// </remarks>
    public ulong JournalId { get; init; }

    /// <summary>True when the journal held more records than this read was allowed to return.</summary>
    public bool MoreAvailable { get; init; }

    public int NativeErrorCode { get; init; }
}

/// <summary>
/// Reads a volume's NTFS change journal.
/// </summary>
/// <remarks>
/// The seam that keeps the incremental scanner testable. Everything interesting about Tier 2 -
/// coalescing records, resolving paths, handling renames as moves, deciding when the journal has
/// gone stale - can be exercised against a scripted list of changes with no filesystem involved.
/// </remarks>
public interface IUsnJournal
{
    /// <summary>Reads the journal's current bounds, or null if it cannot be queried.</summary>
    UsnJournalState? TryGetState();

    /// <summary>
    /// Reads changes from <paramref name="cursor"/> onward.
    /// </summary>
    /// <param name="maxRecords">
    /// Ceiling on records returned in one call. The journal is a durable queue, so stopping early
    /// and resuming next tick is safe; blocking until a 500k-record backlog drains is what would
    /// cause the stall the whole design exists to avoid.
    /// </param>
    UsnReadResult Read(UsnCursor cursor, int maxRecords);
}

/// <summary>Reads the current size of files the journal says have changed.</summary>
/// <remarks>
/// Separate from <see cref="IUsnJournal"/> because it is a different mechanism answering a different
/// question. The journal says *which* files moved; this says *how big they are now*. Splitting them
/// keeps each testable on its own.
/// </remarks>
public interface IFileMetadataReader
{
    /// <summary>
    /// Reads current metadata for a file, by ID, relative to a volume.
    /// </summary>
    /// <returns>False if the file no longer exists or cannot be opened, which is routine.</returns>
    bool TryReadById(long fileId, out FileMetadata metadata);
}

/// <summary>Current metadata for one file.</summary>
public readonly record struct FileMetadata(
    long LogicalSize,
    long SizeOnDisk,
    uint RawAttributes,
    DateTime LastWriteUtc,
    int LinkCount)
{
    /// <summary>
    /// True when several directory entries point at this file, so a size change has to be applied
    /// to more than one row and only one of them may count toward rollups.
    /// </summary>
    public bool HasMultipleLinks => LinkCount > 1;
}
