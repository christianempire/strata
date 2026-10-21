using Strata.Core.Model;
using Strata.Core.Scanning;
using Strata.Tests.Fakes;

namespace Strata.Tests;

public sealed class Tier2ScannerTests
{
    private const long Dir = 100;
    private const long OtherDir = 200;
    private const long MB = 1024 * 1024;

    [Fact]
    public void ANewFileIsRecordedAndCountedTowardItsDirectory()
    {
        var journal = new FakeUsnJournal().Add(Change(1, Dir, "game.pak", UsnChangeKind.Created));
        var metadata = new FakeMetadataReader().Set(1, 32 * MB);
        var store = new FakeFileStateStore().WithDirectory(Dir);

        var result = Run(journal, metadata, store);

        Assert.Equal(UsnReadStatus.Ok, result.Status);
        Assert.Equal(32 * MB, result.Deltas[Dir].OwnBytesDelta);
        Assert.Equal(1, result.Deltas[Dir].FilesAdded);
        Assert.Single(store.Entries);
    }

    [Fact]
    public void AGrowingFileReportsOnlyTheDifference()
    {
        var journal = new FakeUsnJournal().Add(Change(1, Dir, "log.txt", UsnChangeKind.DataChanged));
        var metadata = new FakeMetadataReader().Set(1, 30 * MB);
        var store = new FakeFileStateStore()
            .WithDirectory(Dir)
            .WithEntry(new StoredEntry(Dir, "log.txt", 1, 10 * MB, 10 * MB, false));

        var result = Run(journal, metadata, store);

        Assert.Equal(20 * MB, result.Deltas[Dir].OwnBytesDelta);
        Assert.Equal(0, result.Deltas[Dir].FilesAdded);
    }

    /// <summary>
    /// The journal cannot say how big a deleted file was, and the filesystem no longer can either.
    /// The last recorded size is the correct final value, which is why current state is stored.
    /// </summary>
    [Fact]
    public void ADeletedFileSubtractsItsLastRecordedSize()
    {
        var journal = new FakeUsnJournal().Add(Change(1, Dir, "old.bin", UsnChangeKind.Deleted));
        var metadata = new FakeMetadataReader(); // file is already gone
        var store = new FakeFileStateStore()
            .WithDirectory(Dir)
            .WithEntry(new StoredEntry(Dir, "old.bin", 1, 8 * MB, 8 * MB, false));

        var result = Run(journal, metadata, store);

        Assert.Equal(-8 * MB, result.Deltas[Dir].OwnBytesDelta);
        Assert.Equal(1, result.Deltas[Dir].FilesRemoved);
        Assert.Empty(store.Entries);
    }

    /// <summary>
    /// A move must read as a move, not as a deletion and an unrelated creation. Keying on the file
    /// ID is what makes that possible, and the bytes must land in the destination only.
    /// </summary>
    [Fact]
    public void AMoveBetweenDirectoriesTransfersTheBytes()
    {
        var journal = new FakeUsnJournal()
            .Add(Change(1, Dir, "", UsnChangeKind.RenamedFrom))
            .Add(Change(1, OtherDir, "moved.bin", UsnChangeKind.RenamedTo));

        var metadata = new FakeMetadataReader().Set(1, 5 * MB);
        var store = new FakeFileStateStore()
            .WithDirectory(Dir)
            .WithDirectory(OtherDir)
            .WithEntry(new StoredEntry(Dir, "moved.bin", 1, 5 * MB, 5 * MB, false));

        var result = Run(journal, metadata, store);

        Assert.Equal(-5 * MB, result.Deltas[Dir].OwnBytesDelta);
        Assert.Equal(5 * MB, result.Deltas[OtherDir].OwnBytesDelta);

        var entry = Assert.Single(store.Entries);
        Assert.Equal(OtherDir, entry.ParentId);
    }

    [Fact]
    public void ARenameInPlaceKeepsTheBytesWhereTheyAre()
    {
        var journal = new FakeUsnJournal()
            .Add(Change(1, Dir, "before.txt", UsnChangeKind.RenamedFrom))
            .Add(Change(1, Dir, "after.txt", UsnChangeKind.RenamedTo));

        var metadata = new FakeMetadataReader().Set(1, 2 * MB);
        var store = new FakeFileStateStore()
            .WithDirectory(Dir)
            .WithEntry(new StoredEntry(Dir, "before.txt", 1, 2 * MB, 2 * MB, false));

        var result = Run(journal, metadata, store);

        Assert.Equal(0, result.Deltas[Dir].OwnBytesDelta);
        Assert.Equal("after.txt", Assert.Single(store.Entries).Name);
    }

