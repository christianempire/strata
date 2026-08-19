using System.Runtime.CompilerServices;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Strata.Core.Diffing;
using Strata.Storage;

namespace Strata.Host.Api;

/// <summary>
/// The two endpoints that walk a directory tree.
/// </summary>
/// <remarks>
/// Separate from the rest because these are the ones with something to decide. <c>/tree</c> serves
/// two different sources depending on what it was asked for, and <c>/diff</c> is where the engine
/// that the product exists for actually runs.
/// </remarks>
internal static class TreeEndpoints
{
    /// <summary>
    /// How many nodes one tree request may return.
    /// </summary>
    /// <remarks>
    /// The root of a system volume at unbounded depth is 571k directories, which is a response no
    /// browser is going to enjoy. The walk streams, so this is a courtesy to the caller rather than
    /// a memory limit here.
    /// </remarks>
    private const int DefaultTreeLimit = 10_000;
    private const int MaximumTreeLimit = 600_000;

    /// <summary>Descending forever is never what was meant; 32 is past NTFS's practical depth.</summary>
    private const int MaximumDepth = 32;

    internal static void Map(RouteGroupBuilder api)
    {
        api.MapGet("/tree", GetTree);
        api.MapGet("/diff", GetDiff);
    }

    /// <summary>
    /// A directory tree, either current or historical.
    /// </summary>
    /// <remarks>
    /// <c>volume</c> reads current state from the maintained rollup. <c>snapshot</c> reads the
    /// sparse history as it stood at the moment that snapshot was taken, which since migration 005
    /// is every directory at full depth rather than a rollup bounded by a depth cap and a size
    /// floor. The two now answer the same question at different times.
    /// <para>
    /// Nodes carry a parent id and a leaf name rather than a materialised path. The caller already
    /// knows the path it asked for and every other path is that plus the names on the way down;
    /// sending full paths would have cost roughly 90 MB of strings on a walk of C:.
    /// </para>
    /// </remarks>
    private static IResult GetTree(
        StrataDatabase database,
        long? snapshot,
        string? volume,
        string? path,
        int? depth,
        int? limit,
        CancellationToken cancellationToken)
    {
        if (snapshot is null && string.IsNullOrWhiteSpace(volume))
        {
            return Results.BadRequest(new ApiError(
                "Either volume=<letter|guid|id> for current state, or snapshot=<id> for history."));
        }

        var levels = Math.Clamp(depth ?? 1, 0, MaximumDepth);
        var cap = limit is null ? DefaultTreeLimit : Math.Clamp(limit.Value, 1, MaximumTreeLimit);

        using var connection = database.OpenRead();

        if (snapshot is not null)
        {
            var row = SnapshotQueries.ById(connection, snapshot.Value);

            if (row is null)
            {
                return Results.NotFound(new ApiError($"No snapshot {snapshot}."));
            }

            if (!ApiEndpoints.TryParseTime(row.TakenUtc, out var instant))
            {
                return Results.Problem("That snapshot's timestamp could not be read.");
            }

            // Resolved against the name dimension rather than current state, so a path that no
            // longer exists still resolves. That is the point of a history that outlives its
            // directories.
            var historicalRoot = path is null
                ? DirectoryHistoryQueries.RootId(connection, row.VolumeId)
                : DirectoryHistoryQueries.ResolvePath(connection, row.VolumeId, path);

            if (historicalRoot is null)
            {
                return Results.NotFound(new ApiError(
                    path is null
                        ? "That volume has no directory history yet; it needs a baseline scan."
                        : $"No directory '{path}' has ever been recorded on that volume."));
            }

            return Results.Ok(StreamHistory(
                database, row.VolumeId, historicalRoot.Value, instant!.Value, levels, cap,
                cancellationToken));
        }

        if (!ApiEndpoints.TryResolveVolume(connection, volume, out var volumeId, out var failure))
        {
            return failure;
        }

        var rootId = TreeQueries.ResolvePath(connection, volumeId!.Value, path);

        if (rootId is null)
        {
            return Results.NotFound(new ApiError(
                string.IsNullOrWhiteSpace(path)
                    ? $"Volume '{volume}' has no directory rollup yet; it needs a baseline scan."
                    : $"No directory '{path}' on volume '{volume}'."));
        }

        return Results.Ok(StreamCurrent(database, volumeId.Value, rootId.Value, levels, cap, cancellationToken));
    }

