using System.Runtime.CompilerServices;
using FluentGpu.Foundation;
using FluentGpu.Scroll.Motion;

namespace FluentGpu.Render.Tiles;

// The retained tiled content layer ("picture cache") — docs/plans/scroll-gpu-retained-tiles-implementation.md §A.
// This file holds the vocabulary (slice kinds, invalidation reasons, keys, per-tile state, the slice descriptor) and the
// pure needed-set decision (TileGrid.Needed). No GPU, no TerraFX: the headless TileSuite gates drive every decision here.

/// <summary>What a slice holds (§A.2). <see cref="Static"/> = everything not under a scroll root or an effect, split into
/// an "under" and an "over" band by paint order; <see cref="Scroll"/> = one per scroll root, recorded in CONTENT space and
/// composited at the posed, device-snapped offset; <see cref="Effect"/> = a node with compositor-time parameters (sticky,
/// collapse/hero, parallax, fade, group opacity, self-blur, edge fade, acrylic).</summary>
public enum SliceKind : byte { Static, Scroll, Effect }

/// <summary>Why a tile must be re-rastered (§A.3). <see cref="None"/> = the tile's surface holds its current content.
/// Every reason is counted per frame by the tile census, so a surprise re-raster names its cause.</summary>
public enum InvalidationReason : byte
{
    /// <summary>Valid — the surface holds the tile's current content.</summary>
    None,
    /// <summary>First request: the tile has never been rastered.</summary>
    NoTexture,
    /// <summary>The ops the tile's replay draws changed in place (same count, different bytes: a colour, an inherited
    /// alpha, a glyph, a 1-px move inside the tile) — its content want differs from what it was rastered for; or an
    /// image cross-fade the image clock passed through.</summary>
    Content,
    /// <summary>The NUMBER of ops the tile's replay draws changed: a primitive entered or left it (a row realized into or
    /// parked out of it, a node that appeared, vanished or moved across it).</summary>
    PrimCount,
    /// <summary>Coverage grew into a tile that was only partially covered when it was last rastered.</summary>
    ValidRectChanged,
    /// <summary>The slice's raster scale changed (DPI change, pinch settle) — whole slice.</summary>
    ScaleChanged,
    /// <summary>The slice's device origin, residual fraction or kind changed — whole slice.</summary>
    SliceGeometry,
    /// <summary>The window background or theme changed — whole slice.</summary>
    BackgroundOrTheme,
    /// <summary>The budget evicted the tile's surface; it is re-rastered when next needed.</summary>
    Evicted,
    /// <summary>The slice could not fit its visible tiles in the budget this frame and is rastered directly instead
    /// (today's cost, never blank).</summary>
    Degraded,
}

/// <summary>A tile's identity: the <see cref="SliceTable"/> slot of its slice and its column/row in that slice's
/// <see cref="TileGrid"/> (tile (Tx, Ty) covers slice-space device px [Tx·W, (Tx+1)·W) × [Ty·H, (Ty+1)·H)).</summary>
public readonly record struct TileKey(int SliceId, short Tx, short Ty);

/// <summary>Where a slice's space sits on the device grid (§A.7): <see cref="OriginX"/>/<see cref="OriginY"/> = the slice
/// origin FLOORED to whole device px; <see cref="ResidualX"/>/<see cref="ResidualY"/> = the dropped fraction, baked into
/// the raster so text keeps the same subpixel phase at rest and in motion; <see cref="Scale"/> = the raster scale (DPI).
/// The composite then samples 1:1 (point) because the poser's translate is already whole-device-pixel.</summary>
public readonly record struct SliceFrame(int OriginX, int OriginY, float ResidualX, float ResidualY, float Scale);

