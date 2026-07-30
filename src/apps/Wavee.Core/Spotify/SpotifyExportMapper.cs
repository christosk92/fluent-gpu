using System.Globalization;
using System.Text.Json;

namespace Wavee.Core;

/// <summary>The Anti-Corruption Layer for the Spotify GraphQL export (docs/architecture.md §4.4): translates the raw
/// JSON shapes (playlistV2 / libraryV3 / home) into clean domain records. No JsonElement / GraphQL shape escapes this
/// file. All navigation is null-safe so a missing field degrades gracefully rather than throwing.</summary>
public static class SpotifyExportMapper
{
    // The export's owner — used to decide IsOwner on playlists.
    public const string CurrentUser = "Christos";

    /// <summary>Map a LIVE Pathfinder <c>queryArtistOverview</c> response (root document element) → the domain Artist.
    /// The export's <c>artist-*.json</c> files ARE these responses, so this reuses the same <see cref="MapArtist"/>.</summary>
    public static Artist? ArtistFromOverview(JsonElement responseRoot)
    {
        var au = Dig(responseRoot, "data", "artistUnion");
        return au.ValueKind == JsonValueKind.Object ? MapArtist(au) : null;
    }

    /// <summary>Map the thinner <c>queryNpvArtist</c> response used by album "About the artist" cards. This deliberately
    /// reads only fields that NPV owns instead of treating it as a full overview and manufacturing empty rich facets.</summary>
    public static Artist? ArtistFromNpv(JsonElement responseRoot)
    {
        var au = Dig(responseRoot, "data", "artistUnion");
        if (au.ValueKind != JsonValueKind.Object) return null;
        var uri = Str(au, "uri") ?? ("spotify:artist:" + (Str(au, "id") ?? ""));
        if (uri.EndsWith(':')) return null;
        string name = Str(au, "profile", "name") ?? "";
        bool verified = BoolAt(au, false, "onPlatformReputationTrait", "verification", "isVerified")
                     || BoolAt(au, false, "onPlatformReputationTrait", "verification", "isRegistered")
                     || BoolAt(au, false, "profile", "verified");
        return new Artist(
            IdFromUri(uri), uri, name,
            PickImage(Dig(au, "visuals", "avatarImage", "sources")),
            MonthlyListeners: Long(au, "stats", "monthlyListeners"),
            Followers: Long(au, "stats", "followers"),
            Bio: HtmlText(Str(au, "profile", "biography", "text")),
            Verified: verified,
            WorldRank: (int)Long(au, "stats", "worldRank"),
            HeaderImage: PickImage(Dig(au, "visuals", "headerImage", "sources"))
                ?? PickImage(Dig(au, "headerImage", "data", "sources")),
            Extras: new ArtistExtras(
                Merch: MapMerch(Dig(au, "goods", "merch", "items")),
                TopCities: MapTopCities(Dig(au, "stats", "topCities", "items")),
                ExternalLinks: MapLinks(Dig(au, "profile", "externalLinks", "items")),
                Gallery: MapGallery(Dig(au, "visuals", "gallery", "items"))));
    }

    /// <summary>Map a LIVE Pathfinder <c>getAlbum</c> response (data.albumUnion) → the domain Album WITH its tracklist
    /// (tracksV2.items[].track). Cover from coverArt.sources, year from date.isoString.</summary>
    public static Album? AlbumFromUnion(JsonElement responseRoot)
    {
        var au = Dig(responseRoot, "data", "albumUnion");
        if (au.ValueKind != JsonValueKind.Object) return null;
        var uri = Str(au, "uri") ?? "";
        if (uri.Length == 0) return null;
        var name = Str(au, "name") ?? "";
        var cover = PickImage(Dig(au, "coverArt", "sources"));
        int year = YearFromIso(Str(au, "date", "isoString"));
        var kind = (Str(au, "type") ?? "ALBUM").ToUpperInvariant() switch
        {
            "SINGLE" => AlbumKind.Single, "EP" => AlbumKind.EP, "COMPILATION" => AlbumKind.Compilation, _ => AlbumKind.Album,
        };
        var albumArtists = MapUnionArtists(Dig(au, "artists", "items"));
        var albumRef = new AlbumRef(IdFromUri(uri), uri, name);

        var tracks = new List<Track>();
        var items = Dig(au, "tracksV2", "items");
        if (items.ValueKind == JsonValueKind.Array)
            foreach (var it in items.EnumerateArray())
            {
                var t = Dig(it, "track");
                if (t.ValueKind != JsonValueKind.Object) continue;
                var turi = Str(t, "uri");
                if (turi is null) continue;
                var tArtists = MapUnionArtists(Dig(t, "artists", "items"));
                bool explicitFlag = (Str(t, "contentRating", "label") ?? "NONE") != "NONE";
                var playable = PlayabilityOf(t);
                // `associationsV3.videoAssociations` is deliberately NOT read onto the row: has-video is answered by
                // the VideoAssociation plane (kind 99), which every surface already triggers. A second, weaker source
                // of the same fact on the row is what let a list and its own expand drawer disagree.
                tracks.Add(new Track(IdFromUri(turi), turi, Str(t, "name") ?? "",
                    tArtists.Count > 0 ? tArtists : albumArtists, albumRef,
                    Long(t, "duration", "totalMilliseconds"), explicitFlag, cover,
                    PlayCount: Long(t, "playcount"),
                    Availability: playable, Source: "spotify"));
            }

        var moreBy = new List<Album>();
        var artistGroups = Dig(au, "moreAlbumsByArtist", "items");
        if (artistGroups.ValueKind == JsonValueKind.Array)
            foreach (var group in artistGroups.EnumerateArray())
            {
                var releases = Dig(group, "discography", "popularReleasesAlbums", "items");
                if (releases.ValueKind != JsonValueKind.Array) continue;
                foreach (var release in releases.EnumerateArray())
                    if (MapRelease(release) is { } other && other.Uri != uri) moreBy.Add(other);
            }

        var artistsDetailed = MapUnionArtistsDetailed(Dig(au, "artists", "items"));
        string? label = Str(au, "label");
        string? copyright = JoinCopyright(Dig(au, "copyright", "items"));
        string? releaseDate = Str(au, "date", "isoString");
        string? releasePrecision = Str(au, "date", "precision");
        string? courtesyLine = Str(au, "courtesyLine");
        int discCount = Math.Max(1, (int)Long(au, "discs", "totalCount"));
        string? shareUrl = Str(au, "sharingInfo", "shareUrl");
        bool isPreRelease = BoolAt(au, false, "isPreRelease");
        DateTimeOffset? preReleaseEnd = ParseIso(Str(au, "preReleaseEndDateTime"));

        // "Other versions" — the alternate editions of THIS album (releases.items), excluding the album itself.
        var otherVersions = new List<Album>();
        var seenVersions = new HashSet<string>(StringComparer.Ordinal) { uri };
        var releaseItems = Dig(au, "releases", "items");
        if (releaseItems.ValueKind == JsonValueKind.Array)
            foreach (var rel in releaseItems.EnumerateArray())
                if (MapRelease(rel) is { } v && seenVersions.Add(v.Uri)) otherVersions.Add(v);

        return new Album(IdFromUri(uri), uri, name, cover, albumArtists, year, tracks.Count, tracks, kind,
            moreBy.Count > 0 ? moreBy : null, label, copyright, releaseDate,
            artistsDetailed.Count > 0 ? artistsDetailed : null,
            otherVersions.Count > 0 ? otherVersions : null,
            CourtesyLine: courtesyLine, ReleaseDatePrecision: releasePrecision, DiscCount: discCount,
            ShareUrl: shareUrl, IsPreRelease: isPreRelease, PreReleaseEnd: preReleaseEnd,
            Hydration: AlbumHydrationLevel.Full);
    }

    // The album's primary artists WITH avatars (albumUnion.artists.items[].visuals.avatarImage) — for the stacked header.
    static List<Artist> MapUnionArtistsDetailed(JsonElement items)
    {
        var list = new List<Artist>();
        if (items.ValueKind != JsonValueKind.Array) return list;
        foreach (var a in items.EnumerateArray())
        {
            var u = Str(a, "uri");
            var n = Str(a, "profile", "name");
            if (u is null || n is null) continue;
            list.Add(new Artist(IdFromUri(u), u, n, PickImage(Dig(a, "visuals", "avatarImage", "sources"))));
        }
        return list;
    }

    // Join the copyright lines for "About this release", prefixing the symbol from the line's type when absent.
    static string? JoinCopyright(JsonElement items)
    {
        if (items.ValueKind != JsonValueKind.Array) return null;
        var seen = new System.Collections.Generic.HashSet<string>();
        var sb = new System.Text.StringBuilder();
        foreach (var it in items.EnumerateArray())
        {
            var text = Str(it, "text");
            if (string.IsNullOrWhiteSpace(text)) continue;
            string line = NormalizeCopyrightLine(text!, Str(it, "type"));
            if (!seen.Add(line)) continue;
            if (sb.Length > 0) sb.Append('\n');
            sb.Append(line);
        }
        return sb.Length > 0 ? sb.ToString() : null;
    }

    static string NormalizeCopyrightLine(string text, string? type)
    {
        var line = text.Trim();
        line = line.Replace("\u00C2\u00A9", "\u00A9").Replace("\u00E2\u0084\u0097", "\u2117");
        if (line.StartsWith('\u00A9') || line.StartsWith('\u2117')) return line;
        return type switch
        {
            "C" => "\u00A9 " + line,
            "P" => "\u2117 " + line,
            _ => line,
        };
    }

