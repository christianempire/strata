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
}

/// <summary>One attributed change, ready to become a row in the events feed.</summary>
public sealed record AttributedChange
{
    public required long DirectoryId { get; init; }

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
