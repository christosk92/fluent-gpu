using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using Wavee.Core;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

// Album / single trailing sections, appended below the eager track rows in the outer scroller.
// The enrichment loads behind one route-keyed async boundary so the trailing area shows one coherent skeleton and then
// fades in as a whole. Individual enrichment calls still fail soft inside the aggregate and simply omit their section.
sealed class AlbumTrailing : Component
{
    readonly Loadable<DetailModel> _full;   // read reactively → loaders re-fire when the full album lands / the route swaps
    readonly Signal<Route> _route;
    readonly DetailHandlers _h;
    public AlbumTrailing(Loadable<DetailModel> full, Signal<Route> route, DetailHandlers h)
    { _full = full; _route = route; _h = h; }

    sealed record AlbumTrailingData(
        Artist? About,
        IReadOnlyList<Artist> Fans,
        IReadOnlyList<PlaylistSummary> Featured,
        IReadOnlyList<MerchItem> Merch,
        IReadOnlyList<Album> Similar)
    {
        public static readonly AlbumTrailingData Empty = new(
            null,
            Array.Empty<Artist>(),
            Array.Empty<PlaylistSummary>(),
            Array.Empty<MerchItem>(),
            Array.Empty<Album>());
    }

    public override Element Render()
    {
        var svc = UseContext(Services.Slot);
        var m = _full.Value.Value;                 // subscribe → re-render preview→full (the loaders re-key on `ready`)
        var tracks = m.Tracks;
        bool ready = tracks.Count > 0;             // the album fetch completed (hero + tracks are in) → seed the loaders
        string albumUri = m.ContextUri ?? "";
        string leadArtistUri = m.Artists.Count > 0 ? m.Artists[0].Uri : "";
        string leadTrackUri = ready ? tracks[0].Uri : "";
        string seedTrackUri = SeedTrack(tracks);   // highest play-count track (fallback: track 0) — Spotify's similar seed
        bool shortRelease = m.ReleaseKind == AlbumKind.Single || tracks.Count is > 0 and <= 2;
        // Re-fire EVERY loader when the full model lands (`:p`→`:r`) or the route swaps on a reused instance. The engine
        // cancels the in-flight run and reseeds to empty on a key change (route-keyed cancellation / no stale flash).
        object key = _route.Value.Name + (ready ? ":r" : ":p");

        var trailing = UseAsyncResource(
            ct => LoadTrailingAsync(svc, ready, shortRelease, leadArtistUri, leadTrackUri, albumUri, seedTrackUri, ct),
            AlbumTrailingData.Empty, key);

        if (!ready) return new BoxEl { Direction = 1, Grow = 1f, AlignSelf = FlexAlign.Stretch };

        return new BoxEl
        {
            Direction = 1,
            Grow = 1f,
            AlignSelf = FlexAlign.Stretch,
            Children =
            [
                Skel.Region(
                    trailing,
                    TrailingSkeleton,
                    data => TrailingSections(m, data, shortRelease, _h),
                    reveal: SkelReveal.FadeOnly,
                    onFailed: () => new BoxEl(),
                    isEmpty: data => !HasTrailingSections(m, data, shortRelease),
                    onEmpty: () => new BoxEl(),
                    group: key,
                    smoothResize: false),
            ],
        };
    }

    static Element TrailingSections(DetailModel m, AlbumTrailingData data, bool shortRelease, DetailHandlers h)
    {
        var sections = new List<Element>(8);

        if (shortRelease && m.HasVideo)
            sections.Add(WatchVideoSection(m, h));

        if (data.About is { } about)
            sections.Add(AboutArtistSection(about, h));

        if (data.Fans.Count > 0)
            sections.Add(Section(Loc.Get(Strings.Detail.FansAlsoLike), FansRow(data.Fans, h)));

        var moreBy = m.MoreByArtist is { Count: > 0 } mb ? mb
            : data.About?.TopAlbums is { Count: > 0 } ta ? ta : null;
        if (moreBy is { Count: > 0 } && m.Artists.Count > 0)
            sections.Add(AlbumShelf(Strings.Detail.MoreBy(m.Artists[0].Name), moreBy, h));

        if (data.Featured.Count > 0)
            sections.Add(FeaturedSection(data.Featured, h));

        if (data.Merch.Count > 0)
            sections.Add(MerchSection(data.Merch));

        if (data.Similar.Count > 0)
            sections.Add(AlbumShelf(Loc.Get(Strings.Detail.SimilarAlbums), data.Similar, h));

        return new BoxEl
        {
            Direction = 1,
            Grow = 1f,
            AlignSelf = FlexAlign.Stretch,
            Children = sections.ToArray(),
        };
    }

