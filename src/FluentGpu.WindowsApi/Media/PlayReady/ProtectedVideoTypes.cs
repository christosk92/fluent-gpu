using System;
using System.Threading.Tasks;
using FluentGpu.Media;

namespace FluentGpu.WindowsApi.Media.PlayReady;

/// <summary>
/// The open request for ONE protected source: the DASH segment grid (an init URL plus the arithmetic
/// <c>base + prefix + (startNumber + i·stride) + suffix</c> template, for video and optionally its own soundtrack),
/// the PlayReady init data (PSSH + KID), the app license relay (spec §9.2 <c>WithDrm</c>), and — new with the runtime
/// rework — the position to OPEN AT and the session's retention window. Mapped 1:1 onto the native
/// <c>FgPrOpenDesc</c> by <see cref="ProtectedVideoSession"/>.
/// <para>License acquisition lives in the managed relay, never native; the native side only relays the CDM's
/// challenge up and the license bytes back down.</para>
/// </summary>
public sealed record ProtectedVideoRequest
{
    /// <summary>All selectable protected tracks and representations, when the manifest exposes an adaptive catalog.</summary>
    public ProtectedAdaptiveCatalog? Catalog { get; init; }
    /// <summary>The originating <see cref="MediaSource"/> (advisory — carries the URI + metadata), or null. A prepared
    /// session is found again at open time by <see cref="InitUrl"/>, which names the content AND the rung.</summary>
    public MediaSource? Source { get; init; }
    /// <summary>The protection configuration (drives the <see cref="DrmSystem"/> reported to the relay).</summary>
    public DrmConfig? Drm { get; init; }
    /// <summary>The app license relay: a CDM challenge → a license blob. Runs on the pool; nothing native waits for it.</summary>
    public Func<LicenseRequest, ValueTask<LicenseResponse>>? LicenseRelay { get; init; }

    /// <summary>The video init-segment URL.</summary>
    public string? InitUrl { get; init; }
    /// <summary>Base URL for the media segments.</summary>
    public string? SegmentBaseUrl { get; init; }
    /// <summary>Media-segment name prefix.</summary>
    public string? SegmentPrefix { get; init; }
    /// <summary>Media-segment name suffix (e.g. <c>.m4s</c>).</summary>
    public string? SegmentSuffix { get; init; }
    /// <summary>First segment number (Spotify's timestamped segments start at 0; numbered content at 1).</summary>
    public int StartNumber { get; init; } = 1;
    /// <summary>Total media segments in the presentation (0 = derive from <see cref="DurationMs"/>).</summary>
    public int SegmentCount { get; init; }
    /// <summary>Segment-number step: 1 for numbered <c>$Number$</c> content; Spotify names segments by absolute time,
    /// so this is the segment length in seconds (segment <c>i</c> = <see cref="StartNumber"/> + i·stride).</summary>
    public int SegmentStride { get; init; } = 1;
    /// <summary>The PRESENTATION length of one segment in ms (Spotify's <c>segment_length</c> × 1000). This is the index:
    /// segment <c>i</c> starts at <c>i · SegmentLengthMs</c>, and every segment start is a keyframe, so a seek maps to a
    /// segment with no <c>sidx</c> fetch at all. 0 = unknown (the native side falls back to the stride in seconds).</summary>
    public int SegmentLengthMs { get; init; }
    /// <summary>The manifest's presentation duration in ms, or 0 when unknown (the demuxer extrapolates).</summary>
    public long DurationMs { get; init; }
    /// <summary>Optional explicit PlayReady PSSH init data (else parsed natively from the init segment).</summary>
    public ReadOnlyMemory<byte> Pssh { get; init; }
    /// <summary>The content key id as 32 hex characters (dashless). The license cache is keyed by it; without it the
    /// license cannot be acquired ahead of the attach (the attach still works — the key is then acquired on demand).</summary>
    public string? DefaultKid { get; init; }
    /// <summary>Optional <c>"Name: Value\n"</c> HTTP headers applied to segment fetches (auth for a real CDN).</summary>
    public string? HttpHeaders { get; init; }

