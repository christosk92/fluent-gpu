using System.Runtime.CompilerServices;
using FluentGpu.Foundation;
using FluentGpu.Rhi;

namespace FluentGpu.Render.Tiles;

/// <summary>One tile the backend must raster this turn: its key, the surface slot to raster into, why, its needed-set
/// order (the list is ordered visible → ahead → behind) and the device-px extent of that surface (a region surface for an
/// effect slice's tile, the full tile otherwise). <paramref name="Partial"/> = re-raster only <paramref name="Damage"/> (tile
/// px, half-open; empty = nothing changed): the surface already holds every other pixel of the current content
/// (<see cref="TileDamage"/>, <see cref="SliceTable.PlanDamage"/>); otherwise the whole surface is rastered.</summary>
public readonly record struct TileRaster(TileKey Key, int Surface, InvalidationReason Reason, byte Order, int W = TileGrid.W, int H = TileGrid.H,
    PixelRect Damage = default, bool Partial = false)
{
    /// <summary>The pixel rect this raster writes (tile px): <see cref="Damage"/> for a partial raster, else the surface.</summary>
    public PixelRect Written => Partial ? Damage : new PixelRect(0, 0, W, H);
}

/// <summary>One resident tile a composite item samples: its key, the surface slot holding its pixels and that surface's
/// device-px extent (the placed quad is W×H at the tile's origin).</summary>
/// <summary>A resident tile the composite places: its key, its surface, the surface's extent (<paramref name="W"/> ×
/// <paramref name="H"/>) and the part of it the tile PAINTS ([<paramref name="PaintX0"/>, <paramref name="PaintX1"/>) ×
/// [<paramref name="PaintY0"/>, <paramref name="PaintY1"/>), tile px): the composite draws only that part — the rest of
/// the surface is transparent. The default is the whole surface.</summary>
public readonly record struct TilePlacement(TileKey Key, int Surface, int W = TileGrid.W, int H = TileGrid.H,
    short PaintX0 = 0, short PaintY0 = 0, short PaintX1 = short.MaxValue, short PaintY1 = short.MaxValue)
{
    /// <summary>The painted part, cut by the surface extent (tile px).</summary>
    public int Px0 => Math.Min((int)PaintX0, W);
    public int Py0 => Math.Min((int)PaintY0, H);
    public int Px1 => Math.Min((int)PaintX1, W);
    public int Py1 => Math.Min((int)PaintY1, H);
}

/// <summary>
/// The retained-tile bookkeeping (docs/plans/scroll-gpu-retained-tiles-implementation.md §A.3/§A.4/§A.6): a FIXED slab of
/// <see cref="SliceCap"/> slices × <see cref="TileCap"/> tile entries plus a pool of <see cref="SurfaceCap"/> logical
/// surface slots (the backend's SurfacePool maps a slot to a texture). Zero allocation after construction.
/// <para><b>Per render turn:</b> <see cref="BeginFrame"/> → <see cref="OpenSlice"/> per slice (+ the invalidations no
/// byte describes: an image cross-fade via <see cref="InvalidateRect"/>, a theme / forced-full repaint via
/// <see cref="InvalidateAll"/>) → <see cref="Request{TContent}"/> each slice's
/// <see cref="TileGrid.Needed(in RectF, double, in Scroll.Motion.MotionFeel, double, double, bool, Span{TileKey}, Span{byte}, out int, int)"/>
/// set, checking every resident tile against the content its segment's bytes now describe (content-derived validity,
/// gpu-renderer.md §13.1c) → <see cref="Resolve"/> (surfaces + the raster list, visible first across ALL slices) → the
/// backend rasters and
/// the host calls <see cref="MarkRastered"/> for the tiles it ACTUALLY rastered → <see cref="EndFrame"/> (slices not
/// opened this frame retire).</para>
/// <para><b>Budget (bytes):</b> every surface is charged its real extent — a full tile is
/// <see cref="TileBudget.TileBytes"/>, an effect slice's tile only its content's bucketed region
/// (<see cref="LayerTargetBucket.Dim"/>). A visible (order 0) tile is NEVER evicted in the frame it is visible. Eviction
/// is LRU weighted by distance from the slice's viewport, tiles not needed this frame first (off-coverage/out-of-window),
/// then the behind row, then the ahead band. A slice whose visible tiles cannot all be made resident within the budget
/// is <see cref="IsDegraded">Degraded</see> for the frame: none of its tiles are rastered and the backend draws it directly
/// (today's cost, never blank). A tile idle for <see cref="IdleEvictFrames"/> turns is evicted (its surface returns to the
/// pool).</para>
/// <para><b>Texture lifetime (the table owns it):</b> a surface slot that has held NO tile for <see cref="SurfaceTrimTurns"/>
/// turns is named in <see cref="TrimmedSurfaces"/> (carried on <c>CompositeFrame.TrimSurfaces</c>) and the backend
/// releases its texture — the backend never trims a tile texture on a clock of its own. Only the table knows which slots
/// back a VALID tile: a tile can stay valid and placed for any number of turns while the backend never samples its pixels
/// (a group / self-blur / acrylic backdrop re-drawn from a retained result), and a texture trimmed under such a tile
/// composited NOTHING while the table still believed it valid — content vanished at idle and came back only where damage
/// re-rastered it. A held slot is never trimmed (gpu-renderer.md §13.1g).</para>
/// </summary>
public sealed partial class SliceTable
{
    public const int DefaultSliceCap = 16, DefaultTileCap = 64, DefaultSurfaceCap = 64;

    /// <summary>Eviction weight of one tile of distance from the viewport, in frames of LRU age.</summary>
    public const int DistanceWeightFrames = 8;

