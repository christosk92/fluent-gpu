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

// The hero-adjacent "Top tracks + Releases" band.
sealed partial class ArtistPage : Component
{
    const float TopBandWideW = 760f;
    const float TopBandHysteresis = 24f;   // == DetailLayoutBreakpoints.TierHysteresisDip, by intent
    // Latched across renders so a slow drag across ~760 does not flip the Releases column between the chip strip and
    // the row list on every 0.5px ResponsiveBox rebuild. A plain FIELD, not a signal: it is derived purely from `w`
    // (idempotent for a given width) and must not schedule a render, so writing it from the build lambda is not a
    // backwards write. One ArtistPage instance is alive at a time and it is reused across artist→artist hops — which
    // is correct, because the window width does not change on navigation.
    bool _topBandWide = true;
    bool TopBandWide(float w)
        => _topBandWide = _topBandWide ? w >= TopBandWideW - TopBandHysteresis : w >= TopBandWideW;

    // Top tracks (left, wider) + Releases masthead+strip (right) — stacked on a narrow page.
    Element TopBand(IReadOnlyList<Track> popular, string uri, PlaybackBridge? bridge, Services svc,
                    Album? latest, IReadOnlyList<Album>? popularReleases, ArtistPreRelease? upcoming,
                    Action<string, string?> go, Action<string> play, Func<ColorF> accent) =>
        Responsive.Of(w =>
        {
            bool wide = TopBandWide(w);
            // The chips size to the BARE responsive column — no enclosing card padding to spend. Any inset subtracted
            // here that layout does not actually impose makes the strip narrower than its slot; any inset missed makes
            // it overflow. Keep this equal to the width the strip's parent really offers.
            float columnW = wide ? MathF.Max(0f, (w - Spacing.XL) / 3f) : w;
            float releaseW = columnW;
            string popTitle = Loc.Get(Strings.Artist.TopTracks);
            // NO section container: cards are for OBJECTS (a release, a setting), never for SECTIONS. Section structure
            // comes from typography + spacing alone, so the chart sits directly on the page and its own AccentHeader +
            // pager (built by ArtistPopular) are its header row. The shimmer — SkeletonDeriver strips fills/borders —
            // is the reference for how this must look; live and shimmer now agree.
            Element left = Embed.Comp(() => new ArtistPopular(popular, uri, bridge, svc, popTitle, accent))
                with { SkeletonProxy = () => ArtistPopular.SkeletonShape(popular, popTitle) };
            // ReleasesColumn can return an EMPTY element ("earns no place"), so nothing here may wrap it in chrome or
            // padding that would still paint for a column that isn't there.
            // `stacked` is threaded EXPLICITLY, never inferred from the width inside the column: the strip's own
            // narrow rule (the <370px two-chip case) is a WIDE-mode rule about a third-width column, and a stacked
            // column is full page width — so a width test down there cannot tell the two situations apart.
            Element right = ReleasesColumn(latest, popularReleases, upcoming, go, play, accent, releaseW, stacked: !wide);
            return new BoxEl
            {
                Direction = (byte)(wide ? 0 : 1), Gap = Spacing.XL,
                // WIDE (row): each column keeps its NATURAL height — no cross-stretch. The chart is exactly as tall as
                // its rows and the releases column is usually taller, so the band's bottom is ragged. Stretching the
                // chart to close that gap is what produced first chunky rows and then huge inter-row spacing.
                // The strip sizes its covers from this responsive width, so nothing fluid inflates the band later.
                //
                // STACKED (column): the cross axis is WIDTH, so the same Start COLLAPSES both children to their intrinsic
                // width — and the chart shelf self-measures from the width it is handed, so it measured its own content
                // instead of the page and fitted a single narrow column forever. Stretch is the correct cross-align once
                // the direction flips; the height argument above is about the ROW case only.
                AlignItems = wide ? FlexAlign.Start : FlexAlign.Stretch,
                Children =
                [
                    new BoxEl
                    {
                        Direction = 1, Grow = wide ? 2f : 0f, Basis = wide ? 0f : float.NaN,
                        MinWidth = 0f, Children = [left],
                    },
                    new BoxEl
                    {
                        Direction = 1, Grow = wide ? 1f : 0f, Basis = wide ? 0f : float.NaN,
                        MinWidth = 0f, Children = [right],
                    },
                ],
            };
        }, fallback: 900f);

