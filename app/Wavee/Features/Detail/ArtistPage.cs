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

// The artist page (docs/architecture.md §2 "Album & artist") — WaveeMusic's "magazine" skeleton, the one genuine
// exception to the shared rail+list detail surface. Top-to-bottom: a full-bleed hero (art + eyebrow pills [Verified /
// world rank] + name + a monthly-listeners/followers meta line + Play/Shuffle/Follow), a 2-column "Popular tracks +
// Popular releases" band, an About excerpt, a split Discography (Albums / Singles & EPs shelves), a Profile-facts stat
// grid, and a "Fans also like" shelf. Route-reactive (one instance serves successive artists) and reads the cached
// LibraryStore (per-artist detail cache + the artists pool for fans) so revisits + the library right pane are instant.
sealed class ArtistPage : Component
{
    readonly Signal<Route> _route;
    public ArtistPage(Signal<Route> route) { _route = route; }

    internal static string? UriOf(Route r) =>
        r.Name.StartsWith("artist:", StringComparison.Ordinal) ? r.Name["artist:".Length..] : null;

    public override Element Render()
    {
        var svc = UseContext(Services.Slot);
        var go = UseContext(HistoryStore.NavCtx);
        var bridge = UseContext(PlaybackBridge.Slot);
        var store = UseContext(LibraryStore.Slot);
        if (svc is null || store is null) return new BoxEl { Grow = 1f };

        var route = _route.Value;                       // subscribe → reload on artist→artist nav (reused slot)
        string uri = UriOf(route) ?? "";
        this.UseSoftReveal(dy: 0f, blur: 0f);

        // Cached per-artist detail (instant revisit) + the cached artists pool for "fans also like".
        var artist = store.ArtistDetail(uri, ct => svc.Library.GetArtistAsync(uri, ct), EmptyArtist(uri));
        store.EnsureArtists();
        var fansList = store.Artists.Value.Value;       // subscribe → fans fill in when the pool resolves

        return ScrollView(StatefulRegion.Single(artist, Skeleton, a => Body(a, fansList, svc, go, bridge))) with { Grow = 1f };
    }

    static Artist EmptyArtist(string uri) => new("", uri, "", null);

    // ── body ─────────────────────────────────────────────────────────────────────────────────────────────
    Element Body(Artist a, IReadOnlyList<Artist> fansAll, Services svc, Action<string, string?> go, PlaybackBridge? bridge)
    {
        string uri = a.Uri;
        var popular = FakeData.TopTracksOf(a);
        var albumsAll = a.TopAlbums ?? Array.Empty<Album>();
        var fans = fansAll.Where(f => f.Uri != uri).Take(10).ToArray();
        var albums = albumsAll.Where(al => al.Kind is AlbumKind.Album or AlbumKind.Compilation).ToArray();
        var singles = albumsAll.Where(al => al.Kind is AlbumKind.Single or AlbumKind.EP).ToArray();

        void Play() => _ = svc.Player.PlayAsync(uri, 0);
        void Shuffle() { _ = svc.Player.SetShuffleAsync(true); _ = svc.Player.PlayAsync(uri, 0); }
        void PlayContext(string u) => _ = svc.Player.PlayAsync(u, 0);

        var sections = new List<Element>(8) { Controls(uri, Play, Shuffle) };
        if (popular.Count > 0) sections.Add(TopBand(popular, uri, bridge, svc, albumsAll, go, PlayContext));
        if (a.Bio is { Length: > 0 }) sections.Add(Section(Loc.Get(Strings.Artist.About), AboutBlurb(a)));
        if (albums.Length > 0) sections.Add(DiscographyShelf(Loc.Get(Strings.Artist.Albums), albums, go, PlayContext));
        if (singles.Length > 0) sections.Add(DiscographyShelf(Loc.Get(Strings.Artist.SinglesEps), singles, go, PlayContext));
        sections.Add(ProfileFacts(a, albums.Length, singles.Length));
        if (fans.Length > 0) sections.Add(FansShelf(fans, go, PlayContext));

        var inner = new BoxEl
        {
            Direction = 1, Gap = WaveeSpace.XL,
            Padding = new Edges4(WaveeSpace.L, WaveeSpace.L, WaveeSpace.L, PlayerDock.Reserve + WaveeSpace.XXL),
            Children = sections.ToArray(),
        };
        return new BoxEl { Direction = 1, Children = [Responsive.Of(w => Banner(a, w), fallback: 900f), inner] };
    }

