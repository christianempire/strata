namespace Strata.Host.Setup;

/// <summary>
/// One resident Strata per logon session, and a way to ask the resident one to stop.
/// </summary>
/// <remarks>
/// Two instances fight. The second fails to bind <c>localhost:5177</c> and dies with a Kestrel
/// stack trace, which is an unpleasant way to learn that Strata was already running; worse, two
/// schedulers would run ticks against one SQLite file, each with its own idea of the journal
/// cursor. A named mutex makes the second one say so and exit.
/// <para>
/// The pair of kernel objects is deliberately in the <c>Local\</c> namespace, which is per logon
/// session. Fast user switching gives each session its own database under its own
/// <c>%LOCALAPPDATA%</c>, so each should get its own instance. (The API port is machine-wide and
/// stays a genuine conflict between two simultaneous sessions; that is a real limit and out of
/// scope here.)
/// </para>
/// <para>
/// The shutdown event exists so that <c>strata install</c> can replace a running copy. Asking is
/// better than killing: a tray asked to quit takes its scheduler down through the same path as
/// Exit from the menu, which stops a tick cleanly instead of at whatever statement it had reached.
/// Killing is still the fallback, because a Strata built before this existed will not be listening.
/// </para>
/// </remarks>
internal sealed class SingleInstance : IDisposable
{
    private const string MutexName = @"Local\Strata.Instance";
    private const string ShutdownEventName = @"Local\Strata.Shutdown";

    private readonly Mutex _mutex;
    private bool _disposed;

    private SingleInstance(Mutex mutex) => _mutex = mutex;

    /// <summary>
    /// Claims the session's resident slot, or returns null if another process already holds it.
    /// </summary>
    internal static SingleInstance? TryAcquire()
    {
        var mutex = new Mutex(initiallyOwned: false, MutexName);

        try
        {
            if (!mutex.WaitOne(TimeSpan.Zero))
            {
                mutex.Dispose();
                return null;
            }
        }
        catch (AbandonedMutexException)
        {
            // The previous holder was killed rather than closed. The mutex is ours either way,
            // which is exactly what was being asked for.
        }

        return new SingleInstance(mutex);
    }

    /// <summary>Whether some other process in this session is holding the resident slot.</summary>
    internal static bool IsHeld()
    {
        using var probe = TryAcquire();
        return probe is null;
    }

    /// <summary>
    /// Asks a running instance to shut down.
    /// </summary>
    /// <returns>
    /// False when nothing is listening. The named event only exists while some process holds a
    /// handle to it, so its absence is a reliable "nobody here is going to answer" rather than a
    /// guess.
    /// </returns>
    internal static bool RequestShutdown()
    {
        if (!EventWaitHandle.TryOpenExisting(ShutdownEventName, out var handle))
        {
            return false;
        }

        using (handle)
        {
            // Auto-reset: one Set releases one waiter, so a caller stopping several instances
            // signals once per instance and none of them is left holding a stale signal.
            handle.Set();
            return true;
        }
    }

    /// <summary>
    /// Runs <paramref name="quit"/> when another process asks this one to stop.
    /// </summary>
    /// <remarks>
    /// A background thread rather than a registered wait, because the callback ends the message
    /// pump and the thread that owns it has to be one we are happy to leave blocked.
    /// </remarks>
    internal static void ListenForShutdown(Action quit, CancellationToken stopping)
    {
        ArgumentNullException.ThrowIfNull(quit);

        var signal = new EventWaitHandle(false, EventResetMode.AutoReset, ShutdownEventName);

        var thread = new Thread(() =>
        {
            using (signal)
            {
                if (WaitHandle.WaitAny([signal, stopping.WaitHandle]) == 0)
                {
                    quit();
                }
            }
        })
        {
            IsBackground = true,
            Name = "strata-shutdown-listener",
        };

        thread.Start();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        try
        {
            _mutex.ReleaseMutex();
        }
        catch (ApplicationException)
        {
            // Released from a thread other than the one that took it, or never taken. Process exit
            // abandons it in either case and the next acquirer handles that.
        }

        _mutex.Dispose();
    }
}
