using Strata.Core.Formatting;
using Strata.Platform;

namespace Strata.Host;

/// <summary>
/// <c>strata enable-journal</c> - creates a change journal on volumes that lack one, and grows one
/// that is too small to survive the gap between scans.
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
/// <para>
/// It grows an existing journal as well as creating one because a journal that is present but too
/// small fails in the way that is expensive rather than the way that is obvious: the scanner keeps
/// working, and pays for it with a multi-minute recovery baseline every few days. On this machine
/// all three volumes sat at the Windows default of 32 MB and C: was forced into 13 of them in 22
/// days. See <see cref="Win32UsnJournal.DefaultMaximumSize"/>.
/// </para>
/// </remarks>
internal static class EnableJournalCommand
{
    internal static int Run(CliOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var target = options.JournalSizeBytes is { } mb
            ? (ulong)mb
            : Win32UsnJournal.DefaultMaximumSize;

        var volumes = VolumeEnumerator.Enumerate()
            .Where(VolumesCommand.IsScannable)
            .Where(v => v.SupportsUsnJournal)
            .Where(v => options.Targets.Count == 0 || options.Targets.Any(t => CliOptions.Matches(v, t)))
            .ToList();

        if (!Elevation.IsElevated)
        {
            Console.Error.WriteLine(
                "strata: creating or resizing a change journal requires administrator. " +
                "Re-run this command from an elevated prompt.");
            Console.Error.WriteLine();
        }

        var failures = 0;

        foreach (var volume in volumes)
        {
            using var journal = new Win32UsnJournal(volume);

            var existing = journal.TryGetState();

            if (existing is { } state)
            {
                if (state.MaximumSize >= target)
                {
                    Console.WriteLine(
                        $"{volume.Display,-24} already active at {ByteSize.Format((long)state.MaximumSize)} " +
                        $"(journal {state.JournalId:X}, usn {state.FirstUsn:N0}..{state.NextUsn:N0})");
                    continue;
                }

                if (!journal.TryCreateJournal(target))
                {
                    Console.WriteLine(
                        $"{volume.Display,-24} FAILED to grow from " +
                        $"{ByteSize.Format((long)state.MaximumSize)} to {ByteSize.Format((long)target)}");
                    failures++;
                    continue;
                }

                // Growing a journal is documented to keep its records and its ID, but a stale cursor
                // is a silent multi-minute baseline rather than an error, so the ID is read back and
                // reported rather than assumed.
                var grown = journal.TryGetState();
                var preserved = grown?.JournalId == state.JournalId;

                Console.WriteLine(
                    $"{volume.Display,-24} grown {ByteSize.Format((long)state.MaximumSize)} -> " +
                    $"{ByteSize.Format((long)(grown?.MaximumSize ?? target))} " +
                    (preserved
                        ? $"(journal {state.JournalId:X} kept; history intact)"
                        : $"(journal ID CHANGED {state.JournalId:X} -> {grown?.JournalId:X}; " +
                          "this volume will take one baseline)"));

                continue;
            }

            if (journal.TryCreateJournal(target))
            {
                var created = journal.TryGetState();
                Console.WriteLine(
                    $"{volume.Display,-24} created at {ByteSize.Format((long)(created?.MaximumSize ?? target))} " +
                    $"(journal {created?.JournalId:X})");
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
