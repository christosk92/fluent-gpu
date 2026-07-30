using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using Wavee.Core;
using Wavee.Features.Detail;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

// The shared detail page (playlist / album / single / liked). A Component keyed per route in ContentHost, so the
// existing KeepAlive boundary caches it. It loads the matching IMusicLibrary slice through UseResource (which
// cancels on unmount — a fast nav-away aborts in flight), shows a matched skeleton via Skel.Region, then reveals the
// two-column shell. The per-context config is resolved POST-load (an album with ≤2 tracks becomes a "single").
sealed class DetailPage : Component
{
    readonly Signal<Route> _route;   // the (per-pane) navigation route, read reactively so ONE instance serves successive detail pages
    public DetailPage(Signal<Route> route) { _route = route; }

    // Route → (kind, id): album:/pl: carry the uri after the prefix; "liked" is the saved-tracks collection.
    internal static (DetailKind Kind, string? Id) ParseDetail(Route r) =>
        r.Name.StartsWith("album:", StringComparison.Ordinal) ? (DetailKind.Album, r.Name["album:".Length..])
        // Same kind, same config, same shell — only the id needs resolving before the load can read it.
        : r.Name.StartsWith("prerelease:", StringComparison.Ordinal) ? (DetailKind.Album, r.Name["prerelease:".Length..])
        : r.Name.StartsWith("pl:", StringComparison.Ordinal) ? (DetailKind.Playlist, r.Name["pl:".Length..])
        : r.Name == "local" ? (DetailKind.Playlist, "wavee:local:all")   // the Local Files collection (LocalSource owns it)
        : r.Name.StartsWith("show:", StringComparison.Ordinal) ? (DetailKind.Show, r.Name["show:".Length..])
        : (DetailKind.Liked, null);

