using System;
using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using Wavee.Core;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

// Reusable media cards composed from existing primitives. SIZE-REACTIVE: a Shelf card is built at a caller-supplied
// dynamic width (PagedShelf measures the viewport and fills it with equal cards). Every clamped label gets an EXPLICIT
// width (column context) or Grow+Basis=0 (row context) so text NEVER contributes its full single-line width to parent
// measure (Element.cs:443) — that is what made text bleed out of cards and pushed grids past the viewport edge.
public static class MediaCard
{
    public const float QuickW = 64f;     // quick-pick cover edge
    public const float QuickH = 64f;     // quick-pick tile height
    const float ShelfDecodePx = 256f;    // stable across responsive card widths, avoids resize-time redecodes
    const float FabSize  = 44f;
    internal const float FabInset = 8f;
    const float Pad      = WaveeSpace.S;

    static ColorF AccentCardFill(ColorF? accent) =>
        accent is { } a
            ? ColorF.Lerp(Tok.FillCardSecondary, a, Tok.Theme == ThemeKind.Dark ? 0.35f : 0.22f)
            : Tok.FillCardSecondary;

    static ColorF AccentCardHoverFill(ColorF? accent) =>
        accent is { } a
            ? ColorF.Lerp(Tok.FillCardDefault, a, Tok.Theme == ThemeKind.Dark ? 0.40f : 0.28f)
            : Tok.FillCardDefault;

    static Element ArtworkOrLiked(Image? cover, string uri, float width, float height, float radius, string? morphKey = null, int decodePx = 0) =>
        cover is null && LikedSongsArtwork.IsLikedUri(uri) && MathF.Abs(width - height) < 0.5f
            ? LikedSongsArtwork.Cover(width, radius, morphKey)
            : Surfaces.Artwork(cover, Seed(uri), width, height, radius, morphKey, decodePx);

    // ── Shelf card: square (album/playlist) or circular (artist) cover, sized to fill `cardW`. ───────────
    public static Element Shelf(Image? cover, string title, string subtitle, string uri,
                                Action onClick, Action onPlay, float cardW, bool circular = false, string? morphKey = null,
                                Action<string>? onNavUri = null)
    {
        float inner = MathF.Max(48f, cardW - 2f * Pad);          // cover edge = card width minus side padding
        float r = circular ? inner / 2f : WaveeRadius.Card;

        var coverStack = ZStack(
            // A neutral shimmer tile sits behind the art so a card is never an empty box — it breathes while the real
            // art loads and settles once it lands (or fails: some Spotify covers live on an auth-gated host we can't
            // fetch). Shares the decode handle with the Image below (ShelfDecodePx) so it reads the same load-state.
            cover is null && LikedSongsArtwork.IsLikedUri(uri)
                ? new BoxEl { Width = inner, Height = inner }
                : Surfaces.Shimmer(cover?.Url, (int)ShelfDecodePx, (int)ShelfDecodePx, inner, inner, r),
            // morphKey ⇒ this cover is a connected-animation (Hero) participant. Transparent placeholder so the gradient
            // shows through until the image arrives. A cover-less playlist (MosaicTiles set) renders a 2×2 album mosaic.
            (cover is null && LikedSongsArtwork.IsLikedUri(uri)
                ? LikedSongsArtwork.Cover(inner, r, morphKey)
                : cover?.MosaicTiles is { Count: >= 4 } mtiles
                ? Surfaces.Mosaic(mtiles, inner, inner, r)
                : Image(cover?.Url ?? "", ImageFit.Cover, 1f, ShelfDecodePx, r, placeholder: ColorF.Transparent) with { MorphId = morphKey }),
            // The now-playing equalizer (bottom-left, when this card's context is playing) + the play/pause FAB
            // (bottom-right, REVEALED ON HOVER). Reactive: subscribes to the playback bridge. The container carries NO
            // OnClick, so the hit walks up to the card (its HoverScale fires + the FAB reveals off the card's hover);
            // only the FAB itself is a hit target.
            // Skeletonized(false): a hover-only affordance is not skeleton content — without this the deriver maps the
            // opaque overlay to its default bar, leaving a stray stripe across the top-left of every loading cover.
            Embed.Comp(() => new NowPlayingOverlay(uri, onPlay, FabSize, cover: true, inner)).Skeletonized(false));

        return new BoxEl
        {
            // No explicit Width: the shelf cell (a column container) cross-stretches the card to the cell's LIVE width.
            // Grow=1 fills the cell's HEIGHT too: in a measured shelf the engine sizes the cell to the TALLEST card's
            // natural height and every card fills it → uniform panels, exact, no reserved worst case; content stays
            // top-aligned (cover, then text) with any slack below. The card itself just sizes to its content.
            Direction = 1, Gap = Pad, Grow = 1f,
            Padding = new Edges4(Pad, Pad, Pad, WaveeSpace.M),
            Corners = CornerRadius4.All(WaveeRadius.Card),
            Fill = Tok.FillCardSecondary, HoverFill = Tok.FillCardDefault,
            BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault,
            HoverScale = 1.02f, PressScale = 0.99f, ClipToBounds = true,
            OnClick = onClick,
            Children =
            [
                coverStack,
                new BoxEl
                {
                    Direction = 1, Gap = 2f, AlignItems = circular ? FlexAlign.Center : FlexAlign.Start,
                    Children =
                    [
                        // Explicit Width clamps the run to the card (no overflow, ellipsis at the edge). MaxLines caps how
                        // tall a verbose card can grow (and thus the whole uniform row); short text renders fewer lines.
                        WaveeType.TrackTitle(title) with { Width = inner, Wrap = TextWrap.Wrap, MaxLines = 2, Trim = TextTrim.CharacterEllipsis },
                        // The description can be an HTML fragment (links to artists/playlists, bold) — parse → rich spans
                        // (links accent + clickable via onNavUri, bold rendered, entities decoded), up to 3 lines.
                        RichText.Of(subtitle, 12f, Tok.TextSecondary, Tok.AccentTextPrimary, inner, 3, onNavUri),
                    ],
                },
            ],
        };
    }

