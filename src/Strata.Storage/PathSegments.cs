namespace Strata.Storage;

/// <summary>Path arithmetic shared by the tree and diff reads.</summary>
/// <remarks>
/// Ordinary string work rather than <c>System.IO.Path</c>: these are paths recorded in a database,
/// possibly from a volume that is no longer attached, and nothing here should touch the filesystem
/// or resolve anything against the current working directory.
/// </remarks>
public static class PathSegments
{
    /// <summary>
    /// Splits a caller-supplied path into the segments below the volume root.
    /// </summary>
    /// <remarks>
    /// Accepts what a person would type or paste: <c>C:\Users\chris</c>, <c>\Users\chris</c>,
    /// <c>Users/chris</c>, or the volume GUID form. A leading drive letter is dropped rather than
    /// checked, because the volume is already fixed by the request; treating it as a directory name
    /// would be a way to ask for a path on one volume against the tree of another.
    /// </remarks>
    public static List<string> Split(string? path)
    {
        var segments = new List<string>();

        if (string.IsNullOrWhiteSpace(path))
        {
            return segments;
        }

        var text = path.Trim().Replace('/', '\\');

        if (text.StartsWith(@"\\?\", StringComparison.Ordinal))
        {
            text = text[4..];
        }

        if (text.StartsWith("Volume{", StringComparison.OrdinalIgnoreCase))
        {
            var end = text.IndexOf('\\', StringComparison.Ordinal);
            text = end < 0 ? "" : text[(end + 1)..];
        }

        foreach (var part in text.Split('\\', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segments.Count == 0 && part.Length == 2 && part[1] == ':')
            {
                continue;
            }

            segments.Add(part);
        }

        return segments;
    }

    /// <summary>How many levels <paramref name="path"/> sits below <paramref name="root"/>.</summary>
    public static int DepthBelow(string root, string path)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(path);

        if (path.Length <= root.Length)
        {
            return 0;
        }

        var depth = 0;

        for (var i = root.Length; i < path.Length; i++)
        {
            if (path[i] == '\\')
            {
                depth++;
            }
        }

        return depth;
    }

    /// <summary>The containing directory's path, or null once there is nothing above it.</summary>
    public static string? Parent(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        var slash = path.LastIndexOf('\\');
        return slash <= 0 ? null : path[..slash];
    }

    /// <summary>The last segment. For a volume root that is the whole thing, "C:".</summary>
    public static string Leaf(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        var slash = path.LastIndexOf('\\');
        return slash < 0 ? path : path[(slash + 1)..];
    }

    /// <summary>Whether <paramref name="path"/> is <paramref name="root"/> or sits beneath it.</summary>
    public static bool IsWithin(string root, string path)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(path);

        if (path.Length == root.Length)
        {
            return string.Equals(path, root, StringComparison.OrdinalIgnoreCase);
        }

        // The separator check is what stops "C:\Program Files" from swallowing "C:\Program Files 2".
        return path.Length > root.Length
            && path[root.Length] == '\\'
            && path.AsSpan(0, root.Length).Equals(root, StringComparison.OrdinalIgnoreCase);
    }
}
