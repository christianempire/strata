# Strata — working conventions

Strata is a Windows disk-usage tool that records history and diffs it, answering "what grew, when,
and where?" Read `docs/ARCHITECTURE.md` for the design and `docs/DESIGN-REVIEW.md` for the reasoning
behind decisions that deviate from the original brief.

## Non-negotiables

These come from the project brief and outrank convenience:

1. **The background scanner must be invisible during work and gaming.** A frame drop caused by a
   scan is a project failure, not a bug. Any change that touches scan scheduling or I/O volume must
   preserve this.
2. **Read-only.** Strata never deletes, moves, or modifies a user file. There is no cleanup feature.
   If a code path opens a handle with write access to something outside `%LOCALAPPDATA%\Strata\`,
   that is a bug.
3. **Local only.** No network calls, no telemetry, no auth, no cloud. The only listening socket is
   the localhost API from Phase 3 onward.
4. **Resist scope creep.** Non-goals are listed in `docs/ARCHITECTURE.md` and are firm.

## Phases

Work proceeds one phase at a time, and each stops for review.

| Phase | Contents | State |
|---|---|---|
| 1 | Volume enumeration, Tier 1 scanner, SQLite schema, snapshot writing, `strata scan` CLI | done |
| 2 | USN incremental (Tier 2), scheduler, resource guards, diff engine + `events` | done |
| 3 | Minimal API, then Angular UI (gated on mockups in `docs/design/`) | current |
| 4 | Tray icon, retention/downsampling, polish | not started |

Phase 3 has a handoff brief at `docs/PHASE3-HANDOFF.md`.

Do not build ahead of the current phase. Phase 3's UI is gated on design mockups landing in
`docs/design/`; if they are absent, stop and ask rather than inventing a visual language.

## Solution layout

```
src/Strata.Core       domain models, scan orchestration, rollup + diff engines — NO I/O
src/Strata.Storage    SQLite schema, migrations, repositories
src/Strata.Platform   Win32 P/Invoke: volumes, directory enumeration, USN, idle detection
src/Strata.Host       the executable: CLI now, worker + API + tray later
src/Strata.Web        Angular app (Phase 3)
tests/Strata.Tests    xUnit
```

**Dependency rule, enforced by review:** `Core` references nothing but the BCL. It must stay
unit-testable with no disk, no registry, no P/Invoke. `Platform` may reference `Core` for its types
but never `Storage`. `Host` composes everything. If `Core` needs to read something, it takes an
interface that `Host` supplies.

## C# conventions

- **TFM:** `net9.0-windows` everywhere except `Strata.Core`, which is `net9.0`. Core being
  platform-neutral is how the no-I/O rule gets enforced by the compiler rather than by discipline.
  The installed SDK is 10.x; we target 9 deliberately, per the brief.
- **Nullable enabled, warnings as errors.** Do not suppress with `!` where a real check belongs.
- File-scoped namespaces, `var` when the type is evident on the right-hand side, explicit otherwise.
- `sealed` by default on classes. `readonly record struct` for small value types on hot paths.
- Prefer `internal` over `public`. Public surface is for what other projects actually consume.
- No `#region`. No `I`-prefixed interfaces for single-implementation abstractions unless they exist
  for test seams (`IVolumeScanner` does; it has three implementations).

## P/Invoke conventions

- Use `[LibraryImport]` (source-generated) rather than `[DllImport]`. Add `AllowUnsafeBlocks` only
  in `Strata.Platform`.
- All native declarations live in `Strata.Platform/Native/`, one file per Windows header area
  (`Kernel32.cs`, `Shell32.cs`, ...). Native structs and constants keep their Windows names and
  casing verbatim, so they can be grepped against MSDN. Everything native is `internal`.
- Managed wrappers converting native results into `Core` types live outside `Native/` and use normal
  C# naming.
- Every native call site checks its return value and surfaces `Marshal.GetLastPInvokeError()` in the
  exception or log. Silent native failure is how this class of tool ends up quietly under-reporting.
- Handles use `SafeHandle` subclasses, never raw `IntPtr` held across a call boundary.

## Scan hot-path rules

The Tier 1 enumeration loop runs millions of times per scan. In that loop specifically:

- No LINQ, no `params` arrays, no closures allocated per entry.
- Parse native buffers as `ReadOnlySpan<byte>` with `MemoryMarshal`; do not marshal structs one at a
  time.
- Rent buffers from `ArrayPool<byte>.Shared` and return them in a `finally`.
- Strings are allocated once per entry (the file name) and that is the intended budget. Do not build
  full path strings per file — paths are reconstructed on demand from parent IDs.
- Exceptions are not control flow. Inaccessible directories are an expected outcome and get counted,
  not thrown.

Outside that loop, write ordinary readable C#.

## Storage conventions

- Raw `Microsoft.Data.Sqlite` for writes. Commands are prepared once and parameter values reassigned
  per row, inside one transaction. No Dapper on bulk paths (see `docs/DESIGN-REVIEW.md` C2).
- **Time hot queries, do not trust the plan.** SQLite chooses between a seek and a scan without
  statistics, and gets it catastrophically wrong on large tables: one lookup measured 442 ms against
  0.010 ms with `INDEXED BY`, while the same predicate with a narrower projection planned as a
  covering index scan and looked healthy. Any query on the Tier 2 path gets timed, not inspected.
- Schema changes go in a new numbered migration in `Strata.Storage/Migrations/`. Never edit an
  applied migration.
- All timestamps are UTC. Stored as ISO-8601 strings (`O` format) for legibility in a SQLite browser
  **except in tables with millions of rows**, where they are Unix seconds: measured on the target
  machine, that convention was costing 95 MB in `files` alone, which is not a price worth paying for
  browsability. Sizes are `INTEGER` bytes, never pre-formatted.
- The database lives at `%LOCALAPPDATA%\Strata\strata.db`, WAL mode.

## Testing

- xUnit. Tests for `Strata.Core` are written **alongside** the code, not at the end.
- The rollup and diff engines take data structures in and return data structures out, so they get
  real coverage including the awkward cases: hardlinks, reparse points, renames across directories,
  deep nesting, empty directories.
- `Platform` and `Storage` get thinner tests. Platform tests that touch the real filesystem create a
  temp directory tree and clean up; they are allowed to be slow but must not depend on machine state.
- Run `dotnet test` before every commit.

## Commits

- Conventional commits: `feat:`, `fix:`, `refactor:`, `test:`, `docs:`, `chore:`, `perf:`.
- Small and reviewable. One logical change per commit; a commit should build and pass tests.
- **Never add AI-attribution trailers, co-author lines, or "generated with" phrasing** to commit
  messages, PR descriptions, or changelogs.

## Measurement

This project is justified by performance claims, so measure rather than assert. When a change
touches scan cost or database size, record the before/after numbers in the commit message or in
`docs/MEASUREMENTS.md`. The Phase 1 CLI exists specifically to produce these numbers.
