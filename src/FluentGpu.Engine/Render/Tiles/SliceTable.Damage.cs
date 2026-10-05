using System.Runtime.CompilerServices;
using FluentGpu.Foundation;
using FluentGpu.Rhi;

namespace FluentGpu.Render.Tiles;

// SUB-TILE DAMAGE, the table's half (gpu-renderer.md §13.1l, TileDamage). Per SURFACE the table keeps the op list its
// pixels were rastered from (a SNAPSHOT: one TileOpRec per op) and the slice grid it was rastered on; per scheduled raster
// the recorder hands the list the stream now describes (PlanDamage), and a Content / PrimCount re-raster of a surface whose
// snapshot is trusted becomes a PARTIAL raster of the diff's damage, ∪ what no op describes: an image cross-fade's rect
// (InvalidateRect, per tile) and the pixels an unfaithful raster left behind (per surface). The planned list becomes the
// snapshot only when the backend reports the raster faithful (MarkRastered); anything the table cannot vouch for — a
// fresh surface, another grid, another reason, an unfaithful whole raster — re-rasters whole.
// Storage: every list lives in ONE slab (per surface a snapshot range and this turn's pending range), appended at a bump
// pointer and compacted in place when it runs out; it grows only when the LIVE total (Σ ops of the resident tiles) passes
// its capacity — so a scroll that keeps handing surfaces new tiles allocates nothing once the page's total is reached.
public sealed partial class SliceTable
{
    /// <summary>The slab's starting capacity in op records (24 B each).</summary>
    public const int DamageSlabInitial = 8192;

    private TileOpRec[] _dSlab = [];
    private int _dTop;
    private int[] _dSnapOff = [], _dSnapN = [], _dPendOff = [], _dPendN = [];
    private bool[] _dSnapOk = [];
    private SliceFrame[] _dSnapFrame = [];
    private int[] _dPendTurn = [];
    private SliceFrame[] _dPendFrame = [];
    // The pixels of the surface that do NOT match its snapshot (an unfaithful partial raster wrote into them), tile px.
    private PixelRect[] _dStray = [];
    // This turn's plan per surface: partial (and its damage) or whole.
    private bool[] _dPlannedPartial = [];
    private PixelRect[] _dPlanned = [];
    // Per slab tile: the damage no op describes, accumulated since its last raster (tile px).
    private RectF[] _tExtra = [];
    // compaction scratch: the live ranges (offset, length, owner surface; the sign bit marks a pending range)
    private int[] _dLiveOff = [], _dLiveLen = [], _dLiveOwner = [];
    private int _dPartial, _dWhole;
    private long _dPartialPx, _dWholePx;

    /// <summary>Constructor tail: the per-surface damage arrays.</summary>
    private void InitDamage()
    {
        int n = SurfaceCap;
        _dSlab = new TileOpRec[DamageSlabInitial];
        _dSnapOff = new int[n]; _dSnapN = new int[n]; _dPendOff = new int[n]; _dPendN = new int[n];
        _dSnapOk = new bool[n]; _dSnapFrame = new SliceFrame[n];
        _dPendTurn = new int[n]; _dPendFrame = new SliceFrame[n];
        Array.Fill(_dPendTurn, int.MinValue);
        _dStray = new PixelRect[n];
        _dPlannedPartial = new bool[n]; _dPlanned = new PixelRect[n];
        _tExtra = new RectF[SliceCap * TileCap];
        _dLiveOff = new int[2 * n]; _dLiveLen = new int[2 * n]; _dLiveOwner = new int[2 * n];
    }

    /// <summary>Rasters <see cref="PlanDamage"/> made partial this turn, and the pixels they write.</summary>
    public int PartialRastersThisFrame => _dPartial;
    public long PartialRasterPxThisFrame => _dPartialPx;
    /// <summary>Rasters that stayed whole this turn, and their pixels.</summary>
    public int WholeRastersThisFrame => _dWhole;
    public long WholeRasterPxThisFrame => _dWholePx;
    /// <summary>The damage slab's capacity in op records (diagnostics / gates: it grows only with the live total).</summary>
    public int DamageSlabCapacity => _dSlab.Length;

    /// <summary>BeginFrame: the per-turn damage census.</summary>
    private void DamageBeginFrame() { _dPartial = _dWhole = 0; _dPartialPx = _dWholePx = 0; }

