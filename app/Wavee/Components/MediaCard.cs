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
            Surfaces.Shimmer(cover?.Url, (int)ShelfDecodePx, (int)ShelfDecodePx, inner, inner, r),
            // morphKey ⇒ this cover is a connected-animation (Hero) participant. Transparent placeholder so the gradient
            // shows through until the image arrives (rather than a flat fill that would hide it).
            Image(cover?.Url ?? "", ImageFit.Cover, 1f, ShelfDecodePx, r, placeholder: ColorF.Transparent) with { MorphId = morphKey },
            // The now-playing equalizer (bottom-left, when this card's context is playing) + the play/pause FAB
            // (bottom-right, REVEALED ON HOVER). Reactive: subscribes to the playback bridge. The container carries NO
            // OnClick, so the hit walks up to the card (its HoverScale fires + the FAB reveals off the card's hover);
            // only the FAB itself is a hit target.
            Embed.Comp(() => new NowPlayingOverlay(uri, onPlay, FabSize, cover: true, inner)));

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
                                   Action onClick, Action onPlay, bool circular = false)
    {
        float r = circular ? 9999f : WaveeRadius.Card;
        var coverStack = new BoxEl
        {
            ZStack = true, ClipToBounds = true, Corners = CornerRadius4.All(r),
            Children =
            [
                Surfaces.ArtworkFill(cover, r),
                Embed.Comp(() => new NowPlayingOverlay(uri, onPlay, FabSize, cover: true, 0f)),
            ],
        };
        return new BoxEl
        {
            Direction = 1, Gap = Pad, Grow = 1f, ClipToBounds = true,
            Padding = new Edges4(Pad, Pad, Pad, WaveeSpace.M),
            Corners = CornerRadius4.All(WaveeRadius.Card),
            Fill = Tok.FillCardSecondary, HoverFill = Tok.FillCardDefault,
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
                        Embed.Comp(() => new NowPlayingOverlay(uri, onPlay, FabSize, cover: true, 0f)),
                    ],
                },
                WaveeType.TrackTitle(title) with { Width = inner, Wrap = TextWrap.Wrap, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
                duration.Length == 0 ? new BoxEl()
                    : WaveeType.TrackMeta(duration) with { Width = inner, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
            ],
        };
    }

    // ── Wide "jump back in" tile: cover + title (fills, ellipsised) + trailing now-playing/play overlay ───
    public static Element QuickPick(Image? cover, string title, string uri, Action onClick, Action onPlay)
    {
        return new BoxEl
        {
            Direction = 0, Height = QuickH, AlignItems = FlexAlign.Center, Gap = WaveeSpace.M,
            Corners = CornerRadius4.All(WaveeRadius.Card), Fill = Tok.FillCardSecondary, HoverFill = Tok.FillCardDefault,
            BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault, ClipToBounds = true, OnClick = onClick,
            Children =
            [
                // Surfaces.Artwork = a neutral shimmer/placeholder tile + the real art on top (graceful when the cover
                // is missing or on an auth-gated host that fails to fetch).
                Surfaces.Artwork(cover, Seed(uri), QuickW, QuickH, 0f),
                // Grow + Basis=0: take the remaining width (never the title's intrinsic width) → ellipsis, no overflow.
                WaveeType.TrackTitle(title) with { Grow = 1f, Basis = 0f, Wrap = TextWrap.Wrap, MaxLines = 2, Trim = TextTrim.CharacterEllipsis },
                new BoxEl
                {
                    Direction = 0, AlignItems = FlexAlign.Center,
                    Padding = new Edges4(0f, 0f, WaveeSpace.M, 0f),
                    Children = [ Embed.Comp(() => new NowPlayingOverlay(uri, onPlay, 36f, cover: false, 36f)) ],
                },
            ],
        };
    }

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
        Children = [ Icon(glyph, size * 0.42f, Tok.TextOnAccentPrimary) ],
    };

    // ── Skeletons (matched layout for StatefulRegion's shimmer → reveal) ─────────────────────────────────
    public static Element ShelfSkeleton(float cardW, bool circular = false)
    {
        float inner = MathF.Max(48f, cardW - 2f * Pad);
        return new BoxEl
        {
            Direction = 1, Width = cardW, Gap = Pad,
            Padding = new Edges4(Pad, Pad, Pad, WaveeSpace.M),
            Corners = CornerRadius4.All(WaveeRadius.Card), Fill = Tok.FillCardSecondary,
            Children =
            [
                new BoxEl { Width = inner, Height = inner, Fill = Tok.FillCardDefault,
                            Corners = CornerRadius4.All(circular ? inner / 2f : WaveeRadius.Card) },
                new BoxEl { Width = inner * 0.85f, Height = 13f, Corners = CornerRadius4.All(4f), Fill = Tok.FillCardDefault },
                new BoxEl { Width = inner * 0.55f, Height = 11f, Corners = CornerRadius4.All(4f), Fill = Tok.FillCardDefault },
            ],
        };
    }

    public static Element QuickPickSkeleton() => new BoxEl
    {
        Direction = 0, Height = QuickH, AlignItems = FlexAlign.Center, Gap = WaveeSpace.M,
        Corners = CornerRadius4.All(WaveeRadius.Card), Fill = Tok.FillCardSecondary, ClipToBounds = true,
        Children =
        [
            new BoxEl { Width = QuickW, Height = QuickH, Fill = Tok.FillCardDefault },
            new BoxEl { Grow = 1f, Basis = 0f, Height = 13f, Margin = new Edges4(0f, 0f, WaveeSpace.M, 0f),
                        Corners = CornerRadius4.All(4f), Fill = Tok.FillCardDefault },
        ],
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
    readonly float _fab;
    readonly bool _cover;
    readonly float _inner;
    public NowPlayingOverlay(string uri, Action onPlay, float fab, bool cover, float inner)
    { _uri = uri; _onPlay = onPlay; _fab = fab; _cover = cover; _inner = inner; }

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
            Children = [ MediaCard.PlayFab(Toggle, playingHere ? Icons.Pause : Icons.Play, _fab) ],
        };
        Element EqPill() => new BoxEl
        {
            Padding = new Edges4(5f, 3f, 5f, 3f), Corners = CornerRadius4.All(4f), Fill = ColorF.FromRgba(0, 0, 0, 150),
            Children = [ WaveeEqualizer.Of(playing, Tok.AccentTextPrimary, 14f) ],
        };

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
        if (string.Equals(uri, track.Album.Uri, StringComparison.OrdinalIgnoreCase)) return true;
        foreach (var a in track.Artists)
            if (string.Equals(uri, a.Uri, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }
}