    static bool HasTrailingSections(DetailModel m, AlbumTrailingData data, bool shortRelease)
    {
        if (shortRelease && m.HasVideo) return true;
        if (data.About is not null || data.Fans.Count > 0 || data.Featured.Count > 0 || data.Merch.Count > 0 || data.Similar.Count > 0)
            return true;
        if (m.MoreByArtist is { Count: > 0 } && m.Artists.Count > 0) return true;
        return data.About?.TopAlbums is { Count: > 0 } && m.Artists.Count > 0;
    }

    static async Task<AlbumTrailingData> LoadTrailingAsync(Services? svc, bool ready, bool shortRelease,
        string leadArtistUri, string leadTrackUri, string albumUri, string seedTrackUri, System.Threading.CancellationToken ct)
    {
        if (svc is null || !ready) return AlbumTrailingData.Empty;

        var about = leadArtistUri.Length > 0 && leadTrackUri.Length > 0
            ? Safe(ct2 => svc.AlbumEnrichment.GetAboutArtistAsync(leadArtistUri, leadTrackUri, ct2), (Artist?)null, ct)
            : Task.FromResult<Artist?>(null);
        var fans = Safe(ct2 => FansAsync(svc, ready, shortRelease, leadArtistUri, seedTrackUri, ct2),
            (IReadOnlyList<Artist>)Array.Empty<Artist>(), ct);
        var featured = albumUri.Length > 0
            ? Safe(ct2 => svc.AlbumEnrichment.GetRecommendedPlaylistsAsync(albumUri, ct2),
                (IReadOnlyList<PlaylistSummary>)Array.Empty<PlaylistSummary>(), ct)
            : Task.FromResult<IReadOnlyList<PlaylistSummary>>(Array.Empty<PlaylistSummary>());
        var merch = albumUri.Length > 0
            ? Safe(ct2 => svc.AlbumEnrichment.GetMerchAsync(albumUri, ct2),
                (IReadOnlyList<MerchItem>)Array.Empty<MerchItem>(), ct)
            : Task.FromResult<IReadOnlyList<MerchItem>>(Array.Empty<MerchItem>());
        var similar = seedTrackUri.Length > 0
            ? Safe(ct2 => svc.AlbumEnrichment.GetSimilarAlbumsAsync(seedTrackUri, 24, ct2),
                (IReadOnlyList<Album>)Array.Empty<Album>(), ct)
            : Task.FromResult<IReadOnlyList<Album>>(Array.Empty<Album>());

        await Task.WhenAll(about, fans, featured, merch, similar).ConfigureAwait(false);
        return new AlbumTrailingData(await about.ConfigureAwait(false), await fans.ConfigureAwait(false),
            await featured.ConfigureAwait(false), await merch.ConfigureAwait(false), await similar.ConfigureAwait(false));
    }

