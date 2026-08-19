using System.Diagnostics;
using System.Globalization;
using System.Security;
using System.Text;
using System.Xml.Linq;

namespace Strata.Host.Setup;

/// <summary>
/// The scheduled task that starts Strata at logon.
/// </summary>
/// <remarks>
/// A per-user task rather than a <c>Run</c> key entry or a Startup-folder shortcut. All three are
/// unelevated — the constraint from <c>docs/RUNTIME.md</c> is satisfied by any of them, because the
/// executable's manifest is <c>asInvoker</c> and nothing about the destination changes that — but
/// only the task can express the two things this particular process wants: a delay after logon, so
/// the first tick is not competing with everything else Windows starts at once, and an unlimited
/// execution time, so a resident process is not stopped after the default three days.
/// <para>
/// Driven through <c>schtasks.exe</c> with an XML definition rather than through the Task Scheduler
/// COM API. The COM route would be a hundred lines of interop for a one-off setup verb that runs
/// once and is measured in seconds; <c>schtasks</c> is a documented, stable interface to the same
/// service, and its exit code says whether the registration took.
/// </para>
/// </remarks>
internal static class LogonTask
{
    internal const string Name = "Strata";

    /// <summary>The default pause between logon and launch.</summary>
    /// <remarks>
    /// "At log on" fires while the shell is still starting, the notification area among it. Thirty
    /// seconds costs nothing for a process that then sits still for fifteen minutes, and it keeps
    /// Strata out of the part of logon the user is actually watching.
    /// </remarks>
    internal const int DefaultDelaySeconds = 30;

    /// <summary>
    /// The task definition, as Task Scheduler's own XML.
    /// </summary>
    /// <remarks>
    /// Pure string building, so the interesting decisions in here — least privilege, an interactive
    /// token, no execution time limit — are assertable in a test rather than only observable by
    /// registering a task on somebody's machine.
    /// </remarks>
    internal static string BuildXml(
        string executablePath,
        string arguments,
        string userId,
        int delaySeconds,
        DateTimeOffset registered)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        ArgumentOutOfRangeException.ThrowIfNegative(delaySeconds);

        var workingDirectory = Path.GetDirectoryName(Path.GetFullPath(executablePath))
                               ?? AppContext.BaseDirectory;

        var delay = delaySeconds > 0
            ? $"\n      <Delay>PT{delaySeconds.ToString(CultureInfo.InvariantCulture)}S</Delay>"
            : string.Empty;

