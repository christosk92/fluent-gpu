using FluentGpu.Foundation;
using FluentGpu.Render;
using FluentGpu.Render.Tiles;

using RectF = FluentGpu.Foundation.RectF;

namespace FluentGpu.Rhi.D3D12;

// ── Composite OCCLUSION: items hidden under a later opaque item (CompositeItem.Opaque) are never drawn ──────────────────
//
// The recorder marks the window-px rect each segment paints fully opaque (a solid fill of alpha 1, square, axis-aligned,
// outside every layer). The device turns that into the WHOLE PIXELS the composited item really writes opaque — a tile
// item's whole pixels inside it that its placements cover; a low-resolution surface's pixels whose bilinear upsample reads
// only opaque texels — and marks every EARLIER top-level item whose footprint lies inside as hidden by it
// (_coveredBy[k] = the first such occluder). Painter order makes this exact and cheap:
//
//   * the main composite (and each partial-present rect) skips a hidden item: the occluder overwrites all its pixels;
//   * the mini-composite of acrylic backdrop i skips k only when its occluder lies below i (0 ≤ _coveredBy[k] < i): an
//     occluder above the backdrop does not hide k from the blur source;
//   * the backdrop's content key leaves hidden items out (a page changing under an opaque stage no longer re-blurs the
//     stage's bars), and the partial-present diff never repaints for them.
//
// A fullscreen overlay (the Wavee stage over the app shell, a modal page) stops compositing the window of tiles beneath it.
// GpuKnockouts.NoOcclusion composites every item as before: the pixel-identity control.
public sealed unsafe partial class D3D12Device
{
    private int[] _coveredBy = new int[64];
    private PixelRect[] _occRect = new PixelRect[64];   // per top-level occluder: the whole pixels it writes opaque
    /// <summary>Top-level items the last composite left out as hidden under a later opaque item (always-on counter).</summary>
    public int LastOccludedItems { get; private set; }
    /// <summary>Window px of the items the last composite left out as hidden (their footprints, summed).</summary>
    public long LastOccludedPx { get; private set; }

    /// <summary>Probe control (render thread parked): retire every scratch surface — see <see cref="SurfacePool.DropScratch"/>.</summary>
    public void DropScratchSurfaces() => _surfaces?.DropScratch();

    private void BeginOcclusion(int n)
    {
        if (_coveredBy.Length < n) _coveredBy = new int[Math.Max(n, _coveredBy.Length * 2)];
        if (_occRect.Length < n) _occRect = new PixelRect[Math.Max(n, _occRect.Length * 2)];
        Array.Fill(_coveredBy, -1, 0, n);
        Array.Clear(_occRect, 0, n);
        LastOccludedItems = 0; LastOccludedPx = 0;
    }

    /// <summary>The offscreen phase over the top-level items in painter order: prepare each, then — when it is an opaque
    /// occluder — mark the earlier top-level items it hides. A backdrop prepared later sees every occluder below it.</summary>
    private void PrepareTop(in CompositeFrame frame)
    {
        ReadOnlySpan<CompositeItem> items = frame.Items;
        BeginOcclusion(items.Length);
        bool on = (_frameKnockouts & (GpuKnockouts.NoOcclusion | GpuKnockouts.ForceFullDirect)) == 0;
        for (int j = 0; j < items.Length; j++)
        {
            ref readonly CompositeItem it = ref items[j];
            int end = it.Kind == CompositeKind.Group ? Math.Min(items.Length, j + 1 + it.GroupCount) : j + 1;
            PrepareRange(in frame, j, end);
            if (on && it.Opaque.W > 0f && it.Opaque.H > 0f && it.Kind != CompositeKind.Group && OccluderRect(in frame, j, out PixelRect occ))
            {
                _occRect[j] = occ;
                Occlude(in frame, j, in occ);
            }
            j = end - 1;
        }
    }

    /// <summary>Mark every earlier, not yet hidden top-level item whose footprint lies inside <paramref name="occ"/>.</summary>
    private void Occlude(in CompositeFrame frame, int j, in PixelRect occ)
    {
        ReadOnlySpan<CompositeItem> items = frame.Items;
        for (int k = 0; k < j; k++)
        {
            ref readonly CompositeItem it = ref items[k];
            int next = it.Kind == CompositeKind.Group ? Math.Min(items.Length, k + 1 + it.GroupCount) - 1 : k;
            if (_coveredBy[k] < 0 && it.Kind != CompositeKind.EraseVideoHole && CoverFootprint(in frame, k, out PixelRect foot)
                && foot.Left >= occ.Left && foot.Top >= occ.Top && foot.Right <= occ.Right && foot.Bottom <= occ.Bottom)
            {
                _coveredBy[k] = j;
                LastOccludedItems++;
                LastOccludedPx += (long)(foot.Right - foot.Left) * (foot.Bottom - foot.Top);
            }
            k = next;
        }
    }

