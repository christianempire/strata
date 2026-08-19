using Strata.Core.Model;

namespace Strata.Core.Scanning;

/// <summary>
/// The single seam between the scan orchestration in Core and the Win32 calls in Platform.
/// </summary>
/// <remarks>
/// Keeping this narrow is what lets the parallel scan, the reparse-point rules, the hardlink
/// deduplication and the rollup all be unit-tested against an in-memory tree with no filesystem
/// involved. Implementations must be safe to call concurrently from multiple threads.
/// </remarks>
public interface IDirectoryEnumerator
{
    /// <summary>
    /// Reads every entry in one directory into <paramref name="buffer"/>.
    /// </summary>
    /// <param name="fullPath">Directory to read, already \?\-prefixed by the caller.</param>
    /// <param name="buffer">
    /// Cleared and filled by the implementation. Callers reuse one buffer per worker thread so a
    /// scan does not allocate a collection per directory.
    /// </param>
    /// <param name="failure">Why the read failed, when it returns false.</param>
    /// <param name="nativeErrorCode">Underlying OS error, for logging.</param>
    /// <returns>False if the directory could not be read at all; the scan then counts it and moves on.</returns>
    bool TryEnumerate(
        string fullPath,
        List<DirectoryEntry> buffer,
        out DirectoryEnumerationError failure,
        out int nativeErrorCode);

    /// <summary>
    /// Puts the calling thread into low-I/O-priority background mode for the lifetime of the
    /// returned scope.
    /// </summary>
    /// <remarks>
    /// Per-thread rather than per-process on purpose: from Phase 3 the same process serves the API
    /// and the UI, and a process-wide background mode would throttle the user's own interactions
    /// behind the scan they are watching. See docs/DESIGN-REVIEW.md section A3.
    /// </remarks>
    IDisposable EnterBackgroundIoScope();

    /// <summary>
    /// Resolves a directory's file ID.
    /// </summary>
    /// <remarks>
    /// Needed only for the volume root, which has no parent to have learned its ID from. Every other
    /// directory's ID arrives as part of its parent's enumeration. The real ID matters rather than a
    /// synthetic one because Tier 2 will correlate change-journal records against it by file
    /// reference number.
    /// </remarks>
    bool TryGetDirectoryId(string fullPath, out long fileId);
}
