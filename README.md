# Strata

A Windows disk-usage tool that answers the question the snapshot tools don't:
**what grew, when, and where?**

> "Your D: drive gained 40 GB since last Tuesday. 32 GB of it landed in `D:\Steam\steamapps`."

WinDirStat, WizTree and TreeSize show you a point in time. Strata records history and diffs it,
taking snapshots every 15 to 30 minutes cheaply enough that you never notice it running.

- **Local only.** No cloud, no telemetry, no auth, no network calls.
- **Read only.** Strata never deletes or modifies your files.
- **Invisible.** Scans run at very low I/O priority and defer while you are gaming.

See [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) for the design and
[docs/DESIGN-REVIEW.md](docs/DESIGN-REVIEW.md) for the reasoning behind it.

## Status

Phases 1 and 2 are done: volume enumeration, full baseline scanner, SQLite storage, the NTFS change
journal incremental scanner, the diff engine, the resource guards, and a scheduler.

Phase 3 is done: a localhost API on port 5177 and the Angular UI, both hosted in the same process
as the scanner by `strata serve`. Five screens — Overview, Timeline, Explorer, Compare and Settings
— built to the mockups in [docs/design/](docs/design/). `tools/api-probe.http` exercises every
endpoint.

Phase 4 is next: the tray icon, retention and downsampling.

Strata runs unelevated. The one thing that needs administrator is creating a change journal on a
volume that lacks one, which is a one-time `strata enable-journal` from an elevated prompt. See
[docs/RUNTIME.md](docs/RUNTIME.md) for how this looks once installed - short version: it lives in the
tray and you never run anything from a console.

## Build

```
dotnet build Strata.sln
dotnet test
```

The UI is a separate build, opt-in so that the .NET side works on a machine with no Node:

```
cd src/Strata.Web && npm ci && npm run build
```

or `dotnet build src/Strata.Host -p:BuildWebUi=true`. Whatever is in `src/Strata.Web/dist` gets
copied next to `strata.exe` either way; with nothing there, `strata serve` serves the API alone and
says so.

## Use

```
strata volumes                 list volumes and what Strata will do with each
strata scan D:                 full baseline scan
strata usn-probe               report what the change journal permits here
strata enable-journal          create a journal on volumes lacking one (elevated)
strata watch                   run the incremental scanner on a schedule
strata serve                   run the scanner, the API and the UI together
strata tray                    the same, from the notification area, with no console
```

Then open <http://localhost:5177/>, or click the tray icon.
