using System;
using FluentGpu.Foundation;
using FluentGpu.Render;
using FluentGpu.Render.Tiles;
using FluentGpu.Rhi;
using Xunit;

namespace FluentGpu.Engine.Tests;

/// <summary>The retained self-blur's geometry (GroupCacheKey.BlurRegions / LeafBlur): a blurred leaf moved rigidly by whole
/// pixels keeps its content key; a blurred leaf's source is cut to its composite clip grown by the pipeline's reach
/// (SelfBlurRegion.SupportRadius), so a row scrolled out of its viewport blurs nothing.</summary>
public sealed class RetainedBlurTests
{
    private struct Serials : ITileSerials
    {
        public uint[] Of;
        public readonly uint Serial(int surface) => (uint)surface < (uint)Of.Length ? Of[surface] : 0u;
    }

    private static CompositeItem BlurLeaf(float dx, float dy, RectF clip, RectF source, float sigma = 6.5f)
        => new(4, CompositeKind.Region, Affine2D.Translation(dx, dy), 1f, clip, default, default, sigma, default, 0, SourceClip: source);

    [Fact]
    public void ABlurredLeafMovedByWholePixels_KeepsItsContentKey()
    {
        var clip = new RectF(0f, 0f, 1000f, 1000f);
        CompositeItem at = BlurLeaf(128f, 192f, clip, new RectF(150f, 200f, 400f, 100f));
        CompositeItem moved = BlurLeaf(128f, 256f, clip, new RectF(150f, 264f, 400f, 100f));
        TilePlacement[] placed = [new(new TileKey(4, 0, 0), Surface: 3)];
        var serials = new Serials { Of = [0u, 0u, 0u, 17u] };
        GroupCacheKey.BlurRegions(in at, 1200, 1100, out PixelRect s0, out PixelRect r0);
        GroupCacheKey.BlurRegions(in moved, 1200, 1100, out PixelRect s1, out PixelRect r1);
        Assert.Equal(GroupCacheKey.LeafBlur(in at, placed, in s0, in r0, ref serials), GroupCacheKey.LeafBlur(in moved, placed, in s1, in r1, ref serials));
        // a re-rastered source tile is new content
        var bumped = new Serials { Of = [0u, 0u, 0u, 18u] };
        Assert.NotEqual(GroupCacheKey.LeafBlur(in at, placed, in s0, in r0, ref serials), GroupCacheKey.LeafBlur(in at, placed, in s0, in r0, ref bumped));
    }

    [Fact]
    public void ABlurredLeafsSource_IsCutToItsClipGrownByTheReach()
    {
        const float sigma = 6.5f;
        int reach = SelfBlurRegion.SupportRadius(sigma);
        Assert.True(reach > SelfBlurRegion.TapRadius(sigma));
        var clip = new RectF(100f, 100f, 500f, 400f);   // y 100..500
        // wholly below the clip, past the reach: nothing to blur
        GroupCacheKey.BlurRegions(BlurLeaf(0f, 0f, clip, new RectF(120f, 500f + reach + 10f, 300f, 80f)), 1200, 1100, out PixelRect gone, out _);
        Assert.True(gone.IsEmpty);
        // straddling the bottom edge: cut at clip bottom + reach
        GroupCacheKey.BlurRegions(BlurLeaf(0f, 0f, clip, new RectF(120f, 450f, 300f, 200f)), 1200, 1100, out PixelRect cut, out PixelRect region);
        Assert.Equal(new PixelRect(120, 450, 420, 500 + reach), cut);
        Assert.Equal(500 + reach, region.Bottom);
        // inside the clip: untouched
        GroupCacheKey.BlurRegions(BlurLeaf(0f, 0f, clip, new RectF(120f, 200f, 300f, 80f)), 1200, 1100, out PixelRect inside, out _);
        Assert.Equal(new PixelRect(120, 200, 420, 280), inside);
        // an unbounded clip keeps the recorded source
        GroupCacheKey.BlurRegions(BlurLeaf(0f, 0f, default, new RectF(120f, 2000f, 300f, 80f)), 1200, 1100, out PixelRect unbounded, out _);
        Assert.Equal(new PixelRect(120, 2000, 420, 2080), unbounded);
    }

