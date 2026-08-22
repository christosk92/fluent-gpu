using System.Buffers;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using Wavee.Sdk;

namespace Wavee.Module.YouTube;

/// <summary>
/// Resolves a YouTube link to ONE HTTPS HLS master url with MPEG-TS renditions, which the app hands straight to
/// Media Foundation. Deliberately never touches <c>formats[]</c>, <c>adaptiveFormats[]</c> or
/// <c>dashManifestUrl</c>: progressive itags need PO tokens or the JS cipher, and Win32 Media Engine has no DASH.
/// </summary>
public sealed partial class YouTubeModule : WaveeModule
{
    /// <summary>The InnerTube player endpoint. No API key: JS-less clients do not need one.</summary>
    public const string PlayerEndpoint = "https://www.youtube.com/youtubei/v1/player?prettyPrint=false";

    /// <summary>The UA used for the channel-live HTML scrape and the manifest preflight.</summary>
    public const string DesktopUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) " +
        "Chrome/139.0.0.0 Safari/537.36";

    /// <summary>How far before the signed expiry the host should re-resolve (seconds).</summary>
    public const int ExpirySafetySeconds = 600;

    private static readonly YouTubeClient[] BuiltInClients =
    [
        new("visionos", "VISIONOS", "1.02", 101,
            "Mozilla/5.0 (Macintosh; Intel Mac OS X 15_7_3) AppleWebKit/605.1.15 (KHTML, like Gecko) " +
            "Version/26.0 Safari/605.1.15",
            DeviceMake: "Apple", DeviceModel: "RealityDevice17,1", OsName: "visionOS", OsVersion: "26.5.23O471"),
        new("android", "ANDROID", "21.26.364", 3,
            "com.google.android.youtube/21.26.364 (Linux; U; Android 11) gzip",
            OsName: "Android", OsVersion: "11", AndroidSdkVersion: 30),
        new("ios", "IOS", "21.26.4", 5,
            "com.google.ios.youtube/21.26.4 (iPhone16,2; U; CPU iOS 18_3_2 like Mac OS X;)",
            DeviceMake: "Apple", DeviceModel: "iPhone16,2", OsName: "iPhone", OsVersion: "18.3.2.22D82",
            Warning: "YouTube stops serving the iOS HLS manifest about 30 seconds in without a PO token; " +
                     "playback may cut out."),
    ];

    private readonly HttpClient _http;
    private YouTubeClient[]? _clients;

    /// <summary>The ctor <see cref="ModuleRunner"/> uses: a default handler with redirects and decompression on.</summary>
    public YouTubeModule() : this(null)
    {
    }

    /// <summary>Test/host seam: run every request through <paramref name="handler"/>.</summary>
    /// <param name="handler">The transport, or null for the module's own <see cref="SocketsHttpHandler"/>.</param>
    /// <param name="disposeHandler">True to dispose <paramref name="handler"/> with the module.</param>
    public YouTubeModule(HttpMessageHandler? handler, bool disposeHandler = false)
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

    /// <summary>The client table actually in use (data file if present, built-in table otherwise).</summary>
    public YouTubeClient[] Clients => _clients ??= LoadClients();

    /// <inheritdoc/>
    public override ValueTask InitializeAsync(ModuleContext ctx, CancellationToken ct)
    {
        _clients = LoadClients();
        Host.Log(ModuleLogLevel.Info,
            $"YouTube module ready; {_clients.Length} InnerTube client(s): {string.Join(", ", _clients.Select(c => c.Key))}");
        return default;
    }

    // ---- match -------------------------------------------------------------------------------------------------

    /// <inheritdoc/>
    public override async ValueTask<MatchResult?> MatchAsync(string input, CancellationToken ct)
    {
        YouTubeLink link = YouTubeUrls.Parse(input);
        switch (link.Kind)
        {
            case YouTubeLinkKind.Video:
                return new MatchResult(link.VideoId!, null, MediaForm.Video, false, 1.0);

            case YouTubeLinkKind.ChannelLive:
            {
                string? id = await ScrapeChannelLiveAsync(link.LivePageUrl!, ct).ConfigureAwait(false);
                if (id is null)
                {
                    throw new ModuleException(ModuleErrorCode.Offline, "That channel is not live right now.");
                }

                return new MatchResult(id, null, MediaForm.Video, true, 0.95);
            }

            default:
                return null;
        }
    }

    /// <summary>Fetches a channel's <c>/live</c> page and reads the current broadcast's video id out of it.</summary>
    /// <param name="pageUrl">The absolute channel-live page url.</param>
    /// <param name="ct">Cancels the fetch.</param>
    /// <returns>The video id, or null when the channel is offline.</returns>
    public async Task<string?> ScrapeChannelLiveAsync(string pageUrl, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, pageUrl);
        request.Headers.TryAddWithoutValidation("User-Agent", DesktopUserAgent);
        request.Headers.TryAddWithoutValidation("Accept-Language", "en-US,en;q=0.9");
        // Skips the consent interstitial that EU exit nodes get instead of the watch page.
        request.Headers.TryAddWithoutValidation("Cookie", "SOCS=CAI;CONSENT=YES+1");

        using HttpResponseMessage response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new ModuleException(ModuleErrorCode.Transient,
                $"YouTube answered {(int)response.StatusCode} for that channel page.");
        }

        string html = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        return YouTubeUrls.ExtractLiveVideoId(html);
    }

    // ---- resolve -----------------------------------------------------------------------------------------------

    /// <inheritdoc/>
    public override ValueTask<ResolvedPlayable> ResolveAsync(string playableId, CancellationToken ct)
        => ResolveAsync(playableId, null, ct);

    /// <inheritdoc/>
    public override async ValueTask<ResolvedPlayable> ResolveAsync(string playableId, ResolvePreferences? prefs,
        CancellationToken ct)
    {
        if (!YouTubeUrls.IsVideoId(playableId))
        {
            throw new ModuleException(ModuleErrorCode.NotOwned, $"'{playableId}' is not a YouTube video id.");
        }

        ModuleErrorCode lastCode = ModuleErrorCode.Unavailable;
        string lastReason = "YouTube would not serve this video to any of the configured clients.";

        YouTubeClient[] clients = Clients;
        for (int i = 0; i < clients.Length; i++)
        {
            YouTubeClient client = clients[i];
            YtPlayerResponse? player;
            try
            {
                player = await PlayerAsync(client, playableId, ct).ConfigureAwait(false);
            }
            catch (HttpRequestException ex)
            {
                lastCode = ModuleErrorCode.Transient;
                lastReason = $"Could not reach YouTube ({ex.Message}).";
                continue;
            }
            catch (JsonException)
            {
                lastCode = ModuleErrorCode.Transient;
                lastReason = "YouTube returned an unreadable player response.";
                continue;
            }

            string? status = player?.PlayabilityStatus?.Status;
            string? reason = player?.PlayabilityStatus?.Reason;

            // A response describing a DIFFERENT video means the request never reached the real player: yt-dlp reads
            // this as "your IP is likely being blocked". Another client on the same IP sometimes still works.
            if (player?.VideoDetails?.VideoId is { Length: > 0 } got &&
                !string.Equals(got, playableId, StringComparison.Ordinal))
            {
                lastCode = ModuleErrorCode.Unavailable;
                lastReason = "YouTube is blocking this network (it answered with a different video).";
                Host.Log(ModuleLogLevel.Warn, $"{client.Key}: videoId mismatch ({got} != {playableId}).");
                continue;
            }

            // The bot wall is per CLIENT, not per network: verified 2026-08-22 — VISIONOS answered LOGIN_REQUIRED
            // "confirm you're not a bot" for a live stream that ANDROID then served with an hlsManifestUrl from the
            // same IP. So it is a next-client row; it only becomes the verdict when every client says it.
            if (IsBotWall(status, reason))
            {
                lastCode = ModuleErrorCode.Unavailable;
                lastReason = "YouTube is blocking this network (VPN or datacenter IP): it wants a signed-in browser.";
                Host.Log(ModuleLogLevel.Warn, $"{client.Key}: bot wall ({reason}); trying the next client.");
                continue;
            }

            if (IsAgeGate(status, reason, player))
            {
                throw new ModuleException(ModuleErrorCode.NeedsAuth,
                    "This video is age-restricted and Wavee cannot sign in to YouTube.") { Detail = reason };
            }

            string? hls = player?.StreamingData?.HlsManifestUrl;

            if (string.Equals(status, "LIVE_STREAM_OFFLINE", StringComparison.Ordinal) && hls is null)
            {
                throw new ModuleException(ModuleErrorCode.Offline, OfflineMessage(player)) { Detail = reason };
            }

            if (status is not ("OK" or "LIVE_STREAM_OFFLINE"))
            {
                lastCode = ModuleErrorCode.Unavailable;
                lastReason = string.IsNullOrWhiteSpace(reason)
                    ? $"YouTube refused to play this video ({status ?? "no status"})."
                    : reason;
                Host.Log(ModuleLogLevel.Warn, $"{client.Key}: {status} — {reason}");
                continue;
            }

            if (hls is null)
            {
                bool sabr = player?.StreamingData?.ServerAbrStreamingUrl is { Length: > 0 };
                lastCode = ModuleErrorCode.Unavailable;
                lastReason = sabr
                    ? "YouTube served a SABR-only session for this video; Wavee cannot play those."
                    : "YouTube did not return an HLS manifest for this video.";
                Host.Log(ModuleLogLevel.Warn, $"{client.Key}: {lastReason}");
                continue;
            }

            // Preflight: a signed manifest url that 403s here would 403 inside Media Foundation with no diagnosis.
            PreflightResult preflight = await PreflightAsync(hls, ct).ConfigureAwait(false);
            if (preflight == PreflightResult.Forbidden)
            {
                lastCode = ModuleErrorCode.Unavailable;
                lastReason = "YouTube rejected the stream url for this network.";
                Host.Log(ModuleLogLevel.Warn, $"{client.Key}: manifest preflight returned 403.");
                continue;
            }

            if (preflight == PreflightResult.Unreachable)
            {
                lastCode = ModuleErrorCode.Transient;
                lastReason = "Could not fetch the YouTube stream manifest.";
                continue;
            }

            if (preflight == PreflightResult.NotPlaylist)
            {
                throw new ModuleException(ModuleErrorCode.Unavailable,
                    "YouTube returned something that is not an HLS playlist for this video.");
            }

            if (client.Warning is { Length: > 0 } warning) Host.Log(ModuleLogLevel.Warn, warning);
            Host.Log(ModuleLogLevel.Info, $"Resolved {playableId} through the {client.Key} client.");
            return Build(playableId, player!, hls);
        }

        throw new ModuleException(lastCode, lastReason);
    }

    private static string OfflineMessage(YtPlayerResponse? player)
    {
        string? start = player?.Microformat?.PlayerMicroformatRenderer?.LiveBroadcastDetails?.StartTimestamp;
        if (start is { Length: > 0 } &&
            DateTimeOffset.TryParse(start, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTimeOffset at))
        {
            return $"This stream is offline; it is scheduled for {at.UtcDateTime:u}.";
        }

        return "This stream is offline.";
    }

    private static bool IsBotWall(string? status, string? reason)
        => string.Equals(status, "LOGIN_REQUIRED", StringComparison.Ordinal) &&
           reason is not null && reason.Contains("bot", StringComparison.OrdinalIgnoreCase);

    private static bool IsAgeGate(string? status, string? reason, YtPlayerResponse? player)
    {
        if (player?.PlayabilityStatus?.DesktopLegacyAgeGateReason is not null) return true;
        if (status is "AGE_CHECK_REQUIRED" or "AGE_VERIFICATION_REQUIRED") return true;
        if (reason is not null && AgeReason().IsMatch(reason)) return true;
        return string.Equals(status, "LOGIN_REQUIRED", StringComparison.Ordinal);
    }

    [GeneratedRegex("confirm your age|age-restricted|age restricted|inappropriate",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AgeReason();

    [GeneratedRegex(@"[/?&]expire[/=](\d+)")]
    private static partial Regex ExpireParam();

    private ResolvedPlayable Build(string videoId, YtPlayerResponse player, string hls)
    {
        YtVideoDetails? d = player.VideoDetails;
        YtLiveBroadcastDetails? broadcast = player.Microformat?.PlayerMicroformatRenderer?.LiveBroadcastDetails;

        bool isLive = (d?.IsLive ?? false) || (broadcast?.IsLiveNow ?? false);
        long durationMs = 0;
        if (!isLive && long.TryParse(d?.LengthSeconds, NumberStyles.Integer, CultureInfo.InvariantCulture,
                out long seconds) && seconds > 0)
        {
            durationMs = seconds * 1000L;
        }

        string title = string.IsNullOrWhiteSpace(d?.Title) ? videoId : d!.Title!;
        string[] artists = string.IsNullOrWhiteSpace(d?.Author) ? [] : [d!.Author!];

        return new ResolvedPlayable(
            PlayableId: videoId,
            Title: title,
            Artists: artists,
            ArtworkUrl: WidestThumbnail(d?.Thumbnail?.Thumbnails),
            DurationMs: durationMs,
            IsLive: isLive,
            Form: MediaForm.Video,
            Media: MediaLocator.FromUrl(hls, MediaLocator.ContainerHls, "application/vnd.apple.mpegurl"),
            ExpiresAtUnixMs: ExpiresAt(hls, player.StreamingData?.ExpiresInSeconds),
            Caps: []);
    }

    /// <summary>
    /// When the app must re-resolve: the earlier of the signed <c>/expire/</c> instant and
    /// <c>now + expiresInSeconds</c>, minus a 10-minute safety margin. Null when YouTube signed neither.
    /// </summary>
    /// <param name="manifestUrl">The signed HLS master url.</param>
    /// <param name="expiresInSeconds">The session lifetime string from <c>streamingData</c>.</param>
    public static long? ExpiresAt(string manifestUrl, string? expiresInSeconds)
        => ExpiresAt(manifestUrl, expiresInSeconds, DateTimeOffset.UtcNow);

    /// <summary>Testable overload of <see cref="ExpiresAt(string,string?)"/> with an explicit "now".</summary>
    /// <param name="manifestUrl">The signed HLS master url.</param>
    /// <param name="expiresInSeconds">The session lifetime string from <c>streamingData</c>.</param>
    /// <param name="now">The instant to measure <paramref name="expiresInSeconds"/> from.</param>
    public static long? ExpiresAt(string manifestUrl, string? expiresInSeconds, DateTimeOffset now)
    {
        long? signed = null;
        Match m = ExpireParam().Match(manifestUrl ?? string.Empty);
        if (m.Success && long.TryParse(m.Groups[1].ValueSpan, NumberStyles.Integer, CultureInfo.InvariantCulture,
                out long unix))
        {
            signed = unix;
        }

        long? relative = null;
        if (long.TryParse(expiresInSeconds, NumberStyles.Integer, CultureInfo.InvariantCulture, out long lifetime) &&
            lifetime > 0)
        {
            relative = now.ToUnixTimeSeconds() + lifetime;
        }

        long? best = (signed, relative) switch
        {
            (null, null) => null,
            ({ } a, null) => a,
            (null, { } b) => b,
            ({ } a, { } b) => Math.Min(a, b),
        };

        return best is { } chosen ? (chosen - ExpirySafetySeconds) * 1000L : null;
    }

    private static string? WidestThumbnail(YtThumbnail[]? thumbnails)
    {
        if (thumbnails is null || thumbnails.Length == 0) return null;
        YtThumbnail? best = null;
        foreach (YtThumbnail t in thumbnails)
        {
            if (t.Url is not { Length: > 0 }) continue;
            if (best is null || t.Width > best.Width) best = t;
        }

        return best?.Url;
    }

    // ---- transport ---------------------------------------------------------------------------------------------

    /// <summary>Which way the manifest preflight went.</summary>
    private enum PreflightResult
    {
        Ok,
        Forbidden,
        Unreachable,
        NotPlaylist,
    }

    private async Task<YtPlayerResponse?> PlayerAsync(YouTubeClient client, string videoId, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, PlayerEndpoint);
        request.Headers.TryAddWithoutValidation("User-Agent", client.UserAgent);
        request.Headers.TryAddWithoutValidation("X-YouTube-Client-Name",
            client.ClientId.ToString(CultureInfo.InvariantCulture));
        request.Headers.TryAddWithoutValidation("X-YouTube-Client-Version", client.ClientVersion);
        request.Headers.TryAddWithoutValidation("Origin", "https://www.youtube.com");
        request.Headers.TryAddWithoutValidation("Accept-Language", "en-US,en;q=0.9");
        request.Content = new ByteArrayContent(PlayerBody(client, videoId));
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };

        using HttpResponseMessage response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"the player endpoint answered {(int)response.StatusCode}");
        }

        byte[] body = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        return JsonSerializer.Deserialize(body, YouTubeJsonContext.Default.YtPlayerResponse);
    }

    /// <summary>
    /// Builds the InnerTube request body. Written by hand with <see cref="Utf8JsonWriter"/> so an added client field
    /// in <c>clients.json</c> needs no new DTO — and so nothing here can reach for reflection.
    /// </summary>
    /// <param name="client">The client block to send.</param>
    /// <param name="videoId">The video to ask about.</param>
    public static byte[] PlayerBody(YouTubeClient client, string videoId)
    {
        var buffer = new ArrayBufferWriter<byte>(512);
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteString("videoId", videoId);

            w.WritePropertyName("context");
            w.WriteStartObject();
            w.WritePropertyName("client");
            w.WriteStartObject();
            w.WriteString("clientName", client.ClientName);
            w.WriteString("clientVersion", client.ClientVersion);
            if (client.DeviceMake is { Length: > 0 }) w.WriteString("deviceMake", client.DeviceMake);
            if (client.DeviceModel is { Length: > 0 }) w.WriteString("deviceModel", client.DeviceModel);
            if (client.OsName is { Length: > 0 }) w.WriteString("osName", client.OsName);
            if (client.OsVersion is { Length: > 0 }) w.WriteString("osVersion", client.OsVersion);
            if (client.AndroidSdkVersion is { } sdk) w.WriteNumber("androidSdkVersion", sdk);
            w.WriteString("userAgent", client.UserAgent);
            w.WriteString("hl", "en");
            w.WriteString("timeZone", "UTC");
            w.WriteNumber("utcOffsetMinutes", 0);
            w.WriteEndObject();
            w.WriteEndObject();

            w.WritePropertyName("playbackContext");
            w.WriteStartObject();
            w.WritePropertyName("contentPlaybackContext");
            w.WriteStartObject();
            w.WriteString("html5Preference", "HTML5_PREF_WANTS");
            w.WriteEndObject();
            w.WriteEndObject();

            w.WriteBoolean("contentCheckOk", true);
            w.WriteBoolean("racyCheckOk", true);
            w.WriteEndObject();
        }

        return buffer.WrittenSpan.ToArray();
    }

    private async Task<PreflightResult> PreflightAsync(string manifestUrl, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, manifestUrl);
        request.Headers.TryAddWithoutValidation("User-Agent", DesktopUserAgent);

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
            return PreflightResult.Unreachable;
        }

        using (response)
        {
            if (response.StatusCode is HttpStatusCode.Forbidden) return PreflightResult.Forbidden;
            if (!response.IsSuccessStatusCode) return PreflightResult.Unreachable;

            string head = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return head.TrimStart().StartsWith("#EXTM3U", StringComparison.Ordinal)
                ? PreflightResult.Ok
                : PreflightResult.NotPlaylist;
        }
    }

    // ---- client table ------------------------------------------------------------------------------------------

    /// <summary>
    /// Loads the client fallback order: a <c>clients.json</c> in the module's data dir wins over the one shipped
    /// beside the exe, which wins over the built-in table. YouTube retires client versions every few weeks, so this
    /// is deliberately data the user (or an update) can replace without a new module build.
    /// </summary>
    private YouTubeClient[] LoadClients()
    {
        string?[] candidates =
        [
            HasHost ? Path.Combine(Host.DataDir, "clients.json") : null,
            Path.Combine(AppContext.BaseDirectory, "clients.json"),
        ];

        foreach (string? path in candidates)
        {
            if (path is null || !File.Exists(path)) continue;
            try
            {
                YouTubeClientTable? table = JsonSerializer.Deserialize(File.ReadAllBytes(path),
                    YouTubeJsonContext.Default.YouTubeClientTable);
                if (table?.Clients is { Length: > 0 } clients) return clients;
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                if (HasHost) Host.Log(ModuleLogLevel.Warn, $"Ignoring unreadable {path}: {ex.Message}");
            }
        }

        return BuiltInClients;
    }

    /// <inheritdoc/>
    public override ValueTask ShutdownAsync(CancellationToken ct)
    {
        _http.Dispose();
        return default;
    }
}
