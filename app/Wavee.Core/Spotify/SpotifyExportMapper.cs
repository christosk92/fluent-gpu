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
            Verified: verified);
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
                bool playable = BoolAt(t, true, "playability", "playable");
                bool hasVideo = Long(t, "associationsV3", "videoAssociations", "totalCount") > 0;
                tracks.Add(new Track(IdFromUri(turi), turi, Str(t, "name") ?? "",
                    tArtists.Count > 0 ? tArtists : albumArtists, albumRef,
                    Long(t, "duration", "totalMilliseconds"), explicitFlag, cover,
                    HasVideo: hasVideo, PlayCount: Long(t, "playcount"),
                    Availability: playable ? Availability.Playable : Availability.Unavailable, Source: "spotify"));
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
            Hydration: AlbumHydrationLevel.Full, Palette: ExtractPalette(Dig(au, "coverArt")));
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
        {
            string name = Str(item, "nameV2") ?? Str(item, "name") ?? "";
            if (name.Length == 0) continue;
            result.Add(new MerchItem(name, Str(item, "price") ?? "", HtmlText(Str(item, "description")),
                PickImage(Dig(item, "image", "sources")), Str(item, "url")));
        }
        return result;
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
        bool playable = BoolAt(data, true, "playability", "playable");
        bool hasVideo = Long(data, "associationsV3", "videoAssociations", "totalCount") > 0;

        return new Track(
            IdFromUri(uri), uri, Str(data, "name") ?? "", artists, albumRef,
            dur, explicitFlag, image, HasVideo: hasVideo, PlayCount: plays,
            Origin: TrackOrigin.Streamed,
            Availability: playable ? Availability.Playable : Availability.Unavailable,
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

        return new SearchResults(tracks, albums, artists, playlists,
            TracksTotal: TotalCount(sv, "tracksV2"),
            AlbumsTotal: TotalCount(sv, "albumsV2"),
            ArtistsTotal: TotalCount(sv, "artists"),
            PlaylistsTotal: TotalCount(sv, "playlists"));
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

    static System.Collections.Generic.IEnumerable<JsonElement> Arr(JsonElement e)
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
        bool playable = BoolAt(data, true, "playability", "playable");

        DateTimeOffset? addedAt = null;
        var iso = StrAt(item, "addedAt", "isoString");
        if (iso is not null && DateTimeOffset.TryParse(iso, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dt)) addedAt = dt;
        var addedBy = StrAt(item, "addedBy", "data", "name");

        return new Track(
            IdFromUri(uri), uri, StrAt(data, "name") ?? "", artists, albumRef,
            dur, explicitFlag, image, addedAt, addedBy, HasVideo: false, PlayCount: plays,
            Origin: TrackOrigin.Streamed,
            Availability: playable ? Availability.Playable : Availability.Unavailable,
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
        Palette? palette = ExtractPalette(Dig(data, "visualIdentity", "squareCoverImage")) ?? ExtractPalette(firstImg);

        return new Playlist(
            IdFromUri(uri), uri, StrAt(data, "name") ?? "", HtmlText(StrAt(data, "description")), ownerName,
            ImagesCover(data), trackCount, tracks ?? System.Array.Empty<Track>(),
            owner, caps, StrAt(data, "format"), Source: "spotify", Palette: palette);
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
                return new HomeCard(uri, name, FirstArtistName(data), CoverArt(data) ?? EntityImage(data), HomeCardKind.Album, Accent: ExtractedAccent(data));
            case "Playlist":
                return new HomeCard(uri, name, HtmlText(StrAt(data, "description")) ?? StrAt(data, "ownerV2", "data", "name"),
                    ImagesCover(data) ?? EntityImage(data), HomeCardKind.Playlist, Accent: ExtractedAccent(data));
            case "Artist":
                return new HomeCard(uri, name, "Artist", ArtistAvatar(data) ?? EntityImage(data), HomeCardKind.Artist, Accent: ExtractedAccent(data));
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
        {
            var items = Dig(list, "items", "items");
            if (items.ValueKind != JsonValueKind.Array) continue;
            foreach (var item in items.EnumerateArray())
            {
                if (cards.Count >= max) return cards;
                var wrapper = Dig(item, "entity");
                var data = Dig(wrapper, "data");
                if (data.ValueKind != JsonValueKind.Object) continue;
                if (CardFromRecentEntity(data, StrAt(wrapper, "_uri")) is not { } card) continue;
                if (!seen.Add(card.Uri)) continue;
                cards.Add(card);
            }
        }

        return cards;
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
            return new HomeCard(uri, title, JoinNames("Song", contributors), image, HomeCardKind.Track, Accent: ExtractedAccent(data));

        if (entityType == "ENTITY_TYPE_ARTIST" || uri.StartsWith("spotify:artist:", StringComparison.Ordinal))
            return new HomeCard(uri, title, "Artist", image, HomeCardKind.Artist, Accent: ExtractedAccent(data));

        if (entityType == "ENTITY_TYPE_ALBUM" || uri.StartsWith("spotify:album:", StringComparison.Ordinal))
        {
            var type = TitleCase((StrAt(identity, "type") ?? "Album").Replace('_', ' '));
            return new HomeCard(uri, title, JoinNames(type, contributors), image, HomeCardKind.Album, Accent: ExtractedAccent(data));
        }

        if (entityType == "ENTITY_TYPE_PLAYLIST" || uri.StartsWith("spotify:playlist:", StringComparison.Ordinal))
            return new HomeCard(uri, title, contributors.Count > 0 ? contributors[0].Name : "Playlist", image, HomeCardKind.Playlist, Accent: ExtractedAccent(data));

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

    // ── home card accent (cover-derived section tint) ──────────────────────────────────────────────────────
    /// <summary>A home card's cover-extracted dominant tint as ARGB — from <c>coverArt</c> or
    /// <c>visualIdentityTrait.squareCoverImage</c> (rich <c>extractedColorSet</c> or <c>extractedColors.colorDark</c>).
    /// Skips Spotify's generic fallback (<c>isFallback</c>) so the composer substitutes a semantic per-kind tint instead.</summary>
    static uint? ExtractedAccent(JsonElement data)
        => AccentFromCoverNode(Dig(data, "coverArt"))
        ?? AccentFromCoverNode(Dig(data, "visualIdentityTrait", "squareCoverImage"))
        ?? AccentFromCoverNode(Dig(data, "visualIdentity", "squareCoverImage"));

    static uint? AccentFromCoverNode(JsonElement coverNode)
    {
        if (coverNode.ValueKind != JsonValueKind.Object) return null;
        return ExtractPalette(coverNode)?.TintedDark;
    }

    /// <summary>Parse a <c>#RRGGBB</c> (or bare <c>RRGGBB</c>) hex color → opaque <c>0xFFRRGGBB</c>; null when absent/malformed.</summary>
    public static uint? HexToArgb(string? hex)
    {
        if (string.IsNullOrEmpty(hex)) return null;
        var s = hex[0] == '#' ? hex.AsSpan(1) : hex.AsSpan();
        return s.Length == 6 && uint.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb)
            ? 0xFF000000u | rgb : null;
    }

    // ── cover-extracted page palette (ALBUM / ARTIST / PLAYLIST detail accent) ───────────────────────────────
    /// <summary>A cover's single dark extracted color (<c>extractedColors.colorDark</c>) → a Palette whose slots all
    /// carry that dark tone (the VIEW lifts Accent for legibility). Null when the node is absent, a generic fallback
    /// (<c>isFallback</c>), or malformed — never a wrong colour.</summary>
    static Palette? PaletteFromColorDark(JsonElement extractedColorsNode)
    {
        var cd = Dig(extractedColorsNode, "colorDark");
        if (cd.ValueKind != JsonValueKind.Object || BoolAt(cd, false, "isFallback")) return null;
        if (HexToArgb(Str(cd, "hex")) is not { } dark) return null;
        return new Palette(BackgroundDark: dark, TintedDark: dark, Light: 0xFFFFFFFF, Accent: dark);
    }

    /// <summary>A cover's rich <c>extractedColorSet</c> → a Palette from its dark tier (<c>higherContrast</c>, else
    /// <c>highContrast</c> — WaveeMusic's dark-mode tier choice). backgroundBase backs the wash; backgroundTintedBase
    /// the accent/tint (the VIEW lifts Accent to match BrightenForTint). Null when neither tier is present.</summary>
    static Palette? PaletteFromColorSet(JsonElement extractedColorSetNode)
    {
        var tier = Dig(extractedColorSetNode, "higherContrast");
        if (tier.ValueKind != JsonValueKind.Object) tier = Dig(extractedColorSetNode, "highContrast");
        var bg   = Dig(tier, "backgroundBase");
        var tint = Dig(tier, "backgroundTintedBase");
        if (bg.ValueKind != JsonValueKind.Object || tint.ValueKind != JsonValueKind.Object) return null;
        uint bgArgb = ColorComponentsToArgb(bg), tintArgb = ColorComponentsToArgb(tint);
        return new Palette(BackgroundDark: bgArgb, TintedDark: tintArgb, Light: 0xFFFFFFFF, Accent: tintArgb);
    }

    // {alpha,red,green,blue} (0–255 ints) → opaque ARGB. Alpha is forced to 0xFF (matches HexToArgb's convention) so a
    // missing channel never yields a transparent accent — the fixtures always carry alpha=255, so this is identical to
    // the literal ((uint)alpha<<24)|… form for all real data.
    static uint ColorComponentsToArgb(JsonElement c) =>
        0xFF000000u | ((uint)(Long(c, "red")   & 0xFF) << 16)
                    | ((uint)(Long(c, "green") & 0xFF) << 8)
                    |  (uint)(Long(c, "blue")  & 0xFF);

    /// <summary>Extract a Palette from a cover node — the rich <c>extractedColorSet</c> first, then the single
    /// <c>extractedColors.colorDark</c>. Null (missing/fallback/malformed) ⇒ the page keeps its neutral default.</summary>
    static Palette? ExtractPalette(JsonElement coverNode)
    {
        if (coverNode.ValueKind != JsonValueKind.Object) return null;
        return PaletteFromColorSet(Dig(coverNode, "extractedColorSet"))
            ?? PaletteFromColorDark(Dig(coverNode, "extractedColors"));
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

        var appearsOn = new List<Album>();
        AddReleases(Dig(au, "relatedContent", "appearsOn", "items"), appearsOn);

        var pinned = MapPinned(Dig(au, "profile", "pinnedItem"));
        var concerts = MapConcerts(Dig(au, "goods", "concerts", "items"));
        var extras = new ArtistExtras(
            Concerts: concerts,
            Merch: MapMerch(Dig(au, "goods", "merch", "items")),
            Playlists: MapPlaylistRefs(Dig(au, "profile", "playlistsV2", "items")),
            MusicVideos: null,
            TopCities: MapTopCities(Dig(au, "stats", "topCities", "items")),
            ExternalLinks: MapLinks(Dig(au, "profile", "externalLinks", "items")),
            Gallery: MapGallery(Dig(au, "visuals", "gallery", "items")),
            Related: MapRelated(Dig(au, "relatedContent", "relatedArtists", "items")),
            Tour: FakeData.TourBannerFor(name, concerts));

        return new Artist(IdFromUri(uri), uri, name, avatar, topAlbums,
            MonthlyListeners: Long(au, "stats", "monthlyListeners"), Followers: Long(au, "stats", "followers"), Bio: bio, Verified: verified,
            WorldRank: (int)Long(au, "stats", "worldRank"), HeaderImage: header, TopTracks: topTracks,
            AppearsOn: appearsOn.Count > 0 ? appearsOn : null, Pinned: pinned, Extras: extras,
            Palette: ExtractPalette(Dig(au, "visualIdentity", "wideFullBleedImage")),
            // Per-facet totals — carried alongside the first ~10 items so the grid sizes the whole facet up front.
            AlbumsTotal: (int)Long(au, "discography", "albums", "totalCount"),
            SinglesTotal: (int)Long(au, "discography", "singles", "totalCount"),
            CompilationsTotal: (int)Long(au, "discography", "compilations", "totalCount"));
    }

    /// <summary>Map a LIVE paged discography response (<c>queryArtistDiscography{Albums,Singles,Compilations}</c>) →
    /// one <see cref="DiscographyPage"/> window: the flattened release-groups at <c>data.artistUnion.discography.&lt;facet&gt;.items</c>
    /// plus the facet's <c>totalCount</c> (clamped up to the item count so a lagging total never under-reports the
    /// delivered items). Same hand-walk as <see cref="MapArtist"/> — no JsonSerializer.</summary>
    public static DiscographyPage DiscographyPageFromResponse(JsonElement responseRoot, DiscographyKind kind)
    {
        var facet = kind switch
        {
            DiscographyKind.Singles => "singles",
            DiscographyKind.Compilations => "compilations",
            _ => "albums",
        };
        var node = Dig(responseRoot, "data", "artistUnion", "discography", facet);
        var items = new List<Album>();
        AddReleases(Dig(node, "items"), items);
        int total = (int)Long(node, "totalCount");
        return new DiscographyPage(items, System.Math.Max(total, items.Count));
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
        var kind = (Str(r, "type") ?? "ALBUM").ToUpperInvariant() switch
        {
            "SINGLE" => tracks >= 4 ? AlbumKind.EP : AlbumKind.Single,
            "EP" => AlbumKind.EP,
            "COMPILATION" => AlbumKind.Compilation,
            _ => AlbumKind.Album,
        };
        return new Album(IdFromUri(uri), uri, Str(r, "name") ?? "", CoverArt(r) ?? EntityImage(r),
            System.Array.Empty<ArtistRef>(), (int)Long(r, "date", "year"), tracks, null, kind,
            Palette: ExtractPalette(Dig(r, "coverArt"))
                ?? ExtractPalette(Dig(r, "visualIdentityTrait", "squareCoverImage")));
    }

    // topTracks[].track shape: { name, uri, playcount(string), duration.totalMilliseconds, albumOfTrack.{uri,coverArt}, artists.items[] }
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

    static PinnedItem? MapPinned(JsonElement p)
    {
        if (p.ValueKind != JsonValueKind.Object) return null;
        var uri = Str(p, "uri");
        if (uri is null) return null;
        var cover = PickImage(Dig(p, "thumbnailImage", "data", "sources")) ?? CoverArt(Dig(p, "itemV2", "data"));
        return new PinnedItem("Pinned", Str(p, "title") ?? "", Str(p, "subtitle") ?? "",
            Str(p, "comment") ?? "", cover, uri);
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
