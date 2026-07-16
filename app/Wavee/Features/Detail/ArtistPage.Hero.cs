using System;
using System.Collections.Generic;
using System.Linq;
using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Scene;
using FluentGpu.Signals;
using Wavee.Core;
using Wavee.Features.Detail;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

// The full-bleed artist hero: the photo (parallax + overscroll-stretch, behind a bottom edge-fade), the headline copy
// (name / bio / meta / actions), the eyebrow pills, the action affordances, and the optional wide "pinned promo" card.
sealed partial class ArtistPage : Component
{
    readonly Signal<float> _heroWidth = new(ArtistHeroLayout.WideWidth);

    // ── hero banner ──────────────────────────────────────────────────────────────────────────────────────
    // Collapsing hero: pins at the viewport top and shrinks IN PLACE as you scroll (the WinUI/Spotify large-title
    // collapse), its presented height riding down to zero while the bottom-anchored copy dissolves and the track list
    // rises to meet the live edge — then hands off to the compact ArtistShyPill. The collapse is the generic scroll
    // engine's trailing-anchored presented-height sink (BindSink.PresentedHTrailing): it clips the painted height AND
    // shifts every child up by the same delta, so the copy + the media edge-fade stay attached to the shrinking edge
    // with no relayout. See ArtistPage.cs (the pill arms via the sentinel) and the VerticalSlice 23u2 gate.
    Element Banner(Artist a, string uri, Action play, Action shuffle, Action radio,
        Action<string, string?> go)
    {
        float heroW = _heroWidth.Value;
        float h = ArtistHeroLayout.HeroHeightFor(heroW);
        int albumCount = a.TopAlbums?.Count(al => al.Kind is AlbumKind.Album or AlbumKind.Compilation) ?? 0;
        int singleCount = a.TopAlbums?.Count(al => al.Kind is AlbumKind.Single or AlbumKind.EP) ?? 0;
        var bg = a.HeaderImage ?? a.Image;

        // The width-dependent hero body (photo + scrim + bottom-anchored copy). Built once the available width is known.
        Element Inner(float w, float height)
        {
            bool wide = w >= 960f && a.Pinned is not null;

            var copy = new BoxEl
            {
                Direction = 1, Justify = FlexJustify.End, Gap = WaveeSpace.S, Grow = 1f, Basis = 0f,
                Children =
                [
                    EyebrowPills(a),
                    WaveeType.PageHero(a.Name) with
                    {
                        Size = HeroSize(a.Name), Weight = 900, Color = WhiteText,
                        Wrap = TextWrap.Wrap, MaxLines = 2, Trim = TextTrim.CharacterEllipsis,
                    },
                    HeroBioLine(a.Bio, w),
                    HeroMetaLine(a, albumCount, singleCount),
                    new BoxEl
                    {
                        Direction = 0, Gap = WaveeSpace.M, AlignItems = FlexAlign.Center,
                        Padding = new Edges4(0f, WaveeSpace.S, 0f, 0f),
                        Children =
                        [
                            PlayPill(play), Fab(Icons.Shuffle, shuffle),
                            Embed.Comp(() => new FollowButton(uri, a.Name, WhiteText)) with { SkeletonProxy = FollowButton.SkeletonShape },
                            ArtistRadioPill(radio)
                        ],
                    },
                ],
            };

            // Dissolve the copy gradually from early in the collapse — an even, scroll-linked fade (Linear, so it mirrors
            // the scroll instead of holding-then-snapping), finishing before the copy would clip under the top edge, so it
            // reads as a true dissolve rather than a discrete hide. The Play/Follow affordances re-appear in the compact
            // pill as the hero finishes collapsing, so they can leave with the rest of the copy here.
            var overlay = new BoxEl
            {
                Width = w, Height = height, Direction = 0, AlignItems = FlexAlign.End, Gap = WaveeSpace.XL,
                Padding = new Edges4(WaveeSpace.XL, WaveeSpace.XL, WaveeSpace.XL, WaveeSpace.XL),
                OpacityGroup = true,
                ScrollBinds =
                [
                    new() { From = ScrollChannel.Offset, To = BindSink.Opacity, Range = ScrollRange.Px(height * 0.16f, height * 0.66f), OutStart = 1f, OutEnd = 0f, Ease = Easing.Linear },
                ],
                Children = wide ? [copy, PinnedCard(a.Pinned!, go)] : [copy],
            };

            // The hero photo + its text scrim. The image fills the wide box COVER-fit, centred. A bottom EDGE FADE
            // alpha-masks the photo to transparent over the last ~200px so it composites into the page-over-Mica behind it
            // (and, during the collapse, keeps the live shrinking edge soft rather than a hard cut).
            //
            // While the photo downloads the slot shows a DARK ACCENT WASH (the artist colour, dimmed) rather than a neutral
            // grey, so the hero reads as the artist's own colour during the load — matching WaveeMusic's dark-base-with-accent
            // hero. The lifted _accent guarantees a visible hue even when the cover's extracted dark tone is near-black. On
            // decode the photo reveals via HeroArt's WaveeMusic-matched pop-in (320ms FluentDecelerate fade + 1.0→1.05 zoom).
            // The media also dissolves on the same scroll interval as the copy; once the compact pill owns the header,
            // the large photo must be visually gone, not merely behind the content.
            ColorF HeroWash() => Tok.Theme == ThemeKind.Light
                ? ColorF.Lerp(WaveeColors.FileArea, _accent, 0.12f)
                : ColorF.Lerp(ColorF.FromRgba(0x14, 0x14, 0x16), _accent, 0.30f);
            Element heroArt = bg?.Url is { Length: > 0 } hu
                ? Embed.Comp(() => new HeroArt(hu, _heroWidth, bg.BlurHash, HeroWash)) with { Key = "heroart:" + hu }
                : new BoxEl { Width = w, Height = height, Fill = HeroWash() };
            var media = new BoxEl
            {
                Width = w, Height = height, ZStack = true, ClipToBounds = true,
                ScrollBinds =
                [
                    new() { StretchFromTop = true }, // iOS/Spotify stretchy hero (generic scroll bind)
                    new() { From = ScrollChannel.Offset, To = BindSink.Opacity, Range = ScrollRange.Px(height * 0.16f, height * 0.66f), OutStart = 1f, OutEnd = 0f, Ease = Easing.Linear },
                ],
                TransformOriginX = 0.5f, TransformOriginY = 0f,
                // Deeper bottom fade (was 200) so the soft zone still covers the collapse clip line even with the photo's
                // parallax lag shifting it up — keeps the live shrinking edge soft instead of a hard cut.
                EdgeFade = new EdgeFadeSpec(EdgeMask.Bottom, ArtistHeroLayout.PhotoFadeBand),
                Children = [heroArt],
            };
            // Parallax: the photo rises at ~65% of the collapse rate, so it lags behind the copy (which rides the live
            // bottom edge at full rate). The differential between the two layers is the depth cue. Linear because it's
            // scroll-DRIVEN (the photo's drift must mirror the scroll, not editorialize it). The collapse owner's
            // ChildShiftY moves this layer up by `offset`; this +0.35·offset counter-translate is what makes it lag.
            var heroParallax = new BoxEl
            {
                Width = w, Height = height, ZStack = true,
                ScrollBinds =
                [
                    new() { From = ScrollChannel.Offset, To = BindSink.TransY, Range = ScrollRange.Px(0f, height), OutStart = 0f, OutEnd = height * 0.18f, Ease = Easing.Linear },
                ],
                Children = [media],
            };

            // Contrast belongs ABOVE the edge-faded media, not inside it. The old scrim was a child of `media`, so the
            // same Bottom EdgeFade that dissolved the photo also erased its contrast exactly behind the bottom-anchored
            // copy. A binary black/white palette choice cannot solve mixed collages; this localized veil guarantees the
            // white hero type/buttons remain readable over both pale faces and dark hair, then fades before the page seam.
            //
            // AT MOST 4 STOPS: GradientSpec.MaxStops is 4 and the recorder silently DROPS extras — a 6-stop version of
            // this veil lost its two release stops, so the last kept stop (peak alpha) held solid to the hero's bottom
            // edge: a hard-cut dark plate instead of a fade. The shader clamps to the first stop's colour before its
            // offset, so the transparent top zone needs no explicit 0-offset stop.
            //
            // GradientDown, NOT LinearGradient(180f): 180° is the HORIZONTAL axis (right→left) — authored that way
            // this veil painted a sideways dark band down the hero's full height, which hard-clipped at the
            // presented-height edge: THE seam line at the hero↔content boundary.
            var copyContrast = new BoxEl
            {
                Width = w, Height = height, HitTestPassThrough = true,
                Gradient = GradientDown(
                    new GradientStop(0.34f, Scrim(0f)),
                    new GradientStop(0.68f, Scrim(0.55f)),
                    new GradientStop(0.90f, Scrim(0.22f)),
                    new GradientStop(1f, Scrim(0f))),
                ScrollBinds =
                [
                    new() { From = ScrollChannel.Offset, To = BindSink.Opacity, Range = ScrollRange.Px(height * 0.16f, height * 0.66f), OutStart = 1f, OutEnd = 0f, Ease = Easing.Linear },
                ],
            };

            return new BoxEl
            {
                Width = w, Height = height, ZStack = true, ClipToBounds = true,
                Children = [heroParallax, copyContrast, overlay],
            };
        }

        void MeasureHero(RectF r)
        {
            if (r.W <= 0f) return;
            if (MathF.Abs(r.W - _heroWidth.Peek()) > 0.5f) _heroWidth.Value = r.W;
        }

        // The pinned collapse owner MUST be a direct child of the tall scroll content: the sticky pin's containing-block
        // clamp is the PARENT height, and a tight Responsive wrapper (== hero height) clamps the pin to 0 — the bug that
        // left the empty band (the hero scrolled off while ChildShiftY double-counted). Owning the pin here and measuring
        // this same node gives it a tall containing block so the pin holds the hero at the top while
        // PresentedHTrailing rides its height to zero.
        return new BoxEl
        {
            Direction = 1, Height = h, ClipToBounds = true,
            OnBoundsChanged = MeasureHero,
            ScrollBinds =
            [
                new() { PinTop = 0f },
                new() { From = ScrollChannel.Offset, To = BindSink.PresentedHTrailing, Range = ScrollRange.Px(0f, h), OutStart = h, OutEnd = 0f },
            ],
            Children = [ Inner(heroW, h) ],
        };
    }

