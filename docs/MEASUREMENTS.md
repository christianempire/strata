# Measurements

All figures from the target machine, 19 Aug 2026.

**Machine:** Intel i9-14900K (24 cores / 32 threads), 96 GB RAM, three NVMe SSDs, Windows 11 Pro
26200. **Build:** Release, .NET 9, run **unelevated**.

Phase 1 exists to produce these numbers before the rest of the design is committed to. Everything
below is a measurement, not an estimate, except where explicitly marked.

---

## The volumes

| Volume | Label | FS | Disk | Capacity | Used | Files | Directories | Max depth |
|---|---|---|---|---:|---:|---:|---:|---:|
| C: | OS-M2-1TB | NTFS | 2 | 930.6 GB | 875.7 GB | 3,055,124 | 570,639 | 27 |
| D: | Storage-M2-1TB | NTFS | 0 | 931.5 GB | 853.8 GB | 189,776 | 10,421 | 14 |
| E: | Loadstar-M2-1TB | NTFS | 1 | 931.5 GB | 519.2 GB | 423,063 | 24,525 | 15 |
| - | (recovery) | NTFS | - | 790 MB | 730.8 MB | - | - | - |
| - | (EFI) | FAT32 | - | 96 MB | 34.6 MB | - | - | - |

**The single most important structural fact: scan cost tracks directory count, not capacity.**
D: holds 853 GB and scans in well under a second, because it is 10k directories of very large files.
C: holds a comparable 875 GB across 570k directories and takes a hundred times longer. Any future
tuning should be aimed at cost per directory.

---

## Scan duration

### Cold cache (first scan after boot, nothing in the NTFS metadata cache)

| Volume | Workers | Duration | Throughput |
|---|---:|---:|---:|
| C: | 4 | **211.7 s** | 14,432 files/s |
| E: | 4 | 3.2 s | 130,837 files/s |
| D: | 8 | 0.7 s | 286,844 files/s |

### Warm cache

| Volume | Workers | Duration |
|---|---:|---:|
| C: | 2 | 19.30 s |
| C: | 4 | 11.69 s |
| C: | 8 | 8.92 s |
| C: | 16 | 7.74 s |
| C: | 24 | 7.92 s |

**Full baseline, all three volumes, warm, 8 workers: 17.9 s scan + 4.8 s storage flush.**

### What this means

**The brief's 30–90 s estimate for a full volume holds warm, and is wrong cold.** A cold scan of C:
took 211 s at four workers - over three minutes. That is the number that matters, because a Tier 1
scan is by definition the thing that runs on first sight of a volume, which is exactly when nothing
is cached.

Cold directory opens are roughly 30× more expensive than warm ones. That gap, not CPU, is what a
cold scan spends its time on.

Two consequences for Phase 2:

1. **First-run UX has to assume minutes, not seconds.** A three-minute first scan is acceptable if
   the user is told it is happening and it stays invisible; it is not acceptable as an unexplained
   freeze.
2. **Cold cost is latency-bound, so concurrency is the lever.** The cold C: scan was measured at
   four workers only. It is worth re-measuring cold at eight after a reboot before settling the
   default - see "Not yet measured" below.

### Caveats

- Cold numbers are single observations. The NTFS metadata cache cannot be dropped without a reboot,
  so each volume offers one cold data point per boot and these are not averaged.
- The warm series is clean: all five runs were back to back on a fully warm cache.
- C: has Defender and whatever filter drivers Windows stacks on the system volume; D: and E: do not.
  Some of C:'s per-directory cost is likely filter overhead rather than raw I/O.

---

## Worker count

Measured warm on C:, which is the only volume with enough directories to say anything:

```
2 workers   19.30 s   ████████████████████
4 workers   11.69 s   ████████████
8 workers    8.92 s   █████████
16 workers   7.74 s   ████████
24 workers   7.92 s   ████████
```

The brief specified `min(4, ProcessorCount / 2)`, which on this machine is 4. **That is well short
of the knee.** Directory enumeration is latency-bound rather than bandwidth-bound, so it hides
latency with concurrency, and four workers leaves roughly a third of the achievable throughput on
the table.

**Changed the default to `min(8, ProcessorCount / 4)`**, which is 8 here. Sixteen is measurably
faster, by about 15%, but doubles concurrent I/O against a machine the user is trying to game on.
For a scan that runs rarely, that trade is not worth it. Twenty-four is no faster than sixteen.

### Background I/O priority costs nothing measurable

| Configuration | Duration |
|---|---:|
| 8 workers, `THREAD_MODE_BACKGROUND_BEGIN` on | 7.84 s |
| 8 workers, off | 7.93 s |

On an idle NVMe there is no measurable penalty for running the scan at very low I/O priority, which
means the guard is free to leave on permanently. This says nothing about its benefit under
contention, which is the case it exists for and which is not measurable without a game running.

---

## Database size

Across all three volumes, 3.67M files:

| | Size |
|---|---:|
| Initial schema | 490.9 MB |
| **After storing timestamps as integers** | **395.7 MB** |

That works out at roughly **108 bytes per file row**, dominated by the file name (25 characters on
average) plus the row and index overhead of a `WITHOUT ROWID` table.

**This is a one-time cost, not a growth rate.** `files` holds current state only: Tier 2 updates it
in place, so it grows with the number of files on the machine, not with the number of snapshots.

### The 19% saving

`files.modified_utc` was originally an ISO-8601 string per the project's own convention. At 28 bytes
against 8 for an integer, across 3.5M rows, that convention was costing 95 MB. Now stored as Unix
seconds. Text timestamps remain everywhere else, where the tables are small and legibility in a
SQLite browser is worth more than the bytes.

### Row counts

| Table | Rows |
|---|---:|
| `files` | 3,532,313 |
| `dir_totals` | 605,553 |
| `snapshot_dirs` | 4,502 |
| `snapshots` | 3 |

`files` holds 135,520 fewer rows than the 3,667,833 files counted, which matches the hardlink count
almost exactly - see the known limitation below.

---

## Storage growth: the question the brief was most worried about

`snapshot_dirs` is written once per snapshot. With snapshots every 15 minutes and seven days kept at
full resolution, that is 672 snapshots per week, so its per-snapshot row count multiplies by nearly
seven hundred.

**Measured, at the brief's defaults of depth ≤ 6 and a 10 MB floor:**

| Volume | Qualifying directories | Per snapshot | Per week |
|---|---:|---:|---:|
| C: | 2,984 | 262 KB | 172 MB |
| D: | 1,071 | 94 KB | 62 MB |
| E: | 447 | 39 KB | 26 MB |
| **All three** | **4,502** | **~395 KB** | **~260 MB** |

### Verdict: the brief's parameters are fine, and my concern was unfounded

The design review (section A5) flagged this as the thing most likely to sink the storage model, on
the reasoning that a directory qualifies whenever its whole *subtree* clears the floor, which is true
of nearly every ancestor of anything large. I guessed the count might be 40,000 per volume.

**It is 2,984 on the worst volume.** The estimate was off by more than an order of magnitude. At
~260 MB per week of full-resolution history, before any downsampling, the design works as written.

The full grid on C:, which is the worst case on this machine:

| depth | ≥ 1 MB | ≥ 10 MB | ≥ 100 MB | ≥ 1 GB |
|---|---:|---:|---:|---:|
| ≤ 4 | 5,057 | 1,137 | 380 | 117 |
| ≤ 6 | 9,896 | 3,005 | 997 | 248 |
| ≤ 8 | 16,969 | 5,733 | 1,735 | 405 |
| ≤ 10 | 29,314 | 9,714 | 2,382 | 500 |
| any | 43,785 | 13,409 | 2,950 | 543 |

Even the most generous cell - every directory over 1 MB at any depth - is 43,785 rows, about
3.9 MB per snapshot and 2.6 GB per week. Expensive but not absurd, and that is the extreme corner.

**Change made: depth cap raised from 6 to 8, 10 MB floor kept.** Measured after the change: 5,724 +
2,889 + 798 = 9,411 qualifying directories, costing **543 MB per week** across all three volumes
against 260 MB at depth 6. That is roughly double, and it buys attribution inside
`AppData\Local\Packages\<app>\...` and similar paths that are both deep and among the most common
sources of unexplained growth. Revert with `--depth 6` or by changing the default in
`SnapshotOptions` if you disagree.

