using System;
using FluentGpu.Foundation;
using FluentGpu.Render.Evidence;
using FluentGpu.Render.Tiles;
using Xunit;

namespace FluentGpu.Engine.Tests;

/// <summary>The table half of the raster ledger and the stale-tile invariant (docs/plans/evidence-diagnostics-
/// implementation.md §A.1): a completed raster records the want it was rastered for; a VALID tile used this turn and not
/// re-rastered whose want moved is STALE (named, counted); damage clears it; the sweep visits exactly the used tiles.</summary>
public sealed class SliceTableLedgerTests
{
    private static readonly SliceFrame Grid = new(0, 0, 0f, 0f, 1f);
    private static readonly RectF Viewport = new(0f, 0f, 1024f, 1024f);

    /// <summary>One turn over a two-tile slice: request, resolve, give every placed tile its want from
    /// <paramref name="wantOf"/> (tile row → hash), then mark the scheduled rasters done and sweep.</summary>
    private static (int Id, int Scheduled) Turn(SliceTable t, int frame, Func<int, ulong> wantOf, ulong key, bool raster = true,
        RectF damage = default)
    {
        t.BeginFrame(frame);
        int id = t.OpenSlice(5, 1, SliceKind.Scroll, in Grid, new RectF(0f, 0f, 1024f, 1024f));
        if (!damage.IsEmpty) t.InvalidateRect(id, in damage, InvalidationReason.Content);
        Span<TileKey> need = [new TileKey(id, 0, 0), new TileKey(id, 0, 1)];
        t.Request(id, in Viewport, 0.0, 1024.0, false, need, default);
        Span<TileRaster> r = stackalloc TileRaster[8];
        t.Resolve(long.MaxValue, r, out int n);
        Span<TilePlacement> pl = stackalloc TilePlacement[8];
        int np = t.CollectPlacements(id, pl);
        for (int i = 0; i < np; i++)
            if (t.SurfaceWantKey(pl[i].Surface) != key) t.SetSurfaceWant(pl[i].Surface, key, wantOf(pl[i].Key.Ty));
        t.CountExposedMissing();
        if (raster) for (int i = 0; i < n; i++) t.MarkRastered(r[i].Key);
        return (id, n);
    }

    [Fact]
    public void AValidTileWhoseWantMoved_IsStale_AndNamed()
    {
        var t = new SliceTable(4, 8, 8);
        var (id, scheduled) = Turn(t, 1, ty => 100UL + (ulong)ty, key: 7);
        Assert.Equal(2, scheduled);
        Assert.Equal(0, t.StaleTiles);   // rastered in this very turn: never stale
        t.EndFrame();

        // The bytes of row 1 changed (a new want) but nothing damaged it: the tile is still VALID with row-1 pixels of old.
        (id, scheduled) = Turn(t, 2, ty => ty == 1 ? 999UL : 100UL, key: 8);
        Assert.Equal(0, scheduled);
        Assert.Equal(1, t.StaleTiles);
        Span<StaleTileSample> s = stackalloc StaleTileSample[4];
        Assert.Equal(1, t.CopyStale(s));
        Assert.Equal(new StaleTileSample(2, id, 5, 1, 0, 1, Want: 999UL, Have: 101UL, RasterFrame: 1), s[0]);
        t.EndFrame();
    }

    [Fact]
    public void DamageReRastersTheTile_AndTheInvariantHoldsAgain()
    {
        var t = new SliceTable(4, 8, 8);
        Turn(t, 1, ty => 100UL + (ulong)ty, key: 7);
        t.EndFrame();
        var (_, scheduled) = Turn(t, 2, ty => ty == 1 ? 999UL : 100UL, key: 8, damage: new RectF(0f, 600f, 100f, 40f));
        Assert.Equal(1, scheduled);          // tile row 1 re-rasters
        Assert.Equal(0, t.StaleTiles);
        t.EndFrame();
        (_, scheduled) = Turn(t, 3, ty => ty == 1 ? 999UL : 100UL, key: 8);
        Assert.Equal(0, scheduled);
        Assert.Equal(0, t.StaleTiles);       // its pixels now hold the want they were rastered for
    }

    [Fact]
    public void AnUnfaithfulRaster_LeavesTheTileInvalid_NeverStale()
    {
        var t = new SliceTable(4, 8, 8);
        Turn(t, 1, ty => 100UL + (ulong)ty, key: 7, raster: false);   // the backend completed nothing
        t.EndFrame();
        var (_, scheduled) = Turn(t, 2, ty => 500UL, key: 8, raster: false);
        Assert.Equal(2, scheduled);   // still invalid → scheduled again, so not "valid with old pixels"
        Assert.Equal(0, t.StaleTiles);
    }

    [Fact]
    public void TheSweepVisitsExactlyTheUsedTiles()
    {
        var t = new SliceTable(4, 8, 8);
        Turn(t, 1, ty => 1UL, key: 7);
        Assert.Equal(t.LiveTiles, t.StaleSweepVisits);
        Assert.Equal(2, t.StaleSweepVisits);
    }

    [Fact]
    public void SurfaceLedger_ReportsTheRasterFrameAndHashes()
    {
        var t = new SliceTable(4, 8, 8);
        var (id, _) = Turn(t, 1, ty => 100UL + (ulong)ty, key: 7);
        Span<TilePlacement> pl = stackalloc TilePlacement[8];
        int np = t.CollectPlacements(id, pl);
        Assert.Equal(2, np);
        for (int i = 0; i < np; i++)
        {
            Assert.True(t.SurfaceLedger(pl[i].Surface, out ulong have, out int rf, out ulong want, out bool stale, out bool now));
            Assert.Equal(100UL + (ulong)pl[i].Key.Ty, have);
            Assert.Equal(have, want);
            Assert.Equal(1, rf);
            Assert.False(stale);
            Assert.True(now);
        }
    }
}
