# text-snap-check.py <png> — numeric verdict for the glyph vertical pixel-snap shot (--shot text-snap).
# The scene: 12 identical "Source code" 14px labels, row i offset by i*0.1 DIP of top padding inside a 30-DIP row,
# root padding 20 DIP. With the glyph-run Y snap all 12 rows must rasterize IDENTICAL ink-row profiles (modulo a
# whole-pixel shift); without it the fractional rows lose bottom-row coverage to the linear atlas sampler.
# Verdict: GREEN iff max pairwise aligned-L1 profile distance < 2% of profile mass AND bottom-row mass spread < 10%.
import sys
from PIL import Image

png = sys.argv[1]
img = Image.open(png).convert("L")
W, H = img.size
px = img.load()
scale = W / 420.0                      # shot rendered at --w 420; PNG is device pixels
pad = int(round(20 * scale))           # root padding (20 DIP)
rowh = 30 * scale                      # wrapper height (30 DIP)

def band_profile(i):
    y0, y1 = int(pad + i * rowh), int(pad + (i + 1) * rowh)
    bg = min(px[x, y] for y in range(y0, y1) for x in range(pad, W - pad, 7))
    rows = []
    for y in range(y0, min(y1, H)):
        s = sum(max(0, px[x, y] - bg - 6) for x in range(pad, W - pad))
        rows.append(s)
    # trim to the ink span (keep faint edges: threshold at 0.5% of peak)
    peak = max(rows) or 1
    nz = [k for k, v in enumerate(rows) if v > peak * 0.005]
    return rows[nz[0]:nz[-1] + 1] if nz else []

def aligned_l1(a, b):
    # best whole-pixel alignment in [-3, 3]: the snap may shift a row by one device px — that is fine
    best = None
    for sh in range(-3, 4):
        d = pads = 0
        n = max(len(a), len(b)) + abs(sh)
        for k in range(-abs(sh), n):
            va = a[k] if 0 <= k < len(a) else 0
            vb = b[k + sh] if 0 <= k + sh < len(b) else 0
            d += abs(va - vb)
        best = d if best is None else min(best, d)
    return best

profiles = [band_profile(i) for i in range(12)]
masses = [sum(p) for p in profiles]
bottoms = [ (p[-1] / max(p)) if p else 0 for p in profiles ]   # last ink row vs peak row

print(f"{png}: {W}x{H} scale={scale:.2f}")
for i, p in enumerate(profiles):
    print(f"  row {i:2d}: inkRows={len(p):2d} mass={masses[i]:7d} bottomRow/peak={bottoms[i]*100:5.1f}%")

mean_mass = sum(masses) / len(masses)
worst = max(aligned_l1(profiles[0], profiles[i]) for i in range(1, 12))
worst_pct = 100.0 * worst / mean_mass
b_spread = (max(bottoms) - min(bottoms)) * 100
print(f"  max aligned-L1 vs row0 = {worst_pct:.1f}% of mass | bottom-row spread = {b_spread:.1f}pp | inkRows {min(len(p) for p in profiles)}..{max(len(p) for p in profiles)}")
ok = worst_pct < 2.0 and b_spread < 10.0
print("  VERDICT: " + ("GREEN — all 12 phases rasterize identically (snap active)" if ok
      else "RED — fractional-phase rows render differently (bottom coverage row attenuated)"))
sys.exit(0 if ok else 1)
