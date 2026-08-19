"""Replay a --rollup-log against several dead-band thresholds.

The question: today snapshot_dirs writes all ~5,740 qualifying rows every tick regardless of what
changed. If history instead recorded a directory only once it had drifted past a threshold, how many
rows would a tick actually write?

A dead-band is a stateful filter — the tick that crosses the threshold depends on every tick before
it — so this has to be a replay over the whole log rather than a per-tick count.
"""

import csv
import sqlite3
import sys
from collections import defaultdict, OrderedDict
from datetime import datetime

LOG = sys.argv[1]
DB = sys.argv[2] if len(sys.argv) > 2 else None

KB = 1024
MB = 1024 * KB
BANDS = [0, 64 * KB, 256 * KB, 1 * MB, 4 * MB, 16 * MB]

# Measured on the real database with dbstat, using real NTFS directory ids so the integer widths
# are honest: 26.5 bytes per row for the table, 19.1 more if a time index is added.
BYTES_PER_ROW = 26.5
HOURS_PER_WEEK = 24 * 7
TODAY_ROWS_PER_TICK = 5740       # what snapshot_dirs writes now, on C:
TODAY_BYTES_PER_ROW = 116.5


def human(n):
    """Binary units, labelled as such. Mixing MB and MiB is how a 449 becomes a 429."""
    for unit in ("B", "KiB", "MiB", "GiB"):
        if abs(n) < 1024 or unit == "GB":
            return f"{n:,.1f} {unit}"
        n /= 1024


def load(path):
    ticks = OrderedDict()
    seeds = defaultdict(int)
    touched_rows = 0

    with open(path, newline="", encoding="utf-8") as handle:
        for row in csv.DictReader(handle):
            key = row["tick_utc"]
            ticks.setdefault(key, []).append(
                (int(row["dir_id"]), int(row["delta_bytes"]), int(row["file_delta"]))
            )
            touched_rows += 1
            if row["seed"] == "1":
                seeds[key] += 1

    return ticks, seeds, touched_rows


def replay(ticks, band):
    """Rows written per tick when a directory is recorded only after drifting past `band`."""
    drift = defaultdict(int)
    per_tick = []

    for entries in ticks.values():
        written = 0
        for dir_id, delta, files in entries:
            drift[dir_id] += delta
            # A directory whose file count moved is interesting even at zero net bytes: a rename
            # in and a delete out of the same size is a real event.
            if abs(drift[dir_id]) >= band and (drift[dir_id] != 0 or files != 0):
                written += 1
                drift[dir_id] = 0
        per_tick.append(written)

    return per_tick


def percentile(values, fraction):
    if not values:
        return 0
    ordered = sorted(values)
    return ordered[min(len(ordered) - 1, int(len(ordered) * fraction))]


def depths(db, dir_ids):
    if not db:
        return {}
    connection = sqlite3.connect(f"file:{db}?mode=ro", uri=True)
    found = {}
    for chunk in range(0, len(dir_ids), 900):
        batch = dir_ids[chunk:chunk + 900]
        placeholders = ",".join("?" * len(batch))
        rows = connection.execute(
            f"SELECT dir_id, depth FROM dir_totals WHERE volume_id = 1 AND dir_id IN ({placeholders})",
            batch,
        )
        found.update(dict(rows))
    connection.close()
    return found


ticks, seeds, touched_rows = load(LOG)
tick_count = len(ticks)

moments = [datetime.fromisoformat(t.replace("Z", "+00:00")) for t in ticks]
span_hours = (moments[-1] - moments[0]).total_seconds() / 3600
seed_total = sum(seeds.values())

print(f"sample:        {tick_count} ticks over {span_hours * 60:.0f} min "
      f"({span_hours * 3600 / tick_count:.0f}s apart), {touched_rows:,} touched directory rows")
print(f"seeds:         {seed_total:,} directories changed directly")
print(f"fan-out:       {touched_rows / max(seed_total, 1):.1f}x ancestors per changed directory")
print(f"touched/tick:  mean {touched_rows / tick_count:.0f}, "
      f"median {percentile([len(v) for v in ticks.values()], 0.5)}, "
      f"p95 {percentile([len(v) for v in ticks.values()], 0.95)}, "
      f"max {max(len(v) for v in ticks.values())}")

all_dirs = sorted({d for entries in ticks.values() for d, _, _ in entries})
by_depth = depths(DB, all_dirs)

if by_depth:
    deep = [d for d in all_dirs if by_depth.get(d, 0) > 8]
    print(f"depth:         {len(all_dirs):,} distinct directories, "
          f"{len(deep):,} of them below depth 8 "
          f"({100 * len(deep) / max(len(all_dirs), 1):.0f}% invisible to today's cap), "
          f"deepest {max(by_depth.values(), default=0)}")

print()
print("Projected on elapsed time, not on tick count: rows written per hour is roughly a property")
print("of how much changes on the machine, not of how often it is sampled. Sampling every 57s and")
print("multiplying by a 15-minute cadence would have flattered the result — at 15 minutes more")
print("changes coalesce into one row per directory, so this over-states rather than under-states.")
print()
print(f"{'band':>8} {'rows/tick':>22} {'rows/hr':>9} {'per week':>11} {'vs today':>9}")
print(f"{'':>8} {'mean  med  p95  max':>22}")

# Today writes the full rollup every tick regardless of activity, so its cost is set by the
# snapshot cadence rather than by what happened. 96 snapshots a day is the design's own figure.
today_week = TODAY_ROWS_PER_TICK * 96 * 7 * TODAY_BYTES_PER_ROW
print(f"{'today':>8} {TODAY_ROWS_PER_TICK:>5} {TODAY_ROWS_PER_TICK:>4} "
      f"{TODAY_ROWS_PER_TICK:>4} {TODAY_ROWS_PER_TICK:>4} "
      f"{TODAY_ROWS_PER_TICK * 4:>9,} {human(today_week):>11} {'1x':>9}")

for band in BANDS:
    per_tick = replay(ticks, band)
    written = sum(per_tick)
    per_hour = written / span_hours
    week = per_hour * HOURS_PER_WEEK * BYTES_PER_ROW
    label = "none" if band == 0 else human(band).replace(".0 ", " ")
    print(f"{label:>8} {written / len(per_tick):>5.0f} {percentile(per_tick, 0.5):>4} "
          f"{percentile(per_tick, 0.95):>4} {max(per_tick):>4} "
          f"{per_hour:>9,.0f} {human(week):>11} {today_week / max(week, 1):>8,.0f}x")
