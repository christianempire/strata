using Strata.Storage;

namespace Strata.Host.Setup;

/// <summary>
/// Where an installed Strata lives, and what a directory has to contain to be one.
/// </summary>
/// <remarks>
/// Two directories, and keeping them apart is the point of this type. The **program** directory is
/// disposable: install replaces it wholesale and uninstall deletes it. The **data** directory is
/// not, and nothing in the setup path may write to it. They are deliberately not nested — an
/// install under <c>%LOCALAPPDATA%\Strata\</c> would put a recursive delete one bug away from
/// somebody's scan history.
/// </remarks>
internal static class InstallLayout
{
    internal const string ExecutableName = "strata.exe";

    /// <summary>
    /// The default program directory: <c>%LOCALAPPDATA%\Programs\Strata</c>.
    /// </summary>
    /// <remarks>
    /// Per-user and unelevated, which is the whole arrangement — <c>%ProgramFiles%</c> would need
    /// administrator to write and would make the logon task a machine-wide thing rather than this
    /// account's. <c>Programs</c> under LocalAppData is where per-user applications go on Windows.
    /// </remarks>
    internal static string DefaultDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Programs",
        "Strata");

    /// <summary>The directory the running executable was loaded from, normalised.</summary>
    internal static string RunningDirectory => Normalise(AppContext.BaseDirectory);

    /// <summary>Where the data lives, which install and uninstall must leave alone.</summary>
    internal static string DataDirectory => StrataDatabase.DefaultDirectory;

    /// <summary>
    /// Trailing separators removed and the path made absolute, so two spellings compare equal.
    /// </summary>
    internal static string Normalise(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var full = Path.GetFullPath(path);

        // A root ("C:\") is the one path whose trailing separator is load-bearing.
        return full.Length > 3 ? full.TrimEnd(Path.DirectorySeparatorChar) : full;
    }

    /// <summary>Whether two paths name the same directory, ignoring case and trailing separators.</summary>
    internal static bool SameDirectory(string left, string right)
        => string.Equals(Normalise(left), Normalise(right), StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether <paramref name="path"/> is <paramref name="directory"/> or sits under it.</summary>
    internal static bool IsInside(string path, string directory)
    {
        var candidate = Normalise(path);
        var parent = Normalise(directory);

        if (string.Equals(candidate, parent, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // The separator matters: without it, C:\Program would be judged to be inside C:\Prog.
        var prefix = parent.EndsWith(Path.DirectorySeparatorChar)
            ? parent
            : parent + Path.DirectorySeparatorChar;

        return candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Whether a directory holds a Strata install, and so may be replaced or deleted.
    /// </summary>
    /// <remarks>
    /// The guard on every destructive step in this file. <c>--path</c> takes an arbitrary directory
    /// from the command line and both install and uninstall delete what is there; an empty or
    /// absent directory is fair game, one holding <c>strata.exe</c> is ours, and anything else is
    /// somebody's documents folder and gets a refusal instead.
    /// </remarks>
    internal static bool IsReplaceable(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return true;
        }

        if (File.Exists(Path.Combine(directory, ExecutableName)))
        {
            return true;
        }

        return !Directory.EnumerateFileSystemEntries(directory).Any();
    }

    /// <summary>What a candidate source directory is missing, if anything.</summary>
    internal static SourceContents Inspect(string directory) => new(
        HasExecutable: File.Exists(Path.Combine(directory, ExecutableName)),
        HasUserInterface: File.Exists(Path.Combine(directory, "wwwroot", "index.html")),
        IconCount: Directory.Exists(Path.Combine(directory, "icons"))
            ? Directory.EnumerateFiles(Path.Combine(directory, "icons"), "*.ico").Count()
            : 0);
}

/// <summary>What was found in a directory about to be installed from.</summary>
/// <param name="HasExecutable">Fatal if false: there is nothing to install.</param>
/// <param name="HasUserInterface">
/// A built Angular app. Its absence is survivable — the API still runs — but the tray's
/// "Open Strata" would open a page apologising, so it is worth saying out loud.
/// </param>
/// <param name="IconCount">The tray's four state icons, read from <c>icons/</c> beside the exe.</param>
internal readonly record struct SourceContents(bool HasExecutable, bool HasUserInterface, int IconCount);