    static float HeroSize(string name) =>
        name.Length <= 10 ? 72f : name.Length <= 18 ? 56f : name.Length <= 28 ? 44f : 34f;

    static Element HeroBioLine(string? bio, float w)
    {
        string? line = FirstSentence(bio);
        if (line is null) return new BoxEl();
        return new TextEl(line)
        {
            Size = 14f, Color = WhiteText with { A = 0.8f }, Width = MathF.Min(w - 40f, 860f), Wrap = TextWrap.Wrap,
            MaxLines = 2, Trim = TextTrim.CharacterEllipsis
        };
    }

    // The hero meta strip: "705,764 monthly listeners · 566,287 followers · 10 singles". One shaped flow (SpanTextEl) so it
    // ellipsizes as a single line, with the NUMBERS in full-strength white and the LABELS + separators dimmed (primary vs
    // secondary foreground). A zero count drops its WHOLE segment — never "0 albums" — so a thin catalogue just shows fewer
    // facts (and an artist with nothing to report renders nothing).
    static Element HeroMetaLine(Artist a, int albums, int singles)
    {
        ColorF num = WhiteText;                       // primary foreground (the counts)
        ColorF dim = WhiteText with { A = 0.6f };     // secondary foreground (labels + " · " separators)
        var spans = new List<TextSpan>(8);
        void Seg(long value, string text, string label)
        {
            if (value <= 0) return;                   // drop a zero/absent segment entirely
            if (spans.Count > 0) spans.Add(new TextSpan(" · ", Color: dim));
            spans.Add(new TextSpan(text, Color: num));
            spans.Add(new TextSpan(" " + label, Color: dim));
        }
        Seg(a.MonthlyListeners, Count(a.MonthlyListeners), Loc.Get(Strings.Artist.MetaMonthly));
        Seg(a.Followers, Count(a.Followers), Loc.Get(Strings.Artist.MetaFollowers));
        Seg(albums, albums.ToString(), Loc.Get(Strings.Artist.MetaAlbums));
        Seg(singles, singles.ToString(), Loc.Get(Strings.Artist.MetaSingles));
        if (spans.Count == 0) return new BoxEl();
        return new SpanTextEl(spans.ToArray())
        {
            Size = 14f, Weight = 600, Color = num, Wrap = TextWrap.NoWrap, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
            MinWidth = 0f,   // the NoWrap run must ellipsize at the copy column's width, not inflate it (cf. TrackRow)
        };
    }

