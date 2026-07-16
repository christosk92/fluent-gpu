using System;
using System.Collections.Generic;
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

// The hero-adjacent "Top tracks + Popular releases" band (the ArtistPopular grid lives in its own component file).
sealed partial class ArtistPage : Component
{
    // Top tracks (left, wider) + Popular releases (right) — a 2-column band, stacked on a narrow page.
    Element TopBand(IReadOnlyList<Track> popular, string uri, PlaybackBridge? bridge, Services svc,
                    IReadOnlyList<Album> albumsAll, Action<string, string?> go, Action<string> play,
                    Func<ColorF> accent) =>
        Responsive.Of(w =>
        {
            bool wide = w >= 760f;
            // ArtistPopular owns its own header (title + pager) so the pager sits in the section header like WinUI.
            string popTitle = Loc.Get(Strings.Artist.TopTracksReleases);
            Element left = Embed.Comp(() => new ArtistPopular(popular, uri, bridge, svc, popTitle, accent))
                with { SkeletonProxy = () => ArtistPopular.SkeletonShape(popular, popTitle) };
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

    internal static string KindLabel(AlbumKind k) => k switch
    {
        AlbumKind.Single => Loc.Get(Strings.Detail.Badge.Single),
        AlbumKind.EP => Loc.Get(Strings.Detail.Badge.Ep),
        AlbumKind.Compilation => Loc.Get(Strings.Detail.Badge.Compilation),
        _ => Loc.Get(Strings.Detail.Badge.Album),
    };
}
