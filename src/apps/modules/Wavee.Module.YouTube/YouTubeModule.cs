using System.Buffers;
using System.Collections.Concurrent;
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

    /// <summary>
    /// The InnerTube watch-next endpoint. PAGE PATH ONLY — <see cref="ResolveAsync(string,CancellationToken)"/> never
    /// touches it, because it returns no stream urls and playback latency must not pay for page copy.
    /// </summary>
    public const string NextEndpoint = "https://www.youtube.com/youtubei/v1/next?prettyPrint=false";

    /// <summary>The cookie that skips the EU consent interstitial; sent by every www.youtube.com request.</summary>
    public const string ConsentCookie = "SOCS=CAI;CONSENT=YES+1";

    /// <summary>How many up-next entries one page carries. YouTube offers about twenty; this is the ceiling that
    /// keeps a shelf inside <see cref="ModulePageBudget.MaxItems"/> however long the rail grows.</summary>
    public const int MaxRelated = 24;

    /// <summary>How long a LIVE page stays fresh (ms). A concurrent-viewer count is stale within the minute, and the
    /// broadcast can end at any moment, so the page asks the app for a much shorter cache than the 10-minute
    /// default. A finished video's page has nothing that rots, so it keeps the default.</summary>
    public const int LivePageTtlMs = 60_000;

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
        // Metadata only, and that is the whole point: WEB is permanently banned from /player (it answers a SABR-only
        // session that needs the JS player), but /next hands back no streams at all, so the ban has nothing to bite
        // on. WEB is also the only client that answers `twoColumnWatchNextResults`; the mobile blocks above answer
        // `singleColumnWatchNextResults`, a different document this module does not read.
        new("web", "WEB", "2.20260822.01.00", 1, DesktopUserAgent,
            OsName: "Windows", OsVersion: "10.0", Role: YouTubeClient.RoleMetadata),
    ];

    /// <summary>The <c>video:&lt;id&gt;</c> entity-id prefix (see <see cref="ModulePageDoc"/>).</summary>
    public const string VideoEntityPrefix = "video:";

    /// <summary>The <c>channel:&lt;id&gt;</c> entity-id prefix.</summary>
    public const string ChannelEntityPrefix = "channel:";

    private readonly HttpClient _http;
    private readonly ConcurrentDictionary<string, ChannelSnapshot> _channels = new(StringComparer.Ordinal);
    private YouTubeClient[]? _table;
    private YouTubeClient[]? _playbackClients;
    private YouTubeClient? _metadataClient;

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

    /// <summary>Every block of the client table actually in use, both roles, in file order.</summary>
    public YouTubeClient[] ClientTable
    {
        get
        {
            EnsureClients();
            return _table!;
        }
    }

    /// <summary>The clients the <c>/player</c> fallback walk may try, in order. A
    /// <see cref="YouTubeClient.RoleMetadata"/> block is deliberately absent.</summary>
    public YouTubeClient[] Clients
    {
        get
        {
            EnsureClients();
            return _playbackClients!;
        }
    }

    /// <summary>The one client used for <c>/next</c>, or null when the table configures none — in which case pages
    /// simply render without the watch-next enrichment.</summary>
    public YouTubeClient? MetadataClient
    {
        get
        {
            EnsureClients();
            return _metadataClient;
        }
    }

    /// <summary>
    /// Loads and splits the client table once. The two projections are computed BEFORE <c>_table</c> is published so
    /// a second thread either sees the whole set or none of it: the page path starts <c>/player</c> and <c>/next</c>
    /// concurrently, and they read different projections of the same table.
    /// </summary>
    private void EnsureClients()
    {
        if (_table is not null) return;

        YouTubeClient[] table = LoadClients();
        _playbackClients = [.. table.Where(c => c.IsPlayback)];
        _metadataClient = Array.Find(table, c => c.IsMetadata);
        _table = table;
    }

    /// <inheritdoc/>
    public override ValueTask InitializeAsync(ModuleContext ctx, CancellationToken ct)
    {
        EnsureClients();
        Host.Log(ModuleLogLevel.Info,
            $"YouTube module ready; {_playbackClients!.Length} playback client(s): " +
            $"{string.Join(", ", _playbackClients.Select(c => c.Key))}; metadata client: " +
            $"{_metadataClient?.Key ?? "none"}");
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
        request.Headers.TryAddWithoutValidation("Cookie", ConsentCookie);

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
        string? artwork = WidestThumbnail(d?.Thumbnail?.Thumbnails);
        string? channelId = Blank(d?.ChannelId);

        // The channel page has no endpoint of its own — the player response is all YouTube gives a JS-less client —
        // so remember what THIS video said about its channel and serve that, honestly labelled, on `channel:<id>`.
        if (channelId is not null)
        {
            RememberChannel(channelId, Blank(d?.Author), videoId, title, artwork, isLive, avatar: null);
        }

        return new ResolvedPlayable(
            PlayableId: videoId,
            Title: title,
            Artists: artists,
            ArtworkUrl: artwork,
            DurationMs: durationMs,
            IsLive: isLive,
            Form: MediaForm.Video,
            Media: MediaLocator.FromUrl(hls, MediaLocator.ContainerHls, "application/vnd.apple.mpegurl"),
            ExpiresAtUnixMs: ExpiresAt(hls, player.StreamingData?.ExpiresInSeconds),
            Caps: [],
            PageEntityId: VideoEntityPrefix + videoId,
            SubtitleEntityId: channelId is null ? null : ChannelEntityPrefix + channelId);
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

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

    // ---- pages -------------------------------------------------------------------------------------------------

    /// <summary>What one resolve learned about a channel. The player response is the only channel data a JS-less
    /// client gets, so this is deliberately a snapshot of one video, not a channel record.</summary>
    /// <param name="ChannelId">The channel id.</param>
    /// <param name="Name">The channel name, as the video's <c>author</c> reported it.</param>
    /// <param name="VideoId">The video that taught us about the channel.</param>
    /// <param name="VideoTitle">That video's title.</param>
    /// <param name="Thumbnail">That video's widest thumbnail.</param>
    /// <param name="IsLive">True when that video was on air at resolve time.</param>
    /// <param name="Avatar">The channel's own picture, learned from a <c>/next</c> owner block on a page visit. Null
    /// until some page taught us; a resolve alone never learns it, because the player response has no avatar.</param>
    private sealed record ChannelSnapshot(string ChannelId, string? Name, string VideoId, string VideoTitle,
        string? Thumbnail, bool IsLive, string? Avatar);

    /// <summary>
    /// Records what one video taught us about its channel, MERGING rather than replacing: the avatar only ever
    /// arrives from a page visit and the rest only from the most recent visit of either kind, so a later resolve
    /// must not erase a picture an earlier page learned. That merge is what keeps <see cref="ChannelPage"/> free of
    /// http calls — the avatar is already in hand by the time the channel page is asked for.
    /// </summary>
    /// <param name="channelId">The channel id.</param>
    /// <param name="name">The channel name, or null when unknown.</param>
    /// <param name="videoId">The video that taught us.</param>
    /// <param name="videoTitle">That video's title.</param>
    /// <param name="thumbnail">That video's widest thumbnail.</param>
    /// <param name="isLive">True when that video was on air.</param>
    /// <param name="avatar">The channel avatar, or null to keep whatever is already known.</param>
    private void RememberChannel(string channelId, string? name, string videoId, string videoTitle,
        string? thumbnail, bool isLive, string? avatar)
        => _channels.AddOrUpdate(
            channelId,
            _ => new ChannelSnapshot(channelId, name, videoId, videoTitle, thumbnail, isLive, avatar),
            (_, old) => new ChannelSnapshot(channelId, name ?? old.Name, videoId, videoTitle,
                thumbnail ?? old.Thumbnail, isLive, avatar ?? old.Avatar));

    /// <summary>The watch url for a video id.</summary>
    /// <param name="videoId">The 11-character video id.</param>
    public static string WatchUrl(string videoId) => "https://www.youtube.com/watch?v=" + videoId;

    /// <summary>The channel url for a channel id.</summary>
    /// <param name="channelId">The <c>UC…</c> channel id.</param>
    public static string ChannelUrl(string channelId) => "https://www.youtube.com/channel/" + channelId;

    /// <inheritdoc/>
    public override async ValueTask<ModulePageDoc?> GetPageAsync(string entityId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(entityId)) return null;
        string id = entityId.Trim();

        if (id.StartsWith(VideoEntityPrefix, StringComparison.Ordinal))
        {
            string videoId = id[VideoEntityPrefix.Length..];
            if (!YouTubeUrls.IsVideoId(videoId)) return null;

            // Concurrent on purpose: /next describes the SAME video from a different endpoint, so serialising the two
            // would add its whole round trip to every page open. /player is the one allowed to fail the page;
            // WatchNextAsync answers null instead of throwing, so Task.WhenAll can only surface /player's verdict.
            EnsureClients();
            Task<YtPlayerResponse> player = PlayerForPageAsync(videoId, ct);
            Task<WatchNextInfo?> next = WatchNextAsync(videoId, ct);
            await Task.WhenAll(player, next).ConfigureAwait(false);
            return VideoPage(videoId, player.Result, next.Result);
        }

        if (id.StartsWith(ChannelEntityPrefix, StringComparison.Ordinal))
        {
            string channelId = id[ChannelEntityPrefix.Length..];
            return channelId.Length == 0 ? null : ChannelPage(channelId);
        }

        return null;
    }

    /// <summary>
    /// Fetches a player response for the PAGE, which is a weaker ask than <see cref="ResolveAsync(string,CancellationToken)"/>:
    /// no HLS manifest is required and no preflight runs, because a page is worth showing for a video that will not
    /// play (offline broadcast, SABR-only session). Client fallback and the IP-block detection are the same.
    /// </summary>
    /// <param name="videoId">The video to describe.</param>
    /// <param name="ct">Cancels the fetch.</param>
    private async Task<YtPlayerResponse> PlayerForPageAsync(string videoId, CancellationToken ct)
    {
        ModuleErrorCode lastCode = ModuleErrorCode.Unavailable;
        string lastReason = "YouTube would not describe this video to any of the configured clients.";

        YouTubeClient[] clients = Clients;
        for (int i = 0; i < clients.Length; i++)
        {
            YtPlayerResponse? player;
            try
            {
                player = await PlayerAsync(clients[i], videoId, ct).ConfigureAwait(false);
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

            if (player?.VideoDetails?.VideoId is not { Length: > 0 } got)
            {
                lastCode = ModuleErrorCode.Unavailable;
                lastReason = string.IsNullOrWhiteSpace(player?.PlayabilityStatus?.Reason)
                    ? "YouTube returned nothing about this video."
                    : player!.PlayabilityStatus!.Reason!;
                continue;
            }

            if (!string.Equals(got, videoId, StringComparison.Ordinal))
            {
                lastCode = ModuleErrorCode.Unavailable;
                lastReason = "YouTube is blocking this network (it answered with a different video).";
                continue;
            }

            return player;
        }

        throw new ModuleException(lastCode, lastReason);
    }

    /// <summary>
    /// Builds the <c>video:&lt;id&gt;</c> page: a WATCH document (<see cref="ModulePageDoc.TemplateWatch"/>), because
    /// a video's identity IS its picture. The <c>facts</c> and <c>text</c> sections are exactly what they always were
    /// — the watch layout folds them into its description card, and an app that does not know the template still
    /// renders the same document the old way.
    /// </summary>
    /// <param name="videoId">The video id.</param>
    /// <param name="player">The player response describing it.</param>
    /// <param name="next">What <c>/next</c> added, or null when it did not answer. Null costs the page its avatar,
    /// its concurrent-viewer count, its date line and its up-next shelf — and nothing else.</param>
    private ModulePageDoc VideoPage(string videoId, YtPlayerResponse player, WatchNextInfo? next)
    {
        YtVideoDetails? d = player.VideoDetails;
        YtPlayerMicroformatRenderer? micro = player.Microformat?.PlayerMicroformatRenderer;
        YtLiveBroadcastDetails? broadcast = micro?.LiveBroadcastDetails;
        bool isLive = (d?.IsLive ?? false) || (broadcast?.IsLiveNow ?? false);

        string title = string.IsNullOrWhiteSpace(d?.Title) ? videoId : d!.Title!;
        string? author = Blank(d?.Author) ?? Blank(micro?.OwnerChannelName) ?? next?.OwnerName;
        string? channelId = Blank(d?.ChannelId) ?? Blank(micro?.ExternalChannelId) ?? next?.OwnerChannelId;
        string? views = FormatCount(d?.ViewCount) ?? FormatCount(micro?.ViewCount);
        string? length = isLive ? null : FormatSeconds(d?.LengthSeconds);
        string? thumbnail = WidestThumbnail(d?.Thumbnail?.Thumbnails);

        var facts = new List<string[]>(4);
        if (views is not null) facts.Add(["Views", views]);
        if (length is not null) facts.Add(["Length", length]);
        if (author is not null) facts.Add(["Channel", author]);
        if (isLive && broadcast?.StartTimestamp is { Length: > 0 } start && TryInstant(start, out DateTimeOffset at))
        {
            facts.Add(["Started", at.UtcDateTime.ToString("u", CultureInfo.InvariantCulture)]);
        }

        var sections = new List<PageSection>(4);
        if (facts.Count > 0) sections.Add(PageSection.FromFacts([.. facts], "About"));
        if (Blank(d?.ShortDescription) is { } description)
        {
            sections.Add(PageSection.FromText(description, "Description"));
        }

        // The hero now carries SubtitleEntityId, but the one-card shelf stays: it is what an app that does not read
        // the new hero member falls back to, and dropping it would silently strip the channel link from that app.
        if (channelId is not null)
        {
            sections.Add(PageSection.FromCards(
                [new PageItem(author ?? "YouTube channel", "Channel", next?.OwnerAvatarUrl, null,
                    ChannelEntityPrefix + channelId, null, null, false, null)],
                "Channel"));
        }

        if (next?.Related is { Length: > 0 } related)
        {
            sections.Add(PageSection.FromPlayables(related, "Up next"));
        }

        // The date line is YouTube's own rendered string when /next answered ("Started streaming 3 hours ago"), and
        // the microformat's own date otherwise. Never computed here: relative time is YouTube's arithmetic in the
        // language the request asked for, or it is not shown at all.
        string? dateText = next?.DateText ?? IsoDate(micro?.PublishDate ?? micro?.UploadDate);

        // A live page quotes the CONCURRENT audience. `videoDetails.viewCount` on a broadcast is a lifetime total
        // that climbs whether or not anyone is watching, so it is the wrong number to print beside a LIVE badge and
        // only appears once /next says the count is not a live one.
        string? audience = next is { ViewCountIsLive: true, ViewCountText: { Length: > 0 } watching }
            ? watching
            : views is null ? null : views + " views";

        if (channelId is not null)
        {
            RememberChannel(channelId, author, videoId, title, thumbnail, isLive, next?.OwnerAvatarUrl);
        }

        var hero = new PageHero(
            title,
            isLive ? "Live stream" : "Video",
            author,
            thumbnail,
            MetaLine(isLive ? "Live now" : length, audience, dateText),
            isLive,
            AvatarUrl: next?.OwnerAvatarUrl,
            SubtitleEntityId: channelId is null ? null : ChannelEntityPrefix + channelId);

        return new ModulePageDoc(
            ModulePageDoc.CurrentVersion,
            ModulePageDoc.TemplateWatch,
            hero,
            [
                PageAction.Play(videoId, "Play"),
                PageAction.OpenUrl(WatchUrl(videoId), "Open on YouTube"),
            ],
            [.. sections],
            ExpiresAtUnixMs: isLive
                ? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + LivePageTtlMs
                : null);
    }

    /// <summary>
    /// Builds the <c>channel:&lt;id&gt;</c> page. YouTube's player endpoint describes a VIDEO, never a channel, so
    /// this page says exactly what a resolve happened to learn and sends the user to the browser for the rest —
    /// no invented follower counts, no scraped shelves.
    /// </summary>
    /// <param name="channelId">The channel id.</param>
    private ModulePageDoc ChannelPage(string channelId)
    {
        _channels.TryGetValue(channelId, out ChannelSnapshot? snapshot);

        var sections = new List<PageSection>(2);
        if (snapshot is { IsLive: true })
        {
            sections.Add(PageSection.FromPlayables(
                [new PageItem(snapshot.VideoTitle, snapshot.Name, snapshot.Thumbnail, snapshot.VideoId,
                    VideoEntityPrefix + snapshot.VideoId, null, MediaForm.Video, true, "Live")],
                "Live now"));
        }
        else
        {
            sections.Add(PageSection.FromText(
                "Wavee builds this page from YouTube's player response, which only describes the video you played. " +
                "Open the channel on YouTube for its videos, playlists and about page.",
                "About this page"));
        }

        // The avatar costs no request: it was cached by whichever video page last mentioned this channel, so a
        // channel page still makes ZERO http calls of its own.
        var hero = new PageHero(
            snapshot?.Name ?? "YouTube channel",
            "Channel",
            null,
            null,
            null,
            snapshot?.IsLive ?? false,
            AvatarUrl: snapshot?.Avatar);

        return new ModulePageDoc(
            ModulePageDoc.CurrentVersion,
            ModulePageDoc.TemplateEntity,
            hero,
            [PageAction.OpenUrl(ChannelUrl(channelId), "Open on YouTube")],
            [.. sections],
            ExpiresAtUnixMs: null);
    }

    /// <summary>Joins the non-empty parts of a meta line with a middle dot.</summary>
    /// <param name="parts">The candidate parts, nulls and blanks skipped.</param>
    private static string? MetaLine(params string?[] parts)
    {
        string joined = string.Join(" · ", parts.Where(p => !string.IsNullOrWhiteSpace(p)));
        return joined.Length == 0 ? null : joined;
    }

    /// <summary>Formats an InnerTube count string with thousands separators, or null when it is not a number.</summary>
    /// <param name="raw">The raw value, e.g. <c>"1234"</c>.</param>
    public static string? FormatCount(string? raw)
        => long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out long n) && n >= 0
            ? n.ToString("N0", CultureInfo.InvariantCulture)
            : null;

    /// <summary>Formats an InnerTube <c>lengthSeconds</c> as <c>h:mm:ss</c> / <c>m:ss</c>, or null when it is 0.</summary>
    /// <param name="raw">The raw value, e.g. <c>"3672"</c>.</param>
    public static string? FormatSeconds(string? raw)
    {
        if (!long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out long s) || s <= 0) return null;
        var span = TimeSpan.FromSeconds(s);
        return span.TotalHours >= 1
            ? string.Create(CultureInfo.InvariantCulture, $"{(int)span.TotalHours}:{span.Minutes:00}:{span.Seconds:00}")
            : string.Create(CultureInfo.InvariantCulture, $"{span.Minutes}:{span.Seconds:00}");
    }

    private static bool TryInstant(string text, out DateTimeOffset at)
        => DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out at);

    /// <summary>Renders an InnerTube ISO-8601 instant as a bare <c>yyyy-MM-dd</c>, or null when it is not one.</summary>
    /// <param name="raw">The microformat value, e.g. <c>"2026-08-20T09:00:00-07:00"</c>.</param>
    private static string? IsoDate(string? raw)
        => Blank(raw) is { } text && TryInstant(text, out DateTimeOffset at)
            ? at.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            : null;

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
            WriteClient(w, client);
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

    /// <summary>
    /// What <c>/next</c> added to a page, already flattened out of InnerTube's renderer nesting. Every member is
    /// optional: this record existing means the endpoint answered with a shape we recognised, not that it told us
    /// everything.
    /// </summary>
    /// <param name="OwnerName">The channel name from the owner row.</param>
    /// <param name="OwnerChannelId">The owner row's browse id.</param>
    /// <param name="OwnerAvatarUrl">The channel's widest avatar — the one thing no other endpoint gives us.</param>
    /// <param name="SubscriberText">YouTube's rendered subscriber line, kept verbatim.</param>
    /// <param name="ViewCountText">The rendered view/watching line, kept verbatim.</param>
    /// <param name="ViewCountIsLive">True when <paramref name="ViewCountText"/> is a concurrent count.</param>
    /// <param name="DateText">YouTube's rendered date line, kept verbatim.</param>
    /// <param name="Related">The up-next shelf, already page items.</param>
    private sealed record WatchNextInfo(
        string? OwnerName,
        string? OwnerChannelId,
        string? OwnerAvatarUrl,
        string? SubscriberText,
        string? ViewCountText,
        bool ViewCountIsLive,
        string? DateText,
        PageItem[] Related);

    /// <summary>
    /// Asks <c>/next</c> about a video and flattens the answer. NEVER throws for a YouTube-side problem: a transport
    /// error, an unparseable body or a shape we no longer recognise all cost one log line and return null, and the
    /// page then renders exactly as it did before this endpoint existed. Only the CALLER's cancellation propagates.
    /// </summary>
    /// <param name="videoId">The video to describe.</param>
    /// <param name="ct">Cancels the fetch.</param>
    private async Task<WatchNextInfo?> WatchNextAsync(string videoId, CancellationToken ct)
    {
        if (MetadataClient is not { } client) return null;

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, NextEndpoint);
            request.Headers.TryAddWithoutValidation("User-Agent", client.UserAgent);
            request.Headers.TryAddWithoutValidation("X-YouTube-Client-Name",
                client.ClientId.ToString(CultureInfo.InvariantCulture));
            request.Headers.TryAddWithoutValidation("X-YouTube-Client-Version", client.ClientVersion);
            request.Headers.TryAddWithoutValidation("Origin", "https://www.youtube.com");
            request.Headers.TryAddWithoutValidation("Accept-Language", "en-US,en;q=0.9");
            // Same interstitial the channel scrape dodges: an EU exit node otherwise gets a consent page here too.
            request.Headers.TryAddWithoutValidation("Cookie", ConsentCookie);
            request.Content = new ByteArrayContent(NextBody(client, videoId));
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };

            using HttpResponseMessage response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                LogNextSkipped(videoId, $"the next endpoint answered {(int)response.StatusCode}");
                return null;
            }

            byte[] body = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            WatchNextInfo? info = Digest(JsonSerializer.Deserialize(body, YouTubeJsonContext.Default.YtNextResponse));
            if (info is null) LogNextSkipped(videoId, "the next response carried no watch-next results");
            return info;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            LogNextSkipped(videoId, ex.Message);
            return null;
        }
    }

    /// <summary>The single line a lost enrichment is allowed to cost.</summary>
    /// <param name="videoId">The video the page is about.</param>
    /// <param name="why">What went wrong, in YouTube's or the runtime's own words.</param>
    private void LogNextSkipped(string videoId, string why)
    {
        if (HasHost) Host.Log(ModuleLogLevel.Warn, $"No watch-next enrichment for {videoId}: {why}");
    }

    /// <summary>
    /// Flattens a <c>/next</c> response, or returns null when it carried nothing worth a page. Every step is
    /// defensive by construction — an unknown renderer deserializes to all-null and is skipped rather than fatal,
    /// which is how a shelf survives InnerTube reshuffling the document around it.
    /// </summary>
    /// <param name="response">The parsed response, possibly null.</param>
    private static WatchNextInfo? Digest(YtNextResponse? response)
    {
        if (response?.Contents?.TwoColumnWatchNextResults is not { } two) return null;

        YtVideoPrimaryInfoRenderer? primary = null;
        YtVideoOwnerRenderer? owner = null;
        foreach (YtWatchNextContent content in two.Results?.Results?.Contents ?? [])
        {
            primary ??= content.VideoPrimaryInfoRenderer;
            owner ??= content.VideoSecondaryInfoRenderer?.Owner?.VideoOwnerRenderer;
        }

        YtVideoViewCountRenderer? counter = primary?.ViewCount?.VideoViewCountRenderer;
        string? viewText = YtText.Plain(counter?.ViewCount) ?? YtText.Plain(counter?.ExtraShortViewCount);

        // Lockup FIRST, renderer second. Both shapes are in flight upstream at once (see YtSecondaryResult), so an
        // entry is read in whichever spelling it arrived in rather than the rail being classified as one or other.
        var related = new List<PageItem>(MaxRelated);
        foreach (YtSecondaryResult entry in two.SecondaryResults?.SecondaryResults?.Results ?? [])
        {
            if (related.Count == MaxRelated) break;
            PageItem? item = FromLockup(entry.LockupViewModel) ?? FromCompactVideo(entry.CompactVideoRenderer);
            if (item is not null) related.Add(item);
        }

        var info = new WatchNextInfo(
            YtText.Plain(owner?.Title),
            Blank(owner?.NavigationEndpoint?.BrowseEndpoint?.BrowseId),
            WidestThumbnail(owner?.Thumbnail?.Thumbnails),
            YtText.Plain(owner?.SubscriberCountText),
            viewText,
            counter?.IsLive ?? false,
            YtText.Plain(primary?.DateText),
            [.. related]);

        // "The endpoint answered 200" is not the same as "the endpoint told us something": a document whose every
        // recognised member came back empty is reported as a miss, so the log says so and the page stays honest.
        bool anything = info.OwnerName is not null || info.OwnerAvatarUrl is not null ||
                        info.ViewCountText is not null || info.DateText is not null || info.Related.Length > 0;
        return anything ? info : null;
    }

    /// <summary>
    /// Turns one <c>lockupViewModel</c> rail entry into a page item, or null when it is not a playable video.
    /// <para>
    /// The view-model surface is positional where the renderer surface was named: the title and every fact are plain
    /// <c>{"content":"…"}</c> strings, the duration is an overlay BADGE on the thumbnail rather than a
    /// <c>lengthText</c> member, and the channel is simply row 0 of a metadata grid. All of that is transcribed from
    /// a real capture (2026-08-23); nothing here is reconstructed from the older shape by analogy.
    /// </para>
    /// </summary>
    /// <param name="lockup">The lockup, possibly null.</param>
    private static PageItem? FromLockup(YtLockupViewModel? lockup)
    {
        if (lockup is null) return null;

        // A playlist or mix lockup carries a LIST id in contentId; handing that to the resolve path would fail, so
        // anything that is not explicitly a video is skipped rather than guessed at.
        if (!string.Equals(lockup.ContentType, YtLockupViewModel.VideoContentType, StringComparison.Ordinal))
        {
            return null;
        }

        if (Blank(lockup.ContentId) is not { } id || !YouTubeUrls.IsVideoId(id)) return null;

        YtThumbnailViewModel? thumbnail = lockup.ContentImage?.ThumbnailViewModel;
        string? badge = null;
        foreach (YtThumbnailOverlay overlay in thumbnail?.Overlays ?? [])
        {
            foreach (YtThumbnailBadge entry in overlay.ThumbnailBottomOverlayViewModel?.Badges ?? [])
            {
                badge ??= Blank(entry.ThumbnailBadgeViewModel?.Text);
            }
        }

        YtLockupMetadataViewModel? meta = lockup.Metadata?.LockupMetadataViewModel;
        YtMetadataRow[] rows = meta?.Metadata?.ContentMetadataViewModel?.MetadataRows ?? [];

        string? channel = null;
        string? watching = null;
        for (int row = 0; row < rows.Length; row++)
        {
            foreach (YtMetadataPart part in rows[row].MetadataParts ?? [])
            {
                if (Blank(part.Text?.Content) is not { } text) continue;

                // Row 0 was the channel name in every entry of the capture; the later rows are views and age.
                if (row == 0) channel ??= text;
                else if (watching is null && text.Contains("watching", StringComparison.OrdinalIgnoreCase))
                {
                    watching = text;
                }
            }
        }

        // INFERRED, not observed: the capture held no live entry, so the live test is the two things YouTube visibly
        // does elsewhere — a badge reading LIVE instead of a duration, and a "watching" count in place of views. If
        // the real shape turns out to be a style token instead, this reads false and the entry is simply not badged.
        bool live = watching is not null ||
                    (badge is not null && badge.Equals("LIVE", StringComparison.OrdinalIgnoreCase));

        return new PageItem(
            Blank(meta?.Title?.Content) ?? id,
            channel,
            WidestThumbnail(thumbnail?.Image?.Sources),
            id,
            VideoEntityPrefix + id,
            null,
            MediaForm.Video,
            live,
            live ? watching ?? badge : badge);
    }

    /// <summary>Turns one <c>compactVideoRenderer</c> rail entry into a page item, or null when it is not one. The
    /// older shape, still served to some sessions.</summary>
    /// <param name="video">The renderer, possibly null.</param>
    private static PageItem? FromCompactVideo(YtCompactVideoRenderer? video)
    {
        if (video is null) return null;
        if (Blank(video.VideoId) is not { } id || !YouTubeUrls.IsVideoId(id)) return null;

        bool live = false;
        foreach (YtBadge badge in video.Badges ?? [])
        {
            if (string.Equals(badge.MetadataBadgeRenderer?.Style, YtMetadataBadgeRenderer.LiveNowStyle,
                    StringComparison.Ordinal))
            {
                live = true;
                break;
            }
        }

        // A live entry has no duration to show, so its trailing fact is the audience instead. Both strings are
        // YouTube's own rendering ("1:01:12", "12K watching") and neither is recomputed here.
        string? meta = live
            ? YtText.Plain(video.ViewCountText)
            : YtText.Plain(video.LengthText) ?? YtText.Plain(video.ViewCountText);

        return new PageItem(
            YtText.Plain(video.Title) ?? id,
            YtText.Plain(video.LongBylineText),
            WidestThumbnail(video.Thumbnail?.Thumbnails),
            id,
            VideoEntityPrefix + id,
            null,
            MediaForm.Video,
            live,
            meta);
    }

    /// <summary>
    /// Builds the <c>/next</c> request body: a video id and a client context, nothing else. Same hand-written
    /// <see cref="Utf8JsonWriter"/> style as <see cref="PlayerBody"/>, and deliberately WITHOUT the playback and
    /// content-check members — this endpoint plays nothing, so asking it to is noise.
    /// </summary>
    /// <param name="client">The metadata client block to send.</param>
    /// <param name="videoId">The video to ask about.</param>
    public static byte[] NextBody(YouTubeClient client, string videoId)
    {
        var buffer = new ArrayBufferWriter<byte>(512);
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteString("videoId", videoId);
            w.WritePropertyName("context");
            w.WriteStartObject();
            WriteClient(w, client);
            w.WriteEndObject();
            w.WriteEndObject();
        }

        return buffer.WrittenSpan.ToArray();
    }

    /// <summary>Writes the shared <c>"client"</c> block both InnerTube bodies carry.</summary>
    /// <param name="w">The writer, positioned inside the <c>context</c> object.</param>
    /// <param name="client">The client block to describe.</param>
    private static void WriteClient(Utf8JsonWriter w, YouTubeClient client)
    {
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
        // hl=en is load-bearing: every string this module shows verbatim ("Started streaming 3 hours ago",
        // "12K watching") is rendered by YouTube in the language asked for here.
        w.WriteString("hl", "en");
        w.WriteString("timeZone", "UTC");
        w.WriteNumber("utcOffsetMinutes", 0);
        w.WriteEndObject();
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
    /// Loads the whole client table — both roles, in file order: a <c>clients.json</c> in the module's data dir wins
    /// over the one shipped beside the exe, which wins over the built-in table. YouTube retires client versions every
    /// few weeks, so this is deliberately data the user (or an update) can replace without a new module build.
    /// <see cref="EnsureClients"/> is what splits the result into the <c>/player</c> walk and the <c>/next</c> client.
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
