using System;
using FluentGpu.Foundation;
using FluentGpu.Render.Evidence;
using FluentGpu.Render.Tiles;
using Xunit;

namespace FluentGpu.Engine.Tests;

/// <summary>The per-tile content hash behind the stale-tile invariant (docs/plans/evidence-diagnostics-implementation.md
/// §A.1): a tile's want folds exactly the ops its replay would draw — those whose effective footprint overlaps it, plus the
/// scopes open at its segment's start — in stream order; an op beyond the tile never changes it.</summary>
public sealed class TileContentHashTests
{
    private const float Scale = 1f;
    private static readonly RectF TileA = TileGrid.TileRect(0, 0);   // [0,1024) × [0,512)
    private static readonly RectF TileB = TileGrid.TileRect(0, 1);   // [0,1024) × [512,1024)

    private static TileOp Op(int pos, float y, float h, ulong hash, bool scope = false)
        => new() { Pos = pos, Bounds = new RectF(10f, y, 200f, h), Hash = hash, Scope = scope };

    private static ulong Want(ReadOnlySpan<TileOp> ops, in RectF tile, ReadOnlySpan<int> open = default, int start = 0, int end = int.MaxValue)
        => TileContentHash.TileWant(ops, open, 0, start, end, tile, Scale, 0f, 0f, out _);

    [Fact]
    public void AnOpBeyondTheTile_NeverChangesItsWant()
    {
        TileOp[] before = [Op(0, 20f, 40f, 11), Op(40, 700f, 40f, 22)];
        TileOp[] after = [Op(0, 20f, 40f, 11), Op(40, 700f, 40f, 99)];   // only the op in tile B changed

        Assert.Equal(Want(before, TileA), Want(after, TileA));
        Assert.NotEqual(Want(before, TileB), Want(after, TileB));
    }

    [Fact]
    public void AnOpStraddlingTheBoundary_BelongsToBothTiles()
    {
        TileOp[] before = [Op(0, 490f, 40f, 11)];
        TileOp[] after = [Op(0, 490f, 40f, 12)];
        Assert.NotEqual(Want(before, TileA), Want(after, TileA));
        Assert.NotEqual(Want(before, TileB), Want(after, TileB));
    }

    [Fact]
    public void StreamOrderIsContent()
    {
        TileOp[] ab = [Op(0, 20f, 40f, 11), Op(40, 60f, 40f, 22)];
        TileOp[] ba = [Op(0, 20f, 40f, 22), Op(40, 60f, 40f, 11)];
        Assert.NotEqual(Want(ab, TileA), Want(ba, TileA));   // painter order changes pixels where they overlap
    }

    [Fact]
    public void AScopeOpenAtTheSegmentStart_FoldsIntoTheTilesItOverlaps()
    {
        TileOp[] ops = [Op(0, 0f, 400f, 5, scope: true), Op(40, 20f, 40f, 11)];
        TileOp[] moved = [Op(0, 0f, 300f, 6, scope: true), Op(40, 20f, 40f, 11)];   // the enclosing clip moved
        int[] open = [0];
        ulong a = Want(ops, TileA, open, start: 40), b = Want(moved, TileA, open, start: 40);
        Assert.NotEqual(a, b);
        Assert.Equal(Want(ops, TileB, open, start: 40), Want(moved, TileB, open, start: 40));   // clip is not in tile B
    }

    [Fact]
    public void OnlyTheSegmentsByteRangeCounts()
    {
        TileOp[] ops = [Op(0, 20f, 40f, 11), Op(40, 20f, 40f, 22), Op(80, 20f, 40f, 33)];
        TileOp[] changedOutside = [Op(0, 20f, 40f, 99), Op(40, 20f, 40f, 22), Op(80, 20f, 40f, 98)];
        Assert.Equal(Want(ops, TileA, start: 40, end: 80), Want(changedOutside, TileA, start: 40, end: 80));
    }

    [Fact]
    public void AnInfiniteFootprint_ReachesEveryTile_AndAnEmptyOneNone()
    {
        TileOp[] inf = [new() { Pos = 0, Bounds = RectF.Infinite, Hash = 7 }];
        TileOp[] inf2 = [new() { Pos = 0, Bounds = RectF.Infinite, Hash = 8 }];
        Assert.NotEqual(Want(inf, TileB), Want(inf2, TileB));
        TileOp[] empty = [new() { Pos = 0, Bounds = default, Hash = 7 }];
        TileOp[] empty2 = [new() { Pos = 0, Bounds = default, Hash = 8 }];
        Assert.Equal(Want(empty, TileA), Want(empty2, TileA));
    }

    [Fact]
    public void TheGridOrigin_MapsSliceDipIntoTilePx()
    {
        // a segment cut at device origin y = 512: an op at DIP y = 600 (scale 1) lies in tile row 0 of that segment
        TileOp[] ops = [Op(0, 600f, 20f, 11)];
        TileOp[] ops2 = [Op(0, 600f, 20f, 12)];
        ulong a = TileContentHash.TileWant(ops, default, 0, 0, int.MaxValue, TileA, 1f, 0f, 512f, out _);
        ulong b = TileContentHash.TileWant(ops2, default, 0, 0, int.MaxValue, TileA, 1f, 0f, 512f, out _);
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void FoldTouchesEachOpAtMostOncePerTile()
    {
        var ops = new TileOp[100];
        for (int i = 0; i < ops.Length; i++) ops[i] = Op(i * 8, i * 10f, 8f, (ulong)(i + 1));
        int[] open = [0, 1];
        TileContentHash.TileWant(ops, open, 0, 16, 16 + 50 * 8, TileA, 1f, 0f, 0f, out int visits);
        Assert.Equal(open.Length + 50, visits);   // the open scopes + exactly the segment's ops, never the rest
    }

    [Fact]
    public void OpHash_SeesEveryByte_AndIsNeverZero()
    {
        byte[] a = new byte[37], b = new byte[37];
        for (int i = 0; i < a.Length; i++) a[i] = b[i] = (byte)i;
        b[36] = 0xFF;   // the tail byte past the last whole word
        Assert.NotEqual(TileContentHash.OpHash(a), TileContentHash.OpHash(b));
        Assert.NotEqual(0UL, TileContentHash.OpHash(ReadOnlySpan<byte>.Empty));
    }

    [Fact]
    public void WantKey_ChangesWithTheBufferTheSegmentAndTheGrid()
    {
        ulong k = TileContentHash.WantKey(5, 0, 0f, 0f, 1.5f);
        Assert.NotEqual(k, TileContentHash.WantKey(6, 0, 0f, 0f, 1.5f));
        Assert.NotEqual(k, TileContentHash.WantKey(5, 1, 0f, 0f, 1.5f));
        Assert.NotEqual(k, TileContentHash.WantKey(5, 0, 64f, 0f, 1.5f));
        Assert.NotEqual(k, TileContentHash.WantKey(5, 0, 0f, 0f, 2f));
        Assert.Equal(k, TileContentHash.WantKey(5, 0, 0f, 0f, 1.5f));
    }
}