    public override Element Render()
    {
        var svc = UseContext(Services.Slot);
        if (svc is null) return new BoxEl { Grow = 1f };
        var navPreview = UseContext(NavPreviewStore.Slot);

        // Subscribe the RAW route → re-render when navigation swaps the detail route in place. A nav to ANOTHER page
        // class (artist:/home) also writes this signal, but the reconciler's structural-effect ordering guarantees the
        // KeepAlive boundary parks this page before its render effect drains, so no stale cross-class render happens
        // (engine: ReactiveRuntime.Flush park-before-render; gate.reconciler.park-before-render).
        var route = _route.Value;
        var (kind, id) = ParseDetail(route);

        // Preview identity is route-scoped so a card's already-known header data can appear immediately while the full
        // model loads. It is deliberately not used as a shared-element/connected-animation key.
        string previewKey = route.Name;

        // The PARTIAL model the Home card already had (cover/title/artist) — optional: deep links / search have none.
        var preview = UseMemo(() => navPreview?.Take(previewKey), previewKey);
        // Dep-keyed on the route: when navigation swaps the detail route on a REUSED instance, cancel the prior load and
        // refetch for the new id (resetting to the new preview/skeleton). Fires once at mount when nothing is reused.
        // Stable per-instance loadable, re-driven by the route dep key — DetailShell freezes the model at construction,
        // so the loadable INSTANCE must be stable across route swaps (a fresh store-cache instance per route would leave
        // the reused shell pinned to the first item — the master-detail reactivity bug). KeepAlive caches the parked page.
        var model = UseResource(ct => LoadAsync(svc, kind, id, ct), preview ?? PendingSeed(kind), route.Name).Loadable;

        // §4.1 — open-playlist LIVE in-place refresh (kills the skeleton flash). Subscribe the REAL store; when a push lands
        // for THIS playlist (or a Bulk), debounce the burst 150ms, re-run the SAME load off-thread, and SetReady the SAME
        // loadable in place — NEVER SetPending (that would re-seed to Empty = the shimmer). The UseResource dep stays
        // route.Name, untouched. Offline / fake backend (RealStore null) → a no-op. The subscription reads the LIVE route
        // (so one mount-lifetime subscription serves successive playlists), and eager-push context tracks the open uri.
        var post = Context.UsePost();
        var realStore = svc.RealStore;
        var realSync = svc.RealSync;
        UseEffect(() => realSync?.SetOpenContext(kind == DetailKind.Playlist ? id : null), route.Name);
        Context.UseSignalEffect(() =>
        {
            if (realStore is null) return;
            var gate = new object();
            System.Threading.CancellationTokenSource? debounce = null;
            var sub = realStore.Changes.Subscribe(Wavee.Backend.Observers.From<Wavee.Backend.StoreChange>(c =>
            {
                var (k, pid) = ParseDetail(_route.Peek());
                // Live kinds: an open PLAYLIST refreshes on its own uri (membership/diff writes bump it); the LIKED page
                // refreshes on any Liked-kind change (an unlike bumps the track uri with Kind=Liked — the list must drop
                // the row) — both also on a Bulk (hydrate/delta bursts coalesce into one).
                bool relevant = k switch
                {
                    DetailKind.Playlist when pid is not null => c.IsBulk || c.Uri == pid,
                    DetailKind.Liked => c.IsBulk || c.Kind == Wavee.Core.CollectionKind.Liked,
                    // An open ALBUM refreshes on a Bulk only: the async music-video detection folds its per-track
                    // HasVideo flips into one bulk change, which would otherwise stay invisible until re-navigation.
                    DetailKind.Album when pid is not null => c.IsBulk || c.Uri == pid,
                    DetailKind.Show when pid is not null => c.IsBulk || c.Uri == pid,
                    _ => false,
                };
                if (!relevant) return;
                System.Threading.CancellationTokenSource cts;
                lock (gate) { debounce?.Cancel(); debounce?.Dispose(); debounce = cts = new System.Threading.CancellationTokenSource(); }
                var token = cts.Token;
                _ = Task.Run(async () =>
                {
                    try
                    {
                        // Short settle: long enough to fold a diff-apply + hydration burst into one re-map, short enough
                        // that a SELF-action (unlike the row you're looking at) reads as immediate, not laggy.
                        await Task.Delay(50, token).ConfigureAwait(false);
                        var fresh = await LoadAsync(svc, k, pid, token).ConfigureAwait(false);
                        if (!token.IsCancellationRequested) post(() =>
                        {
                            if (token.IsCancellationRequested) return;
                            // Nav-away race: the debounced load may land after the user routed to a DIFFERENT detail page,
                            // which now reuses this same loadable cell. Re-resolve the LIVE route and drop the write unless
                            // it still points at THIS page — otherwise the old model flashes into the new page.
                            var (k2, pid2) = ParseDetail(_route.Peek());
                            if (k2 != k || pid2 != pid) return;
                            model.SetReady(fresh);
                        });
                    }
                    catch (OperationCanceledException) { }
                    catch { /* a failed background refresh keeps the current content — never surfaces */ }
                });
            }));
            Reactive.OnCleanup(() =>
            {
                sub.Dispose();
                lock (gate) { debounce?.Cancel(); debounce?.Dispose(); debounce = null; }
                realSync?.SetOpenContext(null);
            });
        });

        // Pre-loaded: render the shell straight away from the preview (header live), tracks stream in via Skel.Region.
        // Thread the preview's cover as the fallback so a loaded null cover never drops the flown-in art to a placeholder.
        if (preview is not null)
            return Embed.Comp(() => new DetailShell(_route, model, preview.Cover, svc.Settings));

        // No data at click (deep link): Skel.Region derives the full-page shimmer from the real responsive shell rendered
        // against PendingSeed(kind). The plain Grow=1 wrapper gives the boundary synchronous layout participation.
        return new BoxEl
        {
            Grow = 1f, Direction = 1,
            Children =
            [
                Skel.Region(
                    model,
                    onFailed: () => ErrorState.Build(model.Error),
                    // Pass the SHARED loadable (Ready when content runs), not a fresh Loadable.Ready(m): the shell is REUSED
                    // across detail routes, so it must read the one re-driven loadable — a per-render wrapper would leave the
                    // reused shell pinned to the first album's value.
                    content: _ => new BoxEl
                    {
                        Grow = 1f, Direction = 0,
                        Children =
                        [
                            Embed.Comp(() => new DetailShell(_route, model, settings: svc.Settings))
                                with { DeriveRenderedOutput = true },
                        ],
                    },
                    smoothResize: false),
            ],
        };
    }

    // Album cfg is release-kind-dependent (single = one-track layout, compilation = various-artists rows); playlist/liked fixed.
    internal static DetailConfig ResolveConfig(DetailKind kind, DetailModel m) => kind switch
    {
        DetailKind.Playlist => DetailConfig.Playlist,
        DetailKind.Liked => DetailConfig.Liked,
        DetailKind.Show => DetailConfig.Show,
        _ => m.ReleaseKind switch
        {
            AlbumKind.Single => DetailConfig.Single,
            AlbumKind.Compilation => DetailConfig.Compilation,
            _ => DetailConfig.Album,   // Album + EP share the album layout
        },
    };