    const int StripCap = 3;   // wide: equal-width square chips across a third-width column
    const int ListCap = 5;    // stacked: vertical rows at full page width

    Element ReleasesColumn(Album? latest, IReadOnlyList<Album>? popular, ArtistPreRelease? upcoming,
                           Action<string, string?> go, Action<string> play,
                           Func<ColorF> accent, float availableWidth, bool stacked)
    {
        var popularList = popular ?? Array.Empty<Album>();
        bool hasLatest = latest is { Name.Length: > 0, Uri.Length: > 0 };
        // Wall-clock, never "the field is non-null": a stored announcement outlives its own release, so a cached
        // overview would keep an Upcoming masthead up for a record that shipped last week.
        bool hasUpcoming = upcoming is { IsUpcoming: true };
        Album? mast = hasLatest ? latest : popularList.Count > 0 ? popularList[0] : null;
        // A debut artist can have an announcement and no catalogue at all — the column still earns its place then.
        if (mast is null && !hasUpcoming) return new BoxEl();

        string mastUri = mast?.Uri ?? "";
        // The cap belongs to the VARIANT, not to the column: three chips is what a third-width row of squares holds,
        // while a full-width row list has the vertical room (and, stacked, the page's whole width) for five.
        int cap = stacked ? ListCap : StripCap;
        var strip = new List<Album>(cap);
        for (int i = 0; i < popularList.Count && strip.Count < cap; i++)
        {
            var al = popularList[i];
            if (al.Uri.Length > 0 && string.Equals(al.Uri, mastUri, StringComparison.OrdinalIgnoreCase)) continue;
            strip.Add(al);
        }

        string title = hasLatest || mast is null ? Loc.Get(Strings.Artist.Releases) : Loc.Get(Strings.Artist.PopularReleases);
        string eyebrow = hasLatest ? Loc.Get(Strings.Artist.LatestRelease) : Loc.Get(Strings.Artist.Popular);

        // STABLE KEYS on every child. The upcoming masthead is a CONDITIONAL first sibling that appears and disappears
        // mid-session (the announcement arrives with extras after the first Ready render, and IsUpcoming flips to false
        // the instant the record drops). Keyless children pair by raw index + ElementTypeId, and all three of these are
        // BoxEl — one identical type — so inserting or dropping the first one would re-pair the release masthead
        // against the upcoming card's subtree: the exact silent cross-wiring documented at ArtistPage.cs (the sections
        // list). Keyed children pair by key regardless of position.
        var children = new List<Element>(3);
        // The announcement goes FIRST. It is the one thing in this column that expires — it is the artist's news — and
        // burying it under a record that is already out inverts the reason a visitor is on this page today.
        if (hasUpcoming) children.Add(UpcomingMasthead(upcoming!, go, accent) with { Key = "rel:upcoming" });
        if (mast is not null) children.Add(ReleaseMasthead(mast, eyebrow, go, play, accent) with { Key = "rel:mast" });
        // ONE key across both variants. The keying rationale above is about the CONDITIONAL first sibling (the upcoming
        // masthead) re-pairing its BoxEl neighbours by index; that hazard is unchanged by the variant swap, and a
        // per-variant key would remount the whole releases block on every 760px crossing.
        if (strip.Count > 0)
            children.Add((stacked ? BuildReleaseList(strip, go) : BuildReleaseStrip(strip, go, availableWidth))
                with { Key = "rel:strip" });

        // A SECTION, not a card: cards are for OBJECTS (the mastheads and chips below), and a section is held together
        // by its heading plus this gap — nothing else. The shimmer already renders exactly this (SkeletonDeriver strips
        // fills/borders), and it is the reference. Do not re-introduce a container here.
        return Section(title, new BoxEl
        {
            Direction = 1, Gap = Spacing.S,
            Children = children.ToArray(),
        });
    }