    static string? FirstSentence(string? bio)
    {
        if (string.IsNullOrWhiteSpace(bio)) return null;
        string plain = StripHtml(bio!);
        if (plain.Length == 0) return null;
        int dot = plain.IndexOf(". ", StringComparison.Ordinal);
        string s = dot > 40 ? plain[..(dot + 1)] : plain;
        return s.Length > 220 ? s[..220] + "…" : s;
    }

    static string StripHtml(string s)
    {
        var sb = new System.Text.StringBuilder(s.Length);
        bool tag = false;
        foreach (char c in s)
        {
            if (c == '<') tag = true;
            else if (c == '>') tag = false;
            else if (!tag && c is not ('\r' or '\n')) sb.Append(c);
        }

        return sb.ToString().Trim();
    }

    Element EyebrowPills(Artist a)
    {
        var pills = new List<Element>(2);
        if (a.Verified) pills.Add(VerifiedPill());
        if (a.WorldRank > 0) pills.Add(GlassPill(Strings.Artist.WorldRank(a.WorldRank.ToString())));
        return pills.Count == 0
            ? new BoxEl()
            : new BoxEl
            {
                Direction = 0, Gap = WaveeSpace.S, AlignItems = FlexAlign.Center, Children = pills.ToArray()
            };
    }

