using Strata.Core.Model;
using Strata.Platform;

namespace Strata.Host;

/// <summary>
/// <c>strata usn-probe</c> - reports what the change journal permits on this machine.
/// </summary>
/// <remarks>
/// A diagnostic rather than a product feature. It exists because the elevation requirement for
/// Tier 2 could not be settled from documentation, and the answer determines both how the app is
/// packaged and how much of Tier 2 can be exercised in a given session.
/// </remarks>
internal static class UsnProbeCommand
{
    internal static int Run(CliOptions options)
    {

        var volumes = VolumeEnumerator.Enumerate()
            .Where(VolumesCommand.IsScannable)
            .Where(v => options.Targets.Count == 0 || options.Targets.Any(t => CliOptions.Matches(v, t)))
            .ToList();

        Console.WriteLine();
        Console.WriteLine($"Elevated: {(Elevation.IsElevated ? "yes" : "no")}");
        Console.WriteLine();

        foreach (var volume in volumes)
        {
            Console.WriteLine($"=== {volume.Display}  [{volume.FileSystem}] ===");

            if (!volume.SupportsUsnJournal)
            {
                Console.WriteLine("  no change journal on this filesystem; Tier 3 fallback only");
                Console.WriteLine();
                continue;
            }

            Console.WriteLine($"  {"HANDLE",-22} {"OPEN",5} {"QUERY",6} {"READ",6} {"UNPRIV",7}  DETAIL");

            using (var journal = new Win32UsnJournal(volume))
            {
                Console.WriteLine(
                    $"  read path: {(journal.IsPrivileged ? "privileged (names inline)" : "unprivileged (names redacted, resolved by file id)")}");
            }

            foreach (var attempt in UsnAccessProbe.Probe(volume))
            {
                var detail = attempt switch
                {
                    { HandleOpened: false } => $"CreateFile failed, error {attempt.LastError}",
                    { QuerySucceeded: false } => $"query failed, error {attempt.LastError}",
                    _ => $"journal {attempt.JournalId:X}, usn {attempt.FirstUsn}..{attempt.NextUsn}, " +
                         $"{attempt.RecordsReturned} records, V{attempt.RecordVersion}",
                };

                Console.WriteLine(
                    $"  {attempt.Style,-22} {Mark(attempt.HandleOpened),5} {Mark(attempt.QuerySucceeded),6} " +
                    $"{Mark(attempt.PrivilegedReadSucceeded),6} {Mark(attempt.UnprivilegedReadSucceeded),7}  {detail}");
            }

            Console.WriteLine();
        }

        return 0;
    }

    private static string Mark(bool value) => value ? "yes" : "-";

}
