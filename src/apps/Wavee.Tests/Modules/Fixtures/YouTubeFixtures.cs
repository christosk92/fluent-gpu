namespace Wavee.Tests.Modules.Fixtures;

/// <summary>
/// Sanitized <c>youtubei/v1/player</c> responses and channel-page HTML, shaped exactly like the real endpoints
/// (member names, nesting and value types) with the identifying parts replaced. Kept as raw string literals so the
/// bodies are verbatim JSON but need no copy-to-output plumbing in the test csproj.
/// </summary>
public static class YouTubeFixtures
{
    /// <summary>The video id every fixture talks about.</summary>
    public const string VideoId = "tRsQsTMvPNg";

    /// <summary>The signed HLS master url the OK fixtures return; carries <c>/expire/1767225600/</c>.</summary>
    public const string HlsManifestUrl =
        "https://manifest.googlevideo.com/api/manifest/hls_variant/expire/1767225600/ei/AAAAAAAAAAAA/ip/" +
        "203.0.113.7/id/tRsQsTMvPNg.1/source/yt_live_broadcast/requiressl/yes/hfr/1/playlist_type/DVR/" +
        "sparams/expire%2Cei%2Cip%2Cid/sig/AAAAAAAA/playlist/index.m3u8";

    /// <summary>A live broadcast that plays: status OK, matching id, an HLS master and a session lifetime.</summary>
    public const string PlayerLiveOk = $$"""
    {
      "responseContext": { "visitorData": "CgtBQUFBQUFBQUFBQQ%3D%3D" },
      "playabilityStatus": { "status": "OK", "playableInEmbed": true },
      "streamingData": {
        "expiresInSeconds": "21540",
        "formats": [],
        "adaptiveFormats": [],
        "dashManifestUrl": "https://manifest.googlevideo.com/api/manifest/dash/expire/1767225600/x/y",
        "hlsManifestUrl": "{{HlsManifestUrl}}"
      },
      "videoDetails": {
        "videoId": "{{VideoId}}",
        "title": "Claude FM",
        "lengthSeconds": "0",
        "isLive": true,
        "channelId": "UCAAAAAAAAAAAAAAAAAAAAA",
        "isOwnerViewing": false,
        "isCrawlable": true,
        "thumbnail": {
          "thumbnails": [
            { "url": "https://i.ytimg.com/vi/tRsQsTMvPNg/default.jpg", "width": 120, "height": 90 },
            { "url": "https://i.ytimg.com/vi/tRsQsTMvPNg/maxresdefault.jpg", "width": 1280, "height": 720 }
          ]
        },
        "allowRatings": true,
        "viewCount": "1234",
        "author": "Anthropic",
        "isPrivate": false,
        "isUnpluggedCorpus": false,
        "isLiveContent": true
      },
      "microformat": {
        "playerMicroformatRenderer": {
          "lengthSeconds": "0",
          "isFamilySafe": true,
          "liveBroadcastDetails": { "isLiveNow": true, "startTimestamp": "2026-08-20T09:00:00-07:00" }
        }
      }
    }
    """;

    /// <summary>A regular (finished) video that plays: a real <c>lengthSeconds</c>, no live flags.</summary>
    public const string PlayerVodOk = $$"""
    {
      "playabilityStatus": { "status": "OK" },
      "streamingData": { "expiresInSeconds": "21540", "hlsManifestUrl": "{{HlsManifestUrl}}" },
      "videoDetails": {
        "videoId": "{{VideoId}}",
        "title": "A recorded talk",
        "lengthSeconds": "3672",
        "author": "Anthropic",
        "isLive": false,
        "isLiveContent": false,
        "thumbnail": { "thumbnails": [ { "url": "https://i.ytimg.com/vi/x/hq.jpg", "width": 480, "height": 360 } ] }
      },
      "microformat": { "playerMicroformatRenderer": { "liveBroadcastDetails": { "isLiveNow": false } } }
    }
    """;

    /// <summary>YouTube answered about a different video — yt-dlp reads this as "your IP is being blocked".</summary>
    public const string PlayerVideoIdMismatch = $$"""
    {
      "playabilityStatus": { "status": "OK" },
      "streamingData": { "expiresInSeconds": "21540", "hlsManifestUrl": "{{HlsManifestUrl}}" },
      "videoDetails": { "videoId": "aaaaaaaaaaa", "title": "Something else", "author": "Someone",
                        "lengthSeconds": "60", "isLive": false, "isLiveContent": false }
    }
    """;