    /// <summary>A resident tile not requested for this many turns is evicted (its surface returns to the pool; once it has
    /// held no tile for <see cref="SurfaceTrimTurns"/> more turns its texture is trimmed).</summary>
    public const int IdleEvictFrames = 240;

    /// <summary>A surface slot that has held no tile for this many turns (and may still hold a texture) is named in
    /// <see cref="TrimmedSurfaces"/>: the backend releases its texture. A slot a tile holds is never named, however long the
    /// backend went without sampling it.</summary>
    public const int SurfaceTrimTurns = 120;

    /// <summary>WALL-CLOCK idle trim (<see cref="EvictStale"/>): a resident tile the last composite turn did not request and
    /// that no turn has requested for this long is evicted, and its slot's texture released at once
    /// (<see cref="TrimFreeSlotsNow"/>). The turn-counted rules above (<see cref="IdleEvictFrames"/>,
    /// <see cref="SurfaceTrimTurns"/>) never fire in an app that stops compositing — a still window runs no turns, so the
    /// leftovers of the last scroll (the old viewport's tiles, 10s of MiB on a UMA adapter) stayed resident for ever. The
    /// last turn's own set (visible + the ahead / behind band it requested) is never touched, so returning to the page
    /// re-rasters nothing it could have scrolled onto.</summary>
    public const long StaleTileMs = 10_000;

    public const int ReasonCount = (int)InvalidationReason.Degraded + 1;

    public int SliceCap { get; }
    public int TileCap { get; }
    public int SurfaceCap { get; }

    // ── slices ────────────────────────────────────────────────────────────────────────────────────────────────
    private readonly SliceRow[] _rows;
    private readonly bool[] _live;
    private readonly int[] _openedFrame;
    private readonly bool[] _degraded;
    private readonly bool[] _hasVp;
    private readonly bool[] _horizontal;
    private readonly int[] _vpM0, _vpM1, _vpC0, _vpC1;   // visible tile range in main/cross indices (distance weighting)

    // ── tiles: slab index t = slice * TileCap + i ──────────────────────────────────────────────────────────────
    private readonly TileState[] _tiles;
    private readonly short[] _tx, _ty;
    private readonly bool[] _used;
    private readonly int[] _pendingFrame;
    private readonly int[] _scheduledFrame;
    private int _reqCount;   // tiles the CURRENT / last turn requested: 0 ⇒ the last turn asked for nothing (a minimized window), so the idle trim must not treat everything as stale
    private readonly long[] _usedMs;   // wall clock (Environment.TickCount64) of the tile's latest Request — the idle trim's clock

    // ── surfaces ──────────────────────────────────────────────────────────────────────────────────────────────
    private readonly int[] _freeSurfaces;
    private int _freeCount;
    private int _resident;
    private long _residentBytes;
    // Texture lifetime per surface slot: whether the backend may hold a texture for it (set when the slot is acquired —
    // an acquired tile is invalid, so it is rastered into the slot that turn — cleared when the slot is trimmed), the turn
    // it last returned to the free list, and this turn's trim list.
    private readonly bool[] _surfBacked;
    private readonly int[] _surfFreeSince;
    private readonly int[] _trimList;
    private int _trimCount;
    private long _trimmedTotal;

    // ── per-frame work lists ──────────────────────────────────────────────────────────────────────────────────
    private readonly int[] _pend0, _pend1, _pend2;
    private int _n0, _n1, _n2;

    // ── census ────────────────────────────────────────────────────────────────────────────────────────────────
    private readonly int[] _reasonCounts = new int[ReasonCount];
    private int _frame = int.MinValue + 1;
    private int _evicted, _rastered, _scheduled, _degradedSlices;

    private static int s_nextOwnerId;

    /// <summary>A process-unique, non-zero identity of this table (stamped on every <c>CompositeFrame.OwnerToken</c>
    /// built from it). Its surface numbering (<c>0, 1, 2, …</c>) is only meaningful inside ONE backend tile pool, so a
    /// backend that sees two different owners compositing into the same pool has two hosts overwriting each other's
    /// retained tiles (a detached child that reached <c>SubmitComposite</c>).</summary>
    public int OwnerId { get; } = Interlocked.Increment(ref s_nextOwnerId);

    public SliceTable(int sliceCap = DefaultSliceCap, int tileCap = DefaultTileCap, int surfaceCap = DefaultSurfaceCap)
    {
        if (sliceCap <= 0) throw new ArgumentOutOfRangeException(nameof(sliceCap));
        if (tileCap <= 0) throw new ArgumentOutOfRangeException(nameof(tileCap));
        if (surfaceCap < 0) throw new ArgumentOutOfRangeException(nameof(surfaceCap));
        SliceCap = sliceCap; TileCap = tileCap; SurfaceCap = surfaceCap;

        _rows = new SliceRow[sliceCap];
        _live = new bool[sliceCap];
        _openedFrame = new int[sliceCap];
        _degraded = new bool[sliceCap];
        _hasVp = new bool[sliceCap];
        _horizontal = new bool[sliceCap];
        _vpM0 = new int[sliceCap]; _vpM1 = new int[sliceCap]; _vpC0 = new int[sliceCap]; _vpC1 = new int[sliceCap];

        int slab = sliceCap * tileCap;
        _tiles = new TileState[slab];
        _tx = new short[slab];
        _ty = new short[slab];
        _used = new bool[slab];
        _pendingFrame = new int[slab];
        _scheduledFrame = new int[slab];
        _usedMs = new long[slab];
        _pend0 = new int[slab]; _pend1 = new int[slab]; _pend2 = new int[slab];

        _freeSurfaces = new int[surfaceCap];
        for (int i = 0; i < surfaceCap; i++) _freeSurfaces[i] = surfaceCap - 1 - i;   // pop order 0, 1, 2, …
        _freeCount = surfaceCap;
        _surfBacked = new bool[surfaceCap];
        _surfFreeSince = new int[surfaceCap];
        _trimList = new int[surfaceCap];
        for (int t = 0; t < slab; t++) { _tiles[t].Surface = -1; _scheduledFrame[t] = int.MinValue; }
        InitLedger();
    }

