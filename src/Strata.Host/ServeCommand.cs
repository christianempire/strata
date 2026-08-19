using Strata.Host.Api;
using Strata.Platform;
using Strata.Storage;

namespace Strata.Host;

/// <summary>
/// <c>strata serve</c> — the shipped process: the scanner and the API in one executable.
/// </summary>
/// <remarks>
/// This is what a tray icon will start in Phase 4 and what a scheduled task will run at logon. It
/// differs from <c>strata watch</c> in two ways that matter: it also listens on localhost, and it
/// takes its configuration from the settings table rather than from the command line, so a change
/// made in the UI is picked up on the next tick.
/// <para>
/// <c>watch</c> stays as the diagnostic. Judging the resource behaviour needs every deferral and
/// tick duration printed as it happens, and it needs flags that are not settings.
/// </para>
/// </remarks>
internal static class ServeCommand
{
    internal static int Run(CliOptions options)
    {
        var database = new StrataDatabase(options.DatabasePath);
        database.Migrate();

        var app = ApiHost.Build(database, new ApiOptions
        {
            Port = options.Port,
            LogRequests = options.Verbose,
        });

        Console.WriteLine($"Strata API on http://localhost:{options.Port}/api  (loopback only)");
        Console.WriteLine($"Database: {database.DatabasePath}");
        Console.WriteLine($"Elevated: {(Elevation.IsElevated ? "yes" : "no")}");
        Console.WriteLine();
        Console.WriteLine("  GET  /api/volumes");
        Console.WriteLine("  GET  /api/snapshots?volume=&from=&to=&limit=");
        Console.WriteLine("  GET  /api/tree?volume=|snapshot=&path=&depth=&limit=");
        Console.WriteLine("  GET  /api/diff?from=&to=&path=&minBytes=&limit=");
        Console.WriteLine("  GET  /api/events?volume=&from=&to=&minBytes=&limit=");
        Console.WriteLine("  GET  /api/scan-log?volume=&from=&to=&result=&limit=");
        Console.WriteLine("  POST /api/scan            { \"volume\": \"C:\", \"tier\": \"incremental\" }");
        Console.WriteLine("  GET  /api/settings");
        Console.WriteLine("  PUT  /api/settings        any subset of the fields GET returns");
        Console.WriteLine();
        Console.WriteLine("Ctrl+C to stop.");
        Console.WriteLine();

        app.Run();

        return 0;
    }
}