    /// <summary>Status OK but only a SABR endpoint: nothing Media Foundation can open.</summary>
    public const string PlayerSabrOnly = $$"""
    {
      "playabilityStatus": { "status": "OK" },
      "streamingData": {
        "expiresInSeconds": "21540",
        "serverAbrStreamingUrl": "https://rr1---sn-abcd.googlevideo.com/videoplayback?expire=1767225600&sabr=1",
        "adaptiveFormats": []
      },
      "videoDetails": { "videoId": "{{VideoId}}", "title": "Claude FM", "author": "Anthropic",
                        "lengthSeconds": "0", "isLive": true, "isLiveContent": true }
    }
    """;

    /// <summary>"Sign in to confirm you're not a bot" — the datacenter/VPN wall. Terminal for every client.</summary>
    public const string PlayerBotWall = """
    {
      "playabilityStatus": {
        "status": "LOGIN_REQUIRED",
        "reason": "Sign in to confirm you're not a bot",
        "errorScreen": { "playerErrorMessageRenderer": { "reason": { "simpleText": "Sign in to confirm you're not a bot" } } }
      },
      "videoDetails": { "videoId": "tRsQsTMvPNg", "title": "Claude FM", "author": "Anthropic",
                        "lengthSeconds": "0", "isLive": true, "isLiveContent": true }
    }
    """;

    /// <summary>An age-gated video: the legacy age-gate marker plus the matching reason.</summary>
    public const string PlayerAgeGate = """
    {
      "playabilityStatus": {
        "status": "LOGIN_REQUIRED",
        "reason": "Sign in to confirm your age",
        "desktopLegacyAgeGateReason": 1
      },
      "videoDetails": { "videoId": "tRsQsTMvPNg", "title": "Age restricted", "author": "Someone",
                        "lengthSeconds": "300", "isLive": false, "isLiveContent": false }
    }
    """;

    /// <summary>A scheduled broadcast that has not started.</summary>
    public const string PlayerLiveOffline = """
    {
      "playabilityStatus": {
        "status": "LIVE_STREAM_OFFLINE",
        "reason": "This live event will begin in a few moments."
      },
      "videoDetails": { "videoId": "tRsQsTMvPNg", "title": "Scheduled broadcast", "author": "Anthropic",
                        "lengthSeconds": "0", "isLive": false, "isUpcoming": true, "isLiveContent": true },
      "microformat": {
        "playerMicroformatRenderer": {
          "liveBroadcastDetails": { "isLiveNow": false, "startTimestamp": "2026-09-01T17:00:00+00:00" }
        }
      }
    }
    """;

    /// <summary>An outright refusal for this client ("made for kids", "not available on this app", …).</summary>
    public const string PlayerUnplayable = """
    {
      "playabilityStatus": {
        "status": "UNPLAYABLE",
        "reason": "This video is not available on this app."
      },
      "videoDetails": { "videoId": "tRsQsTMvPNg", "title": "Kids video", "author": "Someone",
                        "lengthSeconds": "120", "isLive": false, "isLiveContent": false }
    }
    """;

    /// <summary>The HLS master the preflight GET reads.</summary>
    public const string HlsMaster = """
    #EXTM3U
    #EXT-X-INDEPENDENT-SEGMENTS
    #EXT-X-STREAM-INF:BANDWIDTH=1478400,CODECS="avc1.4d401f,mp4a.40.2",RESOLUTION=854x480,FRAME-RATE=30
    https://manifest.googlevideo.com/api/manifest/hls_playlist/expire/1767225600/id/x.1/itag/93/playlist/index.m3u8
    """;

    /// <summary>A channel /live page whose player state carries the current broadcast's id.</summary>
    public const string ChannelLiveHtmlWithEndpoint = """
    <!DOCTYPE html><html><head><title>Anthropic - Live</title>
    <link rel="canonical" href="https://www.youtube.com/@anthropic">
    </head><body><script>var ytInitialData = {"responseContext":{},"contents":{},
    "currentVideoEndpoint":{"clickTrackingParams":"AAAA","commandMetadata":{"webCommandMetadata":
    {"url":"/watch?v=tRsQsTMvPNg","webPageType":"WEB_PAGE_TYPE_WATCH"}},
    "watchEndpoint":{"videoId":"tRsQsTMvPNg","params":"BBBB"}}};</script></body></html>
    """;

    /// <summary>A channel /live page with no player state, only the canonical watch link.</summary>
    public const string ChannelLiveHtmlCanonicalOnly = """
    <!DOCTYPE html><html><head><title>Anthropic - Live</title>
    <link rel="canonical" href="https://www.youtube.com/watch?v=tRsQsTMvPNg">
    </head><body><script>var ytInitialData = {"responseContext":{}};</script></body></html>
    """;

    /// <summary>A channel /live page for a channel that is not broadcasting.</summary>
    public const string ChannelLiveHtmlOffline = """
    <!DOCTYPE html><html><head><title>Anthropic</title>
    <link rel="canonical" href="https://www.youtube.com/@anthropic">
    </head><body><script>var ytInitialData = {"responseContext":{},"contents":{}};</script></body></html>
    """;
}