> **Superseded.** `snapshot_dirs`, `--depth`, `--floor-mb` and `SnapshotOptions` are all gone -
> the table in migration 007, the rest on 10 Sep 2026 once it turned out they had stopped doing
> anything at that point. Kept for the reasoning, which is what produced `dir_history`. See
> "Two settings that had done nothing since migration 007".

Measured at the new default:

| Volume | Qualifying directories | Per snapshot | Per week |
|---|---:|---:|---:|
| C: | 5,724 | 503 KB | 330 MB |
| D: | 2,889 | 254 KB | 167 MB |
| E: | 798 | 70 KB | 46 MB |
| **All three** | **9,411** | **~827 KB** | **~543 MB** |

Note that 543 MB is the *rolling* seven-day cost, not unbounded growth: retention downsamples to
hourly after seven days, so the full-resolution window stays this size rather than accumulating.

The optimisations proposed in design review A5 - writing only changed directory rows against a daily
anchor, decoupling the rollup cadence from the snapshot cadence - are **not needed** and have been
left unbuilt.

---

## Correctness observations

### Our total versus the OS's

| Volume | Size on disk | OS reports used | Difference |
|---|---:|---:|---:|
| C: | 846.1 GB | 875.7 GB | −29.6 GB |
| D: | 849.9 GB | 853.8 GB | −3.9 GB |
| E: | 516.9 GB | 519.2 GB | −2.3 GB |

Strata undercounts, which is expected and has three causes:

1. **Unreadable directories.** 557 access-denied on C: unelevated, 4 on D:, 1 on E:. On C: these
   include `System Volume Information` (which holds restore points and can be tens of GB) and other
   users' profile directories.
2. **Filesystem metadata.** The MFT, the log file, bitmaps and the rest are real allocated space that
   no directory enumeration will ever see. On a 930 GB volume with 3M files the MFT alone is several
   GB.
3. **Hardlinks counted once**, by design: 18.2 GB on C: that Explorer counts multiple times.

The C: gap is the one worth watching. It is large enough that Phase 2 should test whether running
elevated closes most of it, which also settles whether Tier 1 wants elevation independently of
whether Tier 2 does.

### Hardlinks are real and concentrated

C: has **135,505 hardlinked files sharing 18.2 GB**, essentially all in `WinSxS`. D: and E: have
effectively none (0 and 14). This confirms the deduplication rule was worth specifying: without it C:
would over-report by 18 GB, and directory rollups would not sum to the volume total.

### Reparse points

114 on C:, none on D: or E:. All recorded as links and not traversed. Without that rule, the
`Documents and Settings` and `Application Data` junctions alone would cause the scan to walk parts of
the user profile repeatedly.

### Directories that vanish mid-scan

One C: run recorded 32 directories that disappeared between being discovered and being opened. On a
live system this is routine, not exceptional, and is exactly the case that used to crash the rollup
before the orphan-reparenting fix.

### Known limitation: hardlink paths are not all browsable

`files` is keyed `(volume_id, file_id)`, and hardlinks share a file ID, so a file with several links
occupies one row. That row now deterministically holds the path that owns the bytes, but the other
paths are not recorded. This affects 135,520 of 3.67M entries on this machine, nearly all inside
`WinSxS`.

Volume totals and directory rollups are unaffected - those come from the in-memory rollup, which sees
every link. Only "show me every path this file is reachable by" is unanswerable.

Fixing it means widening the primary key to include `(parent_id, name)`, which costs little storage
in a `WITHOUT ROWID` table but turns a rename into a delete plus an insert. **Deferred** until the UI
shows whether those paths are worth listing.

---

## Not yet measured

- **Cold scan of C: at 8 and 16 workers.** Needs a reboot. This is the single most valuable
  outstanding measurement, because cold is the case Tier 1 actually runs in and the current default
  was chosen from warm data.
- **Scan cost under load**, particularly with a game running. This is the constraint the whole
  design exists to satisfy and it has not been tested at all yet. Phase 2.
- **Whether elevation closes the 29.6 GB gap on C:.**
- **Tier 2 anything.** No USN journal code exists yet.
- **`dir_totals` write cost at Tier 2 scale.** Tier 1 writes it wholesale in the storage flush;
  the incremental path is Phase 2.

---

## Reproducing

```
dotnet build Strata.sln -c Release
strata volumes
strata scan C: --no-store --histogram          # sizing data, no writes
strata scan C: D: E:                           # full run with storage
strata scan C: --no-store --threads 16         # worker sweep
strata scan C: --no-store --no-background-io   # I/O priority comparison
```


---

# Phase 2 measurements

19 Aug 2026, same machine, **unelevated**.

## The elevation question, settled

The brief assumed Tier 2 would require running as administrator permanently. `strata usn-probe`
tries every combination of handle style against query, privileged read and unprivileged read:

