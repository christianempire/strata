-- Strata schema, migration 001.
--
-- The organising idea: current state is stored once and mutated in place, while history is stored
-- as aggregates. Keeping a full file tree per snapshot would produce gigabytes within a week, which
-- is the specific failure this schema exists to avoid.

-- Volumes are identified by their GUID path, never by drive letter. Letters get reassigned, and
-- letter-keyed history would silently corrupt if two volumes ever swapped.
CREATE TABLE volumes (
    id             INTEGER PRIMARY KEY AUTOINCREMENT,
    guid_path      TEXT    NOT NULL UNIQUE,
    drive_letter   TEXT    NULL,
    label          TEXT    NOT NULL DEFAULT '',
    file_system    TEXT    NOT NULL DEFAULT '',
    kind           INTEGER NOT NULL DEFAULT 0,
    serial_number  INTEGER NOT NULL DEFAULT 0,
    disk_number    INTEGER NULL,
    total_bytes    INTEGER NOT NULL DEFAULT 0,
    free_bytes     INTEGER NOT NULL DEFAULT 0,
    first_seen_utc TEXT    NOT NULL,
    last_seen_utc  TEXT    NOT NULL
);

-- Current state only: one row per file, updated in place by Tier 2 deltas.
--
-- Stores parent_id + name rather than a path string. A directory prefix is then stored once instead
-- of once per descendant, and renaming a directory is an O(1) update instead of a subtree rewrite.
-- Paths are reconstructed on demand by walking parent links.
--
-- WITHOUT ROWID because the primary key is the natural access path and the table is large enough
-- that the extra rowid indirection is real overhead.
CREATE TABLE files (
    volume_id     INTEGER NOT NULL,
    file_id       INTEGER NOT NULL,
    parent_id     INTEGER NOT NULL,
    name          TEXT    NOT NULL,
    logical_size  INTEGER NOT NULL,
    size_on_disk  INTEGER NOT NULL,
    attributes    INTEGER NOT NULL,
    modified_utc  TEXT    NOT NULL,
    -- An additional link to bytes already counted at another path. Counts zero toward rollups so
    -- that directory totals sum exactly to the volume total.
    is_hard_link  INTEGER NOT NULL DEFAULT 0,
    PRIMARY KEY (volume_id, file_id)
) WITHOUT ROWID;

CREATE INDEX ix_files_parent ON files (volume_id, parent_id);

-- Directory subtree totals, maintained wholesale by Tier 1 and — from Phase 2 — incrementally by
-- Tier 2 walking the ancestor chain of each changed file.
--
-- Without this, producing a snapshot would mean a recursive aggregation over every row in `files`
-- on every 15-minute tick, which would blow the Tier 2 budget for reasons unrelated to the journal.
CREATE TABLE dir_totals (
    volume_id       INTEGER NOT NULL,
    dir_id          INTEGER NOT NULL,
    parent_id       INTEGER NOT NULL,
    name            TEXT    NOT NULL,
    depth           INTEGER NOT NULL,
    subtree_bytes   INTEGER NOT NULL,
    subtree_on_disk INTEGER NOT NULL,
    subtree_files   INTEGER NOT NULL,
    subtree_dirs    INTEGER NOT NULL,
    PRIMARY KEY (volume_id, dir_id)
) WITHOUT ROWID;

CREATE INDEX ix_dir_totals_parent ON dir_totals (volume_id, parent_id);

-- One row per completed scan. Small, and kept at full resolution far longer than the rollups.
CREATE TABLE snapshots (
    id                 INTEGER PRIMARY KEY AUTOINCREMENT,
    volume_id          INTEGER NOT NULL REFERENCES volumes (id),
    taken_utc          TEXT    NOT NULL,
    scan_tier          INTEGER NOT NULL,
    duration_ms        INTEGER NOT NULL,
    total_bytes        INTEGER NOT NULL,
    total_on_disk      INTEGER NOT NULL,
    free_bytes         INTEGER NOT NULL,
    file_count         INTEGER NOT NULL,
    dir_count          INTEGER NOT NULL,
    -- Recorded so a total that disagrees with Explorer's has a visible explanation rather than
    -- looking like a bug.
    inaccessible_count INTEGER NOT NULL DEFAULT 0,
    hard_link_count    INTEGER NOT NULL DEFAULT 0,
    hard_link_bytes    INTEGER NOT NULL DEFAULT 0,
    reparse_count      INTEGER NOT NULL DEFAULT 0
);

CREATE INDEX ix_snapshots_volume_taken ON snapshots (volume_id, taken_utc);

-- The historical rollup, and the table that decides whether this design is affordable.
--
-- Limited by a depth cap and a size floor. The path is denormalised on purpose: file IDs churn, and
-- history has to stay readable after the directory itself is gone.
--
-- Open question: whether the default cap and floor keep this affordable at 96 snapshots a day.
-- See docs/DESIGN-REVIEW.md section A5. Phase 1 measures it.
CREATE TABLE snapshot_dirs (
    snapshot_id     INTEGER NOT NULL REFERENCES snapshots (id) ON DELETE CASCADE,
    dir_id          INTEGER NOT NULL,
    path            TEXT    NOT NULL,
    depth           INTEGER NOT NULL,
    subtree_bytes   INTEGER NOT NULL,
    subtree_on_disk INTEGER NOT NULL,
    subtree_files   INTEGER NOT NULL,
    PRIMARY KEY (snapshot_id, dir_id)
) WITHOUT ROWID;

-- The largest files per snapshot, so "what single file ate 8 GB" stays answerable after that file
-- has been deleted. Schema exists from Phase 1; populated from Phase 2 with the diff engine.
CREATE TABLE snapshot_top_files (
    snapshot_id  INTEGER NOT NULL REFERENCES snapshots (id) ON DELETE CASCADE,
    rank         INTEGER NOT NULL,
    path         TEXT    NOT NULL,
    logical_size INTEGER NOT NULL,
    size_on_disk INTEGER NOT NULL,
    PRIMARY KEY (snapshot_id, rank)
) WITHOUT ROWID;

-- The derived, human-readable change log. This is what the Timeline screen reads from, it is small,
-- and it keeps full resolution when the directory rollups get downsampled.
-- Written by the diff engine in Phase 2.
CREATE TABLE events (
    id           INTEGER PRIMARY KEY AUTOINCREMENT,
    volume_id    INTEGER NOT NULL REFERENCES volumes (id),
    snapshot_id  INTEGER NOT NULL REFERENCES snapshots (id) ON DELETE CASCADE,
    when_utc     TEXT    NOT NULL,
    path         TEXT    NOT NULL,
    delta_bytes  INTEGER NOT NULL,
    -- Net change and churn are tracked separately: a directory that wrote and deleted 50 GB has a
    -- net near zero but is very interesting for SSD wear.
    churn_bytes  INTEGER NOT NULL DEFAULT 0,
    file_delta   INTEGER NOT NULL DEFAULT 0,
    -- 1 grew, 2 shrank, 3 appeared, 4 vanished, 5 moved
    kind         INTEGER NOT NULL
);

CREATE INDEX ix_events_when ON events (when_utc);
CREATE INDEX ix_events_volume_when ON events (volume_id, when_utc);

-- Per-volume cursor for the NTFS change journal, written after every successful Tier 2 read.
-- Unused in Phase 1; created now so Phase 2 does not need a migration to start.
CREATE TABLE usn_cursors (
    volume_id  INTEGER PRIMARY KEY REFERENCES volumes (id),
    journal_id INTEGER NOT NULL,
    next_usn   INTEGER NOT NULL,
    updated_utc TEXT   NOT NULL
);
