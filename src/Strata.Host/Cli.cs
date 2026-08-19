using Strata.Core.Model;
using Strata.Storage;

namespace Strata.Host;

/// <summary>
/// Phase 1 command line.
/// </summary>
/// <remarks>
/// Hand-rolled argument parsing rather than a parser library: there are two verbs, and a dependency
/// that has repeatedly changed its API is not worth taking on for that. This grows into the tray
/// application's entry point in Phase 4, at which point it can be revisited.
/// </remarks>
internal static class Cli
{
    internal static int Run(string[] args)
    {
        if (args.Length == 0 || IsHelp(args[0]))
        {
            PrintUsage();
            return args.Length == 0 ? 1 : 0;
        }

        try
        {
            return args[0].ToLowerInvariant() switch
            {
                "volumes" => VolumesCommand.Run(ParseOptions(args)),
                "scan" => ScanCommand.Run(ParseOptions(args)),
                "usn-probe" => UsnProbeCommand.Run(ParseOptions(args)),
                _ => UnknownVerb(args[0]),
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine($"strata: {ex.Message}");

            if (ex.InnerException is not null)
            {
                Console.Error.WriteLine($"  caused by: {ex.InnerException.Message}");
            }

            return 1;
        }
    }

    private static int UnknownVerb(string verb)
    {
        Console.Error.WriteLine($"strata: unknown command '{verb}'");
        PrintUsage();
        return 1;
    }

    private static bool IsHelp(string arg)
        => arg is "-h" or "--help" or "help" or "-?" or "/?";

    private static CliOptions ParseOptions(string[] args)
    {
        var options = new CliOptions();

        for (var i = 1; i < args.Length; i++)
        {
            var arg = args[i];

            switch (arg)
            {
                case "--db":
                    options.DatabasePath = RequireValue(args, ref i);
                    break;

                case "--threads":
                    options.Threads = int.Parse(RequireValue(args, ref i), Culture);
                    break;

                case "--depth":
                    options.MaxDepth = int.Parse(RequireValue(args, ref i), Culture);
                    break;

                case "--floor-mb":
                    options.SizeFloorBytes = long.Parse(RequireValue(args, ref i), Culture) * 1024 * 1024;
                    break;

                case "--exclude":
                    options.Excluded.Add(RequireValue(args, ref i));
                    break;

                case "--all":
                    options.AllVolumes = true;
                    break;

                case "--no-dedup":
                    options.DeduplicateHardLinks = false;
                    break;

                case "--no-background-io":
                    options.BackgroundIo = false;
                    break;

                case "--no-store":
                    options.Persist = false;
                    break;

                case "--histogram":
                    options.ShowHistogram = true;
                    break;

                case "--top":
                    options.TopDirectories = int.Parse(RequireValue(args, ref i), Culture);
                    break;

                case "--no-compact":
                    options.SkipCompact = true;
                    break;

                case "--quiet":
                    options.Quiet = true;
                    break;

                default:
                    if (arg.StartsWith('-'))
                    {
                        throw new ArgumentException($"unknown option '{arg}'");
                    }

                    options.Targets.Add(arg);
                    break;
            }
        }

        return options;
    }

    private static string RequireValue(string[] args, ref int index)
    {
        if (index + 1 >= args.Length)
        {
            throw new ArgumentException($"option '{args[index]}' needs a value");
        }

        return args[++index];
    }

    private static System.Globalization.CultureInfo Culture
        => System.Globalization.CultureInfo.InvariantCulture;

    private static void PrintUsage()
    {
        Console.WriteLine("""
            strata — disk usage with history

            USAGE
              strata volumes                     list volumes and whether Strata will scan them
              strata scan [<volume>...]          run a full baseline scan
              strata scan --all                  scan every eligible fixed volume
              strata usn-probe [<volume>...]     report what the change journal permits here

            SCAN OPTIONS
              --db <path>          database file (default: %LOCALAPPDATA%\Strata\strata.db)
              --threads <n>        enumeration workers (default: min(8, cores/4))
              --depth <n>          snapshot_dirs depth cap (default: 8)
              --floor-mb <n>       snapshot_dirs size floor in MB (default: 10)
              --exclude <name>     skip directories with this name, repeatable
              --no-dedup           do not deduplicate hardlinks, to measure what it costs
              --no-background-io   do not lower I/O priority, to measure what it costs
              --no-store           scan and report without writing to the database
              --histogram          report directory counts by depth and size floor
              --top <n>            list the n largest directories (default: 15)
              --no-compact         skip the post-scan VACUUM
              --quiet              suppress progress output

            EXAMPLES
              strata scan D:
              strata scan --all --histogram
              strata scan C: --no-store --no-background-io --threads 8
            """);
    }
}

/// <summary>Parsed command line.</summary>
internal sealed class CliOptions
{
    internal List<string> Targets { get; } = [];

    internal List<string> Excluded { get; } = [];

    internal string DatabasePath { get; set; } = StrataDatabase.DefaultPath;

    internal int? Threads { get; set; }

    internal int MaxDepth { get; set; } = 8;

    internal long SizeFloorBytes { get; set; } = 10L * 1024 * 1024;

    internal bool AllVolumes { get; set; }

    internal bool DeduplicateHardLinks { get; set; } = true;

    internal bool BackgroundIo { get; set; } = true;

    internal bool Persist { get; set; } = true;

    internal bool ShowHistogram { get; set; }

    internal int TopDirectories { get; set; } = 15;

    internal bool SkipCompact { get; set; }

    internal bool Quiet { get; set; }

    /// <summary>
    /// Matches a user-supplied target ("D:", "d", or a GUID path) against a discovered volume.
    /// </summary>
    internal static bool Matches(VolumeIdentity volume, string target)
    {
        var normalised = target.TrimEnd('\\');

        if (normalised.Length == 1)
        {
            normalised += ":";
        }

        return string.Equals(volume.DriveLetter, normalised, StringComparison.OrdinalIgnoreCase)
            || string.Equals(volume.GuidPath.TrimEnd('\\'), normalised, StringComparison.OrdinalIgnoreCase);
    }
}