    // The artist's announced-but-unreleased record, at the head of the Releases column. Built on ReleaseMasthead's
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
            HoverFill = Tok.FillSubtleSecondary,
            Role = AutomationRole.Button,
            OnClick = () => go(route, p.Name),
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
                                new BoxEl
                                {
                                    Padding = new Edges4(12f, 5f, 12f, 5f), Corners = CornerRadius4.All(4f),
                                    BorderWidth = 1f, BorderColor = Tok.StrokeControlDefault,
                                    HoverFill = Tok.FillSubtleSecondary,
                                    Cursor = CursorId.Hand, Role = AutomationRole.Button,
                                    OnClick = () => go(route, p.Name),
                                    Children =
                                    [
                                        new TextEl(Loc.Get(Strings.Artist.View))
                                        { Size = 12f, Weight = 600, Color = Tok.TextPrimary },
                                    ],
                                },
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

    static Element ReleaseMasthead(Album al, string eyebrow, Action<string, string?> go, Action<string> play,
                                   Func<ColorF> accent)
    {
        ColorF fill = accent();
        ColorF fg = ColorContrast.PickContrast(fill);
        string meta = ReleaseMeta(al);

        return new BoxEl
        {
            // Prototype .mast: 96px cover, 10px padding/gap.
            Direction = 0, Gap = 10f, AlignItems = FlexAlign.Center,
            Padding = Edges4.All(10f), Corners = CornerRadius4.All(Radii.Card),
            // A full card — see UpcomingMasthead: one discrete release is an OBJECT, and objects are what cards are for.
            Fill = Tok.FillCardDefault, BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault,
            HoverFill = Tok.FillSubtleSecondary,
            Role = AutomationRole.Button,
            OnClick = () => go("album:" + al.Uri, al.Name),
            Children =
            [
                new BoxEl
                {
                    Width = 96f, Height = 96f, Shrink = 0f, ClipToBounds = true,
                    Corners = CornerRadius4.All(Radii.Control),
                    Children =
                    [
                        Surfaces.Artwork(al.Cover, al.Id.GetHashCode() & 0x7fffffff, 96f, 96f, Radii.Control, decodePx: 192),
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

    // The popular-releases strip (prototype .strip/.chip): equal-width chips whose square covers fill the chip
    // edge-to-edge. Resolve explicit sizes from the enclosing responsive slot in this same render, so the strip's
    // final height participates in parent layout before the Albums sibling is positioned.
    static Element BuildReleaseStrip(IReadOnlyList<Album> albums, Action<string, string?> go, float availableWidth)
    {
        const float Gap = 2f;      // prototype .strip gap
        const float ChipPad = 6f;  // prototype .chip padding
        // Prototype strip-2 rule: under ~370px, two roomy chips beat three cramped ones.
        int n = Math.Min(albums.Count, availableWidth > 0.5f && availableWidth < 370f ? 2 : 3);
        if (n <= 0) return new BoxEl();
        float chipW = availableWidth > 0.5f ? (availableWidth - (n - 1) * Gap) / n : 0f;
        float cover = chipW > 0f ? MathF.Max(48f, MathF.Floor(chipW - 2f * ChipPad)) : 96f;

        var chips = new Element[n];
        for (int i = 0; i < n; i++)
        {
            var al = albums[i];
            string sub = (al.Year > 0 ? al.Year + " · " : "") + KindLabel(al.Kind);
            chips[i] = new BoxEl
            {
                Direction = 1, Grow = 1f, Basis = 0f, MinWidth = 0f, Gap = 6f,
                Padding = Edges4.All(ChipPad), Corners = CornerRadius4.All(Radii.Card),
                BorderWidth = 1f, BorderColor = ColorF.Transparent,
                HoverFill = Tok.FillSubtleSecondary, HoverBorderColor = Tok.StrokeCardDefault,
                Role = AutomationRole.Button, Cursor = CursorId.Hand,
                OnClick = () => go("album:" + al.Uri, al.Name),
                Children =
                [
                    new BoxEl
                    {
                        Width = cover, Height = cover, Shrink = 0f,
                        Corners = CornerRadius4.All(Radii.Control), ClipToBounds = true,
                        Children =
                        [
                            Surfaces.Artwork(al.Cover, al.Id.GetHashCode() & 0x7fffffff, cover, cover, Radii.Control, decodePx: 256),
                        ],
                    },
                    new TextEl(al.Name)
                    {
                        Size = 12f, Weight = 600, Color = Tok.TextPrimary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
                        MinWidth = 0f,
                    },
                    new TextEl(sub)
                    {
                        Size = 11f, Color = Tok.TextSecondary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
                    },
                ],
            };
        }
        return new BoxEl { Direction = 0, Gap = Gap, AlignItems = FlexAlign.Start, Children = chips };
    }

    // The STACKED variant of the popular-releases strip. Below ~760 the releases column stops being a third-width
    // sidebar and becomes a full-page-width block, where three equal-width chips inflate into three huge squares —
    // covers hundreds of DIP across, and a band taller than the top-tracks chart above it. A vertical row list is the
    // right shape for that slot: it is bounded by ROW COUNT rather than by width, so widening the page cannot inflate it.
    //
    // Geometry is the mastheads' grammar deliberately (the same 10px gap, Radii.Card corners and hover-only plate) at a
    // smaller 52px cover, so the list reads as a continuation of the masthead card stack directly above it rather than
    // as a third design sharing the column.
    static Element BuildReleaseList(IReadOnlyList<Album> albums, Action<string, string?> go)
    {
        if (albums.Count == 0) return new BoxEl();
        var rows = new Element[albums.Count];
        for (int i = 0; i < albums.Count; i++)
        {
            var al = albums[i];
            string sub = (al.Year > 0 ? al.Year + " · " : "") + KindLabel(al.Kind);
            rows[i] = new BoxEl
            {
                Direction = 0, Gap = 10f, AlignItems = FlexAlign.Center,
                Padding = new Edges4(10f, 6f, 10f, 6f), Corners = CornerRadius4.All(Radii.Card),
                // Transparent stroke reserved so the hover border does not re-layout the row (same trick as the chips).
                BorderWidth = 1f, BorderColor = ColorF.Transparent,
                HoverFill = Tok.FillSubtleSecondary, HoverBorderColor = Tok.StrokeCardDefault,
                Role = AutomationRole.Button, Cursor = CursorId.Hand,
                OnClick = () => go("album:" + al.Uri, al.Name),
                Children =
                [
                    new BoxEl
                    {
                        Width = 52f, Height = 52f, Shrink = 0f,
                        Corners = CornerRadius4.All(Radii.Control), ClipToBounds = true,
                        Children =
                        [
                            // 128px decode, not the chips' 256: this cover is 52 DIP even at 2x, so the larger target
                            // would be a second, bigger decode of the same art for no visible gain.
                            Surfaces.Artwork(al.Cover, al.Id.GetHashCode() & 0x7fffffff, 52f, 52f, Radii.Control, decodePx: 128),
                        ],
                    },
                    new BoxEl
                    {
                        Direction = 1, Grow = 1f, Basis = 0f, MinWidth = 0f, Gap = 2f,
                        Children =
                        [
                            new TextEl(al.Name)
                            {
                                Size = 13f, Weight = 600, Color = Tok.TextPrimary, MaxLines = 1,
                                Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
                            },
                            new TextEl(sub)
                            {
                                Size = 11f, Color = Tok.TextSecondary, MaxLines = 1,
                                Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
                            },
                        ],
                    },
                ],
            };
        }
        return new BoxEl { Direction = 1, Gap = 2f, Children = rows };
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
