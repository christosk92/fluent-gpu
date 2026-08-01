using System;
using System.Collections.Generic;
using System.Globalization;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using Wavee.Core;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

// The hero-adjacent two-column Top tracks ledger plus a narrow rail for artist-authored/time-sensitive objects.
sealed partial class ArtistPage : Component
{
    const float TopBandWideW = 760f;
    const float TopBandHysteresis = 24f;   // == DetailLayoutBreakpoints.TierHysteresisDip, by intent
    // Latched across renders so a slow drag across ~760 does not flip the supporting rail on every 0.5px
    // ResponsiveBox rebuild. A plain FIELD, not a signal: it is derived purely from `w`
    // (idempotent for a given width) and must not schedule a render, so writing it from the build lambda is not a
    // backwards write. One ArtistPage instance is alive at a time and it is reused across artist→artist hops — which
    // is correct, because the window width does not change on navigation.
    bool _topBandWide = true;
    bool TopBandWide(float w)
        => _topBandWide = _topBandWide ? w >= TopBandWideW - TopBandHysteresis : w >= TopBandWideW;

    // Top tracks retain the native two-column PagedShelf. The supporting rail now carries artist-authored and
    // time-sensitive objects only: Artist Pick, upcoming, and latest release.
    Element TopBand(IReadOnlyList<Track> popular, string uri, PlaybackBridge? bridge, Services svc,
                    PinnedItem? pinned, Image? artistImage, Image? artistBackground, string artistName,
                    Album? latest, ArtistPreRelease? upcoming,
                    Action<string, string?> go, Action<string> play, Func<ColorF> accent) =>
        Responsive.Of(w =>
        {
            bool wide = TopBandWide(w);
            string popTitle = Loc.Get(Strings.Artist.TopTracks);
            Element tracks = Embed.Comp(() => new ArtistPopular(popular, uri, bridge, svc, popTitle, accent))
                with { SkeletonProxy = () => ArtistPopular.SkeletonShape(popular, popTitle) };
            Element featured = FeaturedColumn(pinned, artistImage, artistBackground, artistName, latest, upcoming, go, play, accent);
            bool hasFeatured = pinned is not null || latest is { Name.Length: > 0, Uri.Length: > 0 }
                               || upcoming is { IsUpcoming: true };

            if (!hasFeatured)
                return new BoxEl { Direction = 1, Children = [tracks] };

            return new BoxEl
            {
                Direction = (byte)(wide ? 0 : 1), Gap = Spacing.XL,
                AlignItems = wide ? FlexAlign.Start : FlexAlign.Stretch,
                Children =
                [
                    new BoxEl
                    {
                        Direction = 1, Grow = wide ? 2f : 0f, Basis = wide ? 0f : float.NaN,
                        MinWidth = 0f, Children = [tracks],
                    },
                    new BoxEl
                    {
                        Direction = 1, Grow = wide ? 1f : 0f, Basis = wide ? 0f : float.NaN,
                        MinWidth = 0f, Children = [featured],
                    },
                ],
            };
        }, fallback: 900f);

    Element FeaturedColumn(PinnedItem? pinned, Image? artistImage, Image? artistBackground, string artistName, Album? latest,
                           ArtistPreRelease? upcoming, Action<string, string?> go,
                           Action<string> play, Func<ColorF> accent)
    {
        var groups = new List<Element>(3);
        if (pinned is { } pick)
        {
            string target = RichText.RouteForUri(pick.TargetUri) ?? ("album:" + pick.TargetUri);
            groups.Add(Section(Loc.Get(Strings.Artist.ArtistPick),
                MediaCard.ArtistPick(pick, artistName, artistImage, artistBackground,
                    onClick: () => go(target, pick.Title),
                    onPlay: () => play(pick.TargetUri),
                    // A pinned item can point at any entity, so the kind comes from the uri — the same discrimination
                    // `target` above used, so the drag payload can never disagree with the click's destination.
                    drag: CardDrag(WaveeDragKindMap.OfUri(pick.TargetUri), pick.TargetUri, pick.Title, pick.Cover)))
                with { Key = "featured:pick" });
        }
        if (upcoming is { IsUpcoming: true } next)
            groups.Add(Section(Loc.Get(Strings.Artist.Upcoming), UpcomingMasthead(next, go, accent))
                with { Key = "featured:upcoming" });
        if (latest is { Name.Length: > 0, Uri.Length: > 0 } release)
            groups.Add(Section(Loc.Get(Strings.Artist.LatestRelease),
                ReleaseMasthead(release, Loc.Get(Strings.Artist.LatestRelease), go, play))
                with { Key = "featured:latest" });

        return new BoxEl { Direction = 1, Gap = Spacing.XL, Children = groups.ToArray() };
    }

