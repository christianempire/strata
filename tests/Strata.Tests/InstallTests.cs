using System.Xml.Linq;
using Strata.Host.Setup;

namespace Strata.Tests;

/// <summary>
/// The parts of <c>strata install</c> that can be judged without changing the machine.
/// </summary>
/// <remarks>
/// Setup code is the least testable code in any product and the most expensive to get wrong: it
/// deletes directories and it registers something that runs at every logon. What is pinned here is
/// everything that decides *whether* to delete — the path arithmetic — and everything that decides
/// what the task will be, which is a string. Registering the task itself is verified against the
/// real Task Scheduler by hand, and the result is written up in docs/RUNTIME.md.
/// </remarks>
public sealed class InstallTests
{
    private const string Exe = @"C:\Users\someone\AppData\Local\Programs\Strata\strata.exe";

    // --- paths ------------------------------------------------------------------------------

    [Theory]
    [InlineData(@"C:\Programs\Strata\", @"C:\Programs\Strata")]
    [InlineData(@"C:\Programs\Strata", @"C:\Programs\Strata")]
    [InlineData(@"C:\", @"C:\")]
    public void NormaliseTrimsEverythingButARoot(string input, string expected)
        => Assert.Equal(expected, InstallLayout.Normalise(input));

    [Fact]
    public void SameDirectoryIgnoresCaseAndTrailingSeparators()
    {
        Assert.True(InstallLayout.SameDirectory(@"C:\Programs\Strata", @"c:\programs\strata\"));
        Assert.False(InstallLayout.SameDirectory(@"C:\Programs\Strata", @"C:\Programs\Strata2"));
    }

    /// <summary>
    /// The separator in the prefix test is the whole point: without it <c>C:\Program</c> would be
    /// judged to be inside <c>C:\Prog</c>, and both install and uninstall delete what they are
    /// given.
    /// </summary>
    [Fact]
    public void IsInsideDoesNotMatchOnASharedNamePrefix()
    {
        Assert.True(InstallLayout.IsInside(@"C:\Prog\Strata\icons", @"C:\Prog"));
        Assert.True(InstallLayout.IsInside(@"C:\Prog", @"C:\Prog"));
        Assert.False(InstallLayout.IsInside(@"C:\Program", @"C:\Prog"));
        Assert.False(InstallLayout.IsInside(@"C:\Prog", @"C:\Prog\Strata"));
    }

    /// <summary>
    /// The data directory is not under the program directory, and nothing in setup may make it so.
    /// An install that nested them would put a recursive delete one bug away from the history.
    /// </summary>
    [Fact]
    public void TheProgramDirectoryDoesNotContainTheDataDirectory()
    {
        Assert.False(InstallLayout.IsInside(InstallLayout.DataDirectory, InstallLayout.DefaultDirectory));
        Assert.False(InstallLayout.IsInside(InstallLayout.DefaultDirectory, InstallLayout.DataDirectory));
    }

    [Fact]
    public void OnlyAnEmptyDirectoryOrOneHoldingStrataMayBeReplaced()
    {
        var root = Path.Combine(Path.GetTempPath(), "strata-install-" + Guid.NewGuid().ToString("N"));

        try
        {
            Assert.True(InstallLayout.IsReplaceable(root), "absent");

            Directory.CreateDirectory(root);
            Assert.True(InstallLayout.IsReplaceable(root), "empty");

            File.WriteAllText(Path.Combine(root, "tax-return.docx"), "");
            Assert.False(InstallLayout.IsReplaceable(root), "somebody else's directory");

            File.WriteAllText(Path.Combine(root, InstallLayout.ExecutableName), "");
            Assert.True(InstallLayout.IsReplaceable(root), "ours");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void InspectReportsAMissingUiAndMissingIcons()
    {
        var root = Path.Combine(Path.GetTempPath(), "strata-install-" + Guid.NewGuid().ToString("N"));

        try
        {
            Directory.CreateDirectory(Path.Combine(root, "icons"));
            File.WriteAllText(Path.Combine(root, InstallLayout.ExecutableName), "");
            File.WriteAllText(Path.Combine(root, "icons", "strata-idle.ico"), "");

            var bare = InstallLayout.Inspect(root);

            Assert.True(bare.HasExecutable);
            Assert.False(bare.HasUserInterface);
            Assert.Equal(1, bare.IconCount);

            Directory.CreateDirectory(Path.Combine(root, "wwwroot"));
            File.WriteAllText(Path.Combine(root, "wwwroot", "index.html"), "");

            Assert.True(InstallLayout.Inspect(root).HasUserInterface);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    // --- the task definition ------------------------------------------------------------------

    [Fact]
    public void TheTaskAsksForNoPrivilegeItDoesNotNeed()
    {
        var task = Definition();

        // The hard constraint from docs/RUNTIME.md: no elevation, so no UAC prompt at logon.
        Assert.Equal("LeastPrivilege", Value(task, "RunLevel"));

        // An interactive token, because a notification icon needs a desktop to appear on. The
        // alternative — "run whether the user is logged on or not" — lands the process in a session
        // with no notification area at all.
        Assert.Equal("InteractiveToken", Value(task, "LogonType"));
    }

    [Fact]
    public void TheTaskRunsTheTrayVerbAtTheInstalledPath()
    {
        var task = Definition();

        Assert.Equal(Exe, Value(task, "Command"));
        Assert.Equal("tray", Value(task, "Arguments"));

        // Set even though nothing depends on it: every path the process resolves goes through
        // AppContext.BaseDirectory precisely because a task's working directory is not guaranteed.
        Assert.Equal(Path.GetDirectoryName(Exe), Value(task, "WorkingDirectory"));
    }

    [Fact]
    public void TheTaskSurvivesThreeDaysAndABattery()
    {
        var task = Definition();

        // The default execution time limit is three days, after which Task Scheduler stops the
        // process. A resident scanner has to opt out of that explicitly.
        Assert.Equal("PT0S", Value(task, "ExecutionTimeLimit"));

        // Both default to true, and both would stop a laptop from ever running Strata unplugged.
        Assert.Equal("false", Value(task, "DisallowStartIfOnBatteries"));
        Assert.Equal("false", Value(task, "StopIfGoingOnBatteries"));

        // A second copy started by hand must not be joined by one from the task.
        Assert.Equal("IgnoreNew", Value(task, "MultipleInstancesPolicy"));
    }

    /// <summary>
    /// Normal priority, not the Task Scheduler default of 7 (below normal). Strata depresses the
    /// priority of its scan threads rather than of its process — see docs/DESIGN-REVIEW.md — and
    /// the same process is serving the UI somebody is looking at.
    /// </summary>
    [Fact]
    public void TheTaskRunsAtNormalPriority()
        => Assert.Equal("6", Value(Definition(), "Priority"));

    [Fact]
    public void TheDelayIsWrittenAsADurationAndOmittedWhenZero()
    {
        Assert.Equal("PT30S", Value(Definition(delaySeconds: 30), "Delay"));
        Assert.Null(Value(Definition(delaySeconds: 0), "Delay"));
    }

    [Fact]
    public void TheTriggerAndThePrincipalNameTheSameUser()
    {
        var task = Definition();

        Assert.All(
            task.Descendants(task.Root!.Name.Namespace + "UserId"),
            element => Assert.Equal(@"PC\someone", element.Value));
    }

    /// <summary>
    /// A path can contain an ampersand — <c>C:\Users\Bill &amp; Ben\</c> is a legal profile — and an
    /// unescaped one turns a task definition into a parse error at the far end.
    /// </summary>
    [Fact]
    public void PathsAreEscapedIntoTheXml()
    {
        var path = @"C:\Users\Bill & Ben\<Programs>\strata.exe";

        var xml = LogonTask.BuildXml(path, "tray", @"PC\Bill & Ben", 30, DateTimeOffset.UnixEpoch);
        var task = XDocument.Parse(xml);

        Assert.Equal(path, Value(task, "Command"));
        Assert.DoesNotContain("Bill & Ben<", xml, StringComparison.Ordinal);
    }

    [Fact]
    public void ANegativeDelayIsRejectedRatherThanWritten()
        => Assert.Throws<ArgumentOutOfRangeException>(() => Definition(delaySeconds: -1));

    private static XDocument Definition(int delaySeconds = 30)
        => XDocument.Parse(LogonTask.BuildXml(Exe, "tray", @"PC\someone", delaySeconds, DateTimeOffset.UnixEpoch));

    private static string? Value(XDocument task, string name)
        => task.Descendants(task.Root!.Name.Namespace + name).FirstOrDefault()?.Value;
}