    static async Task<T> Safe<T>(Func<System.Threading.CancellationToken, Task<T>> read, T fallback, System.Threading.CancellationToken ct)
    {
        try { return await read(ct).ConfigureAwait(false); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { return fallback; }
    }

    // "About this release" — the album facts as a COMPACT bento (Songs / Length / Released / Label tiles, the wrap-grow
    // grid from the artist Profile facts but denser), with the legal lines (Courtesy / Copyright) as a small footnote.
    // Always present for an album with tracks (the Songs/Length tiles), hence the Tracks check.
    internal static bool HasReleasePanel(DetailModel m) =>
        m.Tracks.Count > 0 || m.ReleaseDate is { Length: > 0 } || m.Label is { Length: > 0 } || m.Copyright is { Length: > 0 } ||
        m.CourtesyLine is { Length: > 0 } || m.OtherVersions is { Count: > 0 };

    internal static Element ReleasePanel(DetailModel m, DetailHandlers h, bool outerPadding = true)
    {
        var children = new List<Element>(4);
        if (m.OtherVersions is { Count: > 0 } ov) children.Add(OtherVersionsDropDown(ov, h));

        var tiles = AlbumFactTiles(m);
        var notes = ReleaseNotes(m);
        if (tiles.Length > 0 || notes.Length > 0)
        {
            var body = new List<Element>(3)
            {
                new TextEl("ABOUT THIS RELEASE") { Size = 11f, Weight = 700, Color = Tok.TextTertiary, CharSpacing = 60f },
            };
            // The bento: a wrap row of compact fact tiles. Wrap-grow (FlexLayout.ArrangeWrap) fills each line edge-to-edge,
            // so the tiles flow 2×2 in a narrow rail and a single row when wide — no ragged gaps.
            if (tiles.Length > 0) body.Add(new BoxEl { Direction = 0, Gap = WaveeSpace.S, Wrap = true, Children = tiles });
            if (notes.Length > 0) body.Add(new BoxEl { Direction = 1, Gap = 3f, Children = notes });
            children.Add(new BoxEl { Direction = 1, Gap = WaveeSpace.M, Children = body.ToArray() });
        }

        if (children.Count == 0) return new BoxEl();
        return new BoxEl
        {
            Direction = 1, Gap = WaveeSpace.M,
            Padding = outerPadding ? new Edges4(WaveeSpace.L, WaveeSpace.XL, WaveeSpace.L, WaveeSpace.L) : Edges4.All(0f),
            Children = children.ToArray(),
        };
    }

    // Songs / Length / Released / Label — each present-only (a missing fact drops its tile). Songs+Length come from the
    // track list; Released prefers the formatted date, else the year; Label is the record label.
    static Element[] AlbumFactTiles(DetailModel m)
    {
        var stats = new List<(string Value, string Label)>(4);
        if (m.Tracks.Count > 0)
        {
            stats.Add((m.Tracks.Count.ToString(), "Songs"));
            long ms = DetailFormat.TotalMs(m.Tracks);
            if (ms > 0) stats.Add((DetailFormat.TotalTime(ms), "Length"));
        }
        string? released = m.ReleaseDate is { Length: > 0 } rd ? rd : m.Year;
        if (released is { Length: > 0 }) stats.Add((released, "Released"));
        if (m.Label is { Length: > 0 } lb) stats.Add((lb, "Label"));
        var tiles = new Element[stats.Count];
        for (int i = 0; i < stats.Count; i++) tiles[i] = CompactStatTile(stats[i].Value, stats[i].Label);
        return tiles;
    }

    // The legal footnote (Courtesy + Copyright). Copyright joins several notices with '\n'; the text engine now renders a
    // hard break as a real line break (LineBreaker.IsHardBreak / TextLayoutEngine), so each notice flows onto its own line
    // with no [] tofu — the newline no longer needs splitting at the app layer.
    static Element[] ReleaseNotes(DetailModel m)
    {
        var notes = new List<Element>(2);
        if (m.CourtesyLine is { Length: > 0 } courtesy) notes.Add(NoteText(courtesy));
        if (m.Copyright is { Length: > 0 } cp) notes.Add(NoteText(cp));
        return notes.ToArray();
    }

    static Element NoteText(string value) => new TextEl(value)
        { Size = 11f, Color = Tok.TextTertiary, Wrap = TextWrap.Wrap, MaxLines = 4, Trim = TextTrim.CharacterEllipsis };

    // A compact fact tile (denser than the artist Profile-facts tile: 18px value, tighter padding). Content-width base +
    // Grow=1 so the wrap-grow row fills; a long Label keeps its own width (base = content) and never clips short tiles.
    static Element CompactStatTile(string value, string label) => new BoxEl
    {
        Direction = 1, Gap = 1f, Grow = 1f, Basis = 0f, MinWidth = 0f,
        Padding = new Edges4(WaveeSpace.M, WaveeSpace.S, WaveeSpace.M, WaveeSpace.S),
        Corners = CornerRadius4.All(WaveeRadius.Control), Fill = Tok.FillCardSecondary,
        BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault,
        Children =
        [
            new TextEl(value) { Size = 18f, Weight = 800, Color = Tok.TextPrimary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
            new TextEl(label) { Size = 11f, Color = Tok.TextSecondary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
        ],
    };

    static Element OtherVersionsDropDown(IReadOnlyList<Album> versions, DetailHandlers h)
    {
        var items = new MenuFlyoutItem[versions.Count];
        for (int i = 0; i < versions.Count; i++)
        {
            var v = versions[i];
            items[i] = new MenuFlyoutItem(VersionLabel(v), Icons.MusicNote,
                Invoke: () => h.OpenAlbum(v));
        }
        return new BoxEl
        {
            Direction = 0, Padding = new Edges4(0f, 2f, 0f, 2f),
            Children = [new BoxEl { Grow = 1f, Children = [DropDownButton.Create("Other versions", items, Icons.MusicNote)] }],
        };
    }

    static string VersionLabel(Album a)
    {
        var parts = new List<string>(3) { a.Name };
        if (a.Year > 0) parts.Add(a.Year.ToString());
        parts.Add(a.Kind switch
        {
            AlbumKind.Single => Loc.Get(Strings.Detail.Badge.Single),
            AlbumKind.EP => Loc.Get(Strings.Detail.Badge.Ep),
            AlbumKind.Compilation => Loc.Get(Strings.Detail.Badge.Compilation),
            _ => Loc.Get(Strings.Detail.Badge.Album),
        });
        return string.Join(" · ", parts);
    }

    // Multi-line facts (Copyright joins several legal notices with '\n'; Courtesy can carry raw '\r\n' from Spotify) render
    // LINE-BY-LINE as separate single-line runs: a raw newline reaching the shaper paints as a [] tofu glyph, so we split
    // on it here rather than feed it through. Single-line values are unchanged (just trimmed).
    static readonly char[] NewlineChars = { '\r', '\n' };
    static Element AboutRow(string label, string value)
    {
        var lines = value.Split(NewlineChars, StringSplitOptions.RemoveEmptyEntries);
        Element valueCol = lines.Length <= 1
            ? new TextEl(lines.Length == 1 ? lines[0].Trim() : value)
                { Size = 13f, Color = Tok.TextPrimary, Grow = 1f, Basis = 0f, Wrap = TextWrap.Wrap, MaxLines = 4, Trim = TextTrim.CharacterEllipsis }
            : new BoxEl
            {
                Direction = 1, Grow = 1f, Basis = 0f, Gap = 2f,
                Children = lines.Select(l => (Element)new TextEl(l.Trim())
                    { Size = 13f, Color = Tok.TextPrimary, Wrap = TextWrap.Wrap, MaxLines = 2, Trim = TextTrim.CharacterEllipsis }).ToArray(),
            };
        return new BoxEl
        {
            Direction = 0, Gap = WaveeSpace.L, AlignItems = FlexAlign.Start,
            Children =
            [
                new TextEl(label) { Size = 13f, Color = Tok.TextSecondary, Width = 84f, Shrink = 0f },
                valueCol,
            ],
        };
    }

    // "Fans also like": one loadable regardless of release type. A short release reads the LEAD TRACK's related artists
    // (the same getTrack that carries the video signal); a full album reads the ARTIST's related artists (overview).
    static async Task<IReadOnlyList<Artist>> FansAsync(Services? svc, bool ready, bool shortRelease, string leadArtistUri, string seedTrackUri, System.Threading.CancellationToken ct)
    {
        if (svc is null || !ready) return Array.Empty<Artist>();
        if (shortRelease && seedTrackUri.Length > 0)
        {
            var ctx = await svc.AlbumEnrichment.GetTrackContextAsync(seedTrackUri, ct).ConfigureAwait(false);
            return ctx?.RelatedArtists ?? Array.Empty<Artist>();
        }
        return leadArtistUri.Length == 0
            ? Array.Empty<Artist>()
            : await svc.AlbumEnrichment.GetRelatedArtistsAsync(leadArtistUri, ct).ConfigureAwait(false);
    }

    // The similar-albums seed: the highest play-count track (the album's "hit"), falling back to the first track.
    static string SeedTrack(IReadOnlyList<Track> tracks)
    {
        if (tracks.Count == 0) return "";
        int best = 0;
        for (int i = 1; i < tracks.Count; i++)
            if (tracks[i].PlayCount > tracks[best].PlayCount) best = i;
        return tracks[best].Uri;
    }

    // "Watch the official video" — a thumbnail (the cover) with a centered play badge + the release label/meta.
    static Element WatchVideoSection(DetailModel m, DetailHandlers h) => new BoxEl
    {
        Direction = 1,
        Grow = 1f,
        AlignSelf = FlexAlign.Stretch,
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
        Direction = 0, Gap = WaveeSpace.M, ClipToBounds = true,
        Children = fans.Take(8).Select(a => ArtistChip(a, h)).ToArray(),
    };

    static Element ArtistChip(Artist a, DetailHandlers h) => new BoxEl
    {
        Direction = 0, AlignItems = FlexAlign.Center, Gap = WaveeSpace.S, Shrink = 0f, Height = 48f,
        Padding = new Edges4(WaveeSpace.S, 0f, WaveeSpace.L, 0f),
        Corners = CornerRadius4.All(24f), Fill = Tok.FillCardSecondary,
        BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault,
        HoverFill = Tok.FillCardDefault, PressedFill = Tok.FillSubtleTertiary,
        OnClick = () => h.Go("artist:" + a.Uri, a.Name), Cursor = CursorId.Hand,
        Children =
        [
            new BoxEl
            {
                Width = 32f, Height = 32f, Corners = CornerRadius4.All(16f), ClipToBounds = true,
                Children = [Surfaces.Artwork(a.Image, a.Id.GetHashCode() & 0x7fffffff, 32f, 32f, 16f)],
            },
            new TextEl(a.Name) { Size = 14f, Weight = 600, Color = Tok.TextPrimary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
        ],
    };

    // "Featured on" — a playlist PagedShelf (the More-by recipe, playlists instead of albums).
    static Element FeaturedSection(IReadOnlyList<PlaylistSummary> pls, DetailHandlers h) => new BoxEl
    {
        Direction = 1,
        Grow = 1f,
        AlignSelf = FlexAlign.Stretch,
        Padding = new Edges4(WaveeSpace.L, WaveeSpace.XL, WaveeSpace.L, WaveeSpace.L),
        Children =
        [
            PagedShelf.Create(
                pls.Count,
                cardAt: (i, w) => MediaCard.Shelf(pls[i].Cover, pls[i].Name, pls[i].OwnerName, pls[i].Uri,
                    () => h.OpenPlaylist(pls[i]), () => h.PlayContext(pls[i].Uri), w),
                measured: true,
                header: WaveeType.RailHeader(Loc.Get(Strings.Detail.FeaturedOn))),
        ],
    };

    // Album shelf (More-by / Similar albums) — a PagedShelf of square album cards: open the album, or play it from the card.
    static Element AlbumShelf(string header, IReadOnlyList<Album> albums, DetailHandlers h) => new BoxEl
    {
        Direction = 1,
        Grow = 1f,
        AlignSelf = FlexAlign.Stretch,
        Padding = new Edges4(WaveeSpace.L, WaveeSpace.XL, WaveeSpace.L, WaveeSpace.L),
        Children =
        [
            PagedShelf.Create(
                albums.Count,
                cardAt: (i, w) => MediaCard.Shelf(albums[i].Cover, albums[i].Name, AlbumSubtitle(albums[i]), albums[i].Uri,
                    () => h.OpenAlbum(albums[i]), () => h.PlayContext(albums[i].Uri), w),
                measured: true,
                header: WaveeType.RailHeader(header)),
        ],
    };

    // Album card subtitle: the artist (similar albums carry their own artist), else the year, else the kind badge.
    static string AlbumSubtitle(Album a) =>
        a.Artists.Count > 0 ? a.Artists[0].Name
        : a.Year > 0 ? a.Year.ToString()
        : Loc.Get(Strings.Detail.Badge.Album);

    // "Merch" — a PagedShelf of product cards (image + name + price). The card opens the external shop in the OS browser.
    static Element MerchSection(IReadOnlyList<MerchItem> merch) => new BoxEl
    {
        Direction = 1,
        Grow = 1f,
        AlignSelf = FlexAlign.Stretch,
        Padding = new Edges4(WaveeSpace.L, WaveeSpace.XL, WaveeSpace.L, WaveeSpace.L),
        Children =
        [
            PagedShelf.Create(
                merch.Count,
                cardAt: (i, w) => MerchCard(merch[i], w),
                measured: true,
                header: WaveeType.RailHeader(Loc.Get(Strings.Artist.Merch))),
        ],
    };

    static Element MerchCard(MerchItem item, float w)
    {
        float inner = MathF.Max(48f, w - 2f * WaveeSpace.S);
        return new BoxEl
        {
            Width = w, Direction = 1, Gap = WaveeSpace.XS, Padding = Edges4.All(WaveeSpace.S),
            Corners = CornerRadius4.All(WaveeRadius.Card), ClipToBounds = true,
            HoverFill = Tok.FillCardSecondary, PressedFill = Tok.FillCardDefault,
            // Open the external shop through the IPlatformApp.OpenUri PAL seam (what HyperlinkButton uses); headless records.
            OnClick = item.ShopUrl is { Length: > 0 } url ? () => InputHooks.Current.Default.OpenUri?.Invoke(url) : null,
            Children =
            [
                new BoxEl
                {
                    Width = inner, Height = inner, Corners = CornerRadius4.All(WaveeRadius.Control), ClipToBounds = true,
                    Children = [Surfaces.Artwork(item.Image, item.Name.GetHashCode() & 0x7fffffff, inner, inner, WaveeRadius.Control)],
                },
                new TextEl(item.Name) { Size = 13f, Weight = 600, Color = Tok.TextPrimary, MaxLines = 2, Trim = TextTrim.CharacterEllipsis, Width = inner },
                new TextEl(item.Price.Length > 0 ? item.Price : Loc.Get(Strings.Artist.Buy)) { Size = 12f, Weight = 600, Color = Tok.AccentTextPrimary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, Width = inner },
            ],
        };
    }

    static Element Section(string title, Element body) => new BoxEl
    {
        Direction = 1, Gap = WaveeSpace.M,
        Grow = 1f,
        AlignSelf = FlexAlign.Stretch,
        Padding = new Edges4(WaveeSpace.L, WaveeSpace.XL, WaveeSpace.L, WaveeSpace.L),
        Children = [WaveeType.RailHeader(title), body],
    };

    static Element AboutArtistSection(Artist artist, DetailHandlers h) => new BoxEl
    {
        Direction = 1,
        Grow = 1f,
        AlignSelf = FlexAlign.Stretch,
        Padding = new Edges4(WaveeSpace.L, WaveeSpace.XL, WaveeSpace.L, WaveeSpace.L),
        Children = [AboutCard(artist, h)],
    };

    // The "About the artist" card — compact identity + bio card; the whole card navigates to the artist, with Follow
    // on the right.
    static Element AboutCard(Artist artist, DetailHandlers h)
    {
        return new BoxEl
        {
            Direction = 0, Gap = WaveeSpace.L, AlignItems = FlexAlign.Center,
            Grow = 1f, AlignSelf = FlexAlign.Stretch,
            Padding = new Edges4(WaveeSpace.L, WaveeSpace.M, WaveeSpace.L, WaveeSpace.M),
            Corners = CornerRadius4.All(WaveeRadius.Card), Fill = Tok.FillCardSecondary,
            BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault,
            OnClick = () => h.Go("artist:" + artist.Uri, artist.Name),
            HoverFill = Tok.FillCardDefault, ClipToBounds = true, Cursor = CursorId.Hand,
            Children =
            [
                new BoxEl
                {
                    Width = 84f, Height = 84f, Shrink = 0f, Corners = CornerRadius4.All(42f), ClipToBounds = true,
                    Children = [PersonPicture.Create("", 84f, displayName: artist.Name, imageSourcePath: artist.Image?.Url)],
                },
                new BoxEl
                {
                    Direction = 1, Grow = 1f, Basis = 0f, Gap = WaveeSpace.XS,
                    Children =
                    [
                        new TextEl(Loc.Get(Strings.Detail.AboutTheArtist).ToUpperInvariant())
                            { Size = 11f, Weight = 700, Color = Tok.TextTertiary, CharSpacing = 120f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
                        new BoxEl
                        {
                            Direction = 0, AlignItems = FlexAlign.Center, Gap = WaveeSpace.S,
                            Children =
                            [
                                new TextEl(artist.Name) { Size = 20f, Weight = 700, Color = Tok.TextPrimary, Grow = 1f, Basis = 0f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
                                artist.Verified ? Icon(Mdl.Check, 12f, Tok.TextSecondary) : new BoxEl(),
                            ],
                        },
                        string.IsNullOrWhiteSpace(artist.Bio)
                            ? new BoxEl()
                            : new TextEl(artist.Bio!) { Size = 13f, Color = Tok.TextSecondary, Wrap = TextWrap.Wrap, MaxLines = 2, Trim = TextTrim.CharacterEllipsis },
                    ],
                },
                Embed.Comp(() => new FollowButton(artist.Uri)),
            ],
        };
    }

    // ── per-section loading skeletons (static grey placeholders, same padding as the real Section) ──────────────────
    static Element TrailingSkeleton() => new BoxEl
    {
        Direction = 1,
        Grow = 1f,
        AlignSelf = FlexAlign.Stretch,
        Children =
        [
            CardSkeleton(),
            ChipsSkeleton(),
            ShelfSkeleton(),
        ],
    };

    static Element CardSkeleton() => SectionSkeleton(
        new BoxEl { Height = 96f, Corners = CornerRadius4.All(WaveeRadius.Card), Fill = Tok.FillCardDefault });

    static Element ChipsSkeleton() => SectionSkeleton(new BoxEl
    {
        Direction = 0, Gap = WaveeSpace.S, ClipToBounds = true,
        Children = Enumerable.Range(0, 5).Select(_ => (Element)new BoxEl
        { Width = 132f, Height = 40f, Shrink = 0f, Corners = CornerRadius4.All(20f), Fill = Tok.FillCardDefault }).ToArray(),
    });

    static Element ShelfSkeleton() => SectionSkeleton(new BoxEl
    {
        Direction = 0, Gap = WaveeSpace.M, ClipToBounds = true,
        Children = Enumerable.Range(0, 5).Select(_ => (Element)new BoxEl
        {
            Width = 150f, Direction = 1, Gap = WaveeSpace.S, Shrink = 0f,
            Children =
            [
                new BoxEl { Width = 150f, Height = 150f, Corners = CornerRadius4.All(WaveeRadius.Card), Fill = Tok.FillCardDefault },
                new BoxEl { Width = 110f, Height = 12f, Corners = CornerRadius4.All(4f), Fill = Tok.FillCardDefault },
                new BoxEl { Width = 70f, Height = 10f, Corners = CornerRadius4.All(4f), Fill = Tok.FillCardDefault },
            ],
        }).ToArray(),
    });

    static Element SectionSkeleton(Element body) => new BoxEl
    {
        Direction = 1, Gap = WaveeSpace.M,
        Grow = 1f,
        AlignSelf = FlexAlign.Stretch,
        Padding = new Edges4(WaveeSpace.L, WaveeSpace.XL, WaveeSpace.L, WaveeSpace.L),
        Children =
        [
            new BoxEl { Width = 160f, Height = 18f, Corners = CornerRadius4.All(4f), Fill = Tok.FillCardDefault },
            body,
        ],
    };
}
