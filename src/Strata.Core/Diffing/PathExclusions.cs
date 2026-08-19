using Strata.Core.Rollup;

namespace Strata.Core.Diffing;

/// <summary>
/// Removes changes under given paths from a delta set before it is attributed.
/// </summary>
/// <remarks>
/// Applied to the change log only, never to the rollup. An excluded directory still occupies real
/// disk and still belongs in volume totals and in the treemap. The exclusion says "stop telling me
/// about this", not "pretend this is not there" - a tool that quietly under-reported a volume would
/// be worse than one that was noisy.
/// </remarks>
public static class PathExclusions
{
    public static Dictionary<long, DirectoryDelta> Filter(
        IReadOnlyDictionary<long, DirectoryDelta> deltas,
        IReadOnlyDictionary<long, DirectoryNode> tree,
        string rootPrefix,
        IReadOnlyList<string> excluded)
    {
        ArgumentNullException.ThrowIfNull(deltas);
        ArgumentNullException.ThrowIfNull(tree);
        ArgumentNullException.ThrowIfNull(excluded);

        if (excluded.Count == 0)
        {
            return new Dictionary<long, DirectoryDelta>(deltas);
        }

        var kept = new Dictionary<long, DirectoryDelta>(deltas.Count);

        foreach (var (dirId, delta) in deltas)
        {
            // A normal tick changes dozens of directories, so building a path each is not worth
            // optimising away.
            if (!IsExcluded(PathBuilder.BuildPath(tree, dirId, rootPrefix), excluded))
            {
                kept[dirId] = delta;
            }
        }

        return kept;
    }

    public static bool IsExcluded(string path, IReadOnlyList<string> excluded)
    {
        ArgumentNullException.ThrowIfNull(excluded);

        for (var i = 0; i < excluded.Count; i++)
        {
            var prefix = excluded[i].TrimEnd('\\');

            if (prefix.Length == 0)
            {
                continue;
            }

            // The match has to end on a separator, or excluding "C:\Data" would also silence
            // "C:\DataOfSomethingElse".
            if (path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
                (path.Length == prefix.Length || path[prefix.Length] == '\\'))
            {
                return true;
            }
        }

        return false;
    }
}