/// <summary>One tile's retained state. <see cref="Surface"/> = the surface slot holding its pixels (−1 = none);
/// <see cref="Invalid"/> = why it must be re-rastered (<see cref="InvalidationReason.None"/> = valid);
/// <see cref="ContentEpoch"/> = bumped on every completed raster; <see cref="LastUsedFrame"/> = the last frame that
/// requested it (LRU); <see cref="Order"/> = its needed-set order that frame (0 visible, 1 ahead, 2 behind);
/// <see cref="CoveredLo"/>/<see cref="CoveredHi"/> = the main-axis slice-space span coverage reached inside the tile when
/// it was last scheduled for raster — coverage growing past it is <see cref="InvalidationReason.ValidRectChanged"/>.</summary>
public struct TileState
{
    public int Surface;
    public InvalidationReason Invalid;
    public uint ContentEpoch;
    public int LastUsedFrame;
    public byte Order;
    public float CoveredLo, CoveredHi;
    /// <summary>The device-px extent of the surface the tile rasters into (a <see cref="SliceKind.Effect"/> slice's tile
    /// holds only its content's bucketed extent — a region surface — every other tile the full
    /// <see cref="TileGrid.W"/>×<see cref="TileGrid.H"/>). Budgeted by bytes.</summary>
    public short SurfW, SurfH;
}

/// <summary>A slice's descriptor (§A.2). <see cref="Id"/> is its <see cref="SliceTable"/> slot; (<see cref="NodeIndex"/>,
/// <see cref="Gen"/>, <see cref="Sub"/>) the scene node it was cut at plus its role/segment (see
/// <c>Render.SliceRecorder</c>: a node carries its main slice, a scrollbar thumb, an item band; a slice whose stream holds
/// child markers is split into painter-ordered SEGMENTS, segment k keyed Sub = k·8 + role). The Draw/SpanIndex ranges
/// index the per-turn arrays the slice recorder (P1) fills: this segment's own opcode stream and the per-clean-span
/// <see cref="SliceSpan"/> index a per-tile replay culls against. <see cref="ScrollVp"/> = the scroll viewport node a <see cref="SliceKind.Scroll"/> slice is posed by
/// (−1 otherwise).</summary>
public struct SliceRow
{
    public int Id, NodeIndex;
    public uint Gen;
    public int Sub;
    public SliceKind Kind;
    public SliceFrame Frame;
    public RectF ContentBounds;
    public int DrawListStart, DrawListLength;
    public int SpanIndexStart, SpanIndexCount;
    public int ScrollVp;
    /// <summary>Where the WHOLE slice arena this segment belongs to starts in <c>CompositeFrame.SliceStreams</c>: the bytes
    /// [<see cref="StreamBase"/>, <see cref="DrawListStart"/>) are the segment's PREFIX — the scope ops (clips, stencil
    /// clips, inline layers) still open at the segment's first byte are reconstructed from it by a per-tile replay.</summary>
    public int StreamBase;
    /// <summary>The segment's identity (its slot, index, the markers bracketing it, the scroll base it records under):
    /// a change invalidates the whole segment (<see cref="InvalidationReason.SliceGeometry"/>) — its byte range now names
    /// different content even though no primitive in it was damaged.</summary>
    public ulong Identity;
    /// <summary>A VIRTUAL list's scroll segment: its content grows along the main axis as rows realize, so its tiles keep
    /// whole cells on that axis (<c>SliceTable.SurfaceExtent</c>). False for every other segment — cut at its painted
    /// bounds on both axes.</summary>
    public bool MainAxisGrows;
}

/// <summary>One entry of a slice's span index (§A.4): a clean subtree's slice-space bounds (device px, halos included) and
/// its byte/command range in the slice's stream. Entries are PRE-ORDER (a nested entry follows its ancestor), so a replay
/// may skip every entry that misses its tile and descend into those that hit it. <see cref="HasMarker"/> = the range
/// holds a child slice's marker — never skipped, since the child's composite position is independent of this range's.
/// <see cref="Depth"/> = the node's depth below the slice root (1 = a direct child).</summary>
public readonly record struct SliceSpan(RectF Bounds, int ByteStart, int ByteLength, int SortStart, int SortCount, bool HasMarker, int Depth = 1);

