# Design brief for Strata

Paste everything below the rule into Claude Design. Mockups come back into this directory.

---

**Design the UI for Strata, a Windows disk-usage tool.**

Every other tool (WinDirStat, WizTree, TreeSize) shows you a point in time. Strata records history and
diffs it, so it answers a question they cannot: **what grew, when, and where?** The headline output is
a sentence like *"your D: drive gained 40 GB since last Tuesday, and 32 GB of it landed in
`D:\Steam\steamapps`."*

**Who and where.** One technical power user, on their own machine. It lives in the system tray;
clicking "Open UI" opens `http://localhost:5177` in their default browser. A desktop browser tab,
1280–2560px wide. No login, no accounts, no mobile, no onboarding funnel, no marketing surface
anywhere.

**Visual direction.** Dark, dense, information-first. **An instrument panel, not a product page.**
Think a well-made monitoring console or a pro audio meter bridge: numbers are the content, chrome is
minimal, information density is a feature rather than a problem. Reject the SaaS-dashboard defaults —
no hero cards with 40px of padding, no rounded pastel pills, no illustrations, no empty-state
mascots. Small type is fine. Tabular numerals throughout. Bytes always carry a unit and are never
rounded into meaninglessness. Deltas are signed and colour-coded, and growth versus shrinkage must
survive colour-blindness rather than relying on red against green.

**Please design an explicit palette and type scale**, not "whatever the component library does".
State exact values for: background layers, text at three emphasis levels, grid and rule lines,
positive delta, negative delta, and a categorical series ramp for the stacked area chart and the
treemap — about 12 distinguishable hues that stay legible on a dark ground.

## The five screens

Design against this real data from the target machine, so nothing is invented.

| Volume | Label | Capacity | Free | Files | Directories | Max depth |
|---|---|---|---|---|---|---|
| C: | OS-M2-1TB | 930 GB | 51 GB | 3,053,173 | 571,055 | 27 |
| D: | Storage-M2-1TB | 931 GB | 78 GB | 189,776 | 10,421 | 14 |
| E: | Loadstar-M2-1TB | 931 GB | 412 GB | 423,063 | 24,525 | 15 |

Real top-level directories on C:: `Users` 550 GB, `Program Files (x86)` 134 GB, `Program Files`
92.5 GB, `WINDOWS` 47.5 GB, `src` 42 GB, `ProgramData` 19.4 GB.

A real event from the change log, and a good worst case for path length:
`C:\Users\chris\Documents\My Programming Projects\aerisetta\warrior-of-lust\unity\Library\PlayerDataCache\Win642\Data`
— **−16.78 GB, vanished, −15 files**.

### 1. Overview

*Answers: is everything fine, and if not, which disk?* Per-volume capacity, how much Strata accounts
for against what Windows reports, a 30-day sparkline of used space, last scan time, and the three or
four largest changes across all volumes in the last 24 hours. Volumes grouped by physical disk.

### 2. Timeline — **the headline screen, give it the most attention**

*Answers: what grew, when, and where?* A stacked area chart of used space over time, series being
top-level directories plus an "other" band, with a **range selector** that has to work at 1 hour,
24 hours, 7 days, 30 days and 1 year. Below or beside it, the **events feed** for the selected range:
timestamp, path, signed delta, file-count delta, and kind (`grew` / `shrank` / `appeared` /
`vanished` / `moved`). Brushing the chart filters the feed; clicking an event marks it on the chart.
Paths are long — show how they truncate without losing the meaningful tail.

### 3. Explorer

*Answers: where are the bytes right now?* An **ECharts treemap** with drill-down, breadcrumb
navigation, and a sibling list showing each child's bytes, file count and share of parent. Design the
drill-down transition and the "you are here" state. A sunburst alternate is a nice-to-have. It must
handle a directory with 3 children and one with 2,000.

### 4. Compare

*Answers: what exactly changed between these two moments?* Pick two snapshots, get a ranked tree of
attributed changes. The idea to express visually: **200 files appearing under one game folder is ONE
row** — `...\common\Foo +32 GB, 200 new files` — not 200 rows. Show growth and shrinkage in the same
list, and show "churn" (bytes written *and* deleted, net near zero) as distinct from net change,
because a build directory that cycled 50 GB matters for SSD wear even though it looks unchanged.

### 5. Settings

*Answers: what is this thing doing to my machine?* Scan interval, CPU threshold, exclusion lists,
retention policy, manual scan triggers, and — importantly — an **auditable scan-history log**: every
tick, whether it ran or deferred, why it deferred, and how long it took. This is the screen where the
user verifies the tool is behaving.

## States that must be designed, not left to chance

These are not edge cases, they are the normal life of the app.

- **First run.** No baseline yet. A cold full scan of C: takes **3½ minutes**, and it has to read as
  "working, here is progress" rather than "frozen".
- **Scan in progress**, and **deferred** — "paused because a game is running fullscreen" is a
  first-class state that the whole product exists to honour.
- **Reduced fidelity.** A volume with no NTFS change journal falls back to a slow periodic walk and
  its history is coarser. Label it honestly and offer the one-time elevated fix.
- **Honest undercount.** Running unelevated, 557 directories on C: are unreadable and Strata's total
  is about 30 GB under what Windows reports. Design how that is disclosed — visible, not buried, or
  the number looks like a bug.
- **Hardlinks.** `C:\Windows\WinSxS` is largely links to bytes counted elsewhere. Show a path as "a
  link to bytes counted at another path" without it reading as an error.
- **Detached volume** whose history is still browsable.
- **Empty range** — a timeline window in which nothing happened.

## Technical constraints

Angular with standalone components, plus ECharts, served as static files from the same localhost
origin. Design tokens as CSS custom properties. Dark is the primary theme; say explicitly whether a
light theme is in scope.

## Deliverables

Static mockups — Figma-style frames, single-file HTML or React, whichever suits — for all five
screens plus the states above, an explicit palette and type scale, and short interaction notes for
the two hard ones: the Timeline brush-to-feed link, and the Explorer drill-down. Name the files by
screen so they can be built one at a time.