    /// <summary>Map a LIVE Pathfinder <c>similarAlbumsBasedOnThisTrack</c> response → albums
    /// (data.seoRecommendedTrackAlbum.items[].data). Each carries its own artist(s) + cover + year/type.</summary>
    public static IReadOnlyList<Album> SimilarAlbumsFromTrack(JsonElement responseRoot)
    {
        var items = Dig(responseRoot, "data", "seoRecommendedTrackAlbum", "items");
        if (items.ValueKind != JsonValueKind.Array) return System.Array.Empty<Album>();
        var result = new List<Album>();
        foreach (var wrap in items.EnumerateArray())
        {
            var data = Dig(wrap, "data");
            var uri = Str(data, "uri");
            if (uri is null) continue;
            var kind = (Str(data, "type") ?? "ALBUM").ToUpperInvariant() switch
            {
                "SINGLE" => AlbumKind.Single, "EP" => AlbumKind.EP, "COMPILATION" => AlbumKind.Compilation, _ => AlbumKind.Album,
            };
            result.Add(new Album(IdFromUri(uri), uri, Str(data, "name") ?? "", CoverArt(data),
                MapUnionArtists(Dig(data, "artists", "items")), (int)Long(data, "date", "year"), 0, null, kind));
        }
        return result;
    }

    /// <summary>Map a LIVE Pathfinder <c>queryAlbumMerch</c> response → merch products
    /// (data.albumUnion.merch.items[]). Skips an unnamed item (not a renderable card).</summary>
    public static IReadOnlyList<MerchItem> AlbumMerch(JsonElement responseRoot)
    {
        var items = Dig(responseRoot, "data", "albumUnion", "merch", "items");
        if (items.ValueKind != JsonValueKind.Array) return System.Array.Empty<MerchItem>();
        var result = new List<MerchItem>();
        foreach (var item in items.EnumerateArray())
            if (MerchFromItem(item) is { } mi) result.Add(mi);
        return result;
    }

    /// <summary>Map the track-scoped half of <c>queryNpvArtist</c>: credits, sources, Canvas, and track merch.</summary>
    public static TrackNpvInfo? TrackNpvFromResponse(JsonElement responseRoot)
    {
        var tu = Dig(responseRoot, "data", "trackUnion");
        if (tu.ValueKind != JsonValueKind.Object) return null;
        string trackUri = Str(tu, "uri") ?? "";

        var canvasEl = Dig(tu, "canvas");
        TrackCanvas? canvas = canvasEl.ValueKind == JsonValueKind.Object
            ? new TrackCanvas(Str(canvasEl, "fileId"), Str(canvasEl, "type"), Str(canvasEl, "uri"), Str(canvasEl, "url"))
            : null;

        var credits = new List<TrackCredit>();
        foreach (var c in Arr(Dig(tu, "creditsTrait", "contributors", "items")))
            if (Str(c, "name") is { Length: > 0 } n)
                credits.Add(new TrackCredit(n, Str(c, "role") ?? "", Str(c, "roleGroup", "name"),
                    Str(c, "uri"), Linkable: (Str(c, "uri") ?? "").Length > 0));

        if (credits.Count == 0)
            foreach (var c in Arr(Dig(tu, "credits")))
                if (Str(c, "artistName") is { Length: > 0 } n)
                    credits.Add(new TrackCredit(n, Str(c, "role") ?? "", null,
                        Str(c, "artistUri"), BoolAt(c, false, "isArtistUriLinkable")));

        var sources = new List<string>();
        foreach (var s in Arr(Dig(tu, "creditsTrait", "sources", "items")))
            if (Str(s, "name") is { Length: > 0 } sn) sources.Add(sn);

        var merch = new List<MerchItem>();
        foreach (var item in Arr(Dig(tu, "merch", "items")))
            if (MerchFromItem(item) is { } mi) merch.Add(mi);

        return new TrackNpvInfo(trackUri, credits, sources, canvas, merch);
    }

    static MerchItem? MerchFromItem(JsonElement item)
    {
        string name = Str(item, "nameV2") ?? Str(item, "name") ?? "";
        if (name.Length == 0) return null;
        return new MerchItem(name, Str(item, "price") ?? "", HtmlText(Str(item, "description")),
            PickImage(Dig(item, "image", "sources")) ?? PickImage(Dig(item, "image", "data", "sources")),
            Str(item, "url"));
    }

    /// <summary>Map a LIVE Pathfinder <c>getTrack</c> response → a playable track row with album cover art.</summary>
    public static Track? TrackFromUnion(JsonElement responseRoot)
    {
        var data = Dig(responseRoot, "data", "trackUnion");
        if (data.ValueKind != JsonValueKind.Object) return null;
        var uri = Str(data, "uri");
        if (string.IsNullOrEmpty(uri)) return null;

        var artists = MapUnionArtists(Dig(data, "artists", "items"));
        if (artists.Count == 0) artists = MapUnionArtists(Dig(data, "firstArtist", "items"));

        var album = Dig(data, "albumOfTrack");
        var albumUri = Str(album, "uri") ?? "";
        var albumRef = new AlbumRef(IdFromUri(albumUri), albumUri, Str(album, "name") ?? "");
        var image = CoverArt(album) ?? CoverArt(data);

        long dur = LongAt(data, "trackDuration", "totalMilliseconds");
        if (dur == 0) dur = LongAt(data, "duration", "totalMilliseconds");
        long plays = LongAt(data, "playcount");
        bool explicitFlag = (Str(data, "contentRating", "label") ?? "NONE") != "NONE";
        var playable = PlayabilityOf(data);

        return new Track(
            IdFromUri(uri), uri, Str(data, "name") ?? "", artists, albumRef,
            dur, explicitFlag, image, PlayCount: plays,
            Origin: TrackOrigin.Streamed,
            Availability: playable,
            Source: "spotify");
    }

    /// <summary>Map a LIVE Pathfinder <c>getTrack</c> response → the short-release track context: whether the track
    /// carries a music video, plus the lead artist's related artists
    /// (data.trackUnion.{associationsV3.videoAssociations, firstArtist.items[0].relatedContent.relatedArtists}).</summary>
    public static AlbumTrackContext TrackContextFromUnion(JsonElement responseRoot)
    {
        var union = Dig(responseRoot, "data", "trackUnion");
        if (union.ValueKind != JsonValueKind.Object) return AlbumTrackContext.Empty;
        bool hasVideo = Long(union, "associationsV3", "videoAssociations", "totalCount") > 0;
        var related = new List<Artist>();
        var items = Dig(union, "firstArtist", "items");
        if (items.ValueKind == JsonValueKind.Array && items.GetArrayLength() > 0)
        {
            var rel = Dig(items[0], "relatedContent", "relatedArtists", "items");
            if (rel.ValueKind == JsonValueKind.Array)
                foreach (var item in rel.EnumerateArray())
                {
                    if (related.Count >= 8) break;
                    var uri = Str(item, "uri");
                    var name = Str(item, "profile", "name");
                    if (uri is null || name is null) continue;
                    related.Add(new Artist(IdFromUri(uri), uri, name, PickImage(Dig(item, "visuals", "avatarImage", "sources"))));
                }
        }
        return new AlbumTrackContext(hasVideo, related);
    }

    static List<ArtistRef> MapUnionArtists(JsonElement items)
    {
        var list = new List<ArtistRef>();
        if (items.ValueKind != JsonValueKind.Array) return list;
        foreach (var a in items.EnumerateArray())
        {
            var u = Str(a, "uri");
            var n = Str(a, "profile", "name");
            if (u is not null && n is not null) list.Add(new ArtistRef(IdFromUri(u), u, n));
        }
        return list;
    }

    static int YearFromIso(string? iso)
        => iso is { Length: >= 4 } && int.TryParse(iso.AsSpan(0, 4), out var y) ? y : 0;

