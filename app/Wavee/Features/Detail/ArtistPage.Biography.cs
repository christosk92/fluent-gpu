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

// The biography + profile-facts + "listened most in" two-column band.
sealed partial class ArtistPage : Component
{
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
}
