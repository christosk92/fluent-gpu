using System;
using FluentGpu.Foundation;
using FluentGpu.Render.Evidence;
using FluentGpu.Render.Tiles;
using FluentGpu.Rhi;
using Xunit;

namespace FluentGpu.Engine.Tests;

/// <summary>"What composited at window pixel (x, y)" over hand-built ledger frames (docs/plans/evidence-diagnostics-
/// implementation.md §A.3): painter order, clip exclusion, the tile lookup through the item's placement, the stale flag,
/// and feather values that ARE the shader port's (<see cref="EdgeFeatherMask.Evaluate(in EdgeFeather, float, float)"/>).</summary>
public sealed class PixelQueryTests
{
    // A list item at window (100, 50) px with a 24-px top feather at y = 56 — the pinned facet header shape (#3a).
    private static readonly EdgeFeather TopFeather = new(new RectF(0f, 56f, 800f, 600f), 0f, 24f, 0f, 0f, default);

    private static ItemRecord Tiles(int slice, int dx, int dy, EdgeFeather feather = default, float alpha = 1f,
        (short X, short Y, short W, short H)? clip = null, byte inherited = 0)
        => new()
        {
            NodeIndex = 10 + slice, Gen = 1, SliceId = slice, Kind = (byte)CompositeKind.Tiles, Inherited = inherited,
            AlphaQ8 = CompositeLedger.Q8(alpha), TransDx = dx, TransDy = dy, Feather1 = feather,
            Flags = clip is null ? (byte)0 : ItemRecordFlags.ClipBounded,
            ClipX = clip?.X ?? 0, ClipY = clip?.Y ?? 0, ClipW = clip?.W ?? 0, ClipH = clip?.H ?? 0,
            StickyTopPx = short.MinValue,
        };

    private static PlacementRecord Tile(int slice, short tx, short ty, int rasterFrame, ulong have, ulong want, bool stale)
        => new() { SliceId = slice, Tx = tx, Ty = ty, W = TileGrid.W, H = TileGrid.H, RasterFrame = rasterFrame,
                   RasterHash = have, WantHash = want, Stale = stale ? (byte)1 : (byte)0 };

    private static CompositeFrameView Frame(ItemRecord[] items, PlacementRecord[] placements)
        => new(new CompositeFrameHeader { Frame = 9, Items = items.Length, Placements = placements.Length }, items, placements);

    [Fact]
    public void ReturnsTheItemsUnderThePixelInPainterOrder_WithTheirTiles()
    {
        ItemRecord[] items = [Tiles(1, 0, 0), Tiles(2, 100, 50, TopFeather, inherited: 1)];
        PlacementRecord[] placed = [Tile(1, 0, 0, 3, 0xA, 0xA, false), Tile(2, 0, 0, 7, 0xB, 0xC, true)];
        var hits = new PixelHit[8];

        int n = PixelQuery.Query(Frame(items, placed), 110, 60, hits);

        Assert.Equal(2, n);
        Assert.Equal(0, hits[0].ItemIndex);   // bottom first
        Assert.Equal(1, hits[1].ItemIndex);
        Assert.Equal(new TileKey(2, 0, 0), hits[1].Tile);
        Assert.Equal(7, hits[1].TileRasterFrame);
        Assert.True(hits[1].TileStale);
        Assert.Equal(0xBUL, hits[1].TileRasterHash);
        Assert.Equal(0xCUL, hits[1].TileWantHash);
        Assert.False(hits[0].TileStale);
    }

    [Fact]
    public void FeatherValues_AreTheShaderPortsAtThePixelCentre()
    {
        ItemRecord[] items = [Tiles(2, 100, 50, TopFeather)];
        PlacementRecord[] placed = [Tile(2, 0, 0, 7, 1, 1, false)];
        var hits = new PixelHit[4];
        foreach (int y in new[] { 56, 57, 60, 68, 79, 80, 200 })
        {
            int n = PixelQuery.Query(Frame(items, placed), 300, y, hits);
            Assert.Equal(1, n);
            Assert.Equal(EdgeFeatherMask.Evaluate(TopFeather, 300.5f, y + 0.5f), hits[0].Feather1);
            Assert.Equal(1f, hits[0].Feather2);
        }
        PixelQuery.Query(Frame(items, placed), 300, 60, hits);
        Assert.InRange(hits[0].Feather1, 0.05f, 0.35f);   // 4.5 px into a 24-px smoothstep band
        PixelQuery.Query(Frame(items, placed), 300, 200, hits);
        Assert.Equal(1f, hits[0].Feather1);
    }

