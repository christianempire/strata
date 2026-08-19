using System.ComponentModel;
using System.Diagnostics;

namespace Strata.Host.Setup;

/// <summary>Another Strata process, and where its executable was loaded from.</summary>
/// <param name="Id">The process id.</param>
/// <param name="ExecutablePath">Null when the process could not be opened to ask.</param>
internal readonly record struct RunningInstance(int Id, string? ExecutablePath)
{
    internal string Display => ExecutablePath is null
        ? $"pid {Id}"
        : $"pid {Id}  {ExecutablePath}";
}

/// <summary>
/// Finds and stops the other copies of Strata, so an install is not writing under one.
/// </summary>
/// <remarks>
/// Two separate reasons, and both have already bitten during development. The file lock: Windows
/// will not let anything overwrite a running executable or the DLLs it has mapped, so a copy over a
/// live install fails partway and leaves a directory that is half of each build. And the fight
/// described on <see cref="SingleInstance"/>: leaving the old copy running after installing the new
/// one gives the session two schedulers and one database.
/// </remarks>
internal static class RunningInstances
{
    /// <summary>Every <c>strata</c> process except this one.</summary>
    /// <remarks>
    /// By process name rather than by install path. A <c>strata scan</c> left running in a console
    /// somewhere holds the same database and is just as much of a problem as a stale tray, so the
    /// question worth asking is "is any Strata running", not "is the one I am about to replace".
    /// </remarks>
    internal static IReadOnlyList<RunningInstance> Find()
    {
        var self = Environment.ProcessId;
        var found = new List<RunningInstance>();

        foreach (var process in Process.GetProcessesByName("strata"))
        {
            using (process)
            {
                if (process.Id == self)
                {
                    continue;
                }

                found.Add(new RunningInstance(process.Id, TryGetPath(process)));
            }
        }

        return found;
    }

    /// <summary>
    /// Stops every other Strata: asked first, killed only if it will not go.
    /// </summary>
    /// <param name="grace">How long to wait for a clean exit before killing.</param>
    /// <param name="report">Called once per process with what happened to it.</param>
    /// <returns>False if something is still running when this returns.</returns>
    internal static bool StopAll(TimeSpan grace, Action<string> report)
    {
        ArgumentNullException.ThrowIfNull(report);

        var instances = Find();

        if (instances.Count == 0)
        {
            return true;
        }

        var live = new List<Process>();

        foreach (var instance in instances)
        {
            try
            {
                live.Add(Process.GetProcessById(instance.Id));
            }
            catch (ArgumentException)
            {
                // Gone between the enumeration and here. Nothing to stop.
            }
        }

        var deadline = DateTime.UtcNow + grace;

        while (DateTime.UtcNow < deadline && live.Any(p => !p.HasExited))
        {
            // One Set releases one listener, so this is re-signalled per pass rather than once.
            if (!SingleInstance.RequestShutdown())
            {
                // Nothing is listening: every remaining process either predates the shutdown event
                // or is a console verb. Waiting the full grace period would be waiting for nothing.
                break;
            }

            foreach (var process in live.Where(p => !p.HasExited))
            {
                process.WaitForExit(250);
            }
        }

        var stopped = true;

        foreach (var process in live)
        {
            using (process)
            {
                if (process.HasExited)
                {
                    report($"stopped  pid {process.Id} (asked)");
                    continue;
                }

                try
                {
                    process.Kill();
                    stopped &= process.WaitForExit(5_000);
                    report($"stopped  pid {process.Id} (killed — it did not answer)");
                }
                catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
                {
                    report($"FAILED   pid {process.Id}: {ex.Message}");
                    stopped = false;
                }
            }
        }

        return stopped;
    }

    private static string? TryGetPath(Process process)
    {
        try
        {
            return process.MainModule?.FileName;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            // An elevated process, or one exiting as we ask. The pid is enough to act on.
            return null;
        }
    }
}
