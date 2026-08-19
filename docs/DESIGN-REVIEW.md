# Design review of the Strata brief

Written before any implementation, per the brief's "tell me anything you think is wrong or
unnecessarily complex" instruction. Items are ordered by how expensive they would be to fix later.

Verdict up front: **the architecture is sound and the three-tier scan design is the right call.**
Using the USN journal as the delta source is correct, and is genuinely what makes 15-minute
snapshots affordable. Most of what follows is either a correction to a specific API choice, or a
place where the storage math does not work out as specified.

---

## A. Things I believe are wrong

### A1. `FileSystemEnumerable` cannot produce the schema. It is not a viable fallback. (blocking)

The brief offers `NtQueryDirectoryFile` + `FileIdBothDirectoryInformation` "or at minimum .NET's
`FileSystemEnumerable`". These are not interchangeable:

- .NET's enumerator is built on `FILE_FULL_DIR_INFO`, which contains **no file ID**.
- The entire storage model is keyed on file IDs (`files.file_id`, `files.parent_id`), and Tier 2
  correlates USN records to rows by `FileReferenceNumber`. Without file IDs there is no join.
- It also exposes `Length` (logical) but **not `AllocationSize`**, so `size_on_disk` would need a
  separate `GetCompressedFileSize` call per file, roughly doubling the syscall count of a scan to
  recover a field the directory record already contains for free.

So `FileSystemEnumerable` is only usable for Tier 3 on non-NTFS volumes, where there are no stable
file IDs anyway and we would have to synthesise a surrogate key from the path.

**Decision taken:** Tier 1 uses the ID-bearing directory enumeration from day one.

### A2. Use `GetFileInformationByHandleEx`, not `NtQueryDirectoryFile`. (adopted)

`GetFileInformationByHandleEx` with `FileIdBothDirectoryRestartInfo` / `FileIdBothDirectoryInfo`
returns the identical `FILE_ID_BOTH_DIR_INFO` records that `NtQueryDirectoryFile` returns for
`FileIdBothDirectoryInformation`. It is a thin wrapper over exactly that call, so throughput is the
same, but it is documented, stable Win32 rather than an ntdll export with an `NTSTATUS` /
`IO_STATUS_BLOCK` contract that Microsoft reserves the right to change.

There is no benefit to the ntdll route here. We do not need the extra knobs it offers: no filename
wildcard filtering, and no async completion against a scan we are deliberately running at low I/O
priority. Taking the documented API.

### A3. `PROCESS_MODE_BACKGROUND_BEGIN` is the wrong scope and will make the UI sluggish. (important)

`SetPriorityClass(PROCESS_MODE_BACKGROUND_BEGIN)` is **process-wide**. It drops the whole process to
background CPU priority, background memory priority, and very-low I/O priority. From Phase 3 onward
this same process also serves the ASP.NET Core API and the Angular UI. Putting the process into
background mode during a Tier 1 scan means the user's own UI interactions get throttled behind the
scan they are watching, which is the opposite of what the brief wants.

**Better lever, same effect on I/O:** `SetThreadPriority(GetCurrentThread(), THREAD_MODE_BACKGROUND_BEGIN)`.
It is per-thread, and it lowers that thread's I/O priority to Very Low just as the process-wide
version does. Scan threads get it; API threads never do.

Two consequences:

- Scan workers must be **dedicated threads, not thread-pool threads**. A pooled thread left in
  background mode and returned to the pool would silently poison unrelated work. Dedicated threads
  make the BEGIN/END pairing trivially correct.
- Because the workers are dedicated threads doing synchronous Win32 calls, `Channel<T>` buys nothing
  over `BlockingCollection<T>` and forces sync-over-async at the consumer. Using
  `BlockingCollection<T>` as the bounded work queue: same shape, same parallelism cap, no
  `.GetAwaiter().GetResult()`.

A third, complementary lever the brief does not mention: `FileIoPriorityHintInfo` via
`SetFileInformationByHandle` sets `IoPriorityHintVeryLow` **per handle**. Belt and braces, and it
costs one call per directory handle.

### A4. `SHQueryUserNotificationState` misses borderless-windowed games. (important)

This is the standard API and worth keeping, but it is worth knowing what it does not catch.
`QUNS_RUNNING_D3D_FULL_SCREEN` is reported for *exclusive* fullscreen. Most modern games, and the
default in most engines now, run **borderless fullscreen windowed**, which is an ordinary window.
For those, `SHQueryUserNotificationState` returns `QUNS_ACCEPTS_NOTIFICATIONS` and the scan proceeds
straight into the frame budget. Given the brief calls frame drops a project failure, this API alone
does not meet the bar.

