using System;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Wavee.Backend.Spotify;

namespace Wavee.SpotifyLive;

// ── The Spotify Pathfinder (GraphQL) seam ────────────────────────────────────────────────────────────────────────────
// The wire boundary for rich catalog reads that have NO protobuf equivalent — artist overview, online search, the
// editorial home, browse. POSTs a persisted-query envelope to api-partner; auth REUSES what we already mint (the login5
// bearer + the client-token). AOT-safe by construction: the request is written with Utf8JsonWriter and the response is
// read with JsonDocument — NO reflection-based JsonSerializer, so nothing for the trimmer to strip. Lives in SpotifyLive
// so Wavee.Backend stays JSON/proto-free; callers get a JsonDocument and hand it to the Wavee.Core JsonElement mappers.
//
// Operation hashes are persisted server-side and ROTATE — a stale hash returns HTTP 400; QueryAsync logs + returns null
// so each caller degrades gracefully (store/offline fallback) rather than throwing.
public sealed class PathfinderClient
{
    const string Endpoint = "https://api-partner.spotify.com/pathfinder/v2/query";
    // The desktop (xpui) client identity — what queryArtistOverview expects (the WebPlayer profile omits the music-video map).
    const string DesktopUa = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/146.0.7680.179 Spotify/1.2.88.483 Safari/537.36";
    const string WebPlayerUa = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/147.0.0.0 Safari/537.36";

    readonly Func<CancellationToken, Task<string>> _bearer;
    readonly Func<CancellationToken, Task<string?>> _clientToken;
    readonly Action<string>? _log;

    public PathfinderClient(Func<CancellationToken, Task<string>> bearer, Func<CancellationToken, Task<string?>> clientToken, Action<string>? log = null)
    {
        _bearer = bearer;
        _clientToken = clientToken;
        _log = log;
    }

    /// <summary>The client identity a query runs under (drives app-platform + UA). Desktop = the full catalog ops
    /// (artist/album); WebPlayer = the web-only surfaces (home/browse/search variants).</summary>
    public enum Platform { Desktop, WebPlayer }

    /// <summary>Run a persisted GraphQL query. <paramref name="writeVariables"/> writes INTO the already-open variables
    /// object. Returns the parsed response, or null on any failure (HTTP error / stale hash / network).</summary>
    public async Task<JsonDocument?> QueryAsync(string operationName, string sha256Hash,
        Action<Utf8JsonWriter>? writeVariables, Platform platform = Platform.Desktop, CancellationToken ct = default)
    {
        var body = BuildBody(operationName, sha256Hash, writeVariables);
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, Endpoint);
            req.Headers.TryAddWithoutValidation("authorization", "Bearer " + await _bearer(ct).ConfigureAwait(false));
            if (await _clientToken(ct).ConfigureAwait(false) is { Length: > 0 } token)
                req.Headers.TryAddWithoutValidation("client-token", token);
            req.Headers.TryAddWithoutValidation("accept", "application/json");
            req.Headers.TryAddWithoutValidation("app-platform", platform == Platform.WebPlayer ? "WebPlayer" : "Win32_x86_64");
            req.Headers.TryAddWithoutValidation("user-agent", platform == Platform.WebPlayer ? WebPlayerUa : DesktopUa);
            req.Content = new ByteArrayContent(body);
            req.Content.Headers.TryAddWithoutValidation("content-type", "application/json");

            using var resp = await SharedHttp.Client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                _log?.Invoke($"pathfinder {operationName} → HTTP {(int)resp.StatusCode}{((int)resp.StatusCode == 400 ? " (stale persisted-query hash — needs recapture)" : "")}");
                return null;
            }
            var bytes = await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            return JsonDocument.Parse(bytes);
        }
        catch (Exception ex) { _log?.Invoke("pathfinder " + operationName + " error: " + ex.Message); return null; }
    }

    static byte[] BuildBody(string operationName, string sha256Hash, Action<Utf8JsonWriter>? writeVariables)
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

// The persisted-query registry (operation name + sha256 hash). ONE place to recapture when Spotify rotates a hash; a
// 400 from QueryAsync is the signal. Seeded from the captured web/desktop clients.
public static class PathfinderOps
{
    public const string QueryArtistOverview = "queryArtistOverview";
    public const string QueryArtistOverviewHash = "7f86ff63e38c24973a2842b672abe44c910c1973978dc8a4a0cb648edef34527";

    public const string GetAlbum = "getAlbum";
    public const string GetAlbumHash = "b9bfabef66ed756e5e13f68a942deb60bd4125ec1f1be8cc42769dc0259b4b10";

    public const string Home = "home";
    public const string HomeHash = "40c1423fc26ea0d68cd8f212e79ca47df7968fc40d83d184e756af54fd043143";
}
