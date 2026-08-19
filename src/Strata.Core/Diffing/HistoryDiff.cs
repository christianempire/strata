using Strata.Core.Rollup;

namespace Strata.Core.Diffing;

/// <summary>
/// Compares two moments of the sparse directory history and hands the result to the diff engine.
/// </summary>
/// <remarks>
/// The id-keyed counterpart to <see cref="SnapshotDiff"/>, and simpler than it in every way that
/// matters. That one had to rebuild a tree by parsing paths, because <c>snapshot_dirs</c> stored a
/// materialised path per row and nothing else. Here the tree is already a tree: the history is keyed
/// on directory ids and the names carry parent links, so the structure is read rather than inferred.
/// <para>
/// Two things follow from the history being complete rather than bounded by a depth cap and a size
/// floor. Own bytes are exact — a node's subtree total minus its children's, with no unrecorded
/// remainder standing in for what fell below a floor. And a change at depth 20 is visible, which
/// under the old model it could not be at any storage budget.
/// </para>
/// <para>
/// Pure, like the rest of <c>Strata.Core</c>: measurements in, changes out.
/// </para>
/// </remarks>
public static class HistoryDiff
{
    /// <summary>One directory's subtree totals at a moment.</summary>
    public readonly record struct Measurement(long SubtreeBytes, long SubtreeFiles);

    /// <summary>
    /// Attributes the difference between two moments to the directories that explain it.
    /// </summary>
    /// <param name="nodes">The directory tree, from the name dimension.</param>
    /// <param name="rootId">Where to report from. The volume root, or the scope the caller asked for.</param>
    /// <param name="before">Subtree totals as they stood at the earlier moment, keyed by directory.</param>
    /// <param name="after">Subtree totals as they stood at the later moment.</param>
    /// <param name="rootPrefix">Path prefix for display, typically the drive letter.</param>
    /// <param name="options">Attribution tunables, or the defaults.</param>
    public static IReadOnlyList<AttributedChange> Compare(
        IReadOnlyDictionary<long, DirectoryNode> nodes,
        long rootId,
        IReadOnlyDictionary<long, Measurement> before,
        IReadOnlyDictionary<long, Measurement> after,
        string rootPrefix,
        DiffOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);

        options ??= new DiffOptions();

        if (!nodes.ContainsKey(rootId))
        {
            return [];
        }

        var subtreeDeltas = SubtreeDeltas(nodes, rootId, before, after);

        if (subtreeDeltas.Count == 0)
        {
            return [];
        }

        var deltas = ToOwnDeltas(nodes, subtreeDeltas);

        if (deltas.Count == 0)
        {
            return [];
        }

        var changes = DiffEngine.Attribute(nodes, rootId, deltas, rootPrefix, options);