    /// <summary>Map a LIVE Pathfinder <c>searchTopResultsList</c> response (data.searchV2) → the domain SearchResults.
    /// Per facet: tracksV2.items[].item.data (tracks carry an extra item wrapper); albumsV2/artists/playlists.items[].data.</summary>
    public static SearchResults SearchFromV2(JsonElement responseRoot)
    {
        var sv = Dig(responseRoot, "data", "searchV2");

        var tracks = new List<Track>();
        foreach (var it in Arr(Dig(sv, "tracksV2", "items")))
        {
            var d = it.TryGetProperty("item", out var item) ? Dig(item, "data") : Dig(it, "data");
            if (Str(d, "uri") is not { } uri) continue;
            var alb = Dig(d, "albumOfTrack");
            tracks.Add(new Track(IdFromUri(uri), uri, Str(d, "name") ?? "",
                MapUnionArtists(Dig(d, "artists", "items")),
                new AlbumRef(IdFromUri(Str(alb, "uri") ?? ""), Str(alb, "uri") ?? "", Str(alb, "name") ?? ""),
                Long(d, "duration", "totalMilliseconds"), Str(d, "contentRating", "label") == "EXPLICIT",
                PickImage(Dig(alb, "coverArt", "sources"))));
        }

        var albums = new List<Album>();
        foreach (var it in Arr(Dig(sv, "albumsV2", "items")))
        {
            var d = Dig(it, "data");
            if (Str(d, "uri") is not { } uri) continue;
            albums.Add(new Album(IdFromUri(uri), uri, Str(d, "name") ?? "", PickImage(Dig(d, "coverArt", "sources")),
                MapUnionArtists(Dig(d, "artists", "items")), (int)Long(d, "date", "year"), 0));
        }

        var artists = new List<Artist>();
        foreach (var it in Arr(Dig(sv, "artists", "items")))
        {
            var d = Dig(it, "data");
            if (Str(d, "uri") is not { } uri) continue;
            artists.Add(new Artist(IdFromUri(uri), uri, Str(d, "profile", "name") ?? "",
                PickImage(Dig(d, "visuals", "avatarImage", "sources"))));
        }

        var playlists = new List<Playlist>();
        foreach (var it in Arr(Dig(sv, "playlists", "items")))
        {
            var d = Dig(it, "data");
            if (Str(d, "uri") is not { } uri) continue;
            var imgs = Dig(d, "images", "items");
            Image? cover = imgs.ValueKind == JsonValueKind.Array && imgs.GetArrayLength() > 0 ? PickImage(Dig(imgs[0], "sources")) : null;
            playlists.Add(new Playlist(IdFromUri(uri), uri, Str(d, "name") ?? "", HtmlText(Str(d, "description")),
                Str(d, "ownerV2", "data", "name") ?? "", cover, 0));
        }

        // ── podcasts / audiobooks / episodes / profiles ──────────────────────────────────────────────────────────────
        // Each facet op fills exactly ONE of these collections and leaves the rest absent, so this single mapper serves
        // every facet response without the caller having to know which one it asked for.
        List<Show>? shows = null;
        foreach (var it in Arr(Dig(sv, "podcasts", "items")))
        {
            var d = Dig(it, "data");
            if (Str(d, "uri") is not { } uri || IsNotFound(d)) continue;
            (shows ??= new List<Show>()).Add(new Show(IdFromUri(uri), uri, Str(d, "name") ?? "",
                Str(d, "publisher", "name") ?? "", PickImage(Dig(d, "coverArt", "sources"))));
        }

        List<Episode>? episodes = null;
        foreach (var it in Arr(Dig(sv, "episodes", "items")))
        {
            var d = Dig(it, "data");
            if (Str(d, "uri") is not { } uri || IsNotFound(d)) continue;
            // The episode's own art is the SHOW's cover — the episode node itself carries none.
            var showData = Dig(d, "podcastV2", "data");
            (episodes ??= new List<Episode>()).Add(new Episode(IdFromUri(uri), uri, Str(d, "name") ?? "",
                Str(showData, "name") ?? "", PickImage(Dig(showData, "coverArt", "sources")),
                DurationMs: 0, PublishedAt: default, Description: HtmlText(Str(d, "description"))));
        }

        List<SearchTopHit>? audiobooks = null;
        foreach (var it in Arr(Dig(sv, "audiobooks", "items")))
        {
            var d = Dig(it, "data");
            if (Str(d, "uri") is not { } uri || IsNotFound(d)) continue;
            // authorsV2 is a bare ARRAY of {name,uri} — not the usual {items:[{data:…}]} envelope.
            string authors = JoinNames(Dig(d, "authorsV2"));
            (audiobooks ??= new List<SearchTopHit>()).Add(new SearchTopHit(
                SearchHitKind.Audiobook, uri, Str(d, "name") ?? "", authors, "Audiobook",
                PickImage(Dig(d, "coverArt", "sources")), RoundImage: false, Followable: false, MatchedLyrics: false,
                AccessLabel: Str(d, "accessInfo", "signifier", "text"),
                Detail: HtmlText(Str(d, "description"))));
        }

        List<SearchTopHit>? profiles = null;
        foreach (var it in Arr(Dig(sv, "users", "items")))
        {
            var d = Dig(it, "data");
            if (Str(d, "uri") is not { } uri || IsNotFound(d)) continue;
            // The user node carries no avatar — the card falls back to PersonPicture initials from the display name.
            (profiles ??= new List<SearchTopHit>()).Add(new SearchTopHit(
                SearchHitKind.User, uri, Str(d, "displayName") ?? Str(d, "username") ?? "", "", "Profile",
                Image: null, RoundImage: true, Followable: true, MatchedLyrics: false, AccessLabel: null));
        }

        return new SearchResults(tracks, albums, artists, playlists,
            TracksTotal: TotalCount(sv, "tracksV2"),
            AlbumsTotal: TotalCount(sv, "albumsV2"),
            ArtistsTotal: TotalCount(sv, "artists"),
            PlaylistsTotal: TotalCount(sv, "playlists"),
            Shows: shows, ShowsTotal: TotalCount(sv, "podcasts"),
            Episodes: episodes, EpisodesTotal: TotalCount(sv, "episodes"),
            Audiobooks: audiobooks, AudiobooksTotal: TotalCount(sv, "audiobooks"),
            Profiles: profiles, ProfilesTotal: TotalCount(sv, "users"));
    }

    /// <summary>A search result whose entity no longer resolves comes back as a wrapper with
    /// <c>__typename: "NotFound"</c> and nothing else — observed on searchAuthors, and mixed in among real items on
    /// browse sections. Rendering one produces a blank, unclickable card, so every list skips them.</summary>
    static bool IsNotFound(JsonElement d) =>
        string.Equals(Str(d, "__typename"), "NotFound", StringComparison.Ordinal);

    /// <summary>Comma-joins a bare array of <c>{name,uri}</c> objects (audiobook <c>authorsV2</c>). Returns "" when the
    /// array is absent or every entry is nameless.</summary>
    static string JoinNames(JsonElement arr)
    {
        if (arr.ValueKind != JsonValueKind.Array) return "";
        var sb = new System.Text.StringBuilder();
        foreach (var a in arr.EnumerateArray())
        {
            if (Str(a, "name") is not { Length: > 0 } n) continue;
            if (sb.Length > 0) sb.Append(", ");
            sb.Append(n);
        }
        return sb.ToString();
    }

    /// <summary>Map a LIVE Pathfinder <c>searchTopResultsList</c> response → the ordered unified "All"-tab hits
    /// (topResultsV2.itemsV2). Server order preserved (the FIRST item is the Top Result); each hit keeps its type, a
    /// "LYRICS" lyric-match flag, and an audiobook access signifier ("Included in Premium").</summary>
    public static IReadOnlyList<SearchTopHit> TopHitsFromV2(JsonElement responseRoot)
    {
        var hits = new List<SearchTopHit>();
        foreach (var it in Arr(Dig(responseRoot, "data", "searchV2", "topResultsV2", "itemsV2")))
        {
            var wrapper = it.TryGetProperty("item", out var item) ? item : it;
            bool lyrics = HasMatchedField(it, "LYRICS") || HasMatchedField(wrapper, "LYRICS");
            var data = Dig(wrapper, "data");
            var d = data.ValueKind == JsonValueKind.Object ? data : wrapper;
            var type = TopHitType(Str(wrapper, "__typename"), Str(d, "__typename"), Str(d, "uri"));
            if (MapTopHit(type, d, lyrics) is { } hit) hits.Add(hit);
        }
        return hits;
    }