    Element VerifiedPill() => new BoxEl
    {
        Direction = 0, AlignItems = FlexAlign.Center, Gap = 6f,
        Padding = new Edges4(8f, 4f, 12f, 4f), Corners = CornerRadius4.All(13f), Fill = _accent,
        Children =
        [
            Icon(Mdl.Check, 12f, WaveePalette.OnAccent(_accent)),
            new TextEl(Loc.Get(Strings.Artist.Verified))
                { Size = 11f, Weight = 700, Color = WaveePalette.OnAccent(_accent), CharSpacing = 20f }
        ],
    };

    static Element GlassPill(string text) => new BoxEl
    {
        Padding = new Edges4(12f, 5f, 12f, 5f), Corners = CornerRadius4.All(13f), Fill = WhiteText with { A = 0.16f },
        Children =
        [
            new TextEl(text) { Size = 11f, Weight = 700, Color = WhiteText with { A = 0.95f }, CharSpacing = 20f }
        ],
    };

    // ── hero pinned promo card ───────────────────────────────────────────────────────────────────────────
    static Element PinnedCard(PinnedItem p, Action<string, string?> go) => new BoxEl
    {
        Width = 320f, Shrink = 0f, Direction = 0, Gap = WaveeSpace.M, AlignItems = FlexAlign.Center,
        Padding = new Edges4(WaveeSpace.M, WaveeSpace.M, WaveeSpace.M, WaveeSpace.M),
        Corners = CornerRadius4.All(WaveeRadius.Card), Fill = Scrim(0.55f), ClipToBounds = true,
        HoverFill = Scrim(0.65f), OnClick = () => go("album:" + p.Uri, p.Title),
        Children =
        [
            new BoxEl
            {
                Width = 64f, Height = 64f, Shrink = 0f, Corners = CornerRadius4.All(WaveeRadius.Control),
                ClipToBounds = true,
                Children =
                [
                    Surfaces.Artwork(p.Cover, p.Uri.GetHashCode() & 0x7fffffff, 64f, 64f, WaveeRadius.Control,
                        decodePx: 256)
                ]
            },
            new BoxEl
            {
                Direction = 1, Grow = 1f, Basis = 0f, Gap = 2f,
                Children =
                [
                    new BoxEl
                    {
                        Direction = 0, Gap = 4f, AlignItems = FlexAlign.Center,
                        Children =
                        [
                            Icon(Mdl.Pin, 11f, WhiteText with { A = 0.7f }),
                            new TextEl(p.Eyebrow)
                                { Size = 10f, Weight = 700, Color = WhiteText with { A = 0.7f }, CharSpacing = 20f }
                        ]
                    },
                    new TextEl(p.Title)
                    {
                        Size = 15f, Weight = 700, Color = WhiteText, MaxLines = 1, Trim = TextTrim.CharacterEllipsis
                    },
                    p.Comment.Length == 0
                        ? new BoxEl()
                        : new TextEl(p.Comment)
                        {
                            Size = 12f, Color = WhiteText with { A = 0.75f }, MaxLines = 1,
                            Trim = TextTrim.CharacterEllipsis
                        },
                ]
            },
        ],
    };

