using System.Runtime.InteropServices;

namespace Strata.Platform.Native;

/// <summary>
/// Notification-area declarations.
/// </summary>
/// <remarks>
/// <c>SHQueryUserNotificationState</c> lives in <c>User32.cs</c> for historical reasons and stays
/// there rather than moving under a commit about a tray icon; the convention is one file per header
/// area and this is the file that area should be in.
/// </remarks>
internal static partial class Shell32Tray
{
    private const string Library = "shell32.dll";

    /// <summary>
    /// Adds, changes or removes a notification-area icon.
    /// </summary>
    /// <remarks>
    /// Fails rather than throws, and the failure that matters is transient: Explorer restarting
    /// takes the whole notification area with it, and every call in the window before it comes back
    /// returns false. The caller re-adds on TaskbarCreated rather than treating that as fatal.
    /// </remarks>
    [LibraryImport(Library, EntryPoint = "Shell_NotifyIconW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool Shell_NotifyIcon(uint dwMessage, ref NOTIFYICONDATAW lpData);

    internal const uint NIM_ADD = 0x00000000;
    internal const uint NIM_MODIFY = 0x00000001;
    internal const uint NIM_DELETE = 0x00000002;
    internal const uint NIM_SETVERSION = 0x00000004;

    internal const uint NIF_MESSAGE = 0x00000001;
    internal const uint NIF_ICON = 0x00000002;
    internal const uint NIF_TIP = 0x00000004;
    internal const uint NIF_INFO = 0x00000010;

    /// <summary>Version 4 gives the richer mouse messages and a tooltip past 64 characters.</summary>
    internal const uint NOTIFYICON_VERSION_4 = 4;
}

/// <summary>
/// The notification-area icon descriptor.
/// </summary>
/// <remarks>
/// Windows names, casing and field order kept verbatim so the layout can be checked against MSDN.
/// The character members are inline fixed buffers rather than strings: the struct has to be
/// blittable for the source-generated marshaller, and they are inline in the native layout anyway
/// — a <c>string</c> here would be a pointer and would put the struct's size wrong.
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NOTIFYICONDATAW
{
    internal uint cbSize;
    internal nint hWnd;
    internal uint uID;
    internal uint uFlags;
    internal uint uCallbackMessage;
    internal nint hIcon;

    internal fixed char szTip[128];

    internal uint dwState;
    internal uint dwStateMask;

    internal fixed char szInfo[256];

    /// <summary>A union with uTimeout, which is deprecated. Only the version is ever set.</summary>
    internal uint uVersion;

    internal fixed char szInfoTitle[64];

    internal uint dwInfoFlags;
    internal Guid guidItem;
    internal nint hBalloonIcon;

    /// <summary>
    /// Copies a tooltip into the inline buffer, truncated rather than overrun.
    /// </summary>
    /// <remarks>
    /// The tooltip carries live state — "Strata - scanning C:, 43%" — so it is rewritten on every
    /// status change and is the one field worth a helper. Always null-terminated: Windows reads to
    /// the terminator, and a full buffer with none would run into dwState.
    /// </remarks>
    internal void SetTip(ReadOnlySpan<char> value)
    {
        fixed (char* buffer = szTip)
        {
            var span = new Span<char>(buffer, 128);
            var length = Math.Min(value.Length, span.Length - 1);

            value[..length].CopyTo(span);
            span[length..].Clear();
        }
    }
}