| Volume | Handle | Open | Query | Read | Unprivileged read |
|---|---|---|---|---|---|
| C: | `\.\C:` no access | yes | - | - | - |
| C: | `\.\C:` FILE_READ_ATTRIBUTES | yes | - | - | - |
| C: | `\.\C:` GENERIC_READ | **no (error 5)** | - | - | - |
| C: | `C:\` root directory | yes | **yes** | no | **yes** |
| D:, E: | any | yes | no (1179) | - | - |

**The core Tier 2 loop needs no elevation at all**: a directory handle on the volume root plus
`FSCTL_READ_UNPRIVILEGED_USN_JOURNAL`. The classic `\.\C:` + GENERIC_READ form does need admin,
which is what the brief was describing.

What *does* need elevation is `FSCTL_CREATE_USN_JOURNAL`, one time per volume. D: and E: report
1179 (`ERROR_JOURNAL_NOT_ACTIVE`): Windows keeps a journal on the system volume for Search and
maintenance, and does not on the others. `strata enable-journal`, run elevated, fixes that once.

### The catch: the unprivileged read redacts file names

Every record returned by the unprivileged FSCTL has `FileNameLength` of zero. Confirmed against both
V2 and V3 records, so it is a property of the FSCTL rather than of a record version.

Names are therefore recovered by resolving the file ID through `OpenFileById` +
`FileNameInfo`, which costs one extra call on a handle already being opened to read the size, and
returns the whole path below the volume root rather than just the leaf. Measured at **0.06 ms per
call**, against 0.09 ms for the size read it accompanies.

Two consequences worth knowing:

- Resolution returns the file's **current** path. A rename's old path has to come from our own
  recorded rows and only the new one from the filesystem.
- Deleted files cannot be resolved at all, so their paths come from the database. This is why
  `files` stores current state rather than being append-only.

Running elevated would return names inline and remove the extra call. Given the measured cost, that
is an optimisation rather than a requirement.

## Tier 2 tick cost

Against the 2 s budget from the brief:

| Tick | Records | Metadata reads | Duration |
|---|---:|---:|---:|
| Steady state, quiet | 23 - 200 | 9 - 62 | **~250 ms** |
| Catch-up, capped bite | 50,000 | 7,000 | **~1.0 s** |
| Catch-up, heaviest observed | 50,000 | 16,117 | **1.7 s** |

Phase breakdown of a 50,000-record tick: journal read 24 ms, apply 256 ms (metadata 97 ms, store
139 ms), rollup 18 ms, commit 2 ms. The remainder is loading the directory tree, which a
long-running `watch` does once rather than per tick.

**The budget holds**, including on the largest bite the scanner will ever take in one go.

### One measurement that mattered more than the rest

Before it was found, a 2,000-record tick took **124 seconds**. The whole of it was one query:

```
GetEntriesByFileId: 442 ms per call
DirectoryExists:      0.003 ms
TryGetEntry:          0.004 ms
Upsert:               0.009 ms
```

SQLite planned the lookup as `SEARCH files USING PRIMARY KEY (volume_id=?)` - a range scan over
every row on the volume, 3.67M of them - because `ix_files_file_id` does not cover the size columns
and, with no statistics, a seek plus a row lookup per match did not look better than a scan.

Forcing it with `INDEXED BY` took it to **0.010 ms, a factor of 44,000**. `ANALYZE` now runs
alongside the post-baseline `VACUUM` as well.

What makes this worth recording is that nothing pointed at it. The same predicate with a narrower
projection plans as a covering index scan and looks perfectly healthy, so checking the query in
isolation confirmed the wrong thing. Only timing each store operation separately found it.

## Storage: the hardlink keying fix

`files` was keyed `(volume_id, file_id)`. Hardlinks share a file ID, so entries collided and all but
one path per file was lost - **135,520 of 3.67M entries** on this machine, nearly all in WinSxS.

Re-keyed on `(volume_id, parent_id, name)`, the identity the filesystem itself uses:

| | Rows | Database |
|---|---:|---:|
| Before | 3,532,313 | 396 MB |
| After | 3,668,066 | 447 MB |

3,668,066 is exactly the number of entries the scanner counts, so every path is now stored. The
extra 51 MB is the fan-out index on `(volume_id, file_id)`, which in a `WITHOUT ROWID` table carries
the full primary key. Measured directly by dropping it: **272 MB without, 447 MB with**, so the
index is 175 MB and the re-key itself actually *reduced* the base table.

The index earns that cost twice over: it is what lets a size change fan out across a file's links,
and it is the only way to find an entry whose name the journal redacted.

A partial index over only multi-link files would cut it to roughly 14 MB. Not done, because
correctness of the incremental path matters more than 160 MB and the uniform lookup is much harder
to get wrong.

## Still not measured

- **Scan cost with a game running.** The constraint the whole design exists to satisfy, and it has
  still not been tested under real contention. The gate defers correctly when Windows reports a busy
  or fullscreen state - observed during these runs - but the cost of a tick that does proceed
  alongside a game is unknown.
- **Cold-cache baseline at 8 and 16 workers.** Needs a reboot.
- **Whether elevation closes the 29.6 GB gap on C:.**
- **A journal that wraps or is reset while running.** The code path exists and is unit-tested
  against a fake, but has not been provoked for real.

---

# Phase 3a measurements

19 Aug 2026, same machine, **unelevated**, Release. Database 455 MB at the start, 3.05M files on C:.
Timings are end-to-end HTTP against `strata serve`, five runs each, so they include JSON
serialisation and loopback — which is what the UI will actually experience.

## API endpoint latency

| Endpoint | min | median | max | response |
|---|---:|---:|---:|---:|
| `GET /api/settings` | 1.3 ms | 1.5 ms | 6.6 ms | 325 B |
| `GET /api/events?limit=500` | 1.2 ms | 1.7 ms | 8.6 ms | 7.0 KB |
| `GET /api/tree?volume=C:&depth=1` | 1.8 ms | 2.0 ms | 15.8 ms | 5.2 KB |
| `GET /api/tree?volume=C:&path=…\AppData\Local&depth=1` | 2.5 ms | 2.6 ms | 11.5 ms | 23 KB |
| `GET /api/snapshots?volume=C:&limit=500` | 7.2 ms | 7.5 ms | 31.5 ms | 15 KB |
| `GET /api/volumes` | 14.0 ms | 14.3 ms | 151.7 ms | 1.9 KB |
| `GET /api/tree?snapshot=46&depth=2` | 15.6 ms | 19.4 ms | 36.6 ms | 26 KB |
| `GET /api/diff?from=1&to=46` | 30.9 ms | 41.2 ms | 65.3 ms | 173 B |
| `GET /api/tree?volume=C:&depth=3` | 104 ms | 109 ms | 156 ms | 2.6 MB |

`/api/volumes` is the slowest of the small ones because it enumerates live volumes through Win32 on
every call rather than serving the stored rows alone. That is deliberate — the stored rows only exist
after a scan, and free space changes constantly — but it is why it costs 14 ms and the others cost 2.

### The pathological tree

The root of C: at unbounded depth: **571k directories, 107.5 MB of JSON, 3.40 s**, first byte at
**2.4 ms**. The same request at the default 10,000-node limit: **93 ms**, first byte at 3.0 ms.

That second number is the point of streaming. The walk stops when the caller stops reading, so the
limit is reached without ever querying the rest of the tree.

## The recursive CTE trap

Both of the new tree-shaped queries were written as recursive CTEs over `dir_totals` first. Both were
catastrophic, for the same reason:

```
|--CO-ROUTINE walk
|  `--RECURSIVE STEP
|     |--SEARCH c USING PRIMARY KEY (volume_id=?)     <- every row on the volume, per level
|     `--SCAN w
```

`INDEXED BY ix_dir_totals_parent` changes the line to `SEARCH c USING INDEX ix_dir_totals_parent
(volume_id=?)` and changes nothing else: it still constrains only `volume_id`, never `parent_id`.

| Query | As a recursive CTE | Walked a level at a time |
|---|---:|---:|
| `/tree?volume=C:&depth=1` (13 rows) | **2,342 ms** | **2.2 ms** |
| Materialising `snapshot_dirs` for one tick | **did not finish** | **112–152 ms** |

A factor of about a thousand on the first, and the second was unusable. This is the same lesson as
the 442 ms `GetEntriesByFileId` from Phase 2, in a new place: **the plan looked reasonable and only
the clock disagreed.**

Path resolution had a milder version of it. `WHERE volume_id = ? AND parent_id = ? AND name = ?`
planned as a primary-key range scan at roughly 9 ms a segment, so resolving a four-segment path cost
37 ms. Pinned with `INDEXED BY ix_dir_totals_parent`: **2.6 ms** for the whole path.

## Materialising the historical rollup on every tick

`snapshot_dirs` was previously written only by a baseline scan, so 21 of 22 recorded snapshots could
not take part in a diff. Tier 2 now derives it from `dir_totals`.

Measured on C: in a long-running `watch`, after the first tick has loaded the directory tree:

| Tick phase | Cost |
|---|---:|
| Journal read | 0–1 ms |
| Apply deltas | 2–3 ms |
| Ancestor rollup | 0 ms |
| **`snapshot_dirs` (5,740 rows)** | **112–152 ms** |
| Events | 3–5 ms |
| Commit | 2–3 ms |
| **Total** | **122–163 ms** |

The 2 s budget holds with an order of magnitude to spare. About 50 ms of the rollup write is the
filtered read of `dir_totals` — a range scan over the volume's 571k rows, which is the honest cost of
asking a question about subtree size when nothing indexes subtree size. Indexing it would be worse:
every tick updates that column for every ancestor of every changed file, so the index would be
rewritten on the hot path to speed up a query that runs once per tick.

## Storage: the per-row estimate was low

Phase 1 estimated `snapshot_dirs` at ~88 bytes a row and projected 330 MB per week for C:. That was
an estimate, not a measurement. Measured with `dbstat` over 13 real snapshots:

| | Bytes per row | Per snapshot (5,740 dirs) | C: per week at 15 min |
|---|---:|---:|---:|
| Phase 1 estimate | 88 | 503 KB | 330 MB |
| **Measured, table only** | **117** | **669 KB** | **449 MB** |
| Measured, with the path index | 218 | 1.25 MB | 840 MB |

**The real figure is 449 MB per week for C: alone**, against the 330 MB projected — 36% higher. It is
still a rolling seven-day window rather than unbounded growth, and Phase 4's retention is what bounds
it, but the Phase 4 sizing should start from 449 and not 330.

> **Superseded, and do not size Phase 4 from this.** `snapshot_dirs` was dropped in migration 007
> later the same day and replaced by the sparse `dir_history`, which records a directory only when
> its subtree total moves. The 449 MB/week belongs to a table that no longer exists. The figure to
> start from is in "What retention actually has to bound" at the end of this document.


### An index added by reading a plan, and removed by timing the query

Migration 003 added `ix_snapshot_dirs_path` on `(snapshot_id, path)` so the historical subtree walk
would be a seek rather than a scan. That was reasoning about a plan, which this project has a rule
against. Timed on the real database, reading the `C:\Users` subtree of one snapshot:

| | Time | Cost per row |
|---|---:|---:|
| With the index | 21 ms | 101 bytes |
| Without it | 21 ms | 0 |

No measurable benefit, because one snapshot is only 5,740 rows and the primary key already clusters
them together. The cost was 87% on top of the table's own size, on the one table in the schema that
grows with the number of snapshots — about 390 MB a week of pure index. Migration 004 drops it.

## Correctness observations

### Diffing bounded rollups leaves an unattributable remainder

`SnapshotDiff` recovers a directory's own bytes as its subtree total minus the subtree totals of the
children the snapshot recorded. What falls below the size floor therefore lands on the nearest
recorded ancestor, which is the right place for it — but it behaves like a phantom sibling, and it
can stop the diff engine from descending.

Baseline (snapshot 1) against a recent tick (snapshot 46) on C:

```
C:            -17.487 GB      <- what /api/diff reports
C:\Users      -15.045 GB      86% of it, below the 90% dominance threshold
C: own        - 2.150 GB      nine files deleted directly from C:\ during Phase 2 testing
```

Users accounts for 86% of the change, the dominance threshold is 90%, and no other child clears the
reporting floor — so the engine summarises at the volume root and reports `C: -17.5 GB`. That is the
"technically true and useless" row the diff engine's own documentation says it exists to avoid, and
lowering the reporting floor does not help, because the remainder is at the root rather than in a
child.

Scoping the same diff to `C:\Users` gives the answer anyone would want, in one row:

```
-16.84 GB  C:\Users\chris\Documents\My Programming Projects\aerisetta\warrior-of-lust\unity\Library
```

The engine is behaving as specified and its rules are settled, so nothing was changed. Flagging it
because it is a design question the Compare screen will run into: a node's own delta is not modelled
as a child, so it dilutes dominance without ever being nameable. Reporting the remainder alongside
the children — "C:\Users −15.0 GB, and 2.2 GB elsewhere in C:\" — would be the honest shape.

#### Fixed in Phase 3b

That is now what it does. A directory whose reported children do not account for its change emits a
`remainder` row for the difference, at the level where the change stops being attributable:

```
C:\Users              -15.045 GB   grew/shrank as before
elsewhere in C:\       -2.150 GB   remainder
```

Two things had to be true for it to be an improvement rather than a second useless row. The presence
of a remainder above the reporting floor now forces the node to expand rather than summarise, which
is what stops `C: -17.5 GB` being produced at all. And a remainder that accounts for 90% or more of
the node's change does **not** force expansion — bytes written straight into a directory mean that
directory is the answer, and "elsewhere in D:\ +9 GB" beside "D:\Downloads +1 GB" would be coy about
a change with a perfectly good name.

The rows now reconcile: attributed changes plus remainders sum to the change they describe, which is
what lets the Compare screen state an attribution percentage instead of hoping nobody adds the column
up.

## Phase 3b: what the UI costs

Measured on this machine, production build, after the screens were built.

| | Raw | Transferred (gzip) |
|---|---:|---:|
| Initial load | 310 kB | **88 kB** |
| ECharts chunk, shared by Timeline and Explorer | 602 kB | 170 kB |
| Settings | 41 kB | 11 kB |
| Compare | 20 kB | 6 kB |
| Timeline | 20 kB | 7 kB |
| Overview | 18 kB | 5 kB |
| Explorer | 14 kB | 4 kB |
| Bundled fonts, both families, latin subsets | 77 kB | 77 kB |

ECharts is registered piecewise rather than imported whole, and the screens are lazily loaded, so
opening the tray icon to check Settings does not download a charting library. The 170 kB chart chunk
arrives with Timeline or Explorer and is then shared between them.

### One number rule was wrong, and only a test caught it

The palette says "three significant figures minimum". The first implementation read that as exactly
three and rounded to it, which turned the brief's own worked example — `−16.78 GB`, the real
`PlayerDataCache` deletion — into `−16.8 GB`. The figures in `format.spec.ts` are taken verbatim from
the design brief for exactly this reason: a rounding rule that is subtly wrong produces numbers that
look entirely plausible.

## API latency during a Tier 1 baseline — measured at last

Outstanding since Phase 2, and the claim per-thread background I/O priority was chosen to support:
a scan must not throttle a read in the same process. Taken by driving a full baseline of all three
volumes through `POST /api/scan` with `strata serve` up, sampling four read endpoints every six
seconds throughout.

| Endpoint | Idle | **During a Tier 1 baseline** | After |
|---|---:|---:|---:|
| `/api/volumes` | 12.9 ms | **2.5 ms** | 1.7 ms |
| `/api/snapshots` | 1.9 ms | **2.0 ms** | 1.7 ms |
| `/api/scan-log` | 2.1 ms | **2.0 ms** | 2.1 ms |
| `/api/scan/status` | 1.5 ms | **1.6 ms** | 1.2 ms |

Medians; n=25 during, n=3 either side. **The scan costs the API nothing measurable.** The idle
figures look worse only because the first sample of a cold process pays JIT — 34 ms, 17 ms, 13 ms,
19 ms on the very first pass — which three samples cannot dilute and twenty-five can.

Worst cases during the scan were 13–14 ms, but ~11 ms outliers appear in the idle and after passes
too, so they are not the scan: they track GC and SQLite page-cache misses, not disk contention.

`/api/tree` is deliberately absent. A Tier 1 baseline rebuilds `dir_totals` wholesale, so timing it
would measure a table being replaced rather than a read being served.

### The scan itself

| Volume | Duration | Files | Directories |
|---|---:|---:|---:|
| C: | **125.4 s** | 3,120,923 | 579,196 |
| D: | 1.3 s | 190,369 | 10,514 |
| E: | 3.3 s | 423,063 | 24,525 |

C: warm, at a peak of 26,600 entries/s. The 211 s figure elsewhere in this document is cold, and
remains the number first-run UX has to be designed against.

Database: **438 MB to 604 MB**, the growth being D: and E: gaining a baseline rather than C: gaining
a second one.

### Two defects this run exposed

**A wrapped journal could never recover.** `JournalInvalidated` tells the user a full baseline is
needed; the baseline set `volumes.baseline_utc` and left the dead row in `usn_cursors`, so the next
tick failed identically and asked again. C: was in that loop. A baseline now clears the cursor, and
an absent cursor is already the "start from the journal's current position" signal. After the fix,
C: went from deferring every tick to `ran 0.6 s, 194 journal records`.

**Progress was silent for the first twenty seconds**, and the first diagnosis of why was wrong.

The symptom: `/api/scan/status` reported 0%, zero entries per second and no path for nineteen
seconds of a 125-second scan, then jumped to 3,370 directories in the twentieth. The obvious reading
is that progress was throttled — it fires every 2,000 completed directories — so the trigger also
gained a 500 ms timer. That changed nothing, which is what showed the reading was wrong.

Nothing was being throttled, because nothing was being reported. `SqliteFileSink`'s constructor runs
`DELETE FROM files WHERE volume_id = ?` before enumeration begins — a Tier 1 scan replaces the
volume wholesale — and on C: that is **3.1 million rows and about nineteen seconds**. The progress
counters were zero because the walk had not started, and a callback that only fires on directory
completion cannot report a phase in which no directory completes.

A baseline is therefore three pieces of work and only the middle one has a denominator:

| Phase | On C: | Has progress? |
|---|---:|---|
| Clearing the previous scan | 12–19 s across runs | no |
| Reading the volume | ~106 s | yes |
| Draining the writer, writing the snapshot | seconds | no |

Verified by sampling `/api/scan/status` once a second through a rescan:

```
  1s  phase=preparing     —    basis=none     dirs=      0
 12s  phase=preparing     —    basis=none     dirs=      0
 13s  phase=reading     0.0%   basis=entries  dirs=      0
 14s  phase=reading     0.7%   basis=entries  dirs=  1,386
 26s  phase=reading     7.8%   basis=entries  dirs= 39,372
