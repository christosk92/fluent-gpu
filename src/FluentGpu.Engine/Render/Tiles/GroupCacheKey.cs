using System.Runtime.CompilerServices;
using FluentGpu.Foundation;
using FluentGpu.Rhi;

namespace FluentGpu.Render.Tiles;

/// <summary>The content serial of a tile surface slot — bumped on every raster into it (the backend's surface pool; the
/// headless model counts its own). A group surface keyed by it is re-drawn from the retained result until a tile it
/// samples is re-rastered.</summary>
public interface ITileSerials
{
    uint Serial(int surface);
}

/// <summary>
/// The CONTENT KEY of a composite <see cref="CompositeKind.Group"/> item's surface (gpu-renderer.md §13.1e,
/// docs/plans/composite-fade-groups-implementation.md §2) — engine-side so the D3D12 backend and the headless model compute
/// the same number. A group whose key matches a retained surface is re-drawn from it instead of re-rendered (a page scroll
/// that moves a shelf group rigidly moves its surface origin and every enclosed item by the same whole-pixel delta, so the
/// key — built from positions RELATIVE to the region origin — is unchanged). Invalidation is implicit: anything that
/// changes a pixel of the group changes the key (an enclosed tile re-rastered bumps its serial; a nested scroll changes a
/// relative transform; a hover re-records a tile). Zero allocation.
/// </summary>
public static class GroupCacheKey
{
    /// <summary>A group whose placed footprint (on its slice grid) is at most this many pixels covers its FOOTPRINT —
    /// stable under an ancestor scroll, even partly outside the window. A larger one keeps its clip ∩ the window.</summary>
    public const long MaxFootprintPx = (long)TileGrid.W * TileGrid.H;

    /// <summary>
    /// The window-px region group item <paramref name="it"/>'s surface covers: its placed <see cref="CompositeItem.Footprint"/>
    /// (+ <paramref name="halo"/> is already in it) when that fits <see cref="MaxFootprintPx"/>, else its clip ∩ the
    /// <paramref name="windowW"/>×<paramref name="windowH"/> window grown by <paramref name="halo"/>. Either way its
    /// top-left sits on <see cref="TileGrid.OriginGrid"/> of the group's own space (the item's translation).
    /// </summary>
    public static PixelRect Region(in CompositeItem it, int halo, int windowW, int windowH)
    {
        if (it.Footprint.W > 0f && it.Footprint.H > 0f)
        {
            var fp = new PixelRect((int)MathF.Floor(it.Footprint.X), (int)MathF.Floor(it.Footprint.Y),
                (int)MathF.Ceiling(it.Footprint.Right), (int)MathF.Ceiling(it.Footprint.Bottom));
            PixelRect g = OnSliceGrid(in it, fp);
            if (!g.IsEmpty && (long)(g.Right - g.Left) * (g.Bottom - g.Top) <= MaxFootprintPx) return g;
        }
        int l = 0, t = 0, r = windowW, b = windowH;
        if (!IsUnbounded(it.Clip))
        {
            l = Math.Max(l, (int)MathF.Floor(it.Clip.X) - halo); t = Math.Max(t, (int)MathF.Floor(it.Clip.Y) - halo);
            r = Math.Min(r, (int)MathF.Ceiling(it.Clip.Right) + halo); b = Math.Min(b, (int)MathF.Ceiling(it.Clip.Bottom) + halo);
        }
        return r > l && b > t ? OnSliceGrid(in it, new PixelRect(l, t, r, b)) : default;
    }

    /// <summary><paramref name="r"/> with its top-left floored onto <see cref="TileGrid.OriginGrid"/> of the item's space
    /// (its translation is its slice origin in window px).</summary>
    public static PixelRect OnSliceGrid(in CompositeItem it, PixelRect r)
    {
        if (r.IsEmpty) return r;
        int tx = (int)it.Transform.Dx, ty = (int)it.Transform.Dy;
        return new PixelRect(tx + TileGrid.GridFloor(r.Left - tx), ty + TileGrid.GridFloor(r.Top - ty), r.Right, r.Bottom);
    }

