# Design mockups

Delivered 19 Aug 2026 from Claude Design, built to `BRIEF.md`. Seven files, dark only.

| File | Covers |
|---|---|
| `Strata Palette and Type` | 29 tokens: background layers, three text levels, rules, deltas, the 12-hue series ramp, a nine-step type scale, and the number/unit/timezone rules |
| `Strata Timeline` | The headline screen. Live ECharts stacked area, 1H/24H/7D/30D/1Y, brush-to-feed filtering, click-to-mark |
| `Strata Overview` | Volumes grouped by disk, capacity bars with the hatched undercount band, 30-day sparklines, detached volume |
| `Strata Explorer` | ECharts treemap with drill-down, breadcrumb, sibling list; 3 children and 2,041; hardlink popover |
| `Strata Compare` | Ranked aggregated tree, growth and shrinkage together, "elsewhere in …" remainder rows, churn drawer |
| `Strata Settings` | Intervals, thresholds, defer conditions, retention, exclusions, per-volume fidelity, auditable scan log |
| `Strata States` | All eight states with the rule behind each |

`shots/` holds reference screenshots. `support.js` is the mockups' own runtime, not something to port.

## Notes carried over from the designer

- **The undercount arithmetic is shown, not asserted**: apparent 915 GB, less 36.4 GB of hardlink
  overlap, gives 849 GB attributed, plus 30 GB estimated for unreadable directories, reconciling to
  the 879 GB Windows reports. Worth keeping — it is the difference between a number that looks wrong
  and a number that explains itself.
- **D:'s top-level directory names are invented.** The brief only supplied C:'s. Replace them with
  real ones when building, and do not let them reach a screenshot.

## Deviations from the mockups

Recorded as they happened while building Phase 3b. The brief was explicit that the mockups are the
visual language rather than a starting point, so each of these is a decision rather than a drift.

**D:'s invented directory names never reached the build.** As flagged above. Every screen reads its
top-level directories from the API, so the names on screen are whatever is on the disk.

**Fonts are bundled rather than loaded from Google.** The mockups link `fonts.googleapis.com`, which
is a network call — against the project's third non-negotiable — and one that would report every time
a disk-contents tool is opened. Both families ship as latin-subset variable woff2, 77 KB for the
pair. The geometric shapes used as kind glyphs (`▲ ▼ ◆ ◇ ⇄ ◌`) are outside that subset and fall
through to the system font, which on Windows renders them from Segoe UI Symbol.

**The first-run percentage is measured in bytes, not entries.** `Strata States` frame 01 shows
"1,894,204 of 3,053,173 entries" — a denominator that can only come from a previous scan, which by
definition a first run does not have. The percentage therefore counts bytes read against what Windows
reports used, which is available immediately, and the API labels which basis it used so the screen
can say so. On a *re*scan the entry denominator exists and is used, because enumeration cost tracks
entries rather than bytes. The mockup's rule — progress only ever against a known denominator — is
kept; the denominator is just a different one on the run where the better one cannot exist.

**Compare is a ranked list that expands, not a pre-built tree.** The mockup draws a collapsible tree
with the whole hierarchy in hand. `/diff` returns a flat ranked list, and expanding a row runs the
same diff scoped to that path — which is exactly what the engine does when it descends, so an
expansion is the next level of the same answer rather than a second, differently-computed one. It
also avoids holding a tree of a volume's changes in the browser.

**The remainder row does not carry "412 directories, none above 0.4 GB".** The engine knows the
remainder's bytes but not how many directories were folded into it, and counting them honestly means
attributing every changed directory to its nearest reported ancestor — a whole extra pass for a
sub-label. The row states its bytes and the detail panel names the reporting floor instead.

**Compare's churn cell says "not visible" where the mockup shows a figure.** Comparing two snapshots
genuinely cannot see churn: a directory that wrote and deleted the same 50 GB between two
measurements is indistinguishable from one that did nothing. Where the honest answer is zero the cell
says so and points at the Timeline, which reads the change journal and can. When the rows do carry
churn above their own net, the figure is shown.

**Settings shows the knobs the scanner actually has.** The mockup includes retention tiers, a
history-on-disk breakdown, per-volume tracking toggles and defer-on-battery — none of which exist
yet; retention and downsampling are Phase 4. Drawing controls that do nothing would be worse than
leaving them out, so the screen covers interval, CPU threshold, borderless-fullscreen detection, I/O
priority, exclusions, per-volume scans and the audit log.

**The elevation story is only told when elevation is the cause.** The mockups attribute the whole
undercount to unreadable directories. On the target machine C: currently reports a 58 GB gap with
*zero* unreadable directories, so the note names what the rest actually is — filesystem metadata,
hardlinked bytes counted once at their first path, and writes since the last scan — rather than
blaming a cause that is not there. A volume that has never been scanned is reported as not scanned
rather than as a whole-disk undercount.

**The 30-day sparkline has a state the mockups do not.** It buckets to one reading per day, which
is what the label means — but a volume whose history is hours old has one bucket, and a single bar
stretched across the cell reads as a solid block rather than as a chart with one reading in it. Under
two buckets the cell says "under a day of history" instead.

**Treemap tiles cap at 40 before the tail merges.** The mockup's own figure. Stated here because it
is a number rather than a shape: the sibling list beside the map still lists every child.
