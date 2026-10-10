using System.Runtime.CompilerServices;
using FluentGpu.Foundation;
using FluentGpu.Rhi;

namespace FluentGpu.Render.Tiles;

/// <summary>One thing an unblurred group surface is composited from: the surface px it can paint (relative to the group
/// region's top-left, cut to the item's scissor and the region) and the signature of WHAT it paints there (a tile's
/// surface + raster serial, a prepared surface's content key).</summary>
public struct GroupEntry
{
    public PixelRect Rect;
    public ulong Sig;
}

/// <summary>
/// The DELTA of an unblurred composite <see cref="CompositeKind.Group"/> surface between two turns
/// (gpu-renderer.md §13.1e): a group whose content key (<see cref="Describe"/>) missed only because something INSIDE it
/// was re-rastered — the karaoke wipe sweeping one lyric line inside a faded rail — is repaired in place over the pixels
/// that changed instead of re-rendered whole.
/// <para>The group surface is a pure function of its enclosed items in painter order: cleared, then each item drawn under
/// its scissor. <see cref="Describe"/> splits that function into a SHAPE — every enclosed item's composite parameters and
/// placement geometry, which decide WHERE each item paints and how — and ENTRIES — per tile placement and per prepared
/// surface, the rect it can paint and the signature of what it paints. Two turns with the same shape and region size
/// differ only inside the rects of the entries whose signature changed (<see cref="Diff"/>); clearing those rects and
/// redrawing every item scissored to them reproduces the full render bit for bit, and every pixel outside them is
/// already what a full render would write. A changed shape, a different entry count, or a dirty area past
/// <see cref="MaxDirtyShare"/> of the region falls back to the full render.</para>
/// <para>A BLURRED group is never repaired this way (its blur spreads a change past the entry rect); the caller checks.
/// Zero allocation once the caller's entry buffer has grown.</para>
/// </summary>
public static class GroupDelta
{
    /// <summary>Past this share of the region's area the repair costs about what the full render does (the full render
    /// clears with the load op and draws once): the caller renders whole.</summary>
    public const float MaxDirtyShare = 0.5f;

    /// <summary>At most this many dirty rects are repaired one by one; more are merged into their bounding box.</summary>
    public const int MaxDirtyRects = 8;

    /// <summary>The signature of an enclosed item that has no prepared surface this turn (it draws nothing).</summary>
    private const ulong NoSurface = 0x4E05_0000_0000_0001UL;

    /// <summary>The signature of a prepared surface that has no content key (re-drawn from scratch). Where it paints, the
    /// group is not cacheable (<see cref="Describe"/> returns key 0), so it is never compared.</summary>
    private const ulong Unkeyed = 0x4E05_0000_0000_0002UL;