    /// <summary>
    /// The key of group item <paramref name="i"/> rendered into <paramref name="region"/>: the group's blur σ, the region
    /// size, and per enclosed item in painter
    /// order its kind, slice, transform / clip / rounded clip / feathers / source clip relative to the origin, alpha, σ,
    /// blend, every placed tile (column, row, surface, content serial) and a nested group's own key
    /// (<paramref name="itemKeys"/>[k], prepared first). <paramref name="cacheable"/> = false when something enclosed is
    /// redrawn from scratch every turn or depends on what lies outside the group (a degraded Direct segment, an acrylic
    /// backdrop, an uncacheable nested group).
    /// </summary>
    public static ulong Compute<TSerials>(in CompositeFrame frame, int i, in PixelRect region, ref TSerials serials,
        ReadOnlySpan<ulong> itemKeys, out bool cacheable) where TSerials : struct, ITileSerials
    {
        cacheable = true;
        ReadOnlySpan<CompositeItem> items = frame.Items;
        if ((uint)i >= (uint)items.Length) { cacheable = false; return 0; }
        ref readonly CompositeItem g = ref items[i];
        float ox = region.Left, oy = region.Top;
        ulong h = 0x6C0F_0000_0000_0001UL;
        // The group's own alpha / feathers / clip / rounded clip apply when its surface is DRAWN, not rendered into it — they
        // are not part of its content (a fade ramping near a scroller's end keeps the cached surface). Its blur is.
        Mix(ref h, (ulong)(uint)(region.Right - region.Left) << 32 | (uint)(region.Bottom - region.Top));
        Mix(ref h, Bits(g.BlurSigma, 0f));
        int end = Math.Min(items.Length, i + 1 + g.GroupCount);
        for (int k = i + 1; k < end; k++)
        {
            ref readonly CompositeItem it = ref items[k];
            switch (it.Kind)
            {
                case CompositeKind.Direct:
                case CompositeKind.Backdrop:
                    cacheable = false;
                    return 0;
                case CompositeKind.Group:
                {
                    ulong nested = (uint)k < (uint)itemKeys.Length ? itemKeys[k] : 0UL;
                    if (nested == 0UL) { cacheable = false; return 0; }
                    Mix(ref h, 0x9E57UL);
                    Mix(ref h, nested);
                    Mix(ref h, (ulong)(uint)((int)it.Transform.Dx - region.Left) << 32 | (uint)((int)it.Transform.Dy - region.Top));
                    break;
                }
            }
            Mix(ref h, (ulong)(uint)it.Kind << 32 | (uint)it.SliceId);
            Mix(ref h, (ulong)(uint)((int)it.Transform.Dx - region.Left) << 32 | (uint)((int)it.Transform.Dy - region.Top));
            Mix(ref h, Bits(it.Alpha, it.BlurSigma));
            Mix(ref h, (ulong)it.BlendCopy << 8 | it.HasLayer);
            // The scissor as far as it reaches INTO the surface: an ancestor viewport's clip that the whole region lies
            // inside keeps the key as the group moves under it (only pixels inside the region are ever rendered).
            MixRect(ref h, ClipInRegion(it.Clip, in region), ox, oy);
            MixRect(ref h, it.RoundClip, ox, oy);
            Mix(ref h, Bits(it.ClipRadii.TopLeft, 0f));
            MixRect(ref h, it.SourceClip, ox, oy);
            MixFeather(ref h, it.Feather, ox, oy);
            MixFeather(ref h, it.Feather2, ox, oy);
            if (it.Kind == CompositeKind.Group) k = Math.Min(end, k + 1 + it.GroupCount) - 1;   // its own key covers its items
            else if (it.Kind is CompositeKind.Tiles or CompositeKind.Region)
            {
                ReadOnlySpan<TilePlacement> placed = frame.PlacementsOf(it.SliceId);
                Mix(ref h, (ulong)(uint)placed.Length);
                for (int p = 0; p < placed.Length; p++)
                {
                    Mix(ref h, (ulong)(ushort)placed[p].Key.Tx << 48 | (ulong)(ushort)placed[p].Key.Ty << 32 | (uint)placed[p].Surface);
                    Mix(ref h, (ulong)(uint)placed[p].W << 32 | (uint)placed[p].H);
                    Mix(ref h, serials.Serial(placed[p].Surface));
                }
            }
        }
        return h == 0UL ? 1UL : h;   // 0 is "not cacheable"
    }