    // Representative DATA for content(seed) derivation. Eight blank records give the real track/episode components a
    // useful viewport shape without encoding any playlist length (1494, 1600, or otherwise) into loading geometry.
    internal static DetailModel PendingSeed(DetailKind kind)
    {
        if (kind == DetailKind.Show)
        {
            var episodes = new Episode[8];
            for (int i = 0; i < episodes.Length; i++)
                episodes[i] = new Episode($"pending-episode-{i}", $"pending:episode:{i}", "", "", null,
                    180_000, DateTimeOffset.UnixEpoch);
            return DetailModel.Empty with
            {
                ContextUri = "pending:show",
                BadgeType = " ",
                MetaLine = " ",
                Episodes = episodes,
                Publisher = " ",
            };
        }

        var tracks = new Track[8];
        for (int i = 0; i < tracks.Length; i++)
            tracks[i] = new Track(
                $"pending-track-{i}", $"pending:track:{i}", "",
                Array.Empty<ArtistRef>(), new AlbumRef("", "", ""),
                180_000, false, null);

        return DetailModel.Empty with
        {
            ContextUri = kind == DetailKind.Liked ? "spotify:collection:tracks" : "pending:detail",
            BadgeType = kind == DetailKind.Album ? " " : null,
            OwnerName = kind == DetailKind.Playlist ? " " : null,
            MetaLine = " ",
            Tracks = tracks,
        };
    }

    internal static async Task<DetailModel> LoadAsync(Services svc, DetailKind kind, string? id, CancellationToken ct) => kind switch
    {
        DetailKind.Playlist => await LoadPlaylistWithSaveCountAsync(svc, id ?? "", ct),
        DetailKind.Liked => MapLiked(await svc.Library.GetLikedSongsAsync(ct)),
        DetailKind.Show => MapShow(await svc.Library.GetShowAsync(id ?? "", ct)),
        _ => await LoadAlbumDetailAsync(svc, id ?? "", ct),
    };

    /// <summary>The album detail load, with the ONE extra hop an upcoming release needs.
    ///
    /// A prerelease route must resolve before it can read anything — the two ids are unrelated (Wavee.Core/PreReleaseUris).
    /// The REVERSE hop (album → prerelease link, for the pre-save heart) is deliberately gated: kind 138 404s for almost
    /// every album, so it is only asked when the album already looks upcoming. A normal album open costs exactly what it
    /// costs today.</summary>
    static async Task<DetailModel> LoadAlbumDetailAsync(Services svc, string id, CancellationToken ct)
    {
        string albumUri = id;
        PreReleaseLink? link = null;
        if (PreReleaseUris.IsPreRelease(id))
        {
            link = await svc.PreRelease.ResolveAsync(id, ct).ConfigureAwait(false);
            if (link is null) return DetailModel.Empty;   // unresolvable (offline / 404 / dead entity) → the existing empty state
            albumUri = link.AlbumUri;
        }
        var album = await svc.Library.GetAlbumAsync(albumUri, ct).ConfigureAwait(false);
        if (link is null
            && (album.IsPreRelease || PreReleaseDerivation.UpcomingAt(album, DateTimeOffset.UtcNow) is not null))
            link = await svc.PreRelease.ResolveAsync(albumUri, ct).ConfigureAwait(false);
        return MapAlbum(album, link);
    }

    static async Task<Playlist?> LoadPlaylistAsync(Services svc, string uri, CancellationToken ct)
    {
        var p = await svc.Library.GetPlaylistAsync(uri, ct).ConfigureAwait(false);
        if (p is null || !p.Capabilities.IsOwner || svc.RealPlaylistMutations is null) return p;
        try
        {
            var perm = await svc.RealPlaylistMutations.GetBasePermissionAsync(p.Uri, ct).ConfigureAwait(false);
            if (perm is { } bp)
                return p with { IsPublic = bp.IsPublic, BasePermissionRevision = bp.Revision };
        }
        catch { /* offline / transient — keep default visibility */ }
        return p;
    }

    internal static async Task<DetailModel?> ReloadPlaylistDetailAsync(Services svc, string uri, CancellationToken ct = default)
    {
        var p = await LoadPlaylistAsync(svc, uri, ct).ConfigureAwait(false);
        return p is null ? null : MapPlaylist(p);
    }