    /// <summary>The whole window pixels item <paramref name="j"/> (prepared) writes FULLY OPAQUE, or false.</summary>
    private bool OccluderRect(in CompositeFrame frame, int j, out PixelRect occ)
    {
        occ = default;
        ref readonly CompositeItem it = ref frame.Items[j];
        if (it.Alpha < 1f || it.BlurSigma > 0f || it.RoundClip.W > 0f || !it.Feather.IsNone || !it.Feather2.IsNone) return false;
        PixelRect win = new(0, 0, (int)_w, (int)_h);
        PixelRect sci = IsUnbounded(it.Clip) ? win : Clamp(new PixelRect((int)MathF.Ceiling(it.Clip.X), (int)MathF.Ceiling(it.Clip.Y),
            (int)MathF.Floor(it.Clip.Right), (int)MathF.Floor(it.Clip.Bottom)), in win);
        RectF o = it.Opaque;
        switch (it.Kind)
        {
            case CompositeKind.Tiles:
            case CompositeKind.Region:
            {
                // a pixel whose centre lies half a pixel inside a fill's edge is written at full coverage (the SDF ramp
                // is clamp(0.5 − d/fw)): the whole pixels inside the rect
                occ = Clamp(new PixelRect((int)MathF.Ceiling(o.X), (int)MathF.Ceiling(o.Y), (int)MathF.Floor(o.Right), (int)MathF.Floor(o.Bottom)), in sci);
                if (occ.IsEmpty) return false;
                // ...and only where resident tiles are placed (a missing tile draws nothing there)
                long need = (long)(occ.Right - occ.Left) * (occ.Bottom - occ.Top), have = 0;
                ReadOnlySpan<TilePlacement> placed = frame.PlacementsOf(it.SliceId);
                for (int p = 0; p < placed.Length; p++)
                {
                    RectF r = PlacementRect(in it, in placed[p]);
                    long w = Math.Min(occ.Right, (int)MathF.Floor(r.Right)) - Math.Max(occ.Left, (int)MathF.Ceiling(r.X));
                    long h = Math.Min(occ.Bottom, (int)MathF.Floor(r.Bottom)) - Math.Max(occ.Top, (int)MathF.Ceiling(r.Y));
                    if (w > 0 && h > 0) have += w * h;
                }
                return have >= need;
            }
            case CompositeKind.Direct:
            {
                if (it.LowResDown <= 1 || _itemSurface[j] < 0 || _itemDown[j] != it.LowResDown) return false;
                int d = it.LowResDown;
                PixelRect reg = _itemRegion[j];
                if (!LowResOpaqueSpan(reg.Left, reg.Right, d, o.X, o.Right, out int x0, out int x1)) return false;
                if (!LowResOpaqueSpan(reg.Top, reg.Bottom, d, o.Y, o.Bottom, out int y0, out int y1)) return false;
                occ = Clamp(new PixelRect(x0, y0, x1, y1), in sci);
                return !occ.IsEmpty;
            }
            default:
                return false;
        }
    }

    /// <summary>One axis of a low-resolution surface over [<paramref name="l"/>, <paramref name="r"/>) (window px, d px per
    /// texel): the window pixels [<paramref name="p0"/>, <paramref name="p1"/>) whose upsample reads only texels the fill
    /// [<paramref name="f0"/>, <paramref name="f1"/>] wrote fully opaque. Texel u covers [l + u·d, l + (u+1)·d) and is opaque
    /// when that span lies inside the fill. Pixel p samples t = (p + ½ − l)/d − ½, clamped to [0, n − 1] on both sides (the
    /// sampler's clamp at uv 0, PSSample's clamp at the last written texel); away from a clamped side the bound keeps one
    /// pixel of margin for the filter's fixed-point weights.</summary>
    private static bool LowResOpaqueSpan(int l, int r, int d, float f0, float f1, out int p0, out int p1)
    {
        p0 = p1 = 0;
        int n = (r - l) / d;
        if (n <= 0) return false;
        int u0 = Math.Max(0, (int)MathF.Ceiling((f0 - l) / d));
        int u1 = Math.Min(n - 1, (int)MathF.Floor((f1 - l) / d) - 1);
        if (u1 < u0) return false;
        p0 = u0 == 0 ? l : (int)MathF.Ceiling(l + (u0 + 0.5f) * d - 0.5f) + 1;
        p1 = u1 == n - 1 ? l + n * d : (int)MathF.Ceiling(l + (u1 + 0.5f) * d - 0.5f) - 1;
        return p1 > p0;
    }

