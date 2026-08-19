# Bundled fonts

`IBM Plex Sans` and `JetBrains Mono`, latin subsets, woff2, from Google Fonts.

**Bundled rather than linked.** The project's third non-negotiable is that Strata makes no network
calls. A `<link>` to `fonts.googleapis.com` is one, and it also tells a third party every time
somebody opens the tool — on an app whose entire subject matter is the contents of your disks. So the
files are served from the same localhost origin as everything else.

Both are variable fonts, so one file per family covers every weight the design uses: Sans at 400, 500
and 600, Mono at 400, 500 and 700. 77 KB for the pair.

The latin subset covers `U+0000-00FF` plus the punctuation and symbol ranges Google groups with it,
which includes `U+2212 MINUS SIGN` — the character every negative delta in this interface is written
with. It does **not** cover the geometric shapes used as kind glyphs (`▲ ▼ ◆ ◇ ⇄ ◌`); those fall
through to the system font, which on Windows renders them from Segoe UI Symbol.

To refresh them, request the CSS with a browser user-agent, take the `@font-face` block whose
`unicode-range` begins `U+0000-00FF`, and download its `url()`:

```
https://fonts.googleapis.com/css2?family=IBM+Plex+Sans:wght@400;500;600&family=JetBrains+Mono:wght@400;500;700&display=swap
```
