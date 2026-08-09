using System;
using FluentGpu.Animation;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Scene;
using Wavee.Core;
using Wavee.Features.Detail;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

// Surface helpers. The shell is live Mica with a FLUSH content region over it — the stock Win11 recipe: the
// LayerFillColorDefault smoke, one rounded corner facing the nav pane, a 1px left+top stroke, and NO shadow (it is not
// a floating card, and it never was an opaque canvas). Over that sits a SUBTLE, edge-transparent accent band tint —
// the WaveeMusic look, not an edge-to-edge colour flood. Kept here: the
// accent hero wash for page headers, and the album-art SHIMMER placeholder (a neutral, breathing skeleton tile —
// identical to the app's other loading skeletons) so an art slot with no bitmap yet reads as "loading", not a coloured
// hole. Used by cards, rows, the rail cover and the bar.
public static class Surfaces
{
    // Artwork is opaque content. Theme card brushes are intentionally translucent, so placeholders use explicit opaque
    // neutrals instead of allowing the surface below to wash through the image slot.
    internal static ColorF ArtworkPlaceholder =>
        Tok.Theme == ThemeKind.Dark ? ColorF.FromRgba(0x2A, 0x2A, 0x2A) : ColorF.FromRgba(0xF2, 0xF2, 0xF2);

    // How far a cover's own colour pulls the placeholder away from the neutral tile. Full strength would make a long
    // list read as a wall of saturated blocks; this keeps the slot legible as "art loading" while still being that
    // cover's colour. Same blend technique as ConcertUi's hero band.
    const float TintStrength = 0.55f;

    /// <summary>The art placeholder for an entity whose dominant cover colour is known. Falls back to the neutral tile
    /// when the colour has not been graded yet, so a slot is never a hole.</summary>
    internal static ColorF TintedPlaceholder(uint? tint) =>
        tint is { } argb ? ColorF.Lerp(ArtworkPlaceholder, WaveePalette.ToColor(argb), TintStrength) : ArtworkPlaceholder;

    /// <summary>THE art placeholder resolver. Every art slot in the app goes through here, so no call site has to
    /// know a colour exists, thread a <c>tint:</c> parameter, or remember to prefetch one: the plane is image-keyed
    /// (a cover's colour is a property of the cover), and a miss enqueues that image for grading — rendering the art
    /// IS the request. Light theme only accepts a light grading; a dark-only entry (all kind 179 ever ships) keeps the
    /// neutral tile rather than dropping a dark slab onto a pale page.</summary>
    internal static ColorF PlaceholderFor(string? url)
    {
        if (string.IsNullOrEmpty(url)) return ArtworkPlaceholder;
        bool light = Tok.Theme == ThemeKind.Light;
        return SpotifyLive.CoverColorPlane.Current.TryGetTint(url, light, out uint argb)
            ? ColorF.Lerp(ArtworkPlaceholder, WaveePalette.ToColor(argb), TintStrength)
            : ArtworkPlaceholder;
    }

    /// <summary>The full graded roles behind a cover — for PAGE chrome (hero washes, accent bars, the Play button, the
    /// shell's published material tint) rather than a placeholder tile. Null until the plane has a grading for this theme; a caller
    /// that wants the wash to appear the moment it lands should also read <c>CoverColorPlane.Current.Epoch</c>. That is
    /// safe at page scope (one subscriber) but deliberately NOT done per card — see <c>CoverShimmer</c>.</summary>
    internal static SpotifyLive.CoverColorPlane.Scheme? SchemeFor(string? url) =>
        SpotifyLive.CoverColorPlane.Current.TryGetScheme(url, Tok.Theme == ThemeKind.Light);

    /// <summary>The artwork grading used by solid chrome such as Play and Verified pills. The contrast branch is
    /// intentionally opposite the page branch: light-theme grading is the softer/lower-chroma treatment that sits
    /// comfortably on a dark hero, while dark-theme grading carries the stronger chroma a solid CTA needs against a
    /// pale wash. This is the provider's paired treatment, not an invented HSV transform. A dark-only visual-identity
    /// entry falls back to its available branch.</summary>
    internal static SpotifyLive.CoverColorPlane.Scheme? ChromeSchemeFor(string? url)
    {
        var plane = SpotifyLive.CoverColorPlane.Current;
        bool pageIsLight = Tok.Theme == ThemeKind.Light;
        return plane.TryGetScheme(url, lightTheme: !pageIsLight)
            ?? plane.TryGetScheme(url, lightTheme: pageIsLight);
    }

