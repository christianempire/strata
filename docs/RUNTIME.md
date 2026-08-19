# How Strata runs

Short answer to "do I have to run the Angular UI from a console every time": **no.** You never run
Angular at all. You click the tray icon.

This document describes the shipped shape, so Phase 3 and Phase 4 are built toward it rather than
toward a developer's convenience.

---

## What is actually running

**One process. One executable. `strata.exe`.**

It contains, in a single Windows process:

| Part | What it does |
|---|---|
| Background scanner | `IHostedService` running the Tier 2 tick on a timer |
| Minimal API | ASP.NET Core listening on `http://localhost:5177`, loopback only |
| Static file server | Serves the **pre-built** Angular app from the same host |
| Tray icon | Open Strata / Scan now / Pause / Settings / Exit |

The Angular app is compiled to plain HTML, CSS and JavaScript at *build* time by `ng build`, and
those files are copied into the executable's output and served as static content. At runtime there is
no Node, no `ng serve`, no second process, no console window.

## What you do

1. Log in. Strata is already running, started by a scheduled task, with no window and no console.
2. Its tray icon sits in the notification area.
3. Click it and choose **Open UI**.
4. Your default browser opens `http://localhost:5177` and you are looking at the app.

That is the whole interaction. Closing the browser tab does not stop anything; the scanner keeps
running in the tray. **Exit** from the tray menu is the only thing that stops it.

There is no window of its own — the UI is a browser tab against a local server. That is a deliberate
choice and it is what the brief's stack implies: it avoids bundling a browser engine, it means the
Angular app is the same code whether you open it from the tray or type the URL, and it costs nothing
at rest.

## Installing it, and starting at login

Two commands. The first needs the repository, the SDK and Node; the second needs neither, only the
folder the first produced.

```
tools\publish.ps1
```

That builds the Angular UI, publishes the host complete into `artifacts\publish`, and then runs
`strata install` from there, which:

1. stops any Strata that is already running, by asking rather than killing;
2. copies the whole publish to `%LOCALAPPDATA%\Programs\Strata`;
3. registers a logon task pointing at that copy;
4. starts it through the task, so what runs now is exactly what will run at logon.

Undo it with `strata uninstall`, which stops the process, removes the task, removes the program
directory and **leaves the database alone**. `--purge-data` deletes that too, and is the only way
to.

### Why a copy at all

`strata.exe` lived in `src\Strata.Hostin\Release
et9.0-windows\` throughout development, which
is not a home. `dotnet clean`, a branch switch or moving the repository silently breaks anything
pointing at it, and a rebuild while it is running fails on the file lock — which happened, twice,
and is why install stops the running copy before it writes.

The program directory and the data directory are separate and not nested. Install replaces the
program directory wholesale; the database is somewhere a recursive delete cannot reach it.

```
%LOCALAPPDATA%\Programs\Strata\strata.exe    the program — replaced by install, deleted by uninstall
%LOCALAPPDATA%\Strata\strata.db               the data — untouched by both
```

### The task

```
Task:     Strata
Trigger:  At log on (current user), delayed 30 seconds
Action:   %LOCALAPPDATA%\Programs\Strata\strata.exe tray
Run as:   the current user, InteractiveToken, LeastPrivilege
```

`tray` rather than `serve`: same process, same port, same UI, but it puts an icon in the
notification area and runs without a console. `serve` stays as the developer's shape — a console
attached, `--verbose` printing a line per request. Neither elevates.

**Verified unelevated.** `strata install` registered the task from an ordinary prompt with
`IsInRole(Administrator)` false, and Task Scheduler accepted it: a per-user task in the current
user's own name needs no privilege, and the executable's manifest is `asInvoker`, so there is
nothing to prompt about at logon or at registration. The two alternatives named below would also
have worked; the task was chosen because only it can express the two settings this process wants.

**Four settings that are not the default**, each of which would otherwise be a bug:

| Setting | Default | Here | Why |
|---|---|---|---|
| `ExecutionTimeLimit` | 3 days | `PT0S` (none) | Task Scheduler stops a task after three days. A resident scanner has to opt out. |
| `DisallowStartIfOnBatteries` | true | false | Would mean Strata never runs on an unplugged laptop. |
| `StopIfGoingOnBatteries` | true | false | Same, mid-session. |
| `Priority` | 7 (below normal) | 6 (normal) | Strata depresses its *scan threads*, not its process — see `docs/DESIGN-REVIEW.md`. The same process serves the UI somebody is looking at. |

**Delayed 30 seconds.** "At log on" fires while the shell is still starting, the notification area
among it. Thirty seconds costs nothing for a process that then sits still for fifteen minutes, and
it keeps Strata out of the part of logon the user is watching. `--delay 0` turns it off.

**No console window.** `strata.exe` is a console-subsystem executable, because most of its verbs are
console verbs and a Windows-subsystem build would hand the shell its prompt back before a scan had
printed a line. The subsystem is a property of the file, not of the verb, so a `tray` started
without a console gets a window nobody asked for. The first statement in `Main` hides and releases
it — and only when the console belongs to this process alone, so `strata tray` typed into PowerShell
still prints where you can see it. Measured after starting the task: no `conhost` child process, no
visible top-level window, `MainWindowHandle` zero.

**Only one at a time.** Two instances fight: the second fails to bind `localhost:5177`, and both
would run schedulers against one SQLite file. A named mutex per logon session makes the second one
say `Strata is already running in this session` and exit, and a named event alongside it is how
`strata install` asks a running copy to stop rather than killing it.

### Why not a Run key or a Startup shortcut

Both were on the table and both are equally unelevated — this document named them as the fallbacks
if the task turned out to need administrator, and it did not. Neither can express a delay
after logon, and neither has anything to say about execution time limits or batteries, because
neither is supervising anything. The task is a supervisor; the other two are a spawn and a shrug.

**Explicitly not done:** no Start Menu entry, no desktop shortcut, no MSI, no auto-update, nothing
under `HKLM`, nothing touching another user's account.

### The one thing that does need administrator

`FSCTL_CREATE_USN_JOURNAL` requires elevation, and it is needed once per volume on volumes where
Windows has not already enabled a journal. On this machine that was D: and E:; C: always has one
because Search and maintenance rely on it.

**Done on 19 Aug 2026.** Both were created in one elevated run — `D: created (journal
1DD2FE6835C7B28)`, `E: created (journal 1DD2FE683605825)` — and `strata usn-probe` then reports both
readable on the **unprivileged** path, which is the point of the whole arrangement: one elevated
command, once, and the resident scanner never elevates again. All three volumes now run Tier 2.

Without a journal a volume falls back to Tier 3, a throttled full walk every few hours instead of a
15-minute incremental tick. So this is worth doing once:

```
# from an elevated prompt, one time
strata enable-journal
```

That is a deliberate one-off manual step rather than a reason to elevate the resident process. The
scanner never does it silently: it is a persistent change to a volume's filesystem metadata and the
user should be the one asking for it.

## During development

Different, and only for developers:

- `dotnet run --project src/Strata.Host -- serve` runs the shipped shape with a console attached:
  the scanner and the API in one process, listening on `http://localhost:5177`. Add `--verbose` for
  a line per HTTP request with its duration. `tools/api-probe.http` exercises every endpoint.
