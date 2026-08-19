using System.Diagnostics;

namespace Strata.Host.Setup;

/// <summary>How a removal ended.</summary>
internal enum RemovalOutcome
{
    /// <summary>There was nothing there.</summary>
    NotPresent,

    /// <summary>Gone.</summary>
    Removed,

    /// <summary>Handed to a helper, because the directory holds the executable running this.</summary>
    Deferred,
}

/// <summary>Copying a Strata into place, and taking it out again.</summary>
internal static class ProgramDirectory
{
    /// <summary>Total bytes and file count under a directory. Zero for one that is not there.</summary>
    internal static (long Bytes, int Files) Measure(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return (0, 0);
        }

        long bytes = 0;
        var files = 0;

        foreach (var path in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            bytes += new FileInfo(path).Length;
            files++;
        }

        return (bytes, files);
    }

    /// <summary>
    /// Replaces <paramref name="target"/> with a copy of <paramref name="source"/>.
    /// </summary>
    /// <remarks>
    /// Wholesale rather than over the top. A build's output is a set of files whose names carry
    /// content hashes — the Angular bundles especially — so copying over an older install leaves
    /// every superseded asset behind for ever, and each install would grow the directory. The
    /// caller has already checked that the target is ours to replace.
    /// </remarks>
    internal static (long Bytes, int Files) Replace(string source, string target)
    {
        if (Directory.Exists(target))
        {
            Directory.Delete(target, recursive: true);
        }

        return CopyTree(source, target);
    }

    /// <summary>
    /// Deletes an install.
    /// </summary>
    /// <remarks>
    /// The awkward case is uninstalling from the installed copy, which is the ordinary case: Windows
    /// will not delete a running executable, nor the runtime DLLs it has mapped, so the process
    /// cannot remove the directory it is running out of. The last step is handed to a short-lived
    /// <c>cmd</c> that waits for this process to be gone and then does it.
    /// <para>
    /// <c>ping</c> as the delay rather than <c>timeout</c>, which needs a console input handle and
    /// fails outright when there is not one.
    /// </para>
    /// </remarks>
    internal static RemovalOutcome Remove(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return RemovalOutcome.NotPresent;
        }

        if (!InstallLayout.IsInside(InstallLayout.RunningDirectory, directory))
        {
            Directory.Delete(directory, recursive: true);
            return RemovalOutcome.Removed;
        }

        var quoted = $"\"{directory}\"";

        var info = new ProcessStartInfo("cmd.exe")
        {
            Arguments = $"/c ping -n 4 127.0.0.1 >nul & rd /s /q {quoted} " +
                        $"& ping -n 4 127.0.0.1 >nul & rd /s /q {quoted}",
            UseShellExecute = false,
            CreateNoWindow = true,

            // Not inside the directory it is about to delete.
            WorkingDirectory = Path.GetTempPath(),
        };

        using var helper = Process.Start(info);

        return RemovalOutcome.Deferred;
    }

    private static (long Bytes, int Files) CopyTree(string source, string target)
    {
        Directory.CreateDirectory(target);

        long bytes = 0;
        var files = 0;

        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(target, Path.GetRelativePath(source, directory)));
        }

        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var destination = Path.Combine(target, Path.GetRelativePath(source, file));

            File.Copy(file, destination, overwrite: true);

            bytes += new FileInfo(destination).Length;
            files++;
        }

        return (bytes, files);
    }
}
