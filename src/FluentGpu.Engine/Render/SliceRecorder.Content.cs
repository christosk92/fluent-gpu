using System.Runtime.CompilerServices;
using FluentGpu.Foundation;
using FluentGpu.Render.Evidence;
using FluentGpu.Render.Tiles;

namespace FluentGpu.Render;

// CONTENT-DERIVED TILE VALIDITY, the recorder's half (gpu-renderer.md §13.1c, issue #1). ScanSlot — once per arena
// buffer, i.e. only for a slot re-recorded this pass — fills a per-op table (byte offset, effective footprint, content
// hash, scope bit) and the scopes open at each segment start; a segment then answers "what does tile T's replay draw" as
// a fold of exactly the ops whose footprint reaches T (TileContentHash.TileWant). BuildComposite hands every segment's
// Request a SegmentContent keyed by (buffer gen, segment, grid): the table folds a resident valid tile's want only when
// that key moved, and invalidates the tile when its pixels were rastered for anything else. A kept slice's key never
// moves, so a composite-only turn folds nothing and records nothing. Always on, on every recorder that composites;
// zero allocation per turn once warmed (per-slot tables grow only when a scene first needs more, like every scan table).
public sealed partial class SliceRecorder
{
    private TileOp[][] _cOps = new TileOp[32][];
    private int[] _cOpCount = new int[32];
    private int[][] _cSegScopeStart = new int[32][];
    private int[][] _cSegScopeCount = new int[32][];
    private int[][] _cSegScopeIdx = new int[32][];
    private int[] _cSegScopeLen = new int[32];
    private int[] _cScope = new int[32];
    private ulong[] _cScopeSig = new ulong[33];   // [d] = the fold of the hashes of the d innermost-open scopes' chain
    private int _cScopeDepth;

    private void EnsureContentStorage(int n)
    {
        if (_cOps.Length >= n) return;
        Array.Resize(ref _cOps, n);
        Array.Resize(ref _cOpCount, n);
        Array.Resize(ref _cSegScopeStart, n);
        Array.Resize(ref _cSegScopeCount, n);
        Array.Resize(ref _cSegScopeIdx, n);
        Array.Resize(ref _cSegScopeLen, n);
    }

    /// <summary>ScanSlot start: an empty op table and scope stack for slot <paramref name="s"/>; segment 0 opens with no scope.</summary>
    private void ContentScanBegin(int s)
    {
        EnsureContentStorage(_recs.Length);
        _cOps[s] ??= new TileOp[64];
        _cSegScopeStart[s] ??= new int[9];
        _cSegScopeCount[s] ??= new int[9];
        _cSegScopeIdx[s] ??= new int[16];
        _cOpCount[s] = 0;
        _cSegScopeLen[s] = 0;
        _cScopeDepth = 0;
        _cScopeSig[0] = TileContentHash.Empty;
        _cSegScopeStart[s][0] = 0;
        _cSegScopeCount[s][0] = 0;
    }

    /// <summary>ScanSlot: one op at byte <paramref name="pos"/> with its effective footprint (slice-space DIP) and hash;
    /// <paramref name="scope"/> = it opens a clip / stencil clip / layer the following ops are drawn inside.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ContentScanOp(int s, int pos, in RectF bounds, ulong hash, bool scope, bool clip = false, bool spread = false)
    {
        int n = _cOpCount[s];
        ref TileOp[] ops = ref _cOps[s];
        if (n == ops.Length) Array.Resize(ref ops, n * 2);
        ops[n] = new TileOp { Pos = pos, Bounds = bounds, Hash = hash, Scope = scope, Clip = clip, Spread = spread, ScopeSig = _cScopeSig[_cScopeDepth] };
        _cOpCount[s] = n + 1;
        if (!scope) return;
        if (_cScopeDepth == _cScope.Length) { Array.Resize(ref _cScope, _cScope.Length * 2); Array.Resize(ref _cScopeSig, _cScope.Length + 1); }
        _cScopeSig[_cScopeDepth + 1] = TileContentHash.Fold(_cScopeSig[_cScopeDepth], hash);
        _cScope[_cScopeDepth++] = n;
    }

