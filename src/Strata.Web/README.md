# Strata.Web

The Angular app. Standalone components, Angular 19, ECharts, dark only.

Built to the mockups in `docs/design/`, which are the visual language rather than a starting point.
The tokens in `src/styles.css` are lifted verbatim from `Strata Palette and Type`; a component that
wants a colour takes it from a token rather than inventing one. Deviations are recorded in
`docs/design/README.md`.

## Running it

```
npm ci
npm run build      # writes dist/, which Strata.Host copies to wwwroot next to strata.exe
npm start          # dev server with hot reload, proxying /api to localhost:5177
npm test           # unit tests (needs a browser; see below)
```

In production you never run any of this. `ng build` produces plain HTML, CSS and JavaScript at build
time, `Strata.Host` copies it next to the executable, and `strata serve` serves it from the same
origin as the API. There is no Node at runtime. See `docs/RUNTIME.md`.

`dotnet build src/Strata.Host -p:BuildWebUi=true` runs the npm build as part of the .NET build. It is
opt-in because `dotnet build` and `dotnet test` have to work on a machine with no Node installed.

### Dev server

`npm start` runs on Angular's own port and proxies `/api` to `localhost:5177`, so `strata serve` has
to be running alongside it. This exists so a CSS change does not need a full rebuild; it is not how
the app ships.

### Tests

`npm test` runs Karma, which needs to launch and capture a real browser. In a sandboxed or headless
environment where Chrome cannot be captured it will time out rather than fail — that is the runner,
not the tests. `src/app/core/format.spec.ts` is the one that matters: it pins the number, unit and
time rules against the exact figures from the design brief, so a regression shows up as a mockup that
no longer matches.

## Layout

```
src/app/core/       api client, DTOs, formatting, series colours, shared state
src/app/charts/     the ECharts directive and shared chart chrome
src/app/ui/         pipes and the small shared components (path cell, kind chip, sparkline)
src/app/pages/      the five screens, lazily loaded, plus the first-run state
```

`core/format.ts` holds every number and time rule and has no Angular in it, which is why it is the
part with real test coverage.

## Two things worth knowing

**Fonts are bundled, not linked.** The project makes no network calls, and a `<link>` to
`fonts.googleapis.com` is one. See `public/fonts/README.md`.

**ECharts is registered piecewise.** `charts/echart.ts` imports only the chart types and components
actually drawn, which keeps it out of the initial bundle: the app loads in 88 kB and the 170 kB
charting chunk arrives with Timeline or Explorer.
