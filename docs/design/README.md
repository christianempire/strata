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

Record any here as they happen, with the reason. The brief was explicit that the mockups are the
visual language rather than a starting point, so a divergence should be a decision rather than a
drift.
