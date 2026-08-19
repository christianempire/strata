using System.Runtime.InteropServices;
using Strata.Platform.Native;

namespace Strata.Platform;

/// <summary>One entry in the tray menu.</summary>
/// <param name="Id">Returned by <see cref="TrayIcon.Clicked"/> when chosen. Zero means a separator.</param>
public readonly record struct TrayMenuItem(int Id, string Text, bool Enabled = true, bool Checked = false)
{
    public static TrayMenuItem Separator => new(0, "");
}

/// <summary>
/// A notification-area icon and its menu.
/// </summary>
/// <remarks>
/// Raw Shell_NotifyIcon rather than Windows Forms' <c>NotifyIcon</c>. The wrapper would be a
/// hundred lines shorter, but it loads the Windows Forms stack into a process whose entire promise
/// is to cost nothing while sitting still, and this project already owns its Win32 layer — volume
/// enumeration, the change journal and the idle gate are all P/Invoke by choice. One more area is
/// cheaper than one more framework.
/// <para>
/// **Owns a thread.** A notification-area icon needs a window, a window needs a message pump, and
/// the pump must be the same thread that created the window. <see cref="Run"/> blocks on it, so the
/// caller gives this a thread and gets it back when the icon exits.
/// </para>
/// </remarks>
public sealed class TrayIcon : IDisposable
{
    /// <summary>Our own message for the icon's mouse events. Anything from WM_USER up is ours.</summary>
    private const uint WM_TRAYICON = User32.WM_USER + 1;

    /// <summary>Broadcast when Explorer restarts, taking every tray icon with it.</summary>
    private readonly uint _taskbarCreated;

    private readonly string _className = "StrataTray_" + Guid.NewGuid().ToString("N");
    private readonly WndProc _wndProc;
    private readonly object _gate = new();

    private nint _hwnd;
    private nint _icon;
    private string _tip = "Strata";
    private bool _added;
    private bool _disposed;

    private Func<IReadOnlyList<TrayMenuItem>>? _menu;

    private delegate nint WndProc(nint hWnd, uint msg, nint wParam, nint lParam);

    public TrayIcon()
    {
        // Held in a field: the delegate is handed to Windows as a function pointer, and a local
        // would be collected while the window still had it. This is the classic way to crash an
        // hour after startup.
        _wndProc = WindowProcedure;
        _taskbarCreated = User32.RegisterWindowMessage("TaskbarCreated");
    }

    /// <summary>Raised on the message-pump thread when a menu item is chosen.</summary>
    public event Action<int>? Clicked;

    /// <summary>Raised when the icon is double-clicked, or single-clicked with the left button.</summary>
    public event Action? Activated;

    /// <summary>
    /// Raised once the icon has been offered to the notification area, with whether it took.
    /// </summary>
    /// <remarks>
    /// Worth surfacing rather than assuming. Shell_NotifyIcon fails silently by returning false, and
    /// a tray application whose icon never appeared is indistinguishable from one that crashed —
    /// especially on Windows 11, which hides new icons in the overflow flyout, so "I cannot see it"
    /// is the expected first impression either way.
    /// </remarks>
    public event Action<bool>? Registered;

    /// <summary>
    /// Runs the icon and pumps messages until <see cref="Quit"/>.
    /// </summary>
    /// <param name="iconPath">A .ico file. Missing or unreadable leaves the icon blank rather than failing.</param>
    /// <param name="menu">
    /// Asked for the menu each time it is opened, rather than given one once, so items can reflect
    /// the scanner's state at the moment of the click — "Pause" against "Resume", a greyed
    /// "Scan now" while a scan runs.
    /// </param>
    public void Run(string iconPath, Func<IReadOnlyList<TrayMenuItem>> menu)
    {
        ArgumentNullException.ThrowIfNull(menu);

        _menu = menu;

        CreateWindow();
        LoadIcon(iconPath);
        Add();

        Pump();
    }