    /// <summary>
    /// Hundreds of DATA_EXTEND records for one file must produce one metadata read, not hundreds.
    /// This is the difference between a tick costing milliseconds and a tick costing seconds.
    /// </summary>
    [Fact]
    public void RepeatedWritesToOneFileCoalesceIntoASingleRead()
    {
        var journal = new FakeUsnJournal();

        for (var i = 0; i < 500; i++)
        {
            journal.Add(Change(1, Dir, "growing.bin", UsnChangeKind.DataChanged));
        }

        var metadata = new FakeMetadataReader().Set(1, 100 * MB);
        var store = new FakeFileStateStore().WithDirectory(Dir);

        var result = Run(journal, metadata, store);

        Assert.Equal(500, result.RecordsRead);
        Assert.Equal(1, metadata.ReadCount);
        Assert.Equal(100 * MB, result.Deltas[Dir].OwnBytesDelta);
    }

    [Fact]
    public void ChangesToSeparateFilesAreNotCoalesced()
    {
        var journal = new FakeUsnJournal()
            .Add(Change(1, Dir, "a.bin", UsnChangeKind.Created))
            .Add(Change(2, Dir, "b.bin", UsnChangeKind.Created));

        var metadata = new FakeMetadataReader().Set(1, 1 * MB).Set(2, 3 * MB);
        var store = new FakeFileStateStore().WithDirectory(Dir);

        var result = Run(journal, metadata, store);

        Assert.Equal(2, metadata.ReadCount);
        Assert.Equal(4 * MB, result.Deltas[Dir].OwnBytesDelta);
    }

    /// <summary>
    /// A hardlinked file must not add its bytes twice. Only the owning entry counts, or the volume
    /// total drifts upward every time WinSxS is serviced.
    /// </summary>
    [Fact]
    public void AnAdditionalHardLinkAddsNoBytes()
    {
        var journal = new FakeUsnJournal().Add(Change(1, OtherDir, "link.dll", UsnChangeKind.Created));
        var metadata = new FakeMetadataReader().Set(1, 4 * MB, linkCount: 2);

        var store = new FakeFileStateStore()
            .WithDirectory(Dir)
            .WithDirectory(OtherDir)
            .WithEntry(new StoredEntry(Dir, "owner.dll", 1, 4 * MB, 4 * MB, IsHardLink: false));

        var result = Run(journal, metadata, store);

        Assert.Equal(0, result.Deltas.TryGetValue(OtherDir, out var d) ? d.OwnBytesDelta : 0);
        Assert.Equal(2, store.Entries.Count);
        Assert.Single(store.Entries, e => !e.IsHardLink);
    }

    /// <summary>
    /// Names are redacted under the unprivileged journal read, so the scanner has to fall back to
    /// resolving the path from the file ID. Verified against the real API in usn-tail; this pins the
    /// behaviour so a refactor cannot quietly drop it.
    /// </summary>
    [Fact]
    public void ARedactedNameIsRecoveredByResolvingTheFileId()
    {
        var journal = new FakeUsnJournal().Add(Change(1, Dir, "", UsnChangeKind.Created));
        var metadata = new FakeMetadataReader().Set(1, 7 * MB, path: @"\Games\Foo\data.pak");
        var store = new FakeFileStateStore().WithDirectory(Dir);

        var result = Run(journal, metadata, store);

        Assert.Equal(7 * MB, result.Deltas[Dir].OwnBytesDelta);
        Assert.Equal("data.pak", Assert.Single(store.Entries).Name);
    }

    [Fact]
    public void AnUnnameableChangeIsSkippedRatherThanGuessed()
    {
        var journal = new FakeUsnJournal().Add(Change(1, Dir, "", UsnChangeKind.Created));
        var metadata = new FakeMetadataReader().Set(1, 7 * MB); // no path available
        var store = new FakeFileStateStore().WithDirectory(Dir);

        var result = Run(journal, metadata, store);

        Assert.Empty(store.Entries);
        Assert.Empty(result.Deltas);
    }

