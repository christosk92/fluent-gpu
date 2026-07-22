using System;
using System.Collections.Generic;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using Wavee.Core;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

// The hero-adjacent "Top tracks + Releases" band.
sealed partial class ArtistPage : Component
{
    // Top tracks (left, wider) + Releases masthead+strip (right) — stacked on a narrow page.
    Element TopBand(IReadOnlyList<Track> popular, string uri, PlaybackBridge? bridge, Services svc,
                    Album? latest, IReadOnlyList<Album>? popularReleases,
                    Action<string, string?> go, Action<string> play, Func<ColorF> accent) =>
        Responsive.Of(w =>
        {
            bool wide = w >= 760f;
            string popTitle = Loc.Get(Strings.Artist.TopTracks);
            Element left = Embed.Comp(() => new ArtistPopular(popular, uri, bridge, svc, popTitle, accent))
                with { SkeletonProxy = () => ArtistPopular.SkeletonShape(popular, popTitle) };
            Element right = ReleasesColumn(latest, popularReleases, go, play, accent);
            return new BoxEl
            {
                Direction = (byte)(wide ? 0 : 1), Gap = Spacing.XL,
                // Side-by-side: top-align — the releases column must not cross-stretch to the taller
                // chart's height (the strip chips would absorb the leftover and float their labels).
                AlignItems = wide ? FlexAlign.Start : FlexAlign.Stretch,
                Children =
                [
                    new BoxEl { Direction = 1, Grow = wide ? 2f : 1f, Basis = 0f, MinWidth = 0f, Children = [left] },
                    new BoxEl { Direction = 1, Grow = 1f, Basis = 0f, MinWidth = 0f, Children = [right] },
                ],
            };
        }, fallback: 900f);

    Element ReleasesColumn(Album? latest, IReadOnlyList<Album>? popular, Action<string, string?> go, Action<string> play,
                           Func<ColorF> accent)
    {
        var popularList = popular ?? Array.Empty<Album>();
        bool hasLatest = latest is { Name.Length: > 0, Uri.Length: > 0 };
        Album? mast = hasLatest ? latest : popularList.Count > 0 ? popularList[0] : null;
        if (mast is null) return new BoxEl();

        string mastUri = mast.Uri;
        var strip = new List<Album>(3);
        for (int i = 0; i < popularList.Count && strip.Count < 3; i++)
        {
            var al = popularList[i];
            if (al.Uri.Length > 0 && string.Equals(al.Uri, mastUri, StringComparison.OrdinalIgnoreCase)) continue;
            strip.Add(al);
        }

        string title = hasLatest ? Loc.Get(Strings.Artist.Releases) : Loc.Get(Strings.Artist.PopularReleases);
        string eyebrow = hasLatest ? Loc.Get(Strings.Artist.LatestRelease) : Loc.Get(Strings.Artist.Popular);

        return Section(title, new BoxEl
        {
            Direction = 1, Gap = Spacing.S,
            Children =
            [
                ReleaseMasthead(mast, eyebrow, go, play, accent),
                strip.Count > 0 ? ReleaseStrip(strip, go) : new BoxEl(),
            ],
        });
    }

