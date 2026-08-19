using System.Diagnostics;
using Microsoft.Data.Sqlite;
using Strata.Core.Diffing;
using Strata.Core.Model;
using Strata.Core.Rollup;
using Strata.Core.Scanning;
using Strata.Platform;
using Strata.Storage;

namespace Strata.Host.Scanning;

/// <summary>What one incremental tick did, in the terms a caller wants to report.</summary>
/// <remarks>
/// A returned value rather than a written line, because there are now two callers with different
/// output: the <c>watch</c> verb prints it to a console, and the hosted scheduler logs it. The tick
/// itself has no opinion about either.
/// </remarks>
internal sealed record TickOutcome
{
    internal required ScanTier Tier { get; init; }

    /// <summary>False when the gate deferred, or when there was nothing to do.</summary>
    internal required bool Ran { get; init; }

    /// <summary>Why nothing ran, when nothing did.</summary>
    internal string Deferral { get; init; } = "";

    internal int RecordsRead { get; init; }

    internal int MetadataReads { get; init; }

    internal int DirectoryChanges { get; init; }

    internal bool CatchingUp { get; init; }

    internal TimeSpan Duration { get; init; }

    internal TimeSpan JournalRead { get; init; }

    internal TimeSpan Apply { get; init; }

    internal TimeSpan Metadata { get; init; }

    internal TimeSpan Store { get; init; }

    internal long RollupMs { get; init; }

    /// <summary>
    /// Every directory whose subtree total moved this tick, ancestors included.
    /// </summary>
    /// <remarks>
    /// Carried on the outcome only so <c>--rollup-log</c> can record it. It is the set a sparse
    /// history model would consider writing a row for, and it is computed to do the update anyway.
    /// </remarks>
    internal IReadOnlyDictionary<long, DirectoryDelta> RollupFanOut { get; init; }
        = new Dictionary<long, DirectoryDelta>();

    /// <summary>The subset that changed directly rather than by inheriting a descendant's change.</summary>
    internal IReadOnlyDictionary<long, DirectoryDelta> Seeds { get; init; }
        = new Dictionary<long, DirectoryDelta>();

    /// <summary>Cost of materialising the historical directory rollup for this tick.</summary>
    internal long SnapshotDirsMs { get; init; }

    /// <summary>Directory rows the historical rollup recorded.</summary>
    internal int SnapshotDirCount { get; init; }

    internal long EventMs { get; init; }

    internal long CommitMs { get; init; }

    internal IReadOnlyList<AttributedChange> Changes { get; init; } = [];

    /// <summary>The tick budget from the brief. An overrun is logged, never silently absorbed.</summary>
    internal bool OverBudget => Duration > TimeSpan.FromSeconds(2);

    internal static TickOutcome Skipped(ScanTier tier, string reason)
        => new() { Tier = tier, Ran = false, Deferral = reason };
}

/// <summary>
/// Keeps one volume up to date from the change journal.
/// </summary>
/// <remarks>
/// Extracted from the <c>watch</c> verb in Phase 3 so the API host can run the same code rather than
/// a second implementation of it. The behaviour is unchanged: read the journal from the persisted
/// cursor, apply the deltas, record the tick as a snapshot, attribute the change, and commit the
/// cursor in the same transaction as the data it describes.
/// </remarks>
internal sealed class VolumeWatcher : IDisposable
{
    private readonly StrataDatabase _database;
    private readonly Win32UsnJournal _journal;
    private readonly Win32FileMetadataReader _metadata;

    /// <summary>
    /// The directory tree, loaded once and kept.
    /// </summary>
    /// <remarks>
    /// Half a million rows on a system volume. Re-reading it every tick would cost more than the
    /// journal read the tick exists to perform, and the tree only changes when a directory is
    /// created or removed — which Tier 2 sees and can apply in place.
    /// </remarks>
    private Dictionary<long, DirectoryNode>? _tree;

    internal VolumeWatcher(VolumeIdentity volume, StrataDatabase database)
    {
        Volume = volume;
        _database = database;
        _journal = new Win32UsnJournal(volume);
        _metadata = new Win32FileMetadataReader(volume);
    }

