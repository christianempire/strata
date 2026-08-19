namespace Strata.Core.Scanning;

/// <summary>Why a scan is not running right now.</summary>
public enum DeferralReason
{
    None = 0,

    /// <summary>A game or presentation is on screen. The reason this product has a resource budget.</summary>
    Fullscreen,

    /// <summary>Windows is in a do-not-disturb state.</summary>
    Presenting,

    /// <summary>The user is actively typing. Only defers a full baseline; a delta is cheap enough to run.</summary>
    UserActive,

    /// <summary>System-wide CPU is above the configured threshold.</summary>
    CpuBusy,

    /// <summary>Paused from the tray or the UI.</summary>
    Paused,
}

/// <summary>The outcome of asking whether a scan may run.</summary>
public readonly record struct GateDecision(bool Allowed, DeferralReason Reason, string Detail = "")
{
    public static readonly GateDecision Allow = new(true, DeferralReason.None);

    public static GateDecision Deny(DeferralReason reason, string detail = "") => new(false, reason, detail);
}

/// <summary>
/// Decides whether a scan may run at this instant.
/// </summary>
/// <remarks>
/// The brief treats a frame drop caused by a scan as a project failure rather than a bug, which
/// makes this a correctness boundary and not a performance nicety. It is an interface so the
/// scheduler's behaviour under every combination of conditions can be tested without needing a game
/// running.
/// </remarks>
public interface IScanGate
{
    /// <summary>
    /// Whether a scan of the given tier may start.
    /// </summary>
    /// <remarks>
    /// Tier matters: a full baseline is minutes of sustained I/O and must yield to almost anything,
    /// while an incremental tick is milliseconds and only needs to avoid the cases where even that
    /// is unwelcome.
    /// </remarks>
    GateDecision CanScan(ScanTier tier);
}
