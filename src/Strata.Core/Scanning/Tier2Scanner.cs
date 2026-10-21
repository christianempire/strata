using Strata.Core.Diffing;
using Strata.Core.Model;

namespace Strata.Core.Scanning;

/// <summary>Tunables for an incremental tick.</summary>
public sealed record Tier2Options
{
    /// <summary>
    /// Ceiling on journal records processed in one tick.
    /// </summary>
    /// <remarks>
    /// The brief sets a hard budget of two seconds for the common case but does not say what happens
    /// when a game install or a component-store servicing produces several hundred thousand records
    /// at once. Draining that synchronously is exactly the stall the whole design exists to avoid,
    /// so a tick takes a bounded bite, persists the cursor there, and continues next tick.
    /// <para>
    /// Falling behind is safe only for as long as the journal still holds the cursor's position. It
    /// is a fixed-size circular buffer — 32 MiB on this machine's C: — and once it wraps past us
    /// there is nothing left to resume from. <see cref="Tier2Result.CatchingUp"/> is therefore a
    /// signal the caller has to act on, not a statistic.
    /// </para>
    /// </remarks>
    public int MaxRecordsPerTick { get; init; } = 50_000;

    /// <summary>Budget for the common case. Exceeding it is logged, not enforced mid-tick.</summary>
    public TimeSpan Budget { get; init; } = TimeSpan.FromSeconds(2);
}

/// <summary>What one incremental tick did.</summary>
public sealed class Tier2Result
{
    public required UsnReadStatus Status { get; init; }

    /// <summary>Cursor to persist. Only meaningful when <see cref="Status"/> is Ok.</summary>
    public UsnCursor Cursor { get; init; }

    /// <summary>Per-directory own-deltas, ready for the diff engine.</summary>
    public IReadOnlyDictionary<long, DirectoryDelta> Deltas { get; init; }
        = new Dictionary<long, DirectoryDelta>();

    public int RecordsRead { get; init; }

    public int EntriesTouched { get; init; }

    public int MetadataReads { get; init; }

    /// <summary>
    /// True when the journal held more than this tick was allowed to take.
    /// </summary>
    /// <remarks>
    /// A volume in this state is losing a race, not merely reporting a backlog: it is generating
    /// records faster than one capped tick per interval can drain, and the journal it is falling
    /// behind in is finite. The scheduler shortens the interval until this goes false.
    /// </remarks>
    public bool CatchingUp { get; init; }

    public TimeSpan Duration { get; init; }

    public TimeSpan JournalReadDuration { get; init; }

    public TimeSpan ApplyDuration { get; init; }

    public TimeSpan MetadataDuration { get; init; }

    public TimeSpan StoreDuration { get; init; }

    public int DirectoryChanges { get; init; }

    /// <summary>A baseline is the only way forward, because the journal cannot bridge the gap.</summary>
    public bool RequiresBaseline => Status is UsnReadStatus.JournalInvalidated or UsnReadStatus.JournalNotActive;

    /// <summary>
    /// What kind of remedy this tick needs, for a scheduler deciding whether to force the issue.
    /// </summary>
    /// <remarks>
    /// Narrower than <see cref="RequiresBaseline"/>, which covers a volume with no journal at all.
    /// A baseline would refresh that volume's totals and change nothing about its ability to tick:
    /// only an elevated <c>strata enable-journal</c> does that, and running a three-minute scan
    /// every six hours to not fix it would be worse than saying so.
    /// </remarks>
    public DeferralKind Deferral => Status switch
    {
        UsnReadStatus.Ok => DeferralKind.None,
        UsnReadStatus.JournalInvalidated => DeferralKind.NeedsBaseline,
        _ => DeferralKind.NeedsPerson,
    };
}

/// <summary>
/// Brings a volume's recorded state up to date from the NTFS change journal.
/// </summary>
/// <remarks>
/// The strategy that makes the product viable. A full walk costs time proportional to what exists;
/// this costs time proportional to what changed, which on a quiet fifteen minutes is nearly nothing.
/// <para>
/// The journal never carries file sizes, so the sequence is: read records, reduce to the distinct
/// set of affected files, and read metadata for only those. Reading the journal answers the
/// expensive question - *which* files to look at - that a full walk spends all its time
/// rediscovering.
/// </para>
/// <para>
/// Pure orchestration: the journal, the metadata reads and the store are all injected, so renames,
/// deletions and hardlink fan-out are testable against in-memory fakes.
/// </para>
/// </remarks>
public sealed class Tier2Scanner
{
    private readonly IUsnJournal _journal;
    private readonly IFileMetadataReader _metadata;
    private readonly Tier2Options _options;
    private readonly System.Diagnostics.Stopwatch _metaClock = new();
    private readonly System.Diagnostics.Stopwatch _storeClock = new();
    private int _dirChanges;

