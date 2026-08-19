# A better shape for directory history

Written after Phase 3a, when measuring the read side showed what `snapshot_dirs` actually costs.
Numbers are from the target machine, 19 Aug 2026. Nothing here is built yet.

**Verdict up front: the current table is the wrong shape, and replacing it is worth doing before
Phase 4 rather than after.** It costs 429 MiB a week on C: alone, and in exchange it cannot see more
than half of what changes on the machine. A sparse model measured **24× smaller with complete
coverage**, and it makes the queries simpler rather than harder.

---

## What is wrong with `snapshot_dirs`

Three separate mistakes that happen to compound.

### 1. It writes everything, every tick

Per snapshot it records every directory within a depth cap and above a size floor, whether or not
anything happened to it. On C: that is 5,740 rows every fifteen minutes — 672 snapshots a week —
to record that a handful of folders moved.

**Measured over 35 minutes of real activity: a mean of 68 directories per tick had their subtree
total change at all, at unbounded depth.** The table writes 5,740.

### 2. It stores the path in every row

Measured with `dbstat` over 114,812 real rows: **8.2 MB of the 13.4 MB table is path text**, 61% of
it, and there are only **5,742 distinct paths**. Every path is already stored 20 times in this
database, and would be stored 672 times a week.

The path is denormalised on purpose — history has to stay readable after the directory is gone — but
that is an argument for storing each path once, not for storing it per row per snapshot.

### 3. The floor is on the wrong quantity

A directory qualifies on **absolute size**, so a 10 MB folder that never moves costs exactly as much
to record as a 10 MB folder that grows into 40 GB. What anyone actually wants to track is the second
one, and the schema cannot tell them apart.

The depth cap has the same problem in the other direction. It is 8, and the **mean depth of a C:
directory over 10 MB is 8.83**, with the tree reaching depth 27. Sampled against real activity,
**55% of the directories that changed were below depth 8** — invisible, at any storage budget.

---

## How this was measured

`strata watch --rollup-log <path>` appends every directory whose subtree total moved, per tick, as
CSV. `IncrementalRepository.ApplyDeltas` already computes exactly that set in order to do the update,
so returning it costs nothing.

The log is raw rather than pre-aggregated because a dead-band is a stateful filter: the tick on which
a directory crosses the threshold depends on every tick before it. Only a full log can be replayed
against several thresholds afterwards, and replaying is the point.

**Sample: 37 ticks over 35 minutes on C:, 4,135 touched directory rows.** It deliberately includes
the awkward cases — two full `dotnet clean` + rebuild cycles, a 1.44 GB install-shaped tree written
into fresh directories including one 20 levels deep, an 800 MB second wave, and then deleting all of
it.

**Caveats, all of which point the same way.** The sample is short, from one volume on one machine,
and it does not include a real 100 GB game install or a Windows feature update. It was taken at 57
second intervals and projected on elapsed time rather than on tick count, because rows written per
hour is a property of how much changes on the machine rather than of how often it is sampled — and
at the real 15-minute cadence more changes coalesce into one row per directory, so this **over**
states the cost. The artificial churn is heavier than a typical 35 minutes. The estimate is
conservative in every direction I could find.

---

## The measurements

### Fan-out

| | |
|---|---:|
| Directories changed directly (seeds) | 1,767 |
| Directories touched including ancestors | 4,135 |
| **Ancestor fan-out** | **2.3×** |
| Touched per tick — mean / median / p95 / max | 112 / 78 / 225 / 679 |
| Distinct directories in the sample | 987 |
| **Below depth 8, so invisible today** | **545 (55%)** |
| Deepest touched | 24 |

The 2.3× fan-out is lower than I expected. Changed files cluster, so ancestor chains overlap heavily
and a shared ancestor is one row, not one per descendant.

### Row size, measured not estimated

A prototype table filled with **real NTFS directory ids**, which matters: a file reference number is
around 2^50, so SQLite stores it in 7 or 8 bytes and a synthetic small integer would have flattered
the result. Measured with `dbstat`.

| Shape | Bytes per row |
|---|---:|
| `snapshot_dirs` today | **116.5** |
| Proposed row, `WITHOUT ROWID`, clustered on the directory | **26.5** |
| …plus a secondary index on time | +19.1 |
| Same data in an ordinary rowid table with both indexes | 63.1 |

### What a week costs

Projected on elapsed time from the sample.

| Recording rule | rows/tick (mean) | rows/hour | Per week, C: | vs today |
|---|---:|---:|---:|---:|
| **Today** — full rollup every tick | 5,740 | 22,960 | **428.6 MiB** | 1× |
| **Every touched directory, no band** | 68 | 4,284 | **18.2 MiB** | **24×** |
| Dead-band 64 KiB | 33 | 2,081 | 8.8 MiB | 49× |
| Dead-band 256 KiB | 23 | 1,454 | 6.2 MiB | 69× |
| Dead-band 1 MiB | 15 | 952 | 4.0 MiB | 106× |
| Dead-band 4 MiB | 6 | 380 | 1.6 MiB | 265× |
| Dead-band 16 MiB | 4 | 279 | 1.2 MiB | 362× |