```

The 14-second sample is the 500 ms timer earning its keep: 1,386 directories is below the 2,000
threshold, so a count-only trigger would have shown nothing for another second.

`/api/scan/status` now carries a `phase`, and reports `percent: null` with `basis: "none"` outside
the reading phase rather than a zero that looks like a stall. The first-run screen says "Clearing the
previous scan of this volume" and runs its indeterminate sweep. The 500 ms timer was kept — it costs
a tick count read per directory and guarantees the current-path display refreshes twice a second on
a slow cold branch — but it is not what fixed this.

## Still not measured

Unchanged from Phase 2, plus one:

- **Scan cost with a game running.** The constraint the whole design exists to satisfy, still
  untested under real contention.
- **Cold-cache baseline at 8 and 16 workers.** Needs a reboot.
- **Whether elevation closes the 29.6 GB gap on C:.**
- **A journal that wraps or is reset while running.**
- **`/api/series` latency at a year of history.**
- ~~**D: and E: have no change journal.**~~ Created 19 Aug 2026 in one elevated run; both now read
  on the unprivileged path and tick like C:. Timed only against the current database, which
  holds days rather than months. It is one clustered range read per top-level directory, so the cost
  scales with rows in the window rather than with rows in the table — but that is reasoning about a
  plan, which this project has a rule against.

---

# Phase 3a addendum: sizing a sparse directory history

The Phase 3a numbers showed `snapshot_dirs` costing more than estimated for less coverage than
assumed. `strata watch --rollup-log` was added to size the alternative against real activity; the
analysis and the proposal live in **docs/STORAGE-MODEL.md**, and `tools/replay-rollup-log.py`
reproduces it.

Headline, from 37 ticks over 35 minutes on C: including two clean rebuilds and a 2.2 GB
install-and-delete cycle:

| | |
|---|---:|
| Directories touched per tick, mean | **68** |
| …that `snapshot_dirs` writes per tick regardless | **5,740** |
| Ancestor fan-out per directly-changed directory | 2.3× |
| Touched directories below depth 8, invisible to today's cap | **55%** |
| Proposed row size, measured with `dbstat` on real ids | **26.5 B** vs today's 116.5 B |
| Per week on C:, recording every change at unbounded depth | **18.2 MiB** vs today's 428.6 MiB |

**24× less storage for complete depth coverage**, before any dead-band is applied. A dead-band takes
it further — 4.0 MiB a week at 1 MiB — but turns out to be a tuning knob rather than a necessity.

Reproduce with:

```
strata watch --interval 900 --rollup-log rollup.csv
python tools/replay-rollup-log.py rollup.csv %LOCALAPPDATA%\Strata\strata.db
```

**Built, same day.** Migrations 005 to 007 replaced `snapshot_dirs` with the sparse history. Measured
after the fact: the history row is **22.0 B** against the old **116.4 B**, a tick writes **43-241**
rows against **5,751**, and the database went from **545 MiB to 438 MiB** after a `VACUUM` — half the
storage for history that is complete instead of capped at depth 8 and 10 MB. All 29 directories that
changed between the last two rolled-up snapshots agreed exactly between the two models before the old
table was dropped. Full figures in docs/STORAGE-MODEL.md.

---

# Phase 4 addendum: what an installed Strata costs on disk

`strata install` copies a publish output to `%LOCALAPPDATA%\Programs\Strata`, so the size of that
publish is the whole footprint of the program. Three shapes, published from the same commit with
the same Angular build, measured with `Get-ChildItem -Recurse | Measure-Object -Sum Length`:

| Publish | Files | Size | Needs .NET installed |
|---|---:|---:|---|
| Framework-dependent, no RID | 62 | 30.6 MB | yes |
| **Framework-dependent, `-r win-x64`** | **42** | **3.8 MB** | **yes** |
| Self-contained, `-r win-x64` | 367 | 105.5 MB | no |

Of which the Angular UI is 21 files and 1.10 MB in every case, and the tray's icons are 5 files.

**The RID-less publish is a trap worth naming.** 28.5 MB of its 30.6 MB is
`runtimes\<platform>\native\`, SQLitePCLRaw's SQLite binary built for twenty platforms — ppc64le,
maccatalyst-arm64, musl-s390x — of which exactly one will ever be loaded. `-r win-x64` with
`--self-contained false` keeps the framework-dependent deployment and drops the other nineteen.
That is an 8× saving for a flag.

**Chosen: framework-dependent, RID-specific, `RollForward=Major`.** 3.8 MB against 105.5 MB is 28×,
on a tool whose entire subject is what is filling up a disk; shipping a hundred megabytes of runtime
to report on disk usage would be hard to defend in the app's own treemap.

The one thing self-contained buys is that it cannot fail to find a runtime, and that mattered more
than usual here: a logon task starts this with no console, so `You must install .NET` would go
nowhere anybody reads and Strata would simply never appear. `RollForward=Major` buys the same
insurance for nothing — Strata runs on .NET 9 while it is installed, and on the lowest higher major
if it is ever removed. `Major` rather than `LatestMajor`, which would jump straight to the .NET 10
runtime that is also on this machine and quietly run the shipped process on a runtime the tests
never saw.

`tools\publish.ps1 -SelfContained` remains, for a machine that will have no runtime at all.

## For scale

| | |
|---|---:|
| The installed program | 3.8 MB |
| The database it maintains, C:, D: and E: after a week | 648.5 MB |

The program is 0.6% of its own data directory, and 0.0004% of the 2.2 TB it is watching.

---

# Phase 4: the journal that wrapped

20 Aug 2026, same machine. **A documented assumption, disproved by ordinary use.**

`StrataSettings.MaxRecordsPerTick` said:

> The journal is a durable queue, so falling behind is safe and self-healing.

It is durable. It is not infinite, and nothing in Phase 2 measured how big it is. Both halves of
that sentence after the comma are wrong, and the cost of being wrong was 27 hours of C: history and
25 hours of E:.

## What happened

Strata silently stopped seeing C: at **2026-08-19T16:26:18** and was not noticed until the following
evening, when a folder the user had deleted — `…\My Programming Projects\Blue Otter Games` — was
still on the Explorer screen at its full 16.84 GiB. The screen was not wrong. It was faithfully
showing the last state Strata knew, with nothing to say how old that was.

E: had gone the same way ninety minutes later, at **17:56:21**, and nobody noticed that at all until
this was being investigated.

The run-up on C:, from `scan_log`:

| Time | Records | Duration | |
|---|---:|---:|---|
| 14:56:42 | 47,618 | 1,296 ms | 95% of the cap, and not yet flagged |
| 15:11:43 | 21,513 | 1,214 ms | the last tick that drained the journal |
| 15:26:44 | **50,000** | 825 ms | catching up |
| 15:29:26 | **50,000** | 1,536 ms | catching up |
| 15:44:27 | **50,000** | 553 ms | catching up |
| 15:54:48 | **50,000** | 2,067 ms | catching up |
| 15:56:16 | **50,000** | 2,092 ms | catching up |
| 16:11:17 | **50,000** | 999 ms | catching up |
| 16:26:18 | **50,000** | 1,031 ms | catching up — and the last tick that ever ran |
| 16:41:18 | — | — | `journal reset or cursor trimmed; a full baseline is needed` |

Seven consecutive ticks at exactly the cap, one hour, and then the cursor was outside the window.
Every tick after that named the remedy and none of them applied it:

| Volume | Deferrals after the last good tick | of which "a full baseline is needed" |
|---|---:|---:|
| C: | 74 over 27.2 h | **54** |
| E: | 68 over 25.9 h | **49** |

The other twenty on each volume were the gate doing its job — 13 borderless fullscreen, 5 system
busy, 2 CPU. Those are correct. The 54 are a detected fault with a known remedy, repeated hourly,
acted on never.

## The measurement nobody had taken: how big is the journal?

`strata usn-probe` reports each journal's live window, which no previous phase had recorded.

| Volume | Journal ID | First USN | Next USN | **Window** |
|---|---|---:|---:|---:|
| C: | `1DB931EBE461434` | 102,248,742,912 | 102,282,419,456 | **33,676,544 B — 32.1 MiB** |
| E: | `1DD2FE683605825` | 301,989,888 | 336,636,352 | **34,646,464 B — 33.0 MiB** |
| D: | `1DD2FE6835C7B28` | 0 | 905,176 | 884 KiB, never yet wrapped |

**C:'s change journal is a 32 MiB circular buffer.** That is the whole of the durable queue.

A USN is a byte offset, not a record ordinal, so the window has to be converted before it can be
compared with a record cap. Measured directly, by dividing a tick's cursor advance by the records it
reported reading:

| Tick | Records | Cursor advance | Bytes/record |
|---|---:|---:|---:|
| 20:18:28 | 50,000 | 7,694,088 B | 153.9 |
| 20:19:28 | 17,662 | 1,711,304 B | 96.9 |

Records are variable length — the name is stored inline even though the unprivileged read redacts it
— so call it **97–154 bytes**, and C:'s window **roughly 220,000 to 350,000 records**.

### The arithmetic that made this inevitable

| | |
|---|---:|
| Drain rate, `MaxRecordsPerTick` 50,000 every `IntervalSeconds` 900 | **55.6 records/s** |
| C: churn over the hour it was losing, derived from the deficit crossing the window | **≈ 116 records/s** |
| Journal window, in time, at that churn rate | **≈ 31 minutes** |
| Stored cursor when the fault was diagnosed | 101,767,768,800 |
| Window start at the same moment | 102,240,354,304 |
| **Cursor behind the window** | **472,585,504 B — about 14 windows, 3.1–4.9M records** |

Three things follow, and only the first was previously believed:

1. Falling behind is safe **only until the deficit exceeds the window**, and then it is unrecoverable.
2. **At 116 records a second, C:'s journal holds about half an hour.** Any deferral longer than that
   during a heavy build loses the cursor — including a perfectly correct deferral for a fullscreen
   game. Thirteen consecutive fullscreen deferrals were observed in this very incident, which is
   3.25 hours, or six journal windows.
3. So the cursor **cannot be protected** without breaking the first non-negotiable. What can be
   guaranteed is that losing it is noticed and repaired.

That third point is why the fix is automatic recovery rather than more aggressive ticking.

## Was 50,000 records per tick simply too low?

It is not the lever, and the same log says why. The seven catch-up ticks measured 553–2,092 ms, with
two of them over the 2 s budget from the brief:

```
 553 ms  ███████
 825 ms  ██████████
 999 ms  ████████████
