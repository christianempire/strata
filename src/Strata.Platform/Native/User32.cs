using System.Runtime.InteropServices;

namespace Strata.Platform.Native;

internal static partial class User32
{
    private const string Library = "user32.dll";

    [LibraryImport(Library)]
    internal static partial nint GetForegroundWindow();

    [LibraryImport(Library, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetWindowRect(nint hWnd, out RECT lpRect);

    [LibraryImport(Library, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetLastInputInfo(ref LASTINPUTINFO plii);

    [LibraryImport(Library)]
    internal static partial nint MonitorFromWindow(nint hwnd, uint dwFlags);

    [LibraryImport(Library, EntryPoint = "GetMonitorInfoW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetMonitorInfo(nint hMonitor, ref MONITORINFO lpmi);

    [LibraryImport(Library)]
    internal static partial nint GetShellWindow();

    [LibraryImport(Library)]
    internal static partial uint GetWindowThreadProcessId(nint hWnd, out uint lpdwProcessId);

    internal const uint MONITOR_DEFAULTTONEAREST = 2;
}

internal static partial class Shell32
{
    private const string Library = "shell32.dll";

    /// <summary>
    /// Reports whether the machine is in a state where notifications would be unwelcome, which is
    /// the closest thing Windows offers to "is the user gaming".
    /// </summary>
    [LibraryImport(Library)]
    internal static partial int SHQueryUserNotificationState(out int pquns);

    // QUERY_USER_NOTIFICATION_STATE
    internal const int QUNS_NOT_PRESENT = 1;
    internal const int QUNS_BUSY = 2;
    internal const int QUNS_RUNNING_D3D_FULL_SCREEN = 3;
    internal const int QUNS_PRESENTATION_MODE = 4;
    internal const int QUNS_ACCEPTS_NOTIFICATIONS = 5;
    internal const int QUNS_QUIET_TIME = 6;
    internal const int QUNS_APP = 7;
}

[StructLayout(LayoutKind.Sequential)]
internal struct RECT
{
    internal int Left;
    internal int Top;
    internal int Right;
    internal int Bottom;

    internal readonly int Width => Right - Left;

    internal readonly int Height => Bottom - Top;
}

[StructLayout(LayoutKind.Sequential)]
internal struct MONITORINFO
{
    internal uint cbSize;
    internal RECT rcMonitor;
    internal RECT rcWork;
    internal uint dwFlags;
}

[StructLayout(LayoutKind.Sequential)]
internal struct LASTINPUTINFO
{
    internal uint cbSize;
    internal uint dwTime;
}
