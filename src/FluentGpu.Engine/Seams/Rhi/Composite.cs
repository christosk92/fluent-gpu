using FluentGpu.Foundation;
using FluentGpu.Render;
using FluentGpu.Render.Tiles;

namespace FluentGpu.Rhi;

// The composite seam of the retained tiled content layer (docs/plans/scroll-gpu-retained-tiles-implementation.md §A.5,
// §A.8, §D). A backend that implements IGpuDevice.SubmitComposite rasters the frame's invalid tiles into their surfaces
// (one CLEAR→STORE render pass each), then draws the painter-ordered composite items into the back buffer in ONE
// CLEAR→STORE pass (the load op writes the clear colour; the previous frame is never read), and hands the backend the
// frame's PresentParams (dirty rects — a hint the backend may use when its swap effect allows partial presentation; the
// D3D12 FLIP_DISCARD swapchain does not). Every backend that renders the primary window implements it (the headless
// model and D3D12); popups keep the direct SubmitDrawList route.

/// <summary>What a <see cref="CompositeItem"/> draws.
/// <list type="bullet">
/// <item><see cref="Tiles"/> — the resident tiles of a static/scroll slice SEGMENT (its <see cref="CompositeFrame.PlacementsOf"/>
/// run), placed by the item's transform.</item>
/// <item><see cref="Region"/> — the same for an effect slice (its tiles are region surfaces sized to its content); a leaf
/// effect slice carries its group parameters (alpha / feather / blur) on the item itself.</item>
/// <item><see cref="EraseVideoHole"/> — a DestOut quad over the item's clip so the DirectComposition video visual below the
/// swapchain shows through. Placed BEFORE the segment that punched the hole (it clears what earlier items composited
/// there; that segment's own tile holds the hole with its later chrome painted back over it) — after it only for a hole
/// punched inside an inline group layer (gpu-renderer.md §7.3).</item>
/// <item><see cref="Group"/> — an effect slice with child slices: the next <see cref="CompositeItem.GroupCount"/> items
/// composite into ONE group surface covering the item's clip, which is then composited with the item's alpha / feather /
/// blur (exact group semantics — overlapping children never double-blend).</item>
/// <item><see cref="Backdrop"/> — an in-app acrylic surface: a mini-composite of everything painted before it under its
/// rounded rect (<see cref="CompositeItem.RoundClip"/>/<see cref="CompositeItem.ClipRadii"/>), blurred and tinted with
/// <see cref="CompositeItem.Acrylic"/>. Zero back-buffer reads.</item>
/// <item><see cref="Direct"/> — a DEGRADED segment (its visible tiles did not fit the budget this frame): its stream is
/// replayed straight into the target at the item's transform instead — today's cost, never blank.</item>
/// <item><see cref="Image"/> — a POSED IMAGE LAYER (BoxEl.CompositePose): the slice's stream is exactly one plain image, which
/// the backend draws as ONE bilinear quad straight from the image texture. It holds no tiles; its
/// <see cref="CompositeItem.Transform"/> maps the stream's window-DIP space (the image's free pose) to device px — scale,
/// translate and the accumulated offset in one affine — so a slow pan / zoom changes one matrix and rasters nothing.</item>
/// </list></summary>
public enum CompositeKind : byte { Tiles, Region, EraseVideoHole, Group, Backdrop, Direct, Image }

/// <summary>An in-app acrylic recipe applied at composite time (§A.5): the source is a mini-composite of the slices
/// beneath the item's clip, blurred by <see cref="BlurSigma"/> (device px) then tinted, luminosity-washed and noised —
/// the same terms the PushLayer acrylic carries today. The default value is "no acrylic".</summary>
public readonly record struct AcrylicRecipe(ColorF Tint, ColorF Fallback, float TintOpacity, float BlurSigma,
    float NoiseOpacity, float LuminosityOpacity, float FeatherFrac = 0f)
{
    public bool IsNone => BlurSigma <= 0f && TintOpacity <= 0f && LuminosityOpacity <= 0f && NoiseOpacity <= 0f;
}

