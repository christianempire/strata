using Strata.Core.Scanning;
using Strata.Host.Api;
using Strata.Host.Scanning;
using Strata.Host.Setup;
using Strata.Platform;
using Strata.Storage;

namespace Strata.Host;

/// <summary>
/// <c>strata tray</c> — the shipped shape: the scanner, the API, the UI and a notification icon.
/// </summary>
/// <remarks>
/// The same process as <c>serve</c> with an icon on the front of it. `serve` stays as the thing you
/// run from a console when you want to watch it work; this is the thing a scheduled task starts at
/// logon, with no console and no window.
/// <para>
/// The message pump owns the main thread and the web host runs behind it, which is the opposite of
/// <c>serve</c>. A notification icon's window has to be pumped by the thread that created it, and
/// the pump has to be the thread that outlives everything else — so the host is started, the icon
/// runs, and closing the icon takes the host down with it.
/// </para>
/// </remarks>
internal static class TrayCommand
{
    private const int MenuOpen = 1;
    private const int MenuScan = 2;
    private const int MenuPause = 3;
    private const int MenuSettings = 4;
    private const int MenuExit = 5;

    internal static int Run(CliOptions options)
    {
        // Claimed before the database is opened and long before Kestrel binds. A second tray used
        // to die on the port with a Kestrel stack trace, having already started a second scheduler
        // against the same SQLite file; this says the one useful thing instead.
        using var instance = SingleInstance.TryAcquire();

        if (instance is null)
        {
            Console.Error.WriteLine("strata: Strata is already running in this session.");
            return 1;
        }

        var database = new StrataDatabase(options.DatabasePath);
        database.Migrate();

        var app = ApiHost.Build(database, new ApiOptions
        {
            Port = options.Port,
            LogRequests = options.Verbose,
        });

        var url = $"http://localhost:{options.Port}/";

        using var tray = new TrayIcon();
        using var stopping = new CancellationTokenSource();

        // StartAsync rather than Run: the message pump owns the main thread from here on, so the
        // host has to come up behind it rather than block on it.
        app.StartAsync().GetAwaiter().GetResult();

        var scheduler = app.Services.GetService(typeof(ScanScheduler)) as ScanScheduler;
        var status = new TrayStatus(database, scheduler, tray, url, stopping.Token);

        // How `strata install` replaces a running copy: the same shutdown path as Exit from the
        // menu, so a tick in flight ends the way it would have anyway rather than being killed.
        SingleInstance.ListenForShutdown(tray.Quit, stopping.Token);

        tray.Activated += () => Browser.Open(url);

        tray.Clicked += id =>
        {
            switch (id)
            {
                case MenuOpen:
                    Browser.Open(url);
                    break;

                case MenuScan:
                    status.ScanNow();
                    break;

                case MenuPause:
                    status.TogglePause();
                    break;

                case MenuSettings:
                    Browser.Open(url + "settings");
                    break;

                case MenuExit:
                    tray.Quit();
                    break;
            }
        };

        tray.Registered += ok =>
        {
            // Said out loud because "I cannot see it" is the expected first impression either way:
            // Windows 11 puts every new notification icon in the overflow flyout until it is
            // dragged out, so a working icon and a failed one look identical on the taskbar.
            Console.WriteLine(ok
                ? "Tray icon registered. Windows 11 hides new icons in the overflow: click the ^ in"
                  + " the notification area, then drag Strata onto the taskbar to keep it there."
                : "Tray icon could NOT be registered. The scanner and the UI are running regardless.");
        };

        Console.WriteLine($"Strata on {url}  (loopback only)");
        Console.WriteLine($"Database: {database.DatabasePath}");
        Console.WriteLine();

        status.Start();

        // Blocks on the message pump until Exit.
        tray.Run(Icons.For(ScannerState.Idle), status.Menu);

        stopping.Cancel();

        // Bounded, so Exit from the tray cannot hang on a baseline that is mid-directory. The
        // scheduler's own StopAsync already joins its thread with the same patience.
        using var shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        app.StopAsync(shutdown.Token).GetAwaiter().GetResult();

        return 0;
    }
}

/// <summary>Where the state icons live, and which one goes with which state.</summary>
/// <remarks>
/// Beside the executable, copied there from <c>docs/design/icon</c> at build time. A missing file
/// leaves the tray icon blank rather than failing — the process is still doing its job, and refusing
/// to start a disk scanner because a picture is absent would be absurd.
/// </remarks>
internal static class Icons
{
    private static readonly string Directory =
        Path.Combine(AppContext.BaseDirectory, "icons");

    internal static string For(ScannerState state) => Path.Combine(Directory, state switch
    {
        ScannerState.Scanning => "strata-scanning.ico",
        ScannerState.Deferred => "strata-deferred.ico",
        ScannerState.Paused => "strata-paused.ico",
        _ => "strata-idle.ico",
    });
}

