using System.Runtime.CompilerServices;
using FluentGpu.Foundation;
using FluentGpu.Rhi;

namespace FluentGpu.Render.Tiles;

/// <summary>One op a tile's replay draws, as the sub-tile damage diff sees it (<see cref="TileDamage"/>): its content hash
/// (<see cref="Evidence.TileContentHash.OpHash"/>) folded with the signature of the scopes (clips, layers) it is drawn
/// inside, its effective footprint in the tile's own device px rounded OUT to whole px (half-open, clamped a margin past
/// the tile) and its flags. 24 bytes: the table keeps one per op per resident tile.</summary>
public struct TileOpRec
{
    public const byte FlagInfinite = 1, FlagSpread = 2;
    private const int Margin = 64;

    public ulong Hash;
    public short X0, Y0, X1, Y1;
    public byte Flags;

    /// <summary>The record of an op: <paramref name="hash"/> folded with <paramref name="scopeSig"/> (an op moved into or
    /// out of a clip / layer draws differently with the same bytes and footprint — <see cref="Evidence.TileOp.ScopeSig"/>),
    /// <paramref name="px"/> (tile px) rounded out and clamped to the tile ± a margin.</summary>
    public static TileOpRec Of(ulong hash, ulong scopeSig, in RectF px, byte flags, int tileW, int tileH)
    {
        var r = new TileOpRec { Hash = Evidence.TileContentHash.Fold(hash, scopeSig), Flags = flags };
        if ((flags & FlagInfinite) == 0 && !px.IsEmpty)
        {
            r.X0 = (short)Math.Clamp((int)MathF.Floor(px.X), -Margin, tileW + Margin);
            r.Y0 = (short)Math.Clamp((int)MathF.Floor(px.Y), -Margin, tileH + Margin);
            r.X1 = (short)Math.Clamp((int)MathF.Ceiling(px.Right), -Margin, tileW + Margin);
            r.Y1 = (short)Math.Clamp((int)MathF.Ceiling(px.Bottom), -Margin, tileH + Margin);
        }
        return r;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly bool Same(in TileOpRec o)
        => Hash == o.Hash && Flags == o.Flags && X0 == o.X0 && Y0 == o.Y0 && X1 == o.X1 && Y1 == o.Y1;
}

/// <summary>
/// SUB-TILE DAMAGE (gpu-renderer.md §13.1l): which pixels of a retained tile a content change can reach. A tile's pixel p
/// is the painter-ordered fold of the ops whose footprint covers p (each drawn under the scopes open around it), so two op
/// lists paint the same pixel wherever the ops covering it pair up one-to-one, in order, with identical bytes, footprint
/// and scopes. <see cref="Diff"/> builds a MONOTONE matching of the list the tile's pixels were rastered from against the
/// list the stream now describes (common prefix, common suffix, then a greedy resync with a short lookahead — any
/// monotone matching of equal ops is sound, a better one is only smaller) and returns the union of the footprints of
/// every UNMATCHED op of either list: outside it every pixel is provably unchanged, so the backend may keep it (a
/// PRESERVE load) and re-raster only the damage — clear it, replay the ops that reach it, scissored to it. That is the
/// pixel-identical incremental raster Chromium's cc does per tile ("partial raster": raster the invalidation into the
/// previous tile's resource) and WebRender per picture-cache tile (its dirty rect).
/// <para>Anything the diff cannot bound returns false (re-raster the whole tile): an op with an unbounded footprint in the
/// unmatched set, or a blur / edge-fade layer anywhere in the tile (a Gaussian's output at p reads its content around p,
/// and its scratch would only hold the damage). The caller adds what no op describes (an image cross-fade's rect, pixels
/// an unfaithful raster left behind) and decides when a damage is too large to be worth a partial pass.</para>
/// Pure, zero allocation.
/// </summary>
public static class TileDamage
{
    /// <summary>Partial raster on (default). <c>--fg no-partial-raster</c> turns it off: every invalid tile re-rasters
    /// whole (the A/B arm and the escape hatch).</summary>
    public static bool Enabled = true;

    /// <summary>The partial-present diff uses the precise damage (default): a re-rastered tile dirties only what its raster
    /// wrote, and an item STRUCTURE change dirties only the items that moved. <c>--fg no-precise-present</c> restores the
    /// whole-placement / whole-frame behaviour (the A/B arm).</summary>
    public static bool PrecisePresent = true;

    /// <summary><c>--fg damage-validate</c>: the backend re-rasters every partially rastered tile whole into a shadow
    /// surface and compares the two byte for byte (a mismatch is logged with its extent).</summary>
    public static bool Validate;

    /// <summary><c>--fg damage-log</c>: one line per scheduled tile raster (slice, extent, reason, damage).</summary>
    public static bool Log;

    /// <summary>A damage covering at least this share of its surface re-rasters whole: the PRESERVE load and the per-rect
    /// replay are not worth it for what is nearly the whole tile.</summary>
    public const float MaxPartialShare = 0.6f;

    /// <summary>How far an unmatched op may be from its resync point and still be found (each step looks at most this far
    /// ahead on both lists).</summary>
    public const int Lookahead = 8;

