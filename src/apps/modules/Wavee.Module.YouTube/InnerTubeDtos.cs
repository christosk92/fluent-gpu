using System.Text.Json;
using System.Text.Json.Serialization;

namespace Wavee.Module.YouTube;

/// <summary>The subset of <c>youtubei/v1/player</c> this module reads. Unknown members are ignored by STJ.</summary>
/// <param name="PlayabilityStatus">Whether the video can be served to this client at all.</param>
/// <param name="VideoDetails">Title/author/live flags.</param>
/// <param name="StreamingData">Where the HLS master lives and when it dies.</param>
/// <param name="Microformat">Carries the live-broadcast schedule.</param>
public sealed record YtPlayerResponse(
    YtPlayabilityStatus? PlayabilityStatus,
    YtVideoDetails? VideoDetails,
    YtStreamingData? StreamingData,
    YtMicroformat? Microformat);

/// <summary>Why (or whether) YouTube will serve this video to the requesting client.</summary>
/// <param name="Status"><c>OK</c>, <c>LOGIN_REQUIRED</c>, <c>UNPLAYABLE</c>, <c>ERROR</c>, <c>LIVE_STREAM_OFFLINE</c>, …</param>
/// <param name="Reason">Human-readable reason shown verbatim when nothing better is known.</param>
/// <param name="DesktopLegacyAgeGateReason">Present (any value) when the video is age-gated.</param>
public sealed record YtPlayabilityStatus(
    string? Status,
    string? Reason,
    JsonElement? DesktopLegacyAgeGateReason);

/// <summary>The video's own description of itself.</summary>
/// <param name="VideoId">Must equal the requested id; a mismatch means the IP is being blocked.</param>
/// <param name="Title">Display title.</param>
/// <param name="Author">Channel name.</param>
/// <param name="ChannelId">Channel id.</param>
/// <param name="LengthSeconds">Duration in seconds, as a string; <c>"0"</c> for a live stream.</param>
/// <param name="IsLive">True while broadcasting.</param>
/// <param name="IsLiveContent">True for anything that ever was a broadcast.</param>
/// <param name="IsUpcoming">True for a scheduled premiere/broadcast that has not started.</param>
/// <param name="IsPostLiveDvr">True for the DVR window right after a broadcast ended.</param>
/// <param name="IsLowLatencyLiveStream">True for a low-latency broadcast.</param>
/// <param name="IsPrivate">True for a private video.</param>
/// <param name="Thumbnail">Thumbnail set; the widest entry becomes the artwork.</param>
/// <param name="ShortDescription">The video's description, as plain text with real newlines. Page copy only.</param>
/// <param name="ViewCount">Total views (live: concurrent-ish), as a string. Page copy only.</param>
public sealed record YtVideoDetails(
    string? VideoId,
    string? Title,
    string? Author,
    string? ChannelId,
    string? LengthSeconds,
    bool IsLive,
    bool IsLiveContent,
    bool IsUpcoming,
    bool IsPostLiveDvr,
    bool IsLowLatencyLiveStream,
    bool IsPrivate,
    YtThumbnailSet? Thumbnail,
    string? ShortDescription = null,
    string? ViewCount = null);

/// <summary>The thumbnail array wrapper.</summary>
/// <param name="Thumbnails">Thumbnails, smallest first in practice.</param>
public sealed record YtThumbnailSet(YtThumbnail[]? Thumbnails);

/// <summary>One thumbnail.</summary>
/// <param name="Url">Absolute image url.</param>
/// <param name="Width">Pixel width.</param>
/// <param name="Height">Pixel height.</param>
public sealed record YtThumbnail(string? Url, int Width, int Height);

/// <summary>Where the media lives. Only <paramref name="HlsManifestUrl"/> is ever used.</summary>
/// <param name="ExpiresInSeconds">Session lifetime as a string, typically <c>"21540"</c>.</param>
/// <param name="HlsManifestUrl">The HLS master (MPEG-TS renditions) handed to Media Foundation.</param>
/// <param name="DashManifestUrl">Ignored: Win32 Media Engine has no DASH.</param>
/// <param name="ServerAbrStreamingUrl">Present without an HLS url = a SABR-only session.</param>
public sealed record YtStreamingData(
    string? ExpiresInSeconds,
    string? HlsManifestUrl,
    string? DashManifestUrl,
    string? ServerAbrStreamingUrl);

/// <summary>Microformat wrapper.</summary>
/// <param name="PlayerMicroformatRenderer">The renderer holding the broadcast schedule.</param>
public sealed record YtMicroformat(YtPlayerMicroformatRenderer? PlayerMicroformatRenderer);

/// <summary>The renderer holding the broadcast schedule.</summary>
/// <param name="LiveBroadcastDetails">Live-now flag plus start/end timestamps.</param>
public sealed record YtPlayerMicroformatRenderer(YtLiveBroadcastDetails? LiveBroadcastDetails);

/// <summary>The broadcast schedule.</summary>
/// <param name="IsLiveNow">True while the broadcast is on air.</param>
/// <param name="StartTimestamp">ISO-8601 start instant, present for scheduled broadcasts.</param>
/// <param name="EndTimestamp">ISO-8601 end instant, present once it has finished.</param>
public sealed record YtLiveBroadcastDetails(bool IsLiveNow, string? StartTimestamp, string? EndTimestamp);

/// <summary>One InnerTube client block from <c>clients.json</c>.</summary>
/// <param name="Key">Short key used in logs and diagnostics, e.g. <c>visionos</c>.</param>
/// <param name="ClientName">InnerTube <c>clientName</c>, e.g. <c>VISIONOS</c>.</param>
/// <param name="ClientVersion">InnerTube <c>clientVersion</c>.</param>
/// <param name="ClientId">Numeric client id sent as <c>X-YouTube-Client-Name</c>.</param>
/// <param name="UserAgent">The exact UA this client must send.</param>
/// <param name="DeviceMake">Optional <c>deviceMake</c>.</param>
/// <param name="DeviceModel">Optional <c>deviceModel</c>.</param>
/// <param name="OsName">Optional <c>osName</c>.</param>
/// <param name="OsVersion">Optional <c>osVersion</c>.</param>
/// <param name="AndroidSdkVersion">Optional <c>androidSdkVersion</c> (Android only; required by InnerTube).</param>
/// <param name="Warning">Logged when this client is the one that answered (e.g. the iOS 30-second cut-off).</param>
public sealed record YouTubeClient(
    string Key,
    string ClientName,
    string ClientVersion,
    int ClientId,
    string UserAgent,
    string? DeviceMake = null,
    string? DeviceModel = null,
    string? OsName = null,
    string? OsVersion = null,
    int? AndroidSdkVersion = null,
    string? Warning = null);

/// <summary>The <c>clients.json</c> document: the fallback order, as data.</summary>
/// <param name="SchemaVersion">Document version (currently 1).</param>
/// <param name="Clients">Client blocks, tried in order.</param>
public sealed record YouTubeClientTable(int SchemaVersion, YouTubeClient[] Clients);

/// <summary>Source-generated serializer for every JSON shape this module reads. No reflection, so NativeAOT-clean.</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(YtPlayerResponse))]
[JsonSerializable(typeof(YouTubeClientTable))]
public sealed partial class YouTubeJsonContext : JsonSerializerContext;