    /// <summary>
    /// THE description of group item <paramref name="i"/> rendered into <paramref name="region"/> (window px): its SHAPE
    /// (returned), its ENTRIES (written to <paramref name="entries"/>, grown as needed; <paramref name="count"/> = how many)
    /// and its CONTENT KEY (<paramref name="key"/>; 0 = not cacheable). The key is a hash of the shape and of every entry that
    /// paints (rect and signature), so for one shape "the diff is empty" and "the key is unchanged" are the same statement:
    /// a retained surface is re-used exactly when a repair would touch nothing. Nothing that cannot reach a pixel of the
    /// surface (a tile painted wholly outside its item's scissor, a nested surface outside it) moves either.
    /// <para><paramref name="itemKeys"/> / <paramref name="itemRegions"/> = the content key and window-px region of every
    /// enclosed item already prepared this turn (a self-blurred leaf, a nested group, a low-resolution segment) and
    /// <paramref name="itemSurfaces"/> whether it was (≥ 0; an item with no surface draws nothing this turn — a blurred row
    /// out of its viewport). Not cacheable: a Direct segment, an acrylic backdrop or a posed image enclosed (redrawn every
    /// turn, or dependent on what lies outside the group), or a prepared surface without a content key that paints.</para>
    /// <para>The shape keys each item's scissor CUT TO THE REGION (only pixels inside it are ever rendered), so a group moved
    /// rigidly under an ancestor viewport's clip keeps its shape and its key. Positions are relative to the region's
    /// top-left. The group's own alpha / feathers / clip apply when its surface is drawn and are part of neither; its own
    /// blur is part of the key.</para>
    /// </summary>
    public static ulong Describe<TSerials>(in CompositeFrame frame, int i, in PixelRect region, ref TSerials serials,
        ReadOnlySpan<ulong> itemKeys, ReadOnlySpan<PixelRect> itemRegions, ReadOnlySpan<int> itemSurfaces,
        ref GroupEntry[] entries, out int count, out ulong key)
        where TSerials : struct, ITileSerials
    {
        count = 0;
        key = 0UL;
        ReadOnlySpan<CompositeItem> items = frame.Items;
        int w = region.Right - region.Left, h = region.Bottom - region.Top;
        ulong shape = 0x6D17_0000_0000_0001UL;
        Mix(ref shape, (ulong)(uint)w << 32 | (uint)h);
        if ((uint)i >= (uint)items.Length || w <= 0 || h <= 0) return shape;
        bool cacheable = true;
        int end = Math.Min(items.Length, i + 1 + items[i].GroupCount);
        // the group's IDENTITY: two groups with the same relative layout (made only of nested groups, whose own slice is −1)
        // must not share a memo — each would evict the other's retained surface every turn
        int first = -1;
        for (int k = i + 1; k < end && first < 0; k++) first = items[k].SliceId;
        Mix(ref shape, (ulong)(uint)items[i].GroupCount << 32 | (uint)first);
        for (int k = i + 1; k < end; k++)
        {
            ref readonly CompositeItem it = ref items[k];
            if (it.Kind is CompositeKind.Direct or CompositeKind.Backdrop or CompositeKind.Image) cacheable = false;
            int dx = (int)it.Transform.Dx - region.Left, dy = (int)it.Transform.Dy - region.Top;
            Mix(ref shape, (ulong)(uint)it.Kind << 32 | (uint)it.SliceId);
            Mix(ref shape, (ulong)(uint)dx << 32 | (uint)dy);
            Mix(ref shape, Bits(it.Alpha, it.BlurSigma));
            Mix(ref shape, (ulong)it.BlendCopy << 16 | (ulong)it.HasLayer << 8 | it.LowResDown);
            PixelRect sci = ScissorIn(in it, in region);
            Mix(ref shape, (ulong)(uint)sci.Left << 32 | (uint)sci.Top);
            Mix(ref shape, (ulong)(uint)sci.Right << 32 | (uint)sci.Bottom);
            MixRect(ref shape, it.RoundClip, region.Left, region.Top);
            Mix(ref shape, Bits(it.ClipRadii.TopLeft, it.ClipRadii.TopRight));
            Mix(ref shape, Bits(it.ClipRadii.BottomRight, it.ClipRadii.BottomLeft));
            MixRect(ref shape, it.SourceClip, region.Left, region.Top);
            MixFeather(ref shape, it.Feather, region.Left, region.Top);
            MixFeather(ref shape, it.Feather2, region.Left, region.Top);
            bool prepared = it.Kind is CompositeKind.Group or CompositeKind.Direct
                || (it.Kind is CompositeKind.Tiles or CompositeKind.Region && it.BlurSigma > 0f);
            if (prepared)
            {
                // a surface prepared this turn: where it draws and its content key (one entry; nothing when it has none)
                if ((uint)k < (uint)itemSurfaces.Length && itemSurfaces[k] >= 0)
                {
                    PixelRect r = (uint)k < (uint)itemRegions.Length ? itemRegions[k] : default;
                    var rel = new PixelRect(r.Left - region.Left, r.Top - region.Top, r.Right - region.Left, r.Bottom - region.Top);
                    PixelRect at = Intersect(in rel, in sci);
                    ulong sig = (uint)k < (uint)itemKeys.Length ? itemKeys[k] : 0UL;
                    if (sig == 0UL && !at.IsEmpty) cacheable = false;
                    Add(ref entries, ref count, at, sig != 0UL ? sig : Unkeyed);
                }
                else Add(ref entries, ref count, default, NoSurface);
                if (it.Kind == CompositeKind.Group) k = Math.Min(end, k + 1 + it.GroupCount) - 1;   // its key covers its items
                continue;
            }
            if (it.Kind is CompositeKind.Tiles or CompositeKind.Region)
            {
                ReadOnlySpan<TilePlacement> placed = frame.PlacementsOf(it.SliceId);
                Mix(ref shape, (ulong)(uint)placed.Length);
                for (int p = 0; p < placed.Length; p++)
                {
                    ref readonly TilePlacement tp = ref placed[p];
                    // the part of the tile it PAINTS (the composite draws only that part; the rest of the surface is
                    // transparent): a re-raster that moves it changes the entry's rect, and the diff repairs old ∪ new
                    Mix(ref shape, (ulong)(ushort)tp.Key.Tx << 48 | (ulong)(ushort)tp.Key.Ty << 32 | (uint)tp.W << 16 | (uint)tp.H);
                    ulong sig = (ulong)(uint)tp.Surface << 32 | serials.Serial(tp.Surface);
                    Add(ref entries, ref count, PaintIn(in it, in tp, in region, in sci), sig);
                }
                continue;
            }
            // a video-hole erase (a backdrop makes the group uncacheable): parameters only, in the shape
        }
        if (cacheable) key = KeyOf(shape, items[i].BlurSigma, entries.AsSpan(0, count));
        return shape;
    }