    /// <summary>Replaces the tooltip. Safe from any thread.</summary>
    public void SetTooltip(string tip)
    {
        lock (_gate)
        {
            _tip = tip ?? "Strata";

            if (_added)
            {
                Modify();
            }
        }
    }

    /// <summary>Swaps the icon, for the scanning and deferred states. Safe from any thread.</summary>
    public void SetIcon(string iconPath)
    {
        lock (_gate)
        {
            LoadIcon(iconPath);

            if (_added)
            {
                Modify();
            }
        }
    }

    /// <summary>Ends the message loop. Safe from any thread.</summary>
    public void Quit()
    {
        if (_hwnd != 0)
        {
            User32.PostMessage(_hwnd, User32.WM_CLOSE, 0, 0);
        }
    }

    private void CreateWindow()
    {
        var module = Marshal.GetHINSTANCE(typeof(TrayIcon).Module);
        var className = Marshal.StringToHGlobalUni(_className);

        try
        {
            var wc = new WNDCLASSEXW
            {
                cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
                hInstance = module,
                lpszClassName = className,
            };

            if (User32.RegisterClassEx(ref wc) == 0)
            {
                throw new InvalidOperationException(
                    $"Could not register the tray window class. {Marshal.GetLastPInvokeError()}");
            }

            // A message-only window: parented to HWND_MESSAGE, never shown, never on the taskbar.
            // It exists solely to receive the icon's callbacks.
            _hwnd = User32.CreateWindowEx(
                0, _className, "Strata", 0, 0, 0, 0, 0, -3, 0, module, 0);

            if (_hwnd == 0)
            {
                throw new InvalidOperationException(
                    $"Could not create the tray window. {Marshal.GetLastPInvokeError()}");
            }
        }
        finally
        {
            Marshal.FreeHGlobal(className);
        }
    }

    /// <summary>
    /// Loads the icon at the size the notification area actually asks for.
    /// </summary>
    /// <remarks>
    /// <c>GetSystemMetrics(SM_CXSMICON)</c> rather than a hard-coded 16, because it is 16 at 100%
    /// DPI and 32 at 200%, and a 16px icon stretched to 32 is the blurry tray icon everyone
    /// recognises. A .ico holding both lets Windows pick.
    /// </remarks>
    private void LoadIcon(string iconPath)
    {
        if (string.IsNullOrWhiteSpace(iconPath) || !File.Exists(iconPath))
        {
            return;
        }

        var width = User32.GetSystemMetrics(User32.SM_CXSMICON);
        var height = User32.GetSystemMetrics(User32.SM_CYSMICON);

        var loaded = User32.LoadImage(
            0, iconPath, User32.IMAGE_ICON, width, height, User32.LR_LOADFROMFILE);

        if (loaded == 0)
        {
            return;
        }

        var previous = _icon;
        _icon = loaded;

        // Replaced only after the new one is in place, so a failed load never leaves the tray blank.
        if (previous != 0)
        {
            User32.DestroyIcon(previous);
        }
    }

    private NOTIFYICONDATAW Descriptor()
    {
        var data = new NOTIFYICONDATAW
        {
            cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATAW>(),
            hWnd = _hwnd,
            uID = 1,
            uFlags = Shell32Tray.NIF_MESSAGE | Shell32Tray.NIF_ICON | Shell32Tray.NIF_TIP,
            uCallbackMessage = WM_TRAYICON,
            hIcon = _icon,
            uVersion = Shell32Tray.NOTIFYICON_VERSION_4,
        };

        data.SetTip(_tip);

        return data;
    }

    private void Add()
    {
        var data = Descriptor();

        if (Shell32Tray.Shell_NotifyIcon(Shell32Tray.NIM_ADD, ref data))
        {
            _added = true;
            Shell32Tray.Shell_NotifyIcon(Shell32Tray.NIM_SETVERSION, ref data);
        }

        Registered?.Invoke(_added);
    }