    // ── action affordances ───────────────────────────────────────────────────────────────────────────────
    Element PlayPill(Action onPlay)
        => HeroCta.Pill(Icons.Play, Loc.Get(Strings.Artist.Play), _accent, WaveePalette.OnAccent(_accent), onPlay);

    static Element Fab(string glyph, Action onClick) => new BoxEl
    {
        Width = 44f, Height = 44f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
        Corners = CornerRadius4.All(22f), HoverFill = Tok.FillSubtleSecondary, PressedFill = Tok.FillSubtleTertiary,
        HoverScale = 1.06f, PressScale = 0.94f, OnClick = onClick,
        Children = [Icon(glyph, 18f, WhiteText)],
    };

    static Element ArtistRadioPill(Action onClick) => new BoxEl
    {
        Direction = 0, Gap = WaveeSpace.S, AlignItems = FlexAlign.Center,
        Corners = CornerRadius4.All(22f), Padding = new Edges4(16f, 10f, 16f, 10f),
        BorderWidth = 1f, BorderColor = WhiteText with { A = 0.35f }, HoverFill = WhiteText with { A = 0.12f },
        HoverScale = 1.03f, PressScale = 0.97f, OnClick = onClick,
        Children =
        [
            Icon(Mdl.RadioTower, 16f, WhiteText),
            new TextEl(Loc.Get(Strings.Artist.ArtistRadio)) { Size = 14f, Weight = 600, Color = WhiteText }
        ],
    };
}

// The artist hero photo with a WaveeMusic-matched load-in. The engine's built-in image reveal is an opacity cross-fade —
// here 320ms FluentDecelerate, which is the EXACT curve (cubic-bezier 0.1,0.9,0.2,1.0) and ~timing of WaveeMusic's
// HeroHeader pop-in. This component adds the second half WaveeMusic does and a bare Ui.Image can't: a 1.0→1.05 scale-settle
// that fires the instant the photo decodes. The photo rests at 1.05 (declared static, so any re-render after the one-shot
// settles back onto it — a finite anim track frees on completion); the zoom keyframe seeds exactly ONCE on the
// loading→ready edge (latched, so a re-render mid-flight can't restart it). Like CoverShimmer it reads the load-state from
// the displayed image's EXACT decode handle (same src + (w,h) per ImageDecodeTarget), so it forks no second decode.
sealed class HeroArt : Component
{
    const float RevealFadeMs = 320f;    // opacity 0→1 — matches WaveeMusic's keyframe reaching full at 0.4×800ms
    const float RevealScaleMs = 800f;   // scale 1.0→1.05 over the full WaveeMusic pop-in duration
    const float RestScale = 1.05f;      // WaveeMusic FinalScale — the photo settles (and rests) at a 5% crop-zoom
    const float FrameScale = 1.16f;     // static overscan; makes portrait panning visible even when source/slot AR match
    const float FrameLiftFrac = 0.07f;  // move decoded pixels up inside the clipped hero slot
    static readonly Keyframe[] ZoomIn = [new(0f, 1f), new(1f, RestScale, Easing.FluentDecelerate)];
    static readonly Keyframe[] Rest = [new(0f, RestScale), new(1f, RestScale)];

