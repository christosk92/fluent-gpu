using System.Diagnostics;

namespace FluentGpu.Text;

/// <summary>
/// One glyph-atlas upload flush, as planned by <see cref="GlyphAtlasStore.TryTakeUpload"/>: which rows must be STAGED
/// (memcpy'd into this frame's staging arena right now) and which rows must be COPIED (a GPU copy recorded right now).
/// Rows are always FULL WIDTH — <see cref="RowPitch"/> is the atlas edge — so a backend copies with a placed footprint
/// of <c>Width = RowPitch, Height = CopyRowCount, RowPitch = RowPitch</c> at source offset <see cref="CopyOffset"/> into
/// destination <c>(0, CopyRowStart)</c>. The staging arena is addressed by ONE mapping for the whole frame:
/// atlas row <c>r</c> lives at byte <c>(r - BandBase) * RowPitch</c>.
/// <para><see cref="StageRowCount"/> and <see cref="CopyRowCount"/> differ on purpose: a flush must refresh the arena
/// bytes of every row written since the previous flush (rows an EARLIER flush already recorded a copy for included —
/// that copy reads the arena at GPU-execution time, i.e. after every CPU write of the frame), but it only needs to
/// record a copy for rows no earlier copy of this frame covers.</para>
/// </summary>
public readonly struct GlyphAtlasFlush
{
    internal GlyphAtlasFlush(int bandBase, int rowPitch, int stageRowStart, int stageRowCount, int copyRowStart, int copyRowCount)
    {
        BandBase = bandBase; RowPitch = rowPitch;
        StageRowStart = stageRowStart; StageRowCount = stageRowCount;
        CopyRowStart = copyRowStart; CopyRowCount = copyRowCount;
    }

    /// <summary>The atlas row that arena offset 0 addresses. FIXED for the whole frame (set at its first flush) — that
    /// is what makes many flushes per submit safe (see <see cref="GlyphAtlasStore"/>).</summary>
    public int BandBase { get; }
    /// <summary>Bytes per staged row = the atlas edge (rows are staged full-width).</summary>
    public int RowPitch { get; }
    /// <summary>First row whose arena bytes this flush must (re)write.</summary>
    public int StageRowStart { get; }
    /// <summary>Rows whose arena bytes this flush must (re)write; 0 = nothing to stage.</summary>
    public int StageRowCount { get; }
    /// <summary>First row this flush must record a GPU copy for.</summary>
    public int CopyRowStart { get; }
    /// <summary>Rows this flush must record a GPU copy for; 0 = an earlier flush already covers them.</summary>
    public int CopyRowCount { get; }

    public bool HasStage => StageRowCount > 0;
    public bool HasCopy => CopyRowCount > 0;
    /// <summary>Byte offset of <see cref="StageRowStart"/> inside the frame's staging arena.</summary>
    public int StageOffset => (StageRowStart - BandBase) * RowPitch;
    /// <summary>Byte offset of <see cref="CopyRowStart"/> inside the frame's staging arena (a multiple of
    /// <see cref="RowPitch"/>, hence 512-aligned for any atlas edge that is — D3D12 placed-footprint rule).</summary>
    public int CopyOffset => (CopyRowStart - BandBase) * RowPitch;
    public int StageBytes => StageRowCount * RowPitch;
    public int CopyBytes => CopyRowCount * RowPitch;
}