    // ── census surface ────────────────────────────────────────────────────────────────────────────────────────
    public int Frame => _frame;
    public int ResidentTiles => _resident;
    /// <summary>Bytes the resident surfaces occupy (each charged its real extent).</summary>
    public long ResidentBytes => _residentBytes;
    public int EvictedThisFrame => _evicted;
    public int RasteredThisFrame => _rastered;
    /// <summary>Tiles <see cref="Resolve"/> put on the raster list this frame.</summary>
    public int ScheduledThisFrame => _scheduled;
    public int DegradedSlicesThisFrame => _degradedSlices;

    /// <summary>The surface slots whose textures the backend must release this turn (written by <see cref="Resolve"/>,
    /// cleared by <see cref="BeginFrame"/>): free — held by no tile — for at least <see cref="SurfaceTrimTurns"/> turns.
    /// Never a slot a tile holds, and never a slot acquired this turn.</summary>
    public ReadOnlySpan<int> TrimmedSurfaces => _trimList.AsSpan(0, _trimCount);

    /// <summary>Surface slots trimmed since construction (diagnostics / gates).</summary>
    public long TrimmedTotal => _trimmedTotal;

    /// <summary>True when a tile holds <paramref name="surface"/> (its pixels back that tile).</summary>
    public bool IsSurfaceHeld(int surface) => (uint)surface < (uint)_surfTile.Length && _surfTile[surface] >= 0;

    /// <summary>True when the backend may still hold a texture for <paramref name="surface"/> (acquired since its last trim).</summary>
    public bool IsSurfaceBacked(int surface) => (uint)surface < (uint)_surfBacked.Length && _surfBacked[surface];

    /// <summary>Tiles that went from valid to <paramref name="reason"/> this frame (plus <see cref="InvalidationReason.NoTexture"/>
    /// for first requests).</summary>
    public int InvalidationCount(InvalidationReason reason) => _reasonCounts[(int)reason];

    public int LiveSlices
    {
        get { int n = 0; for (int s = 0; s < SliceCap; s++) if (_live[s]) n++; return n; }
    }

    public int LiveTiles
    {
        get { int n = 0; for (int t = 0; t < _used.Length; t++) if (_used[t]) n++; return n; }
    }

    /// <summary>Σ surface bytes (each charged its real extent) of every VISIBLE (order-0) tile requested in the current
    /// turn, resident or not — the memory the page needs to show without degrading a slice. The census compares it with
    /// the budget (a page whose visible need exceeds the budget degrades by construction).</summary>
    public long VisibleNeedBytes
    {
        get
        {
            long b = 0;
            for (int t = 0; t < _tiles.Length; t++)
            {
                if (!_used[t]) continue;
                ref TileState ts = ref _tiles[t];
                if (ts.LastUsedFrame == _frame && ts.Order == TileGrid.OrderVisible) b += BytesOf(ts);
            }
            return b;
        }
    }

    public bool IsLive(int sliceId) => (uint)sliceId < (uint)SliceCap && _live[sliceId];
    public bool IsDegraded(int sliceId) => (uint)sliceId < (uint)SliceCap && _degraded[sliceId];

    /// <summary>The slice's descriptor; the slice recorder fills its Draw/Damage/SpanIndex ranges.</summary>
    public ref SliceRow Row(int sliceId) => ref _rows[sliceId];

    public bool TryGetTile(in TileKey key, out TileState state)
    {
        int t = Find(key.SliceId, key.Tx, key.Ty);
        if (t < 0) { state = default; return false; }
        state = _tiles[t];
        return true;
    }

    // ── frame ─────────────────────────────────────────────────────────────────────────────────────────────────
    /// <summary>Open render turn <paramref name="frame"/> (monotonically increasing). Resets the per-frame census and,
    /// every 16th turn, evicts tiles idle for more than <see cref="IdleEvictFrames"/> turns.</summary>
    public void BeginFrame(int frame)
    {
        _frame = frame;
        _reqCount = 0;
        _n0 = _n1 = _n2 = 0;
        _trimCount = 0;
        Array.Clear(_degraded);
        Array.Clear(_hasVp);
        if ((frame & 15) == 0)
        {
            for (int t = 0; t < _tiles.Length; t++)
                if (_used[t] && _tiles[t].Surface >= 0 && (long)frame - _tiles[t].LastUsedFrame > IdleEvictFrames) Evict(t);
        }
        // Idle aging is housekeeping, not this frame's budget pressure: the census starts clean after it.
        _evicted = _rastered = _scheduled = _degradedSlices = 0;
        _contentChecked = _contentCaught = 0;
        DamageBeginFrame();
        Array.Clear(_reasonCounts);
    }

    /// <summary>Pure idle-trim decision for one resident tile: not part of the last composite turn's request set and unrequested
    /// for at least <paramref name="staleMs"/> of wall clock.</summary>
    public static bool IsStale(long nowMs, long usedMs, long staleMs, bool requestedLastTurn)
        => !requestedLastTurn && nowMs - usedMs >= staleMs;

    /// <summary>Evict every resident tile that <see cref="IsStale"/> (render thread, BETWEEN turns). Returns how many were
    /// evicted; follow with <see cref="TrimFreeSlotsNow"/> to hand their textures back. A visible tile is never stale: the last
    /// turn requested it.</summary>
    public int EvictStale(long nowMs, long staleMs = StaleTileMs)
    {
        if (_reqCount == 0) return 0;   // the last turn requested nothing (empty viewport): its protected set is empty, so never judge by it
        int n = 0;
        for (int t = 0; t < _tiles.Length; t++)
        {
            if (!_used[t] || _tiles[t].Surface < 0) continue;
            if (!IsStale(nowMs, _usedMs[t], staleMs, _tiles[t].LastUsedFrame == _frame)) continue;
            Evict(t);
            n++;
        }
        return n;
    }

