using System.Runtime.CompilerServices;
using FluentGpu.Render.Evidence;

namespace FluentGpu.Render.Tiles;

// The raster ledger's half in the table (docs/plans/evidence-diagnostics-implementation.md §A.1): per SURFACE the turn
// that rastered its pixels and the slab tile it holds, over the content state SliceTable.Content.cs keeps (want / raster
// hash); and the stale-tile invariant, swept inside CountExposedMissing's existing slab pass. With content-derived
// validity (the content check in Request) a stale tile can only mean a tile the check never saw — it stays a permanent
// detector. Fixed arrays, zero allocation after construction.
public sealed partial class SliceTable
{
    /// <summary>At most this many stale tiles are named per turn (the census counts all of them).</summary>
    public const int StaleListCap = 64;

    private int[] _surfRasterFrame = [];
    private int[] _surfTile = [];
    private readonly int[] _staleList = new int[StaleListCap];
    private int _stale, _staleCount, _sweepVisits;

    /// <summary>Constructor tail: the per-surface ledger arrays.</summary>
    private void InitLedger()
    {
        InitContent();
        _surfRasterFrame = new int[SurfaceCap];
        _surfTile = new int[SurfaceCap];
        Array.Fill(_surfTile, -1);
    }

    /// <summary>A surface was acquired for slab tile <paramref name="t"/>: its ledger starts empty (no pixels yet).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void LedgerAcquired(int t, int surface)
    {
        if ((uint)surface >= (uint)_surfTile.Length) return;
        _surfTile[surface] = t;
        _surfRasterFrame[surface] = 0;
        ContentAcquired(surface);
    }

    /// <summary>A surface returned to the pool: it no longer holds any tile's pixels.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void LedgerReleased(int surface)
    {
        if ((uint)surface < (uint)_surfTile.Length) _surfTile[surface] = -1;
    }

    /// <summary>The backend completed a raster into <paramref name="surface"/>: its pixels now hold the current want.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void LedgerRastered(int surface)
    {
        if ((uint)surface >= (uint)_surfTile.Length) return;
        ContentRastered(surface);
        _surfRasterFrame[surface] = _frame;
    }

    /// <summary>The raster ledger of the tile held in <paramref name="surface"/>: the hash its pixels were rastered for and
    /// the turn, the hash the stream wants now, whether it is STALE (valid, used this turn, not re-rastered in it, and the
    /// two hashes differ) and whether it rasters in this very turn. False when the surface holds no tile.</summary>
    public bool SurfaceLedger(int surface, out ulong rasterHash, out int rasterFrame, out ulong want, out bool stale, out bool rasteredNow)
    {
        rasterHash = 0; rasterFrame = 0; want = 0; stale = false; rasteredNow = false;
        if ((uint)surface >= (uint)_surfTile.Length) return false;
        int t = _surfTile[surface];
        if (t < 0) return false;
        rasterHash = _surfRasterHash[surface];
        rasterFrame = _surfRasterFrame[surface];
        want = _surfWant[surface];
        rasteredNow = _scheduledFrame[t] == _frame;
        stale = IsStale(t, in _tiles[t]);
        return true;
    }

    /// <summary>The stale-tile invariant for slab tile <paramref name="t"/>: a VALID tile used this turn and NOT re-rastered
    /// in it whose pixels were rastered for a different content hash than the stream now wants. Must never hold.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool IsStale(int t, in TileState ts)
    {
        if (!_used[t] || ts.Surface < 0 || ts.Invalid != InvalidationReason.None || ts.LastUsedFrame != _frame) return false;
        if (_scheduledFrame[t] == _frame || (uint)ts.Surface >= (uint)_surfWant.Length) return false;
        ulong want = _surfWant[ts.Surface];
        return want != 0 && _surfRasterHash[ts.Surface] != want;
    }

    private void BeginStaleSweep() { _stale = _staleCount = _sweepVisits = 0; }

    /// <summary>One used tile of CountExposedMissing's sweep (the same pass — no second walk over the slab).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void SweepStale(int t, in TileState ts)
    {
        _sweepVisits++;
        if (!IsStale(t, in ts)) return;
        _stale++;
        if (_staleCount < StaleListCap) _staleList[_staleCount++] = t;
    }

    /// <summary>Stale tiles in the current turn (after <see cref="CountExposedMissing"/>). Must be 0.</summary>
    public int StaleTiles => _stale;

    /// <summary>Used tiles the last sweep examined — the op-count pin (<c>SliceTableLedgerTests</c>): exactly the used
    /// tiles, once each.</summary>
    public int StaleSweepVisits => _sweepVisits;

    /// <summary>The current turn's stale tiles (first <see cref="StaleListCap"/>), as samples. Returns the count written.</summary>
    public int CopyStale(Span<StaleTileSample> dst)
    {
        int n = Math.Min(dst.Length, _staleCount);
        for (int i = 0; i < n; i++)
        {
            int t = _staleList[i];
            int s = t / TileCap;
            ref TileState ts = ref _tiles[t];
            int surf = ts.Surface;
            bool known = (uint)surf < (uint)_surfWant.Length;
            dst[i] = new StaleTileSample(_frame, s, _rows[s].NodeIndex, _rows[s].Gen, _tx[t], _ty[t],
                known ? _surfWant[surf] : 0UL, known ? _surfRasterHash[surf] : 0UL, known ? _surfRasterFrame[surf] : 0, _rows[s].Sub);
        }
        return n;
    }
}