    // A podcast show folds onto the shared detail surface: rail = cover + PODCAST pill + publisher/episode-count meta +
    // description + Play/Follow; the right column renders Episodes (DetailConfig.Show.Content == Episodes → EpisodeList).
    static DetailModel MapShow(Show? s)
    {
        if (s is null) return DetailModel.Empty;
        var eps = s.Episodes ?? Array.Empty<Episode>();
        string meta = s.Publisher + " · " + Strings.Podcast.EpisodeCount(eps.Count);
        return new DetailModel(
            Title: s.Name, Cover: s.Cover, ContextUri: s.Uri,
            BadgeType: Loc.Get(Strings.Podcast.Show), Year: null, OwnerName: null, OwnerImage: null,
            Artists: Array.Empty<ArtistRef>(), Description: s.Description, MetaLine: meta,
            Tracks: Array.Empty<Track>(), AboutArtist: null,
            Episodes: eps, Publisher: s.Publisher);
    }

    /// <summary>How long the header will wait on the save count before rendering without it. The popcount body is
    /// 6-11 bytes and it runs CONCURRENTLY with the (far heavier) playlist load, so in practice this never elapses —
    /// it exists so a hung spclient connection can never hold a painted header hostage to a decorative number.</summary>
    static readonly TimeSpan SaveCountGrace = TimeSpan.FromMilliseconds(250);

    static async Task<DetailModel> LoadPlaylistWithSaveCountAsync(Services svc, string id, CancellationToken ct)
    {
        // Started FIRST and awaited last: the count rides along inside the playlist load's own latency instead of
        // adding to it. Never awaited without a grace window — see SaveCountGrace.
        var saves = svc.PlaylistPopcount.GetSaveCountAsync(PlaylistUri(id), ct);
        var playlist = await LoadPlaylistAsync(svc, id, ct).ConfigureAwait(false);

        long? count = null;
        try { count = await saves.WaitAsync(SaveCountGrace, ct).ConfigureAwait(false); }
        catch (TimeoutException) { }              // slow counter → header renders without the segment
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
        if (playlist is null) return DetailModel.Empty;
        return MapPlaylist(playlist, count);
    }

    /// <summary>The playlist route id as a uri. Ids arrive bare from the route but a full uri also flows through some
    /// call paths, so accept both rather than producing `spotify:playlist:spotify:playlist:…`.</summary>
    static string PlaylistUri(string id)
        => id.StartsWith("spotify:", StringComparison.Ordinal) ? id : "spotify:playlist:" + id;

    static DetailModel MapPlaylist(Playlist p, long? saveCount = null)
    {
        var tracks = p.Tracks ?? Array.Empty<Track>();
        // "50 songs · 12,345 saves · 2 hr 59 min" when the count is known, else the existing two-segment line. A
        // playlist genuinely at 0 saves (a brand-new private one) also omits the segment rather than reading "0 saves".
        string songs = Strings.Detail.SongCount(p.TrackCount);
        string total = DetailFormat.TotalTime(DetailFormat.TotalMs(tracks));
        string meta = saveCount is > 0 and var n
            ? Strings.Detail.MetaLineSaved(songs, Strings.Detail.SaveCount(n), total)
            : Strings.Detail.MetaLine(songs, total);
        // Data-drive the optional columns: show Date-added if any track has one, and Added-by only when the playlist is
        // collaborative (≥2 distinct contributors) — matching the reference app's "hide unless it carries signal" rule.
        bool hasDate = false, hasVideo = false;
        var contributors = new HashSet<string>();
        for (int i = 0; i < tracks.Count; i++)
        {
            if (tracks[i].AddedAt is not null) hasDate = true;
            if (VideoPresence.HasVideo(tracks[i])) hasVideo = true;   // a user-attached mp4 also earns the Video column
            if (tracks[i].AddedBy is { } by) contributors.Add(by);
        }
        LogVideoSweep("playlist", p.Uri, tracks);
        return new DetailModel(
            Title: p.Name, Cover: p.Cover, ContextUri: p.Uri,
            BadgeType: null, Year: null, OwnerName: p.OwnerName, OwnerImage: p.Owner?.Avatar,
            Artists: Array.Empty<ArtistRef>(), Description: p.Description, MetaLine: meta,
            Tracks: tracks, AboutArtist: null,
            HasDateAdded: hasDate, HasAddedBy: contributors.Count >= 2, HasVideo: hasVideo,
            Capabilities: p.Capabilities,
            Collaborators: p.Collaborators,
            UserProfilesById: UserProfileMap(p),
            IsPublic: p.IsPublic,
            BasePermissionRevision: p.BasePermissionRevision,
            Tuning: p.Tuning,
            ShareUrl: SpotifyPlaylistWebUrl(p.Uri));
    }

