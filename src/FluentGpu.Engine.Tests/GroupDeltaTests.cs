using System;
using FluentGpu.Foundation;
using FluentGpu.Render;
using FluentGpu.Render.Tiles;
using FluentGpu.Rhi;
using Xunit;

namespace FluentGpu.Engine.Tests;

/// <summary>The unblurred group-surface repair (GroupDelta): a group whose content changed only inside one enclosed item
/// repairs exactly that item's painted rect; any change to how an item is drawn changes the shape (no repair); too much
/// change, or entries that cannot be compared, fall back to the full render.</summary>
public sealed class GroupDeltaTests
{
    private struct Serials : ITileSerials
    {
        public uint[] Of;
        public readonly uint Serial(int surface) => (uint)surface < (uint)Of.Length ? Of[surface] : 0u;
    }

    private static readonly PixelRect Region = new(100, 100, 600, 900);

    private static CompositeItem Group(int count) => new(-1, CompositeKind.Group, Affine2D.Identity, 1f, default, default, default, 0f,
        default, 0, GroupCount: count);

    private static CompositeItem Tiles(int slice, float dx, float dy, float alpha = 1f, RectF clip = default)
        => new(slice, CompositeKind.Tiles, Affine2D.Translation(dx, dy), alpha, clip, default, default, 0f, default, 0);

    private static readonly FrameInfo Info = new(new Size2(1200, 1000), 1f, default);

    private static ulong Build(CompositeItem[] items, TilePlacement[] placed, uint[] serials, ref GroupEntry[] entries, out int count,
        ulong[]? keys = null, PixelRect[]? regions = null, int[]? surfaces = null)
    {
        var frame = new CompositeFrame(in Info, default, default, default, placed, items, default);
        var s = new Serials { Of = serials };
        return GroupDelta.Build(in frame, 0, in Region, ref s, keys ?? new ulong[items.Length], regions ?? new PixelRect[items.Length],
            surfaces ?? new int[items.Length], fresh: 0xF2E5UL, ref entries, out count);
    }

    // two lines in one segment's two tiles (the second tile paints a 400×100 line at its top)
    private static readonly TilePlacement[] Placed =
    [
        new(new TileKey(7, 0, 0), Surface: 1, W: 1024, H: 512, PaintX0: 20, PaintY0: 40, PaintX1: 420, PaintY1: 140),
        new(new TileKey(7, 0, 1), Surface: 2, W: 1024, H: 512, PaintX0: 20, PaintY0: 0, PaintX1: 420, PaintY1: 100),
    ];

    [Fact]
    public void AReRasteredTile_DirtiesOnlyItsPaintedRect_InTheGroupSurface()
    {
        CompositeItem[] items = [Group(1), Tiles(7, 100f, 100f, clip: new RectF(100f, 100f, 500f, 800f))];
        GroupEntry[] prev = new GroupEntry[4], cur = new GroupEntry[4];
        ulong a = Build(items, Placed, [0u, 5u, 9u], ref prev, out int na);
        ulong b = Build(items, Placed, [0u, 5u, 10u], ref cur, out int nb);   // tile (0,1) re-rastered
        Assert.Equal(a, b);
        Span<PixelRect> dirty = stackalloc PixelRect[GroupDelta.MaxDirtyRects];
        int n = GroupDelta.Diff(prev.AsSpan(0, na), cur.AsSpan(0, nb), 500L * 800, dirty);
        Assert.Equal(1, n);
        // tile (0,1) sits at y 512 of the item (window y 612 → surface y 512); its paint rect 20..420 × 0..100
        Assert.Equal(new PixelRect(20, 512, 420, 612), dirty[0]);
    }