    /// <summary>
    /// The damage (tile px, cut to [0, <paramref name="w"/>) × [0, <paramref name="h"/>), not yet grown by
    /// <see cref="Round"/>) between the op
    /// list a tile's pixels were rastered from (<paramref name="old"/>) and the one its stream now describes
    /// (<paramref name="cur"/>). False = unbounded (re-raster the whole tile). An empty damage with true = the two lists
    /// paint the same pixels (the tile's want moved for a reason that paints nothing — a renumbered segment).
    /// </summary>
    public static bool Diff(ReadOnlySpan<TileOpRec> old, ReadOnlySpan<TileOpRec> cur, int w, int h, out PixelRect damage)
    {
        damage = default;
        for (int i = 0; i < old.Length; i++) if ((old[i].Flags & TileOpRec.FlagSpread) != 0) return false;
        for (int i = 0; i < cur.Length; i++) if ((cur[i].Flags & TileOpRec.FlagSpread) != 0) return false;

        int na = old.Length, nb = cur.Length;
        int p = 0;
        while (p < na && p < nb && old[p].Same(in cur[p])) p++;
        int s = 0;
        while (s < na - p && s < nb - p && old[na - 1 - s].Same(in cur[nb - 1 - s])) s++;
        ReadOnlySpan<TileOpRec> a = old.Slice(p, na - p - s), b = cur.Slice(p, nb - p - s);

        var acc = new Acc(w, h);
        int ia = 0, ib = 0;
        while (ia < a.Length && ib < b.Length)
        {
            if (a[ia].Same(in b[ib])) { ia++; ib++; continue; }
            bool synced = false;
            for (int k = 1; k <= Lookahead && !synced; k++)
            {
                if (ia + k < a.Length && a[ia + k].Same(in b[ib]))
                {
                    for (int q = 0; q < k; q++) if (!acc.Add(in a[ia + q])) return false;   // k ops left the list
                    ia += k; synced = true;
                }
                else if (ib + k < b.Length && a[ia].Same(in b[ib + k]))
                {
                    for (int q = 0; q < k; q++) if (!acc.Add(in b[ib + q])) return false;   // k ops entered it
                    ib += k; synced = true;
                }
            }
            if (synced) continue;
            if (!acc.Add(in a[ia]) || !acc.Add(in b[ib])) return false;   // replaced in place
            ia++; ib++;
        }
        for (; ia < a.Length; ia++) if (!acc.Add(in a[ia])) return false;
        for (; ib < b.Length; ib++) if (!acc.Add(in b[ib])) return false;
        damage = acc.Rect;
        return true;
    }

    /// <summary><paramref name="r"/> (tile px) grown by one pixel of slack and snapped to the even grid (a pixel shader's
    /// derivatives come from 2×2 quads aligned to the target — a damage edge on the quad grid keeps every quad it touches
    /// whole), cut to the surface. Empty stays empty.</summary>
    public static PixelRect Round(in PixelRect r, int w, int h)
    {
        if (r.IsEmpty) return default;
        int l = (r.Left - 1) & ~1, t = (r.Top - 1) & ~1;
        int rr = (r.Right + 2) & ~1, b = (r.Bottom + 2) & ~1;
        l = Math.Clamp(l, 0, w); t = Math.Clamp(t, 0, h);
        rr = Math.Clamp(rr, l, w); b = Math.Clamp(b, t, h);
        return new PixelRect(l, t, rr, b);
    }

    /// <summary><paramref name="r"/> (tile px, fractional) rounded OUT, then <see cref="Round(in PixelRect, int, int)"/>.</summary>
    public static PixelRect Round(in RectF r, int w, int h)
        => r.IsEmpty ? default
            : Round(new PixelRect((int)MathF.Floor(r.X), (int)MathF.Floor(r.Y), (int)MathF.Ceiling(r.Right), (int)MathF.Ceiling(r.Bottom)), w, h);

    /// <summary>The union of two pixel rects (an empty side is ignored).</summary>
    public static PixelRect Union(in PixelRect a, in PixelRect b)
    {
        if (a.IsEmpty) return b;
        if (b.IsEmpty) return a;
        return new PixelRect(Math.Min(a.Left, b.Left), Math.Min(a.Top, b.Top), Math.Max(a.Right, b.Right), Math.Max(a.Bottom, b.Bottom));
    }

    /// <summary>The area of <paramref name="r"/> (0 when empty).</summary>
    public static long Area(in PixelRect r) => r.IsEmpty ? 0L : (long)(r.Right - r.Left) * (r.Bottom - r.Top);

    private struct Acc
    {
        private readonly int _w, _h;
        private int _x0, _y0, _x1, _y1;

        public Acc(int w, int h) { _w = w; _h = h; _x0 = _y0 = int.MaxValue; _x1 = _y1 = int.MinValue; }

        public readonly PixelRect Rect => _x1 > _x0 && _y1 > _y0 ? new PixelRect(_x0, _y0, _x1, _y1) : default;

        /// <summary>Grow by an unmatched op's footprint (cut to the surface); false = unbounded.</summary>
        public bool Add(in TileOpRec op)
        {
            if ((op.Flags & TileOpRec.FlagInfinite) != 0) return false;
            int x0 = Math.Max((int)op.X0, 0), y0 = Math.Max((int)op.Y0, 0), x1 = Math.Min((int)op.X1, _w), y1 = Math.Min((int)op.Y1, _h);
            if (x1 <= x0 || y1 <= y0) return true;   // its footprint misses the surface: it paints nothing here
            _x0 = Math.Min(_x0, x0); _y0 = Math.Min(_y0, y0); _x1 = Math.Max(_x1, x1); _y1 = Math.Max(_y1, y1);
            return true;
        }
    }
}
