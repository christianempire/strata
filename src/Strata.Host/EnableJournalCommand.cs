using Strata.Platform;

namespace Strata.Host;

/// <summary>
/// <c>strata enable-journal</c> - creates a change journal on volumes that lack one.
/// </summary>
/// <remarks>
/// The single operation in the whole product that genuinely requires administrator. Reading the
/// journal does not: measured on the target machine, a root-directory handle plus the unprivileged
/// FSCTL is enough. Creating one is a change to the filesystem's own metadata and is not something
/// an unprivileged process is allowed to do.
/// <para>
/// It is a separate verb rather than something the scanner does silently, because it is a one-time,
/// persistent change to the volume and the user should be the one asking for it.
/// </para>
/// </remarks>
internal static class EnableJournalCommand
{
    internal static int Run(CliOptions options)
    {
        var volumes = VolumeEnumerator.Enumerate()
            .Where(VolumesCommand.IsScannable)
            .Where(v => v.SupportsUsnJournal)
            .Where(v => options.Targets.Count == 0 || options.Targets.Any(t => CliOptions.Matches(v, t)))
            .ToList();

        if (!Elevation.IsElevated)
        {
            Console.Error.WriteLine(
                "strata: creating a change journal requires administrator. " +
                "Re-run this command from an elevated prompt.");
            Console.Error.WriteLine();
        }

        var failures = 0;

        foreach (var volume in volumes)
        {
            using var journal = new Win32UsnJournal(volume);

            if (journal.TryGetState() is { } existing)
            {
                Console.WriteLine(
                    $"{volume.Display,-24} already active (journal {existing.JournalId:X}, " +
                    $"usn {existing.FirstUsn:N0}..{existing.NextUsn:N0})");
                continue;
            }

            if (journal.TryCreateJournal())
            {
                var created = journal.TryGetState();
                Console.WriteLine($"{volume.Display,-24} created (journal {created?.JournalId:X})");
            }
            else
            {
                Console.WriteLine($"{volume.Display,-24} FAILED to create a journal");
                failures++;
            }
        }

        return failures == 0 ? 0 : 1;
    }
}