    /// <summary>Evict EVERY resident tile, the last turn's set included (render thread - or the UI thread in SingleThread mode -
    /// BETWEEN turns; the hidden-window Shallow stage). Unlike <see cref="EvictStale"/> it ignores recency: while the window is
    /// hidden nothing is visible, so no tile is protected. Each tile becomes <see cref="InvalidationReason.Evicted"/>, so the first
    /// composite after the restore schedules it and rasters it in the SAME submission before anything samples it - there is no
    /// "blank tile" state to present. Follow with <see cref="TrimFreeSlotsNow"/> to hand the textures back. Returns the tiles evicted.</summary>
    public int EvictAll()
    {
        int n = 0;
        for (int t = 0; t < _tiles.Length; t++)
        {
            if (!_used[t] || _tiles[t].Surface < 0) continue;
            Evict(t);
            n++;
        }
        return n;
    }

    /// <summary>Milliseconds until the first resident tile outside the last turn's set turns stale (<see cref="EvictStale"/>):
    /// 0 when one already is, -1 when there is none (nothing for the idle trim to wait for).</summary>
    public long NextStaleInMs(long nowMs, long staleMs = StaleTileMs)
    {
        if (_reqCount == 0) return -1;
        long best = long.MaxValue;
        for (int t = 0; t < _tiles.Length; t++)
        {
            if (!_used[t] || _tiles[t].Surface < 0 || _tiles[t].LastUsedFrame == _frame) continue;
            long due = _usedMs[t] + staleMs - nowMs;
            if (due < best) best = due;
        }
        return best == long.MaxValue ? -1 : Math.Max(0, best);
    }

    /// <summary>Name EVERY free slot that may still hold a texture, without waiting out <see cref="SurfaceTrimTurns"/> (the
    /// idle path: no turn is coming to age them). Replaces this turn's trim list; the caller hands the span straight to the
    /// backend. A slot a tile holds is not on the free list, so it is never named.</summary>
    public ReadOnlySpan<int> TrimFreeSlotsNow()
    {
        _trimCount = 0;
        for (int i = 0; i < _freeCount && _trimCount < _trimList.Length; i++)
        {
            int s = _freeSurfaces[i];
            if (!_surfBacked[s]) continue;
            _surfBacked[s] = false;
            _trimList[_trimCount++] = s;
            _trimmedTotal++;
        }
        return _trimList.AsSpan(0, _trimCount);
    }

    /// <summary>Open (or re-open) the slice cut at scene node (<paramref name="nodeIndex"/>, <paramref name="gen"/>) in role /
    /// segment <paramref name="sub"/>.
    /// A re-opened slice whose raster scale changed is invalidated <see cref="InvalidationReason.ScaleChanged"/>; one whose
    /// device origin, residual fraction, kind or <paramref name="identity"/> changed, <see cref="InvalidationReason.SliceGeometry"/>.
    /// Returns the slice id, or −1 when all <see cref="SliceCap"/> slots are live (the recorder folds the excess — §A.2).</summary>
    public int OpenSlice(int nodeIndex, uint gen, SliceKind kind, in SliceFrame frame, in RectF contentBounds, int scrollVp = -1, int sub = 0,
        ulong identity = 0, bool mainAxisGrows = true)
    {
        int s = -1;
        for (int i = 0; i < SliceCap; i++)
            if (_live[i] && _rows[i].NodeIndex == nodeIndex && _rows[i].Gen == gen && _rows[i].Sub == sub) { s = i; break; }

        if (s >= 0)
        {
            ref SliceRow row = ref _rows[s];
            if (row.Frame.Scale != frame.Scale) InvalidateSlice(s, InvalidationReason.ScaleChanged);
            else if (row.Kind != kind || row.Frame.OriginX != frame.OriginX || row.Frame.OriginY != frame.OriginY
                     || row.Frame.ResidualX != frame.ResidualX || row.Frame.ResidualY != frame.ResidualY
                     || row.Identity != identity)
                InvalidateSlice(s, InvalidationReason.SliceGeometry);
        }
        else
        {
            for (int i = 0; i < SliceCap; i++) if (!_live[i]) { s = i; break; }
            if (s < 0) return -1;
            _live[s] = true;
            _rows[s] = default;
            _rows[s].Id = s;
            _rows[s].NodeIndex = nodeIndex;
            _rows[s].Gen = gen;
            _rows[s].Sub = sub;
        }

        ref SliceRow r = ref _rows[s];
        r.Kind = kind;
        r.Frame = frame;
        r.ContentBounds = contentBounds;
        r.ScrollVp = scrollVp;
        r.Identity = identity;
        r.MainAxisGrows = mainAxisGrows;
        _openedFrame[s] = _frame;
        return s;
    }

    /// <summary>Close the turn: every live slice not opened this frame retires and returns its surfaces to the pool.</summary>
    public void EndFrame()
    {
        for (int s = 0; s < SliceCap; s++)
            if (_live[s] && _openedFrame[s] != _frame) RetireSlice(s);
    }

    private void RetireSlice(int s)
    {
        int b = s * TileCap;
        for (int i = 0; i < TileCap; i++)
        {
            int t = b + i;
            if (_tiles[t].Surface >= 0) ReleaseSurface(t);
            _used[t] = false;
            _tiles[t] = default;
            _tiles[t].Surface = -1;
            _scheduledFrame[t] = int.MinValue;
            _tExtra[t] = default;
        }
        _live[s] = false;
        _rows[s] = default;
    }

