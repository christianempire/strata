-- Settings, and the two indexes the read API needs.
--
-- Phase 3 is the first thing to read this database rather than write it, and it exposes two gaps.

-- Settings were command line flags through Phases 1 and 2, which is fine for a console verb and
-- useless for a resident process configured from a UI. Key/value rather than a row of typed columns
-- because the set will keep changing through Phase 4 (retention, downsampling, tray behaviour) and a
-- migration per knob is not worth it. Values are invariant-culture text; lists are JSON arrays.
-- Small table, so the ISO-8601 timestamp convention applies here.
CREATE TABLE settings (
    key         TEXT PRIMARY KEY,
    value       TEXT NOT NULL,
    updated_utc TEXT NOT NULL
) WITHOUT ROWID;

-- Finding a volume's root directory means finding its depth-0 row, and nothing indexed depth. On
-- 571k rows that planned as a full scan of the table, which is not a price worth paying on the first
-- query of every /tree request. Partial, so it holds one row per volume and costs a few hundred
-- bytes rather than indexing a column whose value is 0 for almost nothing.
CREATE INDEX ix_dir_totals_root ON dir_totals (volume_id) WHERE depth = 0;

-- /tree in historical mode walks snapshot_dirs by path prefix, which the (snapshot_id, dir_id)
-- primary key can only answer by scanning that snapshot's rows and comparing strings.
--
-- Dropped again in migration 004. Timed on the real database, the scan and the seek both took 21 ms,
-- because one snapshot is only 5,740 rows and the primary key already clusters them. The index cost
-- 87% of the table's own size on the one table that grows with the number of snapshots. Left here
-- rather than edited out, because an applied migration is never edited.
CREATE INDEX ix_snapshot_dirs_path ON snapshot_dirs (snapshot_id, path);