    public Tier2Scanner(IUsnJournal journal, IFileMetadataReader metadata, Tier2Options? options = null)
    {
        ArgumentNullException.ThrowIfNull(journal);
        ArgumentNullException.ThrowIfNull(metadata);

        _journal = journal;
        _metadata = metadata;
        _options = options ?? new Tier2Options();
    }

    public Tier2Result Run(UsnCursor cursor, IFileStateStore store)
    {
        ArgumentNullException.ThrowIfNull(store);

        var clock = System.Diagnostics.Stopwatch.StartNew();
        var readClock = System.Diagnostics.Stopwatch.StartNew();
        var read = _journal.Read(cursor, _options.MaxRecordsPerTick);
        readClock.Stop();

        if (read.Status != UsnReadStatus.Ok)
        {
            return new Tier2Result { Status = read.Status, Duration = clock.Elapsed };
        }

        var deltas = new Dictionary<long, DirectoryDelta>();
        var metadataReads = 0;
        var touched = 0;
        _metaClock.Reset();
        _storeClock.Reset();
        _dirChanges = 0;

        // Coalesce first. A single file written in a loop produces hundreds of DATA_EXTEND records,
        // and reading its size once at the end is both cheaper and more correct than reading it
        // repeatedly and recording sizes that were true only momentarily.
        var applyClock = System.Diagnostics.Stopwatch.StartNew();

        foreach (var target in Coalesce(read.Changes))
        {
            touched += Apply(target, store, deltas, ref metadataReads);
        }

        applyClock.Stop();
        clock.Stop();

        return new Tier2Result
        {
            Status = UsnReadStatus.Ok,
            Cursor = new UsnCursor(read.JournalId, read.NextUsn),
            Deltas = deltas,
            RecordsRead = read.Changes.Count,
            EntriesTouched = touched,
            MetadataReads = metadataReads,
            CatchingUp = read.MoreAvailable,
            Duration = clock.Elapsed,
            JournalReadDuration = readClock.Elapsed,
            ApplyDuration = applyClock.Elapsed,
            MetadataDuration = _metaClock.Elapsed,
            StoreDuration = _storeClock.Elapsed,
            DirectoryChanges = _dirChanges,
        };
    }

    /// <summary>
    /// Reduces a tick's records to one entry per affected file, keeping the union of what happened.
    /// </summary>
    private static List<CoalescedChange> Coalesce(IReadOnlyList<UsnChange> changes)
    {
        var byFile = new Dictionary<long, CoalescedChange>();

        foreach (var change in changes)
        {
            if (byFile.TryGetValue(change.FileId, out var existing))
            {
                byFile[change.FileId] = existing.Merge(change);
            }
            else
            {
                byFile[change.FileId] = CoalescedChange.From(change);
            }
        }

        return [.. byFile.Values];
    }

