# Fonts

Pinned artefacts, not a dependency. Nothing here is fetched at build time and
nothing here is fetched at runtime from anywhere but this origin — the booking
page is public and bypasses Cloudflare Access, so a font request to a CDN would
be the one outbound call an invitee has no way to refuse.

All three families are OFL-1.1. The licence text ships beside the files, which
the licence requires.

| File | Family | Bytes | Loaded when |
|---|---|---:|---|
| `source-sans-3.latin.woff2` | Source Sans 3, wght 200–900 | 28,740 | always — preloaded |
| `ibm-plex-mono.num.woff2` | IBM Plex Mono, digits only | 2,260 | always — preloaded |
| `source-serif-4.latin.woff2` | Source Serif 4, wght 400 | 18,708 | booking page `<h1>`, after first paint |
| `source-sans-3.latin-ext.woff2` | Source Sans 3 | 60,088 | a Polish (or other latin-ext) name is on the page |
| `source-serif-4.latin-ext.woff2` | Source Serif 4 | 17,476 | ditto, in a link title |
| `source-sans-3.cyrillic.woff2` | Source Sans 3 | 18,496 | a Ukrainian (or other Cyrillic) name is on the page |

The split is load-bearing, not tidiness. Measured across the candidate faces:
the `latin` subset carries `ü` but not `ę ł ś ć ż`, so a single latin file would
have let Polish names fall back mid-word and change width. Splitting by
`unicode-range` means an English invitee never pays for the accents and a Polish
one pays only when there is a Polish name to draw.

`ibm-plex-mono.num.woff2` is subset to `U+0030-0039, U+003A, U+2013, U+2212` —
digits, colon, en dash, minus. That range is what lets it sit *first* in the
global font stack in `kairos.css` while every letter, space and comma behind it
still resolves to Source Sans. No wrapper spans, no per-component overrides.

## Regenerating

Sources are the Fontsource mirror of `google/fonts`. The variable files are
taken as published; the two derived files are not:

```bash
python3 -m venv /tmp/fonts && /tmp/fonts/bin/pip install 'fonttools[woff]' brotli

# Figures: digits and time punctuation only.
/tmp/fonts/bin/pyftsubset ibm-plex-mono-500.woff2 \
  --unicodes='U+0030-0039,U+003A,U+002E,U+002C,U+002D,U+2013,U+002F,U+0020,U+00A0,U+2212' \
  --layout-features='tnum,zero,kern,calt' \
  --flavor=woff2 --output-file=ibm-plex-mono.num.woff2

# Display: one weight, not a variable axis nobody varies.
/tmp/fonts/bin/fonttools varLib.instancer source-serif-4.ttf wght=400 -o pinned.ttf
/tmp/fonts/bin/pyftsubset pinned.ttf --unicodes='*' --flavor=woff2 \
  --output-file=source-serif-4.latin.woff2
```

Changing any file here changes the metric overrides in `kairos.css`. Recompute
them with `scripts/font-metrics.py` rather than leaving the old numbers in
place — they are what keeps the swap from reflowing the page, and a stale one
is worse than none because it looks deliberate.

Both budgets in `scripts/bundle-budget.sh` are measured against what is here.