    [Fact]
    public void DirectoriesCarryNoBytesButAreRecorded()
    {
        var journal = new FakeUsnJournal()
            .Add(Change(900, Dir, "NewFolder", UsnChangeKind.Created, isDirectory: true));

        var metadata = new FakeMetadataReader();
        var store = new FakeFileStateStore().WithDirectory(Dir);

        var result = Run(journal, metadata, store);

        Assert.Empty(result.Deltas);
        Assert.True(store.DirectoryExists(900));
    }

    /// <summary>
    /// A journal that has been reset or has trimmed our cursor away leaves a hole in the history.
    /// A hole is worse than a delay, so the only honest recovery is a full baseline.
    /// </summary>
    [Fact]
    public void AnInvalidatedJournalDemandsABaseline()
    {
        var journal = new FakeUsnJournal { Status = UsnReadStatus.JournalInvalidated };

        var result = Run(journal, new FakeMetadataReader(), new FakeFileStateStore());

        Assert.True(result.RequiresBaseline);
    }

    [Fact]
    public void AMissingJournalDemandsABaseline()
    {
        var journal = new FakeUsnJournal { Status = UsnReadStatus.JournalNotActive };

        Assert.True(Run(journal, new FakeMetadataReader(), new FakeFileStateStore()).RequiresBaseline);
    }

    /// <summary>
    /// A huge backlog must be taken in bounded bites. Draining it synchronously is the stall the
    /// whole design exists to avoid, and the journal is durable, so falling behind is safe.
    /// </summary>
    [Fact]
    public void ABacklogIsCappedAndReportedAsCatchingUp()
    {
        var journal = new FakeUsnJournal();

        for (var i = 0; i < 100; i++)
        {
            journal.Add(Change(i + 1, Dir, $"f{i}.bin", UsnChangeKind.Created));
        }

        var metadata = new FakeMetadataReader();

        for (var i = 0; i < 100; i++)
        {
            metadata.Set(i + 1, MB);
        }

        var result = Run(journal, metadata, new FakeFileStateStore().WithDirectory(Dir),
            new Tier2Options { MaxRecordsPerTick = 10 });

        Assert.Equal(10, journal.LastMaxRecords);
        Assert.True(result.CatchingUp);
        Assert.Equal(10, result.RecordsRead);
    }

    [Fact]
    public void TheCursorAdvancesToTheJournalsNextPosition()
    {
        var journal = new FakeUsnJournal { NextUsn = 987_654 }
            .Add(Change(1, Dir, "a.bin", UsnChangeKind.Created));

        var result = Run(journal, new FakeMetadataReader().Set(1, MB), new FakeFileStateStore().WithDirectory(Dir));

        Assert.Equal(987_654, result.Cursor.NextUsn);
    }

    /// <summary>
    /// The first tick has no stored cursor, so the journal ID has to come from the journal itself.
    /// Echoing the request's ID persisted a zero, which never registers as a set cursor, so every
    /// subsequent tick restarted from the head and no change was ever seen - silently.
    /// </summary>
    [Fact]
    public void TheSavedCursorCarriesTheJournalsOwnIdEvenOnAFirstRun()
    {
        var journal = new FakeUsnJournal { JournalId = 0xABCD, NextUsn = 500 };

        var result = new Tier2Scanner(journal, new FakeMetadataReader())
            .Run(UsnCursor.None, new FakeFileStateStore());

        Assert.Equal(0xABCDul, result.Cursor.JournalId);
        Assert.True(result.Cursor.IsSet);
    }

