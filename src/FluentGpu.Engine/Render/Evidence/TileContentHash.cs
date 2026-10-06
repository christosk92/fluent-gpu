using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using FluentGpu.Foundation;
using FluentGpu.Render.Tiles;

namespace FluentGpu.Render.Evidence;

/// <summary>One recorded op of a slice arena as the tile content hash sees it: its byte offset in the arena, its
/// EFFECTIVE footprint (slice-space window DIP — a primitive's <see cref="SliceOpBounds"/> footprint cut by the in-stream
/// clip open around it, a scope push's own rect), its content hash, and whether it opens a scope (clip / stencil clip /
/// inline layer) the ops after it are drawn inside.</summary>
public struct TileOp
{
    public int Pos;
    public RectF Bounds;
    public ulong Hash;
    public bool Scope;
    /// <summary>A clip scope (PushClip / PushStencilClip): it limits what follows and paints nothing itself — left out of
    /// a tile's painted rect. A layer scope composites its content back and is part of it.</summary>
    public bool Clip;
    /// <summary>An inline blur / edge-fade layer scope: what it composites at a pixel depends on its content AROUND the pixel
    /// (the Gaussian's reach), so a sub-tile repaint cannot bound its effect — a tile holding one re-rasters whole
    /// (<see cref="TileDamage"/>).</summary>
    public bool Spread;

    /// <summary>The fold of the hashes of every scope open around the op (outermost first; the arena's own scopes, not
    /// the op itself): moving an op into or out of a clip or a layer keeps its bytes and footprint but not this. Not part
    /// of any want — the sub-tile damage diff pairs ops by it (folded into <see cref="TileOpRec.Hash"/>).</summary>
    public ulong ScopeSig;
}

/// <summary>
/// The per-tile CONTENT hash behind the stale-tile invariant (docs/plans/evidence-diagnostics-implementation.md §A.1).
/// A tile's pixels are a pure function of the ops its replay draws: the ops of its segment whose effective footprint
/// overlaps the tile (the replay culls against the same <see cref="SliceOpBounds"/> numbers) and the scopes open around
/// them. <see cref="TileWant"/> folds exactly those ops' hashes, in stream order, so two rasters of a tile hash equal iff
/// their bytes — as far as that tile can see — are equal. A VALID tile whose want differs from the hash it was rastered
/// for holds pixels the current stream no longer describes: a stale tile, which must never exist.
/// <para><b>Deviation from the design's leaf-span fold (recorded):</b> the design folded the hashes of the span-index
/// LEAVES overlapping a tile. A leaf that straddles a tile boundary and changes only on one side (a hover inside a tall
/// card) would then change the other tile's want although no pixel of it changed — a false stale that damage correctly
/// never repaints. Folding per OP removes that class; the op scan is the one the slice recorder already runs per arena
/// buffer, so the hash work stays one pass over the bytes.</para>
/// <para>Child-slice markers never enter a want: they draw nothing into their parent's tiles (the child composites
/// from its own), so a composite-parameter change carried in a marker (an effect slice's alpha) is not a content change
/// of the parent. Pure, zero-allocation.</para>
/// </summary>
public static class TileContentHash
{
    private const ulong Basis = 14695981039346656037UL;
    private const ulong Prime = 1099511628211UL;

    /// <summary>FNV-1a 64 of one op's bytes (opcode + body), 8 at a time, length-prefixed. Never 0.</summary>
    public static ulong OpHash(ReadOnlySpan<byte> op)
    {
        ulong h = (Basis ^ (uint)op.Length) * Prime;
        var words = MemoryMarshal.Cast<byte, ulong>(op);
        for (int i = 0; i < words.Length; i++) h = (h ^ words[i]) * Prime;
        for (int i = words.Length * 8; i < op.Length; i++) h = (h ^ op[i]) * Prime;
        return h == 0 ? 1UL : h;
    }

    /// <summary>One fold step (order-dependent: stream order is part of the content).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong Fold(ulong h, ulong v) => (h ^ v) * Prime;

    /// <summary>The empty fold (the start of every want and every arena hash).</summary>
    public const ulong Empty = Basis;