**Recommended supplement (Phase 2):** take `GetForegroundWindow()`, compare its rect against the
rect of the monitor it is on, and treat "covers the whole monitor, is not the shell, is not us" as
presentation-like. Combined with the notification-state check, that covers both modes. Costs two
syscalls per 60 s poll.

### A5. The storage model still grows too fast as specified. (RESOLVED - I was wrong)

> **Settled by measurement.** The qualifying directory count on the worst volume is 2,984, not the
> 40,000 I feared - off by more than an order of magnitude. Full-resolution history costs ~260 MB
> per week across all three volumes at the brief's own defaults. The design works as written and
> none of the mitigations below were built. See docs/MEASUREMENTS.md.

This is the one that worries me most, because it is the failure mode the brief was explicitly
written to avoid, and I do not think the stated parameters get there.

`snapshot_dirs` is "aggregated bytes + file count per directory, to depth 6, above a 10 MB floor",
written per snapshot, with snapshots every 15 to 30 min and 7 days of full-resolution retention.

The arithmetic:

```
96 snapshots/day (at 15 min)  x  7 days full retention  =  672 snapshots
D directories above the floor  x  672  x  ~60-100 bytes/row (path string dominates)
```

Everything turns on `D`. If `D` is 5,000 that is ~250 MB per volume per week and it is fine. If `D`
is 40,000, which I think is plausible on a full 930 GB volume because a directory qualifies whenever
its whole *subtree* exceeds 10 MB (true of nearly every ancestor of anything large), it is ~2 GB per
volume per week and the design has failed on its own terms. Three volumes makes that ~6 GB.

I do not want to guess at `D`, and **this is exactly the number Phase 1 exists to produce.** So the
Phase 1 CLI reports a histogram of directory counts by depth and by size floor, and we size this
decision on the real value from your machine rather than on my estimate.

Three fixes, in my order of preference, to apply once we see the number:

1. **Write `snapshot_dirs` rows only for directories whose rollup changed since the previous
   snapshot**, plus a full row set once daily as an anchor. Reconstruct any snapshot by replaying
   deltas from the nearest anchor. Between two 20-minute snapshots on an idle machine the changed
   set is dozens of directories, not tens of thousands. This is the big win, and it composes
   perfectly with Tier 2, which already knows exactly what changed.
2. **Decouple the `snapshot_dirs` cadence from the snapshot cadence.** Take Tier 2 deltas every
   15 min (cheap: updates `files`, emits `events`), but materialise the directory rollup hourly.
   `events` is the table the headline screen reads, and it stays fine-grained either way.
3. **Make the floor relative, not absolute**, e.g. 0.005% of volume size rather than a flat 10 MB,
   so the row count does not scale with disk size.

(1) and (2) are complementary and I would expect to do both.

### A6. Nothing maintains the directory rollup between snapshots. (gap)

Tier 2 updates `files` in place. But `snapshot_dirs` needs subtree totals, and recomputing those
means a recursive aggregation over every row in `files`, millions of rows, on every 15-minute tick.
That would make Tier 2 fast at reading the journal and then slow at everything after, blowing the
2 s budget for a reason that has nothing to do with the journal.

**Fix:** maintain subtree totals incrementally. Keep a `dir_totals` row per directory
(`subtree_bytes`, `subtree_files`). When Tier 2 sees a file's size change by delta, walk its ancestor
chain and add delta to each. Depth is ~10, so a few thousand changed files is a few tens of thousands
of cheap updates. Tier 1 populates the table wholesale.

Phase 1 builds the rollup engine and the table in this shape so Phase 2 only has to add the
incremental path. Called out here because the brief's storage section does not mention it, and it
would be painful to retrofit.

### A7. The hardlink rule as written makes rollups not sum to the volume total. (needs a rule)

"Deduplicate hardlinks by `(VolumeSerialNumber, FileId)`, count the bytes once, but attribute the
link in the tree" is under-specified. If two paths both show the full size, directory rollups
double-count and no longer sum to the volume total. If both show zero, the bytes vanish.

This matters more than it sounds, because on `C:` the dominant case is `WinSxS`, which is very
largely hardlinks into `System32`. Whatever rule we pick visibly determines whether Strata reports
`WinSxS` as ~10 GB or ~0.5 GB, and users have strong pre-existing opinions about that number.