    /// <summary>
    /// Writing a temporary and renaming it over the original is how atomic saves, extractors and
    /// updaters replace a file, and the replacement's records arrive before the original's deletion.
    /// The original has to come off the totals either way round.
    /// </summary>
    /// <remarks>
    /// Taken the wrong way round, the replacement overwrote the original's row and the deletion that
    /// followed found nothing to subtract. That is how E: came to report 1,446 GB on a disk with
    /// 939 GB used.
    /// </remarks>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void AFileReplacedByARenameOverItGivesUpItsBytes(bool deletionFirst, bool redacted)
    {
        var deletion = Change(1, Dir, redacted ? "" : "save.dat", UsnChangeKind.Deleted);
        var journal = new FakeUsnJournal();

        if (deletionFirst)
        {
            journal.Add(deletion);
        }

        journal
            .Add(Change(2, Dir, redacted ? "" : "save.tmp", UsnChangeKind.Created))
            .Add(Change(2, Dir, redacted ? "" : "save.tmp", UsnChangeKind.RenamedFrom))
            .Add(Change(2, Dir, redacted ? "" : "save.dat", UsnChangeKind.RenamedTo));

        if (!deletionFirst)
        {
            journal.Add(deletion);
        }

        var metadata = new FakeMetadataReader().Set(2, 12 * MB, path: @"\Saves\save.dat");
        var store = new FakeFileStateStore()
            .WithDirectory(Dir)
            .WithEntry(new StoredEntry(Dir, "save.dat", 1, 10 * MB, 10 * MB, false));

        var result = Run(journal, metadata, store);

        Assert.Equal(2 * MB, result.Deltas[Dir].OwnBytesDelta);
        Assert.Equal(0, result.Deltas[Dir].FileCountDelta);

        var entry = Assert.Single(store.Entries);
        Assert.Equal(2, entry.FileId);
    }

    /// <summary>
    /// Rotating a backup renames the original aside before the replacement takes its name. Both
    /// files exist afterwards, so both have to be counted, each once.
    /// </summary>
    [Fact]
    public void RotatingABackupCountsBothFilesOnce()
    {
        var journal = new FakeUsnJournal()
            .Add(Change(2, Dir, "save.tmp", UsnChangeKind.Created))
            .Add(Change(1, Dir, "save.dat", UsnChangeKind.RenamedFrom))
            .Add(Change(1, Dir, "save.bak", UsnChangeKind.RenamedTo))
            .Add(Change(2, Dir, "save.tmp", UsnChangeKind.RenamedFrom))
            .Add(Change(2, Dir, "save.dat", UsnChangeKind.RenamedTo));

        var metadata = new FakeMetadataReader().Set(1, 10 * MB).Set(2, 12 * MB);
        var store = new FakeFileStateStore()
            .WithDirectory(Dir)
            .WithEntry(new StoredEntry(Dir, "save.dat", 1, 10 * MB, 10 * MB, false));

        var result = Run(journal, metadata, store);

        Assert.Equal(12 * MB, result.Deltas[Dir].OwnBytesDelta);
        Assert.Equal(1, result.Deltas[Dir].FileCountDelta);
        Assert.Equal(["save.bak", "save.dat"], store.Entries.Select(e => e.Name).Order());
    }

    /// <summary>
    /// NTFS names compare without case, the stored key does not. A case-only rename must still leave
    /// one entry, or the file is counted twice and its deletion later subtracts it twice.
    /// </summary>
    [Fact]
    public void ARenameThatOnlyChangesCaseLeavesOneEntry()
    {
        var journal = new FakeUsnJournal()
            .Add(Change(1, Dir, "Readme.TXT", UsnChangeKind.RenamedFrom))
            .Add(Change(1, Dir, "readme.txt", UsnChangeKind.RenamedTo));

        var metadata = new FakeMetadataReader().Set(1, 3 * MB);
        var store = new FakeFileStateStore()
            .WithDirectory(Dir)
            .WithEntry(new StoredEntry(Dir, "Readme.TXT", 1, 3 * MB, 3 * MB, false));

        var result = Run(journal, metadata, store);

        Assert.Equal(0, result.Deltas[Dir].OwnBytesDelta);
        Assert.Equal("readme.txt", Assert.Single(store.Entries).Name);
    }