    // A restrained, top-anchored accent fade (Spotify's top-of-page colour band): a soft tint over the header that
    // fades out well before the tracklist, so the art colour reads as an accent — never a full-page flood. The peak
    // alpha is low and the falloff is steep (transparent by ~55% down, clear below); the previous solid WinUI-parity
    // fill (dark α≈0.235 / light α≈0.149, both stops equal — no fade) overpowered a strongly-coloured cover.
    const float HeroWashDarkA = 0.15f;    // peak at the top edge; was 60f/255f ≈0.235 painted solid
    // Light peaks slightly ABOVE dark, which looks backwards until you look at what each paints. Dark paints the art's
    // `backgroundBase` — a near-black tone over a charcoal card, where a little alpha already reads. Light paints the
    // LIFTED accent, whose strongest channel is pinned to 210 precisely so it cannot bruise the off-white card: a pale,
    // low-chroma tone, and 0.10 of it over a near-white surface is below the "is there a wash at all?" threshold on
    // anything but the most saturated cover. 0.16 is the smallest step that makes the band legible on desaturated art;
    // 0.18+ starts to read as a coloured cast at the very top edge on saturated art. Note the stop schedule below:
    // this alpha is the PEAK at y=0 and reaches 0 by HeroWashFade (55%), so the strong value only shows in the top band.
    const float HeroWashLightA = 0.16f;   // peak at the top edge; was 0.10 (and before that 38f/255f ≈0.149 solid)
    const float HeroWashFade = 0.55f;     // top→transparent by this fraction of the page; nothing below it

    /// <summary>Page wash over the content surface — a soft top-anchored accent fade (not an edge-to-edge fill).</summary>
    public static GradientSpec HeroWash(ColorF accent)
    {
        float a = Tok.Theme == ThemeKind.Light ? HeroWashLightA : HeroWashDarkA;
        return GradientDown(
            new GradientStop(0f, accent with { A = a }),
            new GradientStop(HeroWashFade, accent with { A = 0f }),
            new GradientStop(1f, accent with { A = 0f }));
    }

    /// <summary>Semantic copy protection over full-bleed artist photography. Both axes use exactly four stops (the
    /// recorder limit) and release to alpha zero at the hero seam. Peak alphas match the immersive detail hero
    /// (<see cref="DetailHeroWash"/>) rather than a near-opaque plate: the photography must stay READ as photography —
    /// a 0.96 peak flattened the hero into what read as a solid painted band, the opposite of the album pages.</summary>
    public static GradientSpec ArtistHeroVeil(ColorF accent, ArtistHeroVeilAxis axis)
    {
        ColorF layer = Tok.FillLayerDefault;
        float pull = Tok.Theme == ThemeKind.Light ? 0.16f : 0.24f;
        ColorF veil = ColorF.Lerp(layer, accent, pull);
        float top = Tok.Theme == ThemeKind.Light ? 0.42f : 0.78f;
        float mid = Tok.Theme == ThemeKind.Light ? 0.28f : 0.55f;
        if (axis == ArtistHeroVeilAxis.Vertical)
        {
            return GradientDown(
                new GradientStop(0f, veil with { A = 0f }),
                new GradientStop(0.45f, veil with { A = 0.35f }),
                new GradientStop(0.82f, veil with { A = top }),
                new GradientStop(1f, veil with { A = 0f }));
        }
        return GradientRight(
            new GradientStop(0f, veil with { A = top }),
            new GradientStop(0.30f, veil with { A = mid }),
            new GradientStop(0.62f, veil with { A = 0.22f }),
            new GradientStop(1f, veil with { A = 0f }));
    }