    /// <summary>
    /// A LEAF self-blur item's two window-px rects (the backend's blur preparation and the headless model share them):
    /// <paramref name="src"/> = the crisp content the blur reads — its recorded <see cref="CompositeItem.SourceClip"/> (the
    /// visible output grown by the kernel's reach), else its clip ∩ the <paramref name="windowW"/>×<paramref name="windowH"/>
    /// window — and <paramref name="region"/> = the scratch it blurs in (the source, or the clip grown by
    /// <see cref="SelfBlurRegion.TapRadius"/>), its top-left floored onto the item's slice origin grid.
    /// </summary>
    public static void BlurRegions(in CompositeItem it, int windowW, int windowH, out PixelRect src, out PixelRect region)
    {
        if (!it.SourceClip.IsEmpty)
        {
            src = new PixelRect((int)MathF.Floor(it.SourceClip.X), (int)MathF.Floor(it.SourceClip.Y),
                (int)MathF.Ceiling(it.SourceClip.Right), (int)MathF.Ceiling(it.SourceClip.Bottom));
            // The recorded source clip was cut against the clip active at RECORD time — unbounded inside a scroll slice, so
            // a blurred row scrolled out of its viewport still names its whole source. The composite draws the blur only
            // inside the item's clip, and source farther than the pipeline's reach (SelfBlurRegion.SupportRadius) from
            // that clip changes no drawn pixel: it is neither assembled nor blurred, and a row wholly outside its viewport
            // blurs nothing (src empty). The region keeps its slice-grid top-left, so the downsample phase is unchanged.
            src = CutToReach(in it, src);
            region = src;
        }
        else
        {
            int halo = SelfBlurRegion.TapRadius(it.BlurSigma);
            int l = 0, t = 0, r = windowW, b = windowH;
            if (!IsUnbounded(it.Clip))
            {
                l = Math.Max(l, (int)MathF.Floor(it.Clip.X)); t = Math.Max(t, (int)MathF.Floor(it.Clip.Y));
                r = Math.Min(r, (int)MathF.Ceiling(it.Clip.Right)); b = Math.Min(b, (int)MathF.Ceiling(it.Clip.Bottom));
            }
            src = r > l && b > t ? new PixelRect(l, t, r, b) : default;
            region = new PixelRect(src.Left - halo, src.Top - halo, src.Right + halo, src.Bottom + halo);
        }
        region = OnSliceGrid(in it, region);
    }

    /// <summary><paramref name="src"/> ∩ the item's clip grown by the blur pipeline's reach (unchanged when the clip is
    /// unbounded); empty when they miss.</summary>
    private static PixelRect CutToReach(in CompositeItem it, PixelRect src)
    {
        if (IsUnbounded(it.Clip) || src.IsEmpty) return src;
        int reach = SelfBlurRegion.SupportRadius(it.BlurSigma);
        int l = Math.Max(src.Left, (int)MathF.Floor(it.Clip.X) - reach), t = Math.Max(src.Top, (int)MathF.Floor(it.Clip.Y) - reach);
        int r = Math.Min(src.Right, (int)MathF.Ceiling(it.Clip.Right) + reach), b = Math.Min(src.Bottom, (int)MathF.Ceiling(it.Clip.Bottom) + reach);
        return r > l && b > t ? new PixelRect(l, t, r, b) : default;
    }

