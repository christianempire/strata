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

The one that runs every 15 to 30 minutes and makes the product viable. **Built and verified end to
end, unelevated**: creating 65 MB of files reports as `+62 MB  2 files  ...\usn-test\game\data`,
deleting them reports the inverse, and moving a file transfers the bytes between directories rather
than reading as an unrelated delete and create.

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

**Elevation: settled, and better than the brief assumed.** Reading the journal needs no elevation at
all. A directory handle on the volume root plus `FSCTL_READ_UNPRIVILEGED_USN_JOURNAL` is sufficient;
the `\\.\C:` + GENERIC_READ form the brief describes is what needs administrator. Only
`FSCTL_CREATE_USN_JOURNAL` genuinely requires it, one time per volume, which is why
`strata enable-journal` is a separate verb rather than something the scanner does silently.

The catch is that the unprivileged read **redacts file names** — every record returns a name length
of zero, in both V2 and V3. Paths are therefore resolved from the file ID via `OpenFileById` plus
`FileNameInfo`, on a handle already being opened to read the size. That returns the file's *current*
path, so a rename's old path comes from our own rows and only the new one from the filesystem, and
deletions resolve entirely from the database. Running elevated would return names inline; the
measured cost says that is an optimisation rather than a requirement.

**Budget, measured.** A steady-state tick is about 250 ms; the largest bite the scanner takes in one
go, 50,000 records with 7,000 metadata reads, is about 1 s. The brief's 2 s budget holds.

**Backlogs** are taken in bounded bites with the cursor persisted where they stop. The journal is a
durable queue, so falling behind is safe and self-healing, while draining a several-hundred-thousand
record backlog synchronously is precisely the stall the design exists to prevent.

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

**`files`** — **current state only**, one row per *directory entry*, updated in place by Tier 2.
Keyed `(volume_id, parent_id, name)`, which is the identity the filesystem itself uses: a file ID is
an attribute of an entry, not its identity, and hardlinks share one. Keying on the file ID instead
collapsed 135,520 of 3.67M entries on the target machine, losing every path but one per file.

Stores `parent_id` + `name` rather than a path string: paths are reconstructed by walking parent
links, which avoids storing the same directory prefix a hundred thousand times and makes a directory
rename an O(1) update instead of a subtree rewrite. Clustering on that key also turns "list the
children of this directory" into a contiguous range scan, which is the query the Explorer screen
will run constantly.

A secondary index on `(volume_id, file_id)` fans a size change out across a file's links and finds
entries whose name the journal redacted. It costs 175 MB of the 447 MB database and earns it. It has
to be referenced with `INDEXED BY`: without statistics SQLite plans the lookup as a range scan over
the whole volume, measured at 442 ms against 0.010 ms.

**`dir_totals`** — rolled-up subtree bytes and file counts per directory. Populated wholesale by
Tier 1, and from Phase 2 maintained **incrementally**: when a file's size changes by some delta,
Tier 2 walks its ancestor chain adding the delta to each. Without this table, producing a snapshot
would mean a recursive aggregation over every row in `files` on every tick, which would blow the
Tier 2 budget for reasons unrelated to the journal. This table is not in the original brief; see
`DESIGN-REVIEW.md` A6.

**`snapshots`** — one row per scan: volume, timestamp, tier, duration, totals, file count. Cheap.

**`dir_history`** — the historical record, and the table that decides whether this design is
affordable. One row per directory per moment its subtree total moved, at any depth and at any size.
Values are absolute rather than deltas, which costs the same on disk and queries better: "the size
of X at time T" is the latest row at or before T, one seek, with no replay from an anchor and no
drift accumulating across a year.

> **Replaced `snapshot_dirs` in Phase 3, on measurement.** That table recorded every directory
> within a depth cap and above a size floor on *every* snapshot, whether or not anything had
> happened to it: 5,740 rows every fifteen minutes on C:, 429 MiB a week. Sampled against real
> activity, a mean of **68** directories per tick actually moved — and the cap and floor between
> them hid **55%** of them. This records what changed, and costs **18.2 MiB a week** for complete
> coverage. See `docs/STORAGE-MODEL.md`.

A directory is recorded once it has drifted past a configurable band since its last row. The band
defaults to zero, meaning every change: at these sizes exactness is worth more than the megabytes.
Raising it to 1 MiB would cost 4.0 MiB a week instead of 18.2.

