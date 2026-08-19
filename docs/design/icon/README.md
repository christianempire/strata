# Icon

Delivered 19 Aug 2026 from Claude Design, built to [`../ICON-BRIEF.md`](../ICON-BRIEF.md). The full
rationale — geometry table, contrast figures, the light-taskbar approach — is in
`../Strata Tray Icon.dc.html`. The three directions it was chosen from are the `Strata Icon A/B/C`
files beside it.

## What ships

| File | Shown when |
|---|---|
| `strata-idle.ico` | resident, nothing running — almost always |
| `strata-scanning.ico` | a scan is running |
| `strata-deferred.ico` | the gate said no: a game is fullscreen, or the CPU is busy |
| `strata-paused.ico` | suspended by the user |
| `strata.ico` | copy of idle; the executable's own icon and the Angular app's favicon |

Source is four SVGs and 28 PNGs at 16, 20, 24, 32, 48, 64 and 256. `tools/build-icons.py` assembles
each state's seven PNGs into one `.ico` and copies idle to `strata.ico` and to
`src/Strata.Web/public/favicon.ico`.

**Nothing is resampled at any step.** Every size is its own drawing, exported on the 16×16 grid, so
Windows picking the 32px entry at 200% DPI gets a drawing rather than a stretched 16.

```
python tools/build-icons.py     # after re-exporting from Claude Design
```

## The three decisions worth not undoing

**The mark brings its own ground.** A near-black `#0f0d0b` fill covers 14 of the 16 pixels, so the
bands are amber-on-near-black whatever is behind them — contrast is a property of the artwork rather
than of the taskbar. The alternative, a single mid-tone surviving both ends, was drawn as direction C
and rejected on measurement: amber holds 10.1:1 on a dark taskbar and 1.7:1 on a light one, below the
3:1 floor for non-text contrast.

**The whole mark carries state, rim and bands together.** One value changes and every pixel of ink
follows, so the state reads from the corner of the eye at 16px without the shape being re-read.

**Deferred also changes form, because colour is not enough.** `#ff9868` and `#f8ac3d` are 40° apart
at nearly the same lightness: side by side they separate, alone they do not, and idle-versus-deferred
is the most consequential distinction this icon makes. So the bands collapse from 2/1/3px to three
1px rules — solid block against sparse block, which survives greyscale and says the right thing as
well, since nothing is being deposited while deferred.

Cyan was rejected deliberately: `--neg` means shrinkage in every chart in the app, and spending it on
deferral would break that. Red was never a candidate — deferring is the software working correctly.

## If you re-draw it

16×16 is the size that matters; it is what Windows draws at 100% DPI. And there is one artwork for
both taskbars — Windows does not swap tray icons on theme change.
