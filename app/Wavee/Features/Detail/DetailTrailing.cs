using System;
using System.Collections.Generic;
using System.Linq;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Localization;
using Wavee.Core;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

// Album / single trailing sections, appended below the (eager) track rows in the outer scroller: an About-the-artist
// card and a "More by <artist>" PagedShelf (the HomePage shelf recipe — chevron pager + width-reactive recycle).
// ("Fans also like" is omitted in v1: the domain model carries no related-artists list — a data gap, not a UI cut.)
static class DetailTrailing
{
    public static Element[] Build(DetailModel m, DetailHandlers h)
    {
        var sections = new List<Element>(5);

        // A single leads with its official video (the reference's top-of-column card).
        if (m.ReleaseKind == AlbumKind.Single && m.HasVideo)
            sections.Add(WatchVideoSection(m, h));

        var artist = m.AboutArtist;
        if (artist is not null)
        {
            sections.Add(Section(Loc.Get(Strings.Detail.AboutTheArtist), AboutCard(artist, h)));
            if (m.Fans is { Count: > 0 })
                sections.Add(Section(Loc.Get(Strings.Detail.FansAlsoLike), FansRow(m.Fans, h)));
            if (artist.TopAlbums is { Count: > 0 })
                sections.Add(MoreBy(artist, artist.TopAlbums, h));
        }
        if (m.FeaturedOn is { Count: > 0 })
            sections.Add(FeaturedSection(m.FeaturedOn, h));

        return sections.ToArray();
    }