- `dotnet run --project src/Strata.Host -- watch` runs the scanner *without* the API, which is how
  the resource behaviour gets judged — every deferral and every tick's phase breakdown prints, and
  its configuration comes from flags rather than from the settings table, so a measurement run is
  reproducible.
- `npm start` in `src/Strata.Web` runs the Angular dev server on its own port with hot reload,
  proxying `/api` to `localhost:5177` — so `strata serve` has to be running alongside it. This exists
  so a CSS change does not need a full rebuild.
- `npm run build` writes `src/Strata.Web/dist`, which `Strata.Host` copies next to `strata.exe` as
  `wwwroot`. Opt-in from MSBuild as `dotnet build src/Strata.Host -p:BuildWebUi=true`, because
  `dotnet build` and `dotnet test` have to work on a machine with no Node. With nothing built,
  `strata serve` serves the API alone and says so on startup.

None of that is part of using the app.

## Why not elevated

Reading the change journal does not need it. A directory handle on the volume root plus
`FSCTL_READ_UNPRIVILEGED_USN_JOURNAL` is sufficient for any user, and the whole incremental loop is
verified end to end that way.

Two things are given up, both acceptable:

1. **Journal records arrive with names redacted**, so a path is resolved from the file ID per changed
   file. Measured at 0.06 ms per call against the 0.09 ms size read it accompanies, which is a
   rounding error rather than a cost.
2. **A baseline cannot read ACL'd directories.** Unelevated, 557 directories on C: were unreadable
   and the volume total came out 29.6 GB short of what Windows reports. The scan counts and reports
   those directories, so the shortfall is visible rather than silent.

Against that: a permanently-resident elevated process is a much larger thing to ask someone to trust,
and staying unelevated means no UAC prompt at login and no scheduled task with special privileges.
For a read-only tool that watches disks, that trade is clearly worth making.

## Where things live

```
%LOCALAPPDATA%\Programs\Strata\      the program: strata.exe, wwwroot, icons
%LOCALAPPDATA%\Strata\strata.db      the database
%LOCALAPPDATA%\Strata\strata.db-wal  write-ahead log
```

Two directories, and keeping them apart is the point. The first is disposable — install replaces it
wholesale, uninstall deletes it. The second is months of history and neither verb touches it.

Settings live in the database too, in a `settings` table, rather than in a config file. They were
command line flags through Phases 1 and 2, which suits a console verb and does not suit a process
configured from a browser tab. `strata serve` reads them at the start of every cycle, so a change
saved from the UI takes effect on the next tick without a restart. The console verbs deliberately
ignore the table and use their flags, so a measurement run is not silently affected by whatever the
UI last saved.

Strata's own data directory is excluded from the change feed by default. It is rewritten by every
tick, so left in it would permanently be the loudest thing in its own report. It is **not** excluded
from totals or the treemap — the database occupies real disk and should be visible as such.
`--include-strata-db` turns the exclusion off.
