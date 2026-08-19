using System.Text;
using Strata.Core.Model;

namespace Strata.Core.Rollup;

/// <summary>
/// Reconstructs display paths from parent links.
/// </summary>
/// <remarks>
/// Paths are never stored per file, so anything that needs one rebuilds it by walking to the root.
/// That is cheap for the few thousand directories that end up in a snapshot and would be ruinous
/// for the millions of files that do not, which is the whole point of keying on parent IDs.
/// </remarks>
public static class PathBuilder
{
    /// <summary>
    /// Builds the path for a directory, prefixed with <paramref name="rootPrefix"/> (typically "D:").
    /// </summary>
    public static string BuildPath(IReadOnlyDictionary<long, DirectoryNode> nodes, long id, string rootPrefix)
    {
        ArgumentNullException.ThrowIfNull(nodes);

        if (!nodes.TryGetValue(id, out var node))
        {
            return rootPrefix;
        }

        // Collect leaf-to-root, then emit in reverse. Prepending into a StringBuilder would be
        // quadratic, and these are called for every directory in a snapshot.
        var segments = new List<string>(node.Depth + 1);
        var current = node;

        while (current.Depth > 0)
        {
            segments.Add(current.Name);

            if (!nodes.TryGetValue(current.ParentId, out var parent) || ReferenceEquals(parent, current))
            {
                break;
            }

            current = parent;
        }

        var builder = new StringBuilder(rootPrefix.Length + (segments.Count * 12));
        builder.Append(rootPrefix.TrimEnd('\\'));

        for (var i = segments.Count - 1; i >= 0; i--)
        {
            builder.Append('\\').Append(segments[i]);
        }

        return builder.ToString();
    }

    /// <summary>Builds the path of a file: its parent directory's path plus its name.</summary>
    public static string BuildFilePath(
        IReadOnlyDictionary<long, DirectoryNode> nodes,
        in FileRecord file,
        string rootPrefix)
        => BuildPath(nodes, file.ParentId, rootPrefix) + '\\' + file.Name;
}