    [Fact]
    public void ALowResBlur_RunsOnTheFullResolutionGrid_OnlyWhenItsDownsampleLandsThere()
    {
        Assert.True(SelfBlurRegion.LowResBlurOnSameGrid(40f, 4));    // the visualizer's clouds: 4 × 4 = 16
        Assert.True(SelfBlurRegion.LowResBlurOnSameGrid(18f, 4));    // 4 × 2 = 8
        Assert.True(SelfBlurRegion.LowResBlurOnSameGrid(9f, 2));     // 2 × 2 = 4
        Assert.False(SelfBlurRegion.LowResBlurOnSameGrid(6f, 4));    // the blur itself stops at 1/2: a 1/4 grid is coarser
        Assert.False(SelfBlurRegion.LowResBlurOnSameGrid(100f, 4));  // the full schedule clamps at 1/16, the low-res one would not
    }

    /// <summary>A 1-D model of the blur pipeline exactly as the shaders run it: 2× box downsamples (PSDown2 samples between
    /// two texels), the bilinear-folded separable Gaussian (PSBlur, <see cref="AcrylicBackdropMath.BuildKernel"/>), and the
    /// bilinear upsample of the composite (PSSample at (x + ½)/down). Returns, for an impulse at source px
    /// <paramref name="p"/>, the farthest full-resolution output px it changes.</summary>
    private static int ImpulseReach(float sigma, int p, int n)
    {
        int down = AcrylicBackdropMath.DownsampleFactor(sigma, 1f);
        double[] cur = new double[n];
        cur[p] = 1.0;
        for (int d = 1; d < down; d <<= 1)
        {
            var next = new double[(cur.Length + 1) / 2];
            for (int j = 0; j < next.Length; j++) next[j] = 0.5 * (cur[2 * j] + (2 * j + 1 < cur.Length ? cur[2 * j + 1] : 0.0));
            cur = next;
        }
        Span<float> off = stackalloc float[8], wt = stackalloc float[8];
        int taps = AcrylicBackdropMath.BuildKernel(AcrylicBackdropMath.EffectiveTexelSigma(sigma, 1f, down), off, wt);
        double At(double[] a, double x)
        {
            int i0 = (int)Math.Floor(x);
            double f = x - i0, v0 = i0 >= 0 && i0 < a.Length ? a[i0] : 0.0, v1 = i0 + 1 >= 0 && i0 + 1 < a.Length ? a[i0 + 1] : 0.0;
            return v0 + (v1 - v0) * f;
        }
        var blurred = new double[cur.Length];
        for (int j = 0; j < cur.Length; j++)
        {
            double v = cur[j] * wt[0];
            for (int t = 1; t < taps; t++) v += (At(cur, j + off[t]) + At(cur, j - off[t])) * wt[t];
            blurred[j] = v;
        }
        int reach = 0;
        for (int x = 0; x < n; x++)
        {
            double c = (x + 0.5) / down - 0.5;
            if (Math.Abs(At(blurred, c)) > 1e-9) reach = Math.Max(reach, Math.Abs(x - p));
        }
        return reach;
    }

    [Theory]
    [InlineData(1.25f)]
    [InlineData(2.5f)]
    [InlineData(4f)]
    [InlineData(6.5f)]
    [InlineData(10f)]
    [InlineData(18f)]
    [InlineData(40f)]
    public void SupportRadius_BoundsTheWholePipelinesImpulseResponse(float sigma)
    {
        int bound = SelfBlurRegion.SupportRadius(sigma);
        int worst = 0;
        int down = AcrylicBackdropMath.DownsampleFactor(sigma, 1f);
        for (int p = 1024; p < 1024 + 2 * down; p++) worst = Math.Max(worst, ImpulseReach(sigma, p, 2048 + 4 * bound));
        Assert.True(worst <= bound, $"σ {sigma}: the pipeline reaches {worst} px, SupportRadius says {bound}");
        Assert.True(worst > SelfBlurRegion.TapRadius(sigma) - down, "the model is reaching (sanity)");
    }

