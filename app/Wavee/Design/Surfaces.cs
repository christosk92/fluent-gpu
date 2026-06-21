using System;
using FluentGpu.Animation;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Scene;
using Wavee.Core;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

// Surface helpers. The shell is a dark canvas with a FLOATING rounded content card (soft shadow) and a SUBTLE,
// edge-transparent accent band tint over it — the WaveeMusic "Files-style" look, not edge-to-edge Mica. Kept here: the
// accent hero wash for page headers, and the album-art SHIMMER placeholder (a neutral, breathing skeleton tile —
// identical to the app's other loading skeletons) so an art slot with no bitmap yet reads as "loading", not a coloured
// hole. Used by cards, rows, the rail cover and the bar.
public static class Surfaces
{
    /// <summary>A top-anchored accent → transparent wash for a page header, over Mica.</summary>
    public static GradientSpec HeroWash(ColorF accent) => new(
        GradientShape.Linear, 90f,
        [new GradientStop(0f, accent with { A = 0.22f }), new GradientStop(1f, accent with { A = 0f })]);

    /// <summary>A neutral album-art placeholder: the app's skeleton tile (<see cref="Tok.FillCardDefault"/>) that
    /// BREATHES while the art at <paramref name="url"/> is still loading and settles to a calm static tile once it is
    /// ready / failed / absent — so an art slot reads as "loading", never a coloured hole, and the pulse stops (a
    /// forever-loop would pin the frame loop awake). <paramref name="decodePx"/> must match the decode size of the real
    /// image stacked over it, so the load-state read shares the image's cache handle (no second decode).</summary>
    const float ShimmerMinEdge = 80f;   // below this (row/sidebar thumbs) the breathe is imperceptible — use a static tile

    /// <summary><paramref name="decodeW"/>/<paramref name="decodeH"/> must equal the decode target of the real image
    /// stacked over this tile so the load-state read shares its exact cache handle (no forked decode).</summary>
    public static Element Shimmer(string? url, int decodeW, int decodeH, float width, float height, float corners)
    {
        // Small thumbnails (track rows, sidebar, chips) get a CHEAP static neutral tile — no component, no image-epoch
        // subscription, no breathe — so a 50k-row virtualized list pays nothing per item. A url-less slot is static too.
        if (url is not { Length: > 0 } u || MathF.Min(width, height) < ShimmerMinEdge)
            // OPAQUE tile (forced A=1, not the translucent card fill): a small thumb sits over the translucent
            // sidebar/chrome, so a see-through placeholder lets the dark Mica bleed through — the cover reads as a
            // washed, low-contrast smear while it loads (or a dark hole when it has no art / fails). Album art is opaque
            // content; back it with an opaque neutral so it always reads as a solid tile, never the backdrop.
            return new BoxEl { Width = width, Height = height, Corners = CornerRadius4.All(corners), Fill = Tok.FillCardDefault with { A = 1f } };
        // Covers/cards: the breathing shimmer. Keyed by url so a virtualized card that REBINDS to a new cover remounts
        // the tile (a Component freezes its ctor args at mount) — the breathe + load-state read then track the new item.
        return Embed.Comp(() => new CoverShimmer(u, decodeW, decodeH, width, height, corners)) with { Key = "shim:" + u };
    }

    /// <summary>Artwork slot: a neutral <see cref="Shimmer"/> tile under the async image (which cross-fades in over it
    /// once decoded). <paramref name="morphKey"/> tags the image as a connected-animation (Hero) participant so it flies
    /// to/from the like-tagged Home card. The tile shares ONE decode handle with the image (matched W×H, any aspect).</summary>
    public static Element Artwork(Image? image, int seed, float width, float height, float corners, string? morphKey = null, int decodePx = 0)
    {
        string? url = image?.Url is { Length: > 0 } u ? u : null;
        // Decode target: the display size by default; when decodePx>0 decode at THAT square size and COVER-fit it into the
        // slot instead. A connected-animation dest (the detail cover) passes the SAME decodePx as the Home card (256) so it
        // resolves to the SAME cached texture — the Hero fly hands off pixel-identically with NO fresh decode (killing the
        // cold first-visit cover-decode spike). The shimmer shares the chosen decode handle (matched W×H), so no fork.
        int dw = decodePx > 0 ? decodePx : (int)width, dh = decodePx > 0 ? decodePx : (int)height;
        Element img = url is null ? new BoxEl()
            : decodePx > 0
                ? Ui.Image(url, ImageFit.Cover, 1f, decodePx, corners, ColorF.Transparent, image!.BlurHash) with { MorphId = morphKey }
                : Ui.Image(url, width, height, corners, ColorF.Transparent, image!.BlurHash) with { MorphId = morphKey };
        return new BoxEl
        {
            ZStack = true, Width = width, Height = height, ClipToBounds = true,
            Corners = CornerRadius4.All(corners),
            Children = [ Shimmer(url, dw, dh, width, height, corners), img ],
        };
    }
}

// The neutral shimmer cover tile. A Component (granular re-render) so it can read the image load-state and START/STOP
// its own opacity breathe accordingly: the UseKeyframes layout-effect is keyed by `loading`, so when the art becomes
// ready the looping pulse is replaced by a finite flat track (opacity → 1) and the frame loop can quiesce (the engine's
// "no forever-loop" rule). The breathe mirrors the engine SkeletonPulse (1.0↔0.5 over 1s) and the fill matches the app's
// skeleton blocks (DetailSkeleton's reserved cover slot, MediaCard.ShelfSkeleton) so every placeholder reads identically.
sealed class CoverShimmer : Component
{
    static readonly Keyframe[] Breathe = [new(0f, 1f), new(0.5f, 0.5f), new(1f, 1f)];
    static readonly Keyframe[] Flat = [new(0f, 1f), new(1f, 1f)];

    readonly string? _url;
    readonly int _decodeW, _decodeH;
    readonly float _w, _h, _corners;
    public CoverShimmer(string? url, int decodeW, int decodeH, float w, float h, float corners)
    { _url = url; _decodeW = decodeW; _decodeH = decodeH; _w = w; _h = h; _corners = corners; }

    public override Element Render()
    {
        // Breathe only WHILE the art is loading. Once it resolves we latch `settled` and stop calling UseImage, so the
        // tile unsubscribes from the global image epoch — a loaded cover then never re-renders on an unrelated image's
        // status change (no steady-state / scroll re-render storm across a grid of covers).
        var settled = UseRef(false);
        bool loading = false;
        if (!settled.Value && _url is { Length: > 0 } url)
        {
            // Share the displayed image's decode handle (same src + decode target) so this reads the SAME load-state and
            // forks no second decode. UseImage doesn't consume a hook cell, so the conditional call is safe.
            var state = UseImage(url, _decodeW, _decodeH).State;
            if (state is ImageState.Ready or ImageState.Failed) settled.Value = true;   // resolved → stop subscribing
            else loading = true;                                                        // None/Pending → breathe
        }
        // On the loading→settled edge `loading` flips, the dep changes, and the effect re-seeds a finite flat track
        // (loop:false) — the looping pulse is replaced in place and the loop-track count drops so the frame loop quiesces.
        UseKeyframes(AnimChannel.Opacity, loading ? Breathe : Flat, loading ? 1000f : 1f, loading, loading);
        return new BoxEl { Width = _w, Height = _h, Corners = CornerRadius4.All(_corners), Fill = Tok.FillCardDefault };
    }
}
