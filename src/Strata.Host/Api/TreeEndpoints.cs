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
    /// <c>snapshot</c> reads that snapshot's stored rollup, which is bounded by the depth cap and
    /// size floor it was written with. <c>volume</c> reads current state, which is every directory
    /// at full depth. They answer different questions and the endpoint does not blur them: a caller
    /// asking about a snapshot gets what that snapshot recorded, including nothing at all if it
    /// recorded nothing.
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
            if (SnapshotQueries.ById(connection, snapshot.Value) is null)
            {
                return Results.NotFound(new ApiError($"No snapshot {snapshot}."));
            }

            return Results.Ok(StreamSnapshot(database, snapshot.Value, path, levels, cap, cancellationToken));
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
    /// Only snapshots that recorded a directory rollup can take part. An incremental tick records
    /// one from Phase 3 onward, but ticks taken before that did not, and a snapshot with no rows is
    /// reported as such rather than being silently compared against nothing and coming back empty.
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

        if (earlier.RolledUpDirCount == 0 || later.RolledUpDirCount == 0)
        {
            var empty = earlier.RolledUpDirCount == 0 ? from : to;

            return Results.BadRequest(new ApiError(
                $"Snapshot {empty} recorded no directory rollup, so there is nothing to compare. " +
                "Snapshots taken before the rollup was materialised on every tick cannot be diffed."));
        }

        // Ordered so a caller who passes them the wrong way round gets the change that happened
        // rather than its negative.
        var (before, after) = string.CompareOrdinal(earlier.TakenUtc, later.TakenUtc) <= 0
            ? (earlier, later)
            : (later, earlier);

        var changes = SnapshotDiff.Compare(
            DiffQueries.LoadSnapshotDirs(connection, before.Id, path),
            DiffQueries.LoadSnapshotDirs(connection, after.Id, path),
            path,
            new DiffOptions
            {
                MinimumDeltaBytes = Math.Max(0, minBytes ?? 1024 * 1024),
                MaxResults = ApiEndpoints.Clamp(limit),
            });

        return Results.Ok(changes);
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

    private static async IAsyncEnumerable<TreeNode> StreamSnapshot(
        StrataDatabase database,
        long snapshotId,
        string? path,
        int depth,
        int limit,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var connection = database.OpenRead();

        await foreach (var node in TreeQueries
            .WalkSnapshotAsync(connection, snapshotId, path, depth, limit, cancellationToken)
            .ConfigureAwait(false))
        {
            yield return node;
        }
    }
}