/// <summary>One painter-ordered composite draw (§A.5). <see cref="SliceId"/> names the slice whose surfaces the item
/// samples (its <see cref="CompositeFrame.PlacementsOf"/> range; −1 for items that sample none); <see cref="Transform"/>
/// places slice space in device space (whole device px ⇒ 1:1 point sampling); <see cref="Alpha"/> = group opacity;
/// <see cref="Clip"/> = the device-px scissor (pixel-aligned; empty = unbounded); <see cref="RoundClip"/> +
/// <see cref="ClipRadii"/> = the analytic (sdRoundRect) rounded clip (W ≤ 0 = none); <see cref="Feather"/> = the analytic
/// edge fade (<see cref="EdgeFeatherMask"/>); <see cref="BlurSigma"/> = self-blur σ (device px, 0 = none) whose crisp
/// source is bounded by <see cref="SourceClip"/> (device px); <see cref="Acrylic"/> = the backdrop recipe;
/// <see cref="BlendCopy"/> ≠ 0 = write without blending; <see cref="GroupCount"/> = the items a <see cref="CompositeKind.Group"/>
/// encloses; <see cref="HasLayer"/> ≠ 0 = the item carries its slice marker's layer (<see cref="CompositeFrame.ItemLayers"/>).
/// <see cref="Feather2"/> = a SECOND analytic feather multiplied with <see cref="Feather"/> (two nested edge fades distributed
/// onto one item — the coverage is the exact product; gpu-renderer.md §13.1e). <see cref="Footprint"/> = a
/// <see cref="CompositeKind.Group"/>'s placed footprint (device px, window space: the union of what its enclosed items can
/// paint, + its blur halo) — the region its group surface covers when that fits one tile (<c>GroupCacheKey.Region</c>).
/// A Group's <see cref="Transform"/> is its slice's posed offset (whole device px), so a group moved rigidly by an
/// ancestor scroll keeps its content key. <see cref="Inherited"/> = how many ancestor edge fades were DISTRIBUTED onto this
/// item (0–2; their layers are <see cref="CompositeFrame.ItemInherited"/>, their feathers already folded into
/// <see cref="Feather"/>/<see cref="Feather2"/>). <see cref="LowResDown"/> &gt; 1 = a LOW-RESOLUTION repaint boundary
/// (BoxEl.RasterScale): a <see cref="CompositeKind.Direct"/> item the backend replays once per change into one surface at
/// 1/LowResDown of the window scale and upsamples bilinearly — it holds no tiles. <see cref="Opaque"/> = a window-px rect
/// (whole pixels) this item paints FULLY OPAQUE once composited (empty = none known): every item composited before it whose
/// footprint lies inside is hidden, and the backend skips it. A Screen-blended or feedback item never carries one.</summary>
public readonly record struct CompositeItem(int SliceId, CompositeKind Kind, Affine2D Transform, float Alpha, RectF Clip,
    CornerRadius4 ClipRadii, EdgeFeather Feather, float BlurSigma, AcrylicRecipe Acrylic, byte BlendCopy,
    RectF RoundClip = default, int GroupCount = 0, byte HasLayer = 0, RectF SourceClip = default,
    EdgeFeather Feather2 = default, RectF Footprint = default, byte Inherited = 0, byte LowResDown = 0, RectF Opaque = default,
    FeedbackSpec Feedback = default, Affine2D FeedbackWarp = default, float FeedbackDecay = 0f)
{
    /// <summary>A FEEDBACK item (BoxEl.Feedback, visualizer F6): a <see cref="CompositeKind.Direct"/> low-res item whose backend
    /// surface persists and is advanced through <see cref="FeedbackWarp"/> / <see cref="FeedbackDecay"/> each change.</summary>
    public bool IsFeedback => FeedbackDecay > 0f;
    /// <summary><see cref="BlendCopy"/> = 1: write without blending.</summary>
    public const byte BlendCopyWrite = 1;
    /// <summary><see cref="BlendCopy"/> = 2: SCREEN onto the destination, <c>1 − (1 − s)(1 − d)</c> (BoxEl.LayerBlend.Screen).</summary>
    public const byte BlendScreen = 2;
}

/// <summary>The backend's content-keyed offscreen cache for the last composite (gpu-renderer.md §13.1e/§13.1g):
/// <paramref name="GroupSurfaces"/> = group surfaces RENDERED (a miss), <paramref name="GroupCacheHits"/> = groups
/// re-drawn from a retained surface, <paramref name="RetainedBytes"/> = bytes held by retained derived surfaces (groups,
/// self-blurs, acrylic backdrops) — bounded by <c>TileBudget.RetainedShare</c> of the tile budget.</summary>
public readonly record struct CompositeCacheStats(int GroupSurfaces, int GroupCacheHits, long RetainedBytes);

