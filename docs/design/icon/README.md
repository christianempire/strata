# Icon

`strata tray` loads these from `icons/` next to the executable; the Host copies them here at build
time. Four states, one file each:

| File | Shown when |
|---|---|
| `strata-idle.ico` | resident, nothing running — almost always |
| `strata-scanning.ico` | a scan is running |
| `strata-deferred.ico` | the gate said no: a game is fullscreen, or the CPU is busy |
| `strata-paused.ico` | suspended by the user |

`strata.ico` is a copy of the idle state, for the executable's own icon.

## These are placeholders

**Generated, not designed.** Three amber bands on a dark ground, which is the word "strata" taken at
its crudest. They exist so the tray has something to draw and so that *missing* looks different from
*finished* — not as a proposal.

The real artwork is briefed in [`../ICON-BRIEF.md`](../ICON-BRIEF.md), to be produced by Claude
Design the same way the screen mockups were. When it lands, drop the four `.ico` files in here with
the same names and rebuild; nothing in the code needs to change.

Regenerate the placeholders with:

```
python tools/make-placeholder-icon.py
```

## What the real ones have to survive

Repeating the two constraints from the brief, because they are the ones that get lost between a
handsome 256px render and a tray:

- **16×16 is the size that matters.** That is what Windows draws at 100% DPI. 20, 24 and 32 are the
  same icon on a better display, not a chance to add detail.
- **One artwork, two taskbars.** Windows does not swap icons for light and dark themes, so the mark
  cannot rely on being light or on being dark.

The loader asks for `GetSystemMetrics(SM_CXSMICON)` rather than a hard-coded 16, so a `.ico`
containing 16, 20, 24 and 32 lets Windows pick the right one instead of scaling a single bitmap.