    static bool HasMatchedField(JsonElement hit, string field)
    {
        if (!hit.TryGetProperty("matchedFields", out var mf) || mf.ValueKind != JsonValueKind.Array) return false;
        foreach (var f in mf.EnumerateArray())
            if (f.ValueKind == JsonValueKind.String && string.Equals(f.GetString(), field, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    static int TotalCount(JsonElement searchV2, string facet)
        => (int)Long(searchV2, facet, "totalCount");

    static string TopHitType(string? wrapperType, string? dataType, string? uri)
    {
        static string Normalize(string? value)
        {
            if (string.IsNullOrEmpty(value)) return "";
            if (value.Contains("Track", StringComparison.OrdinalIgnoreCase)) return "Track";
            if (value.Contains("Artist", StringComparison.OrdinalIgnoreCase)) return "Artist";
            if (value.Contains("Album", StringComparison.OrdinalIgnoreCase)) return "Album";
            if (value.Contains("Playlist", StringComparison.OrdinalIgnoreCase)) return "Playlist";
            if (value.Contains("Audiobook", StringComparison.OrdinalIgnoreCase)) return "Audiobook";
            if (value.Contains("Podcast", StringComparison.OrdinalIgnoreCase) || value.Contains("Show", StringComparison.OrdinalIgnoreCase)) return "Podcast";
            if (value.Contains("Episode", StringComparison.OrdinalIgnoreCase)) return "Episode";
            return "";
        }
        var type = Normalize(dataType);
        if (type.Length == 0) type = Normalize(wrapperType);
        if (type.Length > 0) return type;
        if (uri is not null)
        {
            if (uri.StartsWith("spotify:track:", StringComparison.Ordinal)) return "Track";
            if (uri.StartsWith("spotify:artist:", StringComparison.Ordinal)) return "Artist";
            if (uri.StartsWith("spotify:album:", StringComparison.Ordinal)) return "Album";
            if (uri.StartsWith("spotify:playlist:", StringComparison.Ordinal)) return "Playlist";
            if (uri.StartsWith("spotify:audiobook:", StringComparison.Ordinal)) return "Audiobook";
            if (uri.StartsWith("spotify:show:", StringComparison.Ordinal)) return "Podcast";
            if (uri.StartsWith("spotify:episode:", StringComparison.Ordinal)) return "Episode";
        }
        return "";
    }

    static SearchTopHit? MapTopHit(string type, JsonElement d, bool lyrics)
    {
        if (Str(d, "uri") is not { } uri) return null;
        switch (type)
        {
            case "Track":
            {
                string label = string.Equals(Str(d, "trackMediaType"), "VIDEO", StringComparison.OrdinalIgnoreCase) ? "Music video" : "Song";
                return new SearchTopHit(SearchHitKind.Track, uri, Str(d, "name") ?? "", label + " • " + ArtistLinks(Dig(d, "artists", "items")), label,
                    PickImage(Dig(d, "albumOfTrack", "coverArt", "sources")), false, false, lyrics, null);
            }
            case "Artist":
                return new SearchTopHit(SearchHitKind.Artist, uri, Str(d, "profile", "name") ?? "", "Artist", "Artist",
                    PickImage(Dig(d, "visuals", "avatarImage", "sources")), true, true, lyrics, null);
            case "Album":
                return new SearchTopHit(SearchHitKind.Album, uri, Str(d, "name") ?? "", "Album • " + ArtistLinks(Dig(d, "artists", "items")), "Album",
                    PickImage(Dig(d, "coverArt", "sources")), false, false, lyrics, null);
            case "Playlist":
            {
                var imgs = Dig(d, "images", "items");
                Image? cover = imgs.ValueKind == JsonValueKind.Array && imgs.GetArrayLength() > 0 ? PickImage(Dig(imgs[0], "sources")) : null;
                return new SearchTopHit(SearchHitKind.Playlist, uri, Str(d, "name") ?? "", "Playlist • " + Esc(Str(d, "ownerV2", "data", "name")), "Playlist",
                    cover, false, false, lyrics, null);
            }
            case "Audiobook":
                return new SearchTopHit(SearchHitKind.Audiobook, uri, Str(d, "name") ?? "", "Audiobook • " + Esc(AuthorName(Dig(d, "authorsV2"))), "Audiobook",
                    PickImage(Dig(d, "coverArt", "sources")), false, false, lyrics,
                    Str(d, "accessInfo", "signifier", "text"), AudiobookDetail(d), AudiobookMeta(d));
            case "Podcast":
                return new SearchTopHit(SearchHitKind.Podcast, uri, Str(d, "name") ?? "", "Podcast • " + Esc(PublisherName(d)), "Podcast",
                    PickImage(Dig(d, "coverArt", "sources")), false, false, lyrics, null);
            case "Episode":
                return new SearchTopHit(SearchHitKind.Episode, uri, Str(d, "name") ?? "", "Episode • " + Esc(EpisodeShowName(d)), "Episode",
                    PickImage(Dig(d, "coverArt", "sources")) ?? PickImage(Dig(d, "podcastV2", "data", "coverArt", "sources")), false, false, lyrics, null);
            default:
                return null;   // Author/User: not surfaced in the All hero list
        }
    }

    // Artist names as an HTML fragment with <a href="uri"> links, so each artist in a row subtitle is individually clickable
    // (RichText routes spotify:artist:… via RouteForUri). Names + uris are HTML-escaped; a uri-less artist renders as text.
    static string ArtistLinks(JsonElement items)
    {
        var refs = MapUnionArtists(items);
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < refs.Count && i < 3; i++)
        {
            if (i > 0) sb.Append(", ");
            var name = Esc(refs[i].Name);
            if (!string.IsNullOrEmpty(refs[i].Uri)) sb.Append("<a href=\"").Append(Esc(refs[i].Uri)).Append("\">").Append(name).Append("</a>");
            else sb.Append(name);
        }
        return sb.ToString();
    }

    // Minimal HTML-escape for dynamic text/attribute values placed into a RichText subtitle fragment ('&' FIRST so we
    // don't double-escape the entities we just introduced).
    static string Esc(string? s) => string.IsNullOrEmpty(s) ? "" : s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    static string AuthorName(JsonElement authors)
    {
        var items = Dig(authors, "items");
        foreach (var a in Arr(items.ValueKind == JsonValueKind.Array ? items : authors))
            return Str(a, "name") ?? Str(a, "data", "name") ?? "";
        return "";
    }

    static string PublisherName(JsonElement d)
        => Str(d, "publisher", "name") ?? Str(d, "publisherName") ?? Str(d, "publisher") ?? "";

    // The audiobook blurb Spotify renders under the subtitle. The richest single field is the (HTML) description, so prefer
    // it — strip tags, collapse whitespace, decode entities. Best-effort: field names in the searchTopResultsList audiobook
    // entity vary, so this returns null (→ no line) when none of the candidates are present rather than guessing.
    static string? AudiobookDetail(JsonElement d)
    {
        var raw = Str(d, "htmlDescription") ?? Str(d, "description");
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var sb = new System.Text.StringBuilder(raw!.Length);
        bool inTag = false, lastSpace = false;
        foreach (char c in raw!)
        {
            if (c == '<') { inTag = true; continue; }
            if (c == '>') { inTag = false; continue; }
            if (inTag) continue;
            if (char.IsWhiteSpace(c)) { if (!lastSpace && sb.Length > 0) { sb.Append(' '); lastSpace = true; } continue; }
            sb.Append(c); lastSpace = false;
        }
        var plain = HtmlText(sb.ToString())?.Trim();
        return string.IsNullOrEmpty(plain) ? null : plain;
    }

    static string? AudiobookMeta(JsonElement d)
    {
        string? date = FormatSpotifyDate(
            Str(d, "publishDate", "isoString") ?? Str(d, "date", "isoString"),
            Str(d, "publishDate", "precision") ?? Str(d, "date", "precision"));
        string? duration = FormatDuration(
            Long(d, "audiobookDuration", "totalMilliseconds") is { } audiobookDuration && audiobookDuration > 0
                ? audiobookDuration
                : Long(d, "duration", "totalMilliseconds"));

        return date is { Length: > 0 } && duration is { Length: > 0 } ? date + " • " + duration
             : date is { Length: > 0 } ? date
             : duration;
    }

    static string? FormatSpotifyDate(string? iso, string? precision)
    {
        if (string.IsNullOrWhiteSpace(iso)) return null;
        if (!DateTimeOffset.TryParse(iso, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date))
            return iso.Length >= 4 ? iso[..4] : null;

        return (precision ?? "").ToUpperInvariant() switch
        {
            "YEAR" => date.ToString("yyyy", CultureInfo.InvariantCulture),
            "MONTH" => date.ToString("MMM yyyy", CultureInfo.InvariantCulture),
            _ => date.ToString("MMM d, yyyy", CultureInfo.InvariantCulture),
        };
    }

    static string? FormatDuration(long milliseconds)
    {
        if (milliseconds <= 0) return null;
        long minutes = Math.Max(1, (long)Math.Round(TimeSpan.FromMilliseconds(milliseconds).TotalMinutes));
        long hours = minutes / 60;
        minutes %= 60;
        if (hours <= 0) return minutes + " min";
        if (minutes == 0) return hours + " hr";
        return hours + " hr " + minutes + " min";
    }

    static string EpisodeShowName(JsonElement d)
        => Str(d, "podcastV2", "data", "name") ?? Str(d, "show", "name") ?? Str(d, "podcast", "name") ?? "";

    /// <summary>Map a LIVE Pathfinder <c>searchSuggestions</c> response → the omnibar's as-you-type suggestion strings:
    /// the autocomplete entities (data.searchV2.topResultsV2.itemsV2[].item.data.text) plus top entity names, deduped.</summary>
    public static IReadOnlyList<string> SuggestFromV2(JsonElement responseRoot)
    {
        return SuggestionsFromV2(responseRoot).Queries;
    }

    /// <summary>Map a LIVE Pathfinder <c>searchSuggestions</c> response into autocomplete queries plus rich typed hits.</summary>
    public static SearchSuggestions SuggestionsFromV2(JsonElement responseRoot)
    {
        var queries = new List<string>();
        var items = new List<SearchSuggestionItem>();
        var seenQueries = new System.Collections.Generic.HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
        var seenItems = new System.Collections.Generic.HashSet<string>(System.StringComparer.OrdinalIgnoreCase);

        foreach (var hit in Arr(Dig(responseRoot, "data", "searchV2", "topResultsV2", "itemsV2")))
        {
            var wrapper = hit.TryGetProperty("item", out var item) ? item : hit;
            var data = Dig(wrapper, "data");
            var itemType = Str(wrapper, "__typename") ?? Str(data, "__typename") ?? "";

            if (Str(data, "text") is { Length: > 0 } query)
            {
                if (seenQueries.Add(query)) queries.Add(query);
                continue;
            }

            if (TryMapSuggestionItem(itemType, data) is { } rich && seenItems.Add(rich.Uri))
                items.Add(rich);

            if (queries.Count >= 8 && items.Count >= 16) break;
        }

        return queries.Count == 0 && items.Count == 0
            ? SearchSuggestions.Empty
            : new SearchSuggestions(queries, items);
    }

    static SearchSuggestionItem? TryMapSuggestionItem(string itemType, JsonElement data)
    {
        var dataType = Str(data, "__typename") ?? "";
        if (itemType.Contains("Track", StringComparison.OrdinalIgnoreCase) || dataType == "Track")
        {
            var uri = Str(data, "uri");
            if (uri is null) return null;
            var artists = MapUnionArtists(Dig(data, "artists", "items"));
            return new SearchSuggestionItem(SearchSuggestionKind.Track, uri, Str(data, "name") ?? "",
                JoinNames("Song", artists), PickImage(Dig(data, "albumOfTrack", "coverArt", "sources")),
                Str(data, "contentRating", "label") == "EXPLICIT");
        }

        if (itemType.Contains("Artist", StringComparison.OrdinalIgnoreCase) || dataType == "Artist")
        {
            var uri = Str(data, "uri");
            if (uri is null) return null;
            return new SearchSuggestionItem(SearchSuggestionKind.Artist, uri, Str(data, "profile", "name") ?? "",
                "Artist", PickImage(Dig(data, "visuals", "avatarImage", "sources")));
        }

        if (itemType.Contains("Album", StringComparison.OrdinalIgnoreCase) || dataType == "Album")
        {
            var uri = Str(data, "uri");
            if (uri is null) return null;
            var artists = MapUnionArtists(Dig(data, "artists", "items"));
            var type = TitleCase((Str(data, "type") ?? "Album").Replace('_', ' '));
            return new SearchSuggestionItem(SearchSuggestionKind.Album, uri, Str(data, "name") ?? "",
                JoinNames(type, artists), PickImage(Dig(data, "coverArt", "sources")));
        }

        if (itemType.Contains("Playlist", StringComparison.OrdinalIgnoreCase) || dataType == "Playlist")
        {
            var uri = Str(data, "uri");
            if (uri is null) return null;
            return new SearchSuggestionItem(SearchSuggestionKind.Playlist, uri, Str(data, "name") ?? "",
                Str(data, "ownerV2", "data", "name") ?? "Playlist", ImagesCover(data));
        }

        return null;
    }

    static string JoinNames(string prefix, IReadOnlyList<ArtistRef> artists)
    {
        if (artists.Count == 0) return prefix;
        var sb = new System.Text.StringBuilder(prefix);
        sb.Append(" - ");
        for (int i = 0; i < artists.Count && i < 3; i++)
        {
            if (i > 0) sb.Append(", ");
            sb.Append(artists[i].Name);
        }
        if (artists.Count > 3) sb.Append(", ...");
        return sb.ToString();
    }

    // Public so the sibling browse mapper can share the SAME null-safe array walk instead of re-implementing it.
    public static System.Collections.Generic.IEnumerable<JsonElement> Arr(JsonElement e)
        => e.ValueKind == JsonValueKind.Array ? e.EnumerateArray() : System.Linq.Enumerable.Empty<JsonElement>();

    // ── safe JSON navigation ───────────────────────────────────────────────────────────────────────────────
    public static JsonElement Dig(JsonElement e, params string[] path)
    {
        foreach (var p in path)
        {
            if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(p, out e)) return default;
        }
        return e;
    }

    /// <summary>Public safe string read at a path (Undefined/non-string → null).</summary>
    public static string? Str(JsonElement e, params string[] path) => StrAt(e, path);

    /// <summary>Public safe long read at a path (number or numeric string → value; else 0).</summary>
    public static long Long(JsonElement e, params string[] path) => LongAt(e, path);

    static string? StrAt(JsonElement e, params string[] path)
    {
        var x = Dig(e, path);
        return x.ValueKind == JsonValueKind.String ? x.GetString() : null;
    }

    static bool BoolAt(JsonElement e, bool fallback, params string[] path)
    {
        var x = Dig(e, path);
        return x.ValueKind == JsonValueKind.True ? true : x.ValueKind == JsonValueKind.False ? false : fallback;
    }

    /// <summary>The playability verdict, or null when the response did not state one.
    ///
    /// Distinguishing "absent" from "playable" is the whole point: only getAlbum/getTrack carry
    /// <c>playability.playable</c>, so a default of true meant every other write asserted a verdict it never received —
    /// and an unreleased track on a partly-released album looked exactly like a normal one.</summary>
    static Availability? PlayabilityOf(JsonElement e)
    {
        var x = Dig(e, "playability", "playable");
        return x.ValueKind switch
        {
            JsonValueKind.True => Availability.Playable,
            JsonValueKind.False => Availability.Unavailable,
            _ => null,
        };
    }

    static long LongAt(JsonElement e, params string[] path)
    {
        var x = Dig(e, path);
        if (x.ValueKind == JsonValueKind.Number) return x.GetInt64();
        if (x.ValueKind == JsonValueKind.String && long.TryParse(x.GetString(), out var v)) return v;
        return 0;
    }

    // ── identity / hashing ─────────────────────────────────────────────────────────────────────────────────
    /// <summary>Decode HTML character references in Spotify free text — bios and descriptions arrive HTML-encoded
    /// (<c>&amp;#39;</c> → an apostrophe, <c>&amp;#x1f90d;</c> → an emoji). A no-op for plain text.</summary>
    public static string? HtmlText(string? s) => string.IsNullOrEmpty(s) ? s : System.Net.WebUtility.HtmlDecode(s);

    /// <summary>The trailing id of a `spotify:kind:id` uri (base-62; never parse "trailing digits").</summary>
    public static string IdFromUri(string uri) { int i = uri.LastIndexOf(':'); return i >= 0 ? uri[(i + 1)..] : uri; }

    /// <summary>A stable non-negative hash of a uri — seeds deterministic synthesized tracks for real-but-trackless items.</summary>
    public static int Hash(string s) { unchecked { int h = 17; foreach (char c in s) h = h * 31 + c; return h & 0x7fffffff; } }

    /// <summary>A plausible, stable track count for a real playlist we have no track data for (12–51).</summary>
    public static int SynthCount(string uri) => 12 + Hash(uri) % 40;

    // ── images ─────────────────────────────────────────────────────────────────────────────────────────────
    /// <summary>Pick the largest-width source url from a `sources` array → an Image (remote CDN url; the engine fetches+caches it).</summary>
    public static Image? PickImage(JsonElement sources)
    {
        if (sources.ValueKind != JsonValueKind.Array) return null;
        string? best = null; int bestW = -1, w = 0, h = 0;
        foreach (var s in sources.EnumerateArray())
        {
            var url = StrAt(s, "url");
            if (url is null) continue;
            int sw = Num(s, "width", "maxWidth");      // coverArt uses width/height; headerImage/visuals use maxWidth/maxHeight
            int sh = Num(s, "height", "maxHeight");
            if (best is null || sw > bestW) { best = url; bestW = sw; w = sw; h = sh; }
        }
        return best is null ? null : new Image(best, w > 0 ? w : null, h > 0 ? h : null);
    }

    static int Num(JsonElement e, string a, string b)
    {
        if (e.TryGetProperty(a, out var v) && v.ValueKind == JsonValueKind.Number) return v.GetInt32();
        if (e.TryGetProperty(b, out var v2) && v2.ValueKind == JsonValueKind.Number) return v2.GetInt32();
        return 0;
    }

    static DateTimeOffset? ParseIso(string? value)
        => DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed
            : null;

    /// <summary>`images.items[0].sources[]` → cover (playlist / show shape).</summary>
    public static Image? ImagesCover(JsonElement data)
    {
        var items = Dig(data, "images", "items");
        return items.ValueKind == JsonValueKind.Array && items.GetArrayLength() > 0
            ? PickImage(Dig(items[0], "sources")) : null;
    }

    /// <summary>`coverArt.sources[]` → cover (album / track shape).</summary>
    public static Image? CoverArt(JsonElement data) => PickImage(Dig(data, "coverArt", "sources"));

    // ── tracks (playlistV2 content.items[]) ────────────────────────────────────────────────────────────────
    public static Track? MapTrack(JsonElement item)
    {
        var data = Dig(item, "itemV2", "data");
        if (data.ValueKind != JsonValueKind.Object) return null;
        var uri = StrAt(data, "uri");
        if (uri is null) return null;

        var artists = new List<ArtistRef>();
        var artItems = Dig(data, "artists", "items");
        if (artItems.ValueKind == JsonValueKind.Array)
            foreach (var a in artItems.EnumerateArray())
            {
                var auri = StrAt(a, "uri") ?? "";
                var name = StrAt(a, "profile", "name") ?? "";
                if (name.Length > 0) artists.Add(new ArtistRef(IdFromUri(auri), auri, name));
            }

        var album = Dig(data, "albumOfTrack");
        var albumUri = StrAt(album, "uri") ?? "";
        var albumRef = new AlbumRef(IdFromUri(albumUri), albumUri, StrAt(album, "name") ?? "");
        var image = CoverArt(album);

        long dur = LongAt(data, "trackDuration", "totalMilliseconds");
        long plays = LongAt(data, "playcount");
        bool explicitFlag = (StrAt(data, "contentRating", "label") ?? "NONE") != "NONE";
        var playable = PlayabilityOf(data);

        DateTimeOffset? addedAt = null;
        var iso = StrAt(item, "addedAt", "isoString");
        if (iso is not null && DateTimeOffset.TryParse(iso, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dt)) addedAt = dt;
        var addedBy = StrAt(item, "addedBy", "data", "name");

        return new Track(
            IdFromUri(uri), uri, StrAt(data, "name") ?? "", artists, albumRef,
            dur, explicitFlag, image, addedAt, addedBy, PlayCount: plays,
            Origin: TrackOrigin.Streamed,
            Availability: playable,
            Source: "spotify");
    }

    // ── playlist header (libraryV3 item.data) ──────────────────────────────────────────────────────────────
    /// <summary>Map a libraryV3 Playlist node → a domain <see cref="Playlist"/> header (Tracks empty — the source
    /// streams them). <paramref name="trackCount"/> is supplied by the caller (real for Iced, synth otherwise).</summary>
    public static Playlist MapPlaylistHeader(JsonElement data, int trackCount, IReadOnlyList<Track>? tracks = null)
    {
        var uri = StrAt(data, "uri") ?? StrAt(data, "_uri") ?? "";
        var ownerName = StrAt(data, "ownerV2", "data", "name") ?? "Spotify";
        var ownerUri = StrAt(data, "ownerV2", "data", "uri") ?? "";
        Image? ownerAvatar = PickImage(Dig(data, "ownerV2", "data", "avatar", "sources"));
        var owner = new Owner(IdFromUri(ownerUri), ownerName, ownerAvatar);

        bool canEdit = BoolAt(data, false, "currentUserCapabilities", "canEditItems");
        bool canView = BoolAt(data, true, "currentUserCapabilities", "canView");
        bool isOwner = string.Equals(ownerName, CurrentUser, StringComparison.OrdinalIgnoreCase);
        var caps = new PlaylistCapabilities(canView, canEdit, CanEditMetadata: isOwner, IsCollaborative: false, IsOwner: isOwner);

        // Cover-extracted page accent: the detail (playlistV2) node carries a rich extractedColorSet on its square
        // cover; the library (libraryV3) node carries the simpler colorDark on its first image. Prefer the rich set;
        // null (missing/fallback) leaves the page on its neutral default.
        var imgItems = Dig(data, "images", "items");
        var firstImg = imgItems.ValueKind == JsonValueKind.Array && imgItems.GetArrayLength() > 0 ? imgItems[0] : default;

        return new Playlist(
            IdFromUri(uri), uri, StrAt(data, "name") ?? "", HtmlText(StrAt(data, "description")), ownerName,
            ImagesCover(data), trackCount, tracks ?? System.Array.Empty<Track>(),
            owner, caps, StrAt(data, "format"), Source: "spotify");
    }

    // ── home cards (an entity inside a section item: Album / Playlist / Artist) ─────────────────────────────
    public static HomeCard? CardFromEntity(JsonElement data)
    {
        var typename = StrAt(data, "__typename");
        var uri = StrAt(data, "uri");
        if (uri is null) return null;
        var name = StrAt(data, "name") ?? "";
        switch (typename)
        {
            case "Album":
                return new HomeCard(uri, name, FirstArtistName(data), CoverArt(data) ?? EntityImage(data), HomeCardKind.Album);
            case "Playlist":
                return new HomeCard(uri, name, HtmlText(StrAt(data, "description")) ?? StrAt(data, "ownerV2", "data", "name"),
                    ImagesCover(data) ?? EntityImage(data), HomeCardKind.Playlist);
            case "Artist":
                // Artist entities expose their display name under profile.name (albums/playlists use top-level name).
                // Reading only data.name produced photo-only cards whose sole caption was the generic "Artist" label.
                var artistName = StrAt(data, "profile", "name") ?? name;
                return new HomeCard(uri, artistName, "Artist", ArtistAvatar(data) ?? EntityImage(data), HomeCardKind.Artist);
            default:
                return null;
        }
    }

    public static IReadOnlyList<HomeCard> RecentCards(JsonElement responseRoot, int max = 8)
    {
        var cards = new List<HomeCard>(max);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var lists = Dig(responseRoot, "data", "lists");
        if (lists.ValueKind != JsonValueKind.Array) return cards;

        foreach (var list in lists.EnumerateArray())
            AppendRecentCards(list, cards, seen, max);

        return cards;
    }

    /// <summary>Recents cards from a single `List` node (data.__typename == "List") — the shape embedded in the home
    /// response's HomeRecentlyPlayedSectionData section item. Same recent-entity mapping as <see cref="RecentCards"/>.</summary>
    public static IReadOnlyList<HomeCard> RecentCardsFromListData(JsonElement listData, int max = 12)
    {
        var cards = new List<HomeCard>(max);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AppendRecentCards(listData, cards, seen, max);
        return cards;
    }

    // Walk a `List` node's items.items[].entity.data → recent cards, deduping by URI into the shared accumulator.
    static void AppendRecentCards(JsonElement list, List<HomeCard> cards, HashSet<string> seen, int max)
    {
        var items = Dig(list, "items", "items");
        if (items.ValueKind != JsonValueKind.Array) return;
        foreach (var item in items.EnumerateArray())
        {
            if (cards.Count >= max) return;
            var wrapper = Dig(item, "entity");
            var data = Dig(wrapper, "data");
            if (data.ValueKind != JsonValueKind.Object) continue;
            if (CardFromRecentEntity(data, StrAt(wrapper, "_uri")) is not { } card) continue;
            if (!seen.Add(card.Uri)) continue;
            cards.Add(card);
        }
    }

    static HomeCard? CardFromRecentEntity(JsonElement data, string? wrapperUri)
    {
        var uri = StrAt(data, "uri") ?? wrapperUri;
        if (string.IsNullOrEmpty(uri)) return null;

        var identity = Dig(data, "identityTrait");
        var title = StrAt(identity, "name") ?? "";
        if (title.Length == 0) return null;

        var entityType = StrAt(data, "entityTypeTrait", "type") ?? "";
        var contributors = RecentContributors(identity);
        var image = RecentImage(data);

        if (entityType == "ENTITY_TYPE_TRACK" || uri.StartsWith("spotify:track:", StringComparison.Ordinal))
            return new HomeCard(uri, title, JoinNames("Song", contributors), image, HomeCardKind.Track);

        if (entityType == "ENTITY_TYPE_ARTIST" || uri.StartsWith("spotify:artist:", StringComparison.Ordinal))
            return new HomeCard(uri, title, "Artist", image, HomeCardKind.Artist);

        if (entityType == "ENTITY_TYPE_ALBUM" || uri.StartsWith("spotify:album:", StringComparison.Ordinal))
        {
            var type = TitleCase((StrAt(identity, "type") ?? "Album").Replace('_', ' '));
            return new HomeCard(uri, title, JoinNames(type, contributors), image, HomeCardKind.Album);
        }

        if (entityType == "ENTITY_TYPE_PLAYLIST" || uri.StartsWith("spotify:playlist:", StringComparison.Ordinal))
            return new HomeCard(uri, title, contributors.Count > 0 ? contributors[0].Name : "Playlist", image, HomeCardKind.Playlist);

        return null;
    }

    static List<ArtistRef> RecentContributors(JsonElement identity)
    {
        var result = new List<ArtistRef>();
        var items = Dig(identity, "contributors", "items");
        if (items.ValueKind != JsonValueKind.Array) return result;
        foreach (var item in items.EnumerateArray())
        {
            var name = StrAt(item, "name") ?? "";
            if (name.Length == 0) continue;
            var uri = StrAt(item, "uri") ?? "";
            result.Add(new ArtistRef(IdFromUri(uri), uri, name));
        }
        return result;
    }

    static Image? RecentImage(JsonElement data) => EntityImage(data);

    static Image? EntityImage(JsonElement data)
        => PickImage(Dig(data, "visualIdentityTrait", "squareCoverImage", "image", "data", "sources"))
        ?? PickImage(Dig(data, "visualIdentityTrait", "squareCoverImage", "image", "sources"))
        ?? PickImageFromOriginalInstances(Dig(data, "visualIdentityTrait", "squareCoverImage", "originalInstances"))
        ?? PickImage(Dig(data, "visualIdentityTrait", "image", "data", "sources"))
        ?? PickImage(Dig(data, "visualIdentityTrait", "image", "sources"))
        ?? PickImage(Dig(data, "visualIdentity", "squareCoverImage", "data", "sources"))
        ?? PickImage(Dig(data, "visualIdentity", "squareCoverImage", "sources"))
        ?? PickImageFromOriginalInstances(Dig(data, "visualIdentity", "squareCoverImage", "originalInstances"))
        ?? PickImage(Dig(data, "visuals", "avatarImage", "sources"))
        ?? PickImage(Dig(data, "image", "data", "sources"))
        ?? PickImage(Dig(data, "image", "sources"))
        ?? CoverArt(data)
        ?? ImagesCover(data)
        ?? CoverArt(Dig(data, "albumOfTrack"));

    /// <summary>Pick the best <c>originalInstances[].flatFile.cdnUrl</c> (<c>i.scdn.co</c>) when image-cdn sources are absent.</summary>
    static Image? PickImageFromOriginalInstances(JsonElement originalInstances)
    {
        if (originalInstances.ValueKind != JsonValueKind.Array) return null;
        string? large = null, def = null, small = null;
        foreach (var inst in originalInstances.EnumerateArray())
        {
            var url = StrAt(inst, "flatFile", "cdnUrl");
            if (url is null) continue;
            var size = StrAt(inst, "size") ?? "";
            if (size == "IMAGE_SIZE_LARGE") large = url;
            else if (size == "IMAGE_SIZE_DEFAULT") def = url;
            else if (size == "IMAGE_SIZE_SMALL") small = url;
        }
        var best = large ?? def ?? small;
        return best is null ? null : new Image(best, null, null);
    }

    static string? FirstArtistName(JsonElement albumData)
    {
        var items = Dig(albumData, "artists", "items");
        if (items.ValueKind == JsonValueKind.Array && items.GetArrayLength() > 0)
            return StrAt(items[0], "profile", "name");
        return null;
    }

    static Image? ArtistAvatar(JsonElement artistData)
    {
        // Artists carry visuals.avatarImage.sources in this schema; fall back to images.items shape.
        var v = PickImage(Dig(artistData, "visuals", "avatarImage", "sources"));
        return v ?? ImagesCover(artistData);
    }

    /// <summary>Parse a <c>#RRGGBB</c> (or bare <c>RRGGBB</c>) hex color → opaque <c>0xFFRRGGBB</c>; null when absent/malformed.</summary>
    public static uint? HexToArgb(string? hex)
    {
        if (string.IsNullOrEmpty(hex)) return null;
        var s = hex[0] == '#' ? hex.AsSpan(1) : hex.AsSpan();
        return s.Length == 6 && uint.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb)
            ? 0xFF000000u | rgb : null;
    }