    /// <summary>The key a segment's tile wants are computed under: the arena buffer generation the bytes live in, the
    /// segment index and the device grid the tiles are cut on (origin + raster scale). A tile's stored want is reused
    /// exactly while this key holds — a composite-only turn recomputes nothing.</summary>
    public static ulong WantKey(ulong bufGen, int segment, float originX, float originY, float scale)
    {
        ulong h = Fold(Basis, bufGen);
        h = Fold(h, (ulong)(uint)segment);
        h = Fold(h, BitConverter.SingleToUInt32Bits(originX));
        h = Fold(h, BitConverter.SingleToUInt32Bits(originY));
        h = Fold(h, BitConverter.SingleToUInt32Bits(scale));
        return h == 0 ? 1UL : h;
    }

    /// <summary>The effective footprint of an op in the tile's space: slice-space DIP → slice-space device px relative to
    /// the segment's tile origin (<paramref name="originX"/>, <paramref name="originY"/>), the arithmetic the tile replay
    /// culls with. An infinite footprint stays infinite.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static RectF ToTilePx(in RectF dip, float scale, float originX, float originY)
        => dip.IsInfinite ? dip : new RectF(dip.X * scale - originX, dip.Y * scale - originY, dip.W * scale, dip.H * scale);

    /// <summary>
    /// The content hash of tile <paramref name="tilePx"/> (slice-space device px, <see cref="TileGrid.TileRect(int, int)"/>)
    /// of one segment: the segment index, then every scope still open at the segment's first byte
    /// (<paramref name="openScopes"/>: indices into <paramref name="ops"/>) whose footprint overlaps the tile, then every
    /// op in [<paramref name="segStart"/>, <paramref name="segEnd"/>) whose footprint overlaps it — in stream order.
    /// <paramref name="visits"/> = ops examined (each at most once: <c>TileContentHashTests</c> pins it). Never 0.
    /// </summary>
    public static ulong TileWant(ReadOnlySpan<TileOp> ops, ReadOnlySpan<int> openScopes, int segment, int segStart, int segEnd,
        in RectF tilePx, float scale, float originX, float originY, out int visits)
        => TileWant(ops, openScopes, segment, segStart, segEnd, in tilePx, scale, originX, originY, out visits, out _);

    /// <summary><see cref="TileWant(ReadOnlySpan{TileOp}, ReadOnlySpan{int}, int, int, int, in RectF, float, float, float, out int)"/>
    /// that also counts the ops folded (<paramref name="hits"/>: the primitives and scopes the tile draws; a change of it
    /// is a <see cref="InvalidationReason.PrimCount"/> invalidation, a primitive entered or left the tile).</summary>
    public static ulong TileWant(ReadOnlySpan<TileOp> ops, ReadOnlySpan<int> openScopes, int segment, int segStart, int segEnd,
        in RectF tilePx, float scale, float originX, float originY, out int visits, out int hits)
        => TileWant(ops, openScopes, segment, segStart, segEnd, in tilePx, scale, originX, originY, out visits, out hits, out _);

    /// <summary><see cref="TileWant(ReadOnlySpan{TileOp}, ReadOnlySpan{int}, int, int, int, in RectF, float, float, float, out int, out int)"/>
    /// that also returns the tile's PAINTED rect (<paramref name="paint"/>, the tile's own px: 0..W × 0..H): the union of
    /// the footprints of the ops it folds, clip scopes left out, cut by the tile. The replay paints nothing outside it, so
    /// the composite places only that part of the tile. Empty = the tile paints nothing.</summary>
    public static ulong TileWant(ReadOnlySpan<TileOp> ops, ReadOnlySpan<int> openScopes, int segment, int segStart, int segEnd,
        in RectF tilePx, float scale, float originX, float originY, out int visits, out int hits, out RectF paint)
    {
        visits = 0; hits = 0;
        float px0 = float.MaxValue, py0 = float.MaxValue, px1 = float.MinValue, py1 = float.MinValue;
        ulong h = Fold(Basis, 0x5E6UL + (ulong)(uint)segment);
        for (int i = 0; i < openScopes.Length; i++)
        {
            int k = openScopes[i];
            if ((uint)k >= (uint)ops.Length) continue;
            visits++;
            if (Hits(in ops[k], in tilePx, scale, originX, originY))
            {
                h = Fold(h, ops[k].Hash); hits++;
                if (!ops[k].Clip) Grow(in ops[k], in tilePx, scale, originX, originY, ref px0, ref py0, ref px1, ref py1);
            }
        }
        for (int i = FirstAtOrAfter(ops, segStart); i < ops.Length && ops[i].Pos < segEnd; i++)
        {
            visits++;
            if (Hits(in ops[i], in tilePx, scale, originX, originY))
            {
                h = Fold(h, ops[i].Hash); hits++;
                if (!ops[i].Clip) Grow(in ops[i], in tilePx, scale, originX, originY, ref px0, ref py0, ref px1, ref py1);
            }
        }
        paint = px1 > px0 && py1 > py0 ? new RectF(px0 - tilePx.X, py0 - tilePx.Y, px1 - px0, py1 - py0) : default;
        return h == 0 ? 1UL : h;
    }

