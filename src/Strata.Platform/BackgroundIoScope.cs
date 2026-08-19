using System.Runtime.InteropServices;
using Strata.Platform.Native;

namespace Strata.Platform;

/// <summary>
/// Puts the calling thread into background mode, dropping its I/O priority to Very Low, and takes it
/// back out on dispose.
/// </summary>
/// <remarks>
/// This is the single most important resource guard in the product. A background scan that competes
/// with a game for disk I/O will cause a hitch, and a hitch means the tool gets uninstalled.
/// <para>
/// Per-thread rather than per-process on purpose. <c>PROCESS_MODE_BACKGROUND_BEGIN</c> would drop
/// the whole process, and from Phase 3 this process also serves the API and the UI, so a scan would
/// throttle the user's own interactions with the thing that is doing the scanning. See
/// docs/DESIGN-REVIEW.md section A3.
/// </para>
/// <para>
/// Only valid on a thread whose lifetime we own. Applying this to a thread-pool thread and handing
/// it back to the pool would leave unrelated work running at very low I/O priority, which is why the
/// scanner uses dedicated threads.
/// </para>
/// </remarks>
public sealed class BackgroundIoScope : IDisposable
{
    private readonly bool _entered;
    private bool _disposed;

    public BackgroundIoScope()
    {
        // The pseudo-handle from GetCurrentThread always refers to the calling thread, so it needs
        // neither closing nor storing.
        _entered = Kernel32.SetThreadPriority(
            Kernel32.GetCurrentThread(),
            Kernel32.THREAD_MODE_BACKGROUND_BEGIN);

        if (!_entered)
        {
            LastError = Marshal.GetLastPInvokeError();
        }
    }

    /// <summary>Whether the thread is genuinely in background mode, rather than having been refused.</summary>
    public bool IsActive => _entered;

    /// <summary>Native error when entry was refused. Zero when it succeeded.</summary>
    public int LastError { get; }

    public void Dispose()
    {
        if (_disposed || !_entered)
        {
            _disposed = true;
            return;
        }

        _disposed = true;

        // Leaving a thread in background mode would be a leak with teeth, but there is nothing
        // useful to do if the exit fails, and throwing from Dispose would mask the real error.
        Kernel32.SetThreadPriority(Kernel32.GetCurrentThread(), Kernel32.THREAD_MODE_BACKGROUND_END);
    }
}
