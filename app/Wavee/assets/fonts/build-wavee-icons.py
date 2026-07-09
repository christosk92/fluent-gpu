#!/usr/bin/env python3
"""Build the WaveeIcons OTF/CFF icon font from two 24x24 SVG paths.

Why OTF/CFF (not TTF): the source SVGs use cubic beziers + elliptical arcs; CFF (Type2)
charstrings are cubic-native, so parse_path -> T2CharStringPen transfers the outlines with
NO quadratic conversion (which would round-trip the arcs through an approximation).

Coordinate mapping: the SVG viewBox is 24x24 with y pointing DOWN; a font em is 1000 units
with y pointing UP. S = 1000/24 scales viewBox -> em; the Transform(S,0,0,-S,0,S*24) matrix
flips y (the -S in the d-slot) and shifts the origin up by one em-box height (e = S*24 = 1000)
so the top-left SVG corner lands at (0, 1000) = the em-box top-left in font space.

Glyphs:
  0xE900 playNext  -> "Play next"  (play-on-top mark)
  0xE901 playAfter -> "Play after" (play-on-bottom / add-to-queue mark)

Run:  python build-wavee-icons.py     (writes wavee-icons.otf next to this file)
Requires fonttools (4.63 present).
"""

import os

from fontTools.fontBuilder import FontBuilder
from fontTools.misc.transform import Transform
from fontTools.pens.boundsPen import BoundsPen
from fontTools.pens.t2CharStringPen import T2CharStringPen
from fontTools.pens.transformPen import TransformPen
from fontTools.svgLib.path import parse_path
from fontTools.ttLib import TTFont

# ── Constants ────────────────────────────────────────────────────────────────────────────
UPM = 1000                      # units per em
VIEWBOX = 24                    # SVG viewBox edge
S = UPM / VIEWBOX               # scale viewBox(24) -> em(1000)
# viewBox(24,y-down) -> em(1000,y-up): scale by S, flip y, lift origin by one em height.
FLIP = Transform(S, 0, 0, -S, 0, S * VIEWBOX)

GLYPH_ORDER = [".notdef", "playNext", "playAfter"]
CMAP = {0xE900: "playNext", 0xE901: "playAfter"}

# Spotify's real "Play next" / "Add to queue" marks (24x24 viewBox).
PATHS = {
    "playNext": "M6 2.86V5H3a1 1 0 00-1 1v12a1 1 0 102 0V7h2v2.137a.5.5 0 00.748.434L13 5.998 6.748 2.426A.5.5 0 006 2.86ZM21 5h-5a1 1 0 100 2h5a1 1 0 100-2Zm0 6H9a1 1 0 000 2h12a1 1 0 000-2Zm0 6H9a1 1 0 000 2h12a1 1 0 000-2Z",
    "playAfter": "M21 6.998a1 1 0 100-2H9a1 1 0 000 2h12ZM6 21.138a.5.5 0 00.748.434L13 18l-6.252-3.573A.5.5 0 006 14.86V17H4V6a1 1 0 00-2 0v12a1 1 0 001 1h3v2.138Zm15-8.14a1 1 0 000-2H9a1 1 0 000 2h12Zm0 6a1 1 0 000-2h-5a1 1 0 000 2h5Z",
}


def build_charstring(d):
    """Parse one SVG path 'd' into a scaled/flipped CFF T2 charstring."""
    pen = T2CharStringPen(UPM, {})            # advance = em width; no component glyphs
    tpen = TransformPen(pen, FLIP)            # apply the viewBox->em + y-flip on the fly
    parse_path(d, tpen)                       # handles M/L/V/H/a/Z incl. arcs -> cubics
    return pen.getCharString()


def build():
    here = os.path.dirname(os.path.abspath(__file__))
    out = os.path.join(here, "wavee-icons.otf")

    charstrings = {}
    # .notdef MUST be an empty charstring (an outline-less, valid glyph 0).
    notdef = T2CharStringPen(UPM, {})
    charstrings[".notdef"] = notdef.getCharString()
    for name, d in PATHS.items():
        charstrings[name] = build_charstring(d)

    fb = FontBuilder(UPM, isTTF=False)
    fb.setupGlyphOrder(GLYPH_ORDER)
    fb.setupCharacterMap(CMAP)
    fb.setupCFF("WaveeIcons", {"FullName": "WaveeIcons"}, charstrings, {})
    fb.setupHorizontalMetrics({g: (UPM, 0) for g in GLYPH_ORDER})
    fb.setupHorizontalHeader(ascent=UPM, descent=0)
    fb.setupNameTable({"familyName": "WaveeIcons", "styleName": "Regular"})
    fb.setupOS2(sTypoAscender=UPM, sTypoDescender=0, usWinAscent=UPM, usWinDescent=0)
    fb.setupPost()
    fb.save(out)
    return out


def verify(path):
    """Reload the saved font and assert the cmap, outlines, and size are sane."""
    size = os.path.getsize(path)
    font = TTFont(path)
    cmap = font.getBestCmap()
    assert 0xE900 in cmap, "cmap missing 0xE900 (playNext)"
    assert 0xE901 in cmap, "cmap missing 0xE901 (playAfter)"
    assert cmap[0xE900] == "playNext", "0xE900 -> %r" % cmap[0xE900]
    assert cmap[0xE901] == "playAfter", "0xE901 -> %r" % cmap[0xE901]
    # The glyphs must carry real outlines (a valid 2-glyph CFF font is ~950B, so the
    # bounding-box check is the true "not broken/empty" guard; size is a coarse floor).
    gs = font.getGlyphSet()
    bounds = {}
    for name in ("playNext", "playAfter"):
        bp = BoundsPen(gs)
        gs[name].draw(bp)
        assert bp.bounds is not None, "%s has no outline" % name
        bounds[name] = tuple(round(v) for v in bp.bounds)
    assert size > 512, "font is only %d bytes (suspiciously empty)" % size
    return size, cmap, bounds


if __name__ == "__main__":
    out = build()
    size, cmap, bounds = verify(out)
    print("OK  wrote %s" % out)
    print("    size: %d bytes" % size)
    print("    cmap: 0xE900 -> %s, 0xE901 -> %s" % (cmap[0xE900], cmap[0xE901]))
    print("    bbox playNext : %s" % (bounds["playNext"],))
    print("    bbox playAfter: %s" % (bounds["playAfter"],))