    static Element ReleaseMasthead(Album al, string eyebrow, Action<string, string?> go, Action<string> play,
                                   Func<ColorF> accent)
    {
        ColorF fill = accent();
        ColorF fg = ColorContrast.PickContrast(fill);
        string meta = ReleaseMeta(al);

        return new BoxEl
        {
            Direction = 0, Gap = Spacing.M, AlignItems = FlexAlign.Center,
            Padding = Edges4.All(Spacing.M), Corners = CornerRadius4.All(Radii.Card),
            Fill = Tok.FillCardDefault, BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault,
            HoverFill = Tok.FillSubtleSecondary,
            Role = AutomationRole.Button,
            OnClick = () => go("album:" + al.Uri, al.Name),
            Children =
            [
                new BoxEl
                {
                    Width = 112f, Height = 112f, Shrink = 0f, ClipToBounds = true,
                    Corners = CornerRadius4.All(Radii.Control),
                    Children =
                    [
                        Surfaces.Artwork(al.Cover, al.Id.GetHashCode() & 0x7fffffff, 112f, 112f, Radii.Control, decodePx: 224),
                    ],
                },
                new BoxEl
                {
                    Direction = 1, Grow = 1f, Basis = 0f, MinWidth = 0f, Gap = 4f,
                    Children =
                    [
                        new TextEl(eyebrow)
                        {
                            Size = 10f, Weight = 700, Color = Tok.TextTertiary, CharSpacing = 20f, MaxLines = 1,
                        },
                        new TextEl(al.Name)
                        {
                            Size = 15f, Weight = 700, Color = Tok.TextPrimary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
                            MinWidth = 0f,
                        },
                        meta.Length > 0
                            ? new TextEl(meta) { Size = 12f, Color = Tok.TextSecondary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis }
                            : new BoxEl(),
                        new BoxEl
                        {
                            Direction = 0, Gap = Spacing.S, Margin = new Edges4(0f, 6f, 0f, 0f),
                            Children =
                            [
                                new BoxEl
                                {
                                    Padding = new Edges4(12f, 5f, 12f, 5f), Corners = CornerRadius4.All(4f),
                                    Fill = fill, Cursor = CursorId.Hand, Role = AutomationRole.Button,
                                    OnClick = () => play(al.Uri),
                                    Children =
                                    [
                                        new TextEl(Loc.Get(Strings.Artist.Play))
                                        { Size = 12f, Weight = 600, Color = fg },
                                    ],
                                },
                                new BoxEl
                                {
                                    Padding = new Edges4(12f, 5f, 12f, 5f), Corners = CornerRadius4.All(4f),
                                    BorderWidth = 1f, BorderColor = Tok.StrokeControlDefault,
                                    HoverFill = Tok.FillSubtleSecondary,
                                    Cursor = CursorId.Hand, Role = AutomationRole.Button,
                                    OnClick = () => go("album:" + al.Uri, al.Name),
                                    Children =
                                    [
                                        new TextEl(Loc.Get(Strings.Detail.GoToPlaylist))
                                        { Size = 12f, Weight = 600, Color = Tok.TextPrimary },
                                    ],
                                },
                            ],
                        }.Skeletonized(false),
                    ],
                },
            ],
        };
    }

    static Element ReleaseStrip(IReadOnlyList<Album> albums, Action<string, string?> go)
    {
        var chips = new Element[albums.Count];
        for (int i = 0; i < albums.Count; i++)
        {
            var al = albums[i];
            string sub = (al.Year > 0 ? al.Year + " · " : "") + KindLabel(al.Kind);
            chips[i] = new BoxEl
            {
                Direction = 1, Grow = 1f, Basis = 0f, MinWidth = 0f, Gap = 8f,
                Padding = Edges4.All(Spacing.S), Corners = CornerRadius4.All(Radii.Card),
                BorderWidth = 1f, BorderColor = ColorF.Transparent,
                HoverFill = Tok.FillSubtleSecondary, HoverBorderColor = Tok.StrokeCardDefault,
                Role = AutomationRole.Button,
                OnClick = () => go("album:" + al.Uri, al.Name),
                Children =
                [
                    new BoxEl
                    {
                        // FIXED square cover (MediaCard.Shelf density) — a fluid AspectRatio box here measures against
                        // the pre-flex offered width and inflates the whole strip's row height (the giant-gap bug).
                        Width = 96f, Height = 96f, Shrink = 0f,
                        Corners = CornerRadius4.All(Radii.Control), ClipToBounds = true,
                        Children =
                        [
                            Surfaces.Artwork(al.Cover, al.Id.GetHashCode() & 0x7fffffff, 96f, 96f, Radii.Control, decodePx: 192),
                        ],
                    },
                    new TextEl(al.Name)
                    {
                        Size = 13f, Weight = 600, Color = Tok.TextPrimary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
                        MinWidth = 0f,
                    },
                    new TextEl(sub)
                    {
                        Size = 12f, Color = Tok.TextSecondary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
                    },
                ],
            };
        }
        return new BoxEl { Direction = 0, Gap = 4f, Children = chips };
    }

    static string ReleaseMeta(Album al)
    {
        var parts = new List<string>(3);
        parts.Add(KindLabel(al.Kind));
        if (al.Year > 0) parts.Add(al.Year.ToString());
        else if (al.ReleaseDate is { Length: >= 4 } rd) parts.Add(rd[..4]);
        if (al.TrackCount > 0)
            parts.Add(al.TrackCount == 1 ? "1 track" : al.TrackCount + " tracks");
        return string.Join(" · ", parts);
    }

    internal static string KindLabel(AlbumKind k) => k switch
    {
        AlbumKind.Single => Loc.Get(Strings.Detail.Badge.Single),
        AlbumKind.EP => Loc.Get(Strings.Detail.Badge.Ep),
        AlbumKind.Compilation => Loc.Get(Strings.Detail.Badge.Compilation),
        _ => Loc.Get(Strings.Detail.Badge.Album),
    };
}
