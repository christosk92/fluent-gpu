using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using Wavee.Core;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

// The paged magazine shelves below the discography: on-tour banner, music videos, playlists, concerts, merch,
// gallery, and the "fans also like" / related shelves.
sealed partial class ArtistPage : Component
{
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
}
