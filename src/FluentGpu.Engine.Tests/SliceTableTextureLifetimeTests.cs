using System;
using FluentGpu.Foundation;
using FluentGpu.Render.Tiles;
using Xunit;

namespace FluentGpu.Engine.Tests;

/// <summary>The table owns tile TEXTURE lifetime (gpu-renderer.md §13.1g): a backend releases a tile slot's texture only
/// when <see cref="SliceTable.TrimmedSurfaces"/> names it, and the table names only slots NO tile holds, idle past
/// <see cref="SliceTable.SurfaceTrimTurns"/>. The defect this pins: the D3D12 surface pool used to trim on its own
/// "last sampled" clock, but a tile consumed only through a retained group / self-blur result is valid and placed every
/// turn while nothing samples it — its texture was trimmed under it and that part of the window composited nothing
/// (album track list / lyrics blank at idle, back only where a hover re-rastered a tile).</summary>
public sealed class SliceTableTextureLifetimeTests
{
    private static readonly SliceFrame Grid = new(0, 0, 0f, 0f, 1f);
    private static readonly RectF Viewport = new(0f, 0f, 1024f, 512f);

    /// <summary>One render turn of a one-tile static slice (node 5): opened when <paramref name="open"/>, its tile requested
    /// when <paramref name="request"/>; every scheduled raster completes. Returns the turn's trim list (a copy) and the slot
    /// the tile was placed in (−1 when not placed).</summary>
    private static int[] Turn(SliceTable t, int frame, bool open, out int placedSurface, bool request = true)
    {
        placedSurface = -1;
        t.BeginFrame(frame);
        int id = -1;
        if (open)
        {
            id = t.OpenSlice(5, 1, SliceKind.Static, in Grid, new RectF(0f, 0f, 1024f, 512f));
            if (request)
            {
                Span<TileKey> need = [new TileKey(id, 0, 0)];
                t.Request(id, in Viewport, 0.0, 512.0, false, need, default);
            }
        }
        Span<TileRaster> r = stackalloc TileRaster[8];
        t.Resolve(long.MaxValue, r, out int n);
        int[] trims = t.TrimmedSurfaces.ToArray();
        for (int i = 0; i < n; i++) t.MarkRastered(r[i].Key);
        if (open)
        {
            Span<TilePlacement> pl = stackalloc TilePlacement[8];
            if (t.CollectPlacements(id, pl) > 0) placedSurface = pl[0].Surface;
        }
        t.EndFrame();
        return trims;
    }

    [Fact]
    public void APlacedValidTile_IsNeverTrimmed_HoweverLongNothingSamplesIt()
    {
        var t = new SliceTable(4, 8, 8);
        int surface = -1;
        // Far past the old pool's 360-turn "unsampled" trim age: the tile is rastered once, then only PLACED (valid) —
        // exactly a tile a retained group surface is re-drawn from every turn.
        int turns = 3 * (SliceTable.IdleEvictFrames + SliceTable.SurfaceTrimTurns);
        for (int f = 1; f <= turns; f++)
        {
            int[] trims = Turn(t, f, open: true, out int placed);
            if (f == 1) surface = placed;
            Assert.True(surface >= 0);
            Assert.Equal(surface, placed);
            Assert.DoesNotContain(surface, trims);
            Assert.True(t.IsSurfaceHeld(surface));
            Assert.True(t.IsSurfaceBacked(surface));
        }
        Assert.Equal(0L, t.TrimmedTotal);
    }

    [Fact]
    public void AReleasedSlot_IsTrimmedOnce_SurfaceTrimTurnsAfterItWasFreed()
    {
        var t = new SliceTable(4, 8, 8);
        Turn(t, 1, open: true, out int s);
        Assert.True(s >= 0);
        // Turn 2: the slice is not opened — it retires at EndFrame and its slot is free from turn 2 on.
        for (int f = 2; f < 2 + SliceTable.SurfaceTrimTurns; f++)
            Assert.Empty(Turn(t, f, open: false, out _));
        Assert.False(t.IsSurfaceHeld(s));
        Assert.True(t.IsSurfaceBacked(s));
        Assert.Equal(new[] { s }, Turn(t, 2 + SliceTable.SurfaceTrimTurns, open: false, out _));
        Assert.False(t.IsSurfaceBacked(s));
        for (int f = 3 + SliceTable.SurfaceTrimTurns; f < 3 * SliceTable.SurfaceTrimTurns; f++)
            Assert.Empty(Turn(t, f, open: false, out _));
        Assert.Equal(1L, t.TrimmedTotal);
    }

    [Fact]
    public void ASlotReacquiredBeforeItsTrim_KeepsItsTexture()
    {
        var t = new SliceTable(4, 8, 8);
        Turn(t, 1, open: true, out int s);
        for (int f = 2; f <= 50; f++) Assert.Empty(Turn(t, f, open: false, out _));
        Turn(t, 51, open: true, out int again);
        Assert.Equal(s, again);   // the most recently freed slot is reused (its texture is still there)
        for (int f = 52; f <= 52 + 3 * SliceTable.SurfaceTrimTurns; f++)
            Assert.Empty(Turn(t, f, open: true, out _));
        Assert.True(t.IsSurfaceBacked(s));
        Assert.Equal(0L, t.TrimmedTotal);
    }

    [Fact]
    public void ATrimmedSlotReacquired_IsBackedAgain_AndRastersBeforeItIsPlaced()
    {
        var t = new SliceTable(4, 8, 8);
        Turn(t, 1, open: true, out int s);
        int f = 2;
        for (; f <= 2 + SliceTable.SurfaceTrimTurns; f++) Turn(t, f, open: false, out _);
        Assert.False(t.IsSurfaceBacked(s));

        t.BeginFrame(f);
        int id = t.OpenSlice(5, 1, SliceKind.Static, in Grid, new RectF(0f, 0f, 1024f, 512f));
        Span<TileKey> need = [new TileKey(id, 0, 0)];
        t.Request(id, in Viewport, 0.0, 512.0, false, need, default);
        Span<TileRaster> r = stackalloc TileRaster[8];
        t.Resolve(long.MaxValue, r, out int n);
        Assert.Equal(1, n);                       // the re-acquired slot is rastered (the backend re-creates its texture)
        Assert.Equal(s, r[0].Surface);
        Assert.True(t.IsSurfaceBacked(s));
        Assert.True(t.TrimmedSurfaces.IsEmpty);   // never trimmed in the turn that acquires it
    }

    [Fact]
    public void AnIdleEvictedTile_IsTrimmedOnlyAfterItsSlotWasFree()
    {
        var t = new SliceTable(4, 8, 8);
        Turn(t, 1, open: true, out int s);
        // Still opened every turn but no longer requested: the idle sweep evicts it (> IdleEvictFrames, on a 16-turn
        // boundary), and only from then does the trim clock run.
        int evictedAt = -1, trimmedAt = -1;
        for (int f = 2; f <= 2 * (SliceTable.IdleEvictFrames + SliceTable.SurfaceTrimTurns) && trimmedAt < 0; f++)
        {
            int[] trims = Turn(t, f, open: true, out _, request: false);
            if (evictedAt < 0 && !t.IsSurfaceHeld(s)) evictedAt = f;
            if (Array.IndexOf(trims, s) >= 0) trimmedAt = f;
        }
        Assert.True(evictedAt > 1 + SliceTable.IdleEvictFrames);
        Assert.Equal(evictedAt + SliceTable.SurfaceTrimTurns, trimmedAt);
    }
}
