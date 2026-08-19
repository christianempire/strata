-- Replace the per-snapshot directory rollup with a sparse history.
--
-- Measured, not argued. See docs/STORAGE-MODEL.md. `snapshot_dirs` writes all 5,740 qualifying rows
-- every fifteen minutes whether or not anything happened to them, which is 429 MiB a week on C:,
-- and in exchange it cannot see anything below depth 8 or under 10 MB — 55% of the directories that
-- actually changed during a sampled 35 minutes of real work. A mean of 68 directories per tick had
-- their subtree total move at all, at unbounded depth. The table writes 5,740.
--
-- This records a directory only when its total has moved, at any depth and at any size: 18.2 MiB a
-- week for complete coverage, 24x smaller than what it replaces.
--
-- `snapshot_dirs` is left in place and still written for now. The read side moves over first and is
-- checked against it, and only then is it dropped. A storage model is not something to swap blind.

-- The name dimension.
--
-- Names already live in `dir_totals`, and deleted directories already survive there as zero-byte
-- rows, so most of this exists. The gap is that a Tier 1 baseline truncates `dir_totals` and
-- rebuilds it wholesale, which would orphan every history row older than the last baseline. So the
-- names need their own table that a baseline merges into rather than replaces.
--
-- Stores parent_id + name rather than a path, exactly as `files` does and for the same reason: a
-- directory prefix is written once instead of once per descendant, and 61% of `snapshot_dirs` was
-- repeated path text — 8.2 MB of it across only 5,742 distinct paths.
--
-- A directory id is safe to key on across deletion. An NTFS file reference number is a 48-bit MFT
-- index plus a 16-bit sequence number, and the sequence increments when an entry is reused, so a
-- recycled index yields a different 64-bit id rather than a stale name.
CREATE TABLE dir_names (
    volume_id      INTEGER NOT NULL,
    dir_id         INTEGER NOT NULL,
    parent_id      INTEGER NOT NULL,
    name           TEXT    NOT NULL,
    depth          INTEGER NOT NULL,
    first_seen_utc TEXT    NOT NULL,
    PRIMARY KEY (volume_id, dir_id)
) WITHOUT ROWID;

-- Listing a directory's children, which is how both the historical tree walk and path building work.
CREATE INDEX ix_dir_names_parent ON dir_names (volume_id, parent_id);

-- The history itself.
--
-- Absolute values rather than deltas, which costs the same on disk and is strictly better to query:
-- "the size of X at time T" becomes the latest row at or before T. One seek, no anchor arithmetic,
-- no replay, and no drift accumulating across a year of history.
--
-- Clustered on the directory so one directory's history is contiguous — that is the Timeline series
-- query. There is deliberately no secondary index on time: measured over 1.34M rows, a six-hour
-- window scanned in about 25 ms without one against roughly 7 ms with, and the index cost 19.1 bytes
-- a row against the table's own 26.5. That is the trade ix_snapshot_dirs_path lost in migration 004,
-- and it loses again here. Add it if Compare measures slow at real retention volumes, not before.
CREATE TABLE dir_history (
    volume_id     INTEGER NOT NULL,
    dir_id        INTEGER NOT NULL,
    taken_unix    INTEGER NOT NULL,
    subtree_bytes INTEGER NOT NULL,
    subtree_files INTEGER NOT NULL,
    PRIMARY KEY (volume_id, dir_id, taken_unix)
) WITHOUT ROWID;

-- What was last written to history for this directory, so a tick can tell whether it has drifted
-- far enough to be worth another row. Kept here rather than in a side table because every tick
-- already reads and writes this row to maintain the rollup.
--
-- Seeded below to the current total, so existing volumes start recording changes from now rather
-- than writing 571k rows on their next tick.
ALTER TABLE dir_totals ADD COLUMN recorded_bytes INTEGER NOT NULL DEFAULT 0;

-- Seed the name dimension from what is currently known.
INSERT OR IGNORE INTO dir_names (volume_id, dir_id, parent_id, name, depth, first_seen_utc)
SELECT volume_id, dir_id, parent_id, name, depth, ''
FROM dir_totals;

-- And an opening row per directory, so that drilling into a directory that has never changed still
-- has a size to report rather than looking as though it did not exist. This is why the anchor covers
-- every directory and not only the large ones: the Explorer drills down, and it can drill anywhere.
--
-- Timestamped 0 so it sorts before any real tick and is unambiguously the seed rather than a
-- measurement taken at a particular moment.
INSERT OR IGNORE INTO dir_history (volume_id, dir_id, taken_unix, subtree_bytes, subtree_files)
SELECT volume_id, dir_id, 0, subtree_bytes, subtree_files
FROM dir_totals;

UPDATE dir_totals SET recorded_bytes = subtree_bytes;