    readonly string _url;
    readonly IReadSignal<float> _width;
    readonly string? _blurHash;
    readonly Func<ColorF> _wash;
    public HeroArt(string url, IReadSignal<float> width, string? blurHash, Func<ColorF> wash)
    { _url = url; _width = width; _blurHash = blurHash; _wash = wash; }

    public override Element Render()
    {
        float w = MathF.Max(1f, _width.Value);
        float h = ArtistHeroLayout.HeroHeightFor(w);

        // Fire the zoom only once the photo is actually resident. Latch on the first Ready/Failed so the tile then stops
        // calling UseImage (unsubscribes from the image epoch → no steady-state re-render) and a later re-render can't
        // re-trigger the settle. UseImage consumes no hook cell, so the conditional call is safe (mirrors CoverShimmer).
        var settled = UseRef(false);
        var zoom = UseRef(false);
        if (!settled.Value)
        {
            var state = UseImage(_url, (int)w, (int)h).State;   // SAME (src,w,h) as the displayed image → shared handle
            if (state == ImageState.Ready) { settled.Value = true; zoom.Value = true; }
            else if (state == ImageState.Failed) settled.Value = true;   // no photo → no zoom, just stop probing
        }
        // One-shot 1.0→1.05 on the ready edge; otherwise hold the resting 1.05. Keyed by the latch so the layout-effect
        // re-seeds exactly once (the finite track frees on completion and settles back onto the element's RestScale).
        Keyframe[] keys = zoom.Value ? ZoomIn : Rest;
        float dur = zoom.Value ? RevealScaleMs : 1f;
        UseKeyframes(AnimChannel.ScaleX, keys, dur, false, DepKey.From(zoom.Value));
        UseKeyframes(AnimChannel.ScaleY, keys, dur, false, DepKey.From(zoom.Value));
        // The reveal zoom rides this host. A nested static frame adds real overscan + lift; FocusY alone only changes
        // visible pixels when Cover actually crops the decoded source, so same-ratio Spotify hero art needs this pan.
        //
        // The zoom rides this BoxEl host (transform props live on BoxEl, not ImageEl). The host + photo are FLUID (a
        // ZStack fills a NaN-sized child to its box — FlexLayout.ArrangeZStack), so LAYOUT — not a frozen ctor width —
        // sizes them to the CURRENT responsive hero width every frame; the photo therefore always spans the full hero
        // (a fixed-width child here left an empty band when the hero grew past the mount width). The measured w/h are used ONLY as
        // a frozen DECODE hint — decodePx + aspect resolve to the same (⌊w⌋,⌊h⌋) target the ready-probe shares — never as
        // a layout extent. Origin defaults to centre; the parent `media` box clips the 5% scale overscan, cover-fit crops.
        return new BoxEl
        {
            ZStack = true, ScaleX = RestScale, ScaleY = RestScale,
            Children =
            [
                new BoxEl
                {
                    ZStack = true,
                    ScaleX = FrameScale,
                    ScaleY = FrameScale,
                    OffsetY = -h * FrameLiftFrac,
                    Children =
                    [
                        Ui.Image(_url, ImageFit.Cover, aspect: w / h, decodePx: w, corners: 0f,
                                placeholder: _wash(), blurHash: _blurHash, transition: ImageTransition.Fade(RevealFadeMs))
                            with { FocusY = 0.35f },
                    ],
                },
            ],
        };
    }
}
