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
        => Describe(items, placed, serials, ref entries, out count, out _, keys, regions, surfaces);

    private static ulong Describe(CompositeItem[] items, TilePlacement[] placed, uint[] serials, ref GroupEntry[] entries, out int count,
        out ulong key, ulong[]? keys = null, PixelRect[]? regions = null, int[]? surfaces = null)
    {
        var frame = new CompositeFrame(in Info, default, default, default, placed, items, default);
        var s = new Serials { Of = serials };
        return GroupDelta.Describe(in frame, 0, in Region, ref s, keys ?? new ulong[items.Length], regions ?? new PixelRect[items.Length],
            surfaces ?? new int[items.Length], ref entries, out count, out key);
    }

    private static ulong Key(CompositeItem[] items, TilePlacement[] placed, uint[] serials,
        ulong[]? keys = null, PixelRect[]? regions = null, int[]? surfaces = null)
    {
        GroupEntry[] e = new GroupEntry[4];
        Describe(items, placed, serials, ref e, out _, out ulong key, keys, regions, surfaces);
        return key;
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
    public void ATileReRasteredOutsideTheClip_KeepsTheContentKey_AndDiffsAsNoChange()
    {
        // the clip ends at window y 300: tile (0,1) (window y 612..712) is placed but scissored away entirely, so its
        // re-raster cannot change a pixel of the group surface. The key used to sign it anyway: the retained surface missed,
        // the zero diff refused the repair and the whole group re-rendered (and a Debug build failed an assert on it).
        CompositeItem[] items = [Group(1), Tiles(7, 100f, 100f, clip: new RectF(100f, 100f, 500f, 300f))];
        GroupEntry[] prev = new GroupEntry[4], cur = new GroupEntry[4];
        Build(items, Placed, [0u, 5u, 9u], ref prev, out int na);
        Build(items, Placed, [0u, 5u, 10u], ref cur, out int nb);   // tile (0,1) re-rastered
        Assert.Equal(0, GroupDelta.Diff(prev.AsSpan(0, na), cur.AsSpan(0, nb), 500L * 800, stackalloc PixelRect[8]));
        Assert.Equal(Key(items, Placed, [0u, 5u, 9u]), Key(items, Placed, [0u, 5u, 10u]));

        // tile (0,0) paints inside the clip: its re-raster moves both
        Assert.NotEqual(Key(items, Placed, [0u, 5u, 9u]), Key(items, Placed, [0u, 6u, 9u]));
        Build(items, Placed, [0u, 6u, 9u], ref cur, out nb);
        Assert.Equal(1, GroupDelta.Diff(prev.AsSpan(0, na), cur.AsSpan(0, nb), 500L * 800, stackalloc PixelRect[8]));
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

    [Fact]
    public void ANestedGroup_IsOneEntryByItsKey_AndItsMembersAreSkipped()
    {
        // outer group: [ nested group (2 tiles of slice 7), tiles of slice 8 ]
        CompositeItem[] items = [Group(4), Group(1), Tiles(7, 100f, 100f), Tiles(8, 100f, 100f)];
        TilePlacement[] placed =
        [
            new(new TileKey(7, 0, 0), Surface: 1, W: 1024, H: 512, PaintX0: 0, PaintY0: 0, PaintX1: 200, PaintY1: 50),
            new(new TileKey(8, 0, 0), Surface: 2, W: 1024, H: 512, PaintX0: 0, PaintY0: 100, PaintX1: 200, PaintY1: 150),
        ];
        items[0] = items[0] with { GroupCount = 3 };
        PixelRect[] regions = [default, new PixelRect(100, 100, 400, 200), default, default];
        GroupEntry[] prev = new GroupEntry[4], cur = new GroupEntry[4];
        Build(items, placed, [0u, 5u, 9u], ref prev, out int na, keys: [0, 0x1111, 0, 0], regions: regions, surfaces: [-1, 6, -1, -1]);
        Assert.Equal(2, na);   // the nested group's surface + slice 8's tile; slice 7's tile is inside the nested key
        // slice 7 re-rastered but the nested key unchanged (a retained nested surface): nothing to repair
        Build(items, placed, [0u, 6u, 9u], ref cur, out int nb, keys: [0, 0x1111, 0, 0], regions: regions, surfaces: [-1, 6, -1, -1]);
        Assert.Equal(0, GroupDelta.Diff(prev.AsSpan(0, na), cur.AsSpan(0, nb), 500L * 800, stackalloc PixelRect[8]));
        // the nested key moved: its region (relative to the outer one) is repaired
        Build(items, placed, [0u, 6u, 9u], ref cur, out nb, keys: [0, 0x2222, 0, 0], regions: regions, surfaces: [-1, 6, -1, -1]);
        Span<PixelRect> dirty = stackalloc PixelRect[8];
        Assert.Equal(1, GroupDelta.Diff(prev.AsSpan(0, na), cur.AsSpan(0, nb), 500L * 800, dirty));
        Assert.Equal(new PixelRect(0, 0, 300, 100), dirty[0]);
    }

    [Fact]
    public void APreparedSurfaceWithNoKey_MakesTheGroupUncacheable_OnlyWhereItPaints()
    {
        CompositeItem blurred = Tiles(9, 300f, 300f) with { Kind = CompositeKind.Region, BlurSigma = 4f };
        CompositeItem[] items = [Group(1), blurred];
        // re-drawn from scratch (key 0) inside the region: nothing can tell its pixels apart from last turn's
        Assert.Equal(0UL, Key(items, [], [], keys: [0, 0], regions: [default, new PixelRect(300, 300, 500, 400)], surfaces: [-1, 3]));
        // the same surface prepared wholly outside the region paints nothing into the group: the group stays cacheable
        Assert.NotEqual(0UL, Key(items, [], [], keys: [0, 0], regions: [default, new PixelRect(700, 950, 800, 990)], surfaces: [-1, 3]));
    }

    [Fact]
    public void ANestedSurfaceOutsideItsScissor_MovesNeitherTheKeyNorTheDiff()
    {
        // the nested group sits below the outer item's clip (window y 300): its content key changing cannot reach a pixel
        CompositeItem nested = Group(0) with { Clip = new RectF(100f, 100f, 500f, 200f) };
        CompositeItem[] items = [Group(1), nested];
        PixelRect[] regions = [default, new PixelRect(100, 400, 400, 500)];
        GroupEntry[] prev = new GroupEntry[4], cur = new GroupEntry[4];
        Describe(items, [], [], ref prev, out int na, out ulong k0, keys: [0, 0x1111], regions: regions, surfaces: [-1, 6]);
        Describe(items, [], [], ref cur, out int nb, out ulong k1, keys: [0, 0x2222], regions: regions, surfaces: [-1, 6]);
        Assert.Equal(0, GroupDelta.Diff(prev.AsSpan(0, na), cur.AsSpan(0, nb), 500L * 800, stackalloc PixelRect[8]));
        Assert.Equal(k0, k1);
        // moved into the clip, the same key change is a repair of exactly its rect, and a new key
        PixelRect[] inside = [default, new PixelRect(100, 150, 400, 250)];
        Describe(items, [], [], ref prev, out na, out k0, keys: [0, 0x1111], regions: inside, surfaces: [-1, 6]);
        Describe(items, [], [], ref cur, out nb, out k1, keys: [0, 0x2222], regions: inside, surfaces: [-1, 6]);
        Span<PixelRect> dirty = stackalloc PixelRect[8];
        Assert.Equal(1, GroupDelta.Diff(prev.AsSpan(0, na), cur.AsSpan(0, nb), 500L * 800, dirty));
        Assert.Equal(new PixelRect(0, 50, 300, 150), dirty[0]);
        Assert.NotEqual(k0, k1);
    }

    [Fact]
    public void AGroupMovedUnderAFixedViewportClip_KeepsItsKey()
    {
        // a shelf scrolled rigidly by 10 px under a viewport whose clip covers the whole region: the key is relative
        CompositeItem[] at0 = [Group(1), Tiles(7, 100f, 100f, clip: new RectF(0f, 0f, 1200f, 1000f))];
        CompositeItem[] at10 = [Group(1), Tiles(7, 100f, 100f, clip: new RectF(0f, -10f, 1200f, 1000f))];
        Assert.Equal(Key(at0, Placed, [0u, 5u, 9u]), Key(at10, Placed, [0u, 5u, 9u]));
    }

    [Fact]
    public void ATilesEntry_IsCutByItsItemsScissor()
    {
        // the item's clip ends at window y 150: the painted 400×100 line at surface y 40..140 is cut at 50 (window 150 − 100)
        CompositeItem[] items = [Group(1), Tiles(7, 100f, 100f, clip: new RectF(100f, 100f, 500f, 50f))];
        GroupEntry[] e = new GroupEntry[4];
        Build(items, Placed, [0u, 5u, 9u], ref e, out int n);
        Assert.Equal(2, n);
        Assert.Equal(new PixelRect(20, 40, 420, 50), e[0].Rect);
        Assert.True(e[1].Rect.IsEmpty);   // the second tile is wholly below the clip
    }

    [Fact]
    public void TwoGroupsOfTheSameLayout_HaveDifferentShapes()
    {
        CompositeItem[] a = [Group(1), Tiles(7, 100f, 100f)];
        CompositeItem[] b = [Group(1), Tiles(11, 100f, 100f)];
        GroupEntry[] e = new GroupEntry[4];
        Assert.NotEqual(Build(a, Placed, [0u, 5u, 9u], ref e, out _), Build(b, [], [0u, 5u, 9u], ref e, out _));
        // made only of nested groups (slice −1): the group's identity still separates them
        CompositeItem[] c = [Group(2), Group(0), Group(0)];
        CompositeItem[] d = [Group(3), Group(0), Group(0), Group(0)];
        Assert.NotEqual(Build(c, [], [], ref e, out _, keys: [0, 1, 1], regions: new PixelRect[3], surfaces: [-1, 1, 1]),
            Build(d, [], [], ref e, out _, keys: [0, 1, 1, 1], regions: new PixelRect[4], surfaces: [-1, 1, 1, 1]));
    }

    [Fact]
    public void NoChangeFound_MeansTheContentKeyIsTheSame()
    {
        foreach (float clipH in new[] { 800f, 200f, 50f, 0.5f })
        foreach (uint[] serials in new[] { new uint[] { 0u, 5u, 9u }, new uint[] { 0u, 5u, 10u }, new uint[] { 0u, 6u, 9u }, new uint[] { 0u, 6u, 10u } })
        {
            CompositeItem[] items = [Group(1), Tiles(7, 100f, 100f, clip: new RectF(100f, 100f, 500f, clipH))];
            var frame = new CompositeFrame(in Info, default, default, default, Placed, items, default);
            GroupEntry[] prev = new GroupEntry[4], cur = new GroupEntry[4];
            var s0 = new Serials { Of = [0u, 5u, 9u] };
            var s1 = new Serials { Of = serials };
            ulong shape0 = GroupDelta.Describe(in frame, 0, in Region, ref s0, new ulong[2], new PixelRect[2], new int[2], ref prev, out int na, out ulong k0);
            ulong shape1 = GroupDelta.Describe(in frame, 0, in Region, ref s1, new ulong[2], new PixelRect[2], new int[2], ref cur, out int nb, out ulong k1);
            Assert.Equal(shape0, shape1);
            int n = GroupDelta.Diff(prev.AsSpan(0, na), cur.AsSpan(0, nb), 500L * 800, stackalloc PixelRect[8]);
            Assert.True(k0 != 0UL && k1 != 0UL);
            Assert.Equal(n == 0, k0 == k1);
        }
    }
}
