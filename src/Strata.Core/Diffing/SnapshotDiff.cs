using Strata.Core.Rollup;

namespace Strata.Core.Diffing;

/// <summary>One directory as a snapshot recorded it: a materialised path and its subtree totals.</summary>
/// <remarks>
/// This is the shape of a <c>snapshot_dirs</c> row. History stores the path rather than a file ID
/// because file IDs churn and a directory that no longer exists still has to be nameable.
/// </remarks>
public readonly record struct SnapshotDirectory(
    string Path,
    int Depth,
    long SubtreeBytes,
    long SubtreeFiles);

/// <summary>
/// Compares two snapshots' directory rollups and hands the result to the diff engine.
/// </summary>
/// <remarks>
/// The engine wants a directory tree plus a per-directory <em>own</em> delta, and a snapshot gives
/// neither directly: it gives paths and subtree totals. So this reconstructs the tree from the union
/// of both sides' paths, and recovers own bytes as a node's subtree total minus the subtree totals
/// of the children the snapshot actually recorded.
/// <para>
/// That subtraction is exact rather than approximate, and it is also where the size floor becomes
/// visible: a directory whose children all fell below the floor keeps their bytes in its own total,
/// which is the correct place for them to be attributed given what was recorded. Rollups still sum
/// to the volume total.
/// </para>
/// <para>
/// Pure, like the rest of <c>Strata.Core</c>: rows in, changes out.
/// </para>
/// </remarks>
public static class SnapshotDiff
{
    /// <summary>
    /// Attributes the difference between two snapshot rollups to the directories that explain it.
    /// </summary>
    /// <param name="from">The earlier snapshot's directory rows.</param>
    /// <param name="to">The later snapshot's directory rows.</param>
    /// <param name="path">Scope the comparison to this subtree. Null compares from the volume root.</param>
    /// <param name="options">Attribution tunables, or the defaults.</param>
    public static IReadOnlyList<AttributedChange> Compare(
        IReadOnlyList<SnapshotDirectory> from,
        IReadOnlyList<SnapshotDirectory> to,
        string? path = null,
        DiffOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(to);

        options ??= new DiffOptions();

        var rootPath = ChooseRoot(from, to, path);

        if (rootPath is null)
        {
            return [];
        }

        var tree = BuildTree(from, to, rootPath);

        if (tree.Nodes.Count == 0)
        {
            return [];
        }

        var deltas = new Dictionary<long, DirectoryDelta>(tree.Nodes.Count);

        foreach (var id in tree.Nodes.Keys)
        {
            var beforeBytes = tree.OwnBytesBefore.GetValueOrDefault(id);
            var afterBytes = tree.OwnBytesAfter.GetValueOrDefault(id);
            var beforeFiles = tree.OwnFilesBefore.GetValueOrDefault(id);
            var afterFiles = tree.OwnFilesAfter.GetValueOrDefault(id);

            var bytesDelta = afterBytes - beforeBytes;
            var filesDelta = afterFiles - beforeFiles;

            if (bytesDelta == 0 && filesDelta == 0)
            {
                continue;
            }

            deltas[id] = new DirectoryDelta(
                bytesDelta,
                // The smallest churn consistent with what the two snapshots show, not a measurement.
                // Between two points in time a directory that wrote and deleted the same 50 GB is
                // indistinguishable from one that did nothing, so the true figure is unknowable here
                // and only the Timeline, which reads the journal, reports it honestly.
                //
                // A lower bound rather than zero because it is load-bearing: the engine will not
                // descend into a node whose net change and churn are both under the floor, and a
                // directory whose growth in one branch exactly cancels deletion in another has a net
                // of zero. Reporting nothing at all there would hide both halves.
                OwnChurnBytes: Math.Abs(bytesDelta),
                FilesAdded: filesDelta > 0 ? (int)filesDelta : 0,
                FilesRemoved: filesDelta < 0 ? (int)-filesDelta : 0);
        }

        if (deltas.Count == 0)
        {
            return [];
        }

        var changes = DiffEngine.Attribute(tree.Nodes, tree.RootId, deltas, rootPath, options);

        return Reclassify(changes, tree);
    }

    /// <summary>
    /// Replaces the engine's guess at a change's kind with what the two snapshots actually show.
    /// </summary>
    /// <remarks>
    /// The engine infers appeared and vanished from added and removed file counts, which is right
    /// when it is fed a change journal and wrong when it is fed two snapshots — there, a directory
    /// that merely shrank and a directory that ceased to exist produce the same delta. Presence in
    /// each side is recorded, so it is answered rather than inferred.
    /// </remarks>
    private static List<AttributedChange> Reclassify(
        IReadOnlyList<AttributedChange> changes,
        Tree tree)
    {
        var result = new List<AttributedChange>(changes.Count);

        foreach (var change in changes)
        {
            var inBefore = tree.PresentBefore.Contains(change.Path);
            var inAfter = tree.PresentAfter.Contains(change.Path);

            var kind = (inBefore, inAfter) switch
            {
                (false, true) => ChangeKind.Appeared,
                (true, false) => ChangeKind.Vanished,
                _ => change.DeltaBytes < 0 ? ChangeKind.Shrank : ChangeKind.Grew,
            };

            result.Add(change with { Kind = kind });
        }

        return result;
    }

