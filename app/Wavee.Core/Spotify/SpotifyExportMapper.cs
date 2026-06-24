using System.Globalization;
using System.Text.Json;

namespace Wavee.Core;

/// <summary>The Anti-Corruption Layer for the Spotify GraphQL export (docs/architecture.md §4.4): translates the raw
/// JSON shapes (playlistV2 / libraryV3 / home) into clean domain records. No JsonElement / GraphQL shape escapes this
/// file. All navigation is null-safe so a missing field degrades gracefully rather than throwing.</summary>
internal static class SpotifyExportMapper
{
    // The export's owner — used to decide IsOwner on playlists.
    public const string CurrentUser = "Christos";

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

        return new Playlist(
            IdFromUri(uri), uri, StrAt(data, "name") ?? "", StrAt(data, "description"), ownerName,
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
                return new HomeCard(uri, name, FirstArtistName(data), CoverArt(data), HomeCardKind.Album);
            case "Playlist":
                return new HomeCard(uri, name, StrAt(data, "description") ?? StrAt(data, "ownerV2", "data", "name"), ImagesCover(data), HomeCardKind.Playlist);
            case "Artist":
                return new HomeCard(uri, name, "Artist", ArtistAvatar(data), HomeCardKind.Artist);
            default:
                return null;
        }
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
        string? bio = Str(au, "profile", "biography", "text");

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
            TopCities: null,
            ExternalLinks: MapLinks(Dig(au, "profile", "externalLinks", "items")),
            Gallery: MapGallery(Dig(au, "visuals", "gallery", "items")),
            Related: MapRelated(Dig(au, "relatedContent", "relatedArtists", "items")),
            Tour: FakeData.TourBannerFor(name, concerts));

        return new Artist(IdFromUri(uri), uri, name, avatar, topAlbums,
            MonthlyListeners: 0, Followers: 0, Bio: bio, Verified: verified,
            WorldRank: 0, HeaderImage: header, TopTracks: topTracks,
            AppearsOn: appearsOn.Count > 0 ? appearsOn : null, Pinned: pinned, Extras: extras);
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
        return new Album(IdFromUri(uri), uri, Str(r, "name") ?? "", CoverArt(r),
            System.Array.Empty<ArtistRef>(), (int)Long(r, "date", "year"), tracks, null, kind);
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
                Str(it, "description"), PickImage(Dig(it, "image", "sources")), Str(it, "url")));
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
