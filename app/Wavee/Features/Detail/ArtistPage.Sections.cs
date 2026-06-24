using System;
using System.Collections.Generic;
using System.Globalization;
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

// Magazine sections below the full-bleed artist hero.
sealed partial class ArtistPage : Component
{
    // ── hero banner ──────────────────────────────────────────────────────────────────────────────────────
    static Element Banner(Artist a, float w, string uri, Action play, Action shuffle, Action radio, Action<string, string?> go)
    {
        const float h = 420f;
        int albumCount = a.TopAlbums?.Count(al => al.Kind is AlbumKind.Album or AlbumKind.Compilation) ?? 0;
        int singleCount = a.TopAlbums?.Count(al => al.Kind is AlbumKind.Single or AlbumKind.EP) ?? 0;
        var bg = a.HeaderImage ?? a.Image;
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
                new TextEl(Strings.Artist.HeroMeta(Count(a.MonthlyListeners), Count(a.Followers), albumCount.ToString(), singleCount.ToString()))
                { Size = 14f, Weight = 600, Color = WhiteText with { A = 0.85f }, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
                new BoxEl
                {
                    Direction = 0, Gap = WaveeSpace.M, AlignItems = FlexAlign.Center,
                    Padding = new Edges4(0f, WaveeSpace.S, 0f, 0f),
                    Children = [ PlayPill(play), Fab(Icons.Shuffle, shuffle), Embed.Comp(() => new FollowButton(uri)), ArtistRadioPill(radio) ],
                },
            ],
        };

        var overlay = new BoxEl
        {
            Width = w, Height = h, Direction = 0, AlignItems = FlexAlign.End, Gap = WaveeSpace.XL,
            Padding = new Edges4(WaveeSpace.XL, WaveeSpace.XL, WaveeSpace.XL, WaveeSpace.XL),
            Children = wide ? [ copy, PinnedCard(a.Pinned!, go) ] : [ copy ],
        };

        // The hero photo + its text scrim. The image fills the wide box COVER-fit, centred (Surfaces.Artwork's explicit
        // w×h path = ImageFit.Cover at 0.5/0.5 — NOT the square-decode path). A bottom EDGE FADE alpha-masks the photo to
        // transparent over the last ~200px so it composites into the page-over-Mica behind it (a fixed gradient colour
        // can't, since Mica is dynamic) — the WinUI "image composition" blend, no discrete cutoff.
        // Explicit box sizing is intentional: the hero viewport is exactly w×420 and ImageFit.Cover performs the
        // centered 0.5/0.5 source crop inside it. The Windows decoder preserves the source aspect ratio; the renderer
        // owns the crop, so the image cannot stretch or acquire a layout-derived natural height.
        Element heroArt = bg?.Url is { Length: > 0 } hu
            ? Ui.Image(hu, w, h, corners: 0f, placeholder: ColorF.FromRgba(0x1C, 0x1C, 0x1C),
                       blurHash: bg.BlurHash) with { Fit = ImageFit.Cover }
            : new BoxEl { Width = w, Height = h, Fill = Tok.FillCardDefault };
        var media = new BoxEl
        {
            // Only the media stretches during top overscroll. Text/actions stay at their authored size while the image
            // expands from its center to cover the rubber-band reveal.
            Width = w, Height = h, ZStack = true, ClipToBounds = true,
            ScrollBinds = [ new() { StretchFromTop = true } ],   // iOS/Spotify stretchy hero (generic scroll bind)
            TransformOriginX = 0.5f, TransformOriginY = 0f,
            EdgeFade = new EdgeFadeSpec(EdgeMask.Bottom, 200f),
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

        // Parallax: the hero photo lags the page scroll (drifts up at ~half speed) for depth, while the overlay text +
        // actions scroll at full speed. The wrapper carries ONLY a transform-Y bind (no paint-above), so the hero stays
        // BEHIND the page content and its own bottom edge-fade — it dissolves into the content scrolling over it and is
        // never pulled to the foreground. (Overscroll-stretch stays on `media`; the two transforms live on separate nodes
        // so they compose instead of clobbering.)
        var heroParallax = new BoxEl
        {
            Width = w, Height = h, ZStack = true,
            ScrollBinds = [ new() { From = ScrollChannel.Offset, To = BindSink.TransY,
                                    Range = ScrollRange.Px(0f, h), OutStart = 0f, OutEnd = h * 0.5f } ],
            Children = [ media ],
        };
        return new BoxEl
        {
            Width = w, Height = h, ZStack = true,
            Children = [ heroParallax, overlay ],
        };
    }

    static float HeroSize(string name) => name.Length <= 10 ? 72f : name.Length <= 18 ? 56f : name.Length <= 28 ? 44f : 34f;