    // ── invalidation ──────────────────────────────────────────────────────────────────────────────────────────
    /// <summary>Invalidate every known tile of <paramref name="sliceId"/> whose rect shares area with the slice-space
    /// device-px <paramref name="sliceRect"/> (touching edges do not count). Returns the number of tiles that went from
    /// valid to <paramref name="reason"/>. A tile that has never been requested needs no invalidation — it is
    /// <see cref="InvalidationReason.NoTexture"/> when it is.</summary>
    public int InvalidateRect(int sliceId, in RectF sliceRect, InvalidationReason reason)
    {
        if (!IsLive(sliceId) || sliceRect.IsEmpty || reason == InvalidationReason.None) return 0;
        int n = 0, b = sliceId * TileCap;
        for (int i = 0; i < TileCap; i++)
        {
            int t = b + i;
            if (!_used[t]) continue;
            if (!TileGrid.TileRect(_tx[t], _ty[t]).Overlaps(sliceRect)) continue;
            DamageExtra(t, in sliceRect, reason);   // a sub-tile raster must cover it (no op describes it)
            if (Invalidate(t, reason)) n++;
        }
        return n;
    }

    /// <summary>Invalidate every known tile of one slice (ScaleChanged, SliceGeometry, BackgroundOrTheme).</summary>
    public int InvalidateSlice(int sliceId, InvalidationReason reason)
    {
        if (!IsLive(sliceId) || reason == InvalidationReason.None) return 0;
        int n = 0, b = sliceId * TileCap;
        for (int i = 0; i < TileCap; i++)
            if (_used[b + i] && Invalidate(b + i, reason)) n++;
        return n;
    }

    /// <summary>Invalidate every known tile of every slice (a theme/background change, a forced full repaint).</summary>
    public int InvalidateAll(InvalidationReason reason)
    {
        int n = 0;
        for (int s = 0; s < SliceCap; s++) n += InvalidateSlice(s, reason);
        return n;
    }

    private bool Invalidate(int t, InvalidationReason reason)
    {
        ref TileState ts = ref _tiles[t];
        if (ts.Invalid != InvalidationReason.None) return false;
        ts.Invalid = reason;
        _reasonCounts[(int)reason]++;
        return true;
    }

    // ── scheduling ────────────────────────────────────────────────────────────────────────────────────────────
    /// <summary>Record one slice's needed set for this frame (the output of <see cref="TileGrid.Needed(in RectF, double, in Scroll.Motion.MotionFeel, double, double, bool, Span{TileKey}, Span{byte}, out int, int)"/>,
    /// with its order span; an empty <paramref name="order"/> treats every key as visible). Marks each tile used this frame
    /// (LRU), creates entries for first requests (<see cref="InvalidationReason.NoTexture"/>), invalidates a valid tile whose
    /// covered span coverage has since grown past (<see cref="InvalidationReason.ValidRectChanged"/>) or whose region
    /// extent changed (<see cref="InvalidationReason.SliceGeometry"/>), and queues every tile that needs a raster for
    /// <see cref="Resolve"/>. <paramref name="viewportSlice"/> anchors distance-weighted eviction. A visible tile the slab
    /// cannot hold degrades the slice.</summary>
    public void Request(int sliceId, in RectF viewportSlice, double coverStart, double coverEnd, bool horizontal,
        ReadOnlySpan<TileKey> needed, ReadOnlySpan<byte> order)
    {
        var none = default(NoTileContent);
        Request(sliceId, in viewportSlice, coverStart, coverEnd, horizontal, needed, order, ref none);
    }

    /// <summary><see cref="Request(int, in RectF, double, double, bool, ReadOnlySpan{TileKey}, ReadOnlySpan{byte})"/> with
    /// CONTENT-DERIVED validity (gpu-renderer.md §13.1c): a resident, valid tile whose want under
    /// <paramref name="content"/>'s key was not computed yet is folded now, and the tile is invalidated
    /// (<see cref="InvalidationReason.Content"/>) when its pixels were rastered for different content — damage rects are
    /// a pre-filter, never the source of truth. A kept segment's key is unchanged, so a composite-only turn folds
    /// nothing.</summary>
    public void Request<TContent>(int sliceId, in RectF viewportSlice, double coverStart, double coverEnd, bool horizontal,
        ReadOnlySpan<TileKey> needed, ReadOnlySpan<byte> order, ref TContent content) where TContent : struct, ITileContent
    {
        if (!IsLive(sliceId)) return;
        int mainSize = horizontal ? TileGrid.W : TileGrid.H, crossSize = horizontal ? TileGrid.H : TileGrid.W;
        _horizontal[sliceId] = horizontal;
        _hasVp[sliceId] = !viewportSlice.IsEmpty;
        _vpM0[sliceId] = TileGrid.FirstIndex(horizontal ? viewportSlice.X : viewportSlice.Y, mainSize);
        _vpM1[sliceId] = TileGrid.LastIndex(horizontal ? viewportSlice.Right : viewportSlice.Bottom, mainSize);
        _vpC0[sliceId] = TileGrid.FirstIndex(horizontal ? viewportSlice.Y : viewportSlice.X, crossSize);
        _vpC1[sliceId] = TileGrid.LastIndex(horizontal ? viewportSlice.Bottom : viewportSlice.Right, crossSize);

        for (int k = 0; k < needed.Length; k++)
        {
            TileKey key = needed[k];
            if (key.SliceId != sliceId) continue;
            byte ord = k < order.Length ? order[k] : TileGrid.OrderVisible;

            int t = FindOrCreate(sliceId, key.Tx, key.Ty);
            if (t < 0)
            {
                if (ord == TileGrid.OrderVisible) Degrade(sliceId);
                continue;
            }

            ref TileState ts = ref _tiles[t];
            if (ts.LastUsedFrame == _frame && ts.Order < ord) ord = ts.Order;   // a duplicate keeps its best order
            ts.LastUsedFrame = _frame;
            _usedMs[t] = Environment.TickCount64;
            _reqCount++;
            ts.Order = ord;

            // The surface extent this tile needs: an effect slice's region, else the whole tile.
            SurfaceExtent(sliceId, key.Tx, key.Ty, out short sw, out short sh);
            if (ts.SurfW != sw || ts.SurfH != sh)
            {
                if (ts.Surface >= 0) ReleaseSurface(t);   // re-acquired (and re-charged) at the new extent by Resolve
                ts.SurfW = sw; ts.SurfH = sh;
                Invalidate(t, InvalidationReason.SliceGeometry);
            }

            int m = horizontal ? key.Tx : key.Ty;
            float lo = (float)Math.Max((double)m * mainSize, coverStart);
            float hi = (float)Math.Min((double)(m + 1) * mainSize, coverEnd);
            if (ts.Invalid == InvalidationReason.None && (lo < ts.CoveredLo || hi > ts.CoveredHi))
                Invalidate(t, InvalidationReason.ValidRectChanged);
            if (ts.Invalid == InvalidationReason.None && ts.Surface >= 0) CheckContent(t, in key, ref content);

            if (ts.Invalid == InvalidationReason.None || _pendingFrame[t] == _frame) continue;
            _pendingFrame[t] = _frame;
            ts.CoveredLo = lo;
            ts.CoveredHi = hi;
            switch (ord)
            {
                case TileGrid.OrderVisible: _pend0[_n0++] = t; break;
                case TileGrid.OrderAhead: _pend1[_n1++] = t; break;
                default: _pend2[_n2++] = t; break;
            }
        }
    }