/// <summary>
/// The CPU side of the glyph/icon coverage atlas: the R8 mirror, the append-only shelf packer, the generational reset,
/// and — the reason this is its own class — the DIRTY-ROW REGION that bounds what each upload flush stages and copies.
/// Backend-agnostic and COM-free (the D3D12 <c>GlyphRenderer</c> and a future Metal backend own only the GPU texture,
/// the staging banks and the copy commands), pure, allocation-free after construction, and render-thread-confined.
///
/// <para><b>Region-tracking invariant.</b> Three clauses, and the upload path is only correct while all three hold:</para>
/// <list type="number">
/// <item><b>Rows, plus a one-row apron.</b> Every atlas ROW written since the last flush is staged and copied, and so
/// is one row above and below it. The apron is not padding-for-safety: a quad's UVs are the cell's exact texel edges,
/// and a LINEAR sampler at the edge reads half a texel PAST it — the packer's 1-texel gutter is only zero on the GPU
/// if it is actually uploaded. Rows go up full width, so the gutter COLUMNS need no separate tracking, and any row a
/// live cell occupies is byte-identical to the mirror across its whole width.</item>
/// <item><b>One fixed band mapping per frame.</b> Arena offset 0 addresses atlas row <c>BandBase</c>, fixed at the
/// frame's first flush at the lowest row that frame can still dirty (the live shelf's top minus the apron, or lower if
/// something below it is already dirty). Many flushes per submit are then safe for the same reason the old full-atlas re-copy was: the
/// CPU writes every staged byte before the single Close+ExecuteCommandLists, so a copy recorded at flush 1 reads the
/// FINAL arena content for ITS rows at execution time. Every flush therefore re-stages the rows written since the
/// previous flush (even ones already copied), but records a copy only for rows not yet covered — so within one frame
/// each dirty row is copied EXACTLY ONCE, and the recorded copy ranges are disjoint.</item>
/// <item><b>Append-only within a frame.</b> The shelf packer only ever moves right and down, and a generational
/// <see cref="Reset"/> happens only at a frame boundary; hence the dirty band never re-bases upward mid-frame and an
/// earlier copy can never be reading rows that later got a DIFFERENT meaning. (If it ever did, <c>TryTakeUpload</c>
/// asserts and re-bases: the re-copy it records covers the same destination rows and executes after the stale ones, so
/// the texture still converges — one unfaithful frame, never permanent corruption.)</item>
/// </list>
///
/// <para>What this buys: the GPU texture is NOT required to equal the mirror everywhere, only over every row a live
/// cell occupies (± the apron). Rows outside that are unreachable — a generational reset drops every cached UV, and
/// the fresh generation re-packs from the top, uploading each row it lands on in full. So a reset needs no full clear
/// upload, and a committed D3D12 resource starts zeroed, which matches the empty mirror.</para>
/// </summary>
public sealed class GlyphAtlasStore
{
    /// <summary>Rows of apron above and below a written band (clause 1). Matches the packer's 1-texel gutter.</summary>
    private const int Apron = 1;

    private readonly int _size;
    private readonly byte[] _cpu;

    private int _shelfX = 1, _shelfY = 1, _shelfH;
    private long _nonZero;
    private int _epoch;

    // Dirty row band [_dirtyLo,_dirtyHi) — rows written since the last flush that landed them. Empty when hi <= lo.
    private int _dirtyLo, _dirtyHi;

    // Per-frame staging band. _bandBase < 0 ⇒ no band opened yet this frame.
    private int _bandBase = -1;
    private int _bandCopiedHi;
    private int _wantRows;
    private int _shortfallRows;
    private int _rebases;

    /// <summary>Allocate a square <paramref name="size"/>×<paramref name="size"/> R8 mirror. A backend that stages with
    /// D3D12 placed footprints wants an edge that is a multiple of 512 (the placement alignment), since a flush's
    /// arena offset is always a whole number of full-width rows.</summary>
    public GlyphAtlasStore(int size)
    {
        if (size <= 2 || (long)size * size > int.MaxValue) throw new ArgumentOutOfRangeException(nameof(size));
        _size = size;
        _cpu = new byte[size * size];
    }

    /// <summary>The atlas edge, in texels (square, R8).</summary>
    public int Size => _size;
    /// <summary>The R8 mirror — the source of every upload, and the authority on what the GPU texture must hold.</summary>
    public ReadOnlySpan<byte> Texels => _cpu;
    /// <summary>Generational reset count (bumped by <see cref="Reset"/>): the epoch every cached UV belongs to.</summary>
    public int Epoch => _epoch;
    /// <summary>Non-zero (inked) texels currently packed — the atlas-occupancy diagnostic.</summary>
    public long NonZeroTexels => _nonZero;
    /// <summary>Rows written since the last flush landed them (an upload is owed).</summary>
    public bool IsDirty => _dirtyHi > _dirtyLo;
    /// <summary>First dirty row (inclusive) — diagnostics/gates.</summary>
    public int DirtyRowStart => _dirtyLo;
    /// <summary>Dirty row count — diagnostics/gates.</summary>
    public int DirtyRowCount => _dirtyHi > _dirtyLo ? _dirtyHi - _dirtyLo : 0;
    /// <summary>The widest band any flush THIS FRAME has needed: the row capacity a staging bank should grow to.
    /// A lifetime high-water would undo a warm-bank shrink on the next tiny upload.</summary>
    public int WantedStagingRows => _wantRows;
    /// <summary>Rows the last flush could NOT land because the staging bank was too small (0 = healthy). They stay
    /// dirty and go out next frame, so the frame just recorded is not a faithful rendering of its text.</summary>
    public int ShortfallRows => _shortfallRows;
    /// <summary>How often the defensive band re-base of clause 3 fired (must stay 0).</summary>
    public int BandRebases => _rebases;
    /// <summary>The next shelf's top row — how far down the packer has walked.</summary>
    public int ShelfRow => _shelfY;
    /// <summary>Occupied shelf extent including the bottom sampling apron, not merely ink coverage.</summary>
    public int OccupiedRowCount => _shelfH == 0 ? 0 : _shelfY + _shelfH + Apron;

