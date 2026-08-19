using System.Runtime.InteropServices;
using Strata.Core.Scanning;
using Strata.Platform.Native;

namespace Strata.Platform;

/// <summary>Thresholds for when a scan may run.</summary>
public sealed record ScanGateOptions
{
    /// <summary>System-wide CPU above this defers a scan. Percent.</summary>
    public double CpuThresholdPercent { get; init; } = 70;

    /// <summary>Recent keyboard or mouse activity within this window counts as "actively using".</summary>
    public TimeSpan UserActiveWindow { get; init; } = TimeSpan.FromSeconds(20);

    /// <summary>Supplement the notification-state check with a foreground-window geometry test.</summary>
    public bool DetectBorderlessFullscreen { get; init; } = true;

    public bool Paused { get; init; }
}

/// <summary>
/// The real gate: asks Windows whether now is a bad time.
/// </summary>
/// <remarks>
/// The brief specifies <c>SHQueryUserNotificationState</c>, which is the right primary signal but
/// has a gap worth closing. It reports <c>QUNS_RUNNING_D3D_FULL_SCREEN</c> only for *exclusive*
/// fullscreen, and most modern games run borderless fullscreen windowed, which is an ordinary window
/// as far as that API is concerned. Relying on it alone would let a scan start in the middle of a
/// session in the most common configuration.
/// <para>
/// So it is supplemented with a geometry test: a foreground window that exactly covers its monitor,
/// and is neither the shell nor us, is treated as presentation-like. Two syscalls, and it closes the
/// gap without needing to identify games by name.
/// </para>
/// </remarks>
public sealed class WindowsScanGate : IScanGate
{
    private readonly ScanGateOptions _options;
    private readonly CpuLoadSampler _cpu = new();

    public WindowsScanGate(ScanGateOptions? options = null) => _options = options ?? new ScanGateOptions();

    public GateDecision CanScan(ScanTier tier)
    {
        if (_options.Paused)
        {
            return GateDecision.Deny(DeferralReason.Paused);
        }

        if (TryGetNotificationState(out var state))
        {
            switch (state)
            {
                case Shell32.QUNS_RUNNING_D3D_FULL_SCREEN:
                    return GateDecision.Deny(DeferralReason.Fullscreen, "exclusive fullscreen");

                case Shell32.QUNS_PRESENTATION_MODE:
                    return GateDecision.Deny(DeferralReason.Presenting, "presentation mode");

                case Shell32.QUNS_BUSY:
                    return GateDecision.Deny(DeferralReason.Presenting, "system busy");
            }
        }

        if (_options.DetectBorderlessFullscreen && IsForegroundWindowFullscreen())
        {
            return GateDecision.Deny(DeferralReason.Fullscreen, "borderless fullscreen");
        }

        // A baseline is minutes of sustained I/O, so it waits for the user to step away. An
        // incremental tick is milliseconds and is not worth deferring for typing.
        if (tier == ScanTier.Full && IsUserActive())
        {
            return GateDecision.Deny(DeferralReason.UserActive, "recent input");
        }

        var load = _cpu.SamplePercent();

        if (load is not null && load > _options.CpuThresholdPercent)
        {
            return GateDecision.Deny(DeferralReason.CpuBusy, $"cpu {load:F0}%");
        }

        return GateDecision.Allow;
    }

    private static bool TryGetNotificationState(out int state)
    {
        state = 0;
        return Shell32.SHQueryUserNotificationState(out state) == 0;
    }

    /// <summary>
    /// True when the foreground window covers its whole monitor and is not the desktop or us.
    /// </summary>
    /// <remarks>
    /// The heuristic that catches borderless-windowed games. Deliberately identifies a *shape*
    /// rather than a list of executables: a list would be permanently out of date, and the shape is
    /// what actually indicates the user is looking at one thing full screen.
    /// </remarks>
    private static bool IsForegroundWindowFullscreen()
    {
        var window = User32.GetForegroundWindow();

        if (window == nint.Zero || window == User32.GetShellWindow())
        {
            return false;
        }

        // Our own window being maximised is not a reason to stop scanning.
        _ = User32.GetWindowThreadProcessId(window, out var processId);

        if (processId == Environment.ProcessId)
        {
            return false;
        }

        if (!User32.GetWindowRect(window, out var windowRect))
        {
            return false;
        }

        var monitor = User32.MonitorFromWindow(window, User32.MONITOR_DEFAULTTONEAREST);

        if (monitor == nint.Zero)
        {
            return false;
        }

        var info = new MONITORINFO { cbSize = (uint)Marshal.SizeOf<MONITORINFO>() };

        if (!User32.GetMonitorInfo(monitor, ref info))
        {
            return false;
        }

        // Exact cover, not "close enough": a maximised window stops short of the taskbar, and
        // treating that as fullscreen would stop scanning whenever anything was maximised.
        return windowRect.Left <= info.rcMonitor.Left &&
               windowRect.Top <= info.rcMonitor.Top &&
               windowRect.Right >= info.rcMonitor.Right &&
               windowRect.Bottom >= info.rcMonitor.Bottom;
    }

    private bool IsUserActive()
    {
        var info = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };

        if (!User32.GetLastInputInfo(ref info))
        {
            return false;
        }

        // Both are tick counts from the same clock, so the subtraction is correct across the 49-day
        // wrap that would break a naive comparison.
        var idleMilliseconds = unchecked((uint)Environment.TickCount - info.dwTime);

        return idleMilliseconds < _options.UserActiveWindow.TotalMilliseconds;
    }
}

/// <summary>
/// System-wide CPU load, sampled from kernel time counters.
/// </summary>
/// <remarks>
/// <c>GetSystemTimes</c> deltas rather than a PerformanceCounter: the counter infrastructure takes
/// roughly a second to initialise on first use and is prone to failing outright on machines with a
/// damaged perf-counter registry, neither of which is acceptable in something that has to be
/// invisible.
/// </remarks>
public sealed class CpuLoadSampler
{
    private long _previousIdle;
    private long _previousTotal;
    private bool _hasPrevious;

    /// <summary>
    /// Load since the previous call, or null on the first call, which has nothing to compare to.
    /// </summary>
    public double? SamplePercent()
    {
        if (!Kernel32.GetSystemTimes(out var idle, out var kernel, out var user))
        {
            return null;
        }

        // Kernel time already includes idle time, so total is kernel + user and busy is total minus
        // idle. Subtracting idle from kernel first would double-count.
        var idleTicks = ToTicks(idle);
        var totalTicks = ToTicks(kernel) + ToTicks(user);

        var idleDelta = idleTicks - _previousIdle;
        var totalDelta = totalTicks - _previousTotal;
        var hadPrevious = _hasPrevious;

        _previousIdle = idleTicks;
        _previousTotal = totalTicks;
        _hasPrevious = true;

        // Without a previous sample the deltas are measured against zero, which yields average load
        // since boot rather than load right now. That number is not wrong so much as an answer to a
        // different question, and acting on it would make the gate's first decision arbitrary.
        if (!hadPrevious || totalDelta <= 0 || idleDelta < 0)
        {
            return null;
        }

        return (1.0 - ((double)idleDelta / totalDelta)) * 100.0;
    }

    private static long ToTicks(FILETIME value) => ((long)value.dwHighDateTime << 32) | (uint)value.dwLowDateTime;
}
