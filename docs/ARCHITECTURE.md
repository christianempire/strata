# Strata — architecture

## What this is

Every disk-usage tool on Windows shows you a snapshot: here is what is on your disk right now.
WinDirStat, WizTree and TreeSize all answer that question, and WizTree in particular answers it
faster than we will.

Strata answers a different question: **what grew, when, and where?**

> "Your D: drive gained 40 GB since last Tuesday. 32 GB of it landed in `D:\Steam\steamapps`."

That requires recording history and diffing it, which requires snapshots frequent enough to be
useful (every 15 to 30 minutes) and cheap enough to be invisible. Those two requirements together
are what shape every decision below.

## The two constraints that drive everything

**1. Invisible.** The scanner runs permanently in the background on a machine used for work and
gaming. If a scan causes a frame drop, the tool is uninstalled and the project has failed. This is
treated as a correctness requirement, not a performance goal.

**2. Frequent without rescanning.** A full walk of a 930 GB NTFS volume takes tens of seconds. Doing
that every 20 minutes, three volumes over, is both visible and pointless: almost nothing changed.
So the common-case update must cost proportional to *what changed*, not to what exists.

The NTFS USN Change Journal is the mechanism that makes the second constraint satisfiable, and
therefore it is the centre of the design.

## Scan architecture: three tiers

A volume is scanned by one of three strategies, selected at runtime per volume, all behind
`IVolumeScanner`. The interface exists for the strategy selection and for testability, and it has
three real implementations, so it is not speculative abstraction.

### Tier 1 — full baseline scan

Runs rarely: first sight of a volume, or when the USN journal has been invalidated underneath us.
Enumerates the entire volume and records every file.

**Mechanism.** `GetFileInformationByHandleEx` with `FileIdBothDirectoryRestartInfo` /
`FileIdBothDirectoryInfo`, reading `FILE_ID_BOTH_DIR_INFO` records out of a pooled 64 KB buffer. One
directory handle at a time, opened with `FILE_FLAG_BACKUP_SEMANTICS`.

That record type is the whole reason this is cheap. A single directory read yields, per entry and
with no additional syscall: the file ID, logical size (`EndOfFile`), size on disk
(`AllocationSize`), attributes, and all four timestamps. Any approach that has to open or stat files
individually to get those fields is doing millions of extra syscalls.

The brief suggested `NtQueryDirectoryFile` for this. We use the documented Win32 wrapper instead; it
issues the same call with the same information class and returns the same records, without depending
on an ntdll contract Microsoft reserves the right to change. The brief's stated fallback,
`FileSystemEnumerable`, is not usable at all: it exposes neither file IDs nor allocation size, and
file IDs are the join key for the entire storage model. Reasoning in `DESIGN-REVIEW.md` A1/A2.

**Parallelism.** A bounded work queue of pending directories, drained by N dedicated worker threads
(default `min(8, cores / 4)`, configurable). The brief specified `min(4, cores / 2)`; measurement
showed four workers well short of the knee, so the default was raised. See docs/MEASUREMENTS.md.

Each worker takes a directory, enumerates it, streams file records to the
writer, and pushes discovered subdirectories back onto the queue. Completion is detected with an
outstanding-directory counter rather than by queue emptiness, since an empty queue mid-scan does not
mean the scan is done.

Dedicated threads rather than the thread pool, because each worker puts itself into
`THREAD_MODE_BACKGROUND_BEGIN` for its lifetime, and a pooled thread left in background mode and
handed back to the pool would silently throttle unrelated work.

**Correctness details.**

- *Reparse points.* Directories carrying `FILE_ATTRIBUTE_REPARSE_POINT` are recorded as links and
  **not traversed**. This prevents both infinite recursion through junctions and the more common
  problem of counting the same bytes twice via a junction into an already-scanned tree.
- *Cycles.* Visited directory IDs are tracked, so even a reparse point we did decide to follow, or a
  filesystem oddity, cannot loop.
- *Hardlinks.* Deduplicated by file ID within a volume. Directory records carry no link count, so
  detection is "have I seen this file ID before". The **first path encountered owns the bytes**;
  later links are recorded with their reported logical size but contribute zero to rollups and carry
  an `IsHardLink` flag. This keeps directory rollups summing exactly to the volume total, which
  matters mainly for `C:\Windows\WinSxS`, which is largely hardlinks into `System32`. Measured on
  the target machine: 135,505 links sharing 18.2 GB on C:, effectively none on the other volumes.
  *Known limitation:* `files` is keyed `(volume_id, file_id)`, so only the byte-owning path is
  persisted. Totals are unaffected; listing every path a file is reachable by is not yet possible.
- *Access denied.* Expected, not exceptional. Counted and reported, never thrown. Unelevated, parts
  of `C:` are genuinely unreadable, and we would rather show "1,204 directories not readable" than
  quietly under-report.

**Cost, measured.** Warm, 7.7 s for a 3M-file volume. **Cold, 211 s for the same volume** - and cold
is the case Tier 1 actually runs in, since it runs on first sight of a volume. Scan cost tracks
*directory count*, not capacity: a 853 GB volume with 10k directories scans in under a second, while
a 875 GB volume with 570k directories takes a hundred times longer.