    /// <summary>Probe an append without moving the shelf or changing upload state.</summary>
    public bool CanPack(int w, int h) => TryLocate(w, h, out _, out _, out _);

    private bool TryLocate(int w, int h, out int x, out int y, out int shelfHeight)
    {
        x = _shelfX; y = _shelfY; shelfHeight = _shelfH;
        if (w <= 0 || h <= 0 || w > _size - 2 || h > _size - 2) return false;
        if (x + w + Apron > _size) { x = 1; y += shelfHeight + Apron; shelfHeight = 0; }
        return y + h + Apron <= _size;
    }

    /// <summary>Create a detached larger generation before recording any uploads. Existing cell coordinates and
    /// coverage are preserved, but normalized UVs must be invalidated by the backend. Allocation failure leaves
    /// this store untouched. The backend publishes the candidate only after creating its GPU realization and
    /// must retain the old GPU texture/descriptor until their last-use fence completes.</summary>
    public GlyphAtlasStore CreateExpanded(int size)
    {
        if (size <= _size) throw new ArgumentOutOfRangeException(nameof(size));
        if (_bandBase >= 0) throw new InvalidOperationException("Atlas growth must precede upload recording.");
        var candidate = new GlyphAtlasStore(size);
        int rows = OccupiedRowCount;
        for (int row = 0; row < rows; row++)
            _cpu.AsSpan(row * _size, _size).CopyTo(candidate._cpu.AsSpan(row * size, _size));
        candidate._shelfX = _shelfX;
        candidate._shelfY = _shelfY;
        candidate._shelfH = _shelfH;
        candidate._nonZero = _nonZero;
        candidate._epoch = checked(_epoch + 1);
        candidate._dirtyHi = rows;
        return candidate;
    }

    /// <summary>Shelf-pack one R8 coverage bitmap (<paramref name="src"/>, row-major, at least <c>w*h</c> bytes) and
    /// mark its rows dirty. False = the atlas is full: NOTHING was written and the caller must not treat
    /// <paramref name="x"/>/<paramref name="y"/> as a cell (an unpacked entry at 0,0 samples the atlas origin — the bug
    /// that smeared every glyph once the atlas filled); it resets the generation at the next frame boundary and retries.</summary>
    public bool TryPack(ReadOnlySpan<byte> src, int w, int h, out int x, out int y)
    {
        x = 0; y = 0;
        if (!TryLocate(w, h, out int nextX, out int nextY, out int shelfHeight)) return false;
        if (src.Length < (long)w * h) throw new ArgumentException("Coverage buffer is smaller than the glyph.", nameof(src));
        x = nextX; y = nextY;
        long nonZero = _nonZero;
        for (int row = 0; row < h; row++)
        {
            int srcOff = row * w;
            int dstOff = (y + row) * _size + x;
            for (int i = 0; i < w; i++)
            {
                byte v = src[srcOff + i];
                _cpu[dstOff + i] = v;
                if (v != 0) nonZero++;
            }
        }
        _nonZero = nonZero;
        _shelfX = x + w + 1;
        _shelfY = y;
        _shelfH = Math.Max(h, shelfHeight);
        MarkRows(y, h);
        return true;
    }

    /// <summary>Union rows <c>[y-Apron, y+h+Apron)</c> (clamped) into the dirty band.</summary>
    private void MarkRows(int y, int h)
    {
        int lo = y - Apron; if (lo < 0) lo = 0;
        int hi = y + h + Apron; if (hi > _size) hi = _size;
        if (_dirtyHi <= _dirtyLo) { _dirtyLo = lo; _dirtyHi = hi; return; }
        if (lo < _dirtyLo) _dirtyLo = lo;
        if (hi > _dirtyHi) _dirtyHi = hi;
    }

    /// <summary>Generational reset: empty the mirror and rewind the packer. Call ONLY at a frame boundary (clause 3) —
    /// mid-frame it would re-assign cells underneath UVs already emitted this frame. The GPU texture is deliberately
    /// NOT cleared: every cached UV dies with the generation, and the fresh generation re-uploads every row it lands
    /// on in full (see the class invariant).</summary>
    public void Reset()
    {
        Array.Clear(_cpu);
        _shelfX = 1; _shelfY = 1; _shelfH = 0;
        _nonZero = 0;
        _dirtyLo = 0; _dirtyHi = 0;
        _bandBase = -1; _bandCopiedHi = 0;
        _epoch++;
    }