    // "Watch the official video" — a thumbnail (the cover) with a centered play badge + the release label/meta.
    static Element WatchVideoSection(DetailModel m, DetailHandlers h) => new BoxEl
    {
        Direction = 1,
        Padding = new Edges4(WaveeSpace.L, WaveeSpace.XL, WaveeSpace.L, 0f),
        Children =
        [
            new BoxEl
            {
                Direction = 0, Gap = WaveeSpace.L, AlignItems = FlexAlign.Center,
                Padding = new Edges4(WaveeSpace.M, WaveeSpace.M, WaveeSpace.L, WaveeSpace.M),
                Corners = CornerRadius4.All(WaveeRadius.Card), Fill = Tok.FillCardSecondary,
                BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault, ClipToBounds = true,
                HoverFill = Tok.FillCardDefault, OnClick = () => h.PlayContext(m.ContextUri ?? ""),
                Children =
                [
                    new BoxEl
                    {
                        Width = 200f, Height = 116f, Shrink = 0f, ZStack = true,
                        Corners = CornerRadius4.All(WaveeRadius.Control), ClipToBounds = true,
                        Children =
                        [
                            Surfaces.Artwork(m.Cover, m.Title.GetHashCode() & 0x7fffffff, 200f, 116f, WaveeRadius.Control),
                            new BoxEl
                            {
                                Width = 200f, Height = 116f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                                Children =
                                [
                                    new BoxEl
                                    {
                                        Width = 44f, Height = 44f, Corners = CornerRadius4.All(22f), Fill = Tok.AccentDefault,
                                        AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                                        Children = [Icon(Icons.Play, 16f, Tok.TextOnAccentPrimary)],   // theme-aware (black on accent in dark)
                                    },
                                ],
                            },
                        ],
                    },
                    new BoxEl
                    {
                        Direction = 1, Grow = 1f, Basis = 0f, Gap = WaveeSpace.XS,
                        Children =
                        [
                            new TextEl(Loc.Get(Strings.Detail.WatchOfficialVideo)) { Size = 11f, Weight = 700, Color = Tok.TextTertiary, CharSpacing = 60f },
                            WaveeType.RailHeader(m.Title) with { MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
                            new TextEl(m.MetaLine) { Size = 12f, Color = Tok.TextSecondary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
                        ],
                    },
                ],
            },
        ],
    };

    // "Fans also like" — a clipped row of artist chips (avatar + name).
    static Element FansRow(IReadOnlyList<Artist> fans, DetailHandlers h) => new BoxEl
    {
        Direction = 0, Gap = WaveeSpace.S, ClipToBounds = true,
        Children = fans.Take(8).Select(a => ArtistChip(a, h)).ToArray(),
    };

    static Element ArtistChip(Artist a, DetailHandlers h) => new BoxEl
    {
        Direction = 0, AlignItems = FlexAlign.Center, Gap = WaveeSpace.S, Shrink = 0f,
        Padding = new Edges4(WaveeSpace.XS, WaveeSpace.XS, WaveeSpace.M, WaveeSpace.XS),
        Corners = CornerRadius4.All(20f), BorderWidth = 1f, BorderColor = Tok.StrokeControlDefault,
        HoverFill = Tok.FillSubtleSecondary, PressedFill = Tok.FillSubtleTertiary,
        OnClick = () => h.Go("artist:" + a.Uri, a.Name),
        Children =
        [
            new BoxEl
            {
                Width = 28f, Height = 28f, Corners = CornerRadius4.All(14f), ClipToBounds = true,
                Children = [Surfaces.Artwork(a.Image, a.Id.GetHashCode() & 0x7fffffff, 28f, 28f, 14f)],
            },
            new TextEl(a.Name) { Size = 13f, Weight = 600, Color = Tok.TextPrimary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
        ],
    };

    // "Featured on" — a playlist PagedShelf (the More-by recipe, playlists instead of albums).
    static Element FeaturedSection(IReadOnlyList<PlaylistSummary> pls, DetailHandlers h) => new BoxEl
    {
        Direction = 1,
        Padding = new Edges4(WaveeSpace.L, WaveeSpace.XL, WaveeSpace.L, WaveeSpace.L),
        Children =
        [
            PagedShelf.Create(
                pls.Count,
                cardAt: (i, w) => MediaCard.Shelf(pls[i].Cover, pls[i].Name, pls[i].OwnerName, pls[i].Uri,
                    () => h.Go("pl:" + pls[i].Uri, pls[i].Name), () => h.PlayContext(pls[i].Uri), w),
                measured: true,
                header: WaveeType.RailHeader(Loc.Get(Strings.Detail.FeaturedOn))),
        ],
    };

    static Element Section(string title, Element body) => new BoxEl
    {
        Direction = 1, Gap = WaveeSpace.M,
        Padding = new Edges4(WaveeSpace.L, WaveeSpace.XL, WaveeSpace.L, WaveeSpace.L),
        Children = [WaveeType.RailHeader(title), body],
    };

    static Element AboutCard(Artist artist, DetailHandlers h) => new BoxEl
    {
        Direction = 0, Gap = WaveeSpace.L, AlignItems = FlexAlign.Center,
        Padding = new Edges4(WaveeSpace.L, WaveeSpace.M, WaveeSpace.L, WaveeSpace.M),
        Corners = CornerRadius4.All(WaveeRadius.Card), Fill = Tok.FillCardSecondary,
        BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault,
        OnClick = () => h.Go("artist:" + artist.Uri, artist.Name),
        HoverFill = Tok.FillCardDefault, ClipToBounds = true,
        Children =
        [
            new BoxEl
            {
                Width = 64f, Height = 64f, Corners = CornerRadius4.All(32f), ClipToBounds = true,
                Children = [Surfaces.Artwork(artist.Image, artist.Id.GetHashCode() & 0x7fffffff, 64f, 64f, 32f)],
            },
            new BoxEl
            {
                Direction = 1, Grow = 1f, Basis = 0f, Gap = WaveeSpace.XS,
                Children =
                [
                    new TextEl(Loc.Get(Strings.Detail.Artist)) { Size = 11f, Weight = 700, Color = Tok.TextTertiary, CharSpacing = 60f },
                    WaveeType.RailHeader(artist.Name) with { MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
                ],
            },
            FollowPill(() => { /* TODO: ILibraryMutations follow (no Core command yet) */ }),
        ],
    };

    static Element FollowPill(Action onClick) => new BoxEl
    {
        Direction = 0, Height = 32f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
        Padding = new Edges4(WaveeSpace.L, 0f, WaveeSpace.L, 0f), Corners = CornerRadius4.All(16f),
        BorderWidth = 1f, BorderColor = Tok.StrokeControlDefault,
        HoverFill = Tok.FillSubtleSecondary, PressedFill = Tok.FillSubtleTertiary, OnClick = onClick,
        Children = [new TextEl(Loc.Get(Strings.Detail.Follow)) { Size = 13f, Weight = 600, Color = Tok.TextSecondary }],
    };

    static Element MoreBy(Artist artist, IReadOnlyList<Album> albums, DetailHandlers h) => new BoxEl
    {
        Direction = 1,
        Padding = new Edges4(WaveeSpace.L, WaveeSpace.XL, WaveeSpace.L, WaveeSpace.L),
        Children =
        [
            PagedShelf.Create(
                albums.Count,
                cardAt: (i, w) => MediaCard.Shelf(
                    albums[i].Cover, albums[i].Name,
                    albums[i].Year > 0 ? albums[i].Year.ToString() : Loc.Get(Strings.Detail.Badge.Album),
                    albums[i].Uri,
                    () => h.Go("album:" + albums[i].Uri, albums[i].Name),
                    () => h.PlayContext(albums[i].Uri), w),
                measured: true,
                header: WaveeType.RailHeader(Strings.Detail.MoreBy(artist.Name))),
        ],
    };
}