    public static GradientSpec DetailHeroWash(ColorF accent, bool immersive)
    {
        if (!immersive) return HeroWash(accent);
        // Peak alphas are high so the melted art edge lands on a readable plate; the falloff stays past the upper list.
        float top = Tok.Theme == ThemeKind.Light ? 0.42f : 0.78f;
        float mid = Tok.Theme == ThemeKind.Light ? 0.28f : 0.55f;
        float low = Tok.Theme == ThemeKind.Light ? 0.10f : 0.22f;
        return GradientDown(
            new GradientStop(0f, accent with { A = top }),
            new GradientStop(0.42f, accent with { A = mid }),
            new GradientStop(0.78f, accent with { A = low }),
            new GradientStop(1f, accent with { A = 0f }));
    }

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
            // OPAQUE tile (forced A=1, not the translucent card fill): a small thumb sits over the sidebar/chrome band,
            // which is an unpainted omission over the window's BASE LAYER (live Mica), so a see-through placeholder lets
            // the desktop read through — the cover becomes a washed, low-contrast smear while it loads (or a dark hole
            // when it has no art / fails). Album art is opaque content; back it with an opaque neutral so it always
            // reads as a solid tile regardless of what is behind the window.
            // Tinted from the cover's own graded colour when the plane has one, else the neutral opaque tile.
            // This is the difference between a track list of blank grey squares and one that paints its covers at once.
            return new BoxEl { Width = width, Height = height, Corners = CornerRadius4.All(corners), Fill = PlaceholderFor(url) };
        // Covers/cards: the breathing shimmer. Keyed by url so a virtualized card that REBINDS to a new cover remounts
        // the tile (a Component freezes its ctor args at mount) — the breathe + load-state read then track the new item.
        // Skeletonized(false): inside a Skel.Region's derived skeleton this opaque component would otherwise map to the
        // deriver's default bar (a stray stripe in the cover). Dropping it lets the paired Image's derived placeholder be
        // the cover square — identical to a grid card (ArtworkFill is a bare Image), so every loading cover reads the same.
        return (Embed.Comp(() => new CoverShimmer(u, decodeW, decodeH, width, height, corners)) with { Key = "shim:" + u })
            .Skeletonized(false);
    }

    /// <summary>Artwork slot: a neutral <see cref="Shimmer"/> tile under the async image (which cross-fades in over it
    /// once decoded). <paramref name="morphKey"/> tags the image as a connected-animation (Hero) participant so it flies
    /// to/from the like-tagged Home card. The tile shares ONE decode handle with the image (matched W×H, any aspect).</summary>
    public static Element Artwork(Image? image, int seed, float width, float height, float corners, string? morphKey = null,
                                  int decodePx = 0, float saturation = 1f, bool preferLargest = false)
    {
        if (image?.MosaicTiles is { Count: > 0 } tiles)
        {
            if (tiles.Count >= 4) return Mosaic(tiles, width, height, corners);
            image = new Image(tiles[0]);   // 1–3 distinct album covers → show the first as a single cover
        }
        string? url = ImageSource.UrlFor(image, preferLargest);
        if (url is { Length: 0 }) url = null;
        // Decode target: the display size by default; when decodePx>0 decode at THAT square size and COVER-fit it into the
        // slot instead. A connected-animation dest (the detail cover) passes the SAME decodePx as the Home card (256) so it
        // resolves to the SAME cached texture — the Hero fly hands off pixel-identically with NO fresh decode (killing the
        // cold first-visit cover-decode spike). The shimmer shares the chosen decode handle (matched W×H), so no fork.
        int dw = decodePx > 0 ? decodePx : (int)width, dh = decodePx > 0 ? decodePx : (int)height;
        // Shared-layout art owns its placeholder. Culling only the tagged ImageEl must not leave a separate shimmer
        // sibling painting the old large slot behind the flying overlay.
        // Un-tagged art keeps its transparent placeholder because the Shimmer tile below already fills the slot (and
        // carries the tint). A morph participant owns its own placeholder — resolve that one directly, since it has no
        // shimmer sibling to inherit from.
        ColorF placeholder = morphKey is null ? ColorF.Transparent : PlaceholderFor(url);
        Element img = url is null ? new BoxEl()
            : decodePx > 0
                ? Ui.Image(url, ImageFit.Cover, 1f, decodePx, corners, placeholder, image!.BlurHash) with { MorphId = morphKey, Saturation = saturation }
                : Ui.Image(url, width, height, corners, placeholder, image!.BlurHash) with { MorphId = morphKey, Saturation = saturation };
        return new BoxEl
        {
            ZStack = true, Width = width, Height = height, ClipToBounds = true,
            Corners = CornerRadius4.All(corners),
            Children = morphKey is null ? [Shimmer(url, dw, dh, width, height, corners), img] : [img],
        };
    }

    /// <summary>A square cover that FILLS the width its layout hands it (CSS aspect-ratio 1) — for responsive grid cells
    /// whose exact width isn't known at template time (ItemsView grid tiles). Same Cover-fit + blurhash as
    /// <see cref="Artwork"/>; pass <see cref="Radii.Full"/> for a layout-derived circular (artist) tile.</summary>
    public static Element ArtworkFill(Image? image, float corners, int decodePx = 256)
    {
        // A cover-less playlist in a fluid grid cell falls back to its first tile (the explicit-size Mosaic needs a known
        // width, which a fill cell doesn't have); the home/sidebar/detail cover-less cases use Artwork/Shelf which mosaic.
        if (image?.MosaicTiles is { Count: > 0 } tiles) image = new Image(tiles[0]);
        string? url = image?.Url is { Length: > 0 } u ? ImageSource.Normalize(u) : null;
        // Tinted from the cover's own graded colour exactly like Shimmer/Artwork. Without this a whole grid of albums
        // loads as identical grey squares while the track list beside it paints in colour — the placeholder is on
        // screen longest precisely where the most art is loading at once.
        return Ui.Image(url ?? "", ImageFit.Cover, 1f, decodePx, corners, PlaceholderFor(url), image?.BlurHash);
    }

    /// <summary>A 2×2 mosaic of 4 album covers at an EXPLICIT size — how Spotify renders a cover-less playlist. Each
    /// quadrant is url-keyed, so when the playlist's tracklist changes the changed tile re-decodes + the rest stay.</summary>
    public static Element Mosaic(System.Collections.Generic.IReadOnlyList<string> tiles, float width, float height, float corners)
    {
        int cell = (int)(width / 2);
        Element Cell(string u)
        {
            string? n = ImageSource.Normalize(u);
            return new BoxEl { Grow = 1f, ClipToBounds = true, Children = [ Ui.Image(n ?? "", ImageFit.Cover, 1f, cell, 0f, PlaceholderFor(n)) ] };
        }
        return new BoxEl
        {
            Width = width, Height = height, ClipToBounds = true, Corners = CornerRadius4.All(corners), Direction = 1,
            Children =
            [
                new BoxEl { Direction = 0, Grow = 1f, Children = [ Cell(tiles[0]), Cell(tiles[1]) ] },
                new BoxEl { Direction = 0, Grow = 1f, Children = [ Cell(tiles[2]), Cell(tiles[3]) ] },
            ],
        };
    }

    // ── section accents (the WaveeMusic "region" look: an accent-ruled header + a faintly tinted band) ─────────

    /// <summary>THE section ornament: a 20×2 accent RULE that sits under a section header's text.
    ///
    /// <para>It replaces a 3 × 22 capsule with a 1.5 radius parked to the LEFT of the header — which was, pixel for
    /// pixel, the selection-indicator geometry (a short accent bar flush against a control's edge, exactly what
    /// SelectorBar's pill and the nav rail's marker mean) doing a decorative job. Reusing selection geometry for
    /// decoration is the one thing the accent budget's first hard rule forbids: with it, every artist-page section read
    /// as "you are here", eight times down one page. A horizontal rule UNDER the text cannot be confused for a
    /// selection marker, and it is the older and quieter editorial idiom besides.</para>
    ///
    /// <para>20 and 2 are both on the 4-grid's half-step ladder and deliberately fixed: a rule that tracked the title's
    /// width would make eight sections eight different lengths, which is the raggedness the constant avoids.</para></summary>
    public static BoxEl AccentRule(ColorF accent) => new()
    {
        // AlignSelf.Start, explicitly: in the header's COLUMN the cross axis is horizontal, and a stretched rule would
        // run the full width of the section instead of being a 20-DIP mark.
        Width = AccentRuleWidth, Height = AccentRuleHeight, Shrink = 0f, AlignSelf = FlexAlign.Start,
        Fill = accent, HitTestVisible = false,
        Margin = new Edges4(0f, AccentRuleGap, 0f, 0f),
    };

    /// <summary>The section rule's geometry — one definition, so the artist page's counted header and the shared shelf
    /// header cannot drift.
    ///
    /// <para><see cref="AccentRuleGap"/> is the rule's TOP margin and stacks on top of the header column's own 2-DIP
    /// gap, so it is half of a two-part distance. At 6 that distance was 8 DIP below a 28-DIP line box — far enough
    /// that the mark floated free of the text and read as a separate object under the header (and, inside a
    /// <c>PagedShelf</c> header row, grew the row ~10 DIP past the 32-DIP chevrons it sits beside). At 2 the total is
    /// 4 DIP: a typographic rule attached to its title, and a +2 DIP shelf header instead of +10.</para></summary>
    public const float AccentRuleWidth = 20f, AccentRuleHeight = 2f, AccentRuleGap = 2f;

    /// <summary>A section header: an optional eyebrow, the title, and the <see cref="AccentRule"/> under them. The rule
    /// and the eyebrow take <paramref name="accent"/> — e.g. a <see cref="WaveePalette.Lift"/>-ed cover-extracted color,
    /// so a shelf's rule matches its content. Returns a <see cref="BoxEl"/> so call sites can layout-tweak it via
    /// <c>with</c>.</summary>
    public static BoxEl AccentHeader(string title, ColorF accent, string? eyebrow = null)
    {
        var head = WaveeType.RailHeader(title) with { MinWidth = 0f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis };
        Element[] lines = eyebrow is { Length: > 0 }
            ? [WaveeType.Eyebrow(eyebrow) with { Color = accent, MaxLines = 1, Trim = TextTrim.CharacterEllipsis }, head, AccentRule(accent)]
            : [head, AccentRule(accent)];
        return new BoxEl { Direction = 1, Gap = 2f, MinWidth = 0f, Children = lines };
    }

    /// <summary>A module header — a title, an optional subdued subtitle on the same BASELINE, and an optional trailing
    /// tools slot. No accent bar: the accent-bar variant (<see cref="AccentHeader"/>) reads as a region marker, which is
    /// right for a handful of distinct page sections (ArtistPage) and wrong for a dozen stacked modules, where it turns
    /// the page into a column of coloured rules.
    ///
    /// <para>The title deliberately carries <c>Grow = 1f</c> and NO <c>Basis</c>. That is not a style choice — a
    /// <c>Basis = 0f</c> here collapsed every header inside a <c>PagedShelf</c> to a single ellipsised letter: a shelf
    /// inserts a custom header raw into a <c>Direction = 0</c> row whose only growable child is a trailing spacer, and in
    /// a definite-width row <c>Basis = 0</c> suppresses intrinsic width entirely (FlexLayout's flex-base rule). With
    /// Basis left at NaN the intrinsic width is the real text width, and Grow still lets it fill and ellipsise when the
    /// row is genuinely tight. <c>BrowsePage</c>'s shelf header is the same shape.</para>
    ///
    /// <para>Deliberately a separate method rather than a flag on <see cref="AccentHeader"/>: that one has live callers
    /// whose look must not change. Note the engine also has a <c>Ui.SectionHeader</c> — call this one qualified.</para></summary>
    public static BoxEl SectionHeader(string title, string? subtitle = null, Element? tools = null)
        => new()
        {
            Direction = 0, Gap = Spacing.M, AlignItems = FlexAlign.Center, MinWidth = 0f,
            Children =
            [
                // Title + subtitle as ONE paragraph so the small run shares the heading's baseline (the engine has no
                // FlexAlign.Baseline). Shrink, never Grow: a SPACER — not the heading — pushes the tools to the
                // trailing edge, which is what keeps the subtitle sitting right next to the title.
                subtitle is { Length: > 0 } s
                    ? WaveeType.ModuleHeader(title, s)
                    : WaveeType.ModuleHeader(title) with { Shrink = 1f, MinWidth = 0f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
                new BoxEl { Grow = 1f, MinWidth = 0f },
                tools ?? new BoxEl(),
            ],
        };

    /// <summary>A "section band": a quiet rounded WASH behind a page section, carrying the accent as a soft tint that
    /// kisses the top edge and fades into the material by ~45% down.
    ///
    /// <para>NOT a card. It used to be one — <see cref="Tok.FillCardDefault"/> plus a <see cref="Tok.StrokeCardDefault"/>
    /// hairline — which put a bordered container around a PAGE SECTION whose content was already a row of bordered
    /// cards: a box of boxes, and one more stroke than Fluent's grouped-content look actually draws. The shell's
    /// published material already carries the page's tint (D19/D29), so the band's only remaining job is to say "these
    /// belong together", and an unbordered wash says it without adding an edge. The border is therefore GONE and the
    /// fill stays as the tint alone; one opaque top→bottom gradient keeps it a single, cheap node.</para>
    ///
    /// <para>Layout-neutral by construction: the dropped border was 1 DIP inside a <see cref="Spacing.L"/> padding box,
    /// so nothing shifts. (No live call site today — Home's hero flattened its 16-DIP inset into
    /// <c>HomeHeroLayout</c> — this is kept as the ONE recipe a future banded section must use, so the treatment cannot
    /// be re-invented with a stroke.)</para></summary>
    public static BoxEl SectionBand(Element content, ColorF accent)
    {
        ColorF card = Tok.FillCardDefault;
        // Kiss the card fill toward the accent at the very top (heavier in dark, where a faint tint would vanish), but
        // hold the card's own alpha so the surface's translucency stays uniform — only the HUE shifts at the top.
        ColorF top = ColorF.Lerp(card, accent, Tok.Theme == ThemeKind.Dark ? 0.10f : 0.06f) with { A = card.A };
        return new BoxEl
        {
            Direction = 1, Gap = Spacing.M,
            Padding = new Edges4(Spacing.L, Spacing.L, Spacing.L, Spacing.L),
            Corners = CornerRadius4.All(Radii.Card),
            Gradient = GradientDown(
                new GradientStop(0f, top),
                new GradientStop(0.45f, card),
                new GradientStop(1f, card)),
            Children = [content],
        };
    }

    /// <summary>The material layer for Home's integrated-cover hero. It keeps the translucent card alpha intact while
    /// kissing only the upper hue toward the cover accent.</summary>
    public static GradientSpec HomeHeroBackdrop(ColorF accent)
    {
        ColorF card = Tok.FillCardDefault;
        ColorF top = ColorF.Lerp(card, accent, Tok.Theme == ThemeKind.Dark ? 0.10f : 0.06f) with { A = card.A };
        return GradientDown(
            new GradientStop(0f, top),
            new GradientStop(0.45f, card),
            new GradientStop(1f, card));
    }
}

