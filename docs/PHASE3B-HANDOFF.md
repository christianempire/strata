# Phase 3b handoff

Context for a fresh session picking up Strata at the Angular UI. Copy the block below as the opening
prompt.

---

You are continuing work on **Strata**, a Windows disk-usage tool at
`c:\Users\chris\Documents\My Programming Projects\christianempire\strata`.

Strata answers the question the snapshot tools don't: *what grew, when, and where?* WinDirStat and
WizTree show a point in time; Strata records history and diffs it, so it can say "your D: drive
gained 40 GB since last Tuesday, and 32 GB of it landed in `D:\Steam\steamapps`."

## Read these first, in this order

1. `CLAUDE.md` — conventions, the phase table, the layering rules. Follow it.
2. `docs/design/README.md` — the mockups and what they cover. **You build to these.**
3. `docs/ARCHITECTURE.md` — the design, in full, including the API surface.
4. `docs/RUNTIME.md` — how the shipped app runs. The UI is a browser tab against `strata serve`.
5. `docs/MEASUREMENTS.md` and `docs/STORAGE-MODEL.md` — every performance claim, measured, plus
   why the storage model looks the way it does.

## State: Phases 1, 2 and 3a are done and committed

30 commits, 132 passing tests, `dotnet build` and `dotnet test` clean.

- **Phase 1** — volume enumeration, Tier 1 baseline scanner, SQLite schema, snapshot writing.
- **Phase 2** — Tier 2 incremental scanning from the NTFS change journal, diff engine, resource
  guards, watch scheduler.
- **Phase 3a** — the localhost API, hosted with the scanner by `strata serve`, plus a replacement
  for the directory-history storage model (migrations 005–007).

CLI verbs: `volumes`, `scan`, `usn-probe`, `usn-tail`, `watch`, `serve`, `enable-journal`.

### The API you are building against

`strata serve` hosts it on `http://localhost:5177`, loopback only, in the same process as the
scanner. `tools/api-probe.http` exercises every endpoint including the refusals — **run it first**,
it is faster than reading the code.

```
GET  /api/volumes
GET  /api/snapshots?volume=&from=&to=&limit=
GET  /api/tree?volume=|snapshot=&path=&depth=&limit=
GET  /api/diff?from=&to=&path=&minBytes=&limit=
GET  /api/events?volume=&from=&to=&minBytes=&limit=
POST /api/scan                     { "volume": "C:", "tier": "full" | "incremental" }
GET|PUT /api/settings
```

Four things about it that will shape the client:

- **JSON, camelCase, no wrapper envelope.** A list endpoint returns a list. The only wrapped shape is
  `{ "error": "..." }`, and only on a failure. Enums are names, not numbers: `"grew"`, `"vanished"`,
  `"incremental"`.
- **Tree nodes carry `parentId` and a leaf `name`, not a path.** You know the path you asked for and
  every other path is that plus the names on the way down. Sending materialised paths would have cost
  ~90 MB of strings on a full walk of C:.
- **`/tree` streams.** The whole of C: is 571k directories and 107 MB of JSON; first byte arrives in
  2.4 ms. The default limit is 10,000 nodes. Drill down a level at a time — that is the access
  pattern the storage is tuned for (`depth=1` measures 2 ms current, 8.5 ms historical).
- **`PUT /api/settings` is a patch**, not a replacement. Send the fields you are changing.

### Numbers you can rely on

- API latency: everything small is under 20 ms. `/tree?depth=3` is ~110 ms for 2.6 MB.
  `/diff` between two snapshots is 120–220 ms. Full figures in `docs/MEASUREMENTS.md`.
- A Tier 2 tick is ~120–160 ms. A cold Tier 1 baseline of C: is **211 s** — the UI has to present
  first-run as a minutes-long background task.
- Database is 438 MiB for 3.05M files on C:.

## Your job: Phase 3b, the Angular UI

**The mockups are in `docs/design/` and they are the visual language, not a starting point.** Seven
files from Claude Design, built to `docs/design/BRIEF.md`. `Strata Palette and Type` holds 29 CSS
custom properties — background layers, three text levels, rules, positive and negative deltas, and a
12-hue series ramp — plus a nine-step type scale and the number, unit and timezone rules. Lift those
tokens verbatim.

Five screens: **Overview**, **Timeline** (the headline: stacked area over time plus the events feed,
with brush-to-feed filtering), **Explorer** (ECharts treemap, drill-down), **Compare**, **Settings**.
`Strata States` covers the eight states that are not the happy path — first run, scanning, deferred
because a game is fullscreen, reduced fidelity, the honest undercount, hardlinks, detached volume,
empty range. Those are not edge cases; they are the normal life of the app.

Build it in `src/Strata.Web`, `ng build` output served as static files by `strata.exe`, per
`docs/RUNTIME.md`. Angular standalone components. Dark only — the palette says so explicitly.

**If a mockup is ambiguous or impractical, say so rather than silently diverging.** Record any
deliberate deviation in `docs/design/README.md`, which has a section for it.

### Two things the mockups assume that the backend does not do yet

1. **Compare has "elsewhere in …" remainder rows.** This is a real requirement, not decoration. A
   directory's own bytes are exact but are not a *child*, so they dilute `DiffEngine`'s 90% dominance
   test without ever being nameable — which is what turns a good answer into the useless row
   `C: −15.7 GB`. Fixing it is a change to `Strata.Core.Diffing.DiffEngine`, which is Phase 2 code
   with its own tests. Read the "unattributable remainder" note in `docs/MEASUREMENTS.md` first.
2. **There is no scan-status endpoint.** `POST /api/scan` returns 202 and nothing observes the job,
   so "first scan in progress, 3 minutes" cannot be rendered. Polling `/api/snapshots` shows the
   result but not the progress. This was deliberately left out of 3a as scope; adding it is fair game
   and probably necessary for the first-run state.

### One thing in the mockups that is wrong

**D:'s top-level directory names are invented.** The brief only supplied C:'s. Replace them with real
ones and do not let the invented ones reach a screenshot.

## Known gaps, carried forward

- **Scan cost under real game contention has never been tested.** The constraint the entire design
  exists to satisfy. Deliberately deferred by the project owner.
- **API latency during a Tier 1 baseline has never been measured.** Per-thread background I/O
  priority exists precisely so a scan cannot throttle a read in the same process, and that has not
  been shown end to end. D: and E: have no baseline yet, so the run that would show it is available.
- D: and E: have no change journal. One-time `strata enable-journal` from an elevated prompt.
- Cold-cache baseline timings at 8 and 16 workers need a reboot.
- `snapshot_top_files` has a schema and is never populated. The only schema-only thing left.
- Retention and downsampling are Phase 4. Note the constraint in `docs/STORAGE-MODEL.md`: because
  "size at T" is the latest row at or before T, downsampling must keep the newest row per directory
  per bucket rather than deleting rows older than a horizon.

## How to work

Small, reviewable commits with conventional messages. Tests alongside the code, not after. Never add
AI-attribution trailers to commit messages. Run `dotnet test` before every commit. When a change
touches scan cost or database size, measure it and record the numbers — this project's rule is to
time things rather than reason about them, and it has caught a factor of 44,000 and a factor of 1,000
so far.

```
dotnet build Strata.sln
dotnet test
dotnet run --project src/Strata.Host -c Release -- serve --verbose
```