    /// <summary>Applies one file's coalesced change to the store, accumulating directory deltas.</summary>
    private int Apply(
        CoalescedChange change,
        IFileStateStore store,
        Dictionary<long, DirectoryDelta> deltas,
        ref int metadataReads)
    {
        // Directories carry no bytes of their own; their totals come from the files beneath them.
        // A new directory still has to be recorded so its children have somewhere to attach.
        if (change.IsDirectory)
        {
            _dirChanges++;
            _storeClock.Start();
            var seen = store.DirectoryExists(change.FileId);
            _storeClock.Stop();

            if (!seen && TryResolveName(change, out var dirName))
            {
                _storeClock.Start();
                store.AddDirectory(change.FileId, change.ParentId, dirName);
                _storeClock.Stop();
            }

            return 0;
        }

        _storeClock.Start();
        var known = store.GetEntriesByFileId(change.FileId);
        _storeClock.Stop();

        _metaClock.Start();
        var exists = _metadata.TryReadById(change.FileId, out var current);
        _metaClock.Stop();
        metadataReads++;

        if (!exists)
        {
            // Gone. Its last recorded size is the correct final value: the journal cannot tell us
            // how big it was, and the filesystem no longer can either.
            return RemoveAll(known, store, deltas);
        }

        var name = ResolveName(change, known);

        if (name.Length == 0)
        {
            // Neither the journal nor the filesystem would give us a name, and we have no record of
            // this file. Nothing can be attributed to a path we cannot name, so skip rather than
            // invent one.
            return 0;
        }

        // A hardlink change names the link that was added or removed, not where the file lives now.
        // Read as a location, the removal of a link was treated as a move to it: the surviving link
        // was retired as stale and the file recorded again where it no longer is. The link count
        // says which it was. Fewer links than recorded means one went, and it is the one named here;
        // if it is not among the links recorded, it was never counted or has already been retired.
        if (IsLinkChangeOnly(change.Kind) && current.LinkCount <= known.Count)
        {
            var removed = change.Name.Length > 0
                ? FindAt(known, change.ParentId, change.Name)
                : FindAtParent(known, change.ParentId);

            return removed is { } link ? Retire(link, store, deltas) : 0;
        }

        var touched = 0;

        // Split what we already know about this file into the entry at its current location and
        // everything recorded elsewhere.
        StoredEntry? previous = null;
        var elsewhere = new List<StoredEntry>();

        foreach (var entry in known)
        {
            if (entry.ParentId == change.ParentId && string.Equals(entry.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                previous = entry;
            }
            else
            {
                elsewhere.Add(entry);
            }
        }

        // What "elsewhere" means depends on how many links the file actually has. With one link, any
        // other recorded location is a leftover from a rename or a move, and dropping it here is
        // what makes a move behave as a move rather than as a create plus an orphan. With several
        // links, those other locations are real hardlinks and must be left alone.
        if (!current.HasMultipleLinks)
        {
            foreach (var stale in elsewhere)
            {
                Accumulate(deltas, stale.ParentId, stale.Removal);
                store.Delete(stale.ParentId, stale.Name);
                touched++;
            }

            elsewhere.Clear();
        }

        // A rename that changes only the case of a name finds its own entry above, because NTFS
        // names compare without case, but the stored key is still the old spelling. Writing the new
        // one beside it would record the file twice, and its deletion would then subtract it twice.
        if (previous is { } own && !string.Equals(own.Name, name, StringComparison.Ordinal))
        {
            store.Delete(own.ParentId, own.Name);
        }

        // Anything else recorded under this name is no longer there: a directory holds one entry
        // per name. This is the replace-by-rename that atomic saves, extractors and updaters all use
        // - write a temporary, rename it over the original - and the replacement's records arrive
        // before the original's deletion. Overwriting the original's row instead of retiring it left
        // that deletion with nothing to find, so the original's bytes stayed in the totals for good:
        // 478 GB on E: in six weeks of a pipeline that unpacks and repacks games.
        if (previous is null
            && store.TryGetEntry(change.ParentId, name, out var occupant)
            && occupant.FileId != change.FileId)
        {
            touched += Retire(occupant, store, deltas);
        }

        // Exactly one entry per file owns its bytes; further links count zero, so directory rollups
        // sum to the volume total rather than counting shared extents once per link. Ownership is
        // decided against entries that survive this tick, not against ones just removed as stale -
        // getting that wrong made a renamed file look like a hardlink to its own former self.
        var ownsBytes = previous is not null
            ? !previous.Value.IsHardLink
            : !elsewhere.Any(static e => !e.IsHardLink);

        var updated = new StoredEntry(
            change.ParentId,
            name,
            change.FileId,
            current.LogicalSize,
            current.SizeOnDisk,
            IsHardLink: !ownsBytes);

        var before = previous?.CountedBytes ?? 0;
        var after = updated.CountedBytes;

        store.Upsert(updated);
        touched++;

        Accumulate(deltas, change.ParentId, new DirectoryDelta(
            after - before,
            Math.Abs(after - before),
            previous is null ? 1 : 0,
            0,
            updated.CountedOnDisk - (previous?.CountedOnDisk ?? 0)));

        return touched;
    }

    /// <summary>
    /// Drops every entry for a file that no longer exists.
    /// </summary>
    /// <remarks>
    /// Each link is removed separately, but only the byte-owning one moves the directory totals:
    /// the others never contributed bytes, so subtracting them would take the volume below its real
    /// size.
    /// </remarks>
    private static int RemoveAll(
        IReadOnlyList<StoredEntry> known,
        IFileStateStore store,
        Dictionary<long, DirectoryDelta> deltas)
    {
        var removed = 0;

        foreach (var entry in known)
        {
            Accumulate(deltas, entry.ParentId, entry.Removal);
            store.Delete(entry.ParentId, entry.Name);
            removed++;
        }

        return removed;
    }

    /// <summary>
    /// Retires one entry of a file that is otherwise still on disk: its name now belongs to a
    /// different file, or this one link was removed.
    /// </summary>
    /// <remarks>
    /// If the entry owned its file's bytes and the file has other links still recorded, ownership
    /// passes to one of them. Otherwise the bytes would leave the totals while the file is still on
    /// disk under another name.
    /// </remarks>
    private static int Retire(StoredEntry entry, IFileStateStore store, Dictionary<long, DirectoryDelta> deltas)
    {
        Accumulate(deltas, entry.ParentId, entry.Removal);
        store.Delete(entry.ParentId, entry.Name);

        if (entry.IsHardLink)
        {
            return 1;
        }

        var links = store.GetEntriesByFileId(entry.FileId);

        if (links.Count > 0)
        {
            var heir = links[0] with { IsHardLink = false };
            store.Upsert(heir);
            Accumulate(deltas, heir.ParentId, new DirectoryDelta(heir.CountedBytes, 0, 0, 0, heir.CountedOnDisk));
        }

        return 1;
    }

    private string ResolveName(CoalescedChange change, IReadOnlyList<StoredEntry> known)
    {
        if (change.Name.Length > 0)
        {
            return change.Name;
        }

        // Under the unprivileged journal read the record's name is redacted, so fall back to the
        // path the filesystem reports for this file ID.
        if (TryResolveName(change, out var resolved))
        {
            return resolved;
        }

        var existing = FindAtParent(known, change.ParentId);
        return existing?.Name ?? "";
    }

    private bool TryResolveName(CoalescedChange change, out string name)
    {
        name = "";

        if (change.Name.Length > 0)
        {
            name = change.Name;
            return true;
        }

        if (_metadata is IFilePathResolver resolver && resolver.TryGetPath(change.FileId, out var path))
        {
            var separator = path.LastIndexOf('\\');
            name = separator >= 0 ? path[(separator + 1)..] : path;
            return name.Length > 0;
        }

        return false;
    }

    private static StoredEntry? FindAt(IReadOnlyList<StoredEntry> entries, long parentId, string name)
    {
        foreach (var entry in entries)
        {
            if (entry.ParentId == parentId && string.Equals(entry.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return entry;
            }
        }

        return null;
    }

    private static StoredEntry? FindAtParent(IReadOnlyList<StoredEntry> entries, long parentId)
    {
        foreach (var entry in entries)
        {
            if (entry.ParentId == parentId)
            {
                return entry;
            }
        }

        return null;
    }

    /// <summary>A link was added or removed, and nothing arrived, left or was renamed.</summary>
    private static bool IsLinkChangeOnly(UsnChangeKind kind)
        => (kind & UsnChangeKind.HardLinkChanged) != 0
           && (kind & (UsnChangeKind.Created | UsnChangeKind.RenamedFrom | UsnChangeKind.RenamedTo)) == 0;

    private static void Accumulate(Dictionary<long, DirectoryDelta> deltas, long directoryId, DirectoryDelta delta)
        => deltas[directoryId] = deltas.TryGetValue(directoryId, out var existing) ? existing + delta : delta;

    /// <summary>All of a tick's records for one file, merged.</summary>
    private readonly record struct CoalescedChange(
        long FileId,
        long ParentId,
        string Name,
        UsnChangeKind Kind,
        bool IsDirectory)
    {
        internal static CoalescedChange From(UsnChange change)
            => new(change.FileId, change.ParentId, change.Name, change.Kind, change.IsDirectory);

        /// <summary>
        /// Later records win for location, because the last one is where the file ended up. That is
        /// what turns a rename pair into a single move rather than a delete plus a create.
        /// </summary>
        internal CoalescedChange Merge(UsnChange change)
            => new(
                FileId,
                change.ParentId,
                change.Name.Length > 0 ? change.Name : Name,
                Kind | change.Kind,
                IsDirectory || change.IsDirectory);
    }
}

/// <summary>Resolves a file's path from its ID, for when the journal redacts names.</summary>
public interface IFilePathResolver
{
    bool TryGetPath(long fileId, out string volumeRelativePath);
}