**`dir_names`** — the name dimension. One row per directory ever seen, storing `parent_id` + `name`
rather than a path, so a prefix is written once instead of once per descendant. 61% of the table it
replaced was repeated path text: 8.2 MB across only 5,742 distinct paths.

Separate from `dir_totals` for one specific reason: a Tier 1 baseline truncates that table and
rebuilds it, which would orphan every history row older than the last baseline. This one is merged
into, never replaced. Keying on the directory id is safe across deletion because an NTFS file
reference number is a 48-bit MFT index plus a 16-bit sequence number, so a recycled index yields a
different id rather than a stale name.

**`snapshot_top_files`** — the ~500 largest files per snapshot, so "what single file ate 8 GB" stays
answerable after that file is deleted. Schema exists from Phase 1. **Still never populated**, and now
the only thing in the storage model that is schema-only.

**`settings`** — key/value, added in Phase 3a. Everything the resident scanner is configured by,
which through Phases 1 and 2 was command line flags.

**`events`** — the derived, human-readable change log: `{ when, volume, path, delta_bytes, kind }`
where kind is `grew | shrank | appeared | vanished`. Written by the diff engine after each snapshot.
**This is the table the most valuable screen in the product reads from**, and it is small, so it
keeps full resolution when directory rollups get downsampled.

**Retention**, run nightly, RRD-style and configurable: every snapshot for 7 days, hourly for 30
days, daily for 1 year, weekly forever.

> **One constraint the sparse history imposes on retention.** Because "the size at T" is the latest
> row at or before T, downsampling must never delete a directory's last row before the horizon — it
> would leave that directory answering from a *newer* measurement than it should. Retention is
> therefore "keep the newest row per directory per bucket", not "delete rows older than X". Phase 4
> work, flagged here because the schema was shaped around it.

## Diff engine

Lives in `Strata.Core`, takes data structures in and returns data structures out, has no I/O, and is
heavily unit-tested. Phase 2.

Given two snapshots it produces a ranked tree of changes, with four behaviours that distinguish it
from a naive subtraction:

1. **Rolled-up deltas.** A parent shows its subtree's net change so the UI can drill down.
2. **Shallowest sufficient attribution.** If 200 files appeared under
   `D:\Steam\steamapps\common\Foo`, the answer is one row — `...\common\Foo (+32 GB, 200 new files)`
   — not 200 rows. The rule: descend from the root while a single child accounts for most of the
   node's net delta (configurable, 90%); stop and summarise as soon as the change is spread across a
   few siblings. This is the core intelligence of the product.

   Two exceptions, both found by testing rather than by reasoning. Summarising is wrong when the node
   is a corridor with many independent movers — thirty directories under the volume root each growing
   reported as the single useless row "D: +465 GB" — and wrong when growth in one branch cancels
   deletion in another, where the net is a misleading zero that hides both halves. In either case the
   report expands into the children instead.
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

### API surface (built)

All under `/api`, JSON, no wrapper envelope. The only shape anything is wrapped in is
`{ "error": "..." }`, and only on a failure.

```
GET  /volumes
GET  /snapshots?volume=&from=&to=&limit=
GET  /tree?volume=|snapshot=&path=&depth=&limit=
GET  /diff?from=&to=&path=&minBytes=&limit=
GET  /events?volume=&from=&to=&minBytes=&limit=
GET  /series?volume=&from=&to=&buckets=&top=
GET  /scan-log?volume=&from=&to=&result=&limit=
POST /scan                     { "volume": "C:", "tier": "full" | "incremental" }
GET  /scan/status
GET|PUT /settings
```

Hosted by `strata serve`, in the same process as the scanner. Bound to `127.0.0.1` and `::1` by
address rather than to the string `localhost`, because local-only is a hard constraint rather than a
default: an explicit Kestrel `Listen` also overrides anything `ASPNETCORE_URLS` might try to bind.

Five things about it are decisions rather than details:

- **`volume` and `snapshot` are the same question at different times.** `/tree?volume=` reads
  current state from `dir_totals`; `/tree?snapshot=` reads `dir_history` as it stood at that
  snapshot's instant. Since Phase 3 both are unbounded, where the historical side used to be capped
  by depth and by a size floor. Paths on the historical side resolve against `dir_names`, so a
  directory that no longer exists still resolves — which is the point of history outliving its
  directories.
