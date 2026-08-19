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
| Tray icon | Open UI / Scan now / Pause / Exit |

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

## Starting at login

Strata runs **unelevated**, so this is simple: a plain scheduled task at logon, or a Run key entry,
or a shortcut in the Startup folder. No UAC prompt, ever, at login or otherwise.

```
Task:     Strata
Trigger:  At log on (current user)
Action:   C:\...\strata.exe serve
```

Whenever an installer exists it will register that. Until then, starting it by hand is just running
the executable, with no prompt.

### The one thing that does need administrator

`FSCTL_CREATE_USN_JOURNAL` requires elevation, and it is needed once per volume on volumes where
Windows has not already enabled a journal. On this machine that is D: and E:; C: always has one
because Search and maintenance rely on it.

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
%LOCALAPPDATA%\Strata\strata.db        the database
%LOCALAPPDATA%\Strata\strata.db-wal    write-ahead log
```

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
