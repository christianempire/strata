using System.Security.Principal;

namespace Strata.Platform;

/// <summary>Whether this process is running with administrator rights.</summary>
/// <remarks>
/// Tier 2 needs an elevated volume handle unless the unprivileged journal read is usable, so this
/// decides which scan strategy a volume gets. Evaluated once: it cannot change within a process.
/// </remarks>
public static class Elevation
{
    public static bool IsElevated { get; } = Evaluate();

    private static bool Evaluate()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }
}