    // ── hero banner ──────────────────────────────────────────────────────────────────────────────────────
    static Element Banner(Artist a, float w)
    {
        const float h = 340f;
        int seed = a.Id.GetHashCode() & 0x7fffffff;
        int rank = WorldRank(a.Uri);
        int albumCount = a.TopAlbums?.Count(al => al.Kind is AlbumKind.Album or AlbumKind.Compilation) ?? 0;
        int singleCount = a.TopAlbums?.Count(al => al.Kind is AlbumKind.Single or AlbumKind.EP) ?? 0;
        return new BoxEl
        {
            Width = w, Height = h, ZStack = true, ClipToBounds = true,
            Children =
            [
                Surfaces.Artwork(a.Image, seed, w, h, 0f, decodePx: 480),
                new BoxEl
                {
                    Width = w, Height = h,
                    Gradient = LinearGradient(180f,
                        new GradientStop(0f, Scrim(0f)),
                        new GradientStop(0.55f, Scrim(0.18f)),
                        new GradientStop(1f, Scrim(0.86f))),
                },
                new BoxEl
                {
                    Width = w, Height = h, Direction = 1, Justify = FlexJustify.End, Gap = WaveeSpace.S,
                    Padding = new Edges4(WaveeSpace.XL, WaveeSpace.XL, WaveeSpace.XL, WaveeSpace.XL),
                    Children =
                    [
                        EyebrowPills(a, rank),
                        WaveeType.PageHero(a.Name) with
                        {
                            Size = HeroSize(a.Name), Weight = 900, Color = WhiteText,
                            Wrap = TextWrap.Wrap, MaxLines = 2, Trim = TextTrim.CharacterEllipsis,
                        },
                        new TextEl(Strings.Artist.HeroMeta(Count(a.MonthlyListeners), Count(a.Followers), albumCount.ToString(), singleCount.ToString()))
                        { Size = 14f, Weight = 600, Color = WhiteText with { A = 0.85f }, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
                    ],
                },
            ],
        };
    }

    static float HeroSize(string name) => name.Length <= 10 ? 72f : name.Length <= 18 ? 56f : name.Length <= 28 ? 44f : 34f;

    static Element EyebrowPills(Artist a, int rank)
    {
        var pills = new List<Element>(2);
        if (a.Verified) pills.Add(VerifiedPill());
        if (rank > 0) pills.Add(GlassPill(Strings.Artist.WorldRank(rank.ToString())));
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

    // ── controls ─────────────────────────────────────────────────────────────────────────────────────────
    Element Controls(string uri, Action play, Action shuffle) => new BoxEl
    {
        Direction = 0, Gap = WaveeSpace.M, AlignItems = FlexAlign.Center,
        Children = [PlayPill(play), Fab(Icons.Shuffle, shuffle), Embed.Comp(() => new FollowButton(uri))],
    };

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
        Children = [Icon(glyph, 18f, Tok.TextSecondary)],
    };

    // ── sections ─────────────────────────────────────────────────────────────────────────────────────────
    static Element Section(string title, Element body) => new BoxEl
    {
        Direction = 1, Gap = WaveeSpace.M, Children = [WaveeType.RailHeader(title), body],
    };

    // Top tracks (left, wider) + Popular releases (right) — a 2-column band, stacked on a narrow page.
    Element TopBand(IReadOnlyList<Track> popular, string uri, PlaybackBridge? bridge, Services svc,
                    IReadOnlyList<Album> albumsAll, Action<string, string?> go, Action<string> play) =>
        Responsive.Of(w =>
        {
            bool wide = w >= 760f;
            Element left = Section(Loc.Get(Strings.Artist.Popular), Embed.Comp(() => new ArtistPopular(popular, uri, bridge, svc)));
            Element right = Section(Loc.Get(Strings.Artist.PopularReleases), PopularReleases(albumsAll, go, play));
            return new BoxEl
            {
                Direction = (byte)(wide ? 0 : 1), Gap = WaveeSpace.XL,
                Children =
                [
                    new BoxEl { Direction = 1, Grow = wide ? 1.5f : 1f, Basis = 0f, Children = [left] },
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

    static Element DiscographyShelf(string title, IReadOnlyList<Album> albums, Action<string, string?> go, Action<string> play) => new BoxEl
    {
        Direction = 1,
        Children =
        [
            PagedShelf.Create(
                albums.Count,
                cardAt: (i, w) => MediaCard.Shelf(albums[i].Cover, albums[i].Name,
                    albums[i].Year > 0 ? albums[i].Year.ToString() : KindLabel(albums[i].Kind), albums[i].Uri,
                    () => go("album:" + albums[i].Uri, albums[i].Name), () => play(albums[i].Uri), w),
                measured: true, header: WaveeType.RailHeader(title)),
        ],
    };

    static Element AboutBlurb(Artist a) => new BoxEl
    {
        Direction = 0, Gap = WaveeSpace.XL, AlignItems = FlexAlign.Start,
        Padding = new Edges4(WaveeSpace.XL, WaveeSpace.L, WaveeSpace.XL, WaveeSpace.L),
        Corners = CornerRadius4.All(WaveeRadius.Card), Fill = Tok.FillCardSecondary,
        BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault, ClipToBounds = true,
        Children =
        [
            new BoxEl { Width = 88f, Height = 88f, Corners = CornerRadius4.All(44f), ClipToBounds = true, Shrink = 0f,
                Children = [Surfaces.Artwork(a.Image, a.Id.GetHashCode() & 0x7fffffff, 88f, 88f, 44f, decodePx: 256)] },
            new TextEl(a.Bio ?? "") { Size = 14f, Color = Tok.TextSecondary, Wrap = TextWrap.Wrap, MaxLines = 6, Trim = TextTrim.CharacterEllipsis, Grow = 1f, Basis = 0f },
        ],
    };

    static Element ProfileFacts(Artist a, int albums, int singles) => Section(Loc.Get(Strings.Artist.ProfileFacts), new BoxEl
    {
        Direction = 0, Gap = WaveeSpace.M, Wrap = true,
        Children =
        [
            StatTile(Count(a.MonthlyListeners), Loc.Get(Strings.Artist.Stat.Monthly)),
            StatTile(Count(a.Followers), Loc.Get(Strings.Artist.Stat.Followers)),
            StatTile(albums.ToString(), Loc.Get(Strings.Artist.Stat.Albums)),
            StatTile(singles.ToString(), Loc.Get(Strings.Artist.Stat.Singles)),
        ],
    });

    static Element StatTile(string value, string label) => new BoxEl
    {
        Direction = 1, Gap = WaveeSpace.XS, Grow = 1f, Basis = 180f,
        Padding = new Edges4(WaveeSpace.L, WaveeSpace.L, WaveeSpace.L, WaveeSpace.L),
        Corners = CornerRadius4.All(WaveeRadius.Card), Fill = Tok.FillCardSecondary,
        BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault,
        Children = [new TextEl(value) { Size = 28f, Weight = 800, Color = Tok.TextPrimary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis }, new TextEl(label) { Size = 12f, Color = Tok.TextSecondary }],
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
                measured: true, header: WaveeType.RailHeader(Loc.Get(Strings.Detail.FansAlsoLike))),
        ],
    };

    static string Count(long n) => n.ToString("N0");
    static ColorF Scrim(float a) => ColorF.FromRgba(0, 0, 0) with { A = a };               // black-with-alpha hero scrim stop
    static readonly ColorF WhiteText = ColorF.FromRgba(255, 255, 255);
    static int StableHash(string s) { int h = 17; foreach (char c in s) h = h * 31 + c; return h & 0x7fffffff; }
    static int WorldRank(string uri) => uri.Length == 0 ? 0 : 1 + StableHash(uri) % 500;

    // ── loading skeleton ───────────────────────────────────────────────────────────────────────────────────
    static Element Skeleton() => new BoxEl
    {
        Direction = 1,
        Children =
        [
            new BoxEl { Height = 340f, Fill = Tok.FillCardDefault },
            new BoxEl
            {
                Direction = 1, Gap = WaveeSpace.XL,
                Padding = new Edges4(WaveeSpace.L, WaveeSpace.L, WaveeSpace.L, WaveeSpace.XXL),
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

// The "Popular" top-tracks list for an artist. Its own component so the now-playing equalizer re-skins on playback
// changes WITHOUT re-rendering the whole artist page. Each row plays the artist context at its index (the same ordered
// set FakeData.ContextTracks resolves for the artist uri), so #1 here is #1 in the queue.
sealed class ArtistPopular : Component
{
    readonly IReadOnlyList<Track> _tracks;
    readonly string _ctx;
    readonly PlaybackBridge? _bridge;
    readonly Services _svc;
    public ArtistPopular(IReadOnlyList<Track> tracks, string ctx, PlaybackBridge? bridge, Services svc)
    { _tracks = tracks; _ctx = ctx; _bridge = bridge; _svc = svc; }

    // The SAME shared cell as the detail + library lists, with a compact "Popular" column set: [#↔play, ♥, art, title,
    // plays, duration]. So a top-track row gets the identical number↔play/pause-on-hover, now-playing equalizer and
    // per-row heart — just fewer columns and no multi-select (this is a short preview).
    static readonly ColumnSet Cols = new(Album: false, By: false, Date: false, Video: false, Plays: true, Heart: true, Thumb: true);
    static readonly TrackSize[] Columns =
        [TrackSize.Px(36f), TrackSize.Px(40f), TrackSize.Px(TrackRow.ThumbSize), TrackSize.Star(), TrackSize.Px(84f), TrackSize.Px(64f)];

    public override Element Render()
    {
        var go = UseContext(HistoryStore.NavCtx);
        var lib = UseContext(LibraryBridge.Slot);
        var cur = _bridge?.CurrentTrack.Value;          // subscribe → now-playing equalizer
        bool playing = _bridge?.IsPlaying.Value ?? false;
        bool buffering = _bridge?.IsBuffering.Value ?? false;
        var rows = new Element[_tracks.Count];
        for (int i = 0; i < _tracks.Count; i++)
        {
            var t = _tracks[i];
            int idx = i;
            bool isNow = cur is not null && cur.Id == t.Id;
            var st = new TrackRow.State(isNow, isNow && playing, isNow && buffering, IsTop: false,
                                        Saved: t.Uri.Length > 0 && (lib?.IsSaved(t.Uri) ?? false));   // subscribe → heart re-skins on toggle
            rows[i] = TrackRow.Row(t, i, st, Cols, Columns, 56f, showTrackArtist: false, go,
                onPlay: () => PlayRow(idx, t),
                onLike: t.Uri.Length > 0 ? () => lib?.ToggleSaved(t.Uri) : null);
        }
        return new BoxEl { Direction = 1, Children = rows };
    }

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