    /// <summary>Open paused (true) and present the first frame at <see cref="StartPosition"/> without playing, or play as
    /// soon as the first frame is up (false).</summary>
    public bool StartPaused { get; init; } = true;
    /// <summary>Where the session OPENS. The first segment fetched is the one containing this position and the source's
    /// first reposition lands on the keyframe at or before it — a song→video switch at 1:23 never presents 0:00 first.</summary>
    public TimeSpan StartPosition { get; init; } = TimeSpan.Zero;
    /// <summary>Retention behind the playhead, in ms — a backward seek inside it never touches the network.</summary>
    public long RetainBehindMs { get; init; } = ProtectedVideoSession.DefaultRetainBehindMs;
    /// <summary>Forward buffering target, in ms.</summary>
    public long BufferAheadMs { get; init; } = ProtectedVideoSession.DefaultBufferAheadMs;
    /// <summary>The byte cap of this session's segment store.</summary>
    public long StoreBudgetBytes { get; init; } = ProtectedVideoSession.DefaultStoreBudgetBytes;
    /// <summary>The <see cref="System.Diagnostics.Stopwatch.GetTimestamp"/> at which the open that produced this request began (F216):
    /// the origin the session's <c>switch.budget</c> line counts from. A prepared session was created long before the open that takes
    /// it, so the stage times are measured from THIS, not from the session's creation. 0 = unknown (the session then counts from its
    /// own attach).</summary>
    public long OriginTimestamp { get; init; }

    // ── the paired AUDIO representation (optional) — the video's own soundtrack ───────────────────────────────────────
    // Null ⇒ video only. Segment count / start number / stride / length are SHARED with the video: both representations
    // sit on the same segment grid, so only the four URL parts and the codec differ.
    /// <summary>Audio init-segment URL, or null when the source has no usable audio representation.</summary>
    public string? AudioInitUrl { get; init; }
    /// <summary>Base URL for the audio media segments.</summary>
    public string? AudioSegmentBaseUrl { get; init; }
    /// <summary>Audio media-segment name prefix.</summary>
    public string? AudioSegmentPrefix { get; init; }
    /// <summary>Audio media-segment name suffix.</summary>
    public string? AudioSegmentSuffix { get; init; }
    /// <summary>The audio representation's codec string (e.g. <c>mp4a.40.2</c>) — advisory for logs/diagnostics.</summary>
    public string? AudioCodecs { get; init; }
}

/// <summary>The lifecycle state of a protected-video session — the one-to-one managed mirror of the native
/// <c>FgPrState</c>, plus <see cref="Licensed"/>/<see cref="Buffering"/> which the managed session derives from events.</summary>
public enum ProtectedVideoState
{
    /// <summary>No session — created but not attached.</summary>
    Idle = 0,
    /// <summary>Attached; fetching / licensing / building the topology. No first frame yet.</summary>
    Loading,
    /// <summary>The DRM license reached USABLE and the first frame has not landed yet.</summary>
    Licensed,
    /// <summary>Rebuffering mid-playback (the store ran dry ahead of the playhead).</summary>
    Buffering,
    /// <summary>Decoding and presenting frames.</summary>
    Playing,
    /// <summary>Paused by request (a first frame may well be on screen).</summary>
    Paused,
    /// <summary>Playback finished (end of stream).</summary>
    Ended,
    /// <summary>Detached. The session and its store survive — a re-attach resumes without refetching.</summary>
    Stopped,
    /// <summary>A terminal error (see the error signal for detail).</summary>
    Error,
}

/// <summary>
/// Where a protected switch is, for the surfaces: the discriminator between "show the poster", "show a spinner" and
/// "drop the poster, the frame is up". Derived from native EVENTS by <see cref="ProtectedVideoSession"/> (never from a
/// state guess or a timer) and read on the UI thread through <see cref="IProtectedVideoPlayer.Phase"/>. The Wavee host
/// mirrors it one-to-one into its own <c>Playback.Video.Phase</c> signal and adds <see cref="Resolving"/> for its
/// manifest step, which the engine never sees.
/// </summary>
public enum ProtectedVideoPhase : byte
{
    /// <summary>Nothing is attached.</summary>
    Idle = 0,
    /// <summary>The app is resolving the manifest (app-owned; the engine never publishes it).</summary>
    Resolving,
    /// <summary>Attached, and the license for the content KID is still pending.</summary>
    Licensing,
    /// <summary>The license is usable (or not needed yet) and the store is filling toward the start position.</summary>
    Buffering,
    /// <summary>The source is set on the engine; metadata / CANPLAY have not produced a first frame yet.</summary>
    Attaching,
    /// <summary>FIRSTFRAMEREADY landed: the frame at the start position is presentable. The poster drops here.</summary>
    Presenting,
    /// <summary>The clock is running.</summary>
    Playing,
    /// <summary>Terminal failure (license, network, decode) — see the player's error.</summary>
    Failed,
}