That is acceptable because it is rare and runs at very low I/O priority, but it means first-run has
to be presented as a minutes-long background task rather than something that finishes while you
watch.

### Tier 2 — incremental delta from the USN journal

The one that runs every 15 to 30 minutes and makes the product viable. **Not yet built (Phase 2);**
described here because Phase 1's schema is shaped to receive it.

Open the volume, `FSCTL_QUERY_USN_JOURNAL` for the journal ID and USN bounds, then
`FSCTL_READ_USN_JOURNAL` from the persisted cursor. Every create, delete, rename and data-extend
since the last read comes back as `USN_RECORD_V2`/`V3` with `FileReferenceNumber`,
`ParentFileReferenceNumber`, `FileName` and `Reason` flags. Persist `(VolumeGuid, JournalId,
NextUsn)` after every successful read.

**The gotcha that makes this work.** USN records do not carry file sizes. So the sequence is: read
records, reduce to the distinct set of changed file IDs, then stat only those. A few thousand stats
is milliseconds. Reading the journal tells us *which* files to look at, which is the expensive part
that a full walk spends all its time rediscovering.

Deleted files cannot be statted, and do not need to be: their last known size is already in `files`,
which is why `files` holds current state rather than being append-only.

**Invalidation.** If `JournalId` differs from the persisted one, or the requested USN is below
`FirstUsn`, the journal was reset or wrapped, and we fall back to Tier 1. If no journal exists on a
volume, create one (elevated, one-time) or fall back to Tier 3.

**Open question, deliberately not settled yet.** The brief assumes Tier 2 requires running as
administrator permanently. `FSCTL_READ_UNPRIVILEGED_USN_JOURNAL` may remove that requirement, which
would be a significantly better posture for a permanently-resident tray app. Phase 2 opens with a
probe to establish what actually works unelevated before the manifest is decided. See
`DESIGN-REVIEW.md` B1.

### Tier 3 — throttled fallback

Non-NTFS volumes, or NTFS where we cannot get at the journal. A full walk on a much longer interval
(default 6 h), only when idle. There are no stable file IDs to rely on here, so entries are keyed by
a surrogate derived from the path. These volumes are marked **reduced fidelity** in the UI, because
their change attribution is coarser and their history is lower-resolution, and pretending otherwise
would be dishonest.

### Explicitly out of scope: Tier 0

Parsing the raw `$MFT` directly, as WizTree does, would give sub-second full scans. Not building it.
Nothing in this design prevents adding it later: it would be one more `IVolumeScanner` producing the
same `FileRecord` stream, and everything downstream is indifferent to where records came from.

## Resource discipline

Implemented as hard requirements, not best-effort.

| Guard | Mechanism | Notes |
|---|---|---|
| Low I/O priority | `THREAD_MODE_BACKGROUND_BEGIN` per scan thread | Per-thread, not process-wide, so the API and UI are never throttled by a scan. Released when the thread finishes. |
| Low I/O priority (per handle) | `FileIoPriorityHintInfo` = `IoPriorityHintVeryLow` | Complementary; one call per directory handle. |
| Gaming / fullscreen | `SHQueryUserNotificationState`, re-checked every 60 s | Defers on `QUNS_RUNNING_D3D_FULL_SCREEN`, `QUNS_PRESENTATION_MODE`, `QUNS_BUSY`. Known gap: misses borderless-windowed games, so Phase 2 supplements with a foreground-window-covers-monitor check. |
| User actively typing | `GetLastInputInfo` | Defers Tier 1 only. Tier 2 is cheap enough to run regardless. |
| System CPU load | `GetSystemTimes` deltas, default threshold 70% | Deliberately not `PerformanceCounter`, which is slow to initialise and flaky. |
| Removable / network | `GetDriveType` | Never scanned unless explicitly opted in. |
| Tier 2 latency | Hard budget of 2 s in the common case | Over-budget ticks are logged. Very large change sets are processed in capped chunks across ticks rather than blocking; the journal is a durable queue, so falling behind is safe. |

## Storage model

The naive design — one full file tree per snapshot — produces gigabytes within a week. The model
below separates *current state*, which is stored once and mutated, from *history*, which is stored
as aggregates.

