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

    /// <summary>Hides the console window a scheduled task hands an inherently-console process.</summary>
    [LibraryImport(Library)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool ShowWindow(nint hWnd, int nCmdShow);

    internal const int SW_HIDE = 0;

    // --- the tray's hidden window ---------------------------------------------------------------
    //
    // A notification-area icon needs a window to send its mouse messages to, and a window needs a
    // class, a message loop and a procedure. None of it is ever shown.

    [LibraryImport(Library, EntryPoint = "RegisterClassExW", SetLastError = true)]
    internal static partial ushort RegisterClassEx(ref WNDCLASSEXW lpwcx);

    [LibraryImport(Library, EntryPoint = "CreateWindowExW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    internal static partial nint CreateWindowEx(
        uint dwExStyle, string lpClassName, string lpWindowName, uint dwStyle,
        int x, int y, int nWidth, int nHeight, nint hWndParent, nint hMenu, nint hInstance, nint lpParam);

    [LibraryImport(Library, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool DestroyWindow(nint hWnd);

    [LibraryImport(Library, EntryPoint = "DefWindowProcW")]
    internal static partial nint DefWindowProc(nint hWnd, uint msg, nint wParam, nint lParam);

    [LibraryImport(Library, EntryPoint = "GetMessageW", SetLastError = true)]
    internal static partial int GetMessage(out MSG lpMsg, nint hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [LibraryImport(Library, EntryPoint = "TranslateMessage")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool TranslateMessage(ref MSG lpMsg);

    [LibraryImport(Library, EntryPoint = "DispatchMessageW")]
    internal static partial nint DispatchMessage(ref MSG lpMsg);

    [LibraryImport(Library)]
    internal static partial void PostQuitMessage(int nExitCode);

    [LibraryImport(Library, EntryPoint = "PostMessageW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool PostMessage(nint hWnd, uint msg, nint wParam, nint lParam);

    [LibraryImport(Library, EntryPoint = "RegisterWindowMessageW", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial uint RegisterWindowMessage(string lpString);

    // --- the menu -------------------------------------------------------------------------------

    [LibraryImport(Library, SetLastError = true)]
    internal static partial nint CreatePopupMenu();

    [LibraryImport(Library, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool DestroyMenu(nint hMenu);

    [LibraryImport(Library, EntryPoint = "AppendMenuW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool AppendMenu(nint hMenu, uint uFlags, nuint uIDNewItem, string? lpNewItem);

    [LibraryImport(Library, SetLastError = true)]
    internal static partial int TrackPopupMenuEx(
        nint hMenu, uint uFlags, int x, int y, nint hwnd, nint lptpm);

    [LibraryImport(Library, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetCursorPos(out POINT lpPoint);

    /// <summary>
    /// Brings the (hidden) window to the foreground before showing the menu.
    /// </summary>
    /// <remarks>
    /// Documented requirement rather than a nicety: without it, a popup menu tracked from a tray
    /// icon does not dismiss when the user clicks elsewhere, and sits over the desktop until it is
    /// clicked through.
    /// </remarks>
    [LibraryImport(Library, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetForegroundWindow(nint hWnd);

    // --- the icon -------------------------------------------------------------------------------

    [LibraryImport(Library, EntryPoint = "LoadImageW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    internal static partial nint LoadImage(
        nint hInst, string name, uint type, int cx, int cy, uint fuLoad);

    [LibraryImport(Library, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool DestroyIcon(nint hIcon);

    [LibraryImport(Library, SetLastError = true)]
    internal static partial int GetSystemMetrics(int nIndex);

    internal const uint WM_DESTROY = 0x0002;
    internal const uint WM_CLOSE = 0x0010;
    internal const uint WM_COMMAND = 0x0111;
    internal const uint WM_LBUTTONUP = 0x0202;
    internal const uint WM_RBUTTONUP = 0x0205;
    internal const uint WM_CONTEXTMENU = 0x007B;
    internal const uint WM_USER = 0x0400;

    internal const uint MF_STRING = 0x00000000;
    internal const uint MF_SEPARATOR = 0x00000800;
    internal const uint MF_CHECKED = 0x00000008;
    internal const uint MF_DISABLED = 0x00000002;
    internal const uint MF_GRAYED = 0x00000001;

    internal const uint TPM_RIGHTBUTTON = 0x0002;
    internal const uint TPM_RETURNCMD = 0x0100;
    internal const uint TPM_BOTTOMALIGN = 0x0020;

    internal const uint IMAGE_ICON = 1;
    internal const uint LR_LOADFROMFILE = 0x00000010;
    internal const uint LR_DEFAULTSIZE = 0x00000040;
    internal const uint LR_SHARED = 0x00008000;

    internal const int SM_CXSMICON = 49;
    internal const int SM_CYSMICON = 50;
}

/// <summary>
/// A window class. Windows names kept verbatim.
/// </summary>
/// <remarks>
/// Deliberately blittable: the source-generated marshaller will not handle a struct with string
/// fields, so the two name members are raw pointers and the caller pins the strings. That is the
/// same trade the scan hot path makes for its native buffers, for the same reason — the generator
/// only helps with layouts it can prove.
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
internal struct WNDCLASSEXW
{
    internal uint cbSize;
    internal uint style;
    internal nint lpfnWndProc;
    internal int cbClsExtra;
    internal int cbWndExtra;
    internal nint hInstance;
    internal nint hIcon;
    internal nint hCursor;
    internal nint hbrBackground;
    internal nint lpszMenuName;
    internal nint lpszClassName;
    internal nint hIconSm;
}

[StructLayout(LayoutKind.Sequential)]
internal struct MSG
{
    internal nint hwnd;
    internal uint message;
    internal nint wParam;
    internal nint lParam;
    internal uint time;
    internal POINT pt;
    internal uint lPrivate;
}

[StructLayout(LayoutKind.Sequential)]
internal struct POINT
{
    internal int x;
    internal int y;
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