    // ── artist overview (data.artistUnion) → the full "magazine" Artist ───────────────────────────────────────
    /// <summary>Map a Spotify <c>artistUnion</c> (the discography/overview GraphQL query) into a domain
    /// <see cref="Artist"/> with all the magazine facets it carries (visuals, discography, top tracks, goods,
    /// profile, related content). Facets absent from this query (monthly listeners / followers / world rank /
    /// top cities / music videos) are left null/0 and backfilled by the source from <see cref="FakeData"/>.</summary>
    public static Artist MapArtist(JsonElement au)
    {
        var uri = Str(au, "uri") ?? ("spotify:artist:" + (Str(au, "id") ?? ""));
        var name = Str(au, "profile", "name") ?? "";
        var avatar = PickImage(Dig(au, "visuals", "avatarImage", "sources"));
        var header = PickImage(Dig(au, "headerImage", "data", "sources"));
        bool verified = BoolAt(au, false, "onPlatformReputationTrait", "verification", "isVerified")
                     || BoolAt(au, false, "onPlatformReputationTrait", "verification", "isRegistered");
        string? bio = HtmlText(Str(au, "profile", "biography", "text"));

        // Discography: albums + compilations + singles all into TopAlbums (the page splits by Kind).
        var topAlbums = new List<Album>();
        AddReleases(Dig(au, "discography", "albums", "items"), topAlbums);
        AddReleases(Dig(au, "discography", "compilations", "items"), topAlbums);
        AddReleases(Dig(au, "discography", "singles", "items"), topAlbums);

        var topTracks = new List<Track>();
        var tt = Dig(au, "discography", "topTracks", "items");
        if (tt.ValueKind == JsonValueKind.Array)
            foreach (var it in tt.EnumerateArray())
                if (MapArtistTrack(Dig(it, "track")) is { } t) topTracks.Add(t);

        // Releases column: latest + popular are first-class Pathfinder facets (not TopAlbums.Take(N)).
        Album? latestRelease = MapRelease(Dig(au, "discography", "latest"));
        var popularReleases = new List<Album>();
        var popItems = Dig(au, "discography", "popularReleasesAlbums", "items");
        if (popItems.ValueKind == JsonValueKind.Array)
            foreach (var it in popItems.EnumerateArray())
                if (MapRelease(it) is { } al) popularReleases.Add(al);

        var appearsOn = new List<Album>();
        AddReleases(Dig(au, "relatedContent", "appearsOn", "items"), appearsOn);

        var pinned = MapPinned(Dig(au, "profile", "pinnedItem"));
        var concerts = MapConcerts(Dig(au, "goods", "concerts", "items"));
        var extras = new ArtistExtras(
            Concerts: concerts,
            Merch: MapMerch(Dig(au, "goods", "merch", "items")),
            Playlists: MapPlaylistRefs(Dig(au, "profile", "playlistsV2", "items")),
            MusicVideos: MapMusicVideos(au),
            TopCities: MapTopCities(Dig(au, "stats", "topCities", "items")),
            ExternalLinks: MapLinks(Dig(au, "profile", "externalLinks", "items")),
            Gallery: MapGallery(Dig(au, "visuals", "gallery", "items")),
            Related: MapRelated(Dig(au, "relatedContent", "relatedArtists", "items")),
            Tour: FakeData.TourBannerFor(name, concerts),
            WatchFeed: MapWatchFeed(Dig(au, "watchFeedEntrypoint")),
            PreRelease: MapPreRelease(Dig(au, "preReleaseV2", "data")));

        return new Artist(IdFromUri(uri), uri, name, avatar, topAlbums,
            MonthlyListeners: Long(au, "stats", "monthlyListeners"), Followers: Long(au, "stats", "followers"), Bio: bio, Verified: verified,
            WorldRank: (int)Long(au, "stats", "worldRank"), HeaderImage: header, TopTracks: topTracks,
            AppearsOn: appearsOn.Count > 0 ? appearsOn : null, Pinned: pinned, Extras: extras,
            // Per-facet totals — carried alongside the first ~10 items so the grid sizes the whole facet up front.
            AlbumsTotal: (int)Long(au, "discography", "albums", "totalCount"),
            SinglesTotal: (int)Long(au, "discography", "singles", "totalCount"),
            CompilationsTotal: (int)Long(au, "discography", "compilations", "totalCount"),
            LatestRelease: latestRelease,
            PopularReleases: popularReleases.Count > 0 ? popularReleases : null);
    }

