namespace Strata.Core.Diffing;

/// <summary>What changed inside one directory, counting only files held directly in it.</summary>
/// <remarks>
/// Net and churn are tracked separately because they answer different questions. A build directory
/// that wrote and deleted 50 GB has a net near zero and is invisible to "what grew", but it is the
/// most interesting thing on the disk if the question is SSD wear.
/// </remarks>
public readonly record struct DirectoryDelta(
    long OwnBytesDelta,
    long OwnChurnBytes,
    int FilesAdded,
    int FilesRemoved)
{
    public int FileCountDelta => FilesAdded - FilesRemoved;

    public bool IsEmpty => OwnBytesDelta == 0 && OwnChurnBytes == 0 && FilesAdded == 0 && FilesRemoved == 0;

    public static DirectoryDelta operator +(DirectoryDelta a, DirectoryDelta b)
        => new(
            a.OwnBytesDelta + b.OwnBytesDelta,
            a.OwnChurnBytes + b.OwnChurnBytes,
            a.FilesAdded + b.FilesAdded,
            a.FilesRemoved + b.FilesRemoved);
}

/// <summary>How a change is described to a human.</summary>
public enum ChangeKind
{
    Grew = 1,
    Shrank = 2,

    /// <summary>The directory did not exist in the earlier state.</summary>
    Appeared = 3,

    /// <summary>The directory existed and no longer does.</summary>
    Vanished = 4,

    /// <summary>Content moved between directories, detected by file ID rather than inferred.</summary>
    Moved = 5,

    /// <summary>
    /// The part of a directory's change that its reported children do not account for.
    /// </summary>
    /// <remarks>
    /// Its own kind rather than a grew or a shrank, because it is a different sort of statement: not
    /// "this directory changed" but "this much changed in here and nothing in it is big enough to
    /// name". It is drawn hatched, in the same ink as the unreadable-directory band, for the same
    /// reason — an estimate is never rendered like a measurement.
    /// </remarks>
    Remainder = 6,
}

/// <summary>One attributed change, ready to become a row in the events feed.</summary>
public sealed record AttributedChange
{
    public required long DirectoryId { get; init; }

    /// <summary>
    /// The directory this change is attributed to.
    /// </summary>
    /// <remarks>
    /// On a <see cref="ChangeKind.Remainder"/> row this is the directory the remainder is left over
    /// inside, not a directory that changed by this amount — it is displayed as "elsewhere in
    /// {path}". A real path rather than a synthesised label, so the row is still something a caller
    /// can scope a further diff to.
    /// </remarks>
    public required string Path { get; init; }

    public required ChangeKind Kind { get; init; }

    /// <summary>Net change across this directory's whole subtree.</summary>
    public required long DeltaBytes { get; init; }

    /// <summary>Bytes written and removed within the subtree, regardless of net effect.</summary>
    public long ChurnBytes { get; init; }

    public int FileCountDelta { get; init; }

    public int Depth { get; init; }

    /// <summary>
    /// Where a move came from. Null unless <see cref="Kind"/> is <see cref="ChangeKind.Moved"/>.
    /// </summary>
    public string? MovedFrom { get; init; }

    public long AbsoluteDelta => Math.Abs(DeltaBytes);
}
