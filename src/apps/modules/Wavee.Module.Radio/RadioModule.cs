using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Text;
using Wavee.Sdk;

namespace Wavee.Module.Radio;

/// <summary>
/// The fallback module: it claims any http(s) link no other module owns, unwraps <c>.pls</c>/<c>.m3u</c> station
/// playlists, and probes the stream's headers so the app knows whether it is looking at an endless Icecast body
/// (interleaved ICY titles, no seeking) or a plain finite file.
/// </summary>
public sealed class RadioModule : WaveeModule
{
    /// <summary>How many playlist-inside-a-playlist hops to follow before giving up.</summary>
    public const int MaxPlaylistHops = 3;

    /// <summary>How much of a playlist body to read; station playlists are a few hundred bytes.</summary>
    public const int MaxPlaylistBytes = 256 * 1024;

    /// <summary>The UA every request carries. Some Icecast front-ends 403 an empty one.</summary>
    public const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) " +
        "Chrome/139.0.0.0 Safari/537.36";

    private static readonly string[] PlaylistExtensions = [".pls", ".m3u", ".m3u8", ".asx"];

    private readonly HttpClient _http;

    /// <summary>The ctor <see cref="ModuleRunner"/> uses: a default handler with redirects and decompression on.</summary>
    public RadioModule() : this(null)
    {
    }

    /// <summary>Test/host seam: run every request through <paramref name="handler"/>.</summary>
    /// <param name="handler">The transport, or null for the module's own <see cref="SocketsHttpHandler"/>.</param>
    /// <param name="disposeHandler">True to dispose <paramref name="handler"/> with the module.</param>
    public RadioModule(HttpMessageHandler? handler, bool disposeHandler = false)
    {
        bool ownsHandler = handler is null || disposeHandler;
        handler ??= new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 5,
            ConnectTimeout = TimeSpan.FromSeconds(10),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        };
        _http = new HttpClient(handler, ownsHandler) { Timeout = TimeSpan.FromSeconds(20) };
    }

    // ---- match -------------------------------------------------------------------------------------------------

    /// <inheritdoc/>
    /// <remarks>
    /// Confidence is deliberately low: this module is the router's last resort, so a link a real module also claims
    /// never ends up here.
    /// </remarks>
    public override ValueTask<MatchResult?> MatchAsync(string input, CancellationToken ct)
    {
        MatchResult? result = TryParseStreamUrl(input, out Uri? uri)
            ? new MatchResult(uri.ToString(), null, MediaForm.Audio, true, 0.1)
            : null;
        return new ValueTask<MatchResult?>(result);
    }

    // ---- resolve -----------------------------------------------------------------------------------------------

    /// <inheritdoc/>
    public override ValueTask<ResolvedPlayable> ResolveAsync(string playableId, CancellationToken ct)
        => ResolveAsync(playableId, null, ct);

    /// <inheritdoc/>
    public override async ValueTask<ResolvedPlayable> ResolveAsync(string playableId, ResolvePreferences? prefs,
        CancellationToken ct)
    {
        if (!TryParseStreamUrl(playableId, out Uri? uri))
        {
            throw new ModuleException(ModuleErrorCode.NotOwned, $"'{playableId}' is not an http(s) stream url.");
        }

        string current = uri.ToString();

        for (int hop = 0; hop <= MaxPlaylistHops; hop++)
        {
            Probe probe = await ProbeAsync(current, ct).ConfigureAwait(false);
            current = probe.FinalUrl;   // a redirected station plays from where the redirect landed

            if (probe.Body is null) return Build(playableId, current, probe.Icy, forceHls: false);

            StreamPlaylist.Kind kind = StreamPlaylist.Classify(probe.Body, probe.Icy.ContentType);
            if (kind is StreamPlaylist.Kind.Hls) return Build(playableId, current, probe.Icy, forceHls: true);
            if (kind is StreamPlaylist.Kind.Stream) return Build(playableId, current, probe.Icy, forceHls: false);

            string? next = StreamPlaylist.FirstEntry(probe.Body, kind, current);
            if (next is null)
            {
                throw new ModuleException(ModuleErrorCode.Unavailable,
                    "That playlist does not contain a stream url.");
            }

            Host.Log(ModuleLogLevel.Debug, $"Playlist {current} -> {next}");
            current = next;
        }

        throw new ModuleException(ModuleErrorCode.Unavailable,
            "That playlist keeps pointing at another playlist.");
    }

    private ResolvedPlayable Build(string playableId, string streamUrl, IcyInfo icy, bool forceHls)
    {
        string container = forceHls ? MediaLocator.ContainerHls : icy.Container;
        bool isLive = !string.Equals(container, MediaLocator.ContainerProgressive, StringComparison.Ordinal);

        string title = icy.Name ?? (Uri.TryCreate(streamUrl, UriKind.Absolute, out Uri? u) ? u.Host : streamUrl);
        string[] artists = icy.Genre is { Length: > 0 } genre ? [genre] : [];

        if (icy.BitrateKbps is { } br)
        {
            Host.Log(ModuleLogLevel.Info, $"{title}: {br} kbit/s {icy.ContentType ?? "(unknown type)"}, container {container}.");
        }

        return new ResolvedPlayable(
            PlayableId: playableId,
            Title: title,
            Artists: artists,
            ArtworkUrl: null,
            DurationMs: 0,
            IsLive: isLive,
            Form: MediaForm.Audio,
            Media: MediaLocator.FromUrl(streamUrl, container, icy.ContentType),
            ExpiresAtUnixMs: null,
            Caps: []);
    }

    // ---- probe -------------------------------------------------------------------------------------------------

    /// <summary>One header probe. <see cref="Body"/> is non-null only when the response looked like a playlist.</summary>
    /// <param name="Icy">What the headers said.</param>
    /// <param name="Body">The playlist text, or null when the response is the audio itself.</param>
    /// <param name="FinalUrl">Where the probe ended up after redirects — the url the locator must carry.</param>
    private readonly record struct Probe(IcyInfo Icy, string? Body, string FinalUrl);

    /// <summary>Redirect hops the probe follows itself. The handler's automatic redirects refuse an https→http
    /// downgrade, and that is exactly what public radio CDNs do (verified 2026-08-22: stream.srg-ssr.ch answers
    /// 302 → http://…/aac/96), so the probe walks 30x responses manually and keeps the final url.</summary>
    private const int MaxRedirects = 5;

    private async Task<Probe> ProbeAsync(string url, CancellationToken ct)
    {
        HttpResponseMessage response;
        for (int redirect = 0; ; redirect++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
            request.Headers.TryAddWithoutValidation(IcyInfo.RequestHeader, "1");
            request.Headers.TryAddWithoutValidation("Accept", "*/*");

            try
            {
                response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
                    .ConfigureAwait(false);
            }
            catch (HttpRequestException ex)
            {
                // SHOUTcast v1 answers "ICY 200 OK", which SocketsHttpHandler rejects outright. The app's own live
                // transport speaks HTTP/1.0 and accepts it, so treat the station as a plain endless ICY body.
                Host.Log(ModuleLogLevel.Info, $"Header probe of {url} failed ({ex.Message}); assuming an ICY stream.");
                return new Probe(IcyInfo.Unknown, null, url);
            }

            int code = (int)response.StatusCode;
            if (code is 301 or 302 or 303 or 307 or 308 && response.Headers.Location is { } location)
            {
                response.Dispose();
                string next = location.IsAbsoluteUri ? location.ToString() : new Uri(new Uri(url), location).ToString();
                if (redirect >= MaxRedirects)
                {
                    throw new ModuleException(ModuleErrorCode.Unavailable, "The station redirects in a loop.");
                }
                Host.Log(ModuleLogLevel.Info, $"{url} → {code} → {next}");
                url = next;
                continue;
            }
            break;
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                throw new ModuleException(
                    response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone
                        ? ModuleErrorCode.Offline
                        : ModuleErrorCode.Unavailable,
                    $"The station answered {(int)response.StatusCode}.");
            }

            string? contentType = response.Content.Headers.ContentType?.ToString();
            long? contentLength = response.Content.Headers.ContentLength;
            IcyInfo icy = IcyInfo.FromHeaders(
                response.Headers.Concat(response.Content.Headers), contentType, contentLength);

            if (!LooksLikePlaylist(url, contentType)) return new Probe(icy, null, url);

            string body = await ReadLimitedAsync(response.Content, MaxPlaylistBytes, ct).ConfigureAwait(false);
            return new Probe(icy, body, url);
        }
    }

    /// <summary>
    /// True when the response should be read as a playlist rather than played: either the server says so, or the
    /// url carries a playlist extension and the server did not claim an audio type.
    /// </summary>
    /// <param name="url">The url being probed.</param>
    /// <param name="contentType">The response's content type, if any.</param>
    public static bool LooksLikePlaylist(string url, string? contentType)
    {
        if (contentType is { Length: > 0 })
        {
            string ct = contentType.ToLowerInvariant();
            if (ct.Contains("scpls", StringComparison.Ordinal) ||
                ct.Contains("mpegurl", StringComparison.Ordinal) ||
                ct.Contains("vnd.ms-asf", StringComparison.Ordinal))
            {
                return true;
            }

            if (ct.StartsWith("audio/", StringComparison.Ordinal) ||
                ct.StartsWith("video/", StringComparison.Ordinal) ||
                ct.Contains("ogg", StringComparison.Ordinal) ||
                ct.Contains("octet-stream", StringComparison.Ordinal))
            {
                return false;
            }
        }

        return HasPlaylistExtension(url);
    }

    /// <summary>True when the url's path ends in a known playlist extension.</summary>
    /// <param name="url">The url to test.</param>
    public static bool HasPlaylistExtension(string url)
    {
        string path = Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) ? uri.AbsolutePath : url;
        foreach (string ext in PlaylistExtensions)
        {
            if (path.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) return true;
        }

        return false;
    }

    private static async Task<string> ReadLimitedAsync(HttpContent content, int max, CancellationToken ct)
    {
        await using Stream stream = await content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        byte[] buffer = new byte[max];
        int total = 0;
        while (total < max)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(total, max - total), ct).ConfigureAwait(false);
            if (read == 0) break;
            total += read;
        }

        return Encoding.UTF8.GetString(buffer, 0, total);
    }

    private static bool TryParseStreamUrl(string? input, [NotNullWhen(true)] out Uri? uri)
    {
        uri = null;
        if (string.IsNullOrWhiteSpace(input)) return false;
        return Uri.TryCreate(input.Trim(), UriKind.Absolute, out uri) && uri.Scheme is "http" or "https";
    }

    /// <inheritdoc/>
    public override ValueTask ShutdownAsync(CancellationToken ct)
    {
        _http.Dispose();
        return default;
    }
}