// The neutral shimmer cover tile. A Component (granular re-render) so it can read the image load-state and START/STOP
// its own opacity breathe accordingly: the UseKeyframes layout-effect is keyed by `loading`, so when the art becomes
// ready the looping pulse is replaced by a finite flat track (opacity → 1) and the frame loop can quiesce (the engine's
// "no forever-loop" rule). The breathe mirrors the engine SkeletonPulse (1.0↔0.5 over 1s) and the fill matches the app's
// skeleton blocks (DetailSkeleton's reserved cover slot, Skel.Region's derived shimmer bars) so every placeholder reads identically.
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
            var binding = UseImage(url, _decodeW, _decodeH);
            var state = binding.State;
            var failure = binding.Failure;
            if (state == ImageState.Ready) settled.Value = true;
            else if (state == ImageState.Failed && failure != ImageFailureKind.Canceled) settled.Value = true;
            else loading = state is ImageState.None or ImageState.Pending;
        }
        // Subscribe to the cover-colour plane ONLY while this tile is still the visible surface: the moment a graded
        // batch lands, mounted placeholders repaint in their covers' colours. Art tiles are the only subscribers by
        // design — subscribing at card level would turn one colour batch into a reconcile flush across every card.
        if (loading) _ = SpotifyLive.CoverColorPlane.Current.Epoch.Value;

        // On the loading→settled edge `loading` flips, the dep changes, and the effect re-seeds a finite flat track
        // (loop:false) — the looping pulse is replaced in place and the loop-track count drops so the frame loop quiesces.
        UseKeyframes(AnimChannel.Opacity, loading ? Breathe : Flat, loading ? 1000f : 1f, loading, DepKey.From(loading));   // #9: DepKey, not a boxed object[]
        // The tile is the cover's own colour, not a grey slab. This is the surface that is on screen the LONGEST for a
        // cold grid, so it is the one that decides whether a page reads as "loading its art" or as "broken".
        return new BoxEl { Width = _w, Height = _h, Corners = CornerRadius4.All(_corners), Fill = Surfaces.PlaceholderFor(_url) };
    }
}