/// <summary>The DXGI Present1 parameters for a composited frame (§D). <see cref="DirtyRects"/> = the device-px rects that
/// changed (old∪new composite item dests, rastered-tile dests, feather bands, thumb); EMPTY means the whole frame changed.
/// On a pure scroll frame <see cref="HasScroll"/> names the scrolled <see cref="ScrollRect"/> (the viewport) and the exact
/// texel shift (<see cref="ScrollDx"/>, <see cref="ScrollDy"/>); the feather bands stay in the dirty set.</summary>
public readonly ref struct PresentParams
{
    public readonly ReadOnlySpan<PixelRect> DirtyRects;
    public readonly bool HasScroll;
    public readonly PixelRect ScrollRect;
    public readonly int ScrollDx, ScrollDy;

    public PresentParams(ReadOnlySpan<PixelRect> dirtyRects)
    {
        DirtyRects = dirtyRects;
        HasScroll = false; ScrollRect = default; ScrollDx = 0; ScrollDy = 0;
    }

    public PresentParams(ReadOnlySpan<PixelRect> dirtyRects, in PixelRect scrollRect, int scrollDx, int scrollDy)
    {
        DirtyRects = dirtyRects;
        HasScroll = true; ScrollRect = scrollRect; ScrollDx = scrollDx; ScrollDy = scrollDy;
    }

    /// <summary>A whole-frame present (no dirty rects, no scroll).</summary>
    public static PresentParams Full => default;

    public bool IsFull => DirtyRects.IsEmpty && !HasScroll;
}