/// <summary>The fixed tile grid and the pure needed-set decision (§A.4).</summary>
public static class TileGrid
{
    /// <summary>Tile size in device px. BGRA8 → <see cref="TileBudget.TileBytes"/> = 2 MiB per tile.</summary>
    public const int W = 1024, H = 512;

    /// <summary>Every raster target's origin sits on this grid of its SLICE's device px (tile origins, degraded / blurred
    /// region scratches, inline-group scratches): a pixel shader's derivatives come from 2×2 pixel quads aligned to the
    /// render target, and a blur's 2× downsample chain samples pairs aligned to it too, so two targets whose origins differ
    /// by an odd pixel (or by a non-multiple of the downsample factor) raster a rounded corner or a blur differently. With
    /// every origin on the grid a tile, a direct region and an inline group see the same quads and the same blur phase —
    /// the bit-identity a composited tile owes a direct raster. 64 also keeps a static segment's origin stable while its
    /// bounds move a few px.</summary>
    public const int OriginGrid = 64;

    /// <summary><paramref name="px"/> floored to <see cref="OriginGrid"/>.</summary>
    public static int GridFloor(int px) => (int)Math.Floor(px / (double)OriginGrid) * OriginGrid;

    /// <summary>Needed-set order of a visible tile (rastered in the same submission that composites it).</summary>
    public const byte OrderVisible = 0;
    /// <summary>Needed-set order of a tile in the ahead-of-motion band (|v|·<see cref="MotionFeel.LookaheadS"/>).</summary>
    public const byte OrderAhead = 1;
    /// <summary>Needed-set order of the one tile row retained behind the viewport.</summary>
    public const byte OrderBehind = 2;

    /// <summary>The needed tiles of one slice this frame, written to <paramref name="dst"/> grouped by order: every
    /// VISIBLE tile first (order 0, row-major), then the AHEAD-of-motion band (order 1, nearest row first) of length
    /// |<paramref name="velocity"/>|·<see cref="MotionFeel.LookaheadS"/> on the leading side, then ONE tile row retained
    /// BEHIND (order 2) — on the trailing side while moving, on both sides at rest (WebRender's one-tile retained margin).
    /// Every row is clamped to coverage: a main-axis tile row that does not overlap [<paramref name="coverStart"/>,
    /// <paramref name="coverEnd"/>) holds no realized rows and is never requested (this agrees with the poser's clamp).
    /// <para>Units: <paramref name="viewportSlice"/>, coverage and velocity are slice-space device px (velocity in px/s,
    /// positive = increasing offset). <paramref name="horizontal"/> selects X as the scroll (main) axis. When
    /// <paramref name="dst"/> is too small the list is truncated in priority order (visible first).</para>
    /// Zero allocation.</summary>
    public static void Needed(in RectF viewportSlice, double velocity, in MotionFeel feel, double coverStart, double coverEnd,
        bool horizontal, Span<TileKey> dst, out int count, int sliceId)
        => Needed(viewportSlice, velocity, feel, coverStart, coverEnd, horizontal, dst, Span<byte>.Empty, out count, sliceId);