**Rule adopted:** the first path encountered in a deterministic traversal order owns the full byte
count. Subsequent links record `logical_size` as reported but contribute `0` to rollups, and carry an
`IsHardLink` flag so the UI can show "this path is a link to bytes counted elsewhere". Rollups then
sum exactly to the volume total, which is the property that matters most.

Worth knowing: directory enumeration records carry **no link count**, so detecting a hardlink
requires seeing the same file ID twice. That means holding a `HashSet<long>` of every file ID seen
during a Tier 1 scan (~3M entries, tens of MB). Phase 1 implements this behind `--no-dedup` so we can
measure what it actually costs.

---

## B. Assumptions I am not confident about

### B1. Elevation may not actually be required for Tier 2. (worth a spike before building Phase 2)

The brief states flatly that reading the USN journal requires opening `\\.\D:` with `GENERIC_READ`
and therefore requires admin. That is true of `FSCTL_READ_USN_JOURNAL`. But since Windows 8 there is
**`FSCTL_READ_UNPRIVILEGED_USN_JOURNAL`**, which exists precisely to let non-elevated callers read
the journal. It returns only records for files the caller can see, which on a single-user machine is
everything that matters.

If that path works, Strata never needs to run as admin, which is a materially better posture for
something that sits in the tray permanently. I am **not confident enough to design around it**,
because the parts I am unsure of are:

- whether `FSCTL_QUERY_USN_JOURNAL`, needed for the journal ID and the `FirstUsn`/`NextUsn` bounds,
  has an unprivileged equivalent, or whether the cursor bootstrap still forces elevation;
- what handle rights the volume open actually needs for the unprivileged variant.

`FSCTL_CREATE_USN_JOURNAL` definitely requires elevation, but that is a one-time setup action that
can prompt, rather than a permanent requirement.

**Proposal:** Phase 2 opens with a short probe that tries the unprivileged path unelevated on
C:/D:/E: and reports what works. It decides whether the app ships with a `requireAdministrator`
manifest or an on-demand elevation prompt. I would rather spend an hour on that than bake
admin-forever into the design on my recollection of an FSCTL.

### B2. Whether the USN journal is already enabled on D: and E:

Windows keeps a journal on the system volume for Search and maintenance. Non-system volumes vary.
The Phase 2 probe answers this at the same time. If it is off, enabling it is a one-time elevated
`FSCTL_CREATE_USN_JOURNAL`, which is fine, but it changes the first-run UX.

### B3. Tier 2's 2 s budget has no defined behaviour when it is blown

The common case is fine. The uncommon case is: you install a 120 GB game, or Windows Update services
the component store, and one tick sees several hundred thousand USN records, each needing a stat.
That is not a 2 s operation. The brief says "log and alert if it doesn't" but does not say what the
scanner *does*.

**Proposal:** cap the changed-file set per tick (config, default ~50k). Beyond that, process the
first N in USN order, persist the cursor at that point, and continue on the next tick. The journal is
a durable queue, so falling behind is safe and self-healing. Emit a "catching up" state to the UI.
The alternative, blocking until drained, is what would cause the frame drop the brief forbids.

### B4. Volume identity should be the GUID, not the letter