/// <summary>
/// Everything one composite turn hands the backend (§A.8): the frame context, the slice descriptors and their
/// concatenated per-slice opcode streams (<see cref="SliceRow.DrawListStart"/>/<see cref="SliceRow.DrawListLength"/>
/// index <see cref="SliceStreams"/>; <see cref="SliceRow.StreamBase"/> the whole arena, for the segment's scope prefix), the
/// tile raster list (visible first — the <see cref="SliceTable.Resolve"/> output), the resident tile placements grouped
/// contiguously by slice (what <see cref="CompositeKind.Tiles"/>/<see cref="CompositeKind.Region"/> items sample), the
/// painter-ordered composite items with their source layers, and the Present1 parameters staged for the target's next
/// Present. The backend writes <see cref="RasterDone"/>[i] = 1 for every raster it ACTUALLY completed (a tile whose replay
/// dropped instances, or that sampled a not-yet-resident image, stays invalid and is rastered again). Spans only — no
/// copies, no allocation.
/// </summary>
public readonly ref struct CompositeFrame
{
    public readonly FrameInfo Info;
    public readonly ReadOnlySpan<SliceRow> Slices;
    public readonly ReadOnlySpan<byte> SliceStreams;
    public readonly ReadOnlySpan<TileRaster> Rasters;
    public readonly ReadOnlySpan<TilePlacement> Placements;
    public readonly ReadOnlySpan<CompositeItem> Items;
    public readonly PresentParams Present;
    /// <summary>Every slice's span index (<see cref="SliceRow.SpanIndexStart"/>/<see cref="SliceRow.SpanIndexCount"/> index it).</summary>
    public readonly ReadOnlySpan<SliceSpan> SliceSpans;
    /// <summary>Parallel to <see cref="Items"/>: the slice marker's layer an item with <see cref="CompositeItem.HasLayer"/>
    /// carries (window DIP at the item's posed offset) — what a leaf effect slice or a group composites with, and what the
    /// headless model reports as the frame's layers. Default for the rest.</summary>
    public readonly ReadOnlySpan<PushLayerCmd> ItemLayers;
    /// <summary>Parallel to <see cref="Rasters"/>: written by the backend, 1 = rastered this submission.</summary>
    public readonly Span<byte> RasterDone;
    /// <summary>Two per item (2·i, 2·i + 1): the ancestor edge-fade layers DISTRIBUTED onto item i (its first
    /// <see cref="CompositeItem.Inherited"/> entries; window DIP at their posed offset, the outer fade first) — what the
    /// headless model re-emits around the item. Default for the rest.</summary>
    public readonly ReadOnlySpan<PushLayerCmd> ItemInherited;
    /// <summary>Parallel to <see cref="Rasters"/>, cleared by the recorder: evidence bits the backend ORs in while it rasters
    /// (<see cref="CompositeFrameFlags"/> — an inline scratch lease refused inside the tile). Never changes what a raster
    /// counts as (that is <see cref="RasterDone"/>'s job); the raster ledger records it (evidence-diagnostics §A.3).</summary>
    public readonly Span<byte> RasterFlags;
    /// <summary>Parallel to <see cref="Items"/>, cleared by the recorder: evidence bits the backend ORs in per item
    /// (<see cref="CompositeFrameFlags"/> — a group re-drawn from its retained surface, or rendered anew).</summary>
    public readonly Span<byte> ItemFlags;
    /// <summary>The tile surface slots whose textures the backend releases this turn (<see cref="SliceTable.TrimmedSurfaces"/>:
    /// slots NO tile holds, idle past <see cref="SliceTable.SurfaceTrimTurns"/>). The table owns tile texture lifetime — a
    /// backend never trims a tile texture on a clock of its own: a tile it has not sampled for many turns may still be valid
    /// and placed (consumed only through a retained group / self-blur / backdrop result), and trimming it would composite
    /// nothing where the table believes current pixels are (gpu-renderer.md §13.1g).</summary>
    public readonly ReadOnlySpan<int> TrimSurfaces;
    /// <summary>The identity of the <see cref="SliceTable"/> this frame's surface numbers belong to (<see cref="SliceTable.OwnerId"/>;
    /// 0 = unstamped, e.g. a hand-built test frame). A backend's tile pool is indexed by those numbers alone, so it records the
    /// first owner after a (re)build and flags any other owner compositing into the same pool.</summary>
    public readonly int OwnerToken;

    public CompositeFrame(in FrameInfo info, ReadOnlySpan<SliceRow> slices, ReadOnlySpan<byte> sliceStreams,
        ReadOnlySpan<TileRaster> rasters, ReadOnlySpan<TilePlacement> placements, ReadOnlySpan<CompositeItem> items,
        PresentParams present)
        : this(in info, slices, sliceStreams, rasters, placements, items, present, default, default, default) { }

    public CompositeFrame(in FrameInfo info, ReadOnlySpan<SliceRow> slices, ReadOnlySpan<byte> sliceStreams,
        ReadOnlySpan<TileRaster> rasters, ReadOnlySpan<TilePlacement> placements, ReadOnlySpan<CompositeItem> items,
        PresentParams present, ReadOnlySpan<SliceSpan> sliceSpans,
        ReadOnlySpan<PushLayerCmd> itemLayers, Span<byte> rasterDone, ReadOnlySpan<PushLayerCmd> itemInherited = default,
        Span<byte> rasterFlags = default, Span<byte> itemFlags = default, ReadOnlySpan<int> trimSurfaces = default,
        int ownerToken = 0)
    {
        Info = info; Slices = slices; SliceStreams = sliceStreams; Rasters = rasters; Placements = placements;
        Items = items; Present = present; SliceSpans = sliceSpans;
        ItemLayers = itemLayers; RasterDone = rasterDone; ItemInherited = itemInherited;
        RasterFlags = rasterFlags; ItemFlags = itemFlags; TrimSurfaces = trimSurfaces; OwnerToken = ownerToken;
    }

    /// <summary>The <paramref name="k"/>-th (0 or 1) distributed ancestor fade of item <paramref name="i"/> (default when
    /// absent).</summary>
    public PushLayerCmd InheritedLayer(int i, int k)
    {
        int at = 2 * i + k;
        return (uint)at < (uint)ItemInherited.Length ? ItemInherited[at] : default;
    }

    /// <summary>The contiguous run of <see cref="Placements"/> belonging to <paramref name="sliceId"/> (empty when none).</summary>
    public ReadOnlySpan<TilePlacement> PlacementsOf(int sliceId)
    {
        int a = 0;
        while (a < Placements.Length && Placements[a].Key.SliceId != sliceId) a++;
        int b = a;
        while (b < Placements.Length && Placements[b].Key.SliceId == sliceId) b++;
        return Placements.Slice(a, b - a);
    }

    /// <summary>The opcode stream of <paramref name="slice"/> (empty when its range is out of bounds).</summary>
    public ReadOnlySpan<byte> StreamOf(in SliceRow slice)
    {
        if (slice.DrawListStart < 0 || slice.DrawListLength <= 0
            || (long)slice.DrawListStart + slice.DrawListLength > SliceStreams.Length) return ReadOnlySpan<byte>.Empty;
        return SliceStreams.Slice(slice.DrawListStart, slice.DrawListLength);
    }

    /// <summary>The PREFIX of <paramref name="slice"/>'s segment: its arena's bytes before the segment starts (the scopes
    /// still open at the segment's first byte are reconstructed from it). Empty for a first segment.</summary>
    public ReadOnlySpan<byte> PrefixOf(in SliceRow slice)
    {
        if (slice.StreamBase < 0 || slice.DrawListStart <= slice.StreamBase || slice.DrawListStart > SliceStreams.Length)
            return ReadOnlySpan<byte>.Empty;
        return SliceStreams.Slice(slice.StreamBase, slice.DrawListStart - slice.StreamBase);
    }

    /// <summary>The row whose <see cref="SliceRow.Id"/> is <paramref name="sliceId"/> (−1 when none).</summary>
    public int RowIndexOf(int sliceId)
    {
        for (int i = 0; i < Slices.Length; i++) if (Slices[i].Id == sliceId) return i;
        return -1;
    }
}

