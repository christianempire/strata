using Strata.Core.Model;

namespace Strata.Core.Scanning;

/// <summary>
/// A strategy for bringing a volume's recorded state up to date.
/// </summary>
/// <remarks>
/// Three implementations are planned and the choice is made per volume at runtime: a full baseline
/// walk, an incremental read of the NTFS change journal, and a throttled fallback walk for volumes
/// with no usable journal. Everything downstream consumes the same records, which is also what would
/// let a raw $MFT parser be added later as a fourth without disturbing anything.
/// </remarks>
public interface IVolumeScanner
{
    ScanTier Tier { get; }

    /// <summary>Whether this strategy can handle the given volume on this machine right now.</summary>
    bool CanScan(VolumeIdentity volume);

    ScanResult Scan(VolumeIdentity volume, IScanSink sink, CancellationToken cancellationToken);
}