    /// <summary>Open a new frame: forget the previous frame's band mapping (arena offset 0 is re-based at the next
    /// flush) and clear the per-frame shortfall. The staging bank for the frame about to record is fenced at this
    /// point, which is the only moment a backend may grow or swap it.</summary>
    public void BeginFrame()
    {
        _bandBase = -1;
        _bandCopiedHi = 0;
        _shortfallRows = 0;
        _wantRows = 0;
    }

    /// <summary>Plan (and consume) one upload flush against a staging bank of <paramref name="stagingRows"/> full-width
    /// rows. False = nothing to do: either no row was written since the last flush, or the bank cannot hold another row
    /// of this frame's band (then <see cref="ShortfallRows"/> is non-zero and <see cref="WantedStagingRows"/> names the
    /// size to grow to at the next frame boundary). The caller must stage <see cref="GlyphAtlasFlush.StageRowCount"/>
    /// rows via <see cref="StageInto"/> and record a copy for <see cref="GlyphAtlasFlush.CopyRowCount"/> rows.</summary>
    public bool TryTakeUpload(int stagingRows, out GlyphAtlasFlush flush)
    {
        flush = default;
        if (_dirtyHi <= _dirtyLo || stagingRows <= 0) return false;

        if (_bandBase < 0)
        {
            // Open the frame's band at the LOWEST row this frame can still dirty, not merely the lowest dirty row: the
            // packer keeps filling the CURRENT shelf (top = ShelfRow) before moving down, so a later flush of this same
            // frame can mark rows as low as ShelfRow−Apron. Basing on _dirtyLo alone would let that later mark fall
            // BELOW the base — the one way clause 3 could be broken without any mid-frame repack (it takes a previous
            // frame's clamped tail to sit above the live shelf, which the shortfall path can produce). Costs at most
            // the current shelf's height in unused arena rows; buys a provably dead re-base branch.
            int floor = _shelfY - Apron; if (floor < 0) floor = 0;
            _bandBase = _dirtyLo < floor ? _dirtyLo : floor;
            _bandCopiedHi = _bandBase;
        }
        else if (_dirtyLo < _bandBase)
        {
            // Clause 3 violated (append-only + frame-boundary-only resets say this cannot happen). Re-base and
            // re-copy the whole band: the copies already recorded this frame then read the re-based layout — wrong
            // bytes for one frame — but this flush's copy covers the same destination rows and executes AFTER them,
            // so the texture converges. Loud (BandRebases + the assert), never silent.
            Debug.Assert(false, "glyph atlas: a dirty row appeared above the frame's staging band base");
            _rebases++;
            _bandBase = _dirtyLo; _bandCopiedHi = _dirtyLo;
        }

        int limit = _bandBase + stagingRows;
        int hi = _dirtyHi < limit ? _dirtyHi : limit;
        if (hi <= _dirtyLo)
        {
            _shortfallRows = _dirtyHi - _dirtyLo;
            if (_dirtyHi - _bandBase > _wantRows) _wantRows = _dirtyHi - _bandBase;
            return false;
        }

        int copyLo = _dirtyLo > _bandCopiedHi ? _dirtyLo : _bandCopiedHi;
        flush = new GlyphAtlasFlush(_bandBase, _size, _dirtyLo, hi - _dirtyLo, copyLo, hi > copyLo ? hi - copyLo : 0);
        if (hi > _bandCopiedHi) _bandCopiedHi = hi;
        if (hi - _bandBase > _wantRows) _wantRows = hi - _bandBase;
        if (hi < _dirtyHi)
        {
            _shortfallRows = _dirtyHi - hi;
            if (_dirtyHi - _bandBase > _wantRows) _wantRows = _dirtyHi - _bandBase;
            _dirtyLo = hi;                       // the tail stays dirty; the next frame's band starts there
        }
        else { _dirtyLo = 0; _dirtyHi = 0; }
        return true;
    }

    /// <summary>Copy the flush's staged rows out of the mirror into <paramref name="arena"/> — the WHOLE mapped staging
    /// bank of the frame (at least <c>stagingRows * Size</c> bytes), written at <see cref="GlyphAtlasFlush.StageOffset"/>.
    /// One contiguous block copy: rows are full-width in both the mirror and the arena.</summary>
    public void StageInto(in GlyphAtlasFlush flush, Span<byte> arena)
    {
        if (flush.StageRowCount <= 0) return;
        _cpu.AsSpan(flush.StageRowStart * _size, flush.StageBytes).CopyTo(arena.Slice(flush.StageOffset, flush.StageBytes));
    }
}