1031 ms  ████████████
1035 ms  ████████████        (a fresh one, 20 Aug)
1536 ms  ███████████████████
2067 ms  ██████████████████████████   over budget
2092 ms  ██████████████████████████   over budget
```

**The cap is already at the budget**, which confirms the Phase 2 figure of "about a 1 s tick" and
rules out raising it: doubling it to 100,000 would put a routine catch-up tick at 2–4 s, which is the
stall the incremental path exists to avoid. `MaxRecordsPerTick` is unchanged at 50,000.

The interval is the free variable, so that is the one that moved. A tick that hits the cap now comes
back after `CatchUpIntervalSeconds`, default 60, instead of waiting out the full 900:

| | Drain rate | Against 116 records/s of churn |
|---|---:|---|
| 50,000 every 900 s | 55.6/s | loses, by a factor of 2.1 |
| **50,000 every 60 s** | **833/s** | **wins, by a factor of 7.2** |

Only the volume that is behind is accelerated. Ticking the quiet ones fifteen times as often would
multiply their `snapshots` rows by fifteen to learn nothing, so each volume now carries its own due
time rather than the scheduler sweeping all of them on one timer.

### Measured live, on the real machine

First cycle after deploying, C: still holding the backlog from the recovery baseline:

```
20:18:28   50,000 records   1,035 ms   catching up   -> next tick 20:19:28   (60 s)
20:19:28   17,662 records     393 ms   drained       -> next tick 20:34:28   (900 s)
```

D: and E: stayed on 900 s throughout, which is the behaviour the per-volume due time exists for.
One accelerated tick, and the volume was current. At the old cadence the same backlog would have
taken until 20:33 and started the next interval already behind.

## What the recovery costs

The baselines that restored service, both taken during this session:

| Volume | Files | Directories | Duration |
|---|---:|---:|---:|
| C: | 3,029,435 | 572,734 | **142.5 s** |
| E: | 343,458 | 23,628 | **~5 s** |

An automatic baseline is gated at Tier 1, so it waits for no game, no presentation, no recent input
and CPU below threshold — the same conditions a manual baseline waits for. It is rate-limited to one
per volume per six hours, which bounds the worst case at four baselines a day: **9.5 minutes of disk
on C:**, taken only in idle moments, against 27 hours of history not being recorded at all.

Applied to the incident as it actually happened, escalation would have fired at **17:26:18** — the
fourth consecutive `NeedsBaseline` deferral, one hour after the fault, and 26 hours before a person
noticed. The machine was idle then; the fullscreen deferrals did not start until 18:26.

## A correction the log forced

The first cut reset the consecutive-deferral count whenever the *kind* of deferral changed, on the
reasoning that the situation had changed. The real E: sequence disproves it: the journal was lost at
18:11 and a game started at 18:26. A gate deferral says nothing about whether the journal is
readable — it means the tick never looked — so treating it as evidence of improvement would have
pushed E:'s recovery back another four cycles for free, and again after every subsequent game.

Counts are now kept per remedy and cleared only by a tick that actually ran, which is the one event
that proves a condition has lifted.

## The recovery, provoked on the real machine

The failure took 27 hours to appear naturally, so it was reproduced deliberately: E:'s stored cursor
was set to a USN its live window no longer contains — which is exactly what the journal wrapping past
it does — with the interval shortened to 30 s so four cycles take two minutes instead of an hour.

```
20:27:03  deferred   journal reset or cursor trimmed; a full baseline is needed
20:27:38  deferred   journal reset or cursor trimmed; a full baseline is needed
20:28:08  deferred   journal reset or cursor trimmed; a full baseline is needed
20:28:38  deferred   journal reset or cursor trimmed; a full baseline is needed
20:28:41  ran        automatic recovery baseline, the journal could not be resumed —
                     343,458 files in 23,628 directories                      2,389 ms