    /// <summary>
    /// Replacing the one link that owned a hardlinked file's bytes must not take the file off the
    /// totals: it is still on disk under its other name, which inherits the bytes.
    /// </summary>
    [Fact]
    public void ReplacingTheOwningLinkPassesTheBytesToTheSurvivingLink()
    {
        var journal = new FakeUsnJournal()
            .Add(Change(2, Dir, "shared.dll", UsnChangeKind.Created))
            .Add(Change(1, Dir, "shared.dll", UsnChangeKind.HardLinkChanged));

        var metadata = new FakeMetadataReader().Set(1, 4 * MB).Set(2, 5 * MB);
        var store = new FakeFileStateStore()
            .WithDirectory(Dir)
            .WithDirectory(OtherDir)
            .WithEntry(new StoredEntry(Dir, "shared.dll", 1, 4 * MB, 4 * MB, IsHardLink: false))
            .WithEntry(new StoredEntry(OtherDir, "shared.dll", 1, 4 * MB, 4 * MB, IsHardLink: true));

        var result = Run(journal, metadata, store);

        var total = result.Deltas.Values.Sum(d => d.OwnBytesDelta);
        Assert.Equal(5 * MB, total);

        var survivor = Assert.Single(store.Entries, e => e.FileId == 1);
        Assert.Equal(OtherDir, survivor.ParentId);
        Assert.False(survivor.IsHardLink);
    }

    /// <summary>
    /// A hardlink record names the link that changed, not where the file lives. Removing the owning
    /// link must keep the file's bytes, on the link that survives, and must not record the file
    /// again at the location it just left.
    /// </summary>
    [Theory]
    [InlineData("owner.dll")]
    [InlineData("")] // redacted
    public void RemovingOneLinkOfAHardlinkedFileKeepsItsBytes(string recordName)
    {
        var journal = new FakeUsnJournal()
            .Add(Change(1, Dir, recordName, UsnChangeKind.HardLinkChanged));

        var metadata = new FakeMetadataReader().Set(1, 4 * MB, linkCount: 1, path: @"\Other\link.dll");
        var store = new FakeFileStateStore()
            .WithDirectory(Dir)
            .WithDirectory(OtherDir)
            .WithEntry(new StoredEntry(Dir, "owner.dll", 1, 4 * MB, 4 * MB, IsHardLink: false))
            .WithEntry(new StoredEntry(OtherDir, "link.dll", 1, 4 * MB, 4 * MB, IsHardLink: true));

        var result = Run(journal, metadata, store);

        Assert.Equal(0, result.Deltas.Values.Sum(d => d.OwnBytesDelta));

        var survivor = Assert.Single(store.Entries);
        Assert.Equal(OtherDir, survivor.ParentId);
        Assert.False(survivor.IsHardLink);
    }

    /// <summary>
    /// A compressed or sparse file takes less space than its length, and the rollup keeps a total of
    /// each. Feeding the logical delta into both made size on disk a second copy of the logical size.
    /// </summary>
    [Fact]
    public void SizeOnDiskMovesByItsOwnDeltaNotTheLogicalOne()
    {
        var journal = new FakeUsnJournal()
            .Add(Change(1, Dir, "new.vhdx", UsnChangeKind.Created))
            .Add(Change(2, Dir, "old.log", UsnChangeKind.Deleted));

        var metadata = new FakeMetadataReader().Set(1, 40 * MB, onDisk: 3 * MB);
        var store = new FakeFileStateStore()
            .WithDirectory(Dir)
            .WithEntry(new StoredEntry(Dir, "old.log", 2, 10 * MB, 2 * MB, false));

        var delta = Run(journal, metadata, store).Deltas[Dir];

        Assert.Equal(30 * MB, delta.OwnBytesDelta);
        Assert.Equal(1 * MB, delta.OwnOnDiskDelta);
    }

    [Fact]
    public void AQuietTickDoesNothingAndCostsNothing()
    {
        var result = Run(new FakeUsnJournal(), new FakeMetadataReader(), new FakeFileStateStore());

        Assert.Equal(UsnReadStatus.Ok, result.Status);
        Assert.Empty(result.Deltas);
        Assert.Equal(0, result.MetadataReads);
    }

    private static Tier2Result Run(
        FakeUsnJournal journal,
        FakeMetadataReader metadata,
        FakeFileStateStore store,
        Tier2Options? options = null)
        => new Tier2Scanner(journal, metadata, options).Run(new UsnCursor(42, 1), store);

    private static UsnChange Change(
        long fileId,
        long parentId,
        string name,
        UsnChangeKind kind,
        bool isDirectory = false)
        => new(0, fileId, parentId, name, kind, isDirectory ? 0x10u : 0x80u, DateTime.UnixEpoch);
}