The brief keys the USN cursor on `VolumeGuid` but the `files` table on `volume_id`. Drive letters get
reassigned, and if `D:` and `E:` ever swap, letter-keyed history silently corrupts. Assuming the
intent was: **`\\?\Volume{guid}\` is the identity, the letter is a mutable display attribute**, and
building it that way. Flagging in case you meant otherwise.

### B5. Your machine is 3 x ~930 GB, not 2 TB

The brief asks how long a full scan of a 2 TB volume takes. The actual volumes are C: 930 GB
(55 GB free), D: 931 GB (78 GB free), E: 931 GB (412 GB free), all NTFS, all fixed, plus a 790 MB
unlettered recovery partition that we should exclude. Phase 1 numbers will be for those. Scan cost
tracks *file count*, not capacity, so the number that matters is how many files each volume holds,
which the scan will tell us.

### B6. Phase 1 runs unelevated, so parts of C: will be unreadable

Tier 1 needs no elevation, but unelevated it cannot read `System Volume Information`, other users'
profile directories, or some ACL'd system paths. Those subtrees will be silently missing from the C:
total, which will make Strata's number disagree with Explorer's. Phase 1 therefore **counts and
reports inaccessible directories**, so we can see how much is being missed before deciding whether
Tier 1 also wants elevation or `SeBackupPrivilege`.

---

## C. Things I think are unnecessary, or that I would defer

### C1. `snapshot_top_files` can wait until Phase 4

It is the least load-bearing of the five tables. "What single file ate 8 GB" is answerable from
`events` plus current `files` for anything recent, and the table only earns its keep for files that
have since been deleted. It is cheap (500 rows per snapshot) so I am not arguing hard. **Keeping
it**, and the schema is created in Phase 1, but not populating it until the diff engine exists.

### C2. Do not use Dapper on the bulk insert path

The brief says "Dapper or raw ADO for the hot paths". For the multi-million-row Tier 1 insert,
Dapper's per-row reflection and parameter object allocation is real overhead. Raw `SqliteCommand`
with parameters created once and values reassigned per row, inside a single transaction, is several
times faster and is about fifteen lines of code. Dapper is fine for the query side in Phase 3.

### C3. Parallelism cap of `min(4, ProcessorCount/2)` is likely too low here

On a 14900K with NVMe, directory enumeration is latency-bound rather than bandwidth-bound and scales
well past 4 threads. Since we are deliberately running at very low I/O priority, the cap costs less
than it otherwise would. **Making it configuration, defaulting to 4 as specified**, and Phase 1
reports throughput so we can tune against a real measurement rather than an argument.

---

## D. What I am doing about all of it in Phase 1

Adopted without asking, as I believe these are unambiguous corrections: A1, A2, A3, A6 (schema shape
only), A7 (rule stated above), B4, C2.

Flagged and deferred to Phase 2, with a probe rather than a guess: B1, B2, B3, A4.

**Needs your decision, informed by Phase 1's numbers:** A5. The Phase 1 CLI prints the directory
histogram specifically so this can be decided on data.


---

## E. What Phase 1 settled

Written after the first real scans. Numbers in docs/MEASUREMENTS.md.

| Item | Status |
|---|---|
| A1 ID-bearing enumeration is mandatory | Confirmed. Built that way; file IDs cross-checked against a second API. |
| A2 `GetFileInformationByHandleEx` over ntdll | Built. The record offsets are validated by tests against a known directory. |
| A3 Per-thread rather than per-process background mode | Built. Costs nothing measurable on an idle NVMe, so it stays on permanently. |
| A5 Storage growth | **I was wrong.** Off by an order of magnitude in the pessimistic direction. No mitigation needed. |
| A6 `dir_totals` | Built, populated wholesale by Tier 1. Incremental maintenance is Phase 2. |
| A7 Hardlink attribution | Rule built and tested. Confirmed material: 135,505 links sharing 18.2 GB on C:. A related limitation surfaced - see below. |
| C3 Worker count | Confirmed too low. Default raised. |

### New, found only by running it

1. **Cold cache dominates everything.** C: takes 211 s cold and 7.7 s warm. The brief's 30-90 s
   estimate describes the warm case; the cold case is the one Tier 1 actually runs in. First-run UX
   has to assume minutes.
2. **Scan cost tracks directory count, not capacity.** D: holds 853 GB and scans in 0.7 s because it
   is only 10k directories. C: holds a similar 875 GB across 570k directories and takes a hundred
   times longer.
3. **Hardlink paths are not all representable.** `files` is keyed `(volume_id, file_id)` and
   hardlinks share a file ID, so only one path per file is stored. Totals are unaffected, since they
   come from the in-memory rollup. Fix is to widen the key; deferred until the UI shows whether those
   paths matter.
4. **A schema convention was costing 95 MB.** Storing `files.modified_utc` as ISO-8601 text, per this
   project's own convention, cost 20 bytes a row across 3.5M rows. Now an integer. The convention now
   applies only to tables small enough for legibility to be worth the bytes.

### Still open for Phase 2

- **B1**, whether `FSCTL_READ_UNPRIVILEGED_USN_JOURNAL` removes the elevation requirement. Untouched;
  still the first thing Phase 2 should probe.
- **B6** grew teeth: unelevated, Strata undercounts C: by 29.6 GB, partly from 557 unreadable
  directories. Worth testing whether elevation closes it, which settles whether Tier 1 wants
  elevation independently of Tier 2.
- **A4**, borderless-fullscreen detection, and the whole question of scan cost under load. The
  constraint the design exists to satisfy has not been tested at all yet.