**The important row is the second one.** Recording *every* directory whose total moved, at unbounded
depth and with no size floor at all, costs 18.2 MiB a week against today's 428.6 MiB. The dead-band
turns out to be a tuning knob rather than a necessity — the win comes almost entirely from writing
what changed instead of writing everything, and from not repeating the path.

---

## Proposed schema

### `dir_history` — sparse, absolute, clustered on the directory

```sql
CREATE TABLE dir_history (
    volume_id     INTEGER NOT NULL,
    dir_id        INTEGER NOT NULL,
    taken_unix    INTEGER NOT NULL,
    subtree_bytes INTEGER NOT NULL,
    subtree_files INTEGER NOT NULL,
    PRIMARY KEY (volume_id, dir_id, taken_unix)
) WITHOUT ROWID;
```

**Absolute values, not deltas.** "The size of X at time T" is then *the latest row at or before T* —
one seek, no anchor arithmetic, no replay, and no drift accumulating over a year of history. Deltas
would be the same size on disk and strictly worse to query.

**A row is written when the directory has moved by more than a band since its last recorded row**,
with the band configurable and possibly zero. Any reported size is then within one band of the truth,
and the volume total stays exact regardless because `snapshots` records it directly.

### `dir_names` — the dimension the paths come from

Names already live in `dir_totals` as `parent_id` + `name`, and deleted directories already survive
there as zero-byte rows, so most of this exists. **The gap is that a Tier 1 baseline truncates
`dir_totals` and rebuilds it**, which would orphan every history row older than the last baseline.

So the name dimension has to be its own append-only table that a baseline merges into rather than
replaces, carrying `first_seen` and `last_seen`. Paths reconstruct by walking parent links, exactly
as they do everywhere else in this schema.

One-time cost on C:: 571k rows at roughly 30 bytes, about 17 MiB. It does not grow with snapshots.

### A daily anchor, for the historical treemap only

Every other query is served by a seek. Rendering the *whole tree* as it stood at time T is the
exception — it would need the latest row at or before T for each of 571k directories.

A once-daily full row set for directories over the floor at any depth (**13,409 on C:**, from the
Phase 1 histogram) makes that a read of the anchor plus whatever changed since. **355 KiB a day,
2.4 MiB a week.**

**Total: roughly 21 MiB a week on C:, against 428.6 MiB, with complete depth coverage.**

## How each query is served

| Question | Served by |
|---|---|
| Size of directory X over time (Timeline series) | Range scan on the primary key — X's rows are contiguous |
| Size of X at time T | Seek to the latest row at or before T |
| What changed between T1 and T2 (Compare) | The set of directories with a row in that window |
| The whole tree as it stood at T (historical treemap) | Nearest daily anchor, plus rows since |
| Where are the bytes now (Explorer) | `dir_totals`, unchanged — this is not a history query |

The Compare query is the one that wants a secondary index on time. **Measured, it does not need one
yet**: over 1.34M rows a six-hour window scanned in about 25 ms without it, against roughly 7 ms
with. The index costs 19.1 bytes a row, a 72% overhead on the fastest-growing table in the schema.
Leave it off, and add it only if Compare measures slow at real retention volumes — this is exactly
the trade that `ix_snapshot_dirs_path` lost in migration 004.

---

## Risks and open questions

**A baseline truncating the name dimension** is the one that would quietly corrupt history, and it is
the reason `dir_names` has to be a separate append-only table rather than a flag on `dir_totals`.

**Retention has to preserve the last row before the horizon.** With absolute values, "size at T" is
the latest row at or before T, so a downsample that simply deletes old rows would leave a directory
with no row before the window and silently answer with a newer value. Downsampling becomes "keep the
newest row per directory per bucket". This is Phase 4 work, but the schema has to anticipate it.

**Directory id recycling.** NTFS file reference numbers are a 48-bit MFT index plus a 16-bit sequence
number, and the sequence increments when an entry is reused, so a recycled index produces a different
64-bit id. That should make stale names impossible on NTFS, but it is an assumption worth testing
rather than trusting, and Tier 3 volumes use a synthesised key with no such guarantee.

**The tail is not well sampled.** The worst tick in the sample touched 679 directories and wrote 455
rows. A 100 GB game install across 5,000 directories would be roughly 11,500 rows once — about
300 KiB, which is nothing — but a Windows feature update servicing the component store has not been
observed. Since `dir_totals` stays exact regardless, deferring rows under load is safe and
self-healing: the band is still exceeded on the next tick, and there is no cursor to corrupt.

**One volume, 35 minutes.** D: and E: have no baseline yet, and a longer sample across all three
would firm up the projection. The instrument is committed, so this is a matter of leaving it running.

---

## Recommendation

Do it, and do it before Phase 4 rather than after, so retention gets written once against the model
it will actually keep.

It does not block the UI: `/api/tree?snapshot=` and `/api/diff` keep their contracts and get
reimplemented underneath. Suggested order:

1. Leave `--rollup-log` running for a day across all three volumes and re-run the replay, to confirm
   the projection against a real day rather than a busy 35 minutes.
2. Migration: `dir_names` and `dir_history`, both written alongside `snapshot_dirs`.
3. Move the read side over, verify `/diff` and `/tree?snapshot=` agree with the old tables.
4. Drop `snapshot_dirs`.

Step 2 writing both is what makes step 3 checkable rather than a leap.