    [Fact]
    public void AnItemWhoseClipExcludesThePixel_IsSkipped_AndAnEmptyBoundedClipShowsNothing()
    {
        ItemRecord[] items =
        [
            Tiles(1, 0, 0, clip: (0, 0, 100, 100)),
            Tiles(2, 0, 0, clip: (-1, -1, 0, 0)),   // the "bounded but empty" encoding
            Tiles(3, 0, 0),                          // unbounded
        ];
        PlacementRecord[] placed = [Tile(1, 0, 0, 1, 1, 1, false), Tile(2, 0, 0, 1, 1, 1, false), Tile(3, 0, 0, 1, 1, 1, false)];
        var hits = new PixelHit[4];

        int n = PixelQuery.Query(Frame(items, placed), 150, 20, hits);

        Assert.Equal(1, n);
        Assert.Equal(3, hits[0].Item.SliceId);
    }

    [Fact]
    public void ATilesItemWithNoResidentTileUnderThePixel_PaintedNothingThere()
    {
        ItemRecord[] items = [Tiles(4, 0, 0)];
        PlacementRecord[] placed = [Tile(4, 0, 1, 1, 1, 1, false)];   // only the tile BELOW y = 512
        var hits = new PixelHit[4];
        Assert.Equal(0, PixelQuery.Query(Frame(items, placed), 10, 10, hits));
        Assert.Equal(1, PixelQuery.Query(Frame(items, placed), 10, 600, hits));
        Assert.Equal(new TileKey(4, 0, 1), hits[0].Tile);
    }

    [Fact]
    public void ThePlacementIsLookedUpInSliceSpace_ThroughTheItemsTranslation()
    {
        ItemRecord[] items = [Tiles(5, -2048, 300)];   // a scrolled slice: window x = slice x − 2048
        PlacementRecord[] placed = [Tile(5, 2, 0, 1, 1, 1, false)];   // slice-space x ∈ [2048, 3072)
        var hits = new PixelHit[2];
        Assert.Equal(1, PixelQuery.Query(Frame(items, placed), 5, 310, hits));
        Assert.Equal(new TileKey(5, 2, 0), hits[0].Tile);
        Assert.Equal(0, PixelQuery.Query(Frame(items, placed), 5, 299, hits));   // above the item's origin: tile row −1
    }

    [Fact]
    public void AGroupItem_IsListedWhereItsClipContainsThePixel_WithItsAlpha()
    {
        var group = new ItemRecord
        {
            SliceId = -1, Kind = (byte)CompositeKind.Group, AlphaQ8 = CompositeLedger.Q8(0.5f), GroupCount = 1,
            Flags = ItemRecordFlags.ClipBounded, ClipX = 0, ClipY = 0, ClipW = 400, ClipH = 400, StickyTopPx = short.MinValue,
        };
        ItemRecord[] items = [group, Tiles(6, 0, 0)];
        PlacementRecord[] placed = [Tile(6, 0, 0, 1, 1, 1, false)];
        var hits = new PixelHit[4];
        int n = PixelQuery.Query(Frame(items, placed), 20, 20, hits);
        Assert.Equal(2, n);
        Assert.Equal(CompositeKind.Group, (CompositeKind)hits[0].Item.Kind);
        Assert.Equal(128f / 255f, hits[0].Alpha);
        Assert.Equal(0.5f * 1f * 1f, hits[0].Coverage, 2);
    }

    /// <summary>The feather quad split (FeatherQuadSplit): a pixel inside the feather's unit interior was composited by the
    /// feather-free interior piece (feather exactly 1); one in the band by a feathered strip.</summary>
    [Fact]
    public void ThePixelNamesTheFeatherQuadPieceThatDrewIt()
    {
        var item = new CompositeItem(2, CompositeKind.Tiles, Affine2D.Translation(100f, 50f), 1f, default, default, TopFeather, 0f, default, 0);
        ItemRecord rec = CompositeLedger.Record(in item, 12, 1, short.MinValue, 0);
        Assert.Equal(80f, rec.InteriorT);   // ceil(56 + 24)
        PlacementRecord[] placed = [Tile(2, 0, 0, 7, 1, 1, false)];
        var hits = new PixelHit[2];
        PixelQuery.Query(Frame([rec], placed), 300, 60, hits);
        Assert.True(hits[0].InFeatherBand);
        PixelQuery.Query(Frame([rec], placed), 300, 200, hits);
        Assert.False(hits[0].InFeatherBand);
        Assert.Equal(1f, hits[0].Feather1);
    }

