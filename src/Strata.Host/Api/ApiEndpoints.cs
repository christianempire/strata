using System.Globalization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Strata.Core.Model;
using Strata.Core.Scanning;
using Strata.Host.Scanning;
using Strata.Platform;
using Strata.Storage;

namespace Strata.Host.Api;

/// <summary>
/// The read and command endpoints, everything except the two tree-shaped ones.
/// </summary>
/// <remarks>
/// JSON with no wrapper envelope, per the brief: a list endpoint returns a list. The only shape
/// anything is wrapped in is <see cref="ApiError"/>, and only on a failure.
/// </remarks>
internal static class ApiEndpoints
{
    /// <summary>Never let one request pull an unbounded result set into memory.</summary>
    private const int DefaultLimit = 500;
    private const int MaximumLimit = 20_000;

    internal static void MapAll(IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api");

        api.MapGet("/volumes", GetVolumes);
        api.MapGet("/snapshots", GetSnapshots);
        api.MapGet("/events", GetEvents);
        api.MapPost("/scan", PostScan);
        api.MapGet("/settings", GetSettings);
        api.MapPut("/settings", PutSettings);

        TreeEndpoints.Map(api);
    }

    private static IResult GetVolumes(StrataDatabase database)
    {
        using var connection = database.OpenRead();

        var stored = VolumeQueries.List(connection)
            .ToDictionary(v => v.GuidPath.TrimEnd('\\'), StringComparer.OrdinalIgnoreCase);

        var responses = new List<VolumeResponse>();

        foreach (var live in VolumeEnumerator.Enumerate())
        {
            stored.Remove(live.GuidPath.TrimEnd('\\'), out var row);
            responses.Add(Merge(live, row));
        }

        // A volume with history that is not plugged in right now. Reported rather than dropped: its
        // history is still browsable and hiding it would look like data loss.
        foreach (var orphan in stored.Values)
        {
            responses.Add(Detached(orphan));
        }

        return Results.Ok(responses);
    }

    private static VolumeResponse Merge(VolumeIdentity live, VolumeRow? row) => new()
    {
        Id = row?.Id,
        GuidPath = live.GuidPath,
        DriveLetter = live.DriveLetter,
        Label = live.Label ?? "",
        FileSystem = live.FileSystem,
        Kind = live.Kind,
        DiskNumber = live.DiskNumber,

        // Capacity comes from the live volume, not from the stored row: free space changes
        // constantly and the row only refreshes when something scans.
        TotalBytes = live.TotalBytes,
        FreeBytes = live.FreeBytes,
        UsedBytes = live.UsedBytes,
        AccountedBytes = row?.AccountedBytes ?? 0,
        Attached = true,
        Scannable = VolumesCommand.IsScannable(live),
        SupportsUsnJournal = live.SupportsUsnJournal,
        BaselineUtc = row?.BaselineUtc,
        LastSnapshotUtc = row?.LastSnapshotUtc,
        SnapshotCount = row?.SnapshotCount ?? 0,
    };

    private static VolumeResponse Detached(VolumeRow row) => new()
    {
        Id = row.Id,
        GuidPath = row.GuidPath,
        DriveLetter = row.DriveLetter,
        Label = row.Label,
        FileSystem = row.FileSystem,
        Kind = row.Kind,
        DiskNumber = row.DiskNumber,
        TotalBytes = row.TotalBytes,
        FreeBytes = row.FreeBytes,
        UsedBytes = row.UsedBytes,
        AccountedBytes = row.AccountedBytes,
        Attached = false,
        Scannable = false,
        SupportsUsnJournal = row.FileSystem.Equals("NTFS", StringComparison.OrdinalIgnoreCase),
        BaselineUtc = row.BaselineUtc,
        LastSnapshotUtc = row.LastSnapshotUtc,
        SnapshotCount = row.SnapshotCount,
    };

    private static IResult GetSnapshots(
        StrataDatabase database,
        string? volume,
        string? from,
        string? to,
        int? limit)
    {
        using var connection = database.OpenRead();

        if (!TryResolveVolume(connection, volume, out var volumeId, out var failure))
        {
            return failure;
        }

        if (!TryParseTime(from, out var fromUtc) || !TryParseTime(to, out var toUtc))
        {
            return BadTime();
        }

        return Results.Ok(SnapshotQueries.List(connection, volumeId, fromUtc, toUtc, Clamp(limit)));
    }

    private static IResult GetEvents(
        StrataDatabase database,
        string? volume,
        string? from,
        string? to,
        long? minBytes,
        int? limit)
    {
        using var connection = database.OpenRead();

        if (!TryResolveVolume(connection, volume, out var volumeId, out var failure))
        {
            return failure;
        }

        if (!TryParseTime(from, out var fromUtc) || !TryParseTime(to, out var toUtc))
        {
            return BadTime();
        }

        return Results.Ok(EventQueries.List(
            connection, volumeId, fromUtc, toUtc, Math.Max(0, minBytes ?? 0), Clamp(limit)));
    }

