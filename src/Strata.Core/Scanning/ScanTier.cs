namespace Strata.Core.Scanning;

/// <summary>Which strategy produced a scan. Persisted with every snapshot so history is auditable.</summary>
public enum ScanTier
{
    /// <summary>Full baseline enumeration of the whole volume. Rare and comparatively expensive.</summary>
    Full = 1,

    /// <summary>Incremental delta read from the NTFS change journal. The common case. Phase 2.</summary>
    Incremental = 2,

    /// <summary>
    /// Throttled full walk for volumes with no usable change journal. Marked reduced fidelity in
    /// the UI, because its change attribution is coarser and its history lower-resolution. Phase 3.
    /// </summary>
    Fallback = 3,
}