    /// <summary>The content key of a shape and its entries: an entry that paints signs its rect and signature, one that
    /// paints nothing signs only that it is there — exactly the distinction <see cref="Diff"/> draws.</summary>
    private static ulong KeyOf(ulong shape, float groupSigma, ReadOnlySpan<GroupEntry> entries)
    {
        ulong h = 0x6C0F_0000_0000_0001UL;
        Mix(ref h, shape);
        Mix(ref h, Bits(groupSigma, 0f));
        for (int e = 0; e < entries.Length; e++)
        {
            PixelRect r = entries[e].Rect;
            if (r.IsEmpty) { Mix(ref h, 0xE7E7UL); continue; }
            Mix(ref h, (ulong)(uint)r.Left << 32 | (uint)r.Top);
            Mix(ref h, (ulong)(uint)r.Right << 32 | (uint)r.Bottom);
            Mix(ref h, entries[e].Sig);
        }
        return h == 0UL ? 1UL : h;   // 0 is "not cacheable"
    }

    /// <summary>
    /// The rects (relative to the region) where <paramref name="cur"/> differs from <paramref name="prev"/> — entries of the
    /// same shape, compared in order — merged into at most <see cref="MaxDirtyRects"/> rects in <paramref name="dirty"/>.
    /// Returns how many, 0 when nothing changed, or −1 when the two cannot be compared (a different entry count) or the
    /// dirty area exceeds <see cref="MaxDirtyShare"/> of <paramref name="regionArea"/> — the caller renders whole.
    /// </summary>
    public static int Diff(ReadOnlySpan<GroupEntry> prev, ReadOnlySpan<GroupEntry> cur, long regionArea, Span<PixelRect> dirty)
    {
        if (prev.Length != cur.Length) return -1;
        int n = 0;
        for (int e = 0; e < cur.Length; e++)
        {
            if (prev[e].Sig == cur[e].Sig && prev[e].Rect.Equals(cur[e].Rect)) continue;
            PixelRect r = Union(prev[e].Rect, cur[e].Rect);
            if (r.IsEmpty) continue;
            if (n < dirty.Length) dirty[n++] = r;
            else dirty[n - 1] = Union(dirty[n - 1], r);
        }
        if (n == 0) return 0;
        // overlapping rects would be repaired twice (each repair clears and redraws its own rect — correct, but wasted):
        // merge them, then bound the total area
        for (bool merged = true; merged;)
        {
            merged = false;
            for (int a = 0; a < n && !merged; a++)
                for (int b = a + 1; b < n; b++)
                {
                    if (!Overlaps(dirty[a], dirty[b])) continue;
                    dirty[a] = Union(dirty[a], dirty[b]);
                    dirty[b] = dirty[--n];
                    merged = true;
                    break;
                }
        }
        long area = 0;
        for (int k = 0; k < n; k++) area += (long)(dirty[k].Right - dirty[k].Left) * (dirty[k].Bottom - dirty[k].Top);
        return area > regionArea * MaxDirtyShare ? -1 : n;
    }

