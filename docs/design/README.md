# Design mockups

Delivered 19 Aug 2026 from Claude Design. Seven files, dark only.

These *are* the visual language, not a starting point. The tokens in
`src/Strata.Web/src/styles.css` are lifted from `Strata Palette and Type` verbatim, and a component
that wants a colour takes it from a token rather than inventing one.

| File | Covers |
|---|---|
| `Strata Palette and Type` | 29 tokens: background layers, three text levels, rules, deltas, the 12-hue series ramp, a nine-step type scale, and the number/unit/timezone rules |
| `Strata Timeline` | The headline screen. Live ECharts stacked area, 1H/24H/7D/30D/1Y, brush-to-feed filtering, click-to-mark |
| `Strata Overview` | Volumes grouped by disk, capacity bars with the hatched undercount band, 30-day sparklines, detached volume |
| `Strata Explorer` | ECharts treemap with drill-down, breadcrumb, sibling list; 3 children and 2,041; hardlink popover |
| `Strata Compare` | Ranked aggregated tree, growth and shrinkage together, "elsewhere in …" remainder rows, churn drawer |
| `Strata Settings` | Intervals, thresholds, defer conditions, retention, exclusions, per-volume fidelity, auditable scan log |
| `Strata States` | All eight states with the rule behind each |
| `Strata Tray Icon` | The chosen icon: geometry table, contrast figures, the light-taskbar approach |
| `Strata Icon A/B/C` | The three directions it was chosen from — contained section, core sample, open section |

`shots/` holds reference screenshots. `icon/` holds the shipped artwork and its own README.
`support.js` is the mockups' own runtime, not something to port.

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

**EFI and recovery partitions are kept out of the pickers.** The mockups show three volumes because
the brief supplied three; this machine enumerates five, the extra two being an EFI system partition
and a WinRE recovery partition. They are identified by having no drive letter — Windows leaves them
unmounted on purpose, and a volume with no path is not one anyone browses to. Overview counts them in
a footnote and Settings lists them in full with a `system` chip, so they are out of the way rather
than hidden.

**Explorer dates a reading once it is more than an hour old.** The mockups draw Explorer as a live
answer to "where are the bytes right now?", with no age on it. It is only ever as fresh as the last
successful tick, and when C: stopped ticking for 27 hours the screen went on showing a folder that
had been deleted the day before, at its full 16.84 GiB, looking exactly like the truth. It follows
the same rule as the not-connected note next to it now: the reading is stated as of when it was
true, with the reason it has not moved since, rather than being hidden or silently refreshed. One
hour is the threshold because at the default fifteen-minute interval that is four ticks that did not
run — deliberately the same count at which the scanner stops waiting and forces the issue.

**The per-volume "journal live" badge checks that the journal is live.** It was derived from the
filesystem alone — NTFS, therefore a journal, therefore fine — and said "journal live" for 27 hours
about a journal that had been trimmed past Strata's cursor and could not be read at all. A badge
asserting that a mechanism works has to be reading whether it is working, so it now says
`journal cursor lost` when the cursor cannot be resumed, `no tick for 27 h` when the volume has gone
quiet for another reason, and `journal live · catching up` while a volume is draining a backlog. The
mockup's three fidelity states are unchanged; these sit below them.

**Settings gains one knob the mockup does not have: the interval while catching up.** Consistent
with the rule above about only drawing controls that do something — it is a real setting, and the
one the incident turned on. It sits under the scan interval and shows the drain rate it implies in
records per second, because that number against the volume's churn is the whole of what it decides.

**Settings has the retention tiers and the history breakdown now, and they say something the mockup
did not.** Both were left out of Phase 3b because retention did not exist; it does. The tiers are
preset buttons rather than free entry, because they have to stay in order and a form that lets you
enter an invalid pair and then rejects it is a worse form than one that cannot — widening a tier
widens whatever sat behind it. The breakdown adds a sentence the mockup has no equivalent of: most
of the database is *current* state, sized by how many files the machine has, so shortening retention
reclaims far less than it looks like it should. Measured on this machine, the history is 14% of a
680 MB file. Saying so up front is cheaper than letting somebody turn the tiers down and wonder why
nothing happened.

**Treemap tiles cap at 40 before the tail merges.** The mockup's own figure. Stated here because it
is a number rather than a shape: the sibling list beside the map still lists every child.