    // ── the per-page-open association sweep (video.assoc.page) ────────────────────────────────────────────────────────
    // Runs where the HasVideo roll-up is computed — inside the async LOAD (LoadAsync / the debounced live re-map), never on
    // a render or a frame. VideoPresence.HasVideo stays the row path's single silent boolean probe; this walks the same
    // tracks once more through the DIAGNOSTIC accessor to split the "no" into its two very different causes:
    //   noRow    — the plane holds nothing for this uri: either nobody ever requested it (a coverage hole) or the request
    //              came back with no kind-99 entry at all.
    //   negative — a row that says "no video": a real 404/empty-200 verdict, or a sealed miss cached from one.
    // The uri SAMPLE is the load-bearing field: the reported symptom ("the playlist says no, searching the same song says
    // yes") is only decidable by comparing the uri a playlist row carries against the uri the search response carried, and
    // relinked/alternative track uris are the expected way for those to differ. The app persists no alias→canonical map
    // (only SpotifyVideoService.RecoverCanonicalAsync derives one, transiently), so an "an alternate uri HAS a video"
    // count cannot be computed here without inventing a resolver — read `video.assoc.recover*` for that half instead.
    static void LogVideoSweep(string kind, string contextUri, IReadOnlyList<Track> tracks)
    {
        var log = WaveeLog.Instance;
        if (!log.IsEnabled(WaveeLogLevel.Info)) return;
        int withVideo = 0, overrideOnly = 0, noRow = 0, negative = 0;
        var missSample = new System.Text.StringBuilder();
        int sampled = 0;
        for (int i = 0; i < tracks.Count; i++)
        {
            var uri = tracks[i].Uri;
            var assoc = VideoPresence.Association(uri);
            if (assoc is { HasVideo: true }) { withVideo++; continue; }
            if (VideoPresence.HasOverride(uri)) { overrideOnly++; continue; }
            if (assoc is null) noRow++; else negative++;
            if (sampled < 6 && uri.StartsWith("spotify:track:", StringComparison.Ordinal))
            {
                if (sampled > 0) missSample.Append(',');
                missSample.Append(uri["spotify:track:".Length..]);
                sampled++;
            }
        }
        log.Event(WaveeLogLevel.Info, "detail", "video.assoc.page", "detail-page music-video roll-up computed",
            fields:
            [
                WaveeLogField.Of("kind", kind), WaveeLogField.Of("contextUri", contextUri),
                WaveeLogField.Of("tracks", tracks.Count), WaveeLogField.Of("withVideo", withVideo),
                WaveeLogField.Of("overrideOnly", overrideOnly), WaveeLogField.Of("noRow", noRow),
                WaveeLogField.Of("negative", negative),
                WaveeLogField.Of("missIds", missSample.Length == 0 ? "-" : missSample.ToString()),
            ]);
    }

    static IReadOnlyDictionary<string, Owner>? UserProfileMap(Playlist p)
    {
        var map = new Dictionary<string, Owner>(StringComparer.OrdinalIgnoreCase);
        Add(p.Owner);
        if (p.Collaborators is { Count: > 0 } collaborators)
            for (int i = 0; i < collaborators.Count; i++) Add(collaborators[i]);
        return map.Count == 0 ? null : map;

        void Add(Owner? owner)
        {
            if (owner is null) return;
            if (owner.Id.Length > 0) map[owner.Id] = owner;
            var canonical = UserProfileIds.Normalize(owner.Id);
            if (canonical is not null)
            {
                map[canonical] = owner;
                map[UserProfileIds.BareId(canonical)] = owner;
            }
        }
    }

    static DetailModel MapLiked(IReadOnlyList<Track> tracks)
    {
        string meta = Strings.Detail.MetaLine(Strings.Detail.SongCount(tracks.Count), DetailFormat.TotalTime(DetailFormat.TotalMs(tracks)));
        LogVideoSweep("liked", "spotify:collection:tracks", tracks);
        return new DetailModel(
            Title: Loc.Get(Strings.Detail.LikedSongs), Cover: null, ContextUri: "spotify:collection:tracks",
            BadgeType: null, Year: null, OwnerName: null, OwnerImage: null,
            Artists: Array.Empty<ArtistRef>(), Description: null, MetaLine: meta,
            Tracks: tracks, AboutArtist: null,
            HasDateAdded: tracks.Any(t => t.AddedAt is not null),   // liked rows carry the collection add time → Date-added column + sort
            HasVideo: tracks.Any(VideoPresence.HasVideo));
    }