    /// <summary>The path both sides are compared under.</summary>
    private static string? ChooseRoot(
        IReadOnlyList<SnapshotDirectory> from,
        IReadOnlyList<SnapshotDirectory> to,
        string? path)
    {
        if (!string.IsNullOrWhiteSpace(path))
        {
            return path.Trim().TrimEnd('\\');
        }

        string? shallowest = null;
        var bestDepth = int.MaxValue;

        foreach (var row in from)
        {
            if (row.Depth < bestDepth)
            {
                bestDepth = row.Depth;
                shallowest = row.Path;
            }
        }

        foreach (var row in to)
        {
            if (row.Depth < bestDepth)
            {
                bestDepth = row.Depth;
                shallowest = row.Path;
            }
        }

        return shallowest;
    }

    private static Tree BuildTree(
        IReadOnlyList<SnapshotDirectory> from,
        IReadOnlyList<SnapshotDirectory> to,
        string rootPath)
    {
        // Every path either side recorded within the scope, deduplicated case-insensitively because
        // NTFS is.
        var idByPath = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase)
        {
            [rootPath] = 1,
        };

        var next = 2L;

        foreach (var row in from)
        {
            next = Register(idByPath, rootPath, row.Path, next);
        }

        foreach (var row in to)
        {
            next = Register(idByPath, rootPath, row.Path, next);
        }

        var nodes = new Dictionary<long, DirectoryNode>(idByPath.Count);

        foreach (var (nodePath, id) in idByPath)
        {
            var parentId = id == 1 ? 1 : NearestAncestor(idByPath, nodePath);

            nodes[id] = new DirectoryNode(id, parentId, PathLeaf(nodePath))
            {
                Depth = DepthBelow(rootPath, nodePath),
            };
        }

        var tree = new Tree(nodes, RootId: 1);

        Accumulate(from, idByPath, tree.OwnBytesBefore, tree.OwnFilesBefore, tree.PresentBefore);
        Accumulate(to, idByPath, tree.OwnBytesAfter, tree.OwnFilesAfter, tree.PresentAfter);

        return tree;
    }

    private static long Register(
        Dictionary<string, long> idByPath,
        string rootPath,
        string path,
        long next)
    {
        if (!IsWithin(rootPath, path) || idByPath.ContainsKey(path))
        {
            return next;
        }

        idByPath[path] = next;
        return next + 1;
    }

    /// <summary>
    /// Turns subtree totals into own totals by subtracting the children the snapshot recorded.
    /// </summary>
    /// <remarks>
    /// The diff engine takes own-deltas and rolls them back up itself. Feeding it subtree totals
    /// instead would count every byte once per level of ancestry.
    /// </remarks>
    private static void Accumulate(
        IReadOnlyList<SnapshotDirectory> rows,
        Dictionary<string, long> idByPath,
        Dictionary<long, long> ownBytes,
        Dictionary<long, long> ownFiles,
        HashSet<string> present)
    {
        foreach (var row in rows)
        {
            if (!idByPath.TryGetValue(row.Path, out var id))
            {
                continue;
            }

            present.Add(row.Path);
            ownBytes[id] = ownBytes.GetValueOrDefault(id) + row.SubtreeBytes;
            ownFiles[id] = ownFiles.GetValueOrDefault(id) + row.SubtreeFiles;
        }

        foreach (var row in rows)
        {
            if (!idByPath.ContainsKey(row.Path))
            {
                continue;
            }

            var parentPath = NearestAncestorPath(idByPath, row.Path);

            if (parentPath is null || !idByPath.TryGetValue(parentPath, out var parentId))
            {
                continue;
            }

            ownBytes[parentId] = ownBytes.GetValueOrDefault(parentId) - row.SubtreeBytes;
            ownFiles[parentId] = ownFiles.GetValueOrDefault(parentId) - row.SubtreeFiles;
        }
    }

    private static long NearestAncestor(Dictionary<string, long> idByPath, string path)
        => NearestAncestorPath(idByPath, path) is { } parent && idByPath.TryGetValue(parent, out var id)
            ? id
            : 1;

    private static string? NearestAncestorPath(Dictionary<string, long> idByPath, string path)
    {
        var current = ParentPath(path);

        while (current is not null)
        {
            if (idByPath.ContainsKey(current))
            {
                return current;
            }

            current = ParentPath(current);
        }

        return null;
    }

    private static string? ParentPath(string path)
    {
        var slash = path.LastIndexOf('\\');
        return slash <= 0 ? null : path[..slash];
    }

    private static string PathLeaf(string path)
    {
        var slash = path.LastIndexOf('\\');
        return slash < 0 ? path : path[(slash + 1)..];
    }

    private static int DepthBelow(string root, string path)
    {
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

    private static bool IsWithin(string root, string path)
    {
        if (path.Length == root.Length)
        {
            return string.Equals(path, root, StringComparison.OrdinalIgnoreCase);
        }

        // The separator test is what keeps "C:\Program Files" from swallowing "C:\Program Files 2".
        return path.Length > root.Length
            && path[root.Length] == '\\'
            && path.AsSpan(0, root.Length).Equals(root, StringComparison.OrdinalIgnoreCase);
    }

    private sealed record Tree(IReadOnlyDictionary<long, DirectoryNode> Nodes, long RootId)
    {
        public Dictionary<long, long> OwnBytesBefore { get; } = [];

        public Dictionary<long, long> OwnBytesAfter { get; } = [];

        public Dictionary<long, long> OwnFilesBefore { get; } = [];

        public Dictionary<long, long> OwnFilesAfter { get; } = [];

        public HashSet<string> PresentBefore { get; } = new(StringComparer.OrdinalIgnoreCase);

        public HashSet<string> PresentAfter { get; } = new(StringComparer.OrdinalIgnoreCase);
    }
}