/// <summary>
/// Keeps the icon and its menu in step with the scanner.
/// </summary>
/// <remarks>
/// Polls rather than subscribing. The scheduler has no event for this and giving it one would mean
/// it knowing about a UI; a two-second poll of an in-memory record costs nothing and keeps the
/// dependency pointing the right way.
/// </remarks>
internal sealed class TrayStatus
{
    private readonly StrataDatabase _database;
    private readonly ScanScheduler? _scheduler;
    private readonly TrayIcon _tray;
    private readonly string _url;
    private readonly CancellationToken _stopping;

    private ScannerState _shown = (ScannerState)(-1);
    private bool _paused;
    private bool _scanning;

    internal TrayStatus(
        StrataDatabase database,
        ScanScheduler? scheduler,
        TrayIcon tray,
        string url,
        CancellationToken stopping)
    {
        _database = database;
        _scheduler = scheduler;
        _tray = tray;
        _url = url;
        _stopping = stopping;
    }

    internal void Start()
    {
        var thread = new Thread(Loop)
        {
            IsBackground = true,
            Name = "strata-tray-status",
        };

        thread.Start();
    }

    /// <summary>
    /// The menu, rebuilt on every open.
    /// </summary>
    /// <remarks>
    /// Rebuilt rather than kept, so it always describes the moment it was opened: Pause reads Resume
    /// when paused, and Scan now is greyed while a scan is already running rather than queueing a
    /// second one behind it.
    /// </remarks>
    internal IReadOnlyList<TrayMenuItem> Menu() =>
    [
        new(1, "Open Strata"),
        TrayMenuItem.Separator,
        new(2, _scanning ? "Scanning…" : "Scan now", Enabled: !_scanning),
        new(3, _paused ? "Resume scanning" : "Pause scanning", Checked: _paused),
        TrayMenuItem.Separator,
        new(4, "Settings"),
        TrayMenuItem.Separator,
        new(5, "Exit"),
    ];

    /// <summary>Queues an incremental tick on every volume that can take one.</summary>
    internal void ScanNow()
    {
        if (_scheduler is null)
        {
            return;
        }

        foreach (var volume in VolumeEnumerator.Enumerate().Where(VolumesCommand.IsScannable))
        {
            if (volume.SupportsUsnJournal)
            {
                _scheduler.Enqueue(volume, ScanTier.Incremental);
            }
        }
    }

    internal void TogglePause()
    {
        try
        {
            using var connection = _database.Open();

            var settings = SettingsRepository.Load(connection);
            SettingsRepository.Save(connection, settings with { Paused = !settings.Paused });

            _scheduler?.Poke();
        }
        catch (Microsoft.Data.Sqlite.SqliteException)
        {
            // A failed toggle is worth nothing more than the next poll correcting the menu.
        }
    }

    private void Loop()
    {
        while (!_stopping.IsCancellationRequested)
        {
            try
            {
                Refresh();
            }
            catch (Exception ex) when (ex is IOException or Microsoft.Data.Sqlite.SqliteException)
            {
                // The tray outliving a transient database problem matters more than it reporting
                // one; the UI behind it says so properly.
            }

            _stopping.WaitHandle.WaitOne(TimeSpan.FromSeconds(2));
        }
    }

    private void Refresh()
    {
        var status = _scheduler?.Status();
        var state = status?.State ?? ScannerState.Idle;

        _paused = state == ScannerState.Paused;
        _scanning = state == ScannerState.Scanning;

        if (state != _shown)
        {
            _shown = state;
            _tray.SetIcon(Icons.For(state));
        }

        _tray.SetTooltip(Tooltip(status));
    }

    /// <summary>
    /// What the icon says on hover.
    /// </summary>
    /// <remarks>
    /// The whole product in 127 characters. A deferral names its reason, because "paused because a
    /// game is running fullscreen" is the single most reassuring thing this software can say and it
    /// should not require opening a browser to read.
    /// </remarks>
    private string Tooltip(SchedulerStatus? status)
    {
        if (status is null)
        {
            return $"Strata — {_url}";
        }

        return status.State switch
        {
            ScannerState.Scanning when status.Activity is { Percent: { } pct } =>
                $"Strata — scanning {status.Activity.Volume}, {pct:F0}%",
            ScannerState.Scanning when status.Activity is { } activity =>
                $"Strata — scanning {activity.Volume}",
            ScannerState.Deferred =>
                $"Strata — deferred: {status.Deferral}",
            ScannerState.Paused =>
                "Strata — paused",
            _ => status.LastTickUtc is { } tick
                ? $"Strata — idle, last scan {DateTime.Parse(tick, null, System.Globalization.DateTimeStyles.RoundtripKind).ToLocalTime():HH:mm}"
                : "Strata — idle",
        };
    }
}

/// <summary>Opens a URL in whatever the user's default browser is.</summary>
internal static class Browser
{
    internal static void Open(string url)
    {
        try
        {
            // UseShellExecute is the whole point: it hands the URL to the shell, which picks the
            // default browser. Without it this tries to execute the URL as a program.
            using var process = System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException)
        {
            // No default browser, or the shell refused. Nothing useful to do from a tray icon.
        }
    }
}
