using Strata.Core.Model;

namespace Strata.Core.Scanning;

/// <summary>
/// Receives file records as a scan discovers them.
/// </summary>
/// <remarks>
/// Files are streamed out rather than accumulated so peak memory stays bounded by the directory
/// count, not the file count. Implementations must be safe to call from multiple scan threads.
/// </remarks>
public interface IScanSink
{
    /// <summary>Accepts a batch of files. The array is pooled and reused; do not retain it.</summary>
    void Write(ReadOnlySpan<FileRecord> batch);
}

/// <summary>Discards everything. Useful for measuring raw enumeration throughput without storage cost.</summary>
public sealed class NullScanSink : IScanSink
{
    public static readonly NullScanSink Instance = new();

    public void Write(ReadOnlySpan<FileRecord> batch)
    {
        // Intentionally empty.
    }
}
