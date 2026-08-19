using Strata.Core.Model;
using Strata.Core.Rollup;

namespace Strata.Core.Scanning;

/// <summary>Everything a completed scan has to say about itself.</summary>
public sealed class ScanResult
{
    public required VolumeIdentity Volume { get; init; }

    public required ScanTier Tier { get; init; }

    public required DateTime StartedUtc { get; init; }

    public required TimeSpan Duration { get; init; }

    public required RollupResult Rollup { get; init; }

    public long FileCount { get; init; }

    public int DirectoryCount { get; init; }

    /// <summary>Total logical bytes, after hardlink deduplication.</summary>
    public long TotalBytes { get; init; }

    public long TotalSizeOnDisk { get; init; }

    /// <summary>Files that were additional links to bytes already counted at another path.</summary>
    public long HardLinkCount { get; init; }

    /// <summary>Bytes that would have been double-counted had hardlinks not been deduplicated.</summary>
    public long HardLinkBytesSaved { get; init; }

    /// <summary>Directories recorded as links and deliberately not descended into.</summary>
    public int ReparsePointCount { get; init; }

    /// <summary>
    /// Directories that could not be read, by reason. Unelevated this will be non-empty on C:, and
    /// reporting it is how the user sees that Strata's total is under Explorer's rather than
    /// wondering why the numbers disagree.
    /// </summary>
    public IReadOnlyDictionary<DirectoryEnumerationError, int> Inaccessible { get; init; }
        = new Dictionary<DirectoryEnumerationError, int>();

    public int InaccessibleTotal
    {
        get
        {
            var total = 0;
            foreach (var count in Inaccessible.Values)
            {
                total += count;
            }

            return total;
        }
    }

    public double FilesPerSecond => Duration.TotalSeconds > 0 ? FileCount / Duration.TotalSeconds : 0;
}