    private void Modify()
    {
        var data = Descriptor();
        Shell32Tray.Shell_NotifyIcon(Shell32Tray.NIM_MODIFY, ref data);
    }

    private void Remove()
    {
        if (!_added)
        {
            return;
        }

        var data = Descriptor();
        Shell32Tray.Shell_NotifyIcon(Shell32Tray.NIM_DELETE, ref data);
        _added = false;
    }

    /// <summary>
    /// The message loop.
    /// </summary>
    /// <remarks>
    /// Static because it pumps the thread's queue rather than this window's: a filter on
    /// <see cref="_hwnd"/> would drop the TaskbarCreated broadcast, which is sent to top-level
    /// windows and is the only way to notice Explorer restarting.
    /// </remarks>
    private static void Pump()
    {
        while (User32.GetMessage(out var msg, 0, 0, 0) > 0)
        {
            User32.TranslateMessage(ref msg);
            User32.DispatchMessage(ref msg);
        }
    }

    private nint WindowProcedure(nint hWnd, uint msg, nint wParam, nint lParam)
    {
        // Explorer restarted and threw the notification area away with every icon in it. Re-adding
        // is the documented recovery, and without it the tray silently empties on an Explorer crash
        // and stays empty until the process is restarted.
        if (msg == _taskbarCreated)
        {
            _added = false;
            Add();

            return 0;
        }

        switch (msg)
        {
            case WM_TRAYICON:
                OnTrayMessage((uint)(lParam & 0xFFFF));
                return 0;

            case User32.WM_COMMAND:
                var id = (int)(wParam & 0xFFFF);

                if (id != 0)
                {
                    Clicked?.Invoke(id);
                }

                return 0;

            case User32.WM_CLOSE:
                Remove();
                User32.DestroyWindow(hWnd);
                return 0;

            case User32.WM_DESTROY:
                User32.PostQuitMessage(0);
                return 0;
        }

        return User32.DefWindowProc(hWnd, msg, wParam, lParam);
    }

    private void OnTrayMessage(uint mouse)
    {
        switch (mouse)
        {
            case User32.WM_LBUTTONUP:
                Activated?.Invoke();
                break;

            case User32.WM_RBUTTONUP:
            case User32.WM_CONTEXTMENU:
                ShowMenu();
                break;
        }
    }

    private void ShowMenu()
    {
        var items = _menu?.Invoke();

        if (items is null || items.Count == 0)
        {
            return;
        }

        var menu = User32.CreatePopupMenu();

        if (menu == 0)
        {
            return;
        }

        try
        {
            foreach (var item in items)
            {
                if (item.Id == 0)
                {
                    User32.AppendMenu(menu, User32.MF_SEPARATOR, 0, null);
                    continue;
                }

                var flags = User32.MF_STRING;

                if (!item.Enabled)
                {
                    flags |= User32.MF_GRAYED | User32.MF_DISABLED;
                }

                if (item.Checked)
                {
                    flags |= User32.MF_CHECKED;
                }

                User32.AppendMenu(menu, flags, (nuint)item.Id, item.Text);
            }

            User32.GetCursorPos(out var cursor);

            // Required, not decorative: without it the menu does not dismiss when the user clicks
            // away and sits over the desktop until it is clicked through.
            User32.SetForegroundWindow(_hwnd);

            var chosen = User32.TrackPopupMenuEx(
                menu,
                User32.TPM_RIGHTBUTTON | User32.TPM_RETURNCMD | User32.TPM_BOTTOMALIGN,
                cursor.x,
                cursor.y,
                _hwnd,
                0);

            if (chosen != 0)
            {
                Clicked?.Invoke(chosen);
            }
        }
        finally
        {
            User32.DestroyMenu(menu);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        Remove();

        if (_icon != 0)
        {
            User32.DestroyIcon(_icon);
            _icon = 0;
        }

        if (_hwnd != 0)
        {
            User32.DestroyWindow(_hwnd);
            _hwnd = 0;
        }
    }
}
