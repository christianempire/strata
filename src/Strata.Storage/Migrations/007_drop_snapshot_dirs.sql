-- Drop the per-snapshot directory rollup, now that the sparse history has replaced it.
--
-- Migration 006 carried its rows across at their real timestamps, and the two were checked against
-- each other before this: across the last two rolled-up snapshots on the target machine, all 29
-- changed directories agreed exactly.
--
-- What it cost, measured on C: 116.4 bytes a row, 5,740 rows every tick whether or not anything
-- had happened to them, 429 MiB a week. What it could see: nothing below depth 8 or under 10 MB,
-- which sampling put at 55% of the directories that actually change.

DROP TABLE snapshot_dirs;