    /// <summary>The artist's upcoming release (<c>artistUnion.preReleaseV2.data</c>). Null unless there is a real one:
    /// the field is <c>null</c> on most artists, and the request only asks for it at all when it sends
    /// <c>preReleaseV2: true</c>.
    ///
    /// Requires a uri and a name — a node with neither cannot be navigated to or labelled, so it would render as a
    /// dead card. A missing <c>preReleaseEndDateTime</c> is tolerated: the card still announces the release, it just
    /// cannot count down to it.</summary>
    static ArtistPreRelease? MapPreRelease(JsonElement node)
    {
        if (node.ValueKind != JsonValueKind.Object) return null;
        string? uri = Str(node, "uri");
        string? name = Str(node, "name");
        if (string.IsNullOrEmpty(uri) || string.IsNullOrEmpty(name)) return null;

        return new ArtistPreRelease(
            uri!, name!,
            PickImage(Dig(node, "coverArt", "sources")),
            ParseIso(Str(node, "preReleaseEndDateTime")),
            Str(node, "type"));
    }

    /// <summary>The artist page's music-video shelf: <c>relatedMusicVideos</c> (videos mapped to a track) plus
    /// <c>unmappedMusicVideosV2</c> (videos with no track counterpart), de-duplicated by uri.
    ///
    /// The ITEM shape is tolerant on purpose. Every captured artist returned <c>totalCount: 0</c> for both lists, so
    /// the element schema is genuinely unverified — this reads the two envelopes Spotify uses everywhere else
    /// (<c>items[].data</c> and a bare <c>items[]</c>) and drops anything without a uri and a name. An artist with no
    /// videos yields null and the shelf does not render, which is also what a shape change degrades to.</summary>
    static IReadOnlyList<MusicVideo>? MapMusicVideos(JsonElement au)
    {
        List<MusicVideo>? list = null;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        AddVideos(Dig(au, "relatedMusicVideos", "items"), ref list, seen);
        AddVideos(Dig(au, "unmappedMusicVideosV2", "items"), ref list, seen);
        return list;
    }