**`volumes`** — identity and capacity. Keyed on the volume GUID path (`\\?\Volume{guid}\`), because
drive letters get reassigned and letter-keyed history would silently corrupt if `D:` and `E:` ever
swapped. The letter is a mutable display attribute.

**`files`** — **current state only**, one row per file, updated in place by Tier 2. Keyed
`(volume_id, file_id)`. Stores `parent_id` + `name` rather than a path string: paths are
reconstructed by walking parent links, which avoids storing the same directory prefix a hundred
thousand times and makes a directory rename an O(1) update instead of a subtree rewrite.

**`dir_totals`** — rolled-up subtree bytes and file counts per directory. Populated wholesale by
Tier 1, and from Phase 2 maintained **incrementally**: when a file's size changes by some delta,
Tier 2 walks its ancestor chain adding the delta to each. Without this table, producing a snapshot
would mean a recursive aggregation over every row in `files` on every tick, which would blow the
Tier 2 budget for reasons unrelated to the journal. This table is not in the original brief; see
`DESIGN-REVIEW.md` A6.

**`snapshots`** — one row per scan: volume, timestamp, tier, duration, totals, file count. Cheap.

**`snapshot_dirs`** — the historical rollup, and the table that decides whether this design is
affordable. Per snapshot, aggregated bytes and file count per directory, limited by a depth cap
(default 6) and a size floor (default 10 MB). Stores the **path as text**, denormalised on purpose:
file IDs churn, and history has to stay readable after the underlying directory is gone.

> **Measured and settled.** 2,984 qualifying directories on the worst volume, not the tens of
> thousands feared, which works out at ~260 MB per week of full-resolution history across all three
> volumes. The design is affordable as specified, and the proposed mitigations (changed-rows-only
> against a daily anchor, decoupled rollup cadence) were **not** built. The depth cap was raised from
> 6 to 8 on the strength of the numbers, since the extra two levels make growth inside deep AppData
> paths attributable and cost about 80 MB a week. See `MEASUREMENTS.md`.

**`snapshot_top_files`** — the ~500 largest files per snapshot, so "what single file ate 8 GB" stays
answerable after that file is deleted. Schema exists from Phase 1; populated from Phase 2.

**`events`** — the derived, human-readable change log: `{ when, volume, path, delta_bytes, kind }`
where kind is `grew | shrank | appeared | vanished`. Written by the diff engine after each snapshot.
**This is the table the most valuable screen in the product reads from**, and it is small, so it
keeps full resolution when directory rollups get downsampled.

**Retention**, run nightly, RRD-style and configurable: every snapshot for 7 days, hourly for 30
days, daily for 1 year, weekly forever.

## Diff engine

Lives in `Strata.Core`, takes data structures in and returns data structures out, has no I/O, and is
heavily unit-tested. Phase 2.

Given two snapshots it produces a ranked tree of changes, with four behaviours that distinguish it
from a naive subtraction:

1. **Rolled-up deltas.** A parent shows its subtree's net change so the UI can drill down.
2. **Shallowest sufficient attribution.** If 200 files appeared under
   `D:\Steam\steamapps\common\Foo`, the answer is one row — `...\common\Foo (+32 GB, 200 new files)`
   — not 200 rows. The rule: descend from the root while a single child accounts for most of the
   node's net delta (threshold configurable, ~90%); stop and report as soon as the change is
   genuinely spread across siblings. This is the core intelligence of the product.
3. **Net versus churn.** A directory that wrote and deleted 50 GB has a net delta near zero but is
   very interesting for SSD wear, so both are tracked and surfaced separately.
4. **Moves are moves.** Renames and moves are detected by file ID and reported as a move, not as a
   delete plus a create. This is a direct payoff of keying on file IDs rather than paths.

## Application shape

A single .NET 9 process, one executable:

- an `IHostedService` background scanner,
- an ASP.NET Core minimal API on `localhost:5177`,
- the built Angular app served as static files from the same host,
- a Windows tray icon with Open UI / Scan now / Pause / Exit.

Data in `%LOCALAPPDATA%\Strata\`. SQLite via `Microsoft.Data.Sqlite`, WAL mode, raw ADO on the write
path. Measured footprint: **396 MB for 3.67M files across three volumes**, roughly 108 bytes a row.
That is a one-time cost rather than a growth rate, because `files` holds current state and is updated
in place.

**Phase 1 is none of that.** It is the same solution with a console entry point exposing
`strata volumes` and `strata scan`, so that real numbers — scan duration, file counts, resulting
database size, directory histogram — exist before the rest is committed to.

### API surface (Phase 3)

All under `/api`, JSON, no wrapper envelope, large trees streamed with `IAsyncEnumerable`:

```
GET  /volumes
GET  /snapshots?volume=&from=&to=
GET  /tree?snapshot=&path=&depth=
GET  /diff?from=&to=&path=
GET  /events?from=&to=
POST /scan
GET|PUT /settings
```

### UI (Phase 3, gated on mockups)

Dark, dense, information-first. An instrument panel, not a marketing page. Five screens: Overview
(per-volume capacity and 30-day sparkline), **Timeline** (the headline screen: stacked area of used
space over time plus the `events` feed for the selected range), Explorer (ECharts treemap with
drill-down, sunburst alternate), Compare (two snapshots, diff tree), Settings (intervals,
exclusions, retention, thresholds, manual scan, and an auditable scan-history log).

Visual direction is supplied separately as mockups in `docs/design/`. We build to those rather than
inventing a palette or accepting a component library's defaults. If they are missing when Phase 3
starts, stop and ask.

## Non-goals

Not building, and pushing back on requests to: file deletion or cleanup actions (Strata is
read-only), cloud sync, multi-machine support, duplicate-file detection, authentication, an
installer, a plugin system, or a Linux/macOS port.