20:29:13  ran        incremental, 0 journal records                                9 ms
20:29:48  ran        incremental, 0 journal records                                9 ms
20:30:18  ran        incremental, 0 journal records                                8 ms
```

Four deferrals, then the baseline three seconds after the fourth, then ticking. C: and D: were
untouched throughout. Against the same fault before this change: 49 deferrals, 25.9 hours, no
recovery.

Then the loop guard, provoked the same way — the cursor poisoned again immediately after the
recovery:

```
20:31:23 · 20:31:53 · 20:32:23 · 20:32:54 · 20:33:25 · 20:33:55   all deferred, no second baseline
```

Six cycles past the four-deferral threshold and nothing fired, because the previous automatic
baseline was minutes old and the cooldown is six hours. A recovery that does not work cannot become
a scanner that runs baselines forever.

## Still not measured

- **Scan cost with a game running.** Unchanged from Phase 2 and Phase 3: still the constraint the
  whole design exists to satisfy, and still never tested under real contention.
- **How large the journal can be made.** `FSCTL_CREATE_USN_JOURNAL` takes a maximum size, and D:'s
  and E:'s were created by `strata enable-journal` without one being chosen deliberately. A larger
  buffer on C: would widen the window that half an hour of heavy building consumes. It needs
  elevation, so it is a setup-time question rather than a runtime one.
- **Cold-cache baseline at 8 and 16 workers.** Needs a reboot.
- **Whether elevation closes the 29.6 GB gap on C:.**

---

# What retention actually has to bound

20 Aug 2026, measured with `dbstat` on the live 680 MB database, 39 hours into continuous running
across three volumes. Taken before starting Phase 4's retention work, because the figure that
document previously pointed Phase 4 at — 449 MB a week — belongs to `snapshot_dirs`, a table dropped
in migration 007.

| Object | Size | Grows with |
|---|---:|---|
| `files` | 251.4 MB | files on the machine |
| `ix_files_file_id` | 191.8 MB | files on the machine |
| **`dir_history`** | **96.1 MB** | **time** |
| `dir_totals` | 36.4 MB | directories on the machine |
| `dir_names` | 31.3 MB | directories ever seen |
| `ix_dir_totals_parent` | 15.2 MB | directories on the machine |
| `ix_dir_names_parent` | 14.9 MB | directories ever seen |
| everything else | < 1 MB combined | — |

**Only `dir_history` grows with time, and it is 14% of the database.** The other 86% is current
state, sized by how many files and directories the machine has. Retention cannot touch it, and no
amount of downsampling will make a 680 MB database materially smaller today. That is worth knowing
before designing retention around an expectation that it will.

## Where the 96 MB came from

`dir_history` holds 3,945,004 rows at **25.5 bytes a row**. It is not one population but three:

| Source | Rows | Size | |
|---|---:|---:|---|
| Migration 005's opening seed | 571,376 | ~14.6 MB | one-off, C: only |
| Baseline anchors, six of them | ~2.89M | ~73.7 MB | **~14.7 MB each on C:** |
| Ordinary ticks | ~484k | ~12.3 MB | 39 h, all three volumes |

Steady-state ticks on C: at the 15-minute cadence wrote 77, 387 and 568 rows — a few kilobytes each.
The documented 18.2 MiB/week for C: still stands as the order of magnitude.

**Baselines dominate, at 15 MiB a time**, because `WriteAnchor` records every directory on the volume
so the Explorer can drill anywhere at any point in history. That is deliberate and documented, and it
is a new input to retention sizing for a reason that did not exist last week: as of the journal
recovery work, a baseline is something the scheduler can decide to run on its own. The six-hour
cooldown caps that at four a day per volume, so the worst case is **59 MiB a day of anchors on C:**,
against a steady-state tick cost of a few MB a week.

Retention's stated rule — *keep the newest row per directory per bucket*, not *delete rows older than
X* — absorbs this without needing a special case: several anchors inside one bucket collapse to one.
The sizing just has to expect them.

## What this means for the retention design

1. **Size it from `dir_history` alone.** Every other table is current state.
2. **Anchors are the volume, not ticks.** A rule that downsamples per-directory-per-bucket is
   already the right shape; one that reasoned about "rows per tick" would miss where the bytes are.
3. **The seven-day full-resolution window is cheap.** At the observed steady-state rate it is single-
   digit MB per volume, so the aggressive downsampling the Phase 1 and 3a projections implied is not
   needed to keep the database in hand.
4. **`snapshot_top_files` is still schema-only.** The last unpopulated table in the storage model,
   and it would grow with time, so whether it is filled is a decision retention has to be designed
   around rather than after.

---

# Phase 4: what retention costs, and what it buys

20 Aug 2026, measured on a `.backup` copy of the live 680 MB database so the real history was never
at risk. `strata prune --as-of` exists for exactly this: the tiers are 7/30/365 days and the database
is 39 hours old, so without it the first honest measurement would have been in a week.

## The passes

| Pass | Rows removed | Of the table | Duration |
|---|---:|---:|---:|
| First, never pruned | **2,137,992** | 54.2% | **8,603 ms** |
| Next night, steady state | **24,331** | 1.3% | **2,442 ms** |
| Same day again | **0** | 0% | 1,214 ms |

The middle row is the one that matters, because it is the one that happens every night for the rest
of the product's life. The first is a one-off backlog of everything recorded before retention
existed.

The third is idempotence, measured rather than argued: a second pass on the same day removes nothing.
That is not obvious — a bucket aligned to the run rather than to the epoch would thin a different set
every night and walk every directory down to a single row over a few months — so it is pinned here
and in a unit test.

**1,214 ms is the floor**, and it is the cost of the table scan. The predicate is on `taken_unix`,
which the primary key `(volume_id, dir_id, taken_unix)` does not lead with. An index on time would
cost 19.1 bytes a row — a 72% overhead on the fastest-growing table in the schema, from
`docs/STORAGE-MODEL.md` — to speed up something that runs once a night. Not added, on purpose, and
this is the number that would have to get much worse before that changes.

## What it reclaims

| | Before | After |
|---|---:|---:|
| `dir_history` rows | 3,945,353 | 1,807,361 |
| `dir_history` bytes | 96.1 MB | **38.2 MB** |
| Database file, after `--compact` | 648.8 MB | **511.7 MB** |

The file shrank by 137 MB while `dir_history` shrank by 58, because `VACUUM` also reclaims the free
pages the six baselines left scattered through the rest of the file.

**The nightly pass does not do that.** Deleted rows return their pages to SQLite's free list, where
the next weeks of history are written instead of extending the file — so the file stays 648.8 MB and
stops growing, which is the outcome that matters. Rewriting a 680 MB database every night to hand
the space back to Windows is precisely the sustained background disk work this product exists to not
do. `strata prune --compact` is there for when somebody actually wants the file smaller.

### A measurement that was wrong, and how it announced itself

The first run of `--compact` reported:

```
648.8 MB -> 1 GB (+377.5 MB)
```

A compaction that grew the file by 377 MB. It had not: `VACUUM` rebuilds the database inside the
write-ahead log, and the size was being read as file plus WAL, so the "after" figure counted the
rebuild twice. It really had reclaimed 137 MB. `Compact` now checkpoints the log before anybody
measures anything.

Worth recording because the number was absurd enough to be caught. A compaction that reported
reclaiming 20 MB when it reclaimed 5 would have been believed.

## What it does not touch

`snapshots` and `events` are left at full resolution, deliberately.

`events` is what the most valuable screen in the product reads, and on this machine it is **197 rows
against 3.9M of history** — thinning it would save nothing measurable and cost the change log.
`snapshots` is smaller still and carries `ON DELETE CASCADE` to `events`, so thinning it would take
the change log with it silently. The architecture already said events keep full resolution; the
cascade is the mechanical reason it has to be `dir_history` alone.

`scan_log` is dropped by age rather than thinned, at 90 days. Nothing reads it as a series — no query
asks what the log said at time T — so no row is load-bearing for a later one, which is the property
`dir_history` does not have and the reason they are two different statements.

## The projection, revised again

Phase 1 projected 330 MB a week. Phase 3a measured 449 MB a week and told Phase 4 to size from it.
Both were `snapshot_dirs`, dropped in migration 007. Measured now, on the model that actually exists:

| | Per week, C: |
|---|---:|
| Phase 1 estimate, `snapshot_dirs` | 330 MB |
| Phase 3a measured, `snapshot_dirs` | 449 MB |
| **`dir_history`, ordinary ticks** | **a few MB** |
| `dir_history`, per baseline anchor | 14.7 MB each, when one runs |

The storage problem retention was designed to solve had already been solved by the storage model. It
still earns its place — an unbounded table on a resident process is a bug waiting for a long enough
uptime, and 54% of a real table was genuinely redundant — but the honest framing is that it keeps
history flat rather than that it rescues a database in trouble.

## Still not measured

- **Scan cost with a game running.** Unchanged since Phase 2. Still the constraint the whole design
  exists to satisfy, and still never tested under real contention.
- **A retention pass at a year of history.** Everything above extrapolates from 39 hours of real
  data aged forward with `--as-of`. The row counts are real; how a 30M-row `dir_history` scans is
  not.
- **Cold-cache baseline at 8 and 16 workers.** Needs a reboot.
- **Whether elevation closes the 29.6 GB gap on C:.**

---

# Twenty-two days resident - 10 Sep 2026

Everything above was measured in sessions: a scan run, a number taken, the process stopped. This is
the first reading taken from a scanner that had simply been *left running* - 19 Aug to 10 Sep, 4,233
snapshots across three volumes, 5,467 scheduler decisions, **zero failed scans**.

Three of the numbers the design rests on did not survive it.

## The Tier 2 budget was measured on the wrong distribution

`ARCHITECTURE.md` said a steady-state tick was about 250 ms, that the largest bite - 50,000 records
- was about 1 s, and that the brief's 2 s budget held. That came from the Phase 2 session in the
table under "Tier 2 tick cost", and it is a fair reading of the ticks in that session. It is not a
fair reading of C:.

Every completed Tier 2 tick on C:, 1,587 of them:

| Records in the tick | Ticks | Mean | Worst |
|---|---:|---:|---:|
| under 5,000 | 281 | 752 ms | 3.2 s |
| 5,000 - 20,000 | 467 | 1,459 ms | 15.7 s |
| 20,000 - 50,000 | 395 | 2,791 ms | 27.0 s |
| at the 50,000 cap | 438 | 3,314 ms | 25.2 s |

**569 of 1,587 C: ticks - 35.9% - exceed the 2 s budget**, 26 of them by more than five times, the
worst at 27.0 s. Across all three volumes it is 705 of 4,142, or 17.0%; D: and E: are quiet and the
old figures still describe them.

The per-record cost is not the problem: it ranges 0.2-1.3 ms and the means above scale almost
linearly with record count. The problem is that the earlier measurement generalised from a quiet
volume. C: routinely produces 20,000-50,000 records in an ordinary fifteen-minute tick, and a tick
of that size was never going to fit in 2 s at any per-record cost this design achieves.

This is not a frame-drop risk - scan threads hold `THREAD_MODE_BACKGROUND_BEGIN` throughout. It is a
**write-lock risk**: a 27 s tick holds the SQLite write transaction for 27 s, and the WAL grew from
19.5 MB to 39.7 MB over a single afternoon of this, which is checkpoints being starved by exactly
that. Recorded here rather than fixed; see "Still not measured".

## Every journal on the machine was 32 MB, including the ones Strata made

```
fsutil usn queryjournal C:   Maximum Size     : 0x0000000002000000 (32.0 MB)
                             Allocation Delta : 0x0000000000800000 ( 8.0 MB)
