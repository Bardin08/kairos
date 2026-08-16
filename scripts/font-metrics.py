#!/usr/bin/env python3
"""Regenerate the metric-override numbers in the FACES block of kairos.css.

A web font that arrives after first paint replaces a fallback of a different
width, and the text reflows. `size-adjust` plus the three `*-override`
descriptors make the fallback render at the web font's metrics so the swap
costs no layout shift. The numbers are a property of the two font files and
have to be recomputed whenever either one changes — which is what this is for.

    python3 scripts/font-metrics.py

Requires fonttools and brotli, which are not project dependencies; this runs by
hand when a face changes, not in CI:

    python3 -m venv /tmp/fm && /tmp/fm/bin/pip install 'fonttools[woff]' brotli
    /tmp/fm/bin/python scripts/font-metrics.py

The fallbacks are read from the machine running this. macOS paths are below;
on another OS, point FALLBACKS at the equivalent files. A fallback that is not
present is skipped and said so, rather than silently dropping a row from the
output.
"""

from __future__ import annotations

import sys
from pathlib import Path

try:
    from fontTools.ttLib import TTFont
except ImportError:  # pragma: no cover - operator-facing message
    sys.exit("fonttools is not installed; see the module docstring")

ROOT = Path(__file__).resolve().parent.parent
FONTS = ROOT / "frontend" / "public" / "fonts"

# English letter frequency, the same weighting fontaine uses. The space is
# weighted heavily because it is the most common character in running text and
# its advance dominates the average line's width.
FREQUENCY = {
    "a": 0.0817, "b": 0.0150, "c": 0.0278, "d": 0.0425, "e": 0.1270,
    "f": 0.0223, "g": 0.0202, "h": 0.0609, "i": 0.0697, "j": 0.0015,
    "k": 0.0077, "l": 0.0403, "m": 0.0241, "n": 0.0675, "o": 0.0751,
    "p": 0.0193, "q": 0.0010, "r": 0.0599, "s": 0.0633, "t": 0.0906,
    "u": 0.0276, "v": 0.0098, "w": 0.0236, "x": 0.0015, "y": 0.0197,
    "z": 0.0007, " ": 0.1818,
}

# The fallback a browser would actually reach for, per platform. Only the ones
# present on this machine can be measured; the rest print as skipped.
FALLBACKS = {
    "Apple (SF)": Path("/System/Library/Fonts/SFNS.ttf"),
    "Generic (Arial/Helvetica)": Path("/System/Library/Fonts/Supplemental/Arial.ttf"),
}


def measure(path: Path) -> dict[str, float]:
    """Average advance width and vertical metrics, both normalised to the em."""
    font = TTFont(str(path), lazy=True)
    upm = font["head"].unitsPerEm
    cmap = font.getBestCmap()
    metrics = font["hmtx"]

    weighted = weights = 0.0
    for char, weight in FREQUENCY.items():
        glyph = cmap.get(ord(char))
        if glyph and glyph in metrics.metrics:
            weighted += metrics.metrics[glyph][0] * weight
            weights += weight
    if not weights:
        raise SystemExit(f"{path}: no measurable glyphs, so no average width")

    os2, hhea = font["OS/2"], font["hhea"]
    # USE_TYPO_METRICS (fsSelection bit 7) is the font saying its typo metrics
    # are the authoritative ones. Reading hhea when it is set gives numbers the
    # browser will not agree with.
    if os2.fsSelection & (1 << 7):
        ascent, descent, gap = os2.sTypoAscender, os2.sTypoDescender, os2.sTypoLineGap
    else:
        ascent, descent, gap = hhea.ascent, hhea.descent, hhea.lineGap

    return {
        "width": (weighted / weights) / upm,
        "ascent": ascent / upm,
        "descent": abs(descent) / upm,
        "gap": gap / upm,
    }


def report(label: str, web: dict[str, float], fallback: dict[str, float]) -> None:
    adjust = web["width"] / fallback["width"]
    print(f"  /* {label} */")
    print(f"  size-adjust:       {adjust * 100:.2f}%;")
    print(f"  ascent-override:   {web['ascent'] / adjust * 100:.2f}%;")
    print(f"  descent-override:  {web['descent'] / adjust * 100:.2f}%;")
    print(f"  line-gap-override: {web['gap'] / adjust * 100:.2f}%;")
    print()


def main() -> None:
    sans = FONTS / "source-sans-3.latin.woff2"
    serif = FONTS / "source-serif-4.latin.woff2"
    for path in (sans, serif):
        if not path.exists():
            raise SystemExit(f"{path} is missing; nothing to measure against")

    web_sans = measure(sans)
    web_serif = measure(serif)
    print(f"Source Sans 3  avg width {web_sans['width']:.4f} em")
    print(f"Source Serif 4 avg width {web_serif['width']:.4f} em")
    print()

    print('@font-face { font-family: "Kairos Sans Fallback ..." }')
    for label, path in FALLBACKS.items():
        if not path.exists():
            print(f"  /* {label}: {path} not on this machine — skipped */\n")
            continue
        report(label, web_sans, measure(path))

    # The display face's fallback is the sans this product already loads, not a
    # system font: by the time the H1 needs a stand-in, Kairos Sans is there.
    print('@font-face { font-family: "Kairos Display Fallback" }')
    report("Source Serif 4 over Source Sans 3", web_serif, web_sans)


if __name__ == "__main__":
    main()
