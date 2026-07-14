using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Wavee.Backend.Spotify;

namespace Wavee.SpotifyLive;

// Spotify Pathfinder (GraphQL) seam for rich catalog reads that have no protobuf equivalent.
public sealed class PathfinderClient
{
    const string Endpoint = "https://api-partner.spotify.com/pathfinder/v2/query";

    readonly IHttpExchange _http;
    readonly WaveeLogger _log;

    public PathfinderClient(IHttpExchange http, WaveeLogger log = default)
    {
        _http = http;
        _log = log;
    }

    public PathfinderClient(Func<CancellationToken, Task<string>> bearer,
        Func<CancellationToken, Task<string?>> clientToken, WaveeLogger log = default)
        : this(new HttpPipeline(
            new HttpClientExchange(HttpPools.Get(HttpPool.ControlPlane)),
            new AuthMiddleware((_, c) => bearer(c)),
            new RateLimitMiddleware(),
            new PathfinderHeadersMiddleware(clientToken)), log) { }

    public enum Platform { Desktop, WebPlayer }

    public async Task<JsonDocument?> QueryAsync(string operationName, string sha256Hash,
        Action<Utf8JsonWriter>? writeVariables, Platform platform = Platform.Desktop, CancellationToken ct = default)
    {
        var body = BuildBody(operationName, sha256Hash, writeVariables);
        var bytes = await QueryBodyBytesAsync(operationName, body, platform, ct).ConfigureAwait(false);
        return bytes is null ? null : JsonDocument.Parse(bytes);
    }

    public async Task<byte[]?> QueryBodyBytesAsync(string operationName, byte[] body,
        Platform platform = Platform.Desktop, CancellationToken ct = default)
    {
        try
        {
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [PathfinderHeadersMiddleware.PlatformHeader] = platform == Platform.WebPlayer
                    ? PathfinderHeadersMiddleware.WebPlayerPlatform
                    : PathfinderHeadersMiddleware.DesktopPlatform,
                ["content-type"] = "application/json",
            };

            using var resp = await _http.SendAsync(new HttpReq("POST", Endpoint, headers, body), ct).ConfigureAwait(false);
            if (resp.Status is < 200 or >= 300)
            {
                _log.Info($"pathfinder {operationName} -> HTTP {resp.Status}{(resp.Status == 400 ? " (stale persisted-query hash - needs recapture)" : "")}");
                return null;
            }
            using var ms = new MemoryStream();
            await resp.Body.CopyToAsync(ms, ct).ConfigureAwait(false);
            return ms.ToArray();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { _log.Info("pathfinder " + operationName + " error: " + ex.Message); return null; }
    }

    public static byte[] BuildBody(string operationName, string sha256Hash, Action<Utf8JsonWriter>? writeVariables)
    {
        using var ms = new MemoryStream(256);
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WritePropertyName("variables");
            w.WriteStartObject();
            writeVariables?.Invoke(w);
            w.WriteEndObject();
            w.WriteString("operationName", operationName);
            w.WritePropertyName("extensions");
            w.WriteStartObject();
            w.WritePropertyName("persistedQuery");
            w.WriteStartObject();
            w.WriteNumber("version", 1);
            w.WriteString("sha256Hash", sha256Hash);
            w.WriteEndObject();
            w.WriteEndObject();
            w.WriteEndObject();
        }
        return ms.ToArray();
    }
}

public static class PathfinderOps
{
    public const string QueryArtistOverview = "queryArtistOverview";
    public const string QueryArtistOverviewHash = "7f86ff63e38c24973a2842b672abe44c910c1973978dc8a4a0cb648edef34527";

    public const string GetAlbum = "getAlbum";
    public const string GetAlbumHash = "b9bfabef66ed756e5e13f68a942deb60bd4125ec1f1be8cc42769dc0259b4b10";

    // Generic image-color extraction: variables { imageUris:[url…] } → data.extractedColors[].{colorDark,colorLight,colorRaw}.hex.
    // Used to tint the playlist page from its cover (albums carry colors inline in getAlbum). Persistent cache is authoritative.
    public const string FetchExtractedColors = "fetchExtractedColors";
    public const string FetchExtractedColorsHash = "36e90fcaea00d47c695fce31874efeb2519b97d4cd0ee1abfb4f8dc9348596ea";

    // Desktop "home" persisted query (INTEGRATION_DESKTOP). The response embeds the recently-played list inline
    // (HomeRecentlyPlayedSectionData → a `List` of recent entities), so no separate `recents` query is needed.
    public const string Home = "home";
    public const string HomeHash = "9052ac65ff42aefe6d39c45c184d9144cf8dbcc233ea1a76f8649264ad3e7896";

    public const string SearchTracks = "searchTracks";
    public const string SearchTracksHash = "59ee4a659c32e9ad894a71308207594a65ba67bb6b632b183abe97303a51fa55";
    public const string SearchAlbums = "searchAlbums";
    public const string SearchAlbumsHash = "5e7d2724fbef31a25f714844bf1313ffc748ebd4bd199eaad50628a4f246a7ab";
    public const string SearchArtists = "searchArtists";
    public const string SearchArtistsHash = "270905851ba5c7faca81cfe053c2dbd8ceb4f156a0e0ef4b385af75ab69ffd13";
    public const string SearchPlaylists = "searchPlaylists";
    public const string SearchPlaylistsHash = "af1730623dc1248b75a61a18bad1f47f1fc7eff802fb0676683de88815c958d8";

