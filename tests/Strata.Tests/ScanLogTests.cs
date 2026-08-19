using Microsoft.Data.Sqlite;
using Strata.Core.Model;
using Strata.Core.Scanning;
using Strata.Storage;

namespace Strata.Tests;

/// <summary>
/// The audit log the Settings screen is built on.
/// </summary>
/// <remarks>
/// Worth real coverage rather than the thin treatment Storage usually gets, because this table is
/// the evidence for the project's first non-negotiable. A log that quietly dropped deferrals would
/// make the scanner look better behaved than it is, which is the one direction the error must not
/// go in.
/// </remarks>
public sealed class ScanLogTests : IDisposable
{
    private readonly string _directory;
    private readonly StrataDatabase _database;
    private readonly SqliteConnection _connection;
    private readonly long _volumeId;

    public ScanLogTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "strata-scanlog-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);

        _database = new StrataDatabase(Path.Combine(_directory, "test.db"));
        _database.Migrate();

        _connection = _database.Open();
        _volumeId = VolumeRepository.Upsert(_connection, Volume("C:"));
    }

    public void Dispose()
    {
        _connection.Dispose();
        SqliteConnection.ClearAllPools();

        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // Leaving a temp database behind is not worth failing a run over.
        }
    }

    [Fact]
    public void ADeferredTickIsRecordedRatherThanBeingTheAbsenceOfARow()
    {
        Append(minutes: 0, ScanOutcome.Ran, "incremental, 1,204 journal records", ms: 1800);
        Append(minutes: 15, ScanOutcome.Deferred, "fullscreen exclusive app: Cyberpunk2077.exe");

        var rows = ScanLogQueries.List(_connection, null, null, null, null, 100);

        Assert.Equal(2, rows.Count);
        Assert.Equal(ScanOutcome.Deferred, rows[0].Result);
        Assert.Equal("fullscreen exclusive app: Cyberpunk2077.exe", rows[0].Reason);
        Assert.Equal(0, rows[0].DurationMs);
    }

    [Fact]
    public void NewestFirst()
    {
        Append(minutes: 0, ScanOutcome.Ran, "first");
        Append(minutes: 15, ScanOutcome.Ran, "second");
        Append(minutes: 30, ScanOutcome.Ran, "third");

        var rows = ScanLogQueries.List(_connection, null, null, null, null, 100);

        Assert.Equal(["third", "second", "first"], rows.Select(r => r.Reason));
    }

    [Fact]
    public void RowsOfOneCycleShareACycleStamp()
    {
        var cycle = Base.AddMinutes(15);

        Append(minutes: 15, ScanOutcome.Ran, "C: ran", cycleUtc: cycle);
        Append(minutes: 15, ScanOutcome.Deferred, "D: deferred", cycleUtc: cycle);
        Append(minutes: 30, ScanOutcome.Ran, "next cycle", cycleUtc: Base.AddMinutes(30));

        var rows = ScanLogQueries.List(_connection, null, null, null, null, 100);
        var cycles = rows.Select(r => r.CycleUtc).Distinct().ToList();

        Assert.Equal(2, cycles.Count);
        Assert.Equal(2, rows.Count(r => r.CycleUtc == cycle.ToString("O")));
    }

    [Fact]
    public void FilteringByResultLeavesTheSummaryCountingEverything()
    {
        Append(minutes: 0, ScanOutcome.Ran, "ran", ms: 1000);
        Append(minutes: 15, ScanOutcome.Deferred, "deferred");
        Append(minutes: 30, ScanOutcome.Deferred, "deferred");
        Append(minutes: 45, ScanOutcome.Failed, "journal wrapped");

        var deferred = ScanLogQueries.List(_connection, null, null, null, ScanOutcome.Deferred, 100);
        var summary = ScanLogQueries.Summarise(_connection, null, null, null);

        Assert.Equal(2, deferred.Count);
        Assert.Equal(1, summary.Ran);
        Assert.Equal(2, summary.Deferred);
        Assert.Equal(1, summary.Failed);
    }

    /// <summary>
    /// The median is over the ticks that ran, not over every row.
    /// </summary>
    /// <remarks>
    /// Deferrals have a duration of zero, and there are enough of them on a gaming afternoon to drag
    /// the median of all rows to zero — which would report the scanner as instantaneous exactly when
    /// it was doing nothing at all.
    /// </remarks>
    [Fact]
    public void TheMedianIgnoresDeferrals()
    {
        Append(minutes: 0, ScanOutcome.Ran, "ran", ms: 1000);
        Append(minutes: 15, ScanOutcome.Ran, "ran", ms: 2000);
        Append(minutes: 30, ScanOutcome.Ran, "ran", ms: 3000);
        Append(minutes: 45, ScanOutcome.Deferred, "deferred");
        Append(minutes: 60, ScanOutcome.Deferred, "deferred");
        Append(minutes: 75, ScanOutcome.Deferred, "deferred");

        var summary = ScanLogQueries.Summarise(_connection, null, null, null);

        Assert.Equal(2000, summary.MedianMs);
    }

    [Fact]
    public void TheMedianOfAnEvenNumberOfTicksIsTheMeanOfTheMiddleTwo()
    {
        Append(minutes: 0, ScanOutcome.Ran, "ran", ms: 1000);
        Append(minutes: 15, ScanOutcome.Ran, "ran", ms: 2000);
        Append(minutes: 30, ScanOutcome.Ran, "ran", ms: 3000);
        Append(minutes: 45, ScanOutcome.Ran, "ran", ms: 5000);

        Assert.Equal(2500, ScanLogQueries.Summarise(_connection, null, null, null).MedianMs);
    }

    [Fact]
    public void TheWorstTickNamesItsVolume()
    {
        var e = VolumeRepository.Upsert(_connection, Volume("E:"));

        Append(minutes: 0, ScanOutcome.Ran, "incremental", ms: 1800);
        Append(minutes: 15, ScanOutcome.Ran, "periodic full walk", ms: 41_000, volumeId: e, volume: "E:");

        var summary = ScanLogQueries.Summarise(_connection, null, null, null);

        Assert.Equal(41_000, summary.WorstMs);
        Assert.Equal("E:", summary.WorstVolume);
        Assert.Equal(42_800, summary.TotalMs);
    }

    /// <summary>
    /// A day of nothing but deferrals names no worst volume.
    /// </summary>
    /// <remarks>
    /// Every deferral has a duration of zero, so "the longest tick" has no meaningful answer — and
    /// reporting one would invent a culprit out of whichever row the ordering happened to reach
    /// first. A gaming afternoon is exactly this case.
    /// </remarks>
    [Fact]
    public void AWindowWithNoRunsNamesNoWorstVolume()
    {
        Append(minutes: 0, ScanOutcome.Deferred, "fullscreen exclusive app");
        Append(minutes: 15, ScanOutcome.Deferred, "fullscreen exclusive app");

        var summary = ScanLogQueries.Summarise(_connection, null, null, null);

        Assert.Equal(0, summary.WorstMs);
        Assert.Equal("", summary.WorstVolume);
    }

    [Fact]
    public void AnEmptyWindowSummarisesToZeroesRatherThanThrowing()
    {
        Append(minutes: 0, ScanOutcome.Ran, "ran", ms: 1000);

        var summary = ScanLogQueries.Summarise(
            _connection, null, Base.AddDays(1), Base.AddDays(2));

        Assert.Equal(0, summary.Ran);
        Assert.Equal(0, summary.MedianMs);
        Assert.Equal("", summary.WorstVolume);
    }

    /// <summary>
    /// A row survives its volume being unknown.
    /// </summary>
    /// <remarks>
    /// The first baseline of a never-seen volume logs before the volume has a row, and Phase 4's
    /// "forget this volume" removes one. Neither should take the record of what the scanner did with
    /// it, so the display name is stored alongside the id rather than joined for.
    /// </remarks>
    [Fact]
    public void ARowWithNoVolumeIdKeepsItsName()
    {
        Append(minutes: 0, ScanOutcome.Deferred, "paused from the UI", volumeId: null, volume: "—");

        var rows = ScanLogQueries.List(_connection, null, null, null, null, 100);

        Assert.Null(rows[0].VolumeId);
        Assert.Equal("—", rows[0].Volume);
    }

    [Fact]
    public void ScopingToAVolumeExcludesRowsWithNoVolume()
    {
        Append(minutes: 0, ScanOutcome.Ran, "C: ran", ms: 1000);
        Append(minutes: 15, ScanOutcome.Deferred, "paused", volumeId: null, volume: "—");

        var rows = ScanLogQueries.List(_connection, _volumeId, null, null, null, 100);

        Assert.Single(rows);
        Assert.Equal("C: ran", rows[0].Reason);
    }

    // --- helpers ------------------------------------------------------------------------------

    private static DateTime Base { get; } = new(2026, 8, 19, 8, 0, 0, DateTimeKind.Utc);

    private void Append(
        int minutes,
        ScanOutcome result,
        string reason,
        long ms = 0,
        long? volumeId = -1,
        string volume = "C:",
        DateTime? cycleUtc = null)
    {
        var when = Base.AddMinutes(minutes);

        ScanLogRepository.Append(_connection, new ScanLogRow
        {
            CycleUtc = (cycleUtc ?? when).ToString("O"),
            WhenUtc = when.ToString("O"),

            // -1 is "the volume this fixture made"; null is genuinely no volume.
            VolumeId = volumeId == -1 ? _volumeId : volumeId,
            Volume = volume,
            Tier = ScanTier.Incremental,
            Result = result,
            Reason = reason,
            DurationMs = ms,
        });
    }

    private static VolumeIdentity Volume(string letter) => new()
    {
        GuidPath = $@"\\?\Volume{{{letter[0]}0000000-0000-0000-0000-000000000001}}\",
        DriveLetter = letter,
        Label = "Test",
        FileSystem = "NTFS",
        Kind = VolumeKind.Fixed,
        TotalBytes = 1000L * 1024 * 1024 * 1024,
        FreeBytes = 100L * 1024 * 1024 * 1024,
    };
}