    /// <summary>
    /// The content key of a LEAF self-blur's retained result (gpu-renderer.md §13.1e "Retained self-blur"): σ, the source
    /// and blur regions (<see cref="BlurRegions"/>), the item's whole-px placement and every placed tile (column, row,
    /// surface, content serial). The rects are keyed RELATIVE to the item's whole-px placement: the blurred surface is a
    /// function of what lies inside it, so a blurred leaf moved rigidly by whole pixels — a scroll pose, an ancestor's
    /// whole-px translation — re-draws its retained result at the new place instead of re-blurring identical pixels.
    /// A turn that changed none of it re-draws the retained blur and samples NONE of the tiles —
    /// which is why a backend must never age a tile texture out on "last sampled": the table owns tile texture lifetime
    /// (<see cref="CompositeFrame.TrimSurfaces"/>). Zero allocation.
    /// </summary>
    public static ulong LeafBlur<TSerials>(in CompositeItem it, ReadOnlySpan<TilePlacement> placed, in PixelRect src, in PixelRect region,
        ref TSerials serials) where TSerials : struct, ITileSerials
    {
        ulong h = 0xB1B1_0000_0000_0001UL;
        int tx = (int)it.Transform.Dx, ty = (int)it.Transform.Dy;
        Mix(ref h, (ulong)BitConverter.SingleToUInt32Bits(it.BlurSigma));
        Mix(ref h, (ulong)(uint)(src.Left - tx) << 32 | (uint)(src.Top - ty)); Mix(ref h, (ulong)(uint)(src.Right - tx) << 32 | (uint)(src.Bottom - ty));
        Mix(ref h, (ulong)(uint)(region.Left - tx) << 32 | (uint)(region.Top - ty)); Mix(ref h, (ulong)(uint)(region.Right - tx) << 32 | (uint)(region.Bottom - ty));
        for (int p = 0; p < placed.Length; p++)
        {
            Mix(ref h, (ulong)(ushort)placed[p].Key.Tx << 48 | (ulong)(ushort)placed[p].Key.Ty << 32 | (uint)placed[p].Surface);
            Mix(ref h, serials.Serial(placed[p].Surface));
        }
        return h;
    }

    /// <summary>An item's device-px scissor (empty = unbounded) cut to the surface region — what of it can matter.</summary>
    private static RectF ClipInRegion(in RectF clip, in PixelRect region)
    {
        var r = new RectF(region.Left, region.Top, region.Right - region.Left, region.Bottom - region.Top);
        if (IsUnbounded(clip)) return r;
        RectF c = clip.Intersect(r);
        return c.IsEmpty ? new RectF(0f, 0f, 0f, 0f) : c;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsUnbounded(RectF clip) => clip.W <= 0f && clip.H <= 0f && clip.X == 0f && clip.Y == 0f;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong Bits(float a, float b) => (ulong)BitConverter.SingleToUInt32Bits(a) << 32 | BitConverter.SingleToUInt32Bits(b);

    private static void MixRect(ref ulong h, in RectF r, float ox, float oy)
    {
        if (r.W <= 0f && r.H <= 0f && r.X == 0f && r.Y == 0f) { Mix(ref h, 0xE7UL); return; }
        Mix(ref h, Bits(r.X - ox, r.Y - oy));
        Mix(ref h, Bits(r.W, r.H));
    }

    private static void MixFeather(ref ulong h, in EdgeFeather f, float ox, float oy)
    {
        if (f.IsNone) { Mix(ref h, 0xFEUL); return; }
        MixRect(ref h, f.Rect, ox, oy);
        Mix(ref h, Bits(f.BandLeft, f.BandTop));
        Mix(ref h, Bits(f.BandRight, f.BandBottom));
        Mix(ref h, Bits(f.Radii.TopLeft, f.Radii.TopRight));
        Mix(ref h, Bits(f.Radii.BottomRight, f.Radii.BottomLeft));
        Mix(ref h, Bits((float)f.Falloff, f.Intensity));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Mix(ref ulong h, ulong v)
    {
        h ^= v + 0x9E3779B97F4A7C15UL + (h << 6) + (h >> 2);
        h *= 0xFF51AFD7ED558CCDUL;
    }
}