    // The album model: hero + tracklist + the "More by" shelf the getAlbum payload carries. The below-the-fold
    // enrichment (About-the-artist / Fans-also-like / Featured-on / Merch / Similar) is deliberately NOT awaited here —
    // AlbumTrailing loads each section independently so the hero and track list render immediately and no slow or failed
    // enrichment can block (or sink) them.
    // `link` is the resolved kind-138 pre-release identity, when the loader had reason to ask for one (a full
    // prerelease route, or an album that already looks upcoming). Optional + null by default: every ordinary album open
    // keeps its single request.
    static DetailModel MapAlbum(Album a, PreReleaseLink? link = null)
    {
        var tracks = a.Tracks ?? Array.Empty<Track>();
        string badge = a.Kind switch
        {
            AlbumKind.Single => Loc.Get(Strings.Detail.Badge.Single),
            AlbumKind.EP => Loc.Get(Strings.Detail.Badge.Ep),
            AlbumKind.Compilation => Loc.Get(Strings.Detail.Badge.Compilation),
            _ => Loc.Get(Strings.Detail.Badge.Album),
        };
        string meta = Strings.Detail.MetaLineYear(
            Strings.Detail.SongCount(a.TrackCount), DetailFormat.TotalTime(DetailFormat.TotalMs(tracks)), a.Year);
        LogVideoSweep("album", a.Uri, tracks);
        return new DetailModel(
            Title: a.Name, Cover: a.Cover, ContextUri: a.Uri,
            BadgeType: badge, Year: a.Year.ToString(), OwnerName: null, OwnerImage: null,
            Artists: a.Artists, Description: null, MetaLine: meta,
            Tracks: tracks, AboutArtist: null,
            HasVideo: tracks.Any(VideoPresence.HasVideo), ReleaseKind: a.Kind, MoreByArtist: a.MoreByArtist,
            Label: a.Label, Copyright: a.Copyright, ReleaseDate: FormatReleaseDate(a.ReleaseDate, a.ReleaseDatePrecision), AlbumArtists: a.ArtistsDetailed,
            OtherVersions: a.OtherVersions, CourtesyLine: a.CourtesyLine, ReleaseDatePrecision: a.ReleaseDatePrecision,
            DiscCount: a.DiscCount, ShareUrl: a.ShareUrl, IsPreRelease: a.IsPreRelease, PreReleaseEnd: a.PreReleaseEnd)
        {
            ReleaseInstant = PreReleaseDerivation.ReleaseInstant(a.ReleaseDate),
            UpcomingAt = PreReleaseDerivation.UpcomingAt(a, DateTimeOffset.UtcNow),
            // Only while genuinely ahead of us: a kind-138 link is cached for up to 30 days and must not turn the heart
            // into a "Pre-save" for a record that shipped last week.
            PreReleaseUri = link is { IsUpcoming: true } l ? l.PreReleaseUri : null,
        };
    }

    // ISO date + Spotify precision: YEAR → "2014"; MONTH → "November 2014"; DAY → "November 4, 2014".
    static string? FormatReleaseDate(string? iso, string? precision)
    {
        if (string.IsNullOrWhiteSpace(iso)) return null;
        if (!System.DateTimeOffset.TryParse(iso, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal, out var d)
            ) return iso;
        return (precision ?? "").ToUpperInvariant() switch
        {
            "YEAR" => d.ToString("yyyy", System.Globalization.CultureInfo.InvariantCulture),
            "MONTH" => d.ToString("MMMM yyyy", System.Globalization.CultureInfo.InvariantCulture),
            _ => d.ToString("MMMM d, yyyy", System.Globalization.CultureInfo.InvariantCulture),
        };
    }

    // Delegates to the ONE consolidated converter (Actions/SpotifyLink.cs); keeps this surface's bare-id fallback
    // (a caller passing a raw playlist id — no spotify: prefix — still gets a playlist url).
    internal static string SpotifyPlaylistWebUrl(string uri)
        => SpotifyLink.WebUrl(uri) ?? $"https://open.spotify.com/playlist/{uri}";
}
