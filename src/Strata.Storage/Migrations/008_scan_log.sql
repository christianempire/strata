-- Every scheduler tick, whether it ran or not.
--
-- The brief asks Settings for "an auditable scan-history log: every tick, whether it ran or
-- deferred, why it deferred, and how long it took". Nothing recorded that. Deferrals were logged to
-- the console at Debug and then forgotten, which makes the one claim the whole project rests on —
-- that the scanner stays out of the way — unverifiable after the fact.
--
-- A tick that did not run is the most interesting row in the table, so a row is written for it too.
-- Silence is not evidence of good behaviour.
CREATE TABLE scan_log (
    id          INTEGER PRIMARY KEY AUTOINCREMENT,

    -- When the scheduler cycle that produced this row began, so the rows belonging to one tick group
    -- exactly rather than by proximity. A cycle covering three volumes writes three rows.
    cycle_utc   TEXT    NOT NULL,

    when_utc    TEXT    NOT NULL,

    -- Nullable, and paired with the display name, because a volume can be attempted before it has a
    -- row: a baseline of a never-seen volume writes its scan_log row before VolumeRepository.Upsert
    -- has given it an id, and a forgotten volume should not take its own history with it.
    volume_id   INTEGER,
    volume      TEXT    NOT NULL,

    -- 1 full, 2 incremental. Matches Strata.Core.Scanning.ScanTier.
    tier        INTEGER NOT NULL,

    -- 1 ran, 2 deferred, 3 failed.
    result      INTEGER NOT NULL,

    -- Why it deferred, or what it did. Free text, shown verbatim in the UI: the value of this log is
    -- that "fullscreen exclusive app: Cyberpunk2077.exe" reads as a decision and an error code does
    -- not.
    reason      TEXT    NOT NULL DEFAULT '',

    duration_ms INTEGER NOT NULL DEFAULT 0,
    records     INTEGER NOT NULL DEFAULT 0,
    events      INTEGER NOT NULL DEFAULT 0
);

-- ISO-8601 rather than Unix seconds, against the convention the large tables follow. Three volumes
-- at a 15-minute cadence is 288 rows a day and 105k a year, which is four orders of magnitude short
-- of the millions that made the timestamp width worth caring about in `files`. This is also the one
-- table someone opens in a SQLite browser on purpose — it exists to be audited — so legibility is
-- the property to optimise for.

CREATE INDEX ix_scan_log_when ON scan_log (when_utc);