    /// <summary>
    /// What changed between two snapshots, attributed to the directories that explain it.
    /// </summary>
    /// <remarks>
    /// Reads the sparse history rather than two stored rollups. A snapshot id is really being used
    /// as a moment in time here: the comparison is between the two instants those snapshots were
    /// taken at, and any directory that moved in between is in scope regardless of how deep it sits
    /// or how small it is. The rollup this replaced could see neither.
    /// <para>
    /// Reads only what changed. The directories with a history row in the window are the answer's
    /// raw material, so a comparison costs what happened rather than what exists.
    /// </para>
    /// </remarks>
    private static IResult GetDiff(
        StrataDatabase database,
        long? from,
        long? to,
        string? path,
        long? minBytes,
        int? limit)
    {
        if (from is null || to is null)
        {
            return Results.BadRequest(new ApiError("from and to are snapshot ids."));
        }

        using var connection = database.OpenRead();

        var earlier = SnapshotQueries.ById(connection, from.Value);
        var later = SnapshotQueries.ById(connection, to.Value);

        if (earlier is null || later is null)
        {
            return Results.NotFound(new ApiError($"No snapshot {(earlier is null ? from : to)}."));
        }

        if (earlier.VolumeId != later.VolumeId)
        {
            return Results.BadRequest(new ApiError("Snapshots are of different volumes."));
        }

        // Ordered so a caller who passes them the wrong way round gets the change that happened
        // rather than its negative.
        var (before, after) = string.CompareOrdinal(earlier.TakenUtc, later.TakenUtc) <= 0
            ? (earlier, later)
            : (later, earlier);

        if (!ApiEndpoints.TryParseTime(before.TakenUtc, out var fromUtc) ||
            !ApiEndpoints.TryParseTime(after.TakenUtc, out var toUtc))
        {
            return Results.Problem("A snapshot timestamp could not be read.");
        }

        var scope = path is null
            ? DirectoryHistoryQueries.RootId(connection, before.VolumeId)
            : DirectoryHistoryQueries.ResolvePath(connection, before.VolumeId, path);

        if (scope is null)
        {
            return Results.NotFound(new ApiError(
                path is null
                    ? "That volume has no directory history yet; it needs a baseline scan."
                    : $"No directory '{path}' has ever been recorded on that volume."));
        }

        var changed = DirectoryHistoryQueries.ChangedBetween(
            connection, before.VolumeId, fromUtc!.Value, toUtc!.Value);

        if (changed.Count == 0)
        {
            return Results.Ok(Array.Empty<AttributedChange>());
        }

        // Ancestors as well, because attribution walks up from what moved and the path of whatever
        // gets reported is built from the same tree.
        var nodes = DirectoryHistoryQueries.LoadNodes(connection, before.VolumeId, changed);

        var changes = HistoryDiff.Compare(
            nodes,
            scope.Value,
            DirectoryHistoryQueries.MeasureAt(connection, before.VolumeId, fromUtc.Value, nodes.Keys),
            DirectoryHistoryQueries.MeasureAt(connection, before.VolumeId, toUtc.Value, nodes.Keys),
            VolumePrefix(connection, before.VolumeId),
            new DiffOptions
            {
                MinimumDeltaBytes = Math.Max(0, minBytes ?? 1024 * 1024),
                MaxResults = ApiEndpoints.Clamp(limit),
            });

        return Results.Ok(changes);
    }

    /// <summary>The drive letter a path should be displayed under, falling back to the GUID path.</summary>
    private static string VolumePrefix(Microsoft.Data.Sqlite.SqliteConnection connection, long volumeId)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT COALESCE(drive_letter, guid_path) FROM volumes WHERE id = $v;";
        command.Parameters.AddWithValue("$v", volumeId);

        return command.ExecuteScalar() as string ?? "";
    }

    /// <summary>
    /// Streams a current-state walk on its own connection.
    /// </summary>
    /// <remarks>
    /// Its own connection because serialisation outlives the endpoint method: by the time the first
    /// node is written the handler has already returned, so a connection scoped to the handler would
    /// be closed underneath the reader.
    /// </remarks>
    private static async IAsyncEnumerable<TreeNode> StreamCurrent(
        StrataDatabase database,
        long volumeId,
        long rootDirId,
        int depth,
        int limit,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var connection = database.OpenRead();

        await foreach (var node in TreeQueries
            .WalkCurrentAsync(connection, volumeId, rootDirId, depth, limit, cancellationToken)
            .ConfigureAwait(false))
        {
            yield return node;
        }
    }

    private static async IAsyncEnumerable<TreeNode> StreamHistory(
        StrataDatabase database,
        long volumeId,
        long rootDirId,
        DateTime instantUtc,
        int depth,
        int limit,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var connection = database.OpenRead();

        await foreach (var node in HistoryTreeWalk
            .WalkAsync(connection, volumeId, rootDirId, instantUtc, depth, limit, cancellationToken)
            .ConfigureAwait(false))
        {
            yield return node;
        }
    }
}