    /// <summary>
    /// Queues a scan.
    /// </summary>
    /// <remarks>
    /// Always 202 and never 200: the scan has not happened when this returns, and a baseline will
    /// not have happened for another few minutes. Progress is visible through <c>/api/snapshots</c>,
    /// which gains a row when the scan lands.
    /// </remarks>
    private static IResult PostScan(ScanRequestBody? body, StrataDatabase database, ScanScheduler scheduler)
    {
        var name = body?.Volume;

        if (string.IsNullOrWhiteSpace(name))
        {
            return Results.BadRequest(new ApiError("A volume is required, for example { \"volume\": \"C:\" }."));
        }

        var tier = (body?.Tier ?? "incremental").ToLowerInvariant() switch
        {
            "full" or "baseline" or "1" => ScanTier.Full,
            "incremental" or "delta" or "2" => ScanTier.Incremental,
            _ => (ScanTier?)null,
        };

        if (tier is null)
        {
            return Results.BadRequest(new ApiError("tier must be 'full' or 'incremental'."));
        }

        var volume = VolumeEnumerator.Enumerate().FirstOrDefault(v => CliOptions.Matches(v, name));

        if (volume is null)
        {
            return Results.NotFound(new ApiError($"No volume matches '{name}'."));
        }

        if (tier == ScanTier.Incremental && !volume.SupportsUsnJournal)
        {
            return Results.BadRequest(new ApiError(
                $"{volume.Display} has no change journal, so only a full scan is possible."));
        }

        var request = scheduler.Enqueue(volume, tier.Value);

        return Results.Accepted($"/api/snapshots?volume={volume.DriveLetter ?? volume.GuidPath}", new ScanAccepted
        {
            Id = request.Id,
            Volume = volume.Display,
            Tier = tier.Value,

            // Measured, not guessed: 211 s cold for 3M files on the target machine, and cold is the
            // case a baseline actually runs in.
            Expect = tier == ScanTier.Full ? "minutes" : "under two seconds",
        });
    }

    private static IResult GetSettings(StrataDatabase database)
    {
        using var connection = database.OpenRead();
        return Results.Ok(SettingsRepository.Load(connection));
    }

    private static IResult PutSettings(SettingsPatch? patch, StrataDatabase database, ScanScheduler scheduler)
    {
        if (patch is null)
        {
            return Results.BadRequest(new ApiError("A settings body is required."));
        }

        using var connection = database.Open();

        var updated = patch.Apply(SettingsRepository.Load(connection));
        var errors = updated.Validate();

        if (errors.Count > 0)
        {
            return Results.BadRequest(new ApiError(string.Join(" ", errors)));
        }

        SettingsRepository.Save(connection, updated);

        // So a shortened interval or an un-pause takes effect now rather than after the old one
        // finishes running out.
        scheduler.Poke();

        return Results.Ok(updated);
    }

    // --- shared parameter handling --------------------------------------------------------------

    internal static int Clamp(int? limit)
        => limit is null ? DefaultLimit : Math.Clamp(limit.Value, 1, MaximumLimit);

    /// <summary>
    /// Resolves an optional volume parameter, distinguishing "not asked for" from "does not exist".
    /// </summary>
    /// <remarks>
    /// Worth the ceremony: an unrecognised volume that quietly fell through to "all volumes" would
    /// produce a chart of the wrong disk, which is worse than an error.
    /// </remarks>
    internal static bool TryResolveVolume(
        Microsoft.Data.Sqlite.SqliteConnection connection,
        string? volume,
        out long? volumeId,
        out IResult failure)
    {
        volumeId = null;
        failure = Results.Empty;

        if (string.IsNullOrWhiteSpace(volume))
        {
            return true;
        }

        volumeId = VolumeQueries.Resolve(connection, volume);

        if (volumeId is not null)
        {
            return true;
        }

        failure = Results.NotFound(new ApiError($"No volume matches '{volume}'."));
        return false;
    }

    /// <summary>Parses a time bound, treating a bare date or time as UTC.</summary>
    internal static bool TryParseTime(string? value, out DateTime? parsed)
    {
        parsed = null;

        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        if (!DateTime.TryParse(
                value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
                out var result))
        {
            return false;
        }

        parsed = result;
        return true;
    }

    internal static IResult BadTime()
        => Results.BadRequest(new ApiError(
            "from and to must be ISO-8601, for example 2026-08-19T00:00:00Z. A value with no zone is read as UTC."));
}
