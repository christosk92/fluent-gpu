using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Scene;
using FluentGpu.Signals;
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

    /// <summary>Wide Home destination used by the concert feature. It keeps one responsive layered tree and avoids the
    /// stateful portrait editorial card's image zoom, acrylic, and shelf-specific clipping behavior.</summary>
    public static Element WideEditorialDestination(Image? artwork, string eyebrow, string title, string subtitle,
        string actionLabel, Action onClick, float fallbackWidth = 1000f) =>
        ConcertUi.WideEditorialDestination(artwork, eyebrow, title, subtitle, actionLabel, onClick, fallbackWidth);

    static ColorF AccentCardFill(ColorF? accent) =>
        accent is { } a
            ? ColorF.Lerp(Tok.FillCardDefault, a, Tok.Theme == ThemeKind.Dark ? 0.12f : 0.08f)
            : Tok.FillCardDefault;

    static ColorF AccentCardHoverFill(ColorF? accent) =>
        accent is { } a
            ? ColorF.Lerp(Tok.FillControlSecondary, a, Tok.Theme == ThemeKind.Dark ? 0.18f : 0.12f)
            : Tok.FillControlSecondary;

    // Hover-revealed corner "…" (top-right of the cover — the FAB's opposite corner): opens the card's attached context
    // menu (the WithMenu at the card root) anchored at the button — the engine's ClickRequestsContext re-enters the
    // context-request funnel here and the walk finds the card's OnContextRequested. Same dark-glass chrome as
    // CoverActionFab; hover-revealed like the play FAB. Rendered only when the card actually carries a menu.
    // Skeletonized(false): a hover-only affordance is not skeleton content (the NowPlayingOverlay rule).
    static Element MoreCorner(bool show, bool persistent = false) => show
        ? new BoxEl
        {
            Grow = 1f, Direction = 1, AlignItems = FlexAlign.End,
            Padding = new Edges4(0f, FabInset, FabInset, 0f),
            Opacity = persistent ? 1f : 0f, HoverOpacity = 1f, HoverDurationMs = 180f, HoverEasing = Easing.FluentDecelerate,
            Children =
            [
                new BoxEl
                {
                    Width = 30f, Height = 30f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                    Corners = CornerRadius4.All(15f),
                    Fill = ColorF.FromRgba(0, 0, 0, 185),
                    HoverFill = ColorF.FromRgba(20, 20, 20, 225),
                    PressedFill = ColorF.FromRgba(0, 0, 0, 245),
                    BorderWidth = 1f, BorderColor = ColorF.FromRgba(255, 255, 255, 70),
                    Shadow = Elevation.Card, HoverScale = 1.07f, PressScale = 0.92f,
                    ClickRequestsContext = true, Cursor = CursorId.Hand, Role = AutomationRole.Button,
                    Children = [ FabGlyph(Mdl.More, 13f, ColorF.FromRgba(255, 255, 255)) ],
                },
            ],
        }.Skeletonized(false)
        : new BoxEl();

    static Element MoreInline(bool show, bool onDark = false, float size = 36f) => show
        ? new BoxEl
        {
            Width = size, Height = size, Shrink = 0f,
            AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            Corners = CornerRadius4.All(size / 2f),
            Fill = onDark ? ColorF.FromRgba(0, 0, 0, 132) : ColorF.Transparent,
            HoverFill = onDark ? ColorF.FromRgba(0, 0, 0, 190) : Tok.FillSubtleSecondary,
            PressedFill = onDark ? ColorF.FromRgba(0, 0, 0, 220) : Tok.FillSubtleTertiary,
            BorderWidth = onDark ? 1f : 0f,
            BorderColor = onDark ? ColorF.FromRgba(255, 255, 255, 58) : ColorF.Transparent,
            ClickRequestsContext = true, Cursor = CursorId.Hand, Role = AutomationRole.Button,
            Children = [ FabGlyph(Mdl.More, 15f, onDark ? ColorF.FromRgba(255, 255, 255) : Tok.TextSecondary) ],
        }.Skeletonized(false)
        : new BoxEl();

    static Element KindChip(HomeCardKind kind)
    {
        string label = kind switch
        {
            HomeCardKind.Track => Loc.Get(Strings.Search.TypeSong),
            HomeCardKind.Artist => Loc.Get(Strings.Search.TypeArtist),
            HomeCardKind.Album => Loc.Get(Strings.Search.TypeAlbum),
            _ => Loc.Get(Strings.Search.TypePlaylist),
        };
        return new BoxEl
        {
            Shrink = 0f, Padding = new Edges4(10f, 5f, 10f, 5f), Corners = CornerRadius4.All(12f),
            Fill = ColorF.FromRgba(0, 0, 0, 142), BorderWidth = 1f,
            BorderColor = ColorF.FromRgba(255, 255, 255, 55), HitTestVisible = false,
            Children = [ new TextEl(label) { Size = 10.5f, Weight = 700, Color = ColorF.FromRgba(255, 255, 255, 225), CharSpacing = 30f } ],
        };
    }

    static Element ArtworkOrLiked(Image? cover, string uri, float width, float height, float radius, string? morphKey = null, int decodePx = 0, Element? diagnostics = null)
    {
        var art = cover is null && LikedSongsArtwork.IsLikedUri(uri) && MathF.Abs(width - height) < 0.5f
            ? LikedSongsArtwork.Cover(width, radius, morphKey)
            : Surfaces.Artwork(cover, Seed(uri), width, height, radius, morphKey, decodePx);
        return diagnostics is null
            ? art
            : new BoxEl
            {
                Width = width,
                Height = height,
                ZStack = true,
                ClipToBounds = true,
                Children = [ art, diagnostics ],
            };
    }

    // ── Shelf card: square (album/playlist) or circular (artist) cover, sized to fill `cardW`. ───────────
    // `menu` (all five factories): an optional attached context menu (right-click / Menu key / long-press) — the
    // calling component resolves the overlay service + builds the lazy model (Menus.CardAttach); null = no menu.
    public static Element Shelf(Image? cover, string title, string subtitle, string uri,
                                Action onClick, Action onPlay, float cardW, bool circular = false, string? morphKey = null,
                                Action<string>? onNavUri = null, MenuAttach? menu = null)
    {
        float inner = MathF.Max(48f, cardW - 2f * Pad);
        float r = circular ? inner / 2f : WaveeRadius.Card;

        Element face = circular
            // A missing artist photo must still be an intentional card, not a blank gray rectangle. PersonPicture gives
            // us WinUI initials/contact fallback and the same circular crop when a real URL is present.
            ? PersonPicture.Create("", inner, displayName: title, imageSourcePath: cover?.Url)
            : cover is null && LikedSongsArtwork.IsLikedUri(uri)
                ? LikedSongsArtwork.Cover(inner, r, morphKey)
                : cover?.MosaicTiles is { Count: >= 4 } mtiles
                    ? Surfaces.Mosaic(mtiles, inner, inner, r)
                    : ZStack(
                        // A neutral shimmer tile sits behind the art so a card is never an empty box — it breathes while
                        // the real art loads and settles once it lands.
                        Surfaces.Shimmer(cover?.Url, (int)ShelfDecodePx, (int)ShelfDecodePx, inner, inner, r),
                        Image(cover?.Url ?? "", ImageFit.Cover, 1f, ShelfDecodePx, r, placeholder: ColorF.Transparent)
                            with { MorphId = morphKey });

        var coverStack = new BoxEl
        {
            Width = inner, Height = inner, ZStack = true, ClipToBounds = true, Corners = CornerRadius4.All(r),
            HoverScale = Motion.ReducedMotion ? 1f : 1.035f,
            HoverDurationMs = 300f, HoverEasing = Easing.FluentDecelerate,
            Children =
            [
            face,
            // The now-playing equalizer (bottom-left, when this card's context is playing) + the play/pause FAB
            // (bottom-right, REVEALED ON HOVER). Reactive: subscribes to the playback bridge. The container carries NO
            // OnClick, so the hit walks up to the card (its HoverScale fires + the FAB reveals off the card's hover);
            // only the FAB itself is a hit target.
            // Skeletonized(false): a hover-only affordance is not skeleton content — without this the deriver maps the
            // opaque overlay to its default bar, leaving a stray stripe across the top-left of every loading cover.
            Embed.Comp(() => new NowPlayingOverlay(uri, onPlay, FabSize, cover: true, inner)).Skeletonized(false),
            MoreCorner(menu is not null),
            ],
        };

        var content = new BoxEl
        {
            // No explicit Width: the shelf cell (a column container) cross-stretches the card to the cell's LIVE width.
            // Grow=1 fills the cell's HEIGHT too: in a measured shelf the engine sizes the cell to the TALLEST card's
            // natural height and every card fills it → uniform panels, exact, no reserved worst case; content stays
            // top-aligned (cover, then text) with any slack below. The card itself just sizes to its content.
            Direction = 1, Gap = Pad, Grow = 1f,
            Padding = new Edges4(Pad, Pad, Pad, WaveeSpace.M),
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
                        WaveeType.TrackTitle(title) with { Width = inner, Wrap = TextWrap.NoWrap, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
                        // The description can be an HTML fragment (links to artists/playlists, bold) — parse → rich spans
                        // (links accent + clickable via onNavUri, bold rendered, entities decoded), capped at two lines.
                        RichText.Of(subtitle, 12f, Tok.TextSecondary, Tok.AccentTextPrimary, inner, 2, onNavUri),
                    ],
                },
            ],
        };
        var card = new BoxEl
        {
            Grow = 1f, ZStack = true, Corners = CornerRadius4.All(WaveeRadius.Card), ClipToBounds = true,
            OnClick = onClick, PressScale = 0.99f,
            WhileHover = Motion.ReducedMotion ? null : new MotionTarget { OffsetY = -4f },
            WhilePressed = Motion.ReducedMotion ? null : new MotionTarget { Scale = 0.99f, OffsetY = -1f },
            Transition = MotionTok.ControlNormal,
            Children =
            [
                new BoxEl
                {
                    Grow = 1f, Corners = CornerRadius4.All(WaveeRadius.Card),
                    Fill = Tok.FillCardDefault, BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault,
                    Shadow = Elevation.Card, Opacity = 0f, HoverOpacity = 1f,
                    HoverDurationMs = 180f, HoverEasing = Easing.FluentDecelerate, HitTestVisible = false,
                },
                content,
            ],
        }.WithMenu(menu);
        return new BoxEl { Grow = 1f, Direction = 1, Padding = new Edges4(0f, 4f, 0f, 2f), Children = [ card ] };
    }

    // ── Grid card: fills the grid cell width (no cardW), square or circular cover. For AutoGrid/UniformGrid cells. ──
    // Mirrors the Shelf card but is width-AGNOSTIC: the cover fills the cell (Surfaces.ArtworkFill, CSS aspect-ratio 1)
    // and the labels truncate to the engine-measured slot width (the proven NavCardContent pattern) — so it drops into a
    // responsive grid whose track width isn't known at template time.
    /// <summary>Dense horizontal Home card used by canonical “Made For {0}” modules.</summary>
    public static Element Compact(Image? cover, string title, string subtitle, string uri, HomeCardKind kind,
                                  Action onClick, Action onPlay, float art, float cardH, MenuAttach? menu = null)
    {
        bool circular = kind == HomeCardKind.Artist;
        float radius = circular ? art / 2f : WaveeRadius.Card;
        float action = art <= 100f ? 40f : 44f;
        var artBox = new BoxEl
        {
            Width = art, Height = art, Shrink = 0f, ZStack = true, ClipToBounds = true,
            Corners = CornerRadius4.All(radius), HoverScale = Motion.ReducedMotion ? 1f : 1.02f,
            HoverDurationMs = 300f, HoverEasing = Easing.FluentDecelerate,
            Children =
            [
                circular
                    ? PersonPicture.Create("", art, displayName: title, imageSourcePath: cover?.Url)
                    : ArtworkOrLiked(cover, uri, art, art, radius, decodePx: 192),
            ],
        };
        var card = new BoxEl
        {
            Direction = 0, Height = cardH, Gap = WaveeSpace.S, AlignItems = FlexAlign.Center,
            Padding = new Edges4(WaveeSpace.S, MathF.Max(0f, (cardH - art) * 0.5f), WaveeSpace.S, MathF.Max(0f, (cardH - art) * 0.5f)),
            Corners = CornerRadius4.All(WaveeRadius.Card), ClipToBounds = true,
            Fill = Tok.FillCardDefault, HoverFill = Tok.FillCardSecondary,
            BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault, Shadow = Elevation.Card,
            OnClick = onClick, PressScale = 0.99f,
            WhileHover = Motion.ReducedMotion ? null : new MotionTarget { OffsetY = -3f },
            WhilePressed = Motion.ReducedMotion ? null : new MotionTarget { Scale = 0.99f, OffsetY = -1f },
            Transition = MotionTok.ControlNormal,
            Children =
            [
                artBox,
                new BoxEl
                {
                    Direction = 1, Grow = 1f, Basis = 0f, Gap = 3f, Justify = FlexJustify.Center,
                    Children =
                    [
                        WaveeType.TrackTitle(title) with { Wrap = TextWrap.NoWrap, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
                        subtitle.Length == 0 ? new BoxEl() : WaveeType.TrackMeta(subtitle) with
                            { Wrap = TextWrap.NoWrap, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
                    ],
                },
                Embed.Comp(() => new NowPlayingOverlay(uri, onPlay, action, cover: false, action, persistent: true)).Skeletonized(false),
                MoreInline(menu is not null),
            ],
        };
        return new BoxEl { Direction = 1, Padding = new Edges4(0f, 3f, 0f, 3f), Children = [ card.WithMenu(menu) ] };
    }

    public static Element GridCard(Image? cover, string title, string subtitle, string uri,
                                   Action onClick, Action onPlay, bool circular = false, Action? onNavigate = null,
                                   ColorF? accent = null, MenuAttach? menu = null)
    {
        float r = circular ? 9999f : WaveeRadius.Card;
        var coverStack = new BoxEl
        {
            ZStack = true, ClipToBounds = true, Corners = CornerRadius4.All(r),
            Children =
            [
                Surfaces.ArtworkFill(cover, r),
                Embed.Comp(() => new NowPlayingOverlay(uri, onPlay, FabSize, cover: true, 0f, onNavigate)).Skeletonized(false),
                MoreCorner(menu is not null),
            ],
        };
        var card = new BoxEl
        {
            // A grid row may reserve trailing space as its vertical gutter. Do not flex-grow into that space: doing so
            // stretches the card past its square-cover + two-label geometry, creates a dead footer, and consumes the
            // intended gap before the next row.
            Direction = 1, Gap = Pad, ClipToBounds = true,
            Padding = new Edges4(Pad, Pad, Pad, WaveeSpace.M),
            Corners = CornerRadius4.All(WaveeRadius.Card),
            Fill = AccentCardFill(accent), HoverFill = AccentCardHoverFill(accent),
            BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault,
            Shadow = Elevation.Card,
            HoverScale = 1.02f, PressScale = 0.99f, OnClick = onClick,
            Children =
            [
                coverStack,
                new BoxEl
                {
                    Direction = 1, Gap = 2f, AlignItems = circular ? FlexAlign.Center : FlexAlign.Start,
                    Children =
                    [
                        WaveeType.TrackTitle(title) with { Wrap = TextWrap.NoWrap, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
                        subtitle.Length == 0 ? new BoxEl()
                            : WaveeType.TrackMeta(subtitle) with { Wrap = TextWrap.Wrap, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
                    ],
                },
            ],
        };
        return card.WithMenu(menu);
    }

    // Editorial home card: intentionally reserved for HomeFeedBaselineSectionData. Normal home sections keep the regular
    // Shelf card; this is the periodic Apple-Music-style visual interruption. Structure mirrors Apple Music's editorial
    // card: FULL-BLEED portrait artwork with a frosted-glass band pinned to the bottom carrying eyebrow + title + subtitle
    // OVER the art. The band is an acrylic backdrop-blur sized to just the copy (lower third) — not half the card — so the
    // artwork stays the hero. (The engine acrylic composite now clips to the scroll viewport, so this band no longer bleeds
    // over the pinned top nav / player bar; that was the earlier overlap bug, not a reason to drop the frosted look.)
    //
    // Stateful (EditorialCardCore): hover zooms the ARTWORK inside the card's own rounded clip (a root HoverScale pushed
    // the outermost shelf cards past the viewport's exact-bounds clip — squared corners), expands the description, and —
    // after a swept countdown ring — peeks the recommendation's preview tracks (previewsOf, the feedBaselineLookup cache).
    // Component props freeze at mount, so the Key remounts on identity or a ≥16px fitted-width change (shelf re-fit).
    public static Element EditorialCard(Image? cover, string? eyebrow, string title, string subtitle, string uri, HomeCardKind kind,
                                        Action onClick, Action onPlay, float cardW, MenuAttach? menu = null,
                                        Func<string, IReadOnlyList<HomePreviewTrack>?>? previewsOf = null,
                                        IReadSignal<int>? previewsEpoch = null)
        => Embed.Comp(() => new EditorialCardCore(cover, eyebrow, title, subtitle, uri, kind, onClick, onPlay, cardW, menu,
                                                  previewsOf, previewsEpoch))
           with
           {
               Key = $"edcard:{uri}:{(int)(cardW / 16f)}",
               // The deriver can't see into the component — hand it the resting card shape (no hover, no peek).
               SkeletonProxy = () => EditorialCardCore.Build(cover, eyebrow, title, subtitle, uri, kind, onClick, onPlay,
                   MathF.Min(cardW, 360f), menu, hovered: false, peek: null, counting: false,
                   arcCapture: null, spotlightCenter: new Point2(0.5f, 0.35f), pointerMove: null, pointerExit: null),
           };

    // The stateful editorial-card core. Hover choreography (every channel animated — no snaps):
    //   • the artwork zooms 1.045 INSIDE the card's rounded clip (rides the card's inherited hover progress);
    //   • the description expands 2 → 5 lines while the frosted band grows to make space (CardResizeHeight reflow);
    //   • a thin countdown ring sweeps once (StrokeTrimEnd 0→1); when it completes — and the preview batch is cached —
    //     the description swaps to the recommendation's preview tracks, each row fading in (PageFade enter).
    // Pointer exit rewinds everything; the countdown is epoch-guarded so a quick re-hover never resurrects a stale peek.
    internal sealed class EditorialCardCore : Component
    {
        const int CountdownMs = 1400;
        const int PeekRows = 5;

        readonly Image? _cover; readonly string? _eyebrow; readonly string _title; readonly string _subtitle;
        readonly string _uri; readonly HomeCardKind _kind; readonly Action _onClick; readonly Action _onPlay; readonly float _cardW;
        readonly MenuAttach? _menu;
        readonly Func<string, IReadOnlyList<HomePreviewTrack>?>? _previewsOf;
        readonly IReadSignal<int>? _previewsEpoch;

        readonly Signal<bool> _hovered = new(false);
        readonly Signal<bool> _revealed = new(false);
        readonly Signal<Point2> _spotlightCenter = new(new Point2(0.5f, 0.35f));
        int _hoverEpoch;                                   // bumped on every hover edge — abandons stale countdown tails
        NodeHandle _arcNode = NodeHandle.Null;

        public EditorialCardCore(Image? cover, string? eyebrow, string title, string subtitle, string uri, HomeCardKind kind,
                                 Action onClick, Action onPlay, float cardW, MenuAttach? menu,
                                 Func<string, IReadOnlyList<HomePreviewTrack>?>? previewsOf, IReadSignal<int>? previewsEpoch)
        {
            _cover = cover; _eyebrow = eyebrow; _title = title; _subtitle = subtitle; _uri = uri; _kind = kind;
            _onClick = onClick; _onPlay = onPlay; _cardW = cardW; _menu = menu;
            _previewsOf = previewsOf; _previewsEpoch = previewsEpoch;
        }

        void HoverStart()
        {
            if (_hovered.Peek()) return;
            _hovered.Value = true;
            int epoch = ++_hoverEpoch;
            _ = RevealAfterCountdownAsync(epoch);
        }

        async Task RevealAfterCountdownAsync(int epoch)
        {
            await Task.Delay(CountdownMs).ConfigureAwait(false);
            if (epoch == _hoverEpoch && _hovered.Peek() && !_revealed.Peek()) _revealed.Value = true;
        }

        void HoverEnd()
        {
            _hoverEpoch++;
            if (_hovered.Peek()) _hovered.Value = false;
            if (_revealed.Peek()) _revealed.Value = false;
        }

        void PointerMove(Point2 local)
        {
            HoverStart();
            if (Motion.ReducedMotion) return;
            float w = MathF.Max(1f, _cardW);
            float h = MathF.Max(360f, _cardW * 1.25f);
            _spotlightCenter.Value = new Point2(Math.Clamp(local.X / w, 0f, 1f), Math.Clamp(local.Y / h, 0f, 1f));
        }

        public override Element Render()
        {
            bool hovered = _hovered.Value;
            bool revealed = _revealed.Value;
            _ = _previewsEpoch?.Value;                    // subscribe: re-render the moment the preview batch lands
            var previews = _previewsOf?.Invoke(_uri);
            bool hasPeek = previews is { Count: > 0 };
            bool counting = hovered && !revealed && hasPeek;

            // Sweep the countdown ring exactly once per hover: the ring child mounts with `counting`, its OnRealized
            // captures the arc node, and this post-commit effect seeds the one-shot trim (the ProgressRing pattern —
            // UseKeyframes only targets the host node, so a child arc is driven through Context.Anim directly).
            UseLayoutEffect(() =>
            {
                if (!counting || Motion.ReducedMotion) return;
                var anim = Context.Anim; var scene = Context.Scene;
                var arc = _arcNode;
                if (anim is null || scene is null || arc.IsNull || !scene.IsLive(arc)) return;
                anim.Keyframes(arc, AnimChannel.StrokeTrimEnd,
                    new Keyframe[] { new(0f, 0f, Easing.Linear), new(1f, 1f, Easing.Linear) }, CountdownMs, false);
            }, counting, _hoverEpoch);

            return Build(_cover, _eyebrow, _title, _subtitle, _uri, _kind, _onClick, _onPlay, _cardW, _menu,
                hovered, revealed && hasPeek ? previews : null, counting,
                arcCapture: h => _arcNode = h, spotlightCenter: Prop<Point2>.FromSignal(_spotlightCenter),
                pointerMove: PointerMove, pointerExit: HoverEnd);
        }

        internal static Element Build(Image? cover, string? eyebrow, string title, string subtitle, string uri, HomeCardKind kind,
                                      Action onClick, Action onPlay, float cardW, MenuAttach? menu,
                                      bool hovered, IReadOnlyList<HomePreviewTrack>? peek, bool counting,
                                      Action<NodeHandle>? arcCapture, Prop<Point2> spotlightCenter,
                                      Action<Point2>? pointerMove, Action? pointerExit)
        {
            const float editorialScale = 1.25f;
            float artH = MathF.Max(360f, cardW * 1.25f);
            float aspect = cardW / artH;
            float inset = Math.Clamp(cardW * 0.055f, 14f, 20f);
            // Empty frosted space above the copy the feather ramps across. PROPORTIONAL to the art (≈ a quarter of the
            // card), not a fixed 52px: a fixed pad on a tall editorial card left a short, abrupt wash — the frost has to
            // own the lower third of the artwork for the dissolve to read as gradual (the Apple editorial gradient zone).
            // Scale only the internal frosted treatment: a taller dissolve zone plus the larger copy below it, while the
            // outer card keeps its original dimensions.
            float featherPad = Math.Clamp(artH * 0.24f * editorialScale, 72f * editorialScale, 200f * editorialScale);
            float textW = MathF.Max(32f, cardW - 2f * inset);
            const float radius = 14f;
            bool showCountdown = counting && !Motion.ReducedMotion;

            var copy = new List<Element>(5);
            // Eyebrow row also hosts the countdown ring (trailing) so the sweep reads as part of the copy header.
            if (eyebrow is { Length: > 0 } || showCountdown)
                copy.Add(new BoxEl
                {
                    Direction = 0, AlignItems = FlexAlign.Center, Gap = 8f, Width = textW, HitTestPassThrough = true,
                    Children =
                    [
                        eyebrow is { Length: > 0 }
                            ? new TextEl(eyebrow)
                            {
                                Size = 12.5f * editorialScale, Weight = 600, Color = ColorF.FromRgba(255, 255, 255, 200),
                                Grow = 1f, Basis = 0f, MaxLines = 1, Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis,
                            }
                            : new BoxEl { Grow = 1f },
                        showCountdown ? CountdownRing(arcCapture) : new BoxEl(),
                    ],
                });
            copy.Add(new TextEl(title)
            {
                Size = 17f * editorialScale, Weight = 700, Color = ColorF.FromRgba(255, 255, 255), Width = textW,
                MaxLines = 1, Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis,
            });
            if (peek is not null)
            {
                // The preview-track peek replaces the description: up to five compact cover+name rows, each fading in
                // (PageFade enter), the container staggering them so the list cascades rather than popping at once.
                // The card itself has a fixed portrait height, so smaller fitted cards cannot safely hold all five rows
                // plus the title and actions. Reserve the complete action/footer geometry first and fit only whole rows
                // in the remainder; this keeps both buttons inside the rounded bottom clip at every shelf width.
                float rowBudget = MathF.Max(36f, artH - featherPad - inset - 116f);
                int fittingRows = Math.Clamp((int)MathF.Floor((rowBudget + 7f) / 43f), 1, PeekRows);
                var rows = new Element[Math.Min(peek.Count, fittingRows)];
                for (int i = 0; i < rows.Length; i++) rows[i] = PeekRow(peek[i], textW);
                copy.Add(new BoxEl
                {
                    Direction = 1, Gap = 7f, Width = textW, HitTestPassThrough = true, Stagger = 45f,
                    Padding = new Edges4(0f, 6f, 0f, 0f), Key = "peek",
                    Children = rows,
                });
            }
            else if (subtitle.Length > 0)
                // The playlist description is an HTML fragment (may carry <a>/<b>) — RichText parses it (decoded, tags
                // not shown raw); links share the copy colour so they read as prose, not clickable chrome (the card owns
                // the tap). Hover relaxes the clamp to 5 lines; the band's CardResizeHeight animates the space it takes.
                copy.Add(RichText.Of(subtitle, 13f * editorialScale, ColorF.FromRgba(255, 255, 255, 224), ColorF.FromRgba(255, 255, 255, 224),
                    textW, hovered ? (artH < 440f ? 4 : 5) : 2));

            copy.Add(new BoxEl
            {
                Direction = 0, Width = textW, Gap = 8f, AlignItems = FlexAlign.Center,
                Padding = new Edges4(0f, 7f, 0f, 0f),
                Children =
                [
                    Embed.Comp(() => new NowPlayingOverlay(uri, onPlay, 48f, cover: false, 48f, persistent: true, light: true)).Skeletonized(false),
                    Embed.Comp(() => new CardLibraryAction(uri, title, kind, onDark: true)).Skeletonized(false),
                    new BoxEl { Grow = 1f },
                ],
            });

            var card = new BoxEl
            {
                Height = artH, ZStack = true, ClipToBounds = true,
                Corners = CornerRadius4.All(radius), Shadow = Elevation.Card,
                PressScale = 0.99f, OnClick = onClick,
                OnPointerMoveWithin = pointerMove,
                OnPointerExit = pointerExit,
                Children =
                [
                    // NO hover zoom on the artwork: rounded clips apply only to RoundRect-pipeline primitives — an IMAGE
                    // clips by the rectangular scissor alone (SceneRecorder tier-2 docs), so any geometry scale pushes the
                    // image's self-rounded corners out and leaves square slivers at the card corners (and a root-level
                    // scale pokes past the shelf viewport's exact-bounds clip). Hover feedback is the FAB reveal, the
                    // description expand, and the countdown peek instead.
                    new BoxEl
                    {
                        Height = artH, ZStack = true, ClipToBounds = true, Corners = CornerRadius4.All(radius),
                        HoverScale = Motion.ReducedMotion ? 1f : 1.055f,
                        HoverDurationMs = 300f, HoverEasing = Easing.FluentDecelerate,
                        Children = [ Ui.Image(cover?.Url ?? "", ImageFit.Cover, aspect, 512, radius, Tok.FillCardDefault, cover?.BlurHash) ],
                    },
                    new BoxEl
                    {
                        Height = artH, HitTestVisible = false, Corners = CornerRadius4.All(radius),
                        Gradient = new GradientSpec(GradientShape.Radial, 0f,
                        [
                            new GradientStop(0f, ColorF.FromRgba(255, 255, 255, 46)),
                            new GradientStop(0.48f, ColorF.FromRgba(255, 255, 255, 20)),
                            new GradientStop(1f, ColorF.Transparent),
                        ])
                        {
                            RadialCenter = new Point2(0.5f, 0.35f),
                            RadialRadius = new Point2(Math.Clamp(Math.Clamp(cardW * 0.46f, 140f, 190f) / MathF.Max(cardW, 1f), 0.01f, 2f),
                                                      Math.Clamp(Math.Clamp(cardW * 0.46f, 140f, 190f) / artH, 0.01f, 2f)),
                        },
                        RadialGradientCenter = spotlightCenter,
                        Opacity = 0f, HoverOpacity = 1f, HoverDurationMs = 180f, HoverEasing = Easing.FluentDecelerate,
                    },
                    new BoxEl
                    {
                        Height = artH, HitTestVisible = false, Corners = CornerRadius4.All(radius),
                        Gradient = new GradientSpec(GradientShape.Linear, 90f,
                        [
                            new GradientStop(0f, ColorF.Transparent),
                            new GradientStop(0.36f, ColorF.Transparent),
                            new GradientStop(0.66f, ColorF.FromRgba(0, 0, 0, 76)),
                            new GradientStop(1f, ColorF.FromRgba(0, 0, 0, 224)),
                        ]),
                    },
                    // Bottom-pinned frosted copy band, cross-stretched to the full card width; it auto-sizes to the copy
                    // plus the feather ramp space (featherPad), so the frost covers the text zone and fades up into the
                    // art. Height changes (line expand / peek swap) tween through real layout (CardResizeHeight).
                    //
                    // The frost is a TRUE GAUSSIAN SELF-BLUR of the artwork (the Element Blur channel, σ26), NOT an
                    // Acrylic backdrop stamp: a backdrop stamp is keyed on the card's canvas POSITION, so a shelf scroll
                    // never HITS its cache — one Gaussian per card per frame (the original perf bug). The self-blur
                    // duplicates the SAME art URL at the SAME 512 decode as the crisp cover above (⇒ shared ImageCache
                    // handle, no extra decode), blurs its OWN pixels, and rides the engine's position-INDEPENDENT blur
                    // pin cache (BlurPinKey): the pin key rebases every op to the layer origin, so a pure scroll
                    // translation yields a byte-identical key ⇒ an on-canvas scrolling card HITS its pin every frame (no
                    // re-blur). Wave A/B also caches edge-clamped regions AT REST (SelfBlurRegion) — so only a card whose
                    // band straddles the viewport edge WHILE IN MOTION re-blurs, and only over the tight band region
                    // (device rect + the kernel's ±min(ceil(3σ),32)px halo), never the whole card. FocusY=1 anchors the
                    // Cover crop to the BOTTOM slice of the art (the region behind the band); ImageTransition.None keeps
                    // the blur subtree static (one Ready-flip re-blur at decode, then nothing animates inside it — the
                    // CardResizeHeight tween lives on the band's OUTER ZStack, not under the blur). The top edge feathers
                    // over the featherPad ramp (the old FeatherTop 0.75 dissolve) via a separate EdgeFade node wrapping
                    // the blur — EdgeFade and self-Blur can't share a node (EdgeFade takes precedence), so the blur node
                    // nests INSIDE it — and the tint/luminosity feel lays over as a plain translucent fill.
                    new BoxEl
                    {
                        Height = artH, Direction = 1, Justify = FlexJustify.End, AlignItems = FlexAlign.Stretch,
                        HitTestPassThrough = true,
                        Children =
                        [
                            new BoxEl
                            {
                                ZStack = true, ClipToBounds = true, HitTestPassThrough = true,
                                Corners = CornerRadius4.All(radius),
                                Animate = MotionRecipes.CardResizeHeight,
                                Children =
                                [
                                    // Frost: blurred artwork copy + tint, feathered in from the top so it dissolves
                                    // into the crisp art above (the corner-following EdgeFade keeps the band's rounded
                                    // bottom corners clean while the top corners fade out under the ramp). The frost box
                                    // and its image carry NO explicit/aspect size, so they MEASURE to 0 — the COPY below
                                    // drives the band height (the caption band, not the whole card) — and ARRANGE to fill
                                    // the band. That fixes the earlier bug where a full-card-tall aspect image forced the
                                    // frost to cover the entire card.
                                    new BoxEl
                                    {
                                        ZStack = true, HitTestVisible = false,
                                        Corners = CornerRadius4.All(radius),
                                        EdgeFade = new EdgeFadeSpec(EdgeMask.Top, featherPad),
                                        Children =
                                        [
                                            // True σ26 self-blur of the artwork. Blur (the Element channel) wraps this
                                            // node's subtree in a PushLayer{Blur} → its OWN pixels Gaussian-blur (CSS
                                            // filter: blur(), NOT the backdrop). BlurCachePolicy.Normal is correct: the
                                            // pin caches at rest for ALL policies, and its position-INDEPENDENT key means
                                            // an on-canvas scrolling card HITS the pin in motion too (Normal, not a Hold
                                            // policy, so the frost never flashes crisp/skips when a fresh card scrolls in
                                            // — it pays one tight-region Gaussian for that frame instead). The image
                                            // carries NO aspect/size (float.NaN) so it MEASURES 0×0 and the COPY below
                                            // drives the band height, then ARRANGES to fill the band; the 512 decode
                                            // matches the crisp cover above ⇒ shared ImageCache handle (no extra decode).
                                            // FocusY=1 anchors the Cover crop to the art's BOTTOM slice; ImageTransition
                                            // .None keeps the blur subtree static (one re-blur at decode-ready, no churn).
                                            new BoxEl
                                            {
                                                ZStack = true, ClipToBounds = true, HitTestVisible = false,
                                                Corners = CornerRadius4.All(radius),
                                                Blur = 26f, BlurCachePolicy = BlurCachePolicy.Normal,
                                                Children =
                                                [
                                                    Ui.Image(cover?.Url ?? "", ImageFit.Cover, float.NaN, 512, radius, Tok.FillCardDefault, cover?.BlurHash, ImageTransition.None) with { FocusY = 1f },
                                                ],
                                            },
                                            // Tint + luminosity feel of the old recipe (Tint rgba(8,8,10)@0.24 over a
                                            // 0.28 luminosity wash) folded into one translucent fill.
                                            new BoxEl { Fill = ColorF.FromRgba(8, 8, 10) with { A = 0.42f }, Corners = CornerRadius4.All(radius) },
                                        ],
                                    },
                                    new BoxEl
                                    {
                                        Direction = 1, Gap = 3f, HitTestPassThrough = true,
                                        Padding = new Edges4(inset, featherPad, inset, inset),
                                        Children = copy.ToArray(),
                                    },
                                ],
                            },
                        ],
                    },
                    new BoxEl
                    {
                        Grow = 1f, Direction = 0, AlignItems = FlexAlign.Start,
                        Padding = new Edges4(inset, inset, inset, 0f), HitTestPassThrough = true,
                        Children = [ KindChip(kind), new BoxEl { Grow = 1f }, MoreInline(menu is not null, onDark: true, size: 36f) ],
                    },
                ],
            };
            return card.WithMenu(menu);
        }

        // The countdown ring: an 18px track circle + a round-capped sweep arc whose StrokeTrimEnd the core animates
        // 0→1 over the countdown (the ProgressRing determinate look, white-on-frost).
        static Element CountdownRing(Action<NodeHandle>? arcCapture) => new BoxEl
        {
            ZStack = true, Width = 18f, Height = 18f, Shrink = 0f, HitTestPassThrough = true,
            Children =
            [
                new BoxEl { Width = 18f, Height = 18f, Arc = new ArcSpec(ColorF.FromRgba(255, 255, 255, 64), 2f, 0f, 360f, RoundCaps: false) },
                new BoxEl
                {
                    Width = 18f, Height = 18f,
                    Arc = new ArcSpec(ColorF.FromRgba(255, 255, 255, 230), 2f, 0f, 360f, RoundCaps: true),
                    OnRealized = arcCapture,
                },
            ],
        };

        static Element PeekRow(HomePreviewTrack t, float textW)
        {
            float nameW = MathF.Max(24f, textW - 36f - 10f);
            return new BoxEl
            {
                Direction = 0, Gap = 10f, AlignItems = FlexAlign.Center, HitTestPassThrough = true,
                Animate = MotionRecipes.PageFade,
                Children =
                [
                    new BoxEl
                    {
                        Width = 36f, Height = 36f, Shrink = 0f, ClipToBounds = true, Corners = CornerRadius4.All(6f),
                        Children = [ Surfaces.Artwork(t.Cover, Seed(t.Uri), 36f, 36f, 6f, decodePx: 64) ],
                    },
                    new TextEl(t.Name)
                    {
                        Size = 12.5f, Weight = 600, Color = ColorF.FromRgba(255, 255, 255, 235),
                        Width = nameW, MaxLines = 1, Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis,
                    },
                ],
            };
        }
    }

    // ── 16:9 video card (sized to a supplied cardW from a measured shelf): wide thumbnail + title + duration. ──
    public static Element VideoCard(Image? thumb, string title, string duration, string uri,
                                    Action onClick, Action onPlay, float cardW, MenuAttach? menu = null)
    {
        float inner = MathF.Max(64f, cardW - 2f * Pad);
        float ar = inner * 9f / 16f;
        var card = new BoxEl
        {
            Direction = 1, Gap = WaveeSpace.S, Grow = 1f, ClipToBounds = true,
            Padding = new Edges4(Pad, Pad, Pad, WaveeSpace.M),
            Corners = CornerRadius4.All(WaveeRadius.Card),
            Fill = Tok.FillCardDefault, HoverFill = Tok.FillControlSecondary,
            BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault,
            Shadow = Elevation.Card,
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
                        MoreCorner(menu is not null),
                    ],
                },
                WaveeType.TrackTitle(title) with { Width = inner, Wrap = TextWrap.Wrap, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
                duration.Length == 0 ? new BoxEl()
                    : WaveeType.TrackMeta(duration) with { Width = inner, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
            ],
        };
        return card.WithMenu(menu);
    }

    // ── Wide "jump back in" tile: cover + title (fills, ellipsised) + trailing now-playing/play overlay ───
    public static Element QuickPick(Image? cover, string title, string uri, Action onClick, Action onPlay, ColorF? accent = null, Element? diagnostics = null, MenuAttach? menu = null)
    {
        var card = new BoxEl
        {
            Direction = 0, Height = QuickH, AlignItems = FlexAlign.Center, Gap = WaveeSpace.M,
            Corners = CornerRadius4.All(WaveeRadius.Card), Fill = AccentCardFill(accent), HoverFill = AccentCardHoverFill(accent),
            BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault, ClipToBounds = true, OnClick = onClick,
            Shadow = Elevation.Card,
            Children =
            [
                // Surfaces.Artwork = a neutral shimmer/placeholder tile + the real art on top (graceful when the cover
                // is missing or on an auth-gated host that fails to fetch).
                ArtworkOrLiked(cover, uri, QuickW, QuickH, 0f, diagnostics: diagnostics),
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
        return card.WithMenu(menu);
    }

    // ── List row: a HORIZONTAL media row (search / "All" lists). The SAME factory + the SAME now-playing/play affordance
    // (the shared NowPlayingOverlay) as the grid/shelf cards — only the SKIN differs (a row vs a tile). `large` is the
    // Top-Result hero variant (bigger art + title + card chrome). Optional eyebrow ("Lyrics match" / "Included in Premium"),
    // a trailing type chip, and a trailing action (save / follow). One home for a future shared context menu.
    public static Element Row(Image? cover, string title, string subtitle, string uri, bool circular,
                              Action onClick, Action onPlay,
                              string? eyebrow = null, ColorF? eyebrowColor = null, string? typeChip = null, Element? trailing = null, bool large = false,
                              string? detail = null, Action<string>? onSubtitleNav = null, string? meta = null, bool detailBelowArt = false,
                              MenuAttach? menu = null)
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
                Fill = Tok.FillCardSecondary,
                HoverFill = Tok.FillCardDefault,
                PressedFill = Tok.FillSubtleTertiary,
                BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault,
                Role = AutomationRole.Button, OnClick = onClick, OnPointerExit = static () => { },
                Children =
                [
                    new BoxEl { Direction = 0, AlignItems = FlexAlign.Center, Gap = WaveeSpace.M, Children = kids.ToArray() },
                    new BoxEl { Direction = 1, Gap = 2f, Children = belowKids.ToArray() },
                ],
            }.WithMenu(menu);
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
            Fill = Tok.FillCardSecondary,
            HoverFill = Tok.FillCardDefault,
            PressedFill = large ? Tok.FillCardDefault : Tok.FillSubtleTertiary,
            BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault,
            // The row is the interactive ancestor (OnClick + a no-op pointer-exit), so the cover's hover-revealed play FAB
            // resolves off ROW hover — identical to the card behavior.
            Role = AutomationRole.Button, OnClick = onClick, OnPointerExit = static () => { },
            Children = kids.ToArray(),
        }.WithMenu(menu);
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
        // The FAB already lives inside a clipped cover. Scaling its rounded plate past its retained paint bounds caused
        // the lower-right sector to be cut out (the visible "Pac-Man" wedge). Keep the plate geometry stable; color and
        // the card's own press response still provide hover/press feedback.
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

sealed class CardLibraryAction : Component
{
    readonly string _uri;
    readonly string _name;
    readonly HomeCardKind _kind;
    readonly bool _onDark;

    public CardLibraryAction(string uri, string name, HomeCardKind kind, bool onDark)
    { _uri = uri; _name = name; _kind = kind; _onDark = onDark; }

    public override Element Render()
    {
        var lib = UseContext(LibraryBridge.Slot);
        if (lib is null || _kind == HomeCardKind.Liked) return new BoxEl();
        bool saved = lib.IsSaved(_uri);
        bool follow = _kind is HomeCardKind.Artist or HomeCardKind.Playlist;
        string tip = follow
            ? Loc.Get(saved ? Strings.Artist.Following : Strings.Artist.Follow)
            : Loc.Get(saved ? Strings.Detail.Edit.Saved : Strings.Detail.Edit.Save);
        ColorF idle = _onDark ? ColorF.FromRgba(255, 255, 255, 225) : Tok.TextSecondary;
        return ToolTip.Wrap(new BoxEl
        {
            Width = 40f, Height = 40f, Shrink = 0f,
            AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            Corners = CornerRadius4.All(20f),
            Fill = _onDark ? ColorF.FromRgba(0, 0, 0, 120) : ColorF.Transparent,
            HoverFill = _onDark ? ColorF.FromRgba(0, 0, 0, 184) : Tok.FillSubtleSecondary,
            PressedFill = _onDark ? ColorF.FromRgba(0, 0, 0, 218) : Tok.FillSubtleTertiary,
            BorderWidth = _onDark ? 1f : 0f,
            BorderColor = _onDark ? ColorF.FromRgba(255, 255, 255, 58) : ColorF.Transparent,
            Role = AutomationRole.Button, Cursor = CursorId.Hand,
            OnClick = () => lib.ToggleSaved(_uri, _name),
            Children = [ Icon(saved ? Mdl.HeartFill : Icons.Heart, 17f, saved ? Tok.AccentTextPrimary : idle) ],
        }, tip);
    }
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
    readonly bool _persistent;
    readonly bool _light;
    public NowPlayingOverlay(string uri, Action onPlay, float fab, bool cover, float inner, Action? onNavigate = null,
                             bool centered = false, bool persistent = false, bool light = false)
    {
        _uri = uri; _onPlay = onPlay; _fab = fab; _cover = cover; _inner = inner;
        _onNavigate = onNavigate; _centered = centered; _persistent = persistent; _light = light;
    }

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

        if (_persistent)
        {
            ColorF fill = _light ? ColorF.FromRgba(255, 255, 255) : Tok.AccentDefault;
            ColorF hover = _light ? ColorF.FromRgba(235, 235, 235) : Tok.AccentSecondary;
            ColorF pressed = _light ? ColorF.FromRgba(215, 215, 215) : Tok.AccentTertiary;
            ColorF ink = _light ? ColorF.FromRgba(12, 12, 14) : Tok.TextOnAccentPrimary;
            return ToolTip.Wrap(new BoxEl
            {
                Width = _fab, Height = _fab, Shrink = 0f,
                AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                Corners = CornerRadius4.All(_fab / 2f), Fill = fill, HoverFill = hover, PressedFill = pressed,
                Shadow = Elevation.Card, Role = AutomationRole.Button, Cursor = CursorId.Hand, OnClick = Toggle,
                Children = [ Icon(playingHere ? Icons.Pause : Icons.Play, _fab * 0.38f, ink) ],
            }, Loc.Get(playingHere ? Strings.Home.Pause : Strings.Home.Play));
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
