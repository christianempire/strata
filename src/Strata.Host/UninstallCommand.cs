using Strata.Core.Formatting;
using Strata.Host.Setup;

namespace Strata.Host;

/// <summary>
/// <c>strata uninstall</c> — stops Strata, removes the logon task, removes the program directory.
/// </summary>
/// <remarks>
/// **It does not touch the database.** Strata's second non-negotiable is that it never deletes
/// anything, and a tool whose entire value is months of accumulated history throwing that away as a
/// side effect of being uninstalled would be the worst thing it could possibly do. The data
/// directory survives, is named on the way out, and is only removed when <c>--purge-data</c> asks
/// for it in as many words.
/// </remarks>
internal static class UninstallCommand
{
    internal static int Run(CliOptions options)
    {
        var target = InstallLayout.Normalise(options.InstallPath ?? Locate());

        Console.WriteLine($"Program:   {target}");
        Console.WriteLine($"Data:      {InstallLayout.DataDirectory}");
        Console.WriteLine();

        // The task first. If anything below fails, what is left behind is a directory rather than a
        // scheduled task that starts a Strata which may no longer be there.
        if (LogonTask.Delete())
        {
            Console.WriteLine($"task     '{LogonTask.Name}' removed");
        }
        else
        {
            Console.WriteLine($"task     '{LogonTask.Name}' was not registered");
        }

        if (!RunningInstances.StopAll(TimeSpan.FromSeconds(15), line => Console.WriteLine(line)))
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine("strata: a copy is still running, so its files cannot be removed.");
            return 1;
        }

        if (!InstallLayout.IsReplaceable(target))
        {
            Console.Error.WriteLine(
                $"strata: {target} holds no {InstallLayout.ExecutableName} and is not empty. " +
                "Refusing to delete it.");

            return 1;
        }

        var size = ProgramDirectory.Measure(target);

        switch (ProgramDirectory.Remove(target))
        {
            case RemovalOutcome.NotPresent:
                Console.WriteLine($"program  {target} was not there");
                break;

            case RemovalOutcome.Removed:
                Console.WriteLine($"program  removed, {ByteSize.Format(size.Bytes)} in {size.Files:N0} files");
                break;

            case RemovalOutcome.Deferred:
                Console.WriteLine(
                    $"program  {ByteSize.Format(size.Bytes)} in {size.Files:N0} files — removed a few " +
                    "seconds after this process exits");
                Console.WriteLine("         (uninstall is running out of the directory it is deleting)");
                break;
        }

        Console.WriteLine();

        return options.PurgeData ? PurgeData() : KeepData();
    }

    private static int KeepData()
    {
        var size = ProgramDirectory.Measure(InstallLayout.DataDirectory);

        Console.WriteLine($"data     KEPT — {InstallLayout.DataDirectory}, {ByteSize.Format(size.Bytes)}");
        Console.WriteLine("         Your scan history is still there. Installing again picks it straight up.");
        Console.WriteLine("         Remove it with:  strata uninstall --purge-data");

        return 0;
    }

    private static int PurgeData()
    {
        var directory = InstallLayout.DataDirectory;
        var size = ProgramDirectory.Measure(directory);

        if (!Directory.Exists(directory))
        {
            Console.WriteLine($"data     nothing at {directory}");
            return 0;
        }

        Directory.Delete(directory, recursive: true);

        Console.WriteLine(
            $"data     DELETED — {directory}, {ByteSize.Format(size.Bytes)} in {size.Files:N0} files");
        Console.WriteLine("         Every snapshot, event and setting is gone. This was --purge-data.");

        return 0;
    }

    /// <summary>
    /// Where the install is, when nobody said.
    /// </summary>
    /// <remarks>
    /// The registered task knows, so ask it: an install that went somewhere non-default with
    /// <c>--path</c> is then undone by a bare <c>strata uninstall</c> rather than needing the same
    /// path typed correctly a second time.
    /// </remarks>
    private static string Locate()
    {
        var command = LogonTask.RegisteredCommand();

        if (command is not null && Path.GetDirectoryName(command) is { Length: > 0 } directory)
        {
            return directory;
        }

        return InstallLayout.DefaultDirectory;
    }
}