    // The artist's announced-but-unreleased record in the supporting rail. Built on ReleaseMasthead's
    // geometry grammar verbatim (96px cover, 10px/700 eyebrow, 15px/700 name, 12px meta, the same paddings, corners,
    // card fill and hover) so the two read as one stack rather than two designs sharing a column.
    static Element UpcomingMasthead(ArtistPreRelease p, Action<string, string?> go, Func<ColorF> accent)
    {
        // Either scheme can land here: preReleaseV2 hands back an ALBUM uri on every capture so far, but a
        // spotify:prerelease: one is equally valid (the two ids DIFFER — neither can be synthesised from the other).
        // RouteForUri routes both; the literal fallback keeps a uri it cannot classify on the album route rather than
        // on the generic "Coming soon" stub, which is where a bare spotify: uri lands.
        string route = RichText.RouteForUri(p.Uri) ?? ("album:" + p.Uri);
        // "Upcoming" keeps the sibling eyebrow's sentence case ("Latest release"); the release TYPE is upper-cased
        // because that is how every other type token in this column reads (KindLabel → "ALBUM", the strip chips'
        // "2026 · SINGLE"). Absent type → the bare word, never a dangling separator.
        string eyebrow = p.Type is { Length: > 0 } type
            ? Loc.Get(Strings.Artist.Upcoming) + " · " + type.ToUpperInvariant()
            : Loc.Get(Strings.Artist.Upcoming);
        // Announced-but-undated is a real state on the wire — then the card announces without promising a day.
        string meta = p.ReleaseAt is { } dated ? Strings.Detail.ReleasesOn(DetailFormat.ShortDate(dated)) : "";

        return new BoxEl
        {
            Direction = 0, Gap = 10f, AlignItems = FlexAlign.Center,
            Padding = Edges4.All(10f), Corners = CornerRadius4.All(Radii.Card),
            // A FULL card, on the bare page surface: this is a discrete promoted object (one announced record) — exactly
            // what a Fluent card is FOR — and the announcement is this page's one piece of news, so it earns the chrome.
            // The stroke is safe because nothing encloses it any more; there is no second hairline to double up with.
            Fill = Tok.FillCardDefault, BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault,
            Children =
            [
                new BoxEl
                {
                    Width = 96f, Height = 96f, Shrink = 0f, ClipToBounds = true,
                    Corners = CornerRadius4.All(Radii.Control),
                    Children =
                    [
                        Surfaces.Artwork(p.Cover, p.Uri.GetHashCode() & 0x7fffffff, 96f, 96f, Radii.Control, decodePx: 192),
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
                        new TextEl(p.Name)
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
                                // NO Play button, and there must never be one: a prerelease uri must never reach
                                // PlayAsync — there is nothing to stream yet, and the sibling masthead's Play slot is
                                // taken here by the only action the record can honour before it drops.
                                //
                                // PROPS FREEZE AT MOUNT, so the embed is keyed on the uri: this column is rebuilt
                                // whenever extras arrive, and an unkeyed PreSaveButton would keep resolving (and
                                // pre-saving) whichever uri happened to be current at mount.
                                Embed.Comp(() => new PreSaveButton { Uri = p.Uri, Name = p.Name, Accent = accent })
                                    with { Key = "presave:" + p.Uri },
                                Button.Create(Loc.Get(Strings.Artist.View), () => go(route, p.Name),
                                    ButtonAppearance.Outline, ControlSize.Small),
                            ],
                        }.Skeletonized(false),
                        // The LIVE clock (UseInterval, auto-paused while the page is parked or the window minimized).
                        // This is the one countdown on the artist page that keeps running: unlike the hero card it does
                        // not collapse away on scroll. Keyed on uri + instant because ReleaseAt freezes at mount.
                        p.ReleaseAt is { } due
                            ? Embed.Comp(() => new PreReleaseCountdown { ReleaseAt = due, Accent = accent })
                                with { Key = "artist-upcoming:" + p.Uri + ":" + due.UtcTicks.ToString(CultureInfo.InvariantCulture) }
                            : new BoxEl(),
                    ],
                },
            ],
        };
    }

    // Instance (was static) so the latest-release card can reach the page's `_acts` for its drag payload — the same
    // reason CardMenu is an instance method.
    Element ReleaseMasthead(Album al, string eyebrow, Action<string, string?> go, Action<string> play)
    {
        string meta = ReleaseMeta(al);
        string subtitle = meta.Length == 0 ? eyebrow : eyebrow + " · " + meta;
        return MediaCard.Compact(al.Cover, al.Name, subtitle, al.Uri, HomeCardKind.Album,
            onClick: () => go("album:" + al.Uri, al.Name),
            onPlay: () => play(al.Uri), art: 96f, cardH: 116f,
            menu: null,
            drag: CardDrag(WaveeResourceKind.Album, al.Uri, al.Name, al.Cover));
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