```

D: and E: identical - and D: and E: got their journals from `strata enable-journal`, because
`Win32UsnJournal.TryCreateJournal` defaulted its `maximumSize` parameter to 32 MB. Strata was not
inheriting the Windows default; it was independently choosing it.

The consequence, over 22 days on C:: **12 full baselines, 7 of them automatic recovery baselines**,
costing between 108 s and 313 s each.

The gap before each journal loss, measured from the last good tick:

| Gap before the cursor was lost | Times |
|---|---:|
| 0.25 h - a single scan interval | 1 |
| 1.0 h | 2 |
| 1.5 - 3.0 h | 7 |
| 4.0 - 6.0 h | 5 |

The 0.25 h case is the one that matters. The section "The measurement nobody had taken" established
C:'s journal as roughly 220,000-350,000 records and called that "about half an hour of history at
the churn rate measured during a heavy build". That estimate was right, and the conclusion drawn
from it - that `CatchUpIntervalSeconds` would close the gap - was only half the answer. Catching up
faster helps a scanner that is *behind*. It does nothing for a scanner that is *deferred*, and
deferring to a fullscreen game is the first non-negotiable in `CLAUDE.md`. Three hours of a game is
three hours in which no tick may run and the journal wraps regardless of how quickly the next one
would drain.

The only variable left is how much history the journal keeps while it waits, and that is a number
Strata chooses. `Win32UsnJournal.DefaultMaximumSize` is now **512 MB**, sixteen times the window for
half a gigabyte of a terabyte volume, and `strata enable-journal` grows an existing undersized
journal rather than reporting it as already active. Two tests in `JournalRecoveryTests` state the
case: a three-hour gap at that file's churn rate loses a 32 MB journal and survives a 512 MB one,
and a long enough outage still asks for a baseline rather than quietly returning a hole.

## `dir_history` is not expensive; baselines are

The table is 7,525,531 rows and 185.8 MB, against a projection of 18.2 MiB a week. That looks like
the sparse-history model failing. It is not. Rows written per day, against C: baselines that day:

| Day type | Rows written |
|---|---:|
| A day with a C: baseline (Aug 24-28, Sep 4, Sep 8, ...) | 575,000 - 650,000 |
| A day without one | 5,000 - 50,000 |

A baseline writes an opening anchor for every directory on the volume - 571,376 of them on C: - and
twelve baselines is essentially the whole table. Ordinary change tracking costs what it was measured
to cost. **The storage number and the scan-cost number have the same root cause**, and sizing the
journal fixes both.

571,376 rows also sit at `taken_unix = 0`. Those are the migration 005 seed, kept deliberately and
explained in migration 006: a directory `snapshot_dirs` never recorded answers "I do not know"
rather than back-dating today's size across all of recorded history.

## The database, broken down

834.6 MB - 203,759 pages at 4 KB, of which only 5,313 are free, so it is not fragmentation.

| | Size | Share |
|---|---:|---:|
| `files` | 261.3 MB | 33.0% |
| `ix_files_file_id` | 199.4 MB | 25.2% |
| `dir_history` | 185.8 MB | 23.5% |
| `dir_names` | 56.7 MB | 7.2% |
| `dir_totals` | 41.7 MB | 5.3% |
| `ix_dir_names_parent` | 24.2 MB | 3.1% |
| `ix_dir_totals_parent` | 17.6 MB | 2.2% |
| everything else | 3.8 MB | 0.5% |

`ARCHITECTURE.md` recorded 396 MB for the same three volumes. The difference is `dir_history` and
the growth of `files` and its index as the machine filled up, not a regression in the storage model.

## Retention works, and this is how that was checked

The rule is "keep the newest row per directory per bucket", so counting distinct timestamps per day
proves nothing - different directories legitimately keep rows at different instants inside one hour.
The check that does prove it is to count `(directory, hour)` buckets holding more than one row,
outside the 7-day full-resolution window:

```sql
SELECT COUNT(*) FROM (
  SELECT dir_id FROM dir_history
  WHERE volume_id = 1 AND taken_unix > 0 AND taken_unix < strftime('%s', '2026-09-03')
  GROUP BY dir_id, taken_unix / 3600 HAVING COUNT(*) > 1);