    public const string SearchSuggestions = "searchSuggestions";
    public const string SearchSuggestionsHash = "556f5a15b2fdd3a7113ffd377ad9805e38a3a27b8bb1ca7d6d76bad54aa8ee12";

    public const string SearchTopResults = "searchTopResultsList";
    public const string SearchTopResultsHash = "63a93cc04f6d8dea84a85de315e43f396a76cb681500de9ac5ccf5fc618c84cb";

    public const string QueryAlbumMerch = "queryAlbumMerch";
    public const string QueryAlbumMerchHash = "3ef44ed6f17be67299538fe77faffab4075aeaf9e1085f10fc835592266711b5";
    public const string QueryNpvArtist = "queryNpvArtist";
    public const string QueryNpvArtistHash = "047c9c225967d41a763949a4db3f0493e901c9f8689a6537408aabf9beffc177";
    public const string SimilarAlbumsBasedOnThisTrack = "similarAlbumsBasedOnThisTrack";
    public const string SimilarAlbumsBasedOnThisTrackHash = "1d1f93a737498adca2c892c73af87fc0b052afe4e1a33c989540c32413dfae17";
    public const string GetTrack = "getTrack";
    public const string GetTrackHash = "612585ae06ba435ad26369870deaae23b5c8800a256cd8a57e08eddc25a37294";

    public const string QueryWhatsNewFeed = "queryWhatsNewFeed";
    public const string QueryWhatsNewFeedHash = "d889c8c936ab192af8ced595427f5ba2acdf63478fdc0a181c8d477f8322630e";

    // Batch preview lookup for home baseline recommendations: variables { uris:[playlist…] } →
    // data.lookup[].{_uri, data.previewItems.items[].data.{name, uri, albumOfTrack.coverArt, previews.audioPreviews}}.
    // Feeds the Featured editorial card's hover peek (HomeBaselinePreviews).
    public const string FeedBaselineLookup = "feedBaselineLookup";
    public const string FeedBaselineLookupHash = "a950fb7c4ecdcaf2aad2f3ca9ee9c3aa4b9c43c97e1d07d05148c4d355bea7fc";

    // Concert discovery/detail contracts captured from the web-player Pathfinder surface. Variable shapes are locked
    // by ConcertCaptureContractTests; hashes intentionally remain centralized here so a recapture changes one seam.
    public const string ArtistConcerts = "ArtistConcerts";
    public const string ArtistConcertsHash = "ef53c43b865496b9890b7167eab1dc614a8949ef9451b3c41184ea888de8bd2b";
    public const string ArtistConcertsPageLocation = "ArtistConcertsPageLocation";
    public const string ArtistConcertsPageLocationHash = "320698465a352f0d0247ec8ed02471244106d4199820f99de4d0a785561c2b03";
    public const string UserLocation = "userLocation";
    public const string UserLocationHash = "079939378ca79b67c6d047be9152ea940d21f10bbfa2f5d4cf4d8320d87774c2";
    public const string InferredUserLocation = "inferredUserLocation";
    public const string InferredUserLocationHash = "5db4c507ea735d2a1f37bd1166eca2c1a0e3387bb875ebca5d6031b6eccceeba";
    public const string ConcertConcepts = "concertConcepts";
    public const string ConcertConceptsHash = "a409c1eb39b6345e7993d424d2408b65a6699bafc2b8a03217033e517cd76b72";
    public const string ConcertFeed = "concertFeed";
    public const string ConcertFeedHash = "9cae2dbee3f47904c60bab45256260b3ddb9844d5ef25038c17112619d14ce9a";
    public const string ConcertCount = "concertCount";
    public const string ConcertCountHash = "29be9d486e073a49268e13ed9e2d2180187e669fcb7a19b98011aca7ab61b141";
    public const string ConcertLocationDetails = "concertLocationDetails";
    public const string ConcertLocationDetailsHash = "b13f195349f188fee25480ae889d782852d68663bf07743c654244454750d681";
    public const string SearchConcertLocations = "searchConcertLocations";
    public const string SearchConcertLocationsHash = "43ededefcba8b3f519fd0c2d6c025dfeec9f742cf47d04a3c3711d95b27deda3";
    public const string ConcertLocationsByLatLon = "concertLocationsByLatLon";
    public const string ConcertLocationsByLatLonHash = "8a059d072a17a1199feb21fe846271f1680eda87010c832852ced0c55c6c7c96";
    public const string SaveLocation = "saveLocation";
    public const string SaveLocationHash = "5502351e9f201ae29014ca55d3b24b755ba261a1a9eb35fb498cb4c7df419353";
    public const string Concert = "concert";
    public const string ConcertHash = "21afefc1c7f9e38cbf7c60d03f5c8b6e602b7a91e04f2c2e0aa7d1743052768e";
}
