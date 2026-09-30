using FluentGpu.Foundation;
using FluentGpu.Render.Tiles;
using Xunit;

namespace FluentGpu.Engine.Tests;

/// <summary>The composite spends the analytic edge feather's per-pixel evaluation only where it is not exactly 1: a
/// feathered quad is split by the feather's unit interior into one feather-free piece and at most four feathered strips.
/// Exact (every pixel centre in the feather-free piece evaluates to 1.0f, so skipping it is bit-identical), a partition
/// (each pixel of the quad drawn once) and tight (a scrolled viewport pays the feather in its bands, not its area).</summary>
public sealed class FeatherQuadSplitTests
{
    private static EdgeFeather Random(Random r)
    {
        float x = (float)(r.NextDouble() * 60 - 20), y = (float)(r.NextDouble() * 60 - 20);
        float w = 40f + (float)(r.NextDouble() * 400), h = 40f + (float)(r.NextDouble() * 400);
        float Band() => r.Next(3) == 0 ? 0f : (float)(r.NextDouble() * 70);
        float Rad() => r.Next(2) == 0 ? 0f : (float)(r.NextDouble() * 30);
        return new EdgeFeather(new RectF(x, y, w, h), Band(), Band(), Band(), Band(),
            new CornerRadius4(Rad(), Rad(), Rad(), Rad()), (FadeFalloff)r.Next(3), r.Next(4) == 0 ? (float)r.NextDouble() : 1f);
    }

    [Fact]
    public void TheFeatherIsExactlyOneAtEveryPixelCentreOfTheUnitInterior()
    {
        var r = new Random(1234);
        for (int n = 0; n < 300; n++)
        {
            var f = Random(r);
            EdgeFeatherMask.UnitInterior(in f, out float il, out float it, out float ir, out float ib);
            int x0 = (int)MathF.Max(il, f.Rect.X - 30), x1 = (int)MathF.Min(ir, f.Rect.Right + 30);
            int y0 = (int)MathF.Max(it, f.Rect.Y - 30), y1 = (int)MathF.Min(ib, f.Rect.Bottom + 30);
            for (int py = y0; py < y1; py++)
                for (int px = x0; px < x1; px++)
                    Assert.Equal(1f, EdgeFeatherMask.Evaluate(in f, px + 0.5f, py + 0.5f));
        }
    }

    [Fact]
    public void TheInteriorIsTightOnEachEnabledEdge()
    {
        // the pixel just outside an enabled edge's interior bound is inside the band: the feather is below 1 there
        var f = new EdgeFeather(new RectF(10f, 20f, 300f, 200f), 0f, 40f, 0f, 40f, default);
        EdgeFeatherMask.UnitInterior(in f, out float il, out float it, out float ir, out float ib);
        Assert.Equal(-EdgeFeatherMask.Unbounded, il);
        Assert.Equal(EdgeFeatherMask.Unbounded, ir);
        Assert.Equal(60f, it);
        Assert.Equal(180f, ib);
        Assert.True(EdgeFeatherMask.Evaluate(in f, 100.5f, it - 1f + 0.5f) < 1f);
        Assert.True(EdgeFeatherMask.Evaluate(in f, 100.5f, ib + 0.5f) < 1f);
    }

    [Fact]
    public void ANoneFeatherIsUnboundedAndLeavesTheQuadWhole()
    {
        EdgeFeatherMask.UnitInterior(default, out float il, out float it, out float ir, out float ib);
        Span<FeatherPiece> p = stackalloc FeatherPiece[FeatherQuadSplit.MaxPieces];
        int n = FeatherQuadSplit.Split(0f, 0f, 512f, 512f, il, it, ir, ib, p);
        Assert.Equal(1, n);
        Assert.False(p[0].Feathered);
        Assert.Equal(512f * 512f, p[0].Area);
    }

    [Fact]
    public void ThePiecesPartitionTheQuadAndOnlyThePlainOneSkipsTheFeather()
    {
        var r = new Random(99);
        Span<FeatherPiece> p = stackalloc FeatherPiece[FeatherQuadSplit.MaxPieces];
        for (int n = 0; n < 500; n++)
        {
            var f = Random(r);
            EdgeFeatherMask.UnitInterior(in f, out float il, out float it, out float ir, out float ib);
            float qx = r.Next(-40, 300), qy = r.Next(-40, 300);
            float qw = r.Next(1, 300), qh = r.Next(1, 300);
            int count = FeatherQuadSplit.Split(qx, qy, qx + qw, qy + qh, il, it, ir, ib, p);
            // every pixel of the quad in exactly one piece; a pixel in a plain piece is exactly 1
            for (int py = (int)qy; py < (int)(qy + qh); py++)
                for (int px = (int)qx; px < (int)(qx + qw); px++)
                {
                    int hits = 0;
                    bool plain = false;
                    for (int k = 0; k < count; k++)
                        if (px >= p[k].X0 && px < p[k].X1 && py >= p[k].Y0 && py < p[k].Y1) { hits++; plain = !p[k].Feathered; }
                    Assert.Equal(1, hits);
                    if (plain) Assert.Equal(1f, EdgeFeatherMask.Evaluate(in f, px + 0.5f, py + 0.5f));
                }
            for (int k = 1; k < count; k++) Assert.True(p[k].Feathered);
        }
    }

    [Fact]
    public void AScrolledViewportPaysTheFeatherOnlyInItsBands()
    {
        // the virtualization bench's viewport at 1.5×: 1278×926 px, 60-px bands top and bottom, tiled 1024×512
        var f = new EdgeFeather(new RectF(36f, 88.5f, 1278f, 926f), 0f, 60f, 0f, 60f, default);
        EdgeFeatherMask.UnitInterior(in f, out float il, out float it, out float ir, out float ib);
        Span<FeatherPiece> p = stackalloc FeatherPiece[FeatherQuadSplit.MaxPieces];
        float feathered = 0f, total = 0f;
        for (int ty = 0; ty < 3; ty++)
            for (int tx = 0; tx < 2; tx++)
            {
                float x0 = 36f + tx * 1024f, y0 = 20f + ty * 512f;
                int count = FeatherQuadSplit.Split(x0, y0, x0 + 1024f, y0 + 512f, il, it, ir, ib, p);
                for (int k = 0; k < count; k++)
                {
                    // what is shaded: the piece inside the viewport scissor
                    var c = new FeatherPiece(MathF.Max(p[k].X0, f.Rect.X), MathF.Max(p[k].Y0, f.Rect.Y),
                        MathF.Min(p[k].X1, f.Rect.Right), MathF.Min(p[k].Y1, f.Rect.Bottom), p[k].Feathered);
                    total += c.Area;
                    if (c.Feathered) feathered += c.Area;
                }
            }
        // the feathered area: the two bands' rows (plus the ≤1-px rounding) across the viewport — not the viewport
        Assert.True(MathF.Abs(total - 1278f * 926f) < 1f, $"shaded {total} px");
        Assert.True(feathered <= 2f * 1278f * 62f, $"feathered {feathered} px of {total}");
    }
}