        return Reclassify(changes, before, after);
    }

    /// <summary>
    /// The net change in each directory's whole subtree, for directories inside the scope.
    /// </summary>
    /// <remarks>
    /// A directory absent from one side had a total of zero then — either it did not exist, or it
    /// existed before any measurement was taken. Both are correctly read as "nothing there yet".
    /// </remarks>
    private static Dictionary<long, (long Bytes, long Files)> SubtreeDeltas(
        IReadOnlyDictionary<long, DirectoryNode> nodes,
        long rootId,
        IReadOnlyDictionary<long, Measurement> before,
        IReadOnlyDictionary<long, Measurement> after)
    {
        var deltas = new Dictionary<long, (long, long)>();

        // Ancestry is shared, so answering "is this inside the scope" once per directory and
        // remembering it turns a walk per directory into a walk per branch.
        var inScope = new Dictionary<long, bool>();

        foreach (var id in Union(before, after))
        {
            if (!nodes.ContainsKey(id) || !IsWithin(nodes, id, rootId, inScope))
            {
                continue;
            }

            before.TryGetValue(id, out var was);
            after.TryGetValue(id, out var now);

            var bytes = now.SubtreeBytes - was.SubtreeBytes;
            var files = now.SubtreeFiles - was.SubtreeFiles;

            if (bytes != 0 || files != 0)
            {
                deltas[id] = (bytes, files);
            }
        }

        return deltas;
    }

    /// <summary>
    /// Turns subtree deltas into own deltas by subtracting each node's children.
    /// </summary>
    /// <remarks>
    /// The engine takes own-deltas and rolls them back up itself. Feeding it subtree totals instead
    /// would count every byte once per level of ancestry, which is the easiest mistake to make here
    /// and the hardest to notice: every number stays plausible and every total is wrong.
    /// </remarks>
    private static Dictionary<long, DirectoryDelta> ToOwnDeltas(
        IReadOnlyDictionary<long, DirectoryNode> nodes,
        Dictionary<long, (long Bytes, long Files)> subtree)
    {
        var own = new Dictionary<long, (long Bytes, long Files)>(subtree);

        foreach (var (id, delta) in subtree)
        {
            if (!nodes.TryGetValue(id, out var node) || node.ParentId == id)
            {
                continue;
            }

            if (own.TryGetValue(node.ParentId, out var parent))
            {
                own[node.ParentId] = (parent.Bytes - delta.Bytes, parent.Files - delta.Files);
            }
        }

        var deltas = new Dictionary<long, DirectoryDelta>(own.Count);

        foreach (var (id, delta) in own)
        {
            if (delta.Bytes == 0 && delta.Files == 0)
            {
                continue;
            }

            deltas[id] = new DirectoryDelta(
                delta.Bytes,
                // The smallest churn consistent with two measurements, not a measurement of churn.
                // A directory that wrote and deleted the same 50 GB between them is indistinguishable
                // from one that did nothing, and only the Timeline — which reads the journal —
                // reports the real figure. A lower bound rather than zero because the engine will
                // not descend into a node whose net and churn are both under the floor, and a branch
                // that grows while a sibling shrinks nets to zero.
                OwnChurnBytes: Math.Abs(delta.Bytes),
                FilesAdded: delta.Files > 0 ? (int)Math.Min(delta.Files, int.MaxValue) : 0,
                FilesRemoved: delta.Files < 0 ? (int)Math.Min(-delta.Files, int.MaxValue) : 0);
        }

        return deltas;
    }

    /// <summary>
    /// Replaces the engine's guess at a change's kind with what the two moments actually show.
    /// </summary>
    /// <remarks>
    /// The engine infers appeared and vanished from added and removed file counts, which is right
    /// when it is fed a change journal and wrong when it is fed two measurements: a directory that
    /// merely shrank and one that ceased to exist produce the same delta. Presence is recorded, so
    /// it is answered rather than inferred.
    /// </remarks>
    private static List<AttributedChange> Reclassify(
        IReadOnlyList<AttributedChange> changes,
        IReadOnlyDictionary<long, Measurement> before,
        IReadOnlyDictionary<long, Measurement> after)
    {
        var result = new List<AttributedChange>(changes.Count);

        foreach (var change in changes)
        {
            // A remainder is not a directory's own change, so presence says nothing about it: the
            // directory it hangs off exists at both moments by construction, and reclassifying would
            // turn "2.2 GB elsewhere in C:\" into "C:\ grew" — the exact row the remainder exists to
            // replace.
            if (change.Kind == ChangeKind.Remainder)
            {
                result.Add(change);
                continue;
            }

            // Zero bytes and zero files is a directory that has been emptied or has not arrived,
            // which for this purpose is the same as not being there.
            var existedBefore = before.TryGetValue(change.DirectoryId, out var was)
                && (was.SubtreeBytes != 0 || was.SubtreeFiles != 0);

            var existsAfter = after.TryGetValue(change.DirectoryId, out var now)
                && (now.SubtreeBytes != 0 || now.SubtreeFiles != 0);

            var kind = (existedBefore, existsAfter) switch
            {
                (false, true) => ChangeKind.Appeared,
                (true, false) => ChangeKind.Vanished,
                _ => change.DeltaBytes < 0 ? ChangeKind.Shrank : ChangeKind.Grew,
            };

            result.Add(change with { Kind = kind });
        }

        return result;
    }

    private static IEnumerable<long> Union(
        IReadOnlyDictionary<long, Measurement> before,
        IReadOnlyDictionary<long, Measurement> after)
    {
        foreach (var id in before.Keys)
        {
            yield return id;
        }

        foreach (var id in after.Keys)
        {
            if (!before.ContainsKey(id))
            {
                yield return id;
            }
        }
    }

    /// <summary>Whether a directory sits at or beneath the scope root.</summary>
    private static bool IsWithin(
        IReadOnlyDictionary<long, DirectoryNode> nodes,
        long id,
        long rootId,
        Dictionary<long, bool> memo)
    {
        var walked = new List<long>();
        var current = id;
        var guard = 0;
        var answer = false;

        while (true)
        {
            if (memo.TryGetValue(current, out var known))
            {
                answer = known;
                break;
            }

            if (current == rootId)
            {
                answer = true;
                break;
            }

            if (!nodes.TryGetValue(current, out var node) || node.ParentId == current || guard++ > 4096)
            {
                break;
            }

            walked.Add(current);
            current = node.ParentId;
        }

        memo[current] = answer;

        foreach (var seen in walked)
        {
            memo[seen] = answer;
        }

        return answer;
    }
}
