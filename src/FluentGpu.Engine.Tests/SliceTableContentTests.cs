using System;
using FluentGpu.Foundation;
using FluentGpu.Render.Tiles;
using Xunit;

namespace FluentGpu.Engine.Tests;

/// <summary>Content-derived tile validity (gpu-renderer.md §13.1c, issue #1): <see cref="SliceTable.Request{TContent}"/>
/// folds a resident, valid tile's content want only when its segment's key moved, and invalidates the tile when its pixels
/// were rastered for different content — PrimCount when the number of ops it draws changed, else Content — with no damage
/// rect anywhere. An unchanged key (a kept slice on a composite-only turn) folds nothing and rasters nothing.</summary>
public sealed class SliceTableContentTests
{
    private static readonly SliceFrame Grid = new(0, 0, 0f, 0f, 1f);
    private static readonly RectF Viewport = new(0f, 0f, 1024f, 1024f);

    /// <summary>A segment's content as a table sees it: a key and, per tile row, a (hash, op count).</summary>
    private struct FakeContent : ITileContent
    {
        public ulong KeyValue;
        public Func<int, (ulong Hash, int Ops)> Of;
        public int Folds;

        public ulong Key => KeyValue;

        public ulong Want(in RectF tilePx, out int ops, out RectF paint)
        {
            Folds++;
            paint = new RectF(0f, 0f, tilePx.W, tilePx.H);
            var (h, o) = Of((int)(tilePx.Y / TileGrid.H));
            ops = o;
            return h;
        }
    }

    /// <summary>One turn over a two-tile slice with <paramref name="content"/>: request (the content check), resolve, the
    /// placed surfaces' wants (the recorder's post-resolve pass), mark every scheduled raster done.</summary>
    private static (int Scheduled, int Folds, TileRaster[] Rasters) Turn(SliceTable t, int frame, ulong key,
        Func<int, (ulong, int)> of)
    {
        t.BeginFrame(frame);
        int id = t.OpenSlice(5, 1, SliceKind.Scroll, in Grid, new RectF(0f, 0f, 1024f, 1024f));
        Span<TileKey> need = [new TileKey(id, 0, 0), new TileKey(id, 0, 1)];
        var content = new FakeContent { KeyValue = key, Of = of };
        t.Request(id, in Viewport, 0.0, 1024.0, false, need, default, ref content);
        var r = new TileRaster[8];
        t.Resolve(long.MaxValue, r, out int n);
        Span<TilePlacement> pl = stackalloc TilePlacement[8];
        int np = t.CollectPlacements(id, pl);
        for (int i = 0; i < np; i++)
            if (t.SurfaceWantKey(pl[i].Surface) != key)
            {
                var (h, o) = of(pl[i].Key.Ty);
                t.SetSurfaceWant(pl[i].Surface, key, h, o);
            }
        t.CountExposedMissing();
        for (int i = 0; i < n; i++) t.MarkRastered(r[i].Key);
        t.EndFrame();
        return (n, content.Folds, r[..n]);
    }

    [Fact]
    public void AnUnchangedKey_FoldsNothing_AndRastersNothing()
    {
        var t = new SliceTable(4, 8, 8);
        Assert.Equal(2, Turn(t, 1, key: 7, ty => (100UL + (ulong)ty, 3)).Scheduled);
        var (scheduled, folds, _) = Turn(t, 2, key: 7, ty => (100UL + (ulong)ty, 3));
        Assert.Equal(0, scheduled);
        Assert.Equal(0, folds);
        Assert.Equal(0, t.ContentCheckedThisFrame);
    }

    [Fact]
    public void ReWalkedBytesThatChangedATile_ReRasterExactlyThatTile_AsContent()
    {
        var t = new SliceTable(4, 8, 8);
        Turn(t, 1, key: 7, ty => (100UL + (ulong)ty, 3));
        // The segment re-recorded (new key): row 1's bytes changed (a baked alpha), row 0's did not — no damage anywhere.
        var (scheduled, folds, rasters) = Turn(t, 2, key: 8, ty => (ty == 1 ? 999UL : 100UL, 3));
        Assert.Equal(2, folds);
        Assert.Equal(1, scheduled);
        Assert.Equal((short)1, rasters[0].Key.Ty);
        Assert.Equal(InvalidationReason.Content, rasters[0].Reason);
        Assert.Equal(0, t.StaleTiles);
    }

    [Fact]
    public void AChangedOpCount_IsPrimCount()
    {
        var t = new SliceTable(4, 8, 8);
        Turn(t, 1, key: 7, ty => (100UL + (ulong)ty, 3));
        var (scheduled, _, rasters) = Turn(t, 2, key: 8, ty => ty == 0 ? (555UL, 4) : (101UL, 3));   // a row entered tile row 0
        Assert.Equal(1, scheduled);
        Assert.Equal((short)0, rasters[0].Key.Ty);
        Assert.Equal(InvalidationReason.PrimCount, rasters[0].Reason);
    }

    [Fact]
    public void ARestoredWant_UnderANewKey_NeedsNoRaster()
    {
        var t = new SliceTable(4, 8, 8);
        Turn(t, 1, key: 7, ty => (100UL + (ulong)ty, 3));
        Turn(t, 2, key: 8, ty => (ty == 1 ? 999UL : 100UL, 3));                 // row 1 re-rastered for 999
        var (scheduled, folds, _) = Turn(t, 3, key: 9, ty => (ty == 1 ? 999UL : 100UL, 3));   // re-walked, same bytes
        Assert.Equal(2, folds);
        Assert.Equal(0, scheduled);
    }
}
