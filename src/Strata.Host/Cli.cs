using Strata.Core.Model;
using Strata.Host.Setup;
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
                "usn-tail" => UsnTailCommand.Run(ParseOptions(args)),
                "watch" => WatchCommand.Run(ParseOptions(args)),
                "prune" => PruneCommand.Run(ParseOptions(args)),
                "serve" => ServeCommand.Run(ParseOptions(args)),
                "tray" => TrayCommand.Run(ParseOptions(args)),
                "enable-journal" => EnableJournalCommand.Run(ParseOptions(args)),
                "install" => InstallCommand.Run(ParseOptions(args)),
                "uninstall" => UninstallCommand.Run(ParseOptions(args)),
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

                case "--journal-mb":
                    options.JournalSizeBytes =
                        long.Parse(RequireValue(args, ref i), Culture) * 1024 * 1024;
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

                case "--seconds":
                    options.TailSeconds = int.Parse(RequireValue(args, ref i), Culture);
                    break;

                case "--from-start":
                    options.FromStart = true;
                    break;

                case "--limit":
                    options.TailLimit = int.Parse(RequireValue(args, ref i), Culture);
                    break;

                case "--port":
                    options.Port = int.Parse(RequireValue(args, ref i), Culture);
                    break;

                case "--interval":
                    options.IntervalSeconds = int.Parse(RequireValue(args, ref i), Culture);
                    break;

                case "--cpu-threshold":
                    options.CpuThreshold = double.Parse(RequireValue(args, ref i), Culture);
                    break;

                case "--max-records":
                    options.MaxRecordsPerTick = int.Parse(RequireValue(args, ref i), Culture);
                    break;

                case "--catch-up-interval":
                    options.CatchUpIntervalSeconds = int.Parse(RequireValue(args, ref i), Culture);
                    break;

                case "--dry-run":
                    options.DryRun = true;
                    break;

                case "--compact":
                    options.Compact = true;
                    break;

                case "--as-of":
                    options.AsOfUtc = DateTime.Parse(
                        RequireValue(args, ref i),
                        Culture,
                        System.Globalization.DateTimeStyles.AdjustToUniversal
                        | System.Globalization.DateTimeStyles.AssumeUniversal);
                    break;

                case "--event-floor-kb":
                    options.EventFloorBytes = long.Parse(RequireValue(args, ref i), Culture) * 1024;
                    break;

                case "--no-fullscreen-detection":
                    options.NoFullscreenDetection = true;
                    break;

                case "--exclude-events":
                    options.ExtraEventExclusions.Add(RequireValue(args, ref i));
                    break;

                case "--include-strata-db":
                    options.IncludeStrataDatabase = true;
                    break;

                case "--rollup-log":
                    options.RollupLogPath = RequireValue(args, ref i);
                    break;

                case "--verbose":
                    options.Verbose = true;
                    break;

                case "--ignore-gate":
                    options.IgnoreGate = true;
                    break;

                case "--once":
                    options.RunOnce = true;
                    break;

                case "--quiet":
                    options.Quiet = true;
                    break;

                case "--path":
                    options.InstallPath = RequireValue(args, ref i);
                    break;

                case "--delay":
                    options.LogonDelaySeconds = int.Parse(RequireValue(args, ref i), Culture);
                    break;

                case "--no-task":
                    options.RegisterTask = false;
                    break;

                case "--no-start":
                    options.StartAfterInstall = false;
                    break;

                case "--purge-data":
                    options.PurgeData = true;
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
              strata usn-tail [<volume>]         print live change-journal records
              strata watch [<volume>...]         run the incremental scanner on a schedule
              strata prune                       thin recorded history to the retention tiers
              strata serve                       run the scanner and the localhost API together
              strata tray                        the same, from the notification area, no console
              strata enable-journal [<volume>]   create or grow a change journal (needs elevation)
                --journal-mb <n>   journal size in MB (default: 512)
              strata install                     copy this build somewhere stable, start it at logon
              strata uninstall                   undo that, keeping the database

            SCAN OPTIONS
              --db <path>          database file (default: %LOCALAPPDATA%\Strata\strata.db)
              --threads <n>        enumeration workers (default: min(8, cores/4))
              --exclude <name>     skip directories with this name, repeatable
              --no-dedup           do not deduplicate hardlinks, to measure what it costs
              --no-background-io   do not lower I/O priority, to measure what it costs
              --no-store           scan and report without writing to the database
              --histogram          report directory counts by depth and size floor
              --top <n>            list the n largest directories (default: 15)
              --no-compact         skip the post-scan VACUUM
              --quiet              suppress progress output

            PRUNE OPTIONS
              --dry-run            report what would be removed, remove nothing
              --as-of <iso-8601>   treat this as the present, to measure a pass without waiting
              --compact            VACUUM afterwards, handing freed pages back to Windows

            WATCH OPTIONS
              --interval <s>       seconds between ticks (default: 900)
              --cpu-threshold <n>  defer above this system CPU percent (default: 70)
              --max-records <n>    journal records per tick (default: 50000)
              --catch-up-interval <s>     seconds between ticks while behind (default: 60)
              --event-floor-kb <n> smallest change worth an event (default: 1024)
              --exclude-events <path>      keep a path out of the change log, repeatable
              --include-strata-db         do not exclude Strata's own database
              --once               run a single tick and exit
              --ignore-gate        run even when the resource guards would defer (diagnostic)
              --verbose            print a per-phase timing breakdown for each tick
              --no-fullscreen-detection   disable the borderless-fullscreen check
              --rollup-log <path>  append every directory whose total moved, per tick, as CSV

            SERVE OPTIONS
              --port <n>           API port on localhost (default: 5177)
              --verbose            log a line per HTTP request with its duration

            INSTALL OPTIONS
              --path <dir>         program directory (default: %LOCALAPPDATA%\Programs\Strata)
              --delay <s>          pause between logon and launch (default: 30, 0 for none)
              --no-task            copy the files without registering the logon task
              --no-start           do not start Strata once it is installed
              --purge-data         uninstall only: also delete %LOCALAPPDATA%\Strata and its history

            EXAMPLES
              strata scan D:
              strata scan --all --histogram
              strata scan C: --no-store --no-background-io --threads 8
              strata watch --once
              strata watch --interval 60
              strata serve --verbose
              strata install
              strata uninstall
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

    /// <summary>Journal size for <c>enable-journal</c>. Null means the measured default.</summary>
    internal long? JournalSizeBytes { get; set; }

    internal bool AllVolumes { get; set; }

    internal bool DeduplicateHardLinks { get; set; } = true;

    internal bool BackgroundIo { get; set; } = true;

    internal bool Persist { get; set; } = true;

    internal bool ShowHistogram { get; set; }

    internal int TopDirectories { get; set; } = 15;

    internal int TailSeconds { get; set; } = 10;

    internal int TailLimit { get; set; } = 40;

    internal bool FromStart { get; set; }

    internal int IntervalSeconds { get; set; } = 900;

    internal double CpuThreshold { get; set; } = 70;

    internal int Port { get; set; } = 5177;

    internal int MaxRecordsPerTick { get; set; } = 50_000;

    internal int CatchUpIntervalSeconds { get; set; } = 60;

    /// <summary>Report what retention would remove without removing it.</summary>
    internal bool DryRun { get; set; }

    /// <summary>VACUUM after pruning, so freed pages go back to Windows rather than to the free list.</summary>
    internal bool Compact { get; set; }

    /// <summary>
    /// What to treat as the present.
    /// </summary>
    /// <remarks>
    /// Exists so a retention pass can be measured against a real database today instead of in a
    /// week, when its rows would have aged into the tiers on their own. A diagnostic in the same
    /// spirit as <c>--no-store</c>: it changes what the run sees, never what it is allowed to do.
    /// </remarks>
    internal DateTime? AsOfUtc { get; set; }

    internal long EventFloorBytes { get; set; } = 1024 * 1024;

    internal bool NoFullscreenDetection { get; set; }

    internal bool Verbose { get; set; }

    /// <summary>Where to append the per-tick rollup fan-out. Diagnostic; off unless asked for.</summary>
    internal string? RollupLogPath { get; set; }

    internal List<string> ExtraEventExclusions { get; } = [];

    internal bool IncludeStrataDatabase { get; set; }

    /// <summary>
    /// The command line as a settings record, for the code paths that are shared with the API host.
    /// </summary>
    /// <remarks>
    /// Deliberately not merged with what is stored in the database. The console verbs exist to
    /// produce measurements, and a measurement that silently picked up whatever the UI last saved
    /// would not be reproducible.
    /// </remarks>
    internal StrataSettings ToSettings() => new()
    {
        IntervalSeconds = IntervalSeconds,
        CpuThresholdPercent = CpuThreshold,
        MaxRecordsPerTick = MaxRecordsPerTick,
        CatchUpIntervalSeconds = CatchUpIntervalSeconds,
        EventFloorBytes = EventFloorBytes,
        ScanWorkers = Threads,
        BackgroundIo = BackgroundIo,
        DetectBorderlessFullscreen = !NoFullscreenDetection,
        IncludeStrataDatabase = IncludeStrataDatabase,
        ExcludedEventPaths = ExtraEventExclusions,
        ExcludedDirectoryNames = Excluded,
    };

    internal bool IgnoreGate { get; set; }

    internal bool RunOnce { get; set; }

    internal bool SkipCompact { get; set; }

    internal bool Quiet { get; set; }

    /// <summary>Where <c>install</c> puts the program, and where <c>uninstall</c> looks for it.</summary>
    internal string? InstallPath { get; set; }

    internal int LogonDelaySeconds { get; set; } = LogonTask.DefaultDelaySeconds;

    internal bool RegisterTask { get; set; } = true;

    internal bool StartAfterInstall { get; set; } = true;

    /// <summary>Deletes the database on uninstall. Off, and only ever on because it was typed.</summary>
    internal bool PurgeData { get; set; }

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