    static void AddVideos(JsonElement items, ref List<MusicVideo>? into, HashSet<string> seen)
    {
        if (items.ValueKind != JsonValueKind.Array) return;
        foreach (var it in items.EnumerateArray())
        {
            // Either envelope: {data:{…}} (the common wrapper) or the fields inline on the item.
            var d = Dig(it, "data");
            if (d.ValueKind != JsonValueKind.Object) d = it;

            var uri = Str(d, "uri") ?? Str(d, "trackUri");
            var name = Str(d, "name") ?? Str(d, "title");
            if (string.IsNullOrEmpty(uri) || string.IsNullOrEmpty(name) || !seen.Add(uri!)) continue;

            // 16:9 stills live under thumbnail/coverArt depending on the node; try both rather than assuming.
            var thumb = PickImage(Dig(d, "thumbnailImage", "data", "sources"))
                        ?? PickImage(Dig(d, "thumbnail", "sources"))
                        ?? PickImage(Dig(d, "coverArt", "sources"));

            (into ??= new List<MusicVideo>()).Add(new MusicVideo(
                uri!, name!, thumb,
                Long(d, "duration", "totalMilliseconds"),
                (Str(d, "contentRating", "label") ?? "NONE") != "NONE"));
        }
    }

    /// <summary>The artist's watch-feed entry point. Returns null unless there is something to open — an entrypoint
    /// with neither a video nor a thumbnail would render as a dead control.
    ///
    /// <c>video</c> is null on plenty of entities (every album entrypoint and several artists in the corpus), so its
    /// absence is normal and yields a thumbnail-only feed. When present, <c>videoType</c> decides what <c>fileId</c>
    /// means: <c>"URL"</c> makes it a ready-to-play canvas mp4, anything else leaves it an opaque id we do not play.
    /// Branching on the discriminator (rather than sniffing the string for "http") keeps a future non-URL variant from
    /// being handed to the media engine as if it were a URL.</summary>
    static ArtistWatchFeed? MapWatchFeed(JsonElement node)
    {
        if (node.ValueKind != JsonValueKind.Object) return null;
        string? entry = Str(node, "entrypointUri");
        if (string.IsNullOrEmpty(entry)) return null;

        var video = Dig(node, "video");
        string? fileId = Str(video, "fileId");
        string? videoType = Str(video, "videoType");
        string? canvasUrl = string.Equals(videoType, "URL", StringComparison.OrdinalIgnoreCase) ? fileId : null;
        // thumbnailImage.data.sources — one level deeper than the usual {sources:[…]} envelope.
        var thumb = PickImage(Dig(node, "thumbnailImage", "data", "sources"));
        if (string.IsNullOrEmpty(fileId) && thumb is null) return null;

        return new ArtistWatchFeed(entry!, thumb, fileId,
            StartMs: Long(video, "startTime"), EndMs: Long(video, "endTime"), CanvasUrl: canvasUrl);
    }

    static IReadOnlyList<TopCity>? MapTopCities(JsonElement items)
    {
        if (items.ValueKind != JsonValueKind.Array || items.GetArrayLength() == 0) return null;
        var list = new List<TopCity>();
        foreach (var c in items.EnumerateArray())
        {
            var city = Str(c, "city");
            if (string.IsNullOrEmpty(city)) continue;
            list.Add(new TopCity(city, Str(c, "country"), Long(c, "numberOfListeners")));
        }
        return list.Count > 0 ? list : null;
    }

    static void AddReleases(JsonElement groups, List<Album> into)
    {
        if (groups.ValueKind != JsonValueKind.Array) return;
        foreach (var g in groups.EnumerateArray())
        {
            var rels = Dig(g, "releases", "items");
            if (rels.ValueKind != JsonValueKind.Array || rels.GetArrayLength() == 0) continue;
            if (MapRelease(rels[0]) is { } al) into.Add(al);
        }
    }

