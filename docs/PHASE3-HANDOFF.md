# Phase 3 handoff

Context for a fresh session picking up Strata at Phase 3. Copy the block below as the opening prompt.

---

You are continuing work on **Strata**, a Windows disk-usage tool at
`c:\Users\chris\Documents\My Programming Projects\christianempire\strata`.

Strata answers the question the snapshot tools don't: *what grew, when, and where?* WinDirStat and
WizTree show a point in time; Strata records history and diffs it, so it can say "your D: drive
gained 40 GB since last Tuesday, and 32 GB of it landed in `D:\Steam\steamapps`."

## Read these first, in this order

1. `CLAUDE.md` — conventions, the phase table, and the layering rules. Follow it.
2. `docs/ARCHITECTURE.md` — the design, in full.
3. `docs/RUNTIME.md` — how the shipped app actually runs. **Read this before designing the API**;
   it determines what the UI is talking to.
4. `docs/MEASUREMENTS.md` — every performance claim, measured on this machine.
5. `docs/DESIGN-REVIEW.md` — the critique of the original brief and which parts were adopted,
   rejected, or settled by measurement.

## State: Phases 1 and 2 are done and committed

18 commits, 101 passing tests, `dotnet build` and `dotnet test` clean.

**Phase 1** — volume enumeration, Tier 1 full baseline scanner, SQLite schema, snapshot writing,
`strata scan` / `strata volumes`.

**Phase 2** — Tier 2 incremental scanning from the NTFS change journal, the diff engine, the resource
guards, and a `strata watch` scheduler. Verified end to end against real changes on C:.

Working CLI verbs: `volumes`, `scan`, `usn-probe`, `usn-tail`, `watch`, `enable-journal`.

### Numbers you can rely on

- Full baseline of C: (3.06M files, 570k directories): **7.7 s warm, 211 s cold**. Cold is the case
  Tier 1 actually runs in. Scan cost tracks *directory count*, not capacity.
- Tier 2 tick: **~250 ms** steady state, **~1.0 s** for a full 50,000-record catch-up bite. The
  brief's 2 s budget holds.
- Database: **447 MB** for 3.67M files across three volumes. That is current state and does not grow
  with snapshots. History adds roughly 543 MB per week at full resolution before downsampling.

### Decisions already made, do not relitigate

- **Runs unelevated.** Reading the journal needs no elevation. Only `FSCTL_CREATE_USN_JOURNAL` does,
  which is a one-time `strata enable-journal` from an elevated prompt. The manifest is `asInvoker`.
- **`files` is keyed `(volume_id, parent_id, name)`**, the directory entry, not the file. Hardlinks
  share a file ID; keying on it lost 135,520 of 3.67M entries.
- **Strata's own data directory is excluded from the events feed** but not from totals or the
  treemap.
- **`Strata.Core` references nothing but the BCL** and targets plain `net9.0` so the compiler
  enforces it. Everything else is `net9.0-windows`.

## Your job: Phase 3

Two halves, and **the second is gated**.

### 3a — the minimal API. Start here.

Add ASP.NET Core to `Strata.Host` and expose, all under `/api`, returning JSON with no wrapper
envelope:

```
GET  /volumes
GET  /snapshots?volume=&from=&to=
GET  /tree?snapshot=&path=&depth=
GET  /diff?from=&to=&path=
GET  /events?from=&to=
POST /scan
GET|PUT /settings
```

Notes that matter:

- Bind to `localhost:5177` only. Never `0.0.0.0`. Local-only is a hard constraint.
- Stream large trees with `IAsyncEnumerable`. `/tree` on the root of C: is 570k directories.
- The same process already hosts the scanner. Do not let API work and scan work interfere: scan
  threads run in per-thread background I/O mode precisely so the API is not throttled behind them.
- The diff engine (`Strata.Core.Diffing.DiffEngine`) already does the hard part for `/diff`. It takes
  a tree plus per-directory deltas and returns ranked, attributed changes. Feed it the difference
  between two snapshots' `snapshot_dirs` rows.
- Time any query on a hot path rather than reading its plan. There is a note in `CLAUDE.md` about
  this: a lookup that looked healthy under `EXPLAIN QUERY PLAN` cost a factor of 44,000.

**Verify the API with a REST client or a throwaway HTML page, and stop there.** Report back with the
endpoints working before touching Angular.

### 3b — the Angular UI. Gated.

**Check `docs/design/` before starting. If it is empty, stop and ask.** The project owner is
producing mockups in Claude Design and the brief is explicit: build to the mockups, do not invent a
visual language, do not reach for a component library's defaults. If a mockup is ambiguous or looks
impractical, say so rather than silently diverging.

Five screens, when the designs land: Overview, **Timeline** (the headline screen: stacked area of
used space over time plus the events feed), Explorer (ECharts treemap with drill-down), Compare, and
Settings.

## Known gaps, carried forward

- **Scan cost under real game contention has never been tested.** This is the constraint the entire
  design exists to satisfy. The gate defers correctly when Windows reports a fullscreen or busy
  state, but the cost of a tick that proceeds alongside a game is unknown. Deliberately deferred.
- D: and E: have no change journal yet, so they need `strata enable-journal` once, elevated.
- Cold-cache baseline timings at 8 and 16 workers need a reboot to measure.
- `snapshot_top_files` has a schema but is never populated.
- Retention and downsampling are not implemented. That is Phase 4.

## How to work

Small, reviewable commits with conventional messages. Tests alongside the code, not after. Never add
AI-attribution trailers to commit messages. Run `dotnet test` before every commit. When a change
touches scan cost or database size, measure it and record the numbers.

Build and test:

```
dotnet build Strata.sln
dotnet test
```
