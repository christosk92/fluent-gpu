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
}
