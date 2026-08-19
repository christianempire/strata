# Phase 1 measurements

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