    private static void Add(ref GroupEntry[] entries, ref int count, PixelRect rect, ulong sig)
    {
        if (count == entries.Length) Array.Resize(ref entries, Math.Max(16, entries.Length * 2));
        entries[count].Rect = rect;
        entries[count].Sig = sig;
        count++;
    }

    /// <summary>The item's scissor in the group surface's px (the whole surface when its clip is unbounded).</summary>
    /// <summary>Item <paramref name="it"/>'s scissor relative to <paramref name="region"/>'s top-left, cut to the region:
    /// the only surface px it can write.</summary>
    internal static PixelRect ScissorIn(in CompositeItem it, in PixelRect region)
    {
        int w = region.Right - region.Left, h = region.Bottom - region.Top;
        return Intersect(Scissor(in it, region.Left, region.Top, w, h), new PixelRect(0, 0, w, h));
    }

    /// <summary>The surface px (relative to <paramref name="region"/>) tile placement <paramref name="tp"/> of item
    /// <paramref name="it"/> paints under <paramref name="sci"/> (<see cref="ScissorIn"/>); empty when it paints nothing
    /// there. <see cref="Describe"/> keys a placement only when this is non-empty, so a re-raster that cannot change
    /// a pixel of the surface moves neither the key nor the diff.</summary>
    internal static PixelRect PaintIn(in CompositeItem it, in TilePlacement tp, in PixelRect region, in PixelRect sci)
    {
        if (tp.Px1 <= tp.Px0 || tp.Py1 <= tp.Py0) return default;
        int x0 = (int)it.Transform.Dx - region.Left + tp.Key.Tx * TileGrid.W, y0 = (int)it.Transform.Dy - region.Top + tp.Key.Ty * TileGrid.H;
        var paint = new PixelRect(x0 + tp.Px0, y0 + tp.Py0, x0 + tp.Px1, y0 + tp.Py1);
        return Intersect(in paint, in sci);
    }

    private static PixelRect Scissor(in CompositeItem it, int ox, int oy, int w, int h)
    {
        if (it.Clip.W <= 0f && it.Clip.H <= 0f && it.Clip.X == 0f && it.Clip.Y == 0f) return new PixelRect(0, 0, w, h);
        return new PixelRect((int)MathF.Floor(it.Clip.X) - ox, (int)MathF.Floor(it.Clip.Y) - oy,
            (int)MathF.Ceiling(it.Clip.Right) - ox, (int)MathF.Ceiling(it.Clip.Bottom) - oy);
    }

    private static PixelRect Intersect(in PixelRect a, in PixelRect b)
    {
        int l = Math.Max(a.Left, b.Left), t = Math.Max(a.Top, b.Top), r = Math.Min(a.Right, b.Right), btm = Math.Min(a.Bottom, b.Bottom);
        return r > l && btm > t ? new PixelRect(l, t, r, btm) : default;
    }

    private static PixelRect Union(in PixelRect a, in PixelRect b)
    {
        if (a.IsEmpty) return b;
        if (b.IsEmpty) return a;
        return new PixelRect(Math.Min(a.Left, b.Left), Math.Min(a.Top, b.Top), Math.Max(a.Right, b.Right), Math.Max(a.Bottom, b.Bottom));
    }

    private static bool Overlaps(in PixelRect a, in PixelRect b)
        => a.Left < b.Right && b.Left < a.Right && a.Top < b.Bottom && b.Top < a.Bottom;

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
