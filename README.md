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
journal incremental scanner, the diff engine, the resource guards, and a scheduler. No API or UI yet.

Strata runs elevated. Reading the journal does not require it, but creating one on a volume that
lacks it does, and elevation also lets a baseline read ACL'd directories. See
[docs/RUNTIME.md](docs/RUNTIME.md) for how this looks once it is installed - short version: it lives
in the tray and you never run anything from a console.

## Build

```
dotnet build Strata.sln
dotnet test
```

## Use

```
strata volumes                 list volumes and what Strata will do with each
strata scan D:                 full baseline scan
strata usn-probe               report what the change journal permits here
strata enable-journal          create a journal on volumes lacking one (elevated)
strata watch                   run the incremental scanner on a schedule
```

Build an unelevated binary for development with
`dotnet build -c Release -p:RequireAdministrator=false`; a `requireAdministrator` executable cannot
be launched from a script.
