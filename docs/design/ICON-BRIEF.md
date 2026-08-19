# Design brief for the Strata icon

Paste everything below the rule into Claude Design. Deliverables come back into this directory as
`icon/`.

The tray host reads `docs/design/icon/strata.ico`; anything else in that folder is source. Until the
real one lands, the build generates a deliberately plain placeholder — see `docs/design/icon/README.md`.

---

**Design the application and system-tray icon for Strata, a Windows disk-usage tool.**

Strata answers the question the snapshot tools don't: **what grew, when, and where?** WinDirStat and
WizTree show a point in time; Strata records history and diffs it, so it can say "your D: drive
gained 40 GB since last Tuesday, and 32 GB of it landed in `D:\Steam\steamapps`."

It runs resident and invisible. It lives in the notification area, scans in the background at very
low I/O priority, and defers while you are gaming. **The icon is the only thing most people will see
most days**, so it has to carry the product at a size where almost nothing survives.

## The hard constraint

**It has to work at 16×16.** That is the size Windows draws a notification-area icon at 100% DPI, and
it is the size that matters — 20, 24 and 32 are the same icon on a better display, not a chance to
add detail.

At 16px:

- No letterforms. An "S" at 16px is four grey pixels.
- No gradients that span fewer than about six pixels; they band or turn to mud.
- No outlines thinner than 1px, and remember 1px at 100% DPI is 2px at 200%, so hairlines that look
  elegant in the artboard vanish on the machine they were designed for.
- Three or four shapes is the budget. Five is too many.

Design **at 16×16 first** and scale up, rather than designing at 256 and hoping. If it only works
large, it does not work.

## It must read on both taskbars

Windows taskbars are dark by default and light for a meaningful minority, and the user can switch
without warning. The icon gets **one** artwork for both — Windows does not swap it — so it cannot
rely on a light shape that disappears on white, or a dark one that disappears on black.

The usual answer is a shape with its own contained ground, or a mid-tone with enough chroma to
separate from both ends. Please state which approach you took and why.

## The name is the idea

*Strata* — layers laid down over time, read as a cross-section. That is literally what the product
does: it stacks directories into bands and shows you the section through them. The Timeline screen is
a stacked area chart; the Explorer screen is a treemap. Horizontal bands of differing thickness are
already the visual signature of the app.

That is a strong starting point and probably the right one, but it is an observation rather than an
instruction. If a sedimentary cross-section reads as a barcode at 16px, say so and take it somewhere
else.

Things to avoid: magnifying glasses, pie charts, folder shapes, hard drives with a magnifying glass
on them, anything that looks like a generic utility. This is an instrument, not a cleaner.

## Palette

Use the app's own tokens, which are fixed and in `docs/design/Strata Palette and Type`:

| Token | Value | Role |
|---|---|---|
| `--pos` | `#f8ac3d` | amber. The product's one accent — the active tab, the growth delta, the progress bar |
| `--neg` | `#5cd0ea` | cyan. Shrinkage. The counterweight to amber |
| `--bg-0` | `#0f0d0b` | near-black, warm |
| `--fg-1` | `#f1eeea` | near-white, warm |
| `--s1`…`--s12` | see the palette file | the twelve-hue categorical ramp, if bands need distinguishing |

Amber against near-black is the app's signature pairing. The icon does not have to use every token —
two or three colours is likely correct at this size.

## Four states

The tray icon is also the product's status light. Please design **four variants** of the same mark:

| State | Meaning | Frequency |
|---|---|---|
| **Idle** | Resident, nothing running. The normal state and the one the product aims for. | almost always |
| **Scanning** | A scan is running. Should read as active without being animated — Windows tray animation is distracting and we will not use it. | seconds at a time, minutes on a baseline |
| **Deferred** | Paused because a game is fullscreen or the CPU is busy. **This is a feature, not a fault** — the whole product exists to honour it — so it must not look like an error. | a normal evening |
| **Paused** | Suspended by the user. A decision, not a condition. | rare, deliberate |

The four must be distinguishable **at 16px, side by side and in isolation**, and must obviously be
the same application. Changing one element — a band's colour, a dot, a fill level — usually beats
four different drawings.

Do not use red for deferred. Red means broken, and deferring is the software working correctly.

## Deliverables

- **SVG** for each of the four states, drawn on a 16×16 grid with everything on whole pixels.
- **PNG** for each state at 16, 20, 24, 32, 48, 64 and 256, so the `.ico` can be assembled without
  anyone guessing at the hinting.
- A short note on the light-taskbar approach, and on which element carries the state difference.
- The 16px versions rendered **at actual size on both a `#0f0d0b` and a `#f3f3f3` strip**, adjacent,
  so the two-taskbar claim can be checked rather than trusted.

Name the files by state — `strata-idle`, `strata-scanning`, `strata-deferred`, `strata-paused` — so
they can be assembled without renaming.

## Not in scope

An installer graphic, a wordmark, a splash screen, a website, a store listing. There is no marketing
surface anywhere in this product and there is not going to be one.