    /// <summary>
    /// The ops <see cref="TileWant(ReadOnlySpan{TileOp}, ReadOnlySpan{int}, int, int, int, in RectF, float, float, float, out int, out int, out RectF)"/>
    /// folds for tile <paramref name="tilePx"/>, in the same order and by the same overlap test, written to
    /// <paramref name="dst"/> as <see cref="TileOpRec"/>s (hash + footprint in the tile's own px): what the tile's replay
    /// draws, which <see cref="TileDamage.Diff"/> compares against the list its pixels were rastered from. The array grows
    /// when too small (a tile's list only grows once per high-water mark). Returns the count written.
    /// </summary>
    public static int CollectTileOps(ReadOnlySpan<TileOp> ops, ReadOnlySpan<int> openScopes, int segStart, int segEnd,
        in RectF tilePx, float scale, float originX, float originY, ref TileOpRec[] dst)
    {
        int n = 0;
        for (int i = 0; i < openScopes.Length; i++)
        {
            int k = openScopes[i];
            if ((uint)k < (uint)ops.Length && Hits(in ops[k], in tilePx, scale, originX, originY))
                Put(ref dst, ref n, in ops[k], in tilePx, scale, originX, originY);
        }
        for (int i = FirstAtOrAfter(ops, segStart); i < ops.Length && ops[i].Pos < segEnd; i++)
            if (Hits(in ops[i], in tilePx, scale, originX, originY))
                Put(ref dst, ref n, in ops[i], in tilePx, scale, originX, originY);
        return n;
    }

    private static void Put(ref TileOpRec[] dst, ref int n, in TileOp op, in RectF tilePx, float scale, float ox, float oy)
    {
        if (n == dst.Length) Array.Resize(ref dst, Math.Max(16, n * 2));
        byte flags = op.Spread ? TileOpRec.FlagSpread : (byte)0;
        RectF px = default;
        if (op.Bounds.IsInfinite) flags |= TileOpRec.FlagInfinite;
        else
        {
            RectF r = ToTilePx(op.Bounds, scale, ox, oy);
            px = new RectF(r.X - tilePx.X, r.Y - tilePx.Y, r.W, r.H);
        }
        dst[n++] = TileOpRec.Of(op.Hash, op.ScopeSig, in px, flags, (int)tilePx.W, (int)tilePx.H);
    }

    /// <summary>Grow the painted extent by a hit op's footprint cut by the tile (an infinite footprint: the whole tile).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Grow(in TileOp op, in RectF tilePx, float scale, float ox, float oy, ref float x0, ref float y0, ref float x1, ref float y1)
    {
        RectF r = op.Bounds.IsInfinite ? tilePx : ToTilePx(op.Bounds, scale, ox, oy);
        x0 = MathF.Min(x0, MathF.Max(r.X, tilePx.X)); y0 = MathF.Min(y0, MathF.Max(r.Y, tilePx.Y));
        x1 = MathF.Max(x1, MathF.Min(r.Right, tilePx.Right)); y1 = MathF.Max(y1, MathF.Min(r.Bottom, tilePx.Bottom));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool Hits(in TileOp op, in RectF tilePx, float scale, float ox, float oy)
    {
        if (op.Bounds.IsInfinite) return true;
        if (op.Bounds.IsEmpty) return false;
        return ToTilePx(op.Bounds, scale, ox, oy).Overlaps(tilePx);
    }

    /// <summary>The first op whose <see cref="TileOp.Pos"/> ≥ <paramref name="pos"/> (ops are in stream order).</summary>
    public static int FirstAtOrAfter(ReadOnlySpan<TileOp> ops, int pos)
    {
        int lo = 0, hi = ops.Length;
        while (lo < hi)
        {
            int mid = (lo + hi) >> 1;
            if (ops[mid].Pos < pos) lo = mid + 1; else hi = mid;
        }
        return lo;
    }
}