- **Tree nodes carry a parent id and a leaf name, not a path.** The caller already knows the path it
  asked for, and every other path is that plus the names on the way down. Sending materialised paths
  would have cost roughly 90 MB of strings on a full walk of C:, to send the caller something it can
  assemble from what it already has.
- **Large trees stream, and a caller that stops reading stops the walk.** The whole of C: is 571k
  directories and 107 MB of JSON; the first byte arrives in 2.4 ms and the rest takes 3.4 s. The same
  request at the default limit of 10,000 nodes finishes in 93 ms without having queried the rest of
  the tree.
- **`POST /scan` always answers 202.** The scan has not happened when it returns, and a baseline will
  not have happened for another few minutes, so the response says which. It runs on the scheduler's
  own dedicated thread, never on a request thread.
- **`PUT /settings` applies the fields present and leaves the rest alone.** A strict replacement would
  mean a client omitting a field it does not know about silently resets it, and this record gains
  fields every phase.
- **`/series` exists because the Timeline could not be built without it.** A stacked area of used
  space by top-level directory, assembled from `/tree`, would need one tree walk per snapshot — 672
  of them for a week at a fifteen-minute cadence. This is one clustered range read per top-level
  directory over the whole window, which is the query `dir_history` was keyed for and the reason
  migration 005 left it without a secondary index on time. It carries what the OS reported used at
  each bucket edge as well, so the difference between that and the sum of the bands is drawable as a
  hatched band rather than being quietly left out.
- **`/scan-log` records the ticks that did not run.** Deferrals used to go to the console at Debug
  and then nowhere, which left the project's first non-negotiable unverifiable after the fact. A
  skipped tick is the row that proves the scanner is behaving, so it is written rather than being
  the absence of a write. The endpoint returns the window's summary alongside the rows, because the
  totals cover the whole window rather than the returned page and two endpoints could disagree.
- **`/scan/status` reads process memory, not the database.** `POST /scan` answers 202 and a baseline
  runs for 211 s, so something has to distinguish working from frozen — and the one moment that
  matters is the moment the database is busiest. Its percentage names its own denominator: entries
  against the last scan's count when there has been one, bytes against what Windows reports used
  when there has not, which is exactly the first run.

Settings live in a `settings` table rather than a config file, read at the start of every scheduler
cycle so a change from the UI takes effect on the next tick. The console verbs deliberately ignore
that table and use their flags, so a measurement run stays reproducible.

### UI (Phase 3b, built)

Dark, dense, information-first. An instrument panel, not a marketing page. Angular 19 standalone
components in `src/Strata.Web`, compiled by `ng build` at build time and served as static files by
the same host that serves the API.

Five screens: Overview (volumes grouped by physical disk, capacity reconciled against what Windows
reports, 30-day sparkline), **Timeline** (the headline: a stacked area over `/series` with the
`events` feed beneath it and a two-way link between them), Explorer (ECharts treemap, one level at a
time), Compare (two snapshots, ranked attributed changes including the `elsewhere in …` remainder
rows), Settings (intervals, exclusions, thresholds, manual scan, and the auditable scan-history log).

Three things about it are decisions rather than details:

- **Same origin is doing real access control.** The API sends no CORS header at all, so the
  browser's own rule is what stops a page from anywhere else reading this machine's disk contents
  through localhost — and that only holds because the app is served from here rather than from a dev
  server or a `file://` page.
- **Fonts are bundled rather than linked.** A `<link>` to `fonts.googleapis.com` is a network call,
  and one that reports every time a disk-contents tool is opened. 77 KB for both families, latin
  subsets, variable weight.
- **The npm build is opt-in from MSBuild.** `dotnet build` and `dotnet test` have to work on a
  machine with no Node; the backend is four projects and 170 tests that have nothing to do with the
  UI. `-p:BuildWebUi=true` runs it when you want it.

Visual direction came from mockups in `docs/design/`, which are the visual language rather than a
starting point. Deviations are recorded in `docs/design/README.md`.

## Non-goals

Not building, and pushing back on requests to: file deletion or cleanup actions (Strata is
read-only), cloud sync, multi-machine support, duplicate-file detection, authentication, an
installer, a plugin system, or a Linux/macOS port.
