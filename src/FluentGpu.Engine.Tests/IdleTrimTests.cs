using System;
using System.Threading;
using FluentGpu.Foundation;
using FluentGpu.Hosting.Threading;
using FluentGpu.Media;
using FluentGpu.Render.Tiles;
using Xunit;

namespace FluentGpu.Engine.Tests;

/// <summary>The wall-clock idle trims: the table's stale-tile pass, the render thread's bounded idle wait, and the fetcher
/// pool swap with a rental in flight.</summary>
public sealed class IdleTrimTests
{
    private static readonly SliceFrame Grid = new(0, 0, 0f, 0f, 1f);
    private static readonly RectF Viewport = new(0f, 0f, 1024f, 512f);

    private static void Turn(SliceTable t, int frame, params (int tx, int ty)[] want)
    {
        t.BeginFrame(frame);
        int id = t.OpenSlice(5, 1, SliceKind.Static, in Grid, new RectF(0f, 0f, 4096f, 512f));
        if (want.Length > 0)
        {
            var need = new TileKey[want.Length];
            for (int i = 0; i < want.Length; i++) need[i] = new TileKey(id, (short)want[i].tx, (short)want[i].ty);
            t.Request(id, in Viewport, 0.0, 512.0, false, need, default);
        }
        Span<TileRaster> r = stackalloc TileRaster[8];
        t.Resolve(long.MaxValue, r, out int n);
        for (int i = 0; i < n; i++) t.MarkRastered(r[i].Key);
        t.EndFrame();
    }

    [Fact]
    public void EvictStaleSparesTheLastTurnsSetAndNamesEachSlotOnce()
    {
        var t = new SliceTable(4, 8, 8);
        Turn(t, 1, (0, 0), (1, 0));
        Turn(t, 2, (0, 0));                       // tile (1,0) was not requested by the last turn
        long later = Environment.TickCount64 + 1_000_000;
        Assert.Equal(2, t.ResidentTiles);
        Assert.Equal(0L, t.NextStaleInMs(later));   // one tile is already stale
        Assert.Equal(1, t.EvictStale(later));
        Assert.Equal(1, t.ResidentTiles);          // the last turn's tile survives
        Assert.Equal(1, t.TrimFreeSlotsNow().Length);
        Assert.True(t.TrimFreeSlotsNow().IsEmpty);   // each slot is named exactly once
        Assert.Equal(0, t.EvictStale(later));        // nothing else is stale
        Assert.Equal(-1, t.NextStaleInMs(later));
    }

    [Fact]
    public void NotYetStaleTilesAreKeptAndTheNextDueTimeIsReported()
    {
        var t = new SliceTable(4, 8, 8);
        Turn(t, 1, (0, 0), (1, 0));
        Turn(t, 2, (0, 0));
        long now = Environment.TickCount64;
        Assert.Equal(0, t.EvictStale(now));
        long due = t.NextStaleInMs(now);
        Assert.InRange(due, 1, SliceTable.StaleTileMs);
    }

    [Fact]
    public void ATurnThatRequestedNothingProtectsEverything()
    {
        var t = new SliceTable(4, 8, 8);
        Turn(t, 1, (0, 0), (1, 0));
        Turn(t, 2);                               // empty viewport (minimized): no request set at all
        long later = Environment.TickCount64 + 1_000_000;
        Assert.Equal(0, t.EvictStale(later));
        Assert.Equal(2, t.ResidentTiles);
        Assert.Equal(-1, t.NextStaleInMs(later));
    }

    [Fact]
    public void TheRenderThreadTrimsOnATimedOutWaitAndStillTurnsOnAWake()
    {
        int polls = 0, submits = 0;
        var due = new ManualResetEventSlim();
        int answer = 30;                           // ms until the next pass
        using var rt = new RenderThread(new SceneFramePublisher(), _ => Interlocked.Increment(ref submits), async: false,
            idleTrim: _ => { if (Interlocked.Increment(ref polls) >= 3) due.Set(); return Volatile.Read(ref answer); });
        Assert.True(due.Wait(5000), "the clean-idle wait never timed out into a trim pass");
        Assert.Equal(0, Volatile.Read(ref submits));   // a trim pass is not a turn
        Volatile.Write(ref answer, -1);                // nothing trimmable: the loop must block, not poll
        Thread.Sleep(200);
        int settled = Volatile.Read(ref polls);
        Thread.Sleep(300);
        Assert.Equal(settled, Volatile.Read(ref polls));
    }

    [Fact]
    public void ThePoolSwapLeavesAnInFlightRentalValid()
    {
        using var f = new DefaultImageFetcher(new System.Net.Http.HttpClient(), null, null);
        var before = f.PoolForTest;
        byte[] buf = f.PoolForTest.Rent(5000);
        Assert.False(f.TrimIdlePool(long.MaxValue));           // never rented through the fetcher: not dirty
        using (var ms = new System.IO.MemoryStream(new byte[100])) Assert.Equal(100, f.ReadAllPooled(ms, 100, CancellationToken.None).GetAwaiter().GetResult().Length);
        Assert.True(f.TrimIdlePool(Environment.TickCount64 + DefaultImageFetcher.PoolIdleTrimMs + 1));
        Assert.NotSame(before, f.PoolForTest);
        f.ReturnBuffer(buf);                                   // returns into the NEW pool without throwing
        using var ms2 = new System.IO.MemoryStream(new byte[100]);
        var again = f.ReadAllPooled(ms2, 100, CancellationToken.None).GetAwaiter().GetResult();
        Assert.Equal(100, again.Length);
    }
}