/// <summary>The evidence bits a backend ORs into <see cref="CompositeFrame.RasterFlags"/> / <see cref="CompositeFrame.ItemFlags"/>
/// (docs/plans/evidence-diagnostics-implementation.md §A.2/§A.3). Diagnostics only — no bit changes what is drawn.</summary>
public static class CompositeFrameFlags
{
    /// <summary>A raster: an inline scratch lease (a folded opacity / blur / fade group inside the tile) was refused — that
    /// group drew nothing into the tile.</summary>
    public const byte RasterScratchRefused = 2;
    /// <summary>A raster: the backend wrote MORE than the planned partial damage (it rastered the tile whole — a texture new
    /// this turn — or grew the damage over an image whose pixels changed under the same id). An unfaithful such raster
    /// leaves no pixel the table's snapshot can vouch for.</summary>
    public const byte RasterBeyondPlan = 4;
    /// <summary>A <see cref="CompositeKind.Group"/> item re-drawn from its retained surface (a group-cache hit).</summary>
    public const byte ItemGroupHit = 1;
    /// <summary>A <see cref="CompositeKind.Group"/> item whose surface was rendered this turn (a group-cache miss).</summary>
    public const byte ItemGroupRendered = 2;
}

public partial interface IGpuDevice
{
    /// <summary>A feedback trail (visualizer F6) is still settling after its content stopped changing: the host must keep
    /// producing (and submitting) frames until it is false. Read cross-thread; a device with no feedback route says false.</summary>
    bool HasLiveFeedback => false;
    /// <summary>True when <see cref="SubmitComposite"/> is implemented (the headless model and D3D12).</summary>
    bool SupportsComposite => false;

    /// <summary>Render thread: raster <see cref="CompositeFrame.Rasters"/> into their surfaces (CLEAR→STORE render pass
    /// each), composite <see cref="CompositeFrame.Items"/> into the PRIMARY back buffer in one CLEAR→STORE pass, and
    /// take <see cref="CompositeFrame.Present"/> for the primary swapchain's next <see cref="ISwapchain.Present"/> (used
    /// only when the swap effect allows partial presentation). Default: not supported — call only when
    /// <see cref="SupportsComposite"/> is true.
    /// <para><paramref name="target"/> is the swapchain the caller is presenting this turn. The composite route owns ONE
    /// device-wide tile pool and draws only into the primary, so D3D12 REJECTS (throws) any other target (the headless model only when its
    /// <c>RejectNonPrimaryComposite</c> is set): a detached
    /// pop-out that reached here would draw its UI into the main window's back buffer, spend the main window's frame
    /// latency credit and overwrite the main window's retained tiles. Secondary swapchains take
    /// <see cref="SubmitDrawList(ReadOnlySpan{byte}, ReadOnlySpan{ulong}, in FrameInfo, ISwapchain)"/>.</para></summary>
    void SubmitComposite(in CompositeFrame frame, ISwapchain target)
        => throw new NotSupportedException(BackendName + " does not implement SubmitComposite (retained tiles, P2).");

    /// <summary>The group-surface cache of the most recent <see cref="SubmitComposite"/> (render thread; read right after
    /// the submit for the <c>TileCensus</c>). Default: nothing cached.</summary>
    CompositeCacheStats LastCompositeCache => default;

    /// <summary>Tile placements the most recent <see cref="SubmitComposite"/> had to SKIP because their surface slot held
    /// no texture — a tile the table believes valid that composited nothing (a blank the user sees). Must be 0: the table
    /// owns tile texture lifetime (<see cref="CompositeFrame.TrimSurfaces"/>), so a placed slot always holds its texture.
    /// The census carries it (<c>TileCensus.LostPlacements</c>). Default: 0.</summary>
    int LastLostPlacements => 0;
}
