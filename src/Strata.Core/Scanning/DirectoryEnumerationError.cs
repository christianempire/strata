namespace Strata.Core.Scanning;

/// <summary>Why a directory could not be read.</summary>
/// <remarks>
/// These are expected outcomes on a live filesystem, not exceptional ones. A full scan of C: without
/// elevation will hit access denials in the hundreds, and a directory being deleted between
/// discovery and enumeration is normal. Throwing per occurrence would be both slow and wrong, so
/// they are counted and reported instead.
/// </remarks>
public enum DirectoryEnumerationError
{
    None = 0,

    /// <summary>ACLs refused us. Expected unelevated for system paths and other users' profiles.</summary>
    AccessDenied,

    /// <summary>Deleted between being discovered and being opened.</summary>
    NotFound,

    /// <summary>Path exceeded what the filesystem or API would accept even with the \?\ prefix.</summary>
    PathTooLong,

    /// <summary>Anything else. The native error code is carried alongside for diagnosis.</summary>
    Other,
}