    /// <summary>The device-px extent tile (<paramref name="tx"/>, <paramref name="ty"/>) of <paramref name="sliceId"/> rasters
    /// into: the tile cell cut at the content's far edge (bucketed with <see cref="LayerTargetBucket.Dim"/>, capped at the
    /// tile) — on both axes, except for a VIRTUAL list's scroll segment (<see cref="SliceRow.MainAxisGrows"/>), cut on the
    /// cross axis only: its main-axis extent grows as rows realize (a growing tile would re-create its texture mid-scroll).
    /// A non-virtual scroll segment is cut like a static one — a thin heading must not charge a whole 512-row cell.</summary>
    private void SurfaceExtent(int sliceId, short tx, short ty, out short w, out short h)
    {
        w = TileGrid.W; h = TileGrid.H;
        ref SliceRow row = ref _rows[sliceId];
        if (row.ContentBounds.IsEmpty) return;
        bool scroll = (row.Kind == SliceKind.Scroll || row.ScrollVp >= 0) && row.MainAxisGrows;
        bool horizontal = _horizontal[sliceId];
        RectF tile = TileGrid.TileRect(tx, ty);
        RectF c = row.ContentBounds;
        if (!scroll || !horizontal)
        {
            int uw = (int)MathF.Ceiling(MathF.Min(tile.Right, c.Right) - tile.X);
            w = (short)Math.Min(TileGrid.W, LayerTargetBucket.Dim(Math.Max(1, uw)));
        }
        if (!scroll || horizontal)
        {
            int uh = (int)MathF.Ceiling(MathF.Min(tile.Bottom, c.Bottom) - tile.Y);
            h = (short)Math.Min(TileGrid.H, LayerTargetBucket.Dim(Math.Max(1, uh)));
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static long BytesOf(in TileState ts) => LayerTargetBucket.Bytes(ts.SurfW, ts.SurfH);

    /// <summary>Give every queued tile a surface within <paramref name="budgetBytes"/> and write the raster list to
    /// <paramref name="raster"/>: all VISIBLE tiles of all slices first (each slice atomically — it gets every visible
    /// surface it lacks, evicting non-visible tiles as needed, or it degrades), then the ahead band, then the behind rows
    /// (these two only take free budget or tiles not needed this frame). A too-small <paramref name="raster"/> span
    /// degrades the slice whose visible tile did not fit. Last, the turn's <see cref="TrimmedSurfaces"/>: the free slots
    /// (none acquired this turn) idle past <see cref="SurfaceTrimTurns"/>. Zero allocation.</summary>
    public void Resolve(long budgetBytes, Span<TileRaster> raster, out int count)
    {
        count = 0;
        if (budgetBytes < 0) budgetBytes = 0;

        // A budget lowered live: shed tiles no longer needed until within budget (visible ones are never shed).
        while (_residentBytes > budgetBytes)
        {
            int v = PickVictim(minClass: 1);
            if (v < 0) break;
            Evict(v);
        }

        // Order 0 — per contiguous run of one slice, atomically.
        int a = 0;
        while (a < _n0)
        {
            int s = _pend0[a] / TileCap;
            int b = a + 1;
            while (b < _n0 && _pend0[b] / TileCap == s) b++;
            ResolveVisibleRun(a, b, s, budgetBytes, raster, ref count);
            a = b;
        }

        ResolveBand(_pend1, _n1, budgetBytes, raster, ref count);
        ResolveBand(_pend2, _n2, budgetBytes, raster, ref count);
        _scheduled = count;
        CollectTrims();
    }

    /// <summary>Name every FREE surface slot that may still hold a texture and has held no tile for
    /// <see cref="SurfaceTrimTurns"/> turns (<see cref="TrimmedSurfaces"/>). Runs after every acquisition of the turn, so a
    /// slot this turn rasters into is never trimmed under it; a slot a tile holds is not on the free list at all.</summary>
    private void CollectTrims()
    {
        for (int i = 0; i < _freeCount; i++)
        {
            int s = _freeSurfaces[i];
            if (!_surfBacked[s] || (long)_frame - _surfFreeSince[s] < SurfaceTrimTurns) continue;
            if (_trimCount == _trimList.Length) break;   // unreachable: a slot is listed at most once (it is unbacked here)
            _surfBacked[s] = false;
            _trimList[_trimCount++] = s;
            _trimmedTotal++;
        }
    }

    private void ResolveVisibleRun(int a, int b, int s, long budgetBytes, Span<TileRaster> raster, ref int count)
    {
        if (_degraded[s]) { MarkRunDegraded(a, b); return; }

        int need = 0, emit = 0;
        long needBytes = 0;
        for (int i = a; i < b; i++)
        {
            ref TileState ts = ref _tiles[_pend0[i]];
            if (ts.Surface < 0) { need++; needBytes += BytesOf(ts); }
            if (ts.Invalid != InvalidationReason.None) emit++;
        }

        if (count + emit > raster.Length) { Degrade(s); MarkRunDegraded(a, b); return; }

        if (need > 0)
        {
            CountEvictable(minClass: 1, out int evictable, out long evictableBytes);
            if (need > _freeCount + evictable || _residentBytes - evictableBytes + needBytes > budgetBytes)
            {
                Degrade(s); MarkRunDegraded(a, b); return;
            }
        }

        for (int i = a; i < b; i++)
        {
            int t = _pend0[i];
            if (_tiles[t].Surface < 0 && !Acquire(t, budgetBytes, minVictimClass: 1)) { Degrade(s); return; }   // unreachable by the count above
        }
        for (int i = a; i < b; i++)
        {
            int t = _pend0[i];
            ref TileState ts = ref _tiles[t];
            if (ts.Invalid != InvalidationReason.None)
            {
                raster[count++] = new TileRaster(KeyOf(t), ts.Surface, ts.Invalid, TileGrid.OrderVisible, ts.SurfW, ts.SurfH);
                _scheduledFrame[t] = _frame;
            }
        }
    }

    private void MarkRunDegraded(int a, int b)
    {
        for (int i = a; i < b; i++)
        {
            ref TileState ts = ref _tiles[_pend0[i]];
            if (ts.Surface < 0 && ts.Invalid != InvalidationReason.Degraded)
            {
                ts.Invalid = InvalidationReason.Degraded;
                _reasonCounts[(int)InvalidationReason.Degraded]++;
            }
        }
    }

    private void ResolveBand(int[] list, int n, long budgetBytes, Span<TileRaster> raster, ref int count)
    {
        for (int i = 0; i < n; i++)
        {
            int t = list[i];
            int s = t / TileCap;
            if (_degraded[s]) continue;
            ref TileState ts = ref _tiles[t];
            if (ts.Invalid == InvalidationReason.None) continue;
            if (count >= raster.Length) return;
            if (ts.Surface < 0 && !Acquire(t, budgetBytes, minVictimClass: 3)) continue;   // only free budget / tiles unused this frame
            raster[count++] = new TileRaster(KeyOf(t), ts.Surface, ts.Invalid, ts.Order, ts.SurfW, ts.SurfH);
            _scheduledFrame[t] = _frame;
        }
    }

    /// <summary>The backend finished rastering <paramref name="key"/>: its surface now holds current content.</summary>
    public bool MarkRastered(in TileKey key)
    {
        int t = Find(key.SliceId, key.Tx, key.Ty);
        if (t < 0 || _tiles[t].Surface < 0) return false;
        ref TileState ts = ref _tiles[t];
        ts.Invalid = InvalidationReason.None;
        ts.ContentEpoch++;
        LedgerRastered(ts.Surface);
        DamageRastered(t, ts.Surface);
        _rastered++;
        return true;
    }

    /// <summary>Write the tiles <paramref name="sliceId"/> composites this frame to <paramref name="dst"/>: every tile used
    /// this frame whose surface is valid OR is on this frame's raster list (it is rastered in the same submission, before
    /// the composite samples it). Returns the count written.</summary>
    public int CollectPlacements(int sliceId, Span<TilePlacement> dst)
    {
        if (!IsLive(sliceId)) return 0;
        int n = 0, b = sliceId * TileCap;
        for (int i = 0; i < TileCap && n < dst.Length; i++)
        {
            int t = b + i;
            ref TileState ts = ref _tiles[t];
            if (_used[t] && ts.Surface >= 0 && ts.LastUsedFrame == _frame
                && (ts.Invalid == InvalidationReason.None || _scheduledFrame[t] == _frame))
                dst[n++] = PlacementPaint(new TilePlacement(KeyOf(t), ts.Surface, ts.SurfW, ts.SurfH));
        }
        return n;
    }

    /// <summary>VISIBLE (order 0) tiles of non-degraded slices that will composite NOTHING this frame (neither valid nor
    /// rastered this submission) — a blank the user would see. Must be 0: the census reports it as
    /// <c>ExposedTileMissing</c>. The same pass sweeps the stale-tile invariant (<see cref="StaleTiles"/>).</summary>
    public int CountExposedMissing()
    {
        int n = 0;
        BeginStaleSweep();
        for (int t = 0; t < _tiles.Length; t++)
        {
            if (!_used[t]) continue;
            ref TileState ts = ref _tiles[t];
            SweepStale(t, in ts);   // evidence-diagnostics §A.1 — no second walk over the slab
            if (ts.LastUsedFrame != _frame || ts.Order != TileGrid.OrderVisible) continue;
            if (_degraded[t / TileCap]) continue;
            bool shown = ts.Surface >= 0 && (ts.Invalid == InvalidationReason.None || _scheduledFrame[t] == _frame);
            if (!shown) n++;
        }
        return n;
    }

    // ── internals ─────────────────────────────────────────────────────────────────────────────────────────────
    private void Degrade(int s)
    {
        if (_degraded[s]) return;
        _degraded[s] = true;
        _degradedSlices++;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private TileKey KeyOf(int t) => new(t / TileCap, _tx[t], _ty[t]);

    private int Find(int s, short tx, short ty)
    {
        if (!IsLive(s)) return -1;
        int b = s * TileCap;
        for (int i = 0; i < TileCap; i++)
        {
            int t = b + i;
            if (_used[t] && _tx[t] == tx && _ty[t] == ty) return t;
        }
        return -1;
    }

    private int FindOrCreate(int s, short tx, short ty)
    {
        int t = Find(s, tx, ty);
        if (t >= 0) return t;

        int b = s * TileCap;
        int slot = -1;
        for (int i = 0; i < TileCap; i++) if (!_used[b + i]) { slot = b + i; break; }

        if (slot < 0)
        {
            // Recycle: a surfaceless entry not requested this frame first, else the worst resident one not requested
            // this frame (its surface returns to the pool). Entries requested this frame are never recycled.
            long best = long.MinValue;
            for (int i = 0; i < TileCap; i++)
            {
                int c = b + i;
                ref TileState cs = ref _tiles[c];
                if (cs.LastUsedFrame == _frame) continue;
                long score = (cs.Surface < 0 ? 1L << 40 : 0L) + VictimScore(c);
                if (score > best) { best = score; slot = c; }
            }
            if (slot < 0) return -1;
            if (_tiles[slot].Surface >= 0) Evict(slot);
        }

        _used[slot] = true;
        _tx[slot] = tx;
        _ty[slot] = ty;
        _pendingFrame[slot] = int.MinValue;
        _scheduledFrame[slot] = int.MinValue;
        _tiles[slot] = default;
        _tiles[slot].Surface = -1;
        _tiles[slot].Invalid = InvalidationReason.NoTexture;
        _tExtra[slot] = default;
        _tiles[slot].LastUsedFrame = int.MinValue;   // "not yet requested": Request stamps the frame + order next
        SurfaceExtent(s, tx, ty, out _tiles[slot].SurfW, out _tiles[slot].SurfH);
        _reasonCounts[(int)InvalidationReason.NoTexture]++;
        return slot;
    }

    /// <summary>Eviction class: 3 = not needed this frame, 2 = the behind row, 1 = the ahead band, 0 = visible (never).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int ClassOf(int t)
    {
        ref TileState ts = ref _tiles[t];
        if (ts.LastUsedFrame != _frame) return 3;
        return ts.Order == TileGrid.OrderBehind ? 2 : ts.Order == TileGrid.OrderAhead ? 1 : 0;
    }

    /// <summary>LRU age plus distance from the slice's viewport (Chebyshev, in tiles) × <see cref="DistanceWeightFrames"/>.</summary>
    private long VictimScore(int t)
    {
        ref TileState ts = ref _tiles[t];
        long age = (long)_frame - ts.LastUsedFrame;
        if (age < 0) age = 0;
        int s = t / TileCap;
        long dist = 1 << 20;
        if (_hasVp[s])
        {
            bool h = _horizontal[s];
            int m = h ? _tx[t] : _ty[t], c = h ? _ty[t] : _tx[t];
            int dm = m < _vpM0[s] ? _vpM0[s] - m : m > _vpM1[s] ? m - _vpM1[s] : 0;
            int dc = c < _vpC0[s] ? _vpC0[s] - c : c > _vpC1[s] ? c - _vpC1[s] : 0;
            dist = Math.Max(dm, dc);
        }
        return age + dist * DistanceWeightFrames;
    }

    private int PickVictim(int minClass)
    {
        int best = -1;
        long bestScore = long.MinValue;
        for (int t = 0; t < _tiles.Length; t++)
        {
            if (!_used[t] || _tiles[t].Surface < 0) continue;
            int cls = ClassOf(t);
            if (cls < minClass) continue;
            long score = ((long)cls << 48) + VictimScore(t);
            if (score > bestScore) { bestScore = score; best = t; }
        }
        return best;
    }

    private void CountEvictable(int minClass, out int count, out long bytes)
    {
        count = 0; bytes = 0;
        for (int t = 0; t < _tiles.Length; t++)
            if (_used[t] && _tiles[t].Surface >= 0 && ClassOf(t) >= minClass) { count++; bytes += BytesOf(_tiles[t]); }
    }

    private bool Acquire(int t, long budgetBytes, int minVictimClass)
    {
        long bytes = BytesOf(_tiles[t]);
        while (_freeCount == 0 || _residentBytes + bytes > budgetBytes)
        {
            int v = PickVictim(minVictimClass);
            if (v < 0) return false;
            Evict(v);
        }
        _tiles[t].Surface = _freeSurfaces[--_freeCount];
        _surfBacked[_tiles[t].Surface] = true;   // the tile is invalid: it rasters into the slot this turn (the backend creates its texture)
        LedgerAcquired(t, _tiles[t].Surface);
        _resident++;
        _residentBytes += bytes;
        return true;
    }

    private void Evict(int t)
    {
        ReleaseSurface(t);
        ref TileState ts = ref _tiles[t];
        if (ts.Invalid == InvalidationReason.None) _reasonCounts[(int)InvalidationReason.Evicted]++;
        ts.Invalid = InvalidationReason.Evicted;
        _evicted++;
    }

    private void ReleaseSurface(int t)
    {
        ref TileState ts = ref _tiles[t];
        _freeSurfaces[_freeCount++] = ts.Surface;
        _surfFreeSince[ts.Surface] = _frame;   // the texture-trim clock starts now (CollectTrims)
        LedgerReleased(ts.Surface);
        ts.Surface = -1;
        _resident--;
        _residentBytes -= BytesOf(ts);
    }
}
