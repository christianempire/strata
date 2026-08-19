-- Carry the existing per-snapshot rollup into the sparse history.
--
-- Migration 005 seeded one opening row per directory so that every directory has a size at every
-- moment, which is what the Explorer needs to drill anywhere. But it dated that row at the epoch and
-- gave it the directory's size *now*, which back-dates the present across the whole of recorded
-- history: a comparison spanning the migration would read every pre-existing snapshot as though the
-- volume had always been the size it is today, and answer "nothing changed" rather than "I do not
-- know". A wrong answer is worse than a missing one.
--
-- snapshot_dirs already holds real measurements at real times, so they are migrated rather than
-- discarded. Every row becomes a history row at its snapshot's timestamp.
--
-- What this cannot recover is the history of directories snapshot_dirs never recorded — anything
-- below its depth cap of 8 or under its 10 MB floor, which sampling put at 55% of the directories
-- that actually change. Those keep only their epoch-dated seed. That is a one-off consequence of
-- the old model's bounds, not a property of the new one, and it applies only to comparisons that
-- span this migration.
--
-- Timestamps are stored as ISO-8601 with a seven-digit fraction, which SQLite's strftime will not
-- parse; the first nineteen characters are the second-resolution prefix it does understand, and
-- the history is second-resolution anyway.
INSERT OR IGNORE INTO dir_history (volume_id, dir_id, taken_unix, subtree_bytes, subtree_files)
SELECT s.volume_id,
       d.dir_id,
       CAST(strftime('%s', substr(s.taken_utc, 1, 19)) AS INTEGER),
       d.subtree_bytes,
       d.subtree_files
FROM snapshot_dirs d
JOIN snapshots s ON s.id = d.snapshot_id
WHERE s.taken_utc <> ''
  AND strftime('%s', substr(s.taken_utc, 1, 19)) IS NOT NULL;
