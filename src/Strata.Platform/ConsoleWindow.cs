using Strata.Platform.Native;

namespace Strata.Platform;

/// <summary>
/// Gets rid of the console window that Windows gives a console process, when nobody asked for one.
/// </summary>
/// <remarks>
/// <c>strata.exe</c> is a console subsystem executable because most of its verbs are console verbs —
/// the CLI exists to produce measurements, and a Windows-subsystem build would hand the shell its
/// prompt back before the scan had printed a line. The subsystem is a property of the file, not of
/// the verb, so <c>strata tray</c> inherits it and Windows allocates a console for it.
/// <para>
/// Started from a shell that is nothing anyone notices: the output goes to the console already on
/// screen. Started by the logon task there is no shell, so Windows creates a console *window* for
/// it, which is exactly the thing <c>docs/RUNTIME.md</c> promises will not appear. This detaches
/// from it in that case and only in that case.
/// </para>
/// <para>
/// The test for "nobody asked for one" is the console's process list. A console created for this
/// process alone has one process attached — us. A console we were handed by <c>cmd</c> or
/// PowerShell has at least two, and detaching from that would silently swallow the output the
/// person at the keyboard is waiting for.
/// </para>
/// </remarks>
public static class ConsoleWindow
{
    /// <summary>
    /// Detaches from a console that exists only because this process was started without one.
    /// </summary>
    /// <returns>
    /// True if a console was hidden and released. False when there is no console, or when it belongs
    /// to a shell that is sharing it with us.
    /// </returns>
    public static bool DetachIfUnwanted()
    {
        var window = Kernel32.GetConsoleWindow();

        if (window == 0)
        {
            return false;
        }

        if (AttachedProcessCount() != 1)
        {
            return false;
        }

        // Hidden before it is freed, and freed before anything writes to it. The window exists from
        // process creation, so this is a race against the runtime's own startup rather than
        // something that can be prevented outright; measured on the target machine the window is
        // never painted.
        User32.ShowWindow(window, User32.SW_HIDE);

        // Console.Out is created lazily and would hold a handle that FreeConsole invalidates. Point
        // it at nothing first, so the tray's startup lines go nowhere instead of throwing.
        Console.SetOut(TextWriter.Null);
        Console.SetError(TextWriter.Null);

        Kernel32.FreeConsole();

        return true;
    }

    private static uint AttachedProcessCount()
    {
        // The documented way to ask for the count is to pass a buffer too small and read the return
        // value, which is the number required. A one-element buffer is enough to learn "more than
        // one", which is the only distinction that matters here.
        var buffer = new uint[1];

        return Kernel32.GetConsoleProcessList(buffer, 1);
    }
}
