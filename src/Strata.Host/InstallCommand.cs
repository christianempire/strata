using Strata.Core.Formatting;
using Strata.Host.Setup;

namespace Strata.Host;

/// <summary>
/// <c>strata install</c> — copies this build somewhere stable and starts it at logon.
/// </summary>
/// <remarks>
/// A setup verb in the shape of <c>strata enable-journal</c>: a one-off that makes a persistent
/// change to the machine, run deliberately, entirely separate from the resident process. It is not
/// an installer — <c>docs/ARCHITECTURE.md</c> still lists one among the non-goals, and this is
/// twenty files copied and one scheduled task registered, with no MSI, no registry, no shortcut and
/// no elevation.
/// <para>
/// It exists because a build output is not a home. Running Strata out of
/// <c>bin\Release\net9.0-windows\</c> means <c>dotnet clean</c>, a branch switch or moving the
/// repository silently breaks whatever points at it, and a rebuild fails on the file lock of the
/// copy that is still running. Both have happened.
/// </para>
/// <para>
/// What it copies is the directory it is running from, whatever that is. That keeps the verb honest
/// about what it can promise — it installs exactly the build you invoked, so publishing
/// self-contained or framework-dependent is a decision made by the publish, not re-litigated here.
/// </para>
/// </remarks>
internal static class InstallCommand
{
    internal static int Run(CliOptions options)
    {
        var source = InstallLayout.RunningDirectory;
        var target = InstallLayout.Normalise(options.InstallPath ?? InstallLayout.DefaultDirectory);
        var executable = Path.Combine(target, InstallLayout.ExecutableName);

        Console.WriteLine($"Source:    {source}");
        Console.WriteLine($"Program:   {target}");
        Console.WriteLine($"Data:      {InstallLayout.DataDirectory}   (untouched by install and uninstall)");
        Console.WriteLine();

        if (InstallLayout.SameDirectory(source, target))
        {
            Console.Error.WriteLine(
                "strata: this build is already running from the install directory, so there is " +
                "nothing to copy.\n" +
                "        Run install from a publish output, or pass --path for a different one.");

            return 1;
        }

        if (InstallLayout.IsInside(InstallLayout.DataDirectory, target)
            || InstallLayout.IsInside(target, InstallLayout.DataDirectory))
        {
            Console.Error.WriteLine(
                $"strata: refusing to install into {target}, which overlaps the data directory " +
                $"{InstallLayout.DataDirectory}.\n" +
                "        Install replaces the program directory wholesale; the database must not be in it.");

            return 1;
        }

        var contents = InstallLayout.Inspect(source);

        if (!contents.HasExecutable)
        {
            Console.Error.WriteLine($"strata: no {InstallLayout.ExecutableName} in {source}.");
            return 1;
        }

        if (!InstallLayout.IsReplaceable(target))
        {
            Console.Error.WriteLine(
                $"strata: {target} already exists, is not empty, and holds no " +
                $"{InstallLayout.ExecutableName}.\n" +
                "        Refusing to delete it. Pass --path to install somewhere else.");

            return 1;
        }

        Warn(contents);

        // Before the copy, not after: Windows will not overwrite a running executable or the
        // runtime DLLs it has mapped, and a half-copied install is worse than a refused one.
        if (!StopRunning())
        {
            return 1;
        }

        var copied = ProgramDirectory.Replace(source, target);

        Console.WriteLine(
            $"copied   {copied.Files:N0} files, {ByteSize.Format(copied.Bytes)} to {target}");

        if (options.RegisterTask)
        {
            var xml = LogonTask.BuildXml(
                executable,
                "tray",
                CurrentUserId(),
                options.LogonDelaySeconds,
                DateTimeOffset.UtcNow);

            LogonTask.Register(xml);

            Console.WriteLine(
                $"task     '{LogonTask.Name}' registered — at logon" +
                (options.LogonDelaySeconds > 0 ? $" + {options.LogonDelaySeconds}s" : string.Empty) +
                ", unelevated, no window");
        }
        else
        {
            Console.WriteLine("task     skipped (--no-task)");
        }

        if (options.StartAfterInstall)
        {
            if (options.RegisterTask)
            {
                // Started through the task rather than directly, so that what runs now is exactly
                // what will run at logon — same principal, same token, same working directory.
                LogonTask.Run();
                Console.WriteLine("started  through the task, as Windows will at logon");
            }
            else
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(executable, "tray")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = target,
                });

                Console.WriteLine("started  directly (no task was registered)");
            }
        }

        Console.WriteLine();
        Console.WriteLine("Strata is in the notification area. Windows 11 hides new icons in the");
        Console.WriteLine("overflow: click the ^ and drag Strata onto the taskbar to keep it there.");
        Console.WriteLine();
        Console.WriteLine($"Undo all of this with:  \"{executable}\" uninstall");

        return 0;
    }

    /// <summary>
    /// Says what the source build is missing without refusing to install it.
    /// </summary>
    /// <remarks>
    /// Neither absence is fatal — the API runs without a UI and the tray runs without artwork — but
    /// both are invisible until somebody clicks the icon and finds a plain-text apology, so they are
    /// worth a line each with the command that fixes them.
    /// </remarks>
    private static void Warn(SourceContents contents)
    {
        if (!contents.HasUserInterface)
        {
            Console.WriteLine("WARNING  no wwwroot in the source: this install has the API but no UI.");
            Console.WriteLine("         Build it with 'npm run build' in src/Strata.Web, or");
            Console.WriteLine("         'dotnet publish src/Strata.Host -p:BuildWebUi=true', then install again.");
            Console.WriteLine();
        }

        if (contents.IconCount < 4)
        {
            Console.WriteLine(
                $"WARNING  {contents.IconCount} of the tray's 4 state icons are in the source's icons/.");
            Console.WriteLine("         The icon will be blank for the states that are missing.");
            Console.WriteLine();
        }
    }

    private static bool StopRunning()
    {
        var running = RunningInstances.Find();

        if (running.Count == 0)
        {
            return true;
        }

        foreach (var instance in running)
        {
            Console.WriteLine($"running  {instance.Display}");
        }

        if (RunningInstances.StopAll(TimeSpan.FromSeconds(15), line => Console.WriteLine(line)))
        {
            return true;
        }

        Console.Error.WriteLine();
        Console.Error.WriteLine(
            "strata: could not stop every running copy, and copying over a running executable " +
            "would fail partway.\n" +
            "        Exit Strata from its tray menu and run install again.");

        return false;
    }

    /// <summary>
    /// The account the task runs as, in the form Task Scheduler wants.
    /// </summary>
    /// <remarks>
    /// <c>MACHINE\user</c> rather than a SID: a SID is equally valid and completely unreadable in
    /// the Task Scheduler window, which is where somebody will go to check what this verb did.
    /// </remarks>
    private static string CurrentUserId()
        => $"{Environment.UserDomainName}\\{Environment.UserName}";
}