        return $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.4" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo>
                <Date>{registered.UtcDateTime.ToString("s", CultureInfo.InvariantCulture)}</Date>
                <Author>{Escape(userId)}</Author>
                <Description>Strata — disk usage with history. Runs in the notification area and scans while the machine is idle.</Description>
                <URI>\{Name}</URI>
              </RegistrationInfo>
              <Triggers>
                <LogonTrigger>
                  <Enabled>true</Enabled>
                  <UserId>{Escape(userId)}</UserId>{delay}
                </LogonTrigger>
              </Triggers>
              <Principals>
                <Principal id="Author">
                  <UserId>{Escape(userId)}</UserId>
                  <LogonType>InteractiveToken</LogonType>
                  <RunLevel>LeastPrivilege</RunLevel>
                </Principal>
              </Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <AllowHardTerminate>true</AllowHardTerminate>
                <StartWhenAvailable>false</StartWhenAvailable>
                <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
                <IdleSettings>
                  <StopOnIdleEnd>false</StopOnIdleEnd>
                  <RestartOnIdle>false</RestartOnIdle>
                </IdleSettings>
                <AllowStartOnDemand>true</AllowStartOnDemand>
                <Enabled>true</Enabled>
                <Hidden>false</Hidden>
                <RunOnlyIfIdle>false</RunOnlyIfIdle>
                <DisallowStartOnRemoteAppSession>false</DisallowStartOnRemoteAppSession>
                <UseUnifiedSchedulingEngine>true</UseUnifiedSchedulingEngine>
                <WakeToRun>false</WakeToRun>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <Priority>6</Priority>
              </Settings>
              <Actions Context="Author">
                <Exec>
                  <Command>{Escape(executablePath)}</Command>
                  <Arguments>{Escape(arguments)}</Arguments>
                  <WorkingDirectory>{Escape(workingDirectory)}</WorkingDirectory>
                </Exec>
              </Actions>
            </Task>
            """;
    }

    /// <summary>Registers, or replaces, the task. Throws with schtasks' own words if it refuses.</summary>
    internal static void Register(string xml)
    {
        // A temp file rather than stdin: schtasks takes the definition only as a path. UTF-16 with
        // a byte-order mark, which is what /XML expects and what Task Scheduler's own exports are.
        var file = Path.Combine(Path.GetTempPath(), $"strata-task-{Environment.ProcessId}.xml");

        try
        {
            File.WriteAllText(file, xml, new UnicodeEncoding(bigEndian: false, byteOrderMark: true));

            var result = Schtasks($"/create /tn {Name} /xml \"{file}\" /f");

            if (result.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"schtasks refused to register the task (exit {result.ExitCode}): {result.Message}");
            }
        }
        finally
        {
            TryDelete(file);
        }
    }

    /// <summary>Whether the task is registered.</summary>
    internal static bool Exists() => Schtasks($"/query /tn {Name}").ExitCode == 0;

    /// <summary>Removes the task. False if it was not there to remove.</summary>
    internal static bool Delete()
    {
        if (!Exists())
        {
            return false;
        }

        var result = Schtasks($"/delete /tn {Name} /f");

        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"schtasks refused to delete the task (exit {result.ExitCode}): {result.Message}");
        }

        return true;
    }

    /// <summary>Starts the task now, as Windows would at logon.</summary>
    internal static void Run()
    {
        var result = Schtasks($"/run /tn {Name}");

        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"schtasks could not start the task (exit {result.ExitCode}): {result.Message}");
        }
    }

    /// <summary>
    /// The executable path the registered task will run, read back from Task Scheduler.
    /// </summary>
    /// <remarks>
    /// So that uninstall can find an install that went somewhere other than the default without
    /// being told where. Null on any difficulty — an absent task, a path schtasks rendered in a
    /// code page that did not survive the pipe — and the caller falls back to the default rather
    /// than acting on a half-read path.
    /// </remarks>
    internal static string? RegisteredCommand()
    {
        var result = Schtasks($"/query /tn {Name} /xml ONE");

        if (result.ExitCode != 0)
        {
            return null;
        }

        try
        {
            var xml = result.Output.TrimStart('﻿', '​').Trim();
            var document = XDocument.Parse(xml);
            var ns = document.Root?.Name.Namespace ?? XNamespace.None;
            var command = document.Descendants(ns + "Command").FirstOrDefault()?.Value;

            return string.IsNullOrWhiteSpace(command) ? null : command.Trim();
        }
        catch (System.Xml.XmlException)
        {
            return null;
        }
    }

    private static (int ExitCode, string Output, string Message) Schtasks(string arguments)
    {
        var info = new ProcessStartInfo("schtasks.exe", arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,

            // schtasks writes its XML in the console output code page. Nothing here contains
            // anything outside ASCII on a default install, and RegisteredCommand treats a failure
            // to parse as "ask somewhere else" rather than as an error.
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        using var process = Process.Start(info)
            ?? throw new InvalidOperationException("Could not start schtasks.exe.");

        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();

        process.WaitForExit();

        var message = string.IsNullOrWhiteSpace(error) ? output : error;

        return (process.ExitCode, output, message.Replace("\r\n", " ").Trim());
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A temp file left behind is not worth failing an install over.
        }
    }

    private static string Escape(string value) => SecurityElement.Escape(value) ?? string.Empty;
}