```

**0.** Hourly downsampling has been applied to every directory beyond the horizon, with no
directory's last row before it deleted.

## Two settings that had done nothing since migration 007

`snapshotMaxDepth` and `snapshotSizeFloorBytes` were validated, persisted, sent over `PUT /settings`
and plumbed through six files. `SnapshotWriter.Write` assigned them to a local and never read it;
they bounded `snapshot_dirs`, which migration 007 dropped. The CLI help still described `--depth` as
the "snapshot_dirs depth cap". Removed, along with `PrintSnapshotDirEstimate`, which projected the
weekly cost of that same dropped table.

Their doc comment claimed they "decide whether a week of 15-minute history costs megabytes or
gigabytes". The measurement above says what actually decides that: how often a baseline runs.

## E: was half a terabyte over, and every tick succeeded

Found on 21 Oct because the Explorer looked wrong, not because anything reported a problem: E: had
not failed a scan since August.

| Volume | Used, per Windows | Strata's total, as found | After a fresh baseline |
|---|---:|---:|---:|
| E: | 938.8 GB | **1,445.9 GB** | 932.4 GB |
| C: | 872.8 GB | 938.0 GB | 834.4 GB |
| D: | 894.6 GB | 900.5 GB | 889.2 GB |

D: looked right only because its error happened to land near the true figure. On E:, nearly all of
it sat in folders that no longer had anything in them:

| Folder on E: | Strata | Walked from disk |
|---|---:|---:|
| `GamcoreGames` | 552.9 GB | empty |
| `GamcoreInput` | 19.8 GB | empty |
| `GamcoreUploads` | 44.2 GB, **-12,760 files** | 106.5 GB, 286,281 files |

**How the drift was located.** For every directory, the bytes its rollup implies it holds directly
(its subtree total minus its children's) against the sum of its rows in `files`. That separates the
two ways it can be wrong. The `files` table was close to the disk - 968 GB counted on E:, about 25 GB
high - and the rollup was 478 GB above *it*: rows had gone without their bytes coming off the
totals. Drift by directory, as found:

| Volume | Directories drifting | Net drift, rollup over `files` |
|---|---:|---:|
| E: | 15,441 of 574,122 | 716.6 GB (478 GB of it reaching the root) |
| C: | 5,550 of 958,745 | 89.3 GB |
| D: | 93 of 15,915 | 40.0 GB |

The difference on E: between 716.6 GB and 478 GB is directories whose parent was never recorded, so
their deltas never reached the root in either direction.

The history of `E:\GamcoreGames` shows it was not one event. It is a working folder that a pipeline
fills with unpacked Ren'Py games and empties again, and on every cycle it fell back less than it rose:
0 to 553 GB over six weeks.

**The four causes**, each fixed in its own commit with tests:

1. **A file replaced by renaming another over it was never subtracted.** The replacement's records
   arrive first, its upsert on `(parent, name)` overwrote the original's row, and the original's
   deletion then found nothing to subtract. Atomic saves, extractors and Steam updates all do this.
   This is the 478 GB. Reproduced in a test before fixing: replacing 10 MB with 10 MB left +10 MB.
2. **A capped tick skipped the rest of its read buffer.** The cursor came from the buffer header,
   past every record in the 64 KiB buffer, even when the 50,000-record cap stopped parsing partway.
   E: had 1,462 capped ticks. Lost deletions are the 25 GB in `files`, and lost directory records are
   the unparented directories.
3. **Removing a hardlink was read as a move to it**, retiring the surviving link and recording the
   file where it no longer was. Found by the tests written for (1).
4. **A directory record did nothing unless the directory was new.** Moved directories kept their old
   parent, deleted ones stayed forever (E: had 574,122 recorded directories; the baseline found
   105,049), and `subtree_dirs` was never maintained, so the Explorer could not expand a folder made
   since the last baseline. `subtree_on_disk` was also being moved by the logical delta.

Found along the way: every tick recorded the free space from when the process started, because the
watcher kept the volume it was created with. E: logged ten distinct values in a week.

**After the fixes, a fresh baseline of each volume:** E: 24.5 s, D: 2.8 s, C: 229.3 s. The same
per-directory comparison then finds **zero drift on all three volumes**.

**What catches the next one.** `RollupDrift` compares, after each scheduled tick, the gap between
recorded size on disk and used space with the same gap at the last baseline, and past 1% of the
volume runs the gated, rate-limited recovery baseline. Its first ticks on the fixed build, before
the requested baselines ran, measured E: at 474.7 GB over, C: at 88 GB and D: at 9.5 GB, and queued a
recovery for each that waited for the machine to be idle, as it should.

## Still not measured

- **Whether the drift guard ever fires again.** It should not, now that the causes found are fixed.
  If `scan_log` shows "the recorded total had drifted" on any volume, there is a fifth cause, and the
  per-directory comparison above is how to find it.
- **Scan cost with a game running.** Unchanged since Phase 2, and now the oldest open item in the
  file. The deferral log proves the scanner *stops* for a game; it has still never been measured
  *running* against one.
- **Whether a 512 MB journal ends the recovery baselines.** The arithmetic and the tests say it
  should. Twenty-two more days of `scan_log` is what would prove it - the same reading as this one,
  taken again.
- **What a 27 s write transaction does to API latency.** The WAL growth says the checkpointer
  notices. No endpoint was timed during one.
- **`snapshot_top_files`.** Still schema-only, still the last unpopulated table.