    // ── Grid card: fills the grid cell width (no cardW), square or circular cover. For AutoGrid/UniformGrid cells. ──
    // Mirrors the Shelf card but is width-AGNOSTIC: the cover fills the cell (Surfaces.ArtworkFill, CSS aspect-ratio 1)
    // and the labels truncate to the engine-measured slot width (the proven NavCardContent pattern) — so it drops into a
    // responsive grid whose track width isn't known at template time.
    public static Element GridCard(Image? cover, string title, string subtitle, string uri,
                                   Action onClick, Action onPlay, bool circular = false, Action? onNavigate = null,
                                   ColorF? accent = null)
    {
        float r = circular ? 9999f : WaveeRadius.Card;
        var coverStack = new BoxEl
        {
            ZStack = true, ClipToBounds = true, Corners = CornerRadius4.All(r),
            Children =
            [
                Surfaces.ArtworkFill(cover, r),
                Embed.Comp(() => new NowPlayingOverlay(uri, onPlay, FabSize, cover: true, 0f, onNavigate)).Skeletonized(false),
            ],
        };
        return new BoxEl
        {
            Direction = 1, Gap = Pad, Grow = 1f, ClipToBounds = true,
            Padding = new Edges4(Pad, Pad, Pad, WaveeSpace.M),
            Corners = CornerRadius4.All(WaveeRadius.Card),
            Fill = AccentCardFill(accent), HoverFill = AccentCardHoverFill(accent),
            BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault,
            HoverScale = 1.02f, PressScale = 0.99f, OnClick = onClick,
            Children =
            [
                coverStack,
                new BoxEl
                {
                    Direction = 1, Gap = 2f, AlignItems = circular ? FlexAlign.Center : FlexAlign.Start,
                    Children =
                    [
                        WaveeType.TrackTitle(title) with { Wrap = TextWrap.Wrap, MaxLines = 2, Trim = TextTrim.CharacterEllipsis },
                        subtitle.Length == 0 ? new BoxEl()
                            : WaveeType.TrackMeta(subtitle) with { Wrap = TextWrap.Wrap, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
                    ],
                },
            ],
        };
    }

    // ── 16:9 video card (sized to a supplied cardW from a measured shelf): wide thumbnail + title + duration. ──
    public static Element VideoCard(Image? thumb, string title, string duration, string uri,
                                    Action onClick, Action onPlay, float cardW)
    {
        float inner = MathF.Max(64f, cardW - 2f * Pad);
        float ar = inner * 9f / 16f;
        return new BoxEl
        {
            Direction = 1, Gap = WaveeSpace.S, Grow = 1f, ClipToBounds = true,
            Padding = new Edges4(Pad, Pad, Pad, WaveeSpace.M),
            Corners = CornerRadius4.All(WaveeRadius.Card),
            Fill = Tok.FillCardSecondary, HoverFill = Tok.FillCardDefault,
            BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault,
            HoverScale = 1.02f, PressScale = 0.99f, OnClick = onClick,
            Children =
            [
                new BoxEl
                {
                    ZStack = true, ClipToBounds = true, Corners = CornerRadius4.All(WaveeRadius.Control),
                    Children =
                    [
                        Surfaces.Artwork(thumb, Seed(uri), inner, ar, WaveeRadius.Control, decodePx: 480),
                        Embed.Comp(() => new NowPlayingOverlay(uri, onPlay, FabSize, cover: true, 0f)).Skeletonized(false),
                    ],
                },
                WaveeType.TrackTitle(title) with { Width = inner, Wrap = TextWrap.Wrap, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
                duration.Length == 0 ? new BoxEl()
                    : WaveeType.TrackMeta(duration) with { Width = inner, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
            ],
        };
    }

    // ── Wide "jump back in" tile: cover + title (fills, ellipsised) + trailing now-playing/play overlay ───
    public static Element QuickPick(Image? cover, string title, string uri, Action onClick, Action onPlay, ColorF? accent = null)
    {
        return new BoxEl
        {
            Direction = 0, Height = QuickH, AlignItems = FlexAlign.Center, Gap = WaveeSpace.M,
            Corners = CornerRadius4.All(WaveeRadius.Card), Fill = AccentCardFill(accent), HoverFill = AccentCardHoverFill(accent),
            BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault, ClipToBounds = true, OnClick = onClick,
            Children =
            [
                // Surfaces.Artwork = a neutral shimmer/placeholder tile + the real art on top (graceful when the cover
                // is missing or on an auth-gated host that fails to fetch).
                ArtworkOrLiked(cover, uri, QuickW, QuickH, 0f),
                // Grow + Basis=0: take the remaining width (never the title's intrinsic width) → ellipsis, no overflow.
                WaveeType.TrackTitle(title) with { Grow = 1f, Basis = 0f, Wrap = TextWrap.Wrap, MaxLines = 2, Trim = TextTrim.CharacterEllipsis },
                new BoxEl
                {
                    Direction = 0, AlignItems = FlexAlign.Center,
                    Padding = new Edges4(0f, 0f, WaveeSpace.M, 0f),
                    Children = [ Embed.Comp(() => new NowPlayingOverlay(uri, onPlay, 36f, cover: false, 36f)).Skeletonized(false) ],
                },
            ],
        };
    }

    // ── List row: a HORIZONTAL media row (search / "All" lists). The SAME factory + the SAME now-playing/play affordance
    // (the shared NowPlayingOverlay) as the grid/shelf cards — only the SKIN differs (a row vs a tile). `large` is the
    // Top-Result hero variant (bigger art + title + card chrome). Optional eyebrow ("Lyrics match" / "Included in Premium"),
    // a trailing type chip, and a trailing action (save / follow). One home for a future shared context menu.
    public static Element Row(Image? cover, string title, string subtitle, string uri, bool circular,
                              Action onClick, Action onPlay,
                              string? eyebrow = null, ColorF? eyebrowColor = null, string? typeChip = null, Element? trailing = null, bool large = false,
                              string? detail = null, Action<string>? onSubtitleNav = null, string? meta = null, bool detailBelowArt = false)
    {
        float art = large ? 84f : 48f;
        float r = circular ? art / 2f : (large ? WaveeRadius.Card : 6f);
        float fab = large ? 44f : 30f;
        bool hasMeta = !large && meta is { Length: > 0 };
        bool hasDetail = !large && detail is { Length: > 0 };   // the audiobook blurb line under the subtitle (Spotify shows a 2-line description)
        bool belowArt = detailBelowArt && (hasMeta || hasDetail);
        var coverStack = new BoxEl
        {
            Width = art, Height = art, Shrink = 0f, ZStack = true, ClipToBounds = true, Corners = CornerRadius4.All(r),
            Children =
            [
                ArtworkOrLiked(cover, uri, art, art, r),
                Embed.Comp(() => new NowPlayingOverlay(uri, onPlay, fab, cover: true, art, centered: true)).Skeletonized(false),
            ],
        };
        var textKids = new System.Collections.Generic.List<Element>(3);
        if (eyebrow is { Length: > 0 }) textKids.Add(new TextEl(eyebrow) { Size = 11f, Weight = 700, Color = eyebrowColor ?? Tok.TextSecondary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis });
        textKids.Add(large
            ? new TextEl(title) { Size = 26f, Weight = 800, Color = Tok.TextPrimary, Grow = 1f, Basis = 0f, Wrap = TextWrap.NoWrap, MaxLines = 1, Trim = TextTrim.CharacterEllipsis }
            : WaveeType.TrackTitle(title) with { Grow = 1f, Basis = 0f, Wrap = TextWrap.NoWrap, MaxLines = 1, Trim = TextTrim.CharacterEllipsis });
        // Subtitle as a rich caption (matches the TrackMeta Caption style: 12px / secondary): anchor spans (artist/album)
        // become accent hyperlinks that navigate on their own, independent of the row's click. Plain text renders identically.
        textKids.Add(RichText.OfRow(subtitle, 12f, Tok.TextSecondary, Tok.AccentTextPrimary, onSubtitleNav));
        if (hasMeta && !belowArt) textKids.Add(new TextEl(meta!) { Size = 11f, Weight = 700, Color = Tok.TextPrimary, Grow = 1f, Basis = 0f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis });
        if (hasDetail && !belowArt) textKids.Add(new TextEl(detail!) { Size = 11f, Color = Tok.TextTertiary, Grow = 1f, Basis = 0f, MaxLines = 2, Wrap = TextWrap.Wrap, Trim = TextTrim.CharacterEllipsis });
        var kids = new System.Collections.Generic.List<Element>(4)
        {
            coverStack,
            new BoxEl { Direction = 1, Grow = 1f, Basis = 0f, Gap = large ? WaveeSpace.S : 1f, Children = textKids.ToArray() },
        };
        if (typeChip is { Length: > 0 }) kids.Add(RowChip(typeChip));
        if (trailing is not null) kids.Add(trailing);
        if (belowArt)
        {
            var belowKids = new System.Collections.Generic.List<Element>(2);
            if (hasMeta) belowKids.Add(new TextEl(meta!) { Size = 12f, Weight = 700, Color = Tok.TextPrimary, Grow = 1f, Basis = 0f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis });
            if (hasDetail) belowKids.Add(new TextEl(detail!) { Size = 12f, Color = Tok.TextSecondary, Grow = 1f, Basis = 0f, MaxLines = 2, Wrap = TextWrap.Wrap, Trim = TextTrim.CharacterEllipsis });

            return new BoxEl
            {
                Direction = 1, Height = float.NaN, MinHeight = 72f, Gap = WaveeSpace.S,
                Padding = new Edges4(WaveeSpace.S, WaveeSpace.S, WaveeSpace.S, WaveeSpace.S),
                Corners = CornerRadius4.All(6f),
                Fill = ColorF.Transparent,
                HoverFill = Tok.FillSubtleSecondary,
                PressedFill = Tok.FillSubtleTertiary,
                Role = AutomationRole.Button, OnClick = onClick, OnPointerExit = static () => { },
                Children =
                [
                    new BoxEl { Direction = 0, AlignItems = FlexAlign.Center, Gap = WaveeSpace.M, Children = kids.ToArray() },
                    new BoxEl { Direction = 1, Gap = 2f, Children = belowKids.ToArray() },
                ],
            };
        }

        return new BoxEl
        {
            // A detail row auto-sizes (Height NaN + MinHeight) so the blurb can take two lines; plain rows stay a tidy 64px.
            // The hero is roomier (taller card, generous inset) so the big title + subtitle aren't cramped against the cover.
            Direction = 0, Height = large ? 112f : (hasDetail ? float.NaN : 64f), MinHeight = hasDetail ? 64f : float.NaN,
            AlignItems = FlexAlign.Center, Gap = large ? WaveeSpace.L : WaveeSpace.M,
            Padding = large ? new Edges4(WaveeSpace.L, WaveeSpace.M, WaveeSpace.L, WaveeSpace.M)
                    : hasDetail ? new Edges4(WaveeSpace.S, WaveeSpace.S, WaveeSpace.S, WaveeSpace.S)
                    : new Edges4(WaveeSpace.S, 0f, WaveeSpace.S, 0f),
            Corners = CornerRadius4.All(large ? WaveeRadius.Card : 6f),
            Fill = large ? Tok.FillCardSecondary : ColorF.Transparent,
            HoverFill = large ? Tok.FillCardDefault : Tok.FillSubtleSecondary,
            PressedFill = large ? Tok.FillCardDefault : Tok.FillSubtleTertiary,
            BorderWidth = large ? 1f : 0f, BorderColor = large ? Tok.StrokeCardDefault : ColorF.Transparent,
            // The row is the interactive ancestor (OnClick + a no-op pointer-exit), so the cover's hover-revealed play FAB
            // resolves off ROW hover — identical to the card behavior.
            Role = AutomationRole.Button, OnClick = onClick, OnPointerExit = static () => { },
            Children = kids.ToArray(),
        };
    }

    static Element RowChip(string text) => new BoxEl
    {
        Shrink = 0f, Padding = new Edges4(10f, 3f, 10f, 3f), Corners = CornerRadius4.All(11f), Fill = Tok.FillSubtleSecondary,
        Children = [ new TextEl(text) { Size = 10f, Weight = 700, Color = Tok.TextTertiary, CharSpacing = 40f } ],
    };

    // A stable-ish placeholder seed from the card's context uri (so each card gets its own gradient cover tone).
    static int Seed(string s) => (s ?? string.Empty).GetHashCode() & 0x7fffffff;

    // ── Accent Play/Pause FAB (own hover/press feedback) — glyph supplied by the caller (play vs pause). ──
    internal static Element PlayFab(Action onClick, string glyph, float size = FabSize) => new BoxEl
    {
        Width = size, Height = size, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
        Corners = CornerRadius4.All(size / 2f),
        Fill = Tok.AccentDefault, HoverFill = Tok.AccentSecondary, PressedFill = Tok.AccentTertiary,
        Shadow = Elevation.Card, HoverScale = 1.07f, PressScale = 0.92f,
        OnClick = onClick, Cursor = CursorId.Hand,
        Children = [ FabGlyph(glyph, size * 0.42f, Tok.TextOnAccentPrimary) ],
    };

    internal static Element CoverActionFab(Action onClick, string glyph, string tooltip, float size) => ToolTip.Wrap(new BoxEl
    {
        Width = size, Height = size, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
        Corners = CornerRadius4.All(size / 2f),
        Fill = ColorF.FromRgba(0, 0, 0, 185),
        HoverFill = ColorF.FromRgba(20, 20, 20, 225),
        PressedFill = ColorF.FromRgba(0, 0, 0, 245),
        BorderWidth = 1f, BorderColor = ColorF.FromRgba(255, 255, 255, 70),
        Shadow = Elevation.Card, HoverScale = 1.07f, PressScale = 0.92f,
        OnClick = onClick, Cursor = CursorId.Hand, Role = AutomationRole.Button, Focusable = true,
        Children = [ FabGlyph(glyph, size * 0.40f, ColorF.FromRgba(255, 255, 255)) ],
    }, tooltip);

    static TextEl FabGlyph(string glyph, float size, ColorF color) => new(glyph)
    {
        Width = size,
        Height = size,
        Size = size,
        LineHeight = size,
        FontFamily = Theme.IconFont,
        Color = color,
    };

}

// The reactive now-playing / play affordance on a content card (mirrors WaveeMusic's ContentCard state model):
//   • the play/pause FAB is REVEALED ON HOVER (and shows PAUSE when this card's context is the one playing);
//   • when this card's context IS playing, the now-playing EQUALIZER shows (bottom-left on a cover; in the trailing
//     slot, hidden on hover so the FAB takes over). "Am I the playing context?" = my uri == the playing context uri,
//     or the current track's album/artist uri (so album/artist cards light up too). Clicking the FAB toggles
//     pause/resume when it's the active context, else plays this context.
sealed class NowPlayingOverlay : Component
{
    readonly string _uri;
    readonly Action _onPlay;
    readonly Action? _onNavigate;
    readonly float _fab;
    readonly bool _cover;
    readonly float _inner;
    readonly bool _centered;
    public NowPlayingOverlay(string uri, Action onPlay, float fab, bool cover, float inner, Action? onNavigate = null, bool centered = false)
    { _uri = uri; _onPlay = onPlay; _fab = fab; _cover = cover; _inner = inner; _onNavigate = onNavigate; _centered = centered; }

    public override Element Render()
    {
        var b = UseContext(PlaybackBridge.Slot);
        // Re-render only when THIS card's own visual state changes — not on every track skip / play-pause of OTHER
        // contexts. Reading CurrentContext/CurrentTrack/IsPlaying directly here would re-render EVERY visible card's
        // overlay on any playback change (N small element-tree allocations per event). Instead, a UseSignalEffect bridges
        // those hot playback signals into a COARSE retained (active, playingHere) signal whose setter suppresses on
        // equality (Signal.cs) — so an unrelated change re-runs only this cheap effect (Matches, zero-alloc) and, when
        // the pair is unchanged (the common case for a non-playing card), schedules NO re-render. `playing` is only ever
        // read when active, where it equals playingHere, so this coarse pair fully captures the overlay's visual state.
        var vis = UseSignal((active: false, playingHere: false));
        UseSignalEffect(() =>
        {
            var ctx = b?.CurrentContext.Value;
            var track = b?.CurrentTrack.Value;
            bool a = Matches(_uri, ctx, track);
            vis.Value = (a, a && (b?.IsPlaying.Value ?? false));   // short-circuit: a non-active card never subscribes to IsPlaying
        });
        var (active, playingHere) = vis.Value;
        bool playing = playingHere;   // the equalizer animates iff this card's context is the one actively playing

        void Toggle()
        {
            if (b is null) { _onPlay(); return; }
            if (Matches(_uri, b.CurrentContext.Peek(), b.CurrentTrack.Peek()))
            {
                bool p = b.IsPlaying.Peek();
                b.IsPlaying.Value = !p;                              // optimistic, then the player reconciles
                if (p) _ = b.Player.PauseAsync(); else _ = b.Player.ResumeAsync();
            }
            else _onPlay();
        }

        // FAB revealed on card hover: the wrapper is non-interactive, so its HoverOpacity resolves off the CARD's
        // hover (the FAB inside keeps its own click). Pause glyph when this context is the one playing. A gentle
        // ~180ms decelerate fade (not the snappy default) so the button eases in rather than popping.
        Element reveal = new BoxEl
        {
            Opacity = 0f, HoverOpacity = 1f, HoverDurationMs = 180f, HoverEasing = Easing.FluentDecelerate,
            Direction = 1, AlignItems = FlexAlign.End, Gap = 7f,
            Children = _onNavigate is null
                ? [ MediaCard.PlayFab(Toggle, playingHere ? Icons.Pause : Icons.Play, _fab) ]
                : [
                    MediaCard.CoverActionFab(_onNavigate, Icons.OpenInNewWindow, "Go to album", MathF.Max(34f, _fab - 8f)),
                    MediaCard.PlayFab(Toggle, playingHere ? Icons.Pause : Icons.Play, _fab)
                  ],
        };
        Element EqPill() => new BoxEl
        {
            Padding = new Edges4(5f, 3f, 5f, 3f), Corners = CornerRadius4.All(4f), Fill = ColorF.FromRgba(0, 0, 0, 150),
            Children = [ WaveeEqualizer.Of(playing, Tok.AccentTextPrimary, 14f) ],
        };

        if (_cover && _centered)
        {
            // Small ROW art (search "All" rows): the equalizer CENTERED at rest (hidden on hover), the play FAB centered
            // over a hover scrim — Spotify's row affordance. SAME component, a row-fit layout (vs the card's bottom corners).
            // A centering flex box (NOT a ZStack): a ZStack honors AlignItems (vertical) but ignores Justify, so it would
            // pin the FAB to the LEFT edge instead of centering it over the art. A single-child flex centers on BOTH axes.
            Element rowFab = new BoxEl
            {
                Width = _inner, Height = _inner, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                Opacity = 0f, HoverOpacity = 1f, HoverDurationMs = 180f, HoverEasing = Easing.FluentDecelerate,
                Fill = ColorF.FromRgba(0, 0, 0, 110),
                Children = [ MediaCard.PlayFab(Toggle, playingHere ? Icons.Pause : Icons.Play, _fab) ],
            };
            return new BoxEl
            {
                Width = _inner, Height = _inner, ZStack = true,
                Children =
                [
                    active
                        ? new BoxEl { Width = _inner, Height = _inner, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, HoverOpacity = 0f, Children = [ EqPill() ] }
                        : new BoxEl(),
                    rowFab,
                ],
            };
        }

        if (_cover)
            // FILL the cover rather than sizing to a captured `_inner`: this overlay rides in an Embed.Comp whose
            // template closure FREEZES at first mount, so a captured width goes stale when the card later re-fits wider
            // (the FAB then floats at the center of the grown cover). Grow=1 + ZStack-fill children always match the
            // live cover box, no matter the fitted width.
            return new BoxEl
            {
                Grow = 1f, ZStack = true,
                Children =
                [
                    new BoxEl   // equalizer — bottom-left, only when this card is the active context
                    {
                        Grow = 1f, Direction = 1, Justify = FlexJustify.End, AlignItems = FlexAlign.Start,
                        Padding = new Edges4(MediaCard.FabInset, 0f, 0f, MediaCard.FabInset),
                        Children = [ active ? EqPill() : new BoxEl() ],
                    },
                    new BoxEl   // FAB — bottom-right, revealed on hover
                    {
                        Grow = 1f, Direction = 1, Justify = FlexJustify.End, AlignItems = FlexAlign.End,
                        Padding = new Edges4(0f, 0f, MediaCard.FabInset, MediaCard.FabInset),
                        Children = [ reveal ],
                    },
                ],
            };

        // Inline trailing slot (QuickPick): the equalizer at rest (hidden on hover) under the hover-revealed FAB.
        return new BoxEl
        {
            Width = _fab, Height = _fab, ZStack = true, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            Children =
            [
                active
                    ? new BoxEl { AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, HoverOpacity = 0f,
                                  Children = [ WaveeEqualizer.Of(playing, Tok.AccentTextPrimary, 14f) ] }
                    : new BoxEl(),
                reveal,
            ],
        };
    }

    static bool Matches(string uri, string? contextUri, Track? track)
    {
        if (string.IsNullOrEmpty(uri)) return false;
        if (!string.IsNullOrEmpty(contextUri) && string.Equals(uri, contextUri, StringComparison.OrdinalIgnoreCase)) return true;
        if (track is null) return false;
        if (string.Equals(uri, track.Uri, StringComparison.OrdinalIgnoreCase)) return true;   // a TRACK row lights up when ITS track is the one playing
        if (string.Equals(uri, track.Album.Uri, StringComparison.OrdinalIgnoreCase)) return true;
        foreach (var a in track.Artists)
            if (string.Equals(uri, a.Uri, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }
}