    static Album? MapRelease(JsonElement r)
    {
        var uri = Str(r, "uri");
        if (uri is null) return null;
        int tracks = (int)Long(r, "tracks", "totalCount");
        var date = Dig(r, "date");
        int year = (int)Long(date, "year");
        int month = (int)Long(date, "month");
        int day = (int)Long(date, "day");
        string? releasePrecision = Str(date, "precision")
            ?? (day > 0 ? "DAY" : month > 0 ? "MONTH" : year > 0 ? "YEAR" : null);
        string? releaseDate = ReleaseDateIso(date, year, releasePrecision);
        var kind = (Str(r, "type") ?? "ALBUM").ToUpperInvariant() switch
        {
            "SINGLE" => tracks >= 4 ? AlbumKind.EP : AlbumKind.Single,
            "EP" => AlbumKind.EP,
            "COMPILATION" => AlbumKind.Compilation,
            _ => AlbumKind.Album,
        };
        return new Album(IdFromUri(uri), uri, Str(r, "name") ?? "", CoverArt(r) ?? EntityImage(r),
            System.Array.Empty<ArtistRef>(), year, tracks, null, kind,
            ReleaseDate: releaseDate, ReleaseDatePrecision: releasePrecision);
    }

    // Discography release dates arrive as discrete { year, month, day, precision } fields rather than the getAlbum
    // envelope's date.isoString. Preserve the same ISO + precision domain contract so every UI can format it consistently.
    static string? ReleaseDateIso(JsonElement date, int year, string? precision)
    {
        if (year is < 1 or > 9999) return null;
        int month = (int)Long(date, "month");
        int day = (int)Long(date, "day");
        return (precision ?? "").ToUpperInvariant() switch
        {
            "DAY" when month is >= 1 and <= 12 && day >= 1 && day <= DateTime.DaysInMonth(year, month)
                => $"{year:D4}-{month:D2}-{day:D2}",
            "MONTH" when month is >= 1 and <= 12 => $"{year:D4}-{month:D2}-01",
            _ => $"{year:D4}-01-01",
        };
    }

    // topTracks[].track shape: { name, uri, playcount(string), duration.totalMilliseconds, albumOfTrack.{uri,coverArt}, artists.items[] }
    //
    // NOTE the album NAME: `albumOfTrack` in this op carries only `uri` + `coverArt` — there is no name on the wire, so
    // the AlbumRef below is deliberately name-LESS (identity + cover, no title). It is not a bug to "fix" here and it is
    // not inferred from anything: inventing a title from the request context would put a fabricated fact on a shared
    // store row, and the row's own uri is all the wire gave us. The gap is closed downstream by the writers that DO know
    // the title — MetadataService.SyncAllAsync's blank-AlbumRef closure (batches those album uris through AlbumV4, whose
    // projection rewrites the tracklist with the full albumRef) and the ordinary album hydration on open — and the empty
    // name can never overwrite a known one, because StoreEntityMerge.MergeAlbumRef is NonEmpty-guarded per field. Until it
    // heals, the Album column renders the shared em-dash rather than a blank lane (TrackRow.AlbumLink).
    static Track? MapArtistTrack(JsonElement t)
    {
        if (t.ValueKind != JsonValueKind.Object) return null;
        var uri = Str(t, "uri");
        if (uri is null) return null;
        var artists = new List<ArtistRef>();
        var ai = Dig(t, "artists", "items");
        if (ai.ValueKind == JsonValueKind.Array)
            foreach (var a in ai.EnumerateArray())
            {
                var auri = Str(a, "uri") ?? "";
                var nm = Str(a, "profile", "name") ?? "";
                if (nm.Length > 0) artists.Add(new ArtistRef(IdFromUri(auri), auri, nm));
            }
        var album = Dig(t, "albumOfTrack");
        var albumUri = Str(album, "uri") ?? "";
        bool explicitFlag = (Str(t, "contentRating", "label") ?? "NONE") != "NONE";
        return new Track(IdFromUri(uri), uri, Str(t, "name") ?? "", artists,
            new AlbumRef(IdFromUri(albumUri), albumUri, ""),
            Long(t, "duration", "totalMilliseconds"), explicitFlag, CoverArt(album),
            PlayCount: Long(t, "playcount"), Source: "spotify");
    }

    /// <summary>The hero pin (<c>artistUnion.profile.pinnedItem</c>): the pin's own display fields plus the identity of
    /// the thing it points at, read out of the <c>itemV2</c> wrapper (<c>itemV2.data.{uri, type, __typename,
    /// preReleaseEndDateTime}</c>).
    ///
    /// A pin whose <c>itemV2</c> carries a future <c>preReleaseEndDateTime</c> is an ANNOUNCEMENT, not a promo, and the
    /// card renders it as one (see <see cref="PinnedItem.IsUpcoming"/>). Nothing here decides that: the mapper only
    /// carries the instant across, so a release that drops flips the card back to an ordinary promo on the next render
    /// with no refetch.
    ///
    /// <c>Eyebrow</c> stays the literal "Pinned" because the wire has no eyebrow — the pin's own <c>type</c> is the
    /// wrapper kind ("ALBUM"), not a label, and inventing one from it would read as a badge. The pre-release surface
    /// overrides the eyebrow at render time from the localized string table.
    ///
    /// Null-tolerant end to end: <c>itemV2: null</c> (or any missing leaf) leaves all four of the new fields null, which
    /// is byte-identical to what every pin mapped before they existed. The cover still prefers <c>thumbnailImage</c> and
    /// falls back to the wrapped item's <c>coverArt</c>.</summary>
    static PinnedItem? MapPinned(JsonElement p)
    {
        if (p.ValueKind != JsonValueKind.Object) return null;
        var uri = Str(p, "uri");
        if (uri is null) return null;
        var item = Dig(p, "itemV2", "data");
        var cover = PickImage(Dig(p, "thumbnailImage", "data", "sources")) ?? CoverArt(item);
        return new PinnedItem("Pinned", Str(p, "title") ?? "", Str(p, "subtitle") ?? "",
            Str(p, "comment") ?? "", cover, uri,
            ItemUri: Str(item, "uri"),
            ItemType: Str(item, "type"),
            ItemTypename: Str(item, "__typename"),
            ReleaseAt: ParseIso(Str(item, "preReleaseEndDateTime")));
    }

    static IReadOnlyList<Concert>? MapConcerts(JsonElement items)
    {
        if (items.ValueKind != JsonValueKind.Array) return null;
        var list = new List<Concert>();
        foreach (var it in items.EnumerateArray())
        {
            var d = Dig(it, "data");
            var uri = Str(d, "uri");
            if (uri is null) continue;
            DateTimeOffset date = default;
            var iso = Str(d, "startDateIsoString");
            if (iso is not null) DateTimeOffset.TryParse(iso, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out date);
            list.Add(new Concert(uri, Str(d, "title"), Str(d, "location", "name") ?? "",
                Str(d, "location", "city") ?? "", date, BoolAt(d, false, "festival")));
        }
        return list.Count > 0 ? list : null;
    }

    static IReadOnlyList<MerchItem>? MapMerch(JsonElement items)
    {
        if (items.ValueKind != JsonValueKind.Array) return null;
        var list = new List<MerchItem>();
        foreach (var it in items.EnumerateArray())
            list.Add(new MerchItem(Str(it, "nameV2") ?? Str(it, "name") ?? "", Str(it, "price") ?? "",
                HtmlText(Str(it, "description")), PickImage(Dig(it, "image", "sources")), Str(it, "url")));
        return list.Count > 0 ? list : null;
    }

    static IReadOnlyList<PlaylistRef>? MapPlaylistRefs(JsonElement items)
    {
        if (items.ValueKind != JsonValueKind.Array) return null;
        var list = new List<PlaylistRef>();
        foreach (var it in items.EnumerateArray())
        {
            var d = Dig(it, "data");
            var uri = Str(d, "uri");
            if (uri is null) continue;
            list.Add(new PlaylistRef(uri, Str(d, "name") ?? "", ImagesCover(d),
                Str(d, "ownerV2", "data", "name") ?? "Spotify"));
        }
        return list.Count > 0 ? list : null;
    }

    static IReadOnlyList<ExternalLink>? MapLinks(JsonElement items)
    {
        if (items.ValueKind != JsonValueKind.Array) return null;
        var list = new List<ExternalLink>();
        foreach (var it in items.EnumerateArray())
        {
            var url = Str(it, "url") ?? "";
            if (url.Length == 0) continue;
            var name = Str(it, "name") ?? "";
            list.Add(new ExternalLink(TitleCase(name), url, ClassifyLink(name + " " + url)));
        }
        return list.Count > 0 ? list : null;
    }

    static ExternalLinkKind ClassifyLink(string s)
    {
        s = s.ToLowerInvariant();
        if (s.Contains("instagram")) return ExternalLinkKind.Instagram;
        if (s.Contains("twitter") || s.Contains("x.com")) return ExternalLinkKind.Twitter;
        if (s.Contains("facebook")) return ExternalLinkKind.Facebook;
        if (s.Contains("youtube")) return ExternalLinkKind.YouTube;
        if (s.Contains("wikipedia")) return ExternalLinkKind.Wikipedia;
        if (s.Contains("tiktok")) return ExternalLinkKind.TikTok;
        return ExternalLinkKind.Generic;
    }

    static string TitleCase(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..].ToLowerInvariant();

    static IReadOnlyList<Image>? MapGallery(JsonElement items)
    {
        if (items.ValueKind != JsonValueKind.Array) return null;
        var list = new List<Image>();
        foreach (var it in items.EnumerateArray())
            if (PickImage(Dig(it, "sources")) is { } im) list.Add(im);
        return list.Count > 0 ? list : null;
    }

    static IReadOnlyList<RelatedArtist>? MapRelated(JsonElement items)
    {
        if (items.ValueKind != JsonValueKind.Array) return null;
        var list = new List<RelatedArtist>();
        foreach (var it in items.EnumerateArray())
        {
            var uri = Str(it, "uri");
            if (uri is null) continue;
            list.Add(new RelatedArtist(IdFromUri(uri), uri, Str(it, "profile", "name") ?? "",
                PickImage(Dig(it, "visuals", "avatarImage", "sources"))));
        }
        return list.Count > 0 ? list : null;
    }
}