    internal VolumeIdentity Volume { get; }

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Performance",
        "CA1859:Use concrete types when possible",
        Justification = "The gate is an abstraction boundary on purpose: it is what lets the " +
                        "scheduler's behaviour be tested without a game running. A virtual call " +
                        "once per volume per fifteen minutes is not a cost worth the coupling.")]
    internal TickOutcome Tick(IScanGate gate, StrataSettings settings, bool ignoreGate = false)
    {
        ArgumentNullException.ThrowIfNull(gate);
        ArgumentNullException.ThrowIfNull(settings);

        using var connection = _database.Open();
        var volumeId = VolumeRepository.Upsert(connection, Volume);

        if (!IncrementalRepository.HasBaseline(connection, volumeId))
        {
            return TickOutcome.Skipped(ScanTier.Full, "no baseline yet; run 'strata scan' first");
        }

        if (settings.Paused)
        {
            return TickOutcome.Skipped(ScanTier.Incremental, "paused");
        }

        var decision = ignoreGate ? GateDecision.Allow : gate.CanScan(ScanTier.Incremental);

        if (!decision.Allowed)
        {
            return TickOutcome.Skipped(
                ScanTier.Incremental,
                $"deferred: {decision.Reason}{Detail(decision)}");
        }

        var cursor = IncrementalRepository.GetCursor(connection, volumeId);
        var scanner = new Tier2Scanner(_journal, _metadata, new Tier2Options
        {
            MaxRecordsPerTick = settings.MaxRecordsPerTick,
        });

        var clock = Stopwatch.StartNew();

        using var transaction = connection.BeginTransaction();
        using var store = new SqliteFileStateStore(connection, volumeId, transaction);

        var result = scanner.Run(cursor, store);

        if (result.Status != UsnReadStatus.Ok)
        {
            transaction.Rollback();
            return TickOutcome.Skipped(ScanTier.Incremental, Explain(result));
        }

        var phase = Stopwatch.StartNew();
        var fanOut = IncrementalRepository.ApplyDeltas(connection, transaction, volumeId, result.Deltas);
        var rollupMs = phase.ElapsedMilliseconds;
        phase.Restart();

        // Every tick is a snapshot, so the timeline is a continuous series rather than a few points
        // wherever a baseline happened to run. Events hang off it.
        var snapshotId = IncrementalRepository.CreateIncrementalSnapshot(
            connection, transaction, volumeId, DateTime.UtcNow,
            (long)clock.Elapsed.TotalMilliseconds, Volume.FreeBytes);

        // The historical rollup, which is what the Compare screen diffs and what the Timeline's
        // stacked area is drawn from. Tier 1 wrote this from the rollup it had just built in memory;
        // Tier 2 derives it from dir_totals, which it has been maintaining all along. Without it a
        // tick's snapshot has totals and nothing to attribute them to.
        var rolledUp = IncrementalRepository.WriteSnapshotRollup(
            connection,
            transaction,
            volumeId,
            snapshotId,
            Volume.DriveLetter ?? Volume.GuidPath,
            new SnapshotOptions
            {
                MaxDepth = settings.SnapshotMaxDepth,
                SizeFloorBytes = settings.SnapshotSizeFloorBytes,
            });

        var snapshotDirsMs = phase.ElapsedMilliseconds;

        phase.Restart();
        var changes = Attribute(connection, transaction, volumeId, result, settings);

        if (changes.Count > 0)
        {
            IncrementalRepository.WriteEvents(
                connection, transaction, volumeId, snapshotId, DateTime.UtcNow, changes);
        }

        var eventMs = phase.ElapsedMilliseconds;

        // The cursor is committed with the data it describes. Committing it separately would let a
        // crash between the two skip those records permanently, which corrupts history with no
        // error to notice.
        IncrementalRepository.SaveCursor(connection, transaction, volumeId, result.Cursor);

        phase.Restart();
        transaction.Commit();
        var commitMs = phase.ElapsedMilliseconds;

        clock.Stop();

        return new TickOutcome
        {
            Tier = ScanTier.Incremental,
            Ran = true,
            RecordsRead = result.RecordsRead,
            MetadataReads = result.MetadataReads,
            DirectoryChanges = result.DirectoryChanges,
            CatchingUp = result.CatchingUp,
            Duration = clock.Elapsed,
            JournalRead = result.JournalReadDuration,
            Apply = result.ApplyDuration,
            Metadata = result.MetadataDuration,
            Store = result.StoreDuration,
            RollupMs = rollupMs,
            RollupFanOut = fanOut.Touched,
            Seeds = result.Deltas,
            SnapshotDirsMs = snapshotDirsMs,
            SnapshotDirCount = rolledUp,
            EventMs = eventMs,
            CommitMs = commitMs,
            Changes = changes,
        };
    }

    /// <summary>Turns this tick's deltas into the rows the events feed shows.</summary>
    private IReadOnlyList<AttributedChange> Attribute(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long volumeId,
        Tier2Result result,
        StrataSettings settings)
    {
        if (result.Deltas.Count == 0)
        {
            return [];
        }

        var tree = _tree ??= IncrementalRepository.LoadDirectoryTree(connection, volumeId);

        if (tree.Count == 0)
        {
            return [];
        }

        // Directories this tick has just discovered are not in the cached tree, and the attribution
        // engine drops a delta whose directory it cannot find. Stitching them on first is what makes
        // a fresh game install visible in the feed instead of only in the totals.
        IncrementalRepository.AddMissingNodes(connection, transaction, volumeId, tree, result.Deltas.Keys);

        var rootId = tree.Values.FirstOrDefault(n => n.Depth == 0)?.Id ?? 5;
        var prefix = Volume.DriveLetter ?? Volume.GuidPath;

        // Strata's own database sits on a watched volume and is rewritten by every tick, so without
        // this it is permanently the loudest thing in its own feed — a 450 MB file churning on a
        // fifteen-minute cycle drowns out whatever the user actually did.
        //
        // Filtered from attribution only, not from the rollup: the database occupies real disk and
        // should still appear in totals and in the treemap. It is noise in the change log, not a lie
        // in the accounting.
        var reportable = PathExclusions.Filter(
            result.Deltas, tree, prefix, Exclusions.ForEvents(settings));

        if (reportable.Count == 0)
        {
            return [];
        }

        return DiffEngine.Attribute(tree, rootId, reportable, prefix, new DiffOptions
        {
            MinimumDeltaBytes = settings.EventFloorBytes,
        });
    }

    private static string Detail(GateDecision decision)
        => string.IsNullOrEmpty(decision.Detail) ? "" : $" ({decision.Detail})";

    private static string Explain(Tier2Result result)
        => result.Status switch
        {
            UsnReadStatus.JournalNotActive =>
                "no change journal; run 'strata enable-journal' elevated to create one",
            UsnReadStatus.JournalInvalidated =>
                "journal reset or cursor trimmed; a full baseline is needed",
            UsnReadStatus.AccessDenied => "journal not readable",
            _ => $"journal read failed ({result.Status})",
        };

    public void Dispose()
    {
        _journal.Dispose();
        _metadata.Dispose();
    }
}
