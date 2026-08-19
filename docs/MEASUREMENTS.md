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
- **`/api/series` latency at a year of history.** Timed only against the current database, which
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
