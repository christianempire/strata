-- Remove the index migration 003 added on snapshot_dirs (snapshot_id, path).
--
-- It was added by reasoning about a query plan rather than by timing the query, which this project
-- has a rule against for exactly this reason. Timed on the real database, both forms answer a
-- subtree read of C:\Users in the same 21 ms, because the primary key already clusters a snapshot's
-- rows together and one snapshot is only 5,740 of them. There was nothing to speed up.
--
-- The cost was not nothing. Measured with dbstat: the index held 101 bytes per row against the
-- table's 117, so it was adding 87% to the size of the one table in the schema that grows with the
-- number of snapshots. At a fifteen-minute cadence on C: alone that is roughly 390 MB per week of
-- index for no measured benefit.
--
-- The prefix range predicate stays as it is: it reads the same rows either way, and it is written as
-- a half-open range rather than LIKE so that "C:\Program Files" cannot swallow "C:\Program Files 2".

DROP INDEX IF EXISTS ix_snapshot_dirs_path;
