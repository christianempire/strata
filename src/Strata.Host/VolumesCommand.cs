using Strata.Core.Formatting;
using Strata.Core.Model;
using Strata.Platform;

namespace Strata.Host;

/// <summary>
/// <c>strata volumes</c> — lists every volume the OS reports, and says what Strata would do with it.
/// </summary>
/// <remarks>
/// Volumes Strata will not scan are listed anyway, with the reason. Silently omitting them is how a
/// user ends up wondering why their external drive never appears.
/// </remarks>
internal static class VolumesCommand
{
    internal static int Run(CliOptions options)
    {
        var volumes = VolumeEnumerator.Enumerate();

        Console.WriteLine();
        Console.WriteLine(
            $"{"VOLUME",-8} {"LABEL",-18} {"FS",-6} {"DISK",4} {"SIZE",10} {"FREE",10} {"USED",10}  PLAN");

        Console.WriteLine(new string('-', 100));

        foreach (var volume in volumes)
        {
            var name = volume.DriveLetter ?? "(none)";
            var label = Truncate(string.IsNullOrEmpty(volume.Label) ? "-" : volume.Label, 18);
            var disk = volume.DiskNumber?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "-";

            Console.WriteLine(
                $"{name,-8} {label,-18} {volume.FileSystem,-6} {disk,4} " +
                $"{ByteSize.Format(volume.TotalBytes),10} " +
                $"{ByteSize.Format(volume.FreeBytes),10} " +
                $"{ByteSize.Format(volume.UsedBytes),10}  {DescribePlan(volume)}");
        }

        Console.WriteLine();
        Console.WriteLine($"{volumes.Count} volume(s). {volumes.Count(IsScannable)} eligible for scanning.");
        Console.WriteLine();

        if (!options.Quiet)
        {
            Console.WriteLine("PLAN column reflects Phase 1 only; Tier 2 arrives in Phase 2.");
            Console.WriteLine();
        }

        return 0;
    }

    /// <summary>
    /// Whether Strata would scan this volume unprompted.
    /// </summary>
    /// <remarks>
    /// A volume with no drive letter is left alone. Windows keeps the EFI system partition and the
    /// WinRE recovery partition unmounted on purpose, and neither is something a person is looking
    /// for: File Explorer does not show them, they hold a few hundred megabytes that never move, and
    /// nothing in this tool can point at a path inside one.
    /// <para>
    /// The cost of not excluding them is not the scan — it is the log. The scheduler attempts every
    /// watchable volume on every tick, so two partitions nobody will ever baseline were writing two
    /// "no baseline yet" rows into <c>scan_log</c> every fifteen minutes, forever, on the one table
    /// that exists to be read by a person auditing the scanner's behaviour.
    /// </para>
    /// <para>
    /// This gates the unprompted path only. An explicit <c>strata scan</c> or <c>POST /api/scan</c>
    /// naming one by GUID still runs: it is a read-only tool and there is no reason to refuse
    /// somebody who asked.
    /// </para>
    /// </remarks>
    internal static bool IsScannable(VolumeIdentity volume)
        => volume.Kind is VolumeKind.Fixed or VolumeKind.RamDisk
           && volume.TotalBytes > 0
           && volume.DriveLetter is not null;

    /// <summary>
    /// What Strata would do with this volume, so the eventual scan strategy is visible before any
    /// scanning happens.
    /// </summary>
    private static string DescribePlan(VolumeIdentity volume)
    {
        if (volume.Kind is VolumeKind.Removable or VolumeKind.Network or VolumeKind.CdRom)
        {
            return $"skip ({volume.Kind.ToString().ToLowerInvariant()}, opt in to include)";
        }

        if (volume.TotalBytes == 0)
        {
            return "skip (no media)";
        }

        if (volume.DriveLetter is null)
        {
            return "skip (no mount point; scan by GUID to override)";
        }

        if (!IsScannable(volume))
        {
            return "skip (unsupported)";
        }

        return volume.SupportsUsnJournal
            ? "full scan; Tier 2 candidate"
            : $"full scan; reduced fidelity ({volume.FileSystem})";
    }

    private static string Truncate(string value, int width)
        => value.Length <= width ? value : value[..(width - 1)] + "…";
}
