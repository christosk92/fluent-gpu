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
            int sw = s.TryGetProperty("width", out var wv) && wv.ValueKind == JsonValueKind.Number ? wv.GetInt32() : 0;
            int sh = s.TryGetProperty("height", out var hv) && hv.ValueKind == JsonValueKind.Number ? hv.GetInt32() : 0;
            if (best is null || sw > bestW) { best = url; bestW = sw; w = sw; h = sh; }
        }
        return best is null ? null : new Image(best, w > 0 ? w : null, h > 0 ? h : null);
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
}
