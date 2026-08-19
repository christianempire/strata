using Strata.Core.Formatting;
using Strata.Core.Model;
using Strata.Core.Scanning;
using Strata.Platform;

namespace Strata.Host;

/// <summary>
/// <c>strata usn-tail</c> - prints live change-journal records.
/// </summary>
/// <remarks>
/// A diagnostic. Record parsing is hand-written against byte offsets, and a wrong constant there
/// produces plausible-looking nonsense rather than an error, so this exists to check the output
/// against changes whose truth is known - touch a file, watch it appear.
/// </remarks>
internal static class UsnTailCommand
{
    internal static int Run(CliOptions options)
    {
        var volume = VolumeEnumerator.Enumerate()
            .Where(VolumesCommand.IsScannable)
            .FirstOrDefault(v => options.Targets.Count == 0 || options.Targets.Any(t => CliOptions.Matches(v, t)));

        if (volume is null)
        {
            Console.Error.WriteLine("strata: no matching volume");
            return 1;
        }

        using var journal = new Win32UsnJournal(volume);
        using var metadata = new Win32FileMetadataReader(volume);

        var state = journal.TryGetState();

        if (state is null)
        {
            Console.Error.WriteLine(
                $"strata: no readable change journal on {volume.Display}. " +
                "Run 'strata enable-journal' elevated to create one.");
            return 1;
        }

        Console.WriteLine($"{volume.Display}  journal {state.Value.JournalId:X}");
        Console.WriteLine($"  usn range {state.Value.FirstUsn:N0} .. {state.Value.NextUsn:N0}");
        Console.WriteLine();

        // Seed from a USN the journal itself reported. An arbitrary value - "head minus a bit" -
        // is rejected with ERROR_INVALID_PARAMETER whenever it does not land on a record boundary,
        // which makes it fail intermittently depending on what the machine happened to be doing.
        // In production this never arises: a stored cursor is always a value the journal returned.
        var cursor = new UsnCursor(
            state.Value.JournalId,
            options.FromStart ? state.Value.FirstUsn : state.Value.NextUsn);
        var deadline = DateTime.UtcNow.AddSeconds(options.TailSeconds);
        var shown = 0;

        while (DateTime.UtcNow < deadline && shown < options.TailLimit)
        {
            var result = journal.Read(cursor, maxRecords: 512);

            if (result.Status != UsnReadStatus.Ok)
            {
                Console.Error.WriteLine($"strata: journal read failed: {result.Status} ({result.NativeErrorCode})");
                return 1;
            }

            foreach (var change in result.Changes)
            {
                if (shown++ >= options.TailLimit)
                {
                    break;
                }

                var size = metadata.TryReadById(change.FileId, out var meta)
                    ? ByteSize.Format(meta.LogicalSize) + (meta.HasMultipleLinks ? $" x{meta.LinkCount}" : "")
                    : "(gone)";

                // The journal name is empty under the unprivileged read, so fall back to resolving
                // the path from the file ID.
                var label = !string.IsNullOrEmpty(change.Name)
                    ? change.Name + "  (from journal)"
                    : metadata.TryGetPath(change.FileId, out var resolved)
                        ? resolved + "  (resolved by id)"
                        : "(unresolvable)";

                Console.WriteLine(
                    $"  {change.TimestampUtc:HH:mm:ss}  {change.Kind,-28} {size,12}  " +
                    $"{(change.IsDirectory ? "[dir] " : "")}{label}");
            }

            cursor = cursor with { NextUsn = result.NextUsn };

            if (!result.MoreAvailable)
            {
                Thread.Sleep(250);
            }
        }

        Console.WriteLine();
        Console.WriteLine($"{shown} record(s).");
        return 0;
    }
}