    [Fact]
    public void Query_AllocatesNothing()
    {
        ItemRecord[] items = [Tiles(1, 0, 0, TopFeather), Tiles(2, 100, 50, TopFeather)];
        PlacementRecord[] placed = [Tile(1, 0, 0, 3, 1, 1, false), Tile(2, 0, 0, 7, 1, 1, false)];
        var hits = new PixelHit[8];
        PixelQuery.Query(Frame(items, placed), 110, 60, hits);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1000; i++) PixelQuery.Query(Frame(items, placed), 110, 60 + (i & 63), hits);
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }
}

/// <summary>The composite item record's publication (§A.2): a committed frame is copied whole; the work frame being filled
/// is never visible; items past the fixed capacity are counted, not stored.</summary>
public sealed class CompositeLedgerTests
{
    [Fact]
    public void CopyLatest_SeesOnlyCommittedFrames()
    {
        var led = new CompositeLedger();
        var copy = new CompositeFrameCopy();
        Assert.False(led.CopyLatest(copy));

        led.Begin(frame: 4, publishSeq: 40, qpc: 1, scale: 1.5f, widthPx: 800, heightPx: 600);
        led.AddItem(new ItemRecord { SliceId = 1 });
        led.AddPlacement(new PlacementRecord { SliceId = 1 });
        led.Commit(staleTiles: 2, exposedMissing: 0);

        led.Begin(frame: 5, publishSeq: 41, qpc: 2, scale: 1.5f, widthPx: 800, heightPx: 600);
        led.AddItem(new ItemRecord { SliceId = 9 });   // in flight — not committed

        Assert.True(led.CopyLatest(copy));
        Assert.Equal(4, copy.Header.Frame);
        Assert.Equal(40UL, copy.Header.PublishSeq);
        Assert.Equal(2, copy.Header.StaleTiles);
        Assert.Equal(1, copy.View.Items.Length);
        Assert.Equal(1, copy.View.Items[0].SliceId);
        Assert.Equal(1, copy.View.Placements.Length);
    }

    [Fact]
    public void ItemsPastTheCapacity_AreCountedNotStored()
    {
        var led = new CompositeLedger();
        led.Begin(1, 1, 0, 1f, 10, 10);
        for (int i = 0; i < CompositeLedger.ItemsPerFrame + 5; i++) led.AddItem(new ItemRecord { SliceId = i });
        led.Commit(0, 0);
        var copy = new CompositeFrameCopy();
        led.CopyLatest(copy);
        Assert.Equal(CompositeLedger.ItemsPerFrame, copy.View.Items.Length);
        Assert.Equal(5, copy.Header.DroppedItems);
    }

    [Fact]
    public void Record_DerivesClipLayerAndStickyBits()
    {
        var it = new CompositeItem(3, CompositeKind.Region, Affine2D.Translation(64f, -12f), 0.25f, new RectF(0f, 56f, 800f, 400f),
            default, default, 0f, default, 0, HasLayer: 1, Inherited: 1);
        ItemRecord r = CompositeLedger.Record(in it, nodeIndex: 77, gen: 2, stickyTopPx: 84, flags: ItemRecordFlags.GroupHit);
        Assert.Equal(77, r.NodeIndex);
        Assert.Equal(64, r.TransDx);
        Assert.Equal(-12, r.TransDy);
        Assert.Equal(64, r.AlphaQ8);
        Assert.Equal(56, r.ClipY);
        Assert.Equal(84, r.StickyTopPx);
        byte want = ItemRecordFlags.GroupHit | ItemRecordFlags.ClipBounded | ItemRecordFlags.HasLayer | ItemRecordFlags.StickyEngaged;
        Assert.Equal(want, r.Flags);

        var unbounded = it with { Clip = default, HasLayer = 0 };
        ItemRecord u = CompositeLedger.Record(in unbounded, 1, 1, short.MinValue, 0);
        Assert.Equal(0, u.Flags);
    }
}