    /// <summary>ScanSlot: a scope closed (PopClip / PopStencilClip / PopLayer).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ContentScanPop()
    {
        if (_cScopeDepth > 0) _cScopeDepth--;
    }

    /// <summary>ScanSlot: segment <paramref name="k"/> starts here (right after a child-slice marker) — snapshot the scopes
    /// still open, which a per-tile replay of that segment reconstructs from its prefix.</summary>
    private void ContentScanSegment(int s, int k)
    {
        ref int[] starts = ref _cSegScopeStart[s];
        ref int[] counts = ref _cSegScopeCount[s];
        if (k >= starts.Length) { Array.Resize(ref starts, Math.Max(k + 1, starts.Length * 2)); Array.Resize(ref counts, starts.Length); }
        int at = _cSegScopeLen[s];
        ref int[] idx = ref _cSegScopeIdx[s];
        if (at + _cScopeDepth > idx.Length) Array.Resize(ref idx, Math.Max(at + _cScopeDepth, idx.Length * 2));
        for (int i = 0; i < _cScopeDepth; i++) idx[at + i] = _cScope[i];
        starts[k] = at;
        counts[k] = _cScopeDepth;
        _cSegScopeLen[s] = at + _cScopeDepth;
    }

    /// <summary>The key segment <paramref name="seg"/> of <paramref name="slot"/>'s tile wants are computed under on the
    /// grid (<paramref name="ox"/>, <paramref name="oy"/>) at <paramref name="scale"/>; 0 when the slot has no scanned
    /// content table (validity then rests on damage alone — never the case for a slot BuildComposite placed).</summary>
    private ulong ContentKey(int slot, int seg, float ox, float oy, float scale)
    {
        if ((uint)slot >= (uint)_cOps.Length || _cOps[slot] is null || (uint)slot >= (uint)_scanSegs.Length
            || _scanSegs[slot] is null || _scanGen[slot] != _curGen[slot] || seg > _scanMarkCount[slot]) return 0UL;
        return TileContentHash.WantKey(_curGen[slot], seg, ox, oy, scale);
    }

    /// <summary>The content hash tile <paramref name="tilePx"/> of segment <paramref name="seg"/> of <paramref name="slot"/>
    /// draws (valid only under a non-zero <see cref="ContentKey"/>), and the part of the tile it paints (tile px).</summary>
    private ulong TileWantOf(int slot, int seg, float ox, float oy, float scale, in RectF tilePx, out int hits, out RectF paint)
    {
        ref ScanSeg sg = ref _scanSegs[slot][seg];
        ReadOnlySpan<TileOp> ops = _cOps[slot].AsSpan(0, _cOpCount[slot]);
        ReadOnlySpan<int> open = seg < _cSegScopeStart[slot].Length
            ? _cSegScopeIdx[slot].AsSpan(_cSegScopeStart[slot][seg], _cSegScopeCount[slot][seg]) : default;
        return TileContentHash.TileWant(ops, open, seg, sg.ByteStart, sg.ByteEnd, in tilePx, scale, ox, oy, out _, out hits, out paint);
    }

    private TileOpRec[] _damageOps = new TileOpRec[1024];
    private int[] _rowOfSliceId = new int[16];

    /// <summary>BuildComposite, after Resolve: plan every scheduled raster's sub-tile damage (<see cref="SliceTable.PlanDamage"/>)
    /// from the ops its replay will draw — a raster whose row or content table is unknown stays whole.</summary>
    private void PlanRasterDamage(SliceTable table, float scale)
    {
        if (_rasterCount == 0) return;
        if (_rowOfSliceId.Length < table.SliceCap) _rowOfSliceId = new int[table.SliceCap];
        Array.Fill(_rowOfSliceId, -1);
        for (int i = 0; i < _rowCount; i++)
            if ((uint)_rows[i].Id < (uint)_rowOfSliceId.Length && _rowOfSliceId[_rows[i].Id] < 0) _rowOfSliceId[_rows[i].Id] = i;
        for (int i = 0; i < _rasterCount; i++)
        {
            ref TileRaster tr = ref _rasters[i];
            int row = (uint)tr.Key.SliceId < (uint)_rowOfSliceId.Length ? _rowOfSliceId[tr.Key.SliceId] : -1;
            if (row < 0 || row >= _rowSlot.Length) { tr = table.PlanDamage(in tr, default, default, known: false); continue; }
            int slot = _rowSlot[row], seg = _rowSeg[row];
            float ox = _rowOx[row], oy = _rowOy[row];
            if (ContentKey(slot, seg, ox, oy, scale) == 0) { tr = table.PlanDamage(in tr, _rows[row].Frame, default, known: false); continue; }
            ref ScanSeg sg = ref _scanSegs[slot][seg];
            ReadOnlySpan<TileOp> ops = _cOps[slot].AsSpan(0, _cOpCount[slot]);
            ReadOnlySpan<int> open = seg < _cSegScopeStart[slot].Length
                ? _cSegScopeIdx[slot].AsSpan(_cSegScopeStart[slot][seg], _cSegScopeCount[slot][seg]) : default;
            int n = TileContentHash.CollectTileOps(ops, open, sg.ByteStart, sg.ByteEnd, TileGrid.TileRect(tr.Key), scale, ox, oy, ref _damageOps);
            tr = table.PlanDamage(in tr, _rows[row].Frame, _damageOps.AsSpan(0, n), known: true);
        }
    }

