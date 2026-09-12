using FluentGpu.Foundation;
using FluentGpu.Render;
using Xunit;

namespace FluentGpu.Windows.Tests;

/// <summary>
/// The PURE placement contract for the edge-fade strip snapshot scratch (<see cref="EdgeFadeStripPack"/>) — the layout
/// the D3D12 <c>OpacityLayerCompositor</c> leases, copies into and restores from. The defect these pin: a plain
/// vertical stack sized the lease <c>max(width) × Σ(height)</c>, so a full-window four-edge fade at 1770×1140 leased
/// 1770×4656 (bucketed 1792×8192 = 56 MiB) to hold ~137 kpx of strips.
/// </summary>
public sealed class EdgeFadeStripPackTests
{
    private static PushLayerCmd EdgeFadeLayer(RectF rect, RectF clip, float band)
        => new(rect, default, default, default, 0f, 0f, 0f, 0f,
            (int)LayerKind.EdgeFade, 1f,
            band, band, band, band, 0, 1f, 1 | 2 | 4 | 8, clip);

    /// <summary>The strips a full-window four-edge fade actually produces (via the owning geometry, not hand-written
    /// boxes), at scale 1 so band DIPs are band pixels.</summary>
    private static SelfBlurPixelBox[] FourEdgeStrips(int w, int h, float band)
    {
        PushLayerCmd layer = EdgeFadeLayer(new RectF(0, 0, w, h), new RectF(0, 0, w, h), band);
        var scratch = new SelfBlurPixelBox[EdgeFadeStrips.MaxStrips];
        EdgeFadeStrips.Compute(in layer, 1f, w, h, scratch, out int count);
        Assert.Equal(4, count);
        return scratch;
    }

    private static SelfBlurPixelBox[][] StripSets() =>
    [
        FourEdgeStrips(1770, 1140, 24f),                                         // the probe's shape
        FourEdgeStrips(400, 400, 8f),                                            // four edges, square
        [new SelfBlurPixelBox(0, 0, 200, 32)],                                   // a single horizontal strip
        [new SelfBlurPixelBox(0, 0, 32, 200)],                                   // a single vertical strip
        [default],                                                               // one degenerate zero-size strip
        [new SelfBlurPixelBox(0, 0, 200, 32), default, new SelfBlurPixelBox(5, 5, 5, 205)],
        [                                                                        // 8 strips, both orientations
            new SelfBlurPixelBox(0, 0, 300, 10), new SelfBlurPixelBox(0, 0, 12, 300),
            new SelfBlurPixelBox(0, 0, 250, 40), new SelfBlurPixelBox(0, 0, 40, 250),
            new SelfBlurPixelBox(0, 0, 7, 7),    new SelfBlurPixelBox(0, 0, 1, 900),
            new SelfBlurPixelBox(0, 0, 900, 1),  new SelfBlurPixelBox(0, 0, 64, 64),
        ],
    ];

    // (1) The probe's shape: a full-window 1770×1140 fade with 24-px bands packs into ≤ 1770×1200 PER HALF. The old
    // vertical stack produced 1770×2328 per half (4656 rows for both halves, bucketed to 8192).
    [Fact]
    public void FullWindowFourEdgeFade_PacksIntoTheWindowFootprint()
    {
        SelfBlurPixelBox[] strips = FourEdgeStrips(1770, 1140, 24f);
        EdgeFadeStripPack.Measure(strips, strips.Length, out int packW, out int packH);

        Assert.True(packW <= 1770, $"packW={packW}");
        Assert.True(packH <= 1200, $"packH={packH}");
        // Both halves together: under 20 MiB of B8G8R8A8, against the 56 MiB the stack leased.
        long bytes = (long)packW * (packH * 2) * 4L;
        Assert.True(bytes < 20L * 1024 * 1024, $"scratch={bytes} bytes");
    }

    // (2) No two strips overlap and none crosses the half boundary: a pixel packed twice would be restored twice, and a
    // D placement spilling past packH would read the F half's post-subtree pixels as its backdrop.
    [Fact]
    public void PlacementsAreDisjointAndStayInsideTheirHalf()
    {
        foreach (SelfBlurPixelBox[] strips in StripSets())
        {
            int count = strips.Length;
            EdgeFadeStripPack.Measure(strips, count, out int packW, out int packH);

            var xs = new int[count];
            var ys = new int[count];
            for (int i = 0; i < count; i++)
            {
                EdgeFadeStripPack.Place(strips, count, i, out int x, out int y);
                xs[i] = x; ys[i] = y;
                Assert.True(x >= 0 && y >= 0, $"strip{i} placed at ({x},{y})");
                Assert.True(x + strips[i].Width <= packW, $"strip{i} overflows packW={packW}");
                Assert.True(y + strips[i].Height <= packH, $"strip{i} crosses the half boundary packH={packH}");
            }

            for (int i = 0; i < count; i++)
            {
                if (strips[i].IsEmpty) continue;   // an empty strip covers no pixel and so can overlap nothing
                for (int j = i + 1; j < count; j++)
                {
                    if (strips[j].IsEmpty) continue;
                    bool overlap =
                        Math.Max(xs[i], xs[j]) < Math.Min(xs[i] + strips[i].Width, xs[j] + strips[j].Width) &&
                        Math.Max(ys[i], ys[j]) < Math.Min(ys[i] + strips[i].Height, ys[j] + strips[j].Height);
                    Assert.False(overlap, $"strips {i}/{j} overlap in the pack");
                }
            }
        }
    }