    [Fact]
    public void TheRecordersTileRequest_CoversWhatTheCompositeBlurs()
    {
        // a scroll row: its recorded source is unbounded by the viewport; the composite clip is the rail
        foreach (float scale in new[] { 1f, 1.25f, 1.5f, 2f })
        foreach (float sigma in new[] { 1.25f, 6.5f, 10f })
        foreach (float rowY in new[] { -400f, 40f, 560f, 600f, 900f })
        {
            var sourceDip = new RectF(20f, rowY, 300f, 60f);
            var clipDip = new RectF(0f, 50f, 340f, 600f);
            RectF requestDip = SelfBlurRegion.SourceRequest(sourceDip, clipDip, sigma, scale);
            var item = BlurLeaf(0f, 0f, new RectF(clipDip.X * scale, clipDip.Y * scale, clipDip.W * scale, clipDip.H * scale),
                new RectF(sourceDip.X * scale, sourceDip.Y * scale, sourceDip.W * scale, sourceDip.H * scale), sigma);
            GroupCacheKey.BlurRegions(in item, 4000, 4000, out PixelRect src, out _);
            if (src.IsEmpty) continue;
            Assert.False(requestDip.IsEmpty, $"scale {scale} σ {sigma} row {rowY}: the composite blurs {src} but nothing was requested");
            Assert.True(MathF.Floor(requestDip.X * scale) <= src.Left && MathF.Floor(requestDip.Y * scale) <= src.Top
                && MathF.Ceiling(requestDip.Right * scale) >= src.Right && MathF.Ceiling(requestDip.Bottom * scale) >= src.Bottom,
                $"scale {scale} σ {sigma} row {rowY}: request {requestDip} (DIP) does not cover {src}");
        }
    }

    [Fact]
    public void ALeafBlursKey_IgnoresATileItsSourceDoesNotReach()
    {
        CompositeItem it = BlurLeaf(0f, 0f, new RectF(0f, 0f, 1000f, 400f), new RectF(100f, 100f, 400f, 100f));
        TilePlacement[] placed = [new(new TileKey(4, 0, 0), Surface: 3), new(new TileKey(4, 0, 2), Surface: 5)];   // y 0..512, 1024..1536
        GroupCacheKey.BlurRegions(in it, 2000, 2000, out PixelRect src, out PixelRect region);
        var a = new Serials { Of = [0u, 0u, 0u, 7u, 0u, 1u] };
        var b = new Serials { Of = [0u, 0u, 0u, 7u, 0u, 2u] };   // the far tile re-rastered
        var c = new Serials { Of = [0u, 0u, 0u, 8u, 0u, 1u] };   // the near one
        ulong ka = GroupCacheKey.LeafBlur(in it, placed, in src, in region, ref a);
        Assert.Equal(ka, GroupCacheKey.LeafBlur(in it, placed, in src, in region, ref b));
        Assert.NotEqual(ka, GroupCacheKey.LeafBlur(in it, placed, in src, in region, ref c));
    }

    [Theory]
    [InlineData(6, 4, false, 1, 0.5f)]    // left edge at 6 of a 1/4 surface: texel 1 straddles, half inside
    [InlineData(7, 4, false, 1, 0.25f)]
    [InlineData(8, 4, false, 2, 1f)]      // on the grid
    [InlineData(1770, 4, true, 443, 0.5f)] // a 1770-px-wide source: the last texel holds 2 of its 4 columns
    [InlineData(1771, 4, true, 443, 0.75f)]
    [InlineData(1768, 4, true, 442, 1f)]
    [InlineData(-3, 4, false, -1, 0.75f)]
    public void ALowResEdgeOffTheGrid_KeepsTheCoveredShareOfItsTexel(int rel, int down, bool far, int texel, float coverage)
    {
        SelfBlurRegion.LowResEdge(rel, down, far, out int t, out float c);
        Assert.Equal(texel, t);
        Assert.Equal(coverage, c, 5);
    }
}
