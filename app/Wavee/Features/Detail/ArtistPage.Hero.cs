using System;
using System.Collections.Generic;
using System.Linq;
using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using Wavee.Core;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

// The full-bleed artist hero: the photo (parallax + overscroll-stretch, behind a bottom edge-fade), the headline copy
// (name / bio / meta / actions), the eyebrow pills, the action affordances, and the optional wide "pinned promo" card.
sealed partial class ArtistPage : Component
{
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
        const float h = 420f;
        int albumCount = a.TopAlbums?.Count(al => al.Kind is AlbumKind.Album or AlbumKind.Compilation) ?? 0;
        int singleCount = a.TopAlbums?.Count(al => al.Kind is AlbumKind.Single or AlbumKind.EP) ?? 0;
        var bg = a.HeaderImage ?? a.Image;

        // The width-dependent hero body (photo + scrim + bottom-anchored copy). Built once the available width is known.
        Element Inner(float w)
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
                    new TextEl(Strings.Artist.HeroMeta(Count(a.MonthlyListeners), Count(a.Followers), albumCount.ToString(),
                        singleCount.ToString()))
                    {
                        Size = 14f, Weight = 600, Color = WhiteText with { A = 0.85f }, MaxLines = 1,
                        Trim = TextTrim.CharacterEllipsis
                    },
                    new BoxEl
                    {
                        Direction = 0, Gap = WaveeSpace.M, AlignItems = FlexAlign.Center,
                        Padding = new Edges4(0f, WaveeSpace.S, 0f, 0f),
                        Children =
                        [
                            PlayPill(play), Fab(Icons.Shuffle, shuffle),
                            Embed.Comp(() => new FollowButton(uri)) with { SkeletonProxy = FollowButton.SkeletonShape },
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
                Width = w, Height = h, Direction = 0, AlignItems = FlexAlign.End, Gap = WaveeSpace.XL,
                Padding = new Edges4(WaveeSpace.XL, WaveeSpace.XL, WaveeSpace.XL, WaveeSpace.XL),
                OpacityGroup = true,
                ScrollBinds =
                [
                    new() { From = ScrollChannel.Offset, To = BindSink.Opacity, Range = ScrollRange.Px(h * 0.16f, h * 0.66f), OutStart = 1f, OutEnd = 0f, Ease = Easing.Linear },
                ],
                Children = wide ? [copy, PinnedCard(a.Pinned!, go)] : [copy],
            };

            // The hero photo + its text scrim. The image fills the wide box COVER-fit, centred. A bottom EDGE FADE
            // alpha-masks the photo to transparent over the last ~200px so it composites into the page-over-Mica behind it
            // (and, during the collapse, keeps the live shrinking edge soft rather than a hard cut).
            Element heroArt = bg?.Url is { Length: > 0 } hu
                ? Ui.Image(hu, w, h, corners: 0f, placeholder: ColorF.FromRgba(0x1C, 0x1C, 0x1C),
                        blurHash: bg.BlurHash) with
                    {
                        Fit = ImageFit.Cover
                    }
                : new BoxEl { Width = w, Height = h, Fill = Tok.FillCardDefault };
            var media = new BoxEl
            {
                Width = w, Height = h, ZStack = true, ClipToBounds = true,
                ScrollBinds = [new() { StretchFromTop = true }], // iOS/Spotify stretchy hero (generic scroll bind)
                TransformOriginX = 0.5f, TransformOriginY = 0f,
                // Deeper bottom fade (was 200) so the soft zone still covers the collapse clip line even with the photo's
                // parallax lag shifting it up — keeps the live shrinking edge soft instead of a hard cut.
                EdgeFade = new EdgeFadeSpec(EdgeMask.Bottom, 260f),
                Children =
                [
                    heroArt,
                    new BoxEl
                    {
                        Width = w, Height = h,
                        Gradient = LinearGradient(180f,
                            new GradientStop(0f, Scrim(0f)),
                            new GradientStop(0.5f, Scrim(0.22f)),
                            new GradientStop(1f, Scrim(0.78f))),
                    },
                ],
            };
            // Parallax: the photo rises at ~65% of the collapse rate, so it lags behind the copy (which rides the live
            // bottom edge at full rate). The differential between the two layers is the depth cue. Linear because it's
            // scroll-DRIVEN (the photo's drift must mirror the scroll, not editorialize it). The collapse owner's
            // ChildShiftY moves this layer up by `offset`; this +0.35·offset counter-translate is what makes it lag.
            var heroParallax = new BoxEl
            {
                Width = w, Height = h, ZStack = true,
                ScrollBinds =
                [
                    new() { From = ScrollChannel.Offset, To = BindSink.TransY, Range = ScrollRange.Px(0f, h), OutStart = 0f, OutEnd = h * 0.18f, Ease = Easing.Linear },
                ],
                Children = [media],
            };

            return new BoxEl
            {
                Width = w, Height = h, ZStack = true, ClipToBounds = true,
                Children = [heroParallax, overlay],
            };
        }

        // The pinned collapse owner MUST be a direct child of the tall scroll content: the sticky pin's containing-block
        // clamp is the PARENT height, and a tight Responsive wrapper (== hero height) clamps the pin to 0 — the bug that
        // left the empty band (the hero scrolled off while ChildShiftY double-counted). Owning the pin here, with the
        // width-built Inner wrapped inside, gives it a tall containing block so the pin holds the hero at the top while
        // PresentedHTrailing rides its height to zero.
        return new BoxEl
        {
            Direction = 1, Height = h, ClipToBounds = true,
            ScrollBinds =
            [
                new() { PinTop = 0f },
                new() { From = ScrollChannel.Offset, To = BindSink.PresentedHTrailing, Range = ScrollRange.Px(0f, h), OutStart = h, OutEnd = 0f },
            ],
            Children = [ Responsive.Of(Inner, fallback: 900f) ],
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
    Element PlayPill(Action onPlay) => new BoxEl
    {
        Direction = 0, Gap = WaveeSpace.S, AlignItems = FlexAlign.Center,
        Corners = CornerRadius4.All(24f), Padding = new Edges4(22f, 12f, 22f, 12f),
        Fill = _accent, HoverScale = 1.04f, PressScale = 0.97f, Shadow = Elevation.Card, OnClick = onPlay,
        Children =
        [
            Icon(Icons.Play, 16f, WaveePalette.OnAccent(_accent)),
            new TextEl(Loc.Get(Strings.Artist.Play)) { Size = 15f, Weight = 700, Color = WaveePalette.OnAccent(_accent) }
        ],
    };

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
