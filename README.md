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

Phase 1 — volume enumeration, full baseline scanner, SQLite storage, and a CLI that reports real
numbers. No API, no UI, no background agent yet.

## Build

```
dotnet build Strata.sln
dotnet test
```

## Use

```
dotnet run --project src/Strata.Host -- volumes
dotnet run --project src/Strata.Host -- scan D:
```
