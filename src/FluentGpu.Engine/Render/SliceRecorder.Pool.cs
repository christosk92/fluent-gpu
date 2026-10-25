using System.Buffers;
using System.Numerics;
using FluentGpu.Render.Evidence;
using FluentGpu.Render.Tiles;

namespace FluentGpu.Render;

// PER-SLOT STORAGE THROUGH THE SHARED POOLS (audit PERF r3 RASTER, C5: render-thread allocation during navigation).
// Every slot keeps its own grow-once storage — the arena's two byte buffers and two sort buffers, the per-op content table,
// the scan's marker and segment tables. A slot is reused by whatever slice is cut next (Allocate takes the lowest free
// slot), so each slot's storage grew to the largest slice it ever hosted while a slot hosting a larger one than before
// allocated anew: a navigation to a new page doubled its slices' storage up from whatever sizes the dead slots happened to
// hold (nav-burst: DrawList.Ensure, ContentScanOp, ScanSlot ≈ 1 MB per 10 s on the render thread, still growing after 30 s).
// Now a slot that died gives its storage back to the shared pools one pass later (ReleaseDeadSlots: the pass that retired it
// and its composite are over, nothing reads it), and a slot's storage grows by renting from those pools, so buffers move to
// the slices that need them. Grown-out tables return at the next pass too (a span of the old one may be held for the rest
// of the pass, as with Array.Resize). A rented table is cleared before the copy: its contents are exactly what
// Array.Resize would have produced (the old prefix, default beyond), so no reader can see another slot's data.
// Memory: a dead slot no longer holds its largest-ever storage; what it returns sits in the shared pool, which bounds and
// trims itself.
public sealed partial class SliceRecorder
{
    private bool[] _slotReleased = new bool[32];
    private readonly List<TileOp[]> _retiredOps = new();
    private readonly List<ScanMark[]> _retiredMarks = new();
    private readonly List<ScanSeg[]> _retiredSegs = new();
    private readonly List<int[]> _retiredInts = new();
    private readonly List<SliceSpan[]> _retiredSpans = new();

    /// <summary>A pooled, cleared table of at least <paramref name="n"/> entries (a power of two: a pool bucket).</summary>
    private static T[] RentCleared<T>(int n)
    {
        var a = ArrayPool<T>.Shared.Rent(Math.Max(n, 16));
        Array.Clear(a);
        return a;
    }

    /// <summary>Array.Resize through the pool: <paramref name="a"/> becomes a table of at least <paramref name="n"/>
    /// entries holding its old contents (default beyond); the old table returns at the next pass.</summary>
    private static void GrowPooled<T>(ref T[] a, int n, List<T[]> retired)
    {
        var b = RentCleared<T>(n);
        Array.Copy(a, b, Math.Min(a.Length, b.Length));
        if (Poolable(a.Length)) retired.Add(a);
        a = b;
    }

    private static bool Poolable(int length) => length >= 16 && BitOperations.IsPow2(length);

    private static void ReturnAll<T>(List<T[]> retired)
    {
        for (int i = 0; i < retired.Count; i++) ArrayPool<T>.Shared.Return(retired[i]);
        retired.Clear();
    }

    private static void ReturnOne<T>(T[]?[] table, int s)
    {
        if (table[s] is { } a && Poolable(a.Length)) ArrayPool<T>.Shared.Return(a);
        table[s] = null;
    }

    /// <summary>BeginPass: return last pass's grown-out tables, and the storage of every slot that died since it was last
    /// handed out.</summary>
    private void ReleaseDeadSlots()
    {
        ReturnAll(_retiredOps);
        ReturnAll(_retiredMarks);
        ReturnAll(_retiredSegs);
        ReturnAll(_retiredInts);
        ReturnAll(_retiredSpans);
        if (_slotReleased.Length < _recs.Length) Array.Resize(ref _slotReleased, _recs.Length);
        for (int s = 1; s < _slotCount; s++)
        {
            if (_recs[s].Live || _slotReleased[s]) continue;
            _slotReleased[s] = true;
            _arenas[s]?.ReleaseBuffers();
            if (s < _cOps.Length)
            {
                ReturnOne(_cOps, s); _cOpCount[s] = 0;
                ReturnOne(_cSegScopeStart, s); ReturnOne(_cSegScopeCount, s); ReturnOne(_cSegScopeIdx, s); _cSegScopeLen[s] = 0;
            }
            // the walk index, double-buffered with the arena (an empty table grows from the pool: BeginIndexEntry)
            ReturnOne(_index, s); _index[s] = Array.Empty<SliceSpan>(); _indexCount[s] = 0;
            ReturnOne(_indexPrior, s); _indexPrior[s] = Array.Empty<SliceSpan>(); _indexPriorCount[s] = 0;
            if (s < _scanMarks.Length) { ReturnOne(_scanMarks, s); ReturnOne(_scanSegs, s); }
        }
    }

    /// <summary>FindOrCreate: slot <paramref name="s"/> hosts a new slice (its storage grows from the pools).</summary>
    private void ClaimSlot(int s)
    {
        if (_slotReleased.Length < _recs.Length) Array.Resize(ref _slotReleased, _recs.Length);
        _slotReleased[s] = false;
    }
}