    /// <summary>A surface was acquired (or released): it holds no snapshot.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void DamageForget(int surface)
    {
        if ((uint)surface >= (uint)_dSnapOk.Length) return;
        _dSnapOk[surface] = false;
        _dSnapN[surface] = 0;
        _dStray[surface] = default;
        _dPendTurn[surface] = int.MinValue;
        _dPendN[surface] = 0;
    }

    /// <summary>InvalidateRect's half: a Content rect (an image cross-fade) reaching slab tile <paramref name="t"/> is damage
    /// no op describes.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void DamageExtra(int t, in RectF sliceRect, InvalidationReason reason)
    {
        if (reason != InvalidationReason.Content || (uint)t >= (uint)_tExtra.Length) return;
        RectF tile = TileGrid.TileRect(_tx[t], _ty[t]);
        RectF r = sliceRect.Intersect(tile);
        if (r.IsEmpty) return;
        r = new RectF(r.X - tile.X, r.Y - tile.Y, r.W, r.H);
        RectF e = _tExtra[t];
        _tExtra[t] = e.IsEmpty ? r : RectF.FromLTRB(MathF.Min(e.X, r.X), MathF.Min(e.Y, r.Y), MathF.Max(e.Right, r.Right), MathF.Max(e.Bottom, r.Bottom));
    }

    /// <summary>
    /// Plan scheduled raster <paramref name="tr"/>: remember <paramref name="cur"/> (the ops its replay will draw, on grid
    /// <paramref name="frame"/>; <paramref name="known"/> false = the recorder could not list them) as the surface's
    /// snapshot-to-be, and return the raster PARTIAL — <see cref="TileRaster.Damage"/> = the diff against the surface's
    /// snapshot ∪ the tile's undescribed damage ∪ the surface's stray pixels, rounded out — when every pixel outside it is
    /// provably current: the tile was valid and re-rasters for its content (Content / PrimCount), the surface holds a
    /// trusted snapshot on the same grid, the diff is bounded and the damage is under <see cref="TileDamage.MaxPartialShare"/>
    /// of the surface. Otherwise <paramref name="tr"/> unchanged (whole). Call once per raster, after <see cref="Resolve"/>.
    /// </summary>
    public TileRaster PlanDamage(in TileRaster tr, in SliceFrame frame, ReadOnlySpan<TileOpRec> cur, bool known)
    {
        int s = tr.Surface;
        if ((uint)s >= (uint)_dSnapOk.Length) return tr;
        _dPendN[s] = 0;   // a range written earlier this turn (a duplicate raster entry) is garbage now
        if (known && SlabReserve(cur.Length))
        {
            cur.CopyTo(_dSlab.AsSpan(_dTop));
            _dPendOff[s] = _dTop;
            _dPendN[s] = cur.Length;
            _dTop += cur.Length;
            _dPendTurn[s] = _frame;
            _dPendFrame[s] = frame;
        }
        else _dPendTurn[s] = int.MinValue;

        TileRaster plan = tr;
        if (TileDamage.Enabled && _dPendTurn[s] == _frame && (tr.Reason == InvalidationReason.Content || tr.Reason == InvalidationReason.PrimCount)
            && _dSnapOk[s] && _dSnapFrame[s] == frame)
        {
            int t = Find(tr.Key.SliceId, tr.Key.Tx, tr.Key.Ty);
            if (t >= 0 && TileDamage.Diff(_dSlab.AsSpan(_dSnapOff[s], _dSnapN[s]), cur, tr.W, tr.H, out PixelRect d))
            {
                PixelRect dmg = TileDamage.Round(in d, tr.W, tr.H);
                PixelRect extra = TileDamage.Round(in _tExtra[t], tr.W, tr.H);
                dmg = TileDamage.Union(in dmg, in extra);
                dmg = TileDamage.Union(in dmg, in _dStray[s]);
                if (TileDamage.Area(in dmg) < (long)(TileDamage.MaxPartialShare * tr.W * tr.H))
                    plan = tr with { Damage = dmg, Partial = true };
            }
        }
        _dPlannedPartial[s] = plan.Partial;
        _dPlanned[s] = plan.Damage;
        if (plan.Partial) { _dPartial++; _dPartialPx += TileDamage.Area(in _dPlanned[s]); }
        else { _dWhole++; _dWholePx += (long)tr.W * tr.H; }
        return plan;
    }