    [Fact]
    public void NothingChanged_IsZeroRects_AndADrawParameterChange_IsANewShape()
    {
        CompositeItem[] items = [Group(1), Tiles(7, 100f, 100f)];
        GroupEntry[] prev = new GroupEntry[4], cur = new GroupEntry[4];
        ulong a = Build(items, Placed, [0u, 5u, 9u], ref prev, out int na);
        Build(items, Placed, [0u, 5u, 9u], ref cur, out int nb);
        Assert.Equal(0, GroupDelta.Diff(prev.AsSpan(0, na), cur.AsSpan(0, nb), 500L * 800, stackalloc PixelRect[8]));

        CompositeItem[] faded = [Group(1), Tiles(7, 100f, 100f, alpha: 0.5f)];
        Assert.NotEqual(a, Build(faded, Placed, [0u, 5u, 9u], ref cur, out _));
        CompositeItem[] moved = [Group(1), Tiles(7, 101f, 100f)];
        Assert.NotEqual(a, Build(moved, Placed, [0u, 5u, 9u], ref cur, out _));
    }

    [Fact]
    public void TooMuchChanged_FallsBackToTheFullRender()
    {
        CompositeItem[] items = [Group(1), Tiles(7, 100f, 100f)];
        GroupEntry[] prev = new GroupEntry[4], cur = new GroupEntry[4];
        Build(items, Placed, [0u, 5u, 9u], ref prev, out int na);
        Build(items, Placed, [0u, 6u, 10u], ref cur, out int nb);
        // both lines changed: 2 × 400×100 of a tiny 300×300 region is past MaxDirtyShare
        Assert.Equal(-1, GroupDelta.Diff(prev.AsSpan(0, na), cur.AsSpan(0, nb), 300L * 300, stackalloc PixelRect[8]));
        // a different entry count cannot be compared
        Assert.Equal(-1, GroupDelta.Diff(prev.AsSpan(0, na), cur.AsSpan(0, 1), 500L * 800, stackalloc PixelRect[8]));
    }

    [Fact]
    public void APreparedSurface_SignsByItsKey_AndOneThatDrawsNothing_DirtiesWhereItDrewBefore()
    {
        CompositeItem blurred = Tiles(9, 300f, 300f) with { Kind = CompositeKind.Region, BlurSigma = 4f };
        CompositeItem[] items = [Group(1), blurred];
        PixelRect[] regions = [default, new PixelRect(300, 300, 500, 400)];
        GroupEntry[] prev = new GroupEntry[4], cur = new GroupEntry[4];
        ulong a = Build(items, [], [], ref prev, out int na, keys: [0, 0xAAAA], regions: regions, surfaces: [-1, 3]);
        ulong b = Build(items, [], [], ref cur, out int nb, keys: [0, 0xBBBB], regions: regions, surfaces: [-1, 3]);
        Assert.Equal(a, b);
        Span<PixelRect> dirty = stackalloc PixelRect[8];
        Assert.Equal(1, GroupDelta.Diff(prev.AsSpan(0, na), cur.AsSpan(0, nb), 500L * 800, dirty));
        Assert.Equal(new PixelRect(200, 200, 400, 300), dirty[0]);

        // scrolled out of its viewport this turn: no surface — the rect it drew last turn is repaired (cleared)
        Build(items, [], [], ref cur, out nb, keys: [0, 0], regions: regions, surfaces: [-1, -1]);
        Assert.Equal(1, GroupDelta.Diff(prev.AsSpan(0, na), cur.AsSpan(0, nb), 500L * 800, dirty));
        Assert.Equal(new PixelRect(200, 200, 400, 300), dirty[0]);
    }

    [Fact]
    public void OverlappingDirtyRects_AreMerged()
    {
        GroupEntry[] prev = [new() { Rect = new PixelRect(0, 0, 100, 100), Sig = 1 }, new() { Rect = new PixelRect(50, 50, 150, 150), Sig = 2 }];
        GroupEntry[] cur = [new() { Rect = new PixelRect(0, 0, 100, 100), Sig = 3 }, new() { Rect = new PixelRect(50, 50, 150, 150), Sig = 4 }];
        Span<PixelRect> dirty = stackalloc PixelRect[8];
        Assert.Equal(1, GroupDelta.Diff(prev, cur, 1000L * 1000, dirty));
        Assert.Equal(new PixelRect(0, 0, 150, 150), dirty[0]);
    }
}
