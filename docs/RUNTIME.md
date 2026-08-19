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
| Minimal API | ASP.NET Core listening on `http://localhost:5177` |
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

This is where the decision to run elevated has a consequence worth knowing.

An executable marked `requireAdministrator` **cannot be started silently from the usual places.** The
`Run` registry key and the Startup folder both trigger a UAC consent prompt, so you would be
approving a dialog at every single login. That is unacceptable for something meant to be invisible.

The way to auto-start an elevated process without a prompt is **Task Scheduler with "Run with highest
privileges"**, triggered at logon. Windows starts the task elevated without asking, because the task
was registered by an administrator once.

So installation, whenever it is built, registers a scheduled task rather than a Run key. Until then,
starting it by hand costs one UAC prompt per launch.

```
Task:     Strata
Trigger:  At log on (current user)
Action:   C:\...\strata.exe watch
Settings: Run with highest privileges
          Do not start if on battery  (optional; it already defers under load)
```

## During development

Different, and only for developers:

- `dotnet run --project src/Strata.Host -- watch` runs the scanner and API with a console attached,
  which is how the resource behaviour gets judged — every deferral and tick duration prints.
- `ng serve` runs the Angular dev server on its own port with hot reload, proxying `/api` to
  `localhost:5177`. This exists so a CSS change does not need a full rebuild.
- Build with `-p:RequireAdministrator=false` for an `asInvoker` binary. Identical in every other way,
  and it degrades to the unprivileged journal path. A `requireAdministrator` binary cannot be
  launched from a script or a test run, because the consent prompt has no one to answer it.

None of that is part of using the app.

## What elevation buys, and what it costs

Reading the change journal does **not** need elevation — a directory handle on the volume root plus
`FSCTL_READ_UNPRIVILEGED_USN_JOURNAL` is sufficient, and that path stays supported and working.
Running elevated buys three things:

1. **`FSCTL_CREATE_USN_JOURNAL`**, one time per volume. On this machine D: and E: have no journal at
   all, so without it they are stuck on Tier 3 full rescans every few hours instead of 15-minute
   incremental ticks. This is the big one.
2. **Journal records that carry file names.** The unprivileged read redacts them, forcing a path
   resolution per changed file — measured at 0.06 ms each, so this is a modest saving rather than a
   transformation.
3. **A baseline that can read ACL'd directories.** Unelevated, 557 directories on C: were unreadable
   and the volume total came out 29.6 GB short of what Windows reports.

The cost is the scheduled task above, and the fact that a permanently-resident elevated process is a
larger thing to trust. Strata is read-only by design and never opens a handle with write access to
anything outside its own data directory, which is the mitigation that matters.

## Where things live

```
%LOCALAPPDATA%\Strata\strata.db        the database
%LOCALAPPDATA%\Strata\strata.db-wal    write-ahead log
```

Strata's own data directory is excluded from the change feed by default. It is rewritten by every
tick, so left in it would permanently be the loudest thing in its own report. It is **not** excluded
from totals or the treemap — the database occupies real disk and should be visible as such.
`--include-strata-db` turns the exclusion off.
