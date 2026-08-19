-- Re-key `files` on the directory entry rather than on the file.
--
-- Migration 001 used PRIMARY KEY (volume_id, file_id). That is wrong, and Phase 1's measurements
-- showed exactly how wrong: 135,520 of 3.67M entries on the target machine collided and were lost,
-- because hardlinks share a file ID. A file reachable by four paths was stored once, so three of
-- its paths simply did not exist in the database.
--
-- The correct key is the one the filesystem itself uses: a directory entry is identified by its
-- parent directory and its name. A file ID is an attribute of that entry (which inode it points
-- at), not its identity. Two entries can share a file ID; two entries in the same directory can
-- never share a name.
--
-- This also matches what the change journal gives us. A USN record carries
-- ParentFileReferenceNumber + FileName, so every record addresses exactly one row by primary key,
-- with no lookup needed.
--
-- Third benefit: clustering by (volume_id, parent_id, name) makes "list the children of this
-- directory" a contiguous range scan that reads whole rows. Under the old key it was a secondary
-- index scan followed by a lookup per row, and that query is the one the Explorer screen runs
-- constantly.
--
-- The cost is a secondary index on (volume_id, file_id), needed to fan out a size change across
-- every link of the same file. In a WITHOUT ROWID table that index carries the full primary key,
-- which is not cheap. Measured cost is recorded in docs/MEASUREMENTS.md.
--
-- `files` holds current state only and is fully rebuilt by a baseline scan, so this drops and
-- recreates rather than migrating rows across. Volumes are marked as needing a baseline instead.

DROP INDEX IF EXISTS ix_files_parent;
DROP TABLE IF EXISTS files;

CREATE TABLE files (
    volume_id     INTEGER NOT NULL,
    -- The directory holding this entry.
    parent_id     INTEGER NOT NULL,
    name          TEXT    NOT NULL,
    -- Which file this entry points at. Shared between hardlinks, hence not part of the key.
    file_id       INTEGER NOT NULL,
    logical_size  INTEGER NOT NULL,
    size_on_disk  INTEGER NOT NULL,
    attributes    INTEGER NOT NULL,
    -- Unix seconds. An ISO-8601 string costs 28 bytes against 8, which measured at ~95 MB across
    -- three volumes on the target machine.
    modified_unix INTEGER NOT NULL,
    -- 0 for the entry that owns the bytes, 1 for every additional link to the same file. Rollups
    -- count only owners, so directory totals sum exactly to the volume total.
    is_hard_link  INTEGER NOT NULL DEFAULT 0,
    PRIMARY KEY (volume_id, parent_id, name)
) WITHOUT ROWID;

-- Fan-out: given a file whose data changed, find every entry pointing at it.
CREATE INDEX ix_files_file_id ON files (volume_id, file_id);

-- When a volume last had a full baseline scan. NULL means it needs one before Tier 2 can run,
-- since an incremental delta against no baseline is meaningless.
ALTER TABLE volumes ADD COLUMN baseline_utc TEXT NULL;

-- Existing volumes predate this table shape, so their file rows are gone and they need a baseline.
UPDATE volumes SET baseline_utc = NULL;
