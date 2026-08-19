namespace Strata.Core.Model;

/// <summary>How a volume is attached, which decides whether Strata will scan it unprompted.</summary>
public enum VolumeKind
{
    Unknown = 0,
    Fixed,
    Removable,
    Network,
    CdRom,
    RamDisk,
}

/// <summary>
/// A volume as discovered on the machine.
/// </summary>
/// <remarks>
/// <see cref="GuidPath"/> — the \?\Volume{...}\ path — is the identity. Drive letters are a mutable
/// display attribute: they get reassigned, and letter-keyed history would silently corrupt if two
/// volumes ever swapped letters.
/// </remarks>
public sealed record VolumeIdentity
{
    /// <summary>The \?\Volume{guid}\ path. Stable across reboots and letter reassignment.</summary>
    public required string GuidPath { get; init; }

    /// <summary>Drive letter with colon (for example "D:"), or null for an unlettered volume.</summary>
    public string? DriveLetter { get; init; }

    public string? Label { get; init; }

    /// <summary>Filesystem name as reported by the OS: NTFS, ReFS, exFAT, FAT32.</summary>
    public string FileSystem { get; init; } = "";

    public VolumeKind Kind { get; init; }

    public uint SerialNumber { get; init; }

    public long TotalBytes { get; init; }

    public long FreeBytes { get; init; }

    /// <summary>Physical disk number, for grouping volumes by drive in the UI. Null if unavailable.</summary>
    public int? DiskNumber { get; init; }

    /// <summary>True when the volume supports the NTFS change journal, so Tier 2 is a candidate.</summary>
    public bool SupportsUsnJournal => FileSystem.Equals("NTFS", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Root path to enumerate from. Prefers the volume GUID path so unlettered volumes work, but
    /// uses the drive letter when present because it produces far more readable output.
    /// </summary>
    public string RootPath => DriveLetter is not null ? @"\\?\" + DriveLetter + @"\" : GuidPath;

    /// <summary>Short human label for CLI and UI: "D: (Storage-M2-1TB)".</summary>
    public string Display =>
        (DriveLetter ?? GuidPath) + (string.IsNullOrEmpty(Label) ? "" : $" ({Label})");

    public long UsedBytes => TotalBytes - FreeBytes;
}