    // Per composite row (BuildComposite): which slot / segment it is and the device grid it is cut on.
    private int[] _rowSlot = new int[64], _rowSeg = new int[64];
    private float[] _rowOx = new float[64], _rowOy = new float[64];

    /// <summary>BuildComposite: row <paramref name="row"/> is segment <paramref name="segment"/> of <paramref name="slot"/>,
    /// cut on the device grid at (<paramref name="ox"/>, <paramref name="oy"/>).</summary>
    private void NoteRow(int row, int slot, int segment, float ox, float oy)
    {
        if (row >= _rowSlot.Length)
        {
            int n = Math.Max(row + 1, _rowSlot.Length * 2);
            Array.Resize(ref _rowSlot, n); Array.Resize(ref _rowSeg, n); Array.Resize(ref _rowOx, n); Array.Resize(ref _rowOy, n);
        }
        _rowSlot[row] = slot; _rowSeg[row] = segment; _rowOx[row] = ox; _rowOy[row] = oy;
    }

    /// <summary>One segment's content, as <see cref="SliceTable.Request{TContent}"/> asks for it.</summary>
    private readonly struct SegmentContent : ITileContent
    {
        private readonly SliceRecorder _r;
        private readonly int _slot, _seg;
        private readonly float _ox, _oy, _scale;

        public SegmentContent(SliceRecorder r, int slot, int seg, float ox, float oy, float scale)
        {
            _r = r; _slot = slot; _seg = seg; _ox = ox; _oy = oy; _scale = scale;
            Key = r.ContentKey(slot, seg, ox, oy, scale);
        }

        public ulong Key { get; }

        public ulong Want(in RectF tilePx, out int ops, out RectF paint) => _r.TileWantOf(_slot, _seg, _ox, _oy, _scale, in tilePx, out ops, out paint);
    }

    /// <summary>BuildComposite, after the placements of row <paramref name="row"/> were collected into
    /// [<paramref name="p0"/>, <paramref name="p1"/>): the want of every placed tile whose surface has none under the
    /// segment's key — a surface acquired this turn, which rasters in this very submission for exactly that content. (A
    /// resident tile's want was settled by the content check in Request.) One key compare per placed tile otherwise.</summary>
    private void ComputeWants(SliceTable table, int row, int p0, int p1, float scale)
    {
        if (p1 <= p0 || row >= _rowSlot.Length) return;
        int slot = _rowSlot[row], seg = _rowSeg[row];
        float ox = _rowOx[row], oy = _rowOy[row];
        ulong key = ContentKey(slot, seg, ox, oy, scale);
        if (key == 0) return;
        for (int p = p0; p < p1; p++)
        {
            ref readonly TilePlacement tp = ref _placements[p];
            if (table.SurfaceWantKey(tp.Surface) == key) continue;
            ulong want = TileWantOf(slot, seg, ox, oy, scale, TileGrid.TileRect(tp.Key), out int ops, out RectF paint);
            table.SetSurfaceWant(tp.Surface, key, want, ops, paint);
            _placements[p] = table.PlacementPaint(in tp);   // it rasters this submission: its quad is what it now paints
        }
    }
}