    /// <summary>The window pixels item <paramref name="k"/> (prepared — it lies below the occluder being applied) can write,
    /// rounded out; false when it writes nothing anyone could see (no test needed).</summary>
    private bool CoverFootprint(in CompositeFrame frame, int k, out PixelRect foot)
    {
        ref readonly CompositeItem it = ref frame.Items[k];
        PixelRect win = new(0, 0, (int)_w, (int)_h);
        PixelRect sci = IsUnbounded(it.Clip) ? win : Clamp(Out(it.Clip), in win);
        foot = default;
        switch (it.Kind)
        {
            case CompositeKind.Tiles:
            case CompositeKind.Region:
                if (it.BlurSigma <= 0f && _itemSurface[k] < 0)
                {
                    ReadOnlySpan<TilePlacement> placed = frame.PlacementsOf(it.SliceId);
                    for (int p = 0; p < placed.Length; p++) foot = Union(in foot, Out(PlacementRect(in it, in placed[p])));
                    foot = Clamp(in foot, in sci);
                    return !foot.IsEmpty;
                }
                goto case CompositeKind.Direct;
            case CompositeKind.Direct:
            {
                if (_itemSurface[k] >= 0) foot = _itemRegion[k];
                int end = _itemChunkStart[k] + _itemChunkCount[k];
                for (int c = _itemChunkStart[k]; c < end; c++) { PixelRect cr = _chunks[c].Rect; foot = Union(in foot, in cr); }
                foot = Clamp(in foot, in sci);
                return !foot.IsEmpty;
            }
            case CompositeKind.Backdrop:
                foot = Clamp(Out(it.RoundClip), in sci);
                return !foot.IsEmpty;
            case CompositeKind.Group:
                foot = it.Footprint.W > 0f && it.Footprint.H > 0f ? Clamp(Out(it.Footprint), in sci) : sci;
                return !foot.IsEmpty;
            default:
                return false;
        }
    }

    /// <summary>The highest top-level occluder below item <paramref name="i"/> whose opaque pixels contain
    /// <paramref name="region"/>, or -1. A backdrop's mini-composite over that region starts at it: everything before it
    /// (and the clear) paints nothing there. An occluder hidden by a later one lies inside that one, which also contains the
    /// region, so the highest is always drawn.</summary>
    private int CoverFrom(int i, in PixelRect region)
    {
        if (region.IsEmpty) return -1;
        for (int j = Math.Min(i, _occRect.Length) - 1; j >= 0; j--)
        {
            ref readonly PixelRect o = ref _occRect[j];
            if (!o.IsEmpty && region.Left >= o.Left && region.Top >= o.Top && region.Right <= o.Right && region.Bottom <= o.Bottom) return j;
        }
        return -1;
    }

    /// <summary>Is item <paramref name="k"/> hidden for a composite of items [.., <paramref name="end"/>) — by an occluder
    /// that composite draws?</summary>
    private bool Hidden(int k, int end) => (uint)k < (uint)_coveredBy.Length && _coveredBy[k] >= 0 && _coveredBy[k] < end;

    private static PixelRect Out(in RectF r)
        => new((int)MathF.Floor(r.X), (int)MathF.Floor(r.Y), (int)MathF.Ceiling(r.X + r.W), (int)MathF.Ceiling(r.Y + r.H));

    private static PixelRect Clamp(in PixelRect a, in PixelRect b)
    {
        var r = new PixelRect(Math.Max(a.Left, b.Left), Math.Max(a.Top, b.Top), Math.Min(a.Right, b.Right), Math.Min(a.Bottom, b.Bottom));
        return r.IsEmpty ? default : r;
    }

    private static PixelRect Union(in PixelRect a, in PixelRect b)
    {
        if (a.IsEmpty) return b;
        if (b.IsEmpty) return a;
        return new PixelRect(Math.Min(a.Left, b.Left), Math.Min(a.Top, b.Top), Math.Max(a.Right, b.Right), Math.Max(a.Bottom, b.Bottom));
    }
}