    /// <summary>Room for <paramref name="n"/> more records at the slab's top: compact the live ranges (every trusted snapshot
    /// and this turn's pending lists) to the front when the top ran out, grow only when the live total itself does not fit.
    /// False = a list too long to keep (its raster stays whole and leaves no snapshot).</summary>
    private bool SlabReserve(int n)
    {
        if (n > 1 << 20) return false;
        if (_dTop + n <= _dSlab.Length) return true;
        int live = SlabCompact();
        if (live + n > _dSlab.Length)
        {
            int cap = _dSlab.Length;
            while (cap < live + n) cap *= 2;
            Array.Resize(ref _dSlab, cap);
        }
        return true;
    }

    /// <summary>Move every live range to the front of the slab in offset order (an in-place, overlap-safe move) and reset the
    /// top after them. Returns the live total.</summary>
    private int SlabCompact()
    {
        int m = 0;
        for (int s = 0; s < _dSnapOk.Length; s++)
        {
            if (_dSnapOk[s] && _dSnapN[s] > 0) { _dLiveOff[m] = _dSnapOff[s]; _dLiveLen[m] = _dSnapN[s]; _dLiveOwner[m] = s; m++; }
            if (_dPendTurn[s] == _frame && _dPendN[s] > 0) { _dLiveOff[m] = _dPendOff[s]; _dLiveLen[m] = _dPendN[s]; _dLiveOwner[m] = s | int.MinValue; m++; }
        }
        // insertion sort by offset (≤ 2 × SurfaceCap ranges; compaction is rare)
        for (int i = 1; i < m; i++)
        {
            int o = _dLiveOff[i], l = _dLiveLen[i], w = _dLiveOwner[i], j = i - 1;
            while (j >= 0 && _dLiveOff[j] > o) { _dLiveOff[j + 1] = _dLiveOff[j]; _dLiveLen[j + 1] = _dLiveLen[j]; _dLiveOwner[j + 1] = _dLiveOwner[j]; j--; }
            _dLiveOff[j + 1] = o; _dLiveLen[j + 1] = l; _dLiveOwner[j + 1] = w;
        }
        int top = 0;
        for (int i = 0; i < m; i++)
        {
            int from = _dLiveOff[i], len = _dLiveLen[i];
            if (from != top) Array.Copy(_dSlab, from, _dSlab, top, len);
            int owner = _dLiveOwner[i];
            if (owner < 0) _dPendOff[owner & int.MaxValue] = top; else _dSnapOff[owner] = top;
            top += len;
        }
        _dTop = top;
        return top;
    }

    /// <summary>MarkRastered's half: the planned list is now what the surface's pixels hold.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void DamageRastered(int t, int surface)
    {
        if ((uint)t < (uint)_tExtra.Length) _tExtra[t] = default;
        if ((uint)surface >= (uint)_dSnapOk.Length) return;
        if (_dPendTurn[surface] != _frame) { DamageForget(surface); return; }
        _dSnapOff[surface] = _dPendOff[surface];
        _dSnapN[surface] = _dPendN[surface];
        _dSnapFrame[surface] = _dPendFrame[surface];
        _dSnapOk[surface] = true;
        _dStray[surface] = default;
        _dPendTurn[surface] = int.MinValue;
        _dPendN[surface] = 0;
    }

    /// <summary>The backend did NOT raster <paramref name="key"/> faithfully this turn (dropped instances, an image still in
    /// flight, a skipped raster): a partial raster's damage may now hold anything (all of the surface when the backend wrote
    /// beyond the plan, <paramref name="beyondPlan"/>) — it stays stray until a faithful raster covers it; a whole one leaves
    /// no pixel the snapshot can vouch for. The tile stays invalid (it re-rasters next turn).</summary>
    public void MarkRasterFailed(in TileKey key, bool beyondPlan = false)
    {
        int t = Find(key.SliceId, key.Tx, key.Ty);
        if (t < 0) return;
        int s = _tiles[t].Surface;
        if ((uint)s >= (uint)_dSnapOk.Length) return;
        if (!beyondPlan && _dPendTurn[s] == _frame && _dPlannedPartial[s]) _dStray[s] = TileDamage.Union(in _dStray[s], in _dPlanned[s]);
        else DamageForget(s);
        _dPendTurn[s] = int.MinValue;
        _dPendN[s] = 0;
    }

    /// <summary>The trusted snapshot of <paramref name="surface"/> (tests / diagnostics): its op count, or −1 when none.</summary>
    public int SnapshotOps(int surface)
        => (uint)surface < (uint)_dSnapOk.Length && _dSnapOk[surface] ? _dSnapN[surface] : -1;
}