    /// <summary><see cref="Needed(in RectF, double, in MotionFeel, double, double, bool, Span{TileKey}, out int, int)"/> that
    /// also writes each key's order (0/1/2) to the parallel <paramref name="order"/> span (ignored when empty).</summary>
    public static void Needed(in RectF viewportSlice, double velocity, in MotionFeel feel, double coverStart, double coverEnd,
        bool horizontal, Span<TileKey> dst, Span<byte> order, out int count, int sliceId)
    {
        count = 0;
        if (viewportSlice.IsEmpty || !(coverEnd > coverStart)) return;

        int mainSize = horizontal ? W : H, crossSize = horizontal ? H : W;
        double vpLo = horizontal ? viewportSlice.X : viewportSlice.Y;
        double vpHi = horizontal ? viewportSlice.Right : viewportSlice.Bottom;
        double crLo = horizontal ? viewportSlice.Y : viewportSlice.X;
        double crHi = horizontal ? viewportSlice.Bottom : viewportSlice.Right;

        int c0 = FirstIndex(crLo, crossSize), c1 = LastIndex(crHi, crossSize);
        int m0 = FirstIndex(vpLo, mainSize), m1 = LastIndex(vpHi, mainSize);
        // Rows that overlap coverage (half-open): the only requestable rows.
        int cov0 = FirstIndex(coverStart, mainSize), cov1 = LastIndex(coverEnd, mainSize);

        var w = new Writer(dst, order, sliceId, horizontal, c0, c1, cov0, cov1);

        // order 0 — visible rows
        for (int m = m0; m <= m1; m++) w.Row(m, OrderVisible);

        // order 1 — ahead of motion
        double lead = Math.Abs(velocity) * feel.LookaheadS;
        if (velocity > 0.0 && lead > 0.0)
        {
            int a1 = LastIndex(vpHi + lead, mainSize);
            for (int m = m1 + 1; m <= a1; m++) w.Row(m, OrderAhead);
        }
        else if (velocity < 0.0 && lead > 0.0)
        {
            int a0 = FirstIndex(vpLo - lead, mainSize);
            for (int m = m0 - 1; m >= a0; m--) w.Row(m, OrderAhead);
        }

        // order 2 — one retained row behind (trailing side while moving; both sides at rest)
        if (velocity >= 0.0) w.Row(m0 - 1, OrderBehind);
        if (velocity <= 0.0) w.Row(m1 + 1, OrderBehind);

        count = w.Count;
    }

    /// <summary>The slice-space device-px rect tile (<paramref name="tx"/>, <paramref name="ty"/>) covers.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static RectF TileRect(int tx, int ty) => new(tx * (float)W, ty * (float)H, W, H);

    /// <summary>The slice-space device-px rect <paramref name="key"/> covers.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static RectF TileRect(in TileKey key) => TileRect(key.Tx, key.Ty);

    /// <summary>Index of the tile containing coordinate <paramref name="lo"/> (floor division; negative-safe).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int FirstIndex(double lo, int size) => (int)Math.Floor(lo / size);

    /// <summary>Index of the last tile a HALF-OPEN span ending at <paramref name="hi"/> touches (hi on a tile boundary
    /// does not reach the next tile).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int LastIndex(double hi, int size) => (int)Math.Ceiling(hi / size) - 1;

    private ref struct Writer
    {
        private readonly Span<TileKey> _dst;
        private readonly Span<byte> _order;
        private readonly int _slice, _c0, _c1, _cov0, _cov1;
        private readonly bool _horizontal;
        public int Count;

        public Writer(Span<TileKey> dst, Span<byte> order, int slice, bool horizontal, int c0, int c1, int cov0, int cov1)
        {
            _dst = dst; _order = order; _slice = slice; _horizontal = horizontal;
            _c0 = c0; _c1 = c1; _cov0 = cov0; _cov1 = cov1; Count = 0;
        }

        public void Row(int m, byte ord)
        {
            if (m < _cov0 || m > _cov1 || m < short.MinValue || m > short.MaxValue) return;   // no realized rows ⇒ never requested
            for (int c = _c0; c <= _c1; c++)
            {
                if (c < short.MinValue || c > short.MaxValue) continue;
                if (Count >= _dst.Length) return;
                _dst[Count] = _horizontal ? new TileKey(_slice, (short)m, (short)c) : new TileKey(_slice, (short)c, (short)m);
                if (Count < _order.Length) _order[Count] = ord;
                Count++;
            }
        }
    }
}