    static Element HeroBioLine(string? bio, float w)
    {
        string? line = FirstSentence(bio);
        if (line is null) return new BoxEl();
        return new TextEl(line) { Size = 14f, Color = WhiteText with { A = 0.8f }, Width = MathF.Min(w - 40f, 860f), Wrap = TextWrap.Wrap, MaxLines = 2, Trim = TextTrim.CharacterEllipsis };
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

    static Element EyebrowPills(Artist a)
    {
        var pills = new List<Element>(2);
        if (a.Verified) pills.Add(VerifiedPill());
        if (a.WorldRank > 0) pills.Add(GlassPill(Strings.Artist.WorldRank(a.WorldRank.ToString())));
        return pills.Count == 0 ? new BoxEl() : new BoxEl { Direction = 0, Gap = WaveeSpace.S, AlignItems = FlexAlign.Center, Children = pills.ToArray() };
    }

    static Element VerifiedPill() => new BoxEl
    {
        Direction = 0, AlignItems = FlexAlign.Center, Gap = 6f,
        Padding = new Edges4(8f, 4f, 12f, 4f), Corners = CornerRadius4.All(13f), Fill = Tok.AccentDefault,
        Children = [Icon(Mdl.Check, 12f, Tok.TextOnAccentPrimary), new TextEl(Loc.Get(Strings.Artist.Verified)) { Size = 11f, Weight = 700, Color = Tok.TextOnAccentPrimary, CharSpacing = 20f }],
    };

    static Element GlassPill(string text) => new BoxEl
    {
        Padding = new Edges4(12f, 5f, 12f, 5f), Corners = CornerRadius4.All(13f), Fill = WhiteText with { A = 0.16f },
        Children = [new TextEl(text) { Size = 11f, Weight = 700, Color = WhiteText with { A = 0.95f }, CharSpacing = 20f }],
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
            new BoxEl { Width = 64f, Height = 64f, Shrink = 0f, Corners = CornerRadius4.All(WaveeRadius.Control), ClipToBounds = true,
                Children = [Surfaces.Artwork(p.Cover, p.Uri.GetHashCode() & 0x7fffffff, 64f, 64f, WaveeRadius.Control, decodePx: 256)] },
            new BoxEl { Direction = 1, Grow = 1f, Basis = 0f, Gap = 2f,
                Children =
                [
                    new BoxEl { Direction = 0, Gap = 4f, AlignItems = FlexAlign.Center,
                        Children = [ Icon(Mdl.Pin, 11f, WhiteText with { A = 0.7f }), new TextEl(p.Eyebrow) { Size = 10f, Weight = 700, Color = WhiteText with { A = 0.7f }, CharSpacing = 20f } ] },
                    new TextEl(p.Title) { Size = 15f, Weight = 700, Color = WhiteText, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
                    p.Comment.Length == 0 ? new BoxEl() : new TextEl(p.Comment) { Size = 12f, Color = WhiteText with { A = 0.75f }, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
                ] },
        ],
    };

    // ── action affordances ───────────────────────────────────────────────────────────────────────────────
    static Element PlayPill(Action onPlay) => new BoxEl
    {
        Direction = 0, Gap = WaveeSpace.S, AlignItems = FlexAlign.Center,
        Corners = CornerRadius4.All(24f), Padding = new Edges4(22f, 12f, 22f, 12f),
        Fill = Tok.AccentDefault, HoverScale = 1.04f, PressScale = 0.97f, Shadow = Elevation.Card, OnClick = onPlay,
        Children = [Icon(Icons.Play, 16f, Tok.TextOnAccentPrimary), new TextEl(Loc.Get(Strings.Artist.Play)) { Size = 15f, Weight = 700, Color = Tok.TextOnAccentPrimary }],
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
        Children = [Icon(Mdl.RadioTower, 16f, WhiteText), new TextEl(Loc.Get(Strings.Artist.ArtistRadio)) { Size = 14f, Weight = 600, Color = WhiteText }],
    };

    // ── section scaffolding ──────────────────────────────────────────────────────────────────────────────
    internal static BoxEl AccentHeader(string title) => new BoxEl
    {
        Direction = 0, Gap = 10f, AlignItems = FlexAlign.Center,
        Children =
        [
            new BoxEl
            {
                Width = 3f, MinHeight = 22f, AlignSelf = FlexAlign.Stretch,
                Corners = CornerRadius4.All(1.5f), Fill = Tok.AccentDefault,
            },
            WaveeType.RailHeader(title) with { MinWidth = 0f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
        ],
    };

    internal static BoxEl AccentHeader(string title, int count) => new BoxEl
    {
        Direction = 0, Gap = 10f, AlignItems = FlexAlign.Center,
        Children =
        [
            new BoxEl
            {
                Width = 3f, MinHeight = 22f, AlignSelf = FlexAlign.Stretch,
                Corners = CornerRadius4.All(1.5f), Fill = Tok.AccentDefault,
            },
            WaveeType.RailHeader(title) with { MinWidth = 0f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
            new TextEl(count.ToString()) { Size = 15f, Weight = 600, Color = Tok.TextTertiary },
        ],
    };

    static Element Section(string title, Element body) => new BoxEl
    {
        Direction = 1, Gap = WaveeSpace.M, Children = [AccentHeader(title), body],
    };

    static Element SectionN(string title, int count, Element body) => new BoxEl
    {
        Direction = 1, Gap = WaveeSpace.M,
        Children = [ AccentHeader(title, count), body ],
    };

    // Top tracks (left, wider) + Popular releases (right) — a 2-column band, stacked on a narrow page.
    Element TopBand(IReadOnlyList<Track> popular, string uri, PlaybackBridge? bridge, Services svc,
                    IReadOnlyList<Album> albumsAll, Action<string, string?> go, Action<string> play) =>
        Responsive.Of(w =>
        {
            bool wide = w >= 760f;
            // ArtistPopular owns its own header (title + pager) so the pager sits in the section header like WinUI.
            Element left = Embed.Comp(() => new ArtistPopular(popular, uri, bridge, svc, Loc.Get(Strings.Artist.TopTracksReleases)));
            Element right = Section(Loc.Get(Strings.Artist.PopularReleases), PopularReleases(albumsAll, go, play));
            return new BoxEl
            {
                Direction = (byte)(wide ? 0 : 1), Gap = WaveeSpace.XL,
                Children =
                [
                    new BoxEl { Direction = 1, Grow = wide ? 2f : 1f, Basis = 0f, Children = [left] },
                    new BoxEl { Direction = 1, Grow = 1f, Basis = 0f, Children = [right] },
                ],
            };
        }, fallback: 900f);

    static Element PopularReleases(IReadOnlyList<Album> albums, Action<string, string?> go, Action<string> play) => new BoxEl
    {
        Direction = 1, Gap = WaveeSpace.XS,
        Children = albums.Take(4).Select(al => ReleaseRow(al, go, play)).ToArray(),
    };

    static Element ReleaseRow(Album al, Action<string, string?> go, Action<string> play) => new BoxEl
    {
        Direction = 0, Height = 64f, AlignItems = FlexAlign.Center, Gap = WaveeSpace.M,
        Padding = new Edges4(WaveeSpace.S, 0f, WaveeSpace.S, 0f), Corners = CornerRadius4.All(6f),
        HoverFill = Tok.FillSubtleSecondary, PressedFill = Tok.FillSubtleTertiary,
        OnClick = () => go("album:" + al.Uri, al.Name),
        Children =
        [
            new BoxEl { Width = 48f, Height = 48f, Shrink = 0f, Corners = CornerRadius4.All(WaveeRadius.Control), ClipToBounds = true,
                Children = [Surfaces.Artwork(al.Cover, al.Id.GetHashCode() & 0x7fffffff, 48f, 48f, WaveeRadius.Control)] },
            new BoxEl { Direction = 1, Grow = 1f, Basis = 0f, Gap = 1f,
                Children =
                [
                    new TextEl(al.Name) { Size = 14f, Weight = 600, Color = Tok.TextPrimary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
                    new TextEl((al.Year > 0 ? al.Year + " · " : "") + KindLabel(al.Kind)) { Size = 12f, Color = Tok.TextSecondary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
                ] },
        ],
    };

    static string KindLabel(AlbumKind k) => k switch
    {
        AlbumKind.Single => Loc.Get(Strings.Detail.Badge.Single),
        AlbumKind.EP => Loc.Get(Strings.Detail.Badge.Ep),
        AlbumKind.Compilation => Loc.Get(Strings.Detail.Badge.Compilation),
        _ => Loc.Get(Strings.Detail.Badge.Album),
    };

    // ── discography (responsive grids) ───────────────────────────────────────────────────────────────────
    static Element DiscographyGrid(string title, IReadOnlyList<Album> albums, Action<string, string?> go, Action<string> play)
    {
        var cells = albums.Take(24).Select(al => MediaCard.GridCard(al.Cover, al.Name,
            al.Year > 0 ? al.Year + " · " + KindLabel(al.Kind) : KindLabel(al.Kind), al.Uri,
            () => go("album:" + al.Uri, al.Name), () => play(al.Uri))).ToArray();
        return SectionN(title, albums.Count, AutoGrid(180f, WaveeSpace.M, float.NaN, cells));
    }

    static Element AppearsOnShelf(IReadOnlyList<Album> albums, Action<string, string?> go, Action<string> play) => new BoxEl
    {
        Direction = 1,
        Children =
        [
            PagedShelf.Create(
                albums.Count,
                cardAt: (i, w) => MediaCard.Shelf(albums[i].Cover, albums[i].Name,
                    albums[i].Year > 0 ? albums[i].Year.ToString() : KindLabel(albums[i].Kind), albums[i].Uri,
                    () => go("album:" + albums[i].Uri, albums[i].Name), () => play(albums[i].Uri), w),
                measured: true, header: AccentHeader(Loc.Get(Strings.Artist.AppearsOn))),
        ],
    };

    // ── on-tour banner ───────────────────────────────────────────────────────────────────────────────────
    static Element TourBannerCard(TourBanner t, Action onClick) => new BoxEl
    {
        Direction = 0, AlignItems = FlexAlign.Center, Gap = WaveeSpace.L,
        Padding = new Edges4(WaveeSpace.L, WaveeSpace.L, WaveeSpace.L, WaveeSpace.L),
        Corners = CornerRadius4.All(WaveeRadius.Card), Fill = Tok.FillCardSecondary,
        BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault, HoverFill = Tok.FillCardDefault,
        HoverScale = 1.005f, PressScale = 0.997f, OnClick = onClick,
        Children =
        [
            new BoxEl { Width = 44f, Height = 44f, Shrink = 0f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                Corners = CornerRadius4.All(22f), Fill = Tok.AccentDefault,
                Children = [ Icon(t.IsLive ? Mdl.RadioTower : Mdl.Calendar, 18f, Tok.TextOnAccentPrimary) ] },
            new BoxEl { Direction = 1, Grow = 1f, Basis = 0f, Gap = 2f,
                Children =
                [
                    new TextEl(t.Eyebrow) { Size = 11f, Weight = 700, Color = Tok.AccentTextPrimary, CharSpacing = 30f },
                    new TextEl(t.Headline) { Size = 16f, Weight = 700, Color = Tok.TextPrimary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
                    new TextEl(t.Subline) { Size = 13f, Color = Tok.TextSecondary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
                ] },
            Icon(Mdl.ChevronRight, 16f, Tok.TextSecondary),
        ],
    };

    // ── music videos (16:9 shelf) ────────────────────────────────────────────────────────────────────────
    static Element MusicVideosShelf(IReadOnlyList<MusicVideo> videos, Action<string> play) => new BoxEl
    {
        Direction = 1,
        Children =
        [
            PagedShelf.Create(
                videos.Count,
                cardAt: (i, w) => MediaCard.VideoCard(videos[i].Thumbnail, videos[i].Title, Dur(videos[i].DurationMs),
                    videos[i].TrackUri, () => play(videos[i].TrackUri), () => play(videos[i].TrackUri), w),
                measured: true, header: AccentHeader(Loc.Get(Strings.Artist.MusicVideos))),
        ],
    };

    // ── playlists and discovery ──────────────────────────────────────────────────────────────────────────
    static Element PlaylistsShelf(IReadOnlyList<PlaylistRef> pls, Action<string, string?> go, Action<string> play) => new BoxEl
    {
        Direction = 1,
        Children =
        [
            PagedShelf.Create(
                pls.Count,
                cardAt: (i, w) => MediaCard.Shelf(pls[i].Cover, pls[i].Name, pls[i].Subtitle, pls[i].Uri,
                    () => go("pl:" + pls[i].Uri, pls[i].Name), () => play(pls[i].Uri), w),
                measured: true, header: AccentHeader(Loc.Get(Strings.Artist.PlaylistsDiscovery))),
        ],
    };

    // ── upcoming concerts ────────────────────────────────────────────────────────────────────────────────
    static Element ConcertsRow(IReadOnlyList<Concert> concerts) => new BoxEl
    {
        Direction = 1,
        Children =
        [
            PagedShelf.Create(
                concerts.Count,
                cardAt: (i, w) => ConcertStub(concerts[i]),
                measured: true, header: AccentHeader(Loc.Get(Strings.Artist.UpcomingConcerts))),
        ],
    };

    static Element ConcertStub(Concert c) => new BoxEl
    {
        Direction = 0, Grow = 1f, Gap = WaveeSpace.M, AlignItems = FlexAlign.Center,
        Padding = new Edges4(WaveeSpace.M, WaveeSpace.M, WaveeSpace.M, WaveeSpace.M),
        Corners = CornerRadius4.All(WaveeRadius.Card), Fill = Tok.FillCardSecondary,
        BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault,
        Children =
        [
            new BoxEl { Direction = 1, Width = 48f, Shrink = 0f, AlignItems = FlexAlign.Center, Gap = 0f,
                Children =
                [
                    new TextEl(c.Date.ToString("MMM", CultureInfo.InvariantCulture).ToUpperInvariant()) { Size = 11f, Weight = 700, Color = Tok.AccentTextPrimary, CharSpacing = 10f },
                    new TextEl(c.Date.Day.ToString()) { Size = 24f, Weight = 800, Color = Tok.TextPrimary },
                ] },
            new BoxEl { Direction = 1, Grow = 1f, Basis = 0f, Gap = 2f,
                Children =
                [
                    new TextEl(c.Venue) { Size = 14f, Weight = 600, Color = Tok.TextPrimary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
                    new BoxEl { Direction = 0, Gap = 4f, AlignItems = FlexAlign.Center,
                        Children = [ Icon(Mdl.MapPin, 11f, Tok.TextSecondary), new TextEl(c.City) { Size = 12f, Color = Tok.TextSecondary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis } ] },
                ] },
        ],
    };

    // ── merch ────────────────────────────────────────────────────────────────────────────────────────────
    static Element MerchRow(IReadOnlyList<MerchItem> merch) => new BoxEl
    {
        Direction = 1,
        Children =
        [
            PagedShelf.Create(
                merch.Count,
                cardAt: (i, w) => MerchCard(merch[i], w),
                measured: true, header: AccentHeader(Loc.Get(Strings.Artist.Merch))),
        ],
    };

    static Element MerchCard(MerchItem m, float w) => new BoxEl
    {
        Direction = 1, Gap = WaveeSpace.S, Grow = 1f, ClipToBounds = true,
        Padding = new Edges4(WaveeSpace.S, WaveeSpace.S, WaveeSpace.S, WaveeSpace.M),
        Corners = CornerRadius4.All(WaveeRadius.Card), Fill = Tok.FillCardSecondary,
        BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault, HoverFill = Tok.FillCardDefault, HoverScale = 1.02f,
        Children =
        [
            new BoxEl { ZStack = true, ClipToBounds = true, Corners = CornerRadius4.All(WaveeRadius.Control),
                Children = [ Surfaces.ArtworkFill(m.Image, WaveeRadius.Control) ] },
            new TextEl(m.Name) { Size = 13f, Weight = 600, Color = Tok.TextPrimary, Wrap = TextWrap.Wrap, MaxLines = 2, Trim = TextTrim.CharacterEllipsis },
            new TextEl(m.Price) { Size = 13f, Weight = 700, Color = Tok.AccentTextPrimary, MaxLines = 1 },
        ],
    };

    // ── biography + profile facts + listened-most-in (2-column band) ─────────────────────────────────────
    Element BiographyBand(Artist a, int albums, int singles, ArtistExtras? extras, int relatedCount, Action<string, string?> go) =>
        Responsive.Of(w =>
        {
            bool wide = w >= 820f;
            var left = new BoxEl
            {
                Direction = 1, Gap = WaveeSpace.L, Grow = wide ? 2f : 1f, Basis = 0f,
                Padding = new Edges4(WaveeSpace.XL, WaveeSpace.L, WaveeSpace.XL, WaveeSpace.L),
                Corners = CornerRadius4.All(WaveeRadius.Card), Fill = Tok.FillCardSecondary,
                BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault,
                Children =
                [
                    AccentHeader(Loc.Get(Strings.Artist.Biography)),
                    a.Bio is { Length: > 0 }
                        ? RichText.Of(a.Bio, 14f, Tok.TextSecondary, Tok.AccentTextPrimary, w * (wide ? 0.62f : 1f) - 60f, 14, key => go(key, null))
                        : new BoxEl(),
                    extras?.ExternalLinks is { Count: > 0 } links ? ExternalLinkPills(links) : new BoxEl(),
                    extras?.TopCities is { Count: > 0 } cities ? TopCitiesList(cities) : new BoxEl(),
                ],
            };
            var right = new BoxEl
            {
                Direction = 1, Gap = WaveeSpace.M, Grow = 1f, Basis = 0f,
                Children =
                [
                    AccentHeader(Loc.Get(Strings.Artist.ProfileFacts)),
                    new BoxEl
                    {
                        Direction = 0, Gap = WaveeSpace.M, Wrap = true,
                        Children =
                        [
                            StatTile(Count(a.MonthlyListeners), Loc.Get(Strings.Artist.Stat.Monthly)),
                            StatTile(Count(a.Followers), Loc.Get(Strings.Artist.Stat.Followers)),
                            StatTile(albums.ToString(), Loc.Get(Strings.Artist.Stat.Albums)),
                            StatTile(singles.ToString(), Loc.Get(Strings.Artist.Stat.Singles)),
                            StatTile((extras?.Concerts?.Count ?? 0).ToString(), Loc.Get(Strings.Artist.Stat.Concerts)),
                            StatTile(relatedCount.ToString(), Loc.Get(Strings.Artist.Stat.Related)),
                        ],
                    },
                ],
            };
            return new BoxEl { Direction = (byte)(wide ? 0 : 1), Gap = WaveeSpace.XL, Children = [left, right] };
        }, fallback: 900f);

    static Element ExternalLinkPills(IReadOnlyList<ExternalLink> links) => new BoxEl
    {
        Direction = 0, Gap = WaveeSpace.S, Wrap = true,
        Children = links.Select(l => (Element)new BoxEl
        {
            Direction = 0, Gap = 6f, AlignItems = FlexAlign.Center,
            Padding = new Edges4(12f, 7f, 14f, 7f), Corners = CornerRadius4.All(WaveeRadius.Pill),
            BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault, HoverFill = Tok.FillSubtleSecondary,
            Children = [ Icon(Mdl.Link, 13f, Tok.TextSecondary), new TextEl(l.Name) { Size = 13f, Weight = 600, Color = Tok.TextPrimary } ],
        }).ToArray(),
    };

    static Element TopCitiesList(IReadOnlyList<TopCity> cities)
    {
        long max = 1;
        foreach (var c in cities) if (c.Listeners > max) max = c.Listeners;
        var rows = new List<Element>(cities.Count + 1) { new TextEl(Loc.Get(Strings.Artist.ListenedMostIn)) { Size = 13f, Weight = 700, Color = Tok.TextTertiary, CharSpacing = 10f } };
        foreach (var c in cities) rows.Add(CityBarRow(c, max));
        return new BoxEl { Direction = 1, Gap = WaveeSpace.S, Children = rows.ToArray() };
    }

    static Element CityBarRow(TopCity c, long max)
    {
        float frac = max > 0 ? (float)((double)c.Listeners / max) : 0f;
        return new BoxEl
        {
            Direction = 1, Gap = 4f,
            Children =
            [
                new BoxEl { Direction = 0, AlignItems = FlexAlign.Center,
                    Children =
                    [
                        new TextEl(c.City) { Size = 14f, Color = Tok.TextPrimary, Grow = 1f, Basis = 0f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
                        new TextEl(Count(c.Listeners)) { Size = 13f, Color = Tok.TextSecondary },
                    ] },
                new BoxEl { Direction = 0, Height = 4f,
                    Children =
                    [
                        new BoxEl { Grow = MathF.Max(0.001f, frac), Height = 4f, Corners = CornerRadius4.All(2f), Fill = Tok.AccentDefault },
                        new BoxEl { Grow = MathF.Max(0.001f, 1f - frac), Height = 4f },
                    ] },
            ],
        };
    }

    static Element StatTile(string value, string label) => new BoxEl
    {
        Direction = 1, Gap = WaveeSpace.XS, Grow = 1f, Basis = 140f,
        Padding = new Edges4(WaveeSpace.L, WaveeSpace.L, WaveeSpace.L, WaveeSpace.L),
        Corners = CornerRadius4.All(WaveeRadius.Card), Fill = Tok.FillCardSecondary,
        BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault,
        Children = [new TextEl(value) { Size = 26f, Weight = 800, Color = Tok.TextPrimary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis }, new TextEl(label) { Size = 12f, Color = Tok.TextSecondary }],
    };

    // ── gallery ──────────────────────────────────────────────────────────────────────────────────────────
    static Element GalleryStrip(IReadOnlyList<Image> photos) => new BoxEl
    {
        Direction = 1,
        Children =
        [
            PagedShelf.Create(
                photos.Count,
                cardAt: (i, w) => new BoxEl { Width = w, Height = w, Corners = CornerRadius4.All(WaveeRadius.Card), ClipToBounds = true,
                    Children = [ Surfaces.Artwork(photos[i], i, w, w, WaveeRadius.Card, decodePx: 480) ] },
                measured: true, header: AccentHeader(Loc.Get(Strings.Artist.Gallery))),
        ],
    };

    // ── fans also like ───────────────────────────────────────────────────────────────────────────────────
    static Element RelatedShelf(IReadOnlyList<RelatedArtist> related, Action<string, string?> go, Action<string> play) => new BoxEl
    {
        Direction = 1,
        Children =
        [
            PagedShelf.Create(
                related.Count,
                cardAt: (i, w) => MediaCard.Shelf(related[i].Image, related[i].Name, Loc.Get(Strings.Search.TypeArtist), related[i].Uri,
                    () => go("artist:" + related[i].Uri, related[i].Name), () => play(related[i].Uri), w, circular: true),
                measured: true, header: AccentHeader(Loc.Get(Strings.Detail.FansAlsoLike))),
        ],
    };

    static Element FansShelf(IReadOnlyList<Artist> fans, Action<string, string?> go, Action<string> play) => new BoxEl
    {
        Direction = 1,
        Children =
        [
            PagedShelf.Create(
                fans.Count,
                cardAt: (i, w) => MediaCard.Shelf(fans[i].Image, fans[i].Name, Loc.Get(Strings.Search.TypeArtist), fans[i].Uri,
                    () => go("artist:" + fans[i].Uri, fans[i].Name), () => play(fans[i].Uri), w, circular: true),
                measured: true, header: AccentHeader(Loc.Get(Strings.Detail.FansAlsoLike))),
        ],
    };

    static string Count(long n) => n.ToString("N0");
    static string Dur(long ms) { var t = TimeSpan.FromMilliseconds(ms); return t.Hours > 0 ? $"{t.Hours}:{t.Minutes:D2}:{t.Seconds:D2}" : $"{t.Minutes}:{t.Seconds:D2}"; }
    static ColorF Scrim(float a) => ColorF.FromRgba(0, 0, 0) with { A = a };               // black-with-alpha hero scrim stop
    static readonly ColorF WhiteText = ColorF.FromRgba(255, 255, 255);

    // ── loading skeleton ───────────────────────────────────────────────────────────────────────────────────
    static Element Skeleton() => new BoxEl
    {
        Direction = 1,
        Children =
        [
            new BoxEl { Height = 420f, Fill = Tok.FillCardDefault },
            new BoxEl
            {
                Direction = 1, Gap = WaveeSpace.XL,
                Padding = new Edges4(32f, 40f, 32f, WaveeSpace.XXL),
                Children =
                [
                    new BoxEl { Direction = 0, Gap = WaveeSpace.M, Children = [SkelBar(160f, 48f), SkelBar(44f, 44f), SkelBar(110f, 36f)] },
                    SkelRows(5),
                ],
            },
        ],
    };

    static Element SkelBar(float w, float h) => new BoxEl { Width = w, Height = h, Corners = CornerRadius4.All(WaveeRadius.Control), Fill = Tok.FillCardDefault };

    static Element SkelRows(int n)
    {
        var rows = new Element[n];
        for (int i = 0; i < n; i++)
            rows[i] = new BoxEl
            {
                Direction = 0, Height = 56f, AlignItems = FlexAlign.Center, Gap = WaveeSpace.M,
                Children = [SkelBar(20f, 14f), SkelBar(40f, 40f), new BoxEl { Grow = 1f, Height = 14f, Corners = CornerRadius4.All(4f), Fill = Tok.FillCardDefault }, SkelBar(40f, 12f)],
            };
        return new BoxEl { Direction = 1, Gap = WaveeSpace.S, Children = rows };
    }
}

// The floating "shy" artist pill — revealed once the hero scrolls past the viewport top (the sticky sentinel in the page
// body flips `pinned`). Kept SMALL (avatar + name + monthly listeners + Play + Follow) and rendered through a pass-through
// overlay so it never blocks scrolling. Its own component so a pin toggle re-renders only this, not the whole page.
sealed class ArtistShyPill : Component
{
    readonly string _uri;
    readonly Loadable<Artist> _artist;
    readonly Signal<bool> _pinned;
    readonly Services _svc;
    public ArtistShyPill(string uri, Loadable<Artist> artist, Signal<bool> pinned, Services svc)
    { _uri = uri; _artist = artist; _pinned = pinned; _svc = svc; }

    public override Element Render()
    {
        if (!_pinned.Value) return new BoxEl();          // not stuck → no pill, no hit area
        var a = _artist.Value.Value;                     // Loadable.Value is a Signal<Artist>; read its value
        return new BoxEl
        {
            Direction = 0, Gap = WaveeSpace.M, AlignItems = FlexAlign.Center,
            Padding = new Edges4(WaveeSpace.S, WaveeSpace.S, WaveeSpace.L, WaveeSpace.S),
            Corners = CornerRadius4.All(28f), Acrylic = Tok.AcrylicFlyout, Fill = Tok.FillLayerDefault, Shadow = Elevation.Card,
            BorderWidth = 1f, BorderColor = Tok.StrokeSurfaceDefault,
            Children =
            [
                new BoxEl { Width = 40f, Height = 40f, Shrink = 0f, Corners = CornerRadius4.All(20f), ClipToBounds = true,
                    Children = [ Surfaces.Artwork(a.Image, a.Id.GetHashCode() & 0x7fffffff, 40f, 40f, 20f, decodePx: 256) ] },
                new BoxEl { Direction = 1, Gap = 1f,
                    Children =
                    [
                        new TextEl(a.Name) { Size = 14f, Weight = 700, Color = Tok.TextPrimary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
                        new TextEl(Strings.Artist.MonthlyListeners(a.MonthlyListeners.ToString("N0"))) { Size = 12f, Color = Tok.TextSecondary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
                    ] },
                new BoxEl
                {
                    Direction = 0, Gap = WaveeSpace.S, AlignItems = FlexAlign.Center,
                    Corners = CornerRadius4.All(18f), Padding = new Edges4(16f, 8f, 16f, 8f),
                    Fill = Tok.AccentDefault, HoverScale = 1.04f, PressScale = 0.97f,
                    OnClick = () => _ = _svc.Player.PlayAsync(_uri, 0),
                    Children = [ Icon(Icons.Play, 14f, Tok.TextOnAccentPrimary), new TextEl(Loc.Get(Strings.Artist.Play)) { Size = 13f, Weight = 700, Color = Tok.TextOnAccentPrimary } ],
                },
                Embed.Comp(() => new FollowButton(_uri)),
            ],
        };
    }
}

// The "Popular" top-tracks list for an artist. Its own component so the now-playing equalizer re-skins on playback
// changes WITHOUT re-rendering the whole artist page. Each row plays the artist context at its index (the same ordered
// set FakeData.ContextTracks resolves for the artist uri), so #1 here is #1 in the queue.
sealed class ArtistPopular : Component
{
    readonly IReadOnlyList<Track> _tracks;
    readonly string _ctx, _title;
    readonly PlaybackBridge? _bridge;
    readonly Services _svc;
    public ArtistPopular(IReadOnlyList<Track> tracks, string ctx, PlaybackBridge? bridge, Services svc, string title)
    { _tracks = tracks; _ctx = ctx; _bridge = bridge; _svc = svc; _title = title; }

    const int Rows = 4;          // WinUI ColumnsFirstGridLayout MaxRows
    const int MaxTracks = 10;
    const float ItemHeight = 68f;
    const float RowGap = 4f;
    const float ColumnGap = 8f;
    const float MinItemWidth = 280f;
    const int MaxColumns = 3;

    // WinUI ArtistPage: ColumnsFirstGridLayout, four 68px rows, 280px minimum cells, column-first pagination.

    public override Element Render()
    {
        var go = UseContext(HistoryStore.NavCtx);
        var lib = UseContext(LibraryBridge.Slot);
        var width = UseSignal(600f);                     // self-measured → responsive column count
        var page = UseSignal(0);
        var dragX = UseRef(0f);                          // horizontal swipe anchor (trackpad/touch/mouse) → page flip
        var cur = _bridge?.CurrentTrack.Value;          // subscribe → now-playing equalizer
        bool playing = _bridge?.IsPlaying.Value ?? false;
        bool buffering = _bridge?.IsBuffering.Value ?? false;

        int total = Math.Min(_tracks.Count, MaxTracks);
        int cols = Math.Clamp((int)MathF.Floor((width.Value + ColumnGap) / (MinItemWidth + ColumnGap)), 1, MaxColumns);
        int perPage = cols * Rows;
        int pages = Math.Max(1, (total + perPage - 1) / perPage);
        int pg = Math.Min(page.Value, pages - 1);

        Element Cell(int c, int r)                       // column-first: cell(r,c) = the (c*Rows+r)-th track on this page
        {
            int gi = pg * perPage + c * Rows + r;
            if (gi >= total) return new BoxEl { Grow = 1f, Basis = 0f };
            var t = _tracks[gi];
            int idx = gi;
            bool isNow = cur is not null && cur.Id == t.Id;
            var st = new TrackRow.State(isNow, isNow && playing, isNow && buffering, IsTop: false,
                                        Saved: t.Uri.Length > 0 && (lib?.IsSaved(t.Uri) ?? false));   // subscribe → heart re-skins on toggle
            return CompactTrack(
                t, st, go,
                onPlay: () => PlayRow(idx, t),
                onLike: t.Uri.Length > 0 ? () => lib?.ToggleSaved(t.Uri) : null,
                onPointerDown: p => dragX.Value = p.X,
                onDrag: Swipe);
        }

        var rowEls = new Element[Rows];
        for (int r = 0; r < Rows; r++)
        {
            var cells = new Element[cols];
            for (int c = 0; c < cols; c++) cells[c] = Cell(c, r);
            rowEls[r] = new BoxEl { Direction = 0, Gap = ColumnGap, Children = cells };
        }

        var header = new BoxEl
        {
            Direction = 0, AlignItems = FlexAlign.Center, Gap = WaveeSpace.M,
            Children =
            [
                ArtistPage.AccentHeader(_title) with { Grow = 1f, Basis = 0f },
                pages > 1 ? Pager(pg, pages, page) : new BoxEl(),
            ],
        };

        // Trackpad / touch / mouse horizontal swipe flips pages (FlipView-style). DragYieldsToPan lets a vertical drag
        // fall through to the page scroller while a horizontal drag pages here; a tap still reaches the track rows.
        void Swipe(Point2 p)
        {
            if (pages <= 1) return;
            float dx = p.X - dragX.Value;
            if (MathF.Abs(dx) < 56f) return;
            int np = Math.Clamp(pg + (dx < 0f ? 1 : -1), 0, pages - 1);
            if (np != page.Peek()) page.Value = np;
            dragX.Value = p.X;                           // re-anchor so a long drag can page again
        }

        return new BoxEl
        {
            Direction = 1, Gap = 20f,
            OnBoundsChanged = r => { if (r.W > 0f && MathF.Abs(r.W - width.Peek()) > 0.5f) width.Value = r.W; },
            Children = [ header, new BoxEl { Direction = 1, Gap = RowGap, Children = rowEls } ],
        };
    }

    // Source-matched WinUI TrackItem Compact cell: transparent at rest, one-cell hover plate, 48px artwork,
    // title + explicit/video/artists + full play count, then heart and duration. Swipe input belongs to each cell,
    // rather than the shared band, so hovering one track cannot light every track at once.
    static Element CompactTrack(Track t, in TrackRow.State st, Action<string, string?> go,
                                Action onPlay, Action? onLike, Action<Point2> onPointerDown, Action<Point2> onDrag)
    {
        var metadata = new List<Element>(4);
        if (t.IsExplicit) metadata.Add(ExplicitBadge());
        if (t.HasVideo)
        {
            metadata.Add(new BoxEl
            {
                Opacity = 0.7f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                Children = [Icon(Icons.Movie, 13f, Tok.TextTertiary)],
            });
            metadata.Add(new TextEl("·") { Size = 12f, Color = Tok.TextTertiary });
        }
        metadata.Add(TrackRow.ArtistLinks(t.Artists, go));

        Element artOverlay = st.IsBuffering
            ? TrackRow.Spinner()
            : new BoxEl
            {
                Width = 48f, Height = 48f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                Fill = ColorF.FromRgba(0, 0, 0, (byte)(Tok.Theme == ThemeKind.Light ? 70 : 125)),
                Opacity = 0f, HoverOpacity = 1f, HoverDurationMs = 140f,
                Children = [Icon(st.IsNow && st.IsPlaying ? Icons.Pause : Icons.Play, 20f, ColorF.FromRgba(255, 255, 255))],
            };

        Element nowPlaying = st.IsNow
            ? new BoxEl
            {
                Grow = 1f, Direction = 1, Justify = FlexJustify.End, AlignItems = FlexAlign.End,
                Padding = new Edges4(0f, 0f, 3f, 3f),
                Children =
                [
                    new BoxEl
                    {
                        Padding = new Edges4(2f, 2f, 2f, 2f), Corners = CornerRadius4.All(6f),
                        Fill = ColorF.FromRgba(0, 0, 0, 204),
                        Children = [WaveeEqualizer.Of(st.IsPlaying, Tok.AccentTextPrimary, 14f)],
                    },
                ],
            }
            : new BoxEl();

        return new BoxEl
        {
            Direction = 0, Grow = 1f, Basis = 0f, MinWidth = 0f, MinHeight = ItemHeight,
            Gap = 12f, Padding = new Edges4(8f, 4f, 8f, 4f), AlignItems = FlexAlign.Center,
            Corners = CornerRadius4.All(6f), ClipToBounds = true,
            Fill = ColorF.Transparent, HoverFill = Tok.FillCardDefault, PressedFill = Tok.FillSubtleSecondary,
            BorderWidth = 1f, BorderColor = ColorF.Transparent, HoverBorderColor = Tok.StrokeCardDefault,
            PressScale = 0.99f, Role = AutomationRole.Button, OnClick = onPlay,
            OnPointerDown = onPointerDown, OnDrag = onDrag, DragYieldsToPan = true,
            OnPointerExit = static () => { },
            Children =
            [
                new BoxEl
                {
                    Width = 48f, Height = 48f, Shrink = 0f, ZStack = true, ClipToBounds = true,
                    Corners = CornerRadius4.All(4f),
                    Children =
                    [
                        Surfaces.Artwork(t.Image, t.Id.GetHashCode() & 0x7fffffff, 48f, 48f, 4f, decodePx: 64),
                        nowPlaying,
                        artOverlay,
                    ],
                },
                new BoxEl
                {
                    Direction = 1, Grow = 1f, Basis = 0f, MinWidth = 0f, Gap = 2f, Justify = FlexJustify.Center,
                    Children =
                    [
                        new TextEl(t.Title)
                        {
                            Size = 13f, Weight = 600, Color = st.IsNow ? Tok.AccentTextPrimary : Tok.TextPrimary,
                            MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
                        },
                        new BoxEl { Direction = 0, Gap = 4f, AlignItems = FlexAlign.Center, Children = metadata.ToArray() },
                        new TextEl($"{t.PlayCount:N0} plays")
                        {
                            Size = 10f, Color = Tok.TextTertiary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
                        },
                    ],
                },
                TrackRow.Heart(st.Saved, onLike),
                new BoxEl
                {
                    Padding = new Edges4(6f, 0f, 6f, 0f), AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                    Children = [new TextEl(DetailFormat.TrackTime(t.DurationMs)) { Size = 12f, Color = Tok.TextSecondary }],
                },
            ],
        };
    }

    static Element ExplicitBadge() => new BoxEl
    {
        MinWidth = 13f, Height = 13f, Padding = new Edges4(2f, 0f, 2f, 0f),
        Corners = CornerRadius4.All(2f), BorderWidth = 1f, BorderColor = Tok.TextTertiary,
        Opacity = 0.6f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
        Children = [new TextEl("E") { Size = 8f, Weight = 600, Color = Tok.TextTertiary }],
    };

    static Element Pager(int pg, int pages, Signal<int> page) => new BoxEl
    {
        Direction = 0, Gap = WaveeSpace.XS, AlignItems = FlexAlign.Center,
        Children =
        [
            Chevron(Mdl.ChevronLeft, pg > 0, () => page.Value = pg - 1),
            new TextEl($"{pg + 1}/{pages}") { Size = 12f, Weight = 600, Color = Tok.TextSecondary },
            Chevron(Mdl.ChevronRight, pg < pages - 1, () => page.Value = pg + 1),
        ],
    };

    static Element Chevron(string glyph, bool enabled, Action onClick) => new BoxEl
    {
        Width = 28f, Height = 28f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
        Corners = CornerRadius4.All(14f), HoverFill = enabled ? Tok.FillSubtleSecondary : default,
        HoverScale = enabled ? 1.06f : 1f, OnClick = enabled ? onClick : null,
        Children = [ Icon(glyph, 12f, enabled ? Tok.TextSecondary : Tok.TextTertiary) ],
    };

    // Single click PLAYS this track in the artist context (so #1 here == #1 in the queue), or pauses/resumes it when it's
    // already the now-playing one — the same transport semantics as the detail list's row.
    void PlayRow(int i, Track t)
    {
        if (_bridge is not null && _bridge.CurrentTrack.Peek()?.Id == t.Id)
        {
            bool p = _bridge.IsPlaying.Peek();
            _bridge.IsPlaying.Value = !p;
            if (p) _ = _bridge.Player.PauseAsync(); else _ = _bridge.Player.ResumeAsync();
            return;
        }
        _ = _svc.Player.PlayAsync(_ctx, i);
    }
}
