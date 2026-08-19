using Strata.Platform;

namespace Strata.Host;

internal static class Program
{
    private static int Main(string[] args)
    {
        // First statement in the process, before anything can write to the console and before the
        // rest of the CLI is even parsed. `tray` is the verb the logon task runs, and a console
        // process started without a console gets a window it never asked for; the sooner that is
        // hidden the less chance there is of it being painted. Started from a shell this does
        // nothing at all. See Strata.Platform.ConsoleWindow.
        if (args.Length > 0 && args[0].Equals("tray", StringComparison.OrdinalIgnoreCase))
        {
            ConsoleWindow.DetachIfUnwanted();
        }

        return Cli.Run(args);
    }
}
