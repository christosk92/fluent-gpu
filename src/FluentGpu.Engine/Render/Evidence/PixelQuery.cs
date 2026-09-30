using FluentGpu.Render.Tiles;
using FluentGpu.Rhi;

namespace FluentGpu.Render.Evidence;

/// <summary>One composite item that painted (or may have painted) a queried window pixel: the item's index in painter
/// order and its record, the value of each analytic feather at the pixel centre (1 = no feather), the item's composite
/// alpha, and — for a Tiles/Region item — the tile under the pixel with its raster frame, raster hash and whether it is
/// STALE. <see cref="Covered"/> = the item's footprint (its tile, or for a group / backdrop / hole / degraded item its
/// clip) contains the pixel.</summary>
public readonly record struct PixelHit(int ItemIndex, ItemRecord Item, float Feather1, float Feather2, float Alpha,
    TileKey Tile, int TileRasterFrame, ulong TileRasterHash, ulong TileWantHash, bool TileStale, bool Covered,
    bool FeatherBand = false)
{
    /// <summary>The item is feathered and the pixel lies OUTSIDE its feather quad split's interior: the composite drew it
    /// through a feathered strip (<see cref="FeatherQuadSplit"/>); false = the feather-free interior (or no feather).</summary>
    public bool InFeatherBand => FeatherBand;

    /// <summary>The combined analytic coverage the composite multiplies the item's sample by at the pixel.</summary>
    public float Coverage => Alpha * Feather1 * Feather2;
}

/// <summary>
/// "What composited at window pixel (x, y) in this frame" (docs/plans/evidence-diagnostics-implementation.md §A.3) — a
/// pure function over one ledger frame. Items whose clip excludes the pixel are skipped; a Tiles/Region item with no
/// resident tile under the pixel painted nothing there and is skipped too; every other item is returned in painter order
/// (bottom → top) with its feathers evaluated by <see cref="EdgeFeatherMask.Evaluate(in EdgeFeather, float, float)"/> at
/// the pixel centre — the same function the composite shader runs (held to ≤ 1/255 by <c>tile-feather-identity</c>).
/// Zero allocation.
/// </summary>
public static class PixelQuery
{
    public static int Query(in CompositeFrameView frame, int px, int py, Span<PixelHit> dst)
    {
        int n = 0;
        ReadOnlySpan<ItemRecord> items = frame.Items;
        float cx = px + 0.5f, cy = py + 0.5f;
        for (int i = 0; i < items.Length && n < dst.Length; i++)
        {
            ref readonly ItemRecord it = ref items[i];
            if ((it.Flags & ItemRecordFlags.ClipBounded) != 0 && !InClip(in it, px, py)) continue;
            float f1 = it.Feather1.IsNone ? 1f : EdgeFeatherMask.Evaluate(it.Feather1, cx, cy);
            float f2 = it.Feather2.IsNone ? 1f : EdgeFeatherMask.Evaluate(it.Feather2, cx, cy);
            TileKey key = default;
            int rasterFrame = 0;
            ulong rasterHash = 0, wantHash = 0;
            bool stale = false;
            var kind = (CompositeKind)it.Kind;
            if (kind is CompositeKind.Tiles or CompositeKind.Region)
            {
                if (!frame.TryPlacementAt(it.SliceId, px - it.TransDx, py - it.TransDy, out PlacementRecord p)) continue;
                key = new TileKey(p.SliceId, p.Tx, p.Ty);
                rasterFrame = p.RasterFrame;
                rasterHash = p.RasterHash;
                wantHash = p.WantHash;
                stale = p.Stale != 0;
            }
            bool feathered = !it.Feather1.IsNone || !it.Feather2.IsNone;
            bool band = feathered && !(cx >= it.InteriorL && cx < it.InteriorR && cy >= it.InteriorT && cy < it.InteriorB);
            dst[n++] = new PixelHit(i, it, f1, f2, it.AlphaQ8 / 255f, key, rasterFrame, rasterHash, wantHash, stale, true, band);
        }
        return n;
    }

    /// <summary>The device-px clip contains the pixel (a bounded empty clip contains nothing).</summary>
    public static bool InClip(in ItemRecord it, int px, int py)
        => px >= it.ClipX && px < it.ClipX + it.ClipW && py >= it.ClipY && py < it.ClipY + it.ClipH;
}