    // (3) D and F are the SAME placement plus the half offset — the restore shader carries ONE "row delta to the F
    // half" constant (packH), which is only correct if the halves are congruent and the F half stays in [packH, 2packH).
    [Fact]
    public void TheTwoHalvesDifferOnlyByTheHalfOffset()
    {
        foreach (SelfBlurPixelBox[] strips in StripSets())
        {
            int count = strips.Length;
            EdgeFadeStripPack.Measure(strips, count, out _, out int packH);
            for (int i = 0; i < count; i++)
            {
                EdgeFadeStripPack.Place(strips, count, i, out int dx, out int dy);
                int fx = dx, fy = dy + packH;                    // what CopyStripSnapshot(post: true) writes
                Assert.Equal(dx, fx);
                Assert.Equal(packH, fy - dy);
                Assert.True(fy >= packH && fy + strips[i].Height <= packH * 2, $"strip{i} F half out of range");
            }
        }
    }

    // Measure is exactly the bounding box of the placements — no slack row or column beyond the shelf.
    [Fact]
    public void MeasureIsTheBoundingBoxOfThePlacements()
    {
        foreach (SelfBlurPixelBox[] strips in StripSets())
        {
            int count = strips.Length;
            EdgeFadeStripPack.Measure(strips, count, out int packW, out int packH);
            int maxX = 0, maxY = 0;
            for (int i = 0; i < count; i++)
            {
                EdgeFadeStripPack.Place(strips, count, i, out int x, out int y);
                maxX = Math.Max(maxX, x + strips[i].Width);
                maxY = Math.Max(maxY, y + strips[i].Height);
            }
            Assert.Equal(packW, maxX);
            Assert.Equal(packH, maxY);
        }
    }

    // The shelf layout itself: wide bands on one column at x = 0, tall bands side by side on one shelf beneath them.
    [Fact]
    public void FourEdgeFadeUsesTheColumnPlusShelfLayout()
    {
        SelfBlurPixelBox[] s = FourEdgeStrips(1770, 1140, 24f);
        EdgeFadeStripPack.Measure(s, 4, out int packW, out int packH);
        Assert.Equal(1770, packW);          // the two full-width bands set the width
        Assert.Equal(1140, packH);          // 24 + 24 (column) + 1092 (the shelf's tallest)

        EdgeFadeStripPack.Place(s, 4, 0, out int x0, out int y0);   // top band
        EdgeFadeStripPack.Place(s, 4, 1, out int x1, out int y1);   // bottom band
        EdgeFadeStripPack.Place(s, 4, 2, out int x2, out int y2);   // left band
        EdgeFadeStripPack.Place(s, 4, 3, out int x3, out int y3);   // right band
        Assert.Equal((0, 0), (x0, y0));
        Assert.Equal((0, s[0].Height), (x1, y1));
        Assert.Equal((0, s[0].Height + s[1].Height), (x2, y2));
        Assert.Equal((s[2].Width, s[0].Height + s[1].Height), (x3, y3));
    }

    // Degenerate inputs the compositor relies on: an empty set measures 0 (AcquireStripScratch then returns -1 and the
    // fade falls back to the legacy lease), and an out-of-range index places at the origin rather than throwing on the
    // render thread.
    [Fact]
    public void DegenerateInputsMeasureZeroAndPlaceAtOrigin()
    {
        EdgeFadeStripPack.Measure(default, 0, out int packW, out int packH);
        Assert.Equal(0, packW);
        Assert.Equal(0, packH);

        var one = new[] { new SelfBlurPixelBox(0, 0, 10, 4) };
        EdgeFadeStripPack.Place(one, 1, 7, out int x, out int y);
        Assert.Equal(0, x);
        Assert.Equal(0, y);

        EdgeFadeStripPack.Measure(one, 5, out int w, out int h);   // count past the span end is clamped
        Assert.Equal(10, w);
        Assert.Equal(4, h);
    }
}
