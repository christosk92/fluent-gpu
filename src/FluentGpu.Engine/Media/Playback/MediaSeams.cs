using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FluentGpu.Foundation;
using FluentGpu.Pal;

namespace FluentGpu.Media;

// ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────────
// Layer 3 — the seams (spec §4.4, §5.1–§5.6, §7, §9, §10). Interfaces + POD only in M0; the backends land in M1/M2/M5.
// The shapes here are the frozen contract those milestones implement against.
// ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

// ── Network + headers (referenced by DataSpec + per-source overrides) ────────────────────────────────────────────────

/// <summary>A tiny mutable header bag (order-preserving). Deliberately not a dictionary — HTTP allows repeats and the
/// producer just appends. Cold path (per open / per request), never touched on the RT feed thread.</summary>
public sealed class HeaderList
{
    private readonly List<(string Name, string Value)> _items = new();
    /// <summary>The number of headers.</summary>
    public int Count => _items.Count;
    /// <summary>The header at <paramref name="i"/>.</summary>
    public (string Name, string Value) this[int i] => _items[i];
    /// <summary>Append a header (repeats allowed).</summary>
    public void Add(string name, string value) => _items.Add((name, value));
}

/// <summary>A per-request mutation surface handed to <see cref="NetworkOptions.OnRequest"/> (auth injection, header
/// rewriting) — the header list is mutable in place.</summary>
public sealed class NetworkRequest
{
    /// <summary>The request URI.</summary>
    public required string Uri { get; init; }
    /// <summary>The mutable header list the app can append auth/etc onto.</summary>
    public HeaderList Headers { get; } = new();
}

/// <summary>Per-source (or per-player-default) network options (spec §5, example D). The engine owns
/// coalescing/prefetch/firewalling; the app only tweaks requests + buffering targets.</summary>
public sealed record NetworkOptions(
    Func<NetworkRequest, NetworkRequest>? OnRequest = null,
    TimeSpan? ConnectTimeout = null,
    int? MaxRetries = null);

/// <summary>The forward-buffer policy (spec example D). Declarative target — the engine owns eviction/flow control.</summary>
public sealed record BufferPolicy
{
    /// <summary>Steady-state forward target.</summary>
    public TimeSpan TargetForward { get; init; } = TimeSpan.FromSeconds(30);
    /// <summary>Minimum media required to leave initial buffering.</summary>
    public TimeSpan InitialPlayback { get; init; } = TimeSpan.FromSeconds(1.5);
    /// <summary>Minimum media required to resume after an underrun.</summary>
    public TimeSpan ResumePlayback { get; init; } = TimeSpan.FromSeconds(3);
    /// <summary>Backward retention for quick reverse seeks.</summary>
    public TimeSpan RetainBehind { get; init; } = TimeSpan.FromSeconds(30);
    /// <summary>Upper encoded-memory budget; schedulers evict behind first.</summary>
    public long MaxEncodedBytes { get; init; } = 96L * 1024 * 1024;
    /// <summary>What to do on an underrun.</summary>
    public StallPolicy StallPolicy { get; init; } = StallPolicy.Rebuffer;

    public static BufferPolicy Vod { get; } = new();
    public static BufferPolicy Live { get; } = new()
    {
        TargetForward = TimeSpan.FromSeconds(12), InitialPlayback = TimeSpan.FromSeconds(1),
        ResumePlayback = TimeSpan.FromSeconds(2), RetainBehind = TimeSpan.FromMinutes(5)
    };
    public static BufferPolicy LowLatencyLive { get; } = new()
    {
        TargetForward = TimeSpan.FromSeconds(3), InitialPlayback = TimeSpan.FromMilliseconds(500),
        ResumePlayback = TimeSpan.FromSeconds(1), RetainBehind = TimeSpan.FromMinutes(2),
        StallPolicy = StallPolicy.SkipForward
    };
}

// ── DRM relay (spec §9.2) ────────────────────────────────────────────────────────────────────────────────────────────

/// <summary>The protection system a license request targets (spec §9). Native in-process PlayReady is the SHIPPED v1
/// DRM path (spec §9.2 M5, landed — see <c>FluentGpu.WindowsApi/Media/PlayReady/</c>); Widevine-via-WebView2 is an
/// optional later fallback for Widevine-only content; FairPlay is the macOS story.</summary>
public enum DrmSystem : byte { None, Widevine, PlayReady, FairPlay, ClearKey }

/// <summary>Per-source DRM configuration (spec §5 <c>With(DrmConfig)</c>). The engine never sees a content key or a
/// decrypted pixel — DRM attaches at the single protected-handle bind point (spec §9.2).</summary>
public sealed record DrmConfig(DrmSystem System, string? LicenseServerUri = null, MediaContentType? ContentType = null);

/// <summary>An EME-shaped license request (spec §9.2): the CDM emitted a challenge; the app relays it to a license
/// server and returns the <see cref="LicenseResponse"/>. Headlessly testable — no CDM required to exercise the relay.</summary>
public sealed record LicenseRequest(DrmSystem System, ReadOnlyMemory<byte> Challenge, string? KeyId, MediaLocus Locus);

/// <summary>The license blob returned to the CDM (spec §9.2).</summary>
public sealed record LicenseResponse(ReadOnlyMemory<byte> License);

// ── Subtitles (spec §6) ──────────────────────────────────────────────────────────────────────────────────────────────

/// <summary>An external subtitle/caption source (spec §5 <c>WithExternalSubtitle</c>, §6). Rendered by the engine's own
/// GPU text stack, not an OS overlay.</summary>
public sealed record SubtitleSource(string Uri, MediaContentType ContentType)
{
    /// <summary>A subtitle track loaded from a local file (format sniffed on open).</summary>
    public static SubtitleSource FromFile(string path) => new(path, MediaContentType.Sniff());
    /// <summary>A subtitle track loaded from a URL (format sniffed on open).</summary>
    public static SubtitleSource FromUri(string url) => new(url, MediaContentType.Sniff());
}

// ── §5.1 Pull seam — IMediaByteSource (THE load-bearing seam; PlayPlay's front door) ─────────────────────────────────

/// <summary>Random-access = a RE-OPEN-able Range op (ExoPlayer), not an in-place seek. A fresh signed/expiring CDN URL
/// or key can be injected per open.</summary>
public readonly struct DataSpec
{
    /// <summary>The resource uri (interned).</summary>
    public StringId Uri { get; init; }
    /// <summary>The start byte offset.</summary>
    public long Position { get; init; }
    /// <summary>The length to read; <c>-1</c> == to-EOF.</summary>
    public long Length { get; init; }
    /// <summary>Per-open request headers (may be null).</summary>
    public HeaderList? Headers { get; init; }
}

/// <summary>Declared source capabilities (spec §5.1) — seekability is a DECLARED capability, never an assumed guarantee.</summary>
public readonly struct SourceCaps
{
    /// <summary>The source can seek at all.</summary>
    public bool Seekable { get; init; }
    /// <summary>A seek is expensive (a network re-open) — the engine avoids it on the hot path.</summary>
    public bool ExpensiveSeek { get; init; }
    /// <summary>The total length is known up front.</summary>
    public bool KnownLength { get; init; }
}

/// <summary>The canonical low-level SYNCHRONOUS <c>read(2)</c>-style byte seam (spec §5.1). The engine runs it on a
/// firewalled worker thread (blocking is fine there) and coalesces reads; it is the shape <see cref="DecryptingSource"/>
/// composes over. Async/<c>Stream</c>/<c>PipeReader</c> producers plug in at the convenience layer (<c>FromStream</c>/
/// <c>FromFeed</c>).</summary>
public interface IMediaByteSource
{
    /// <summary>(Re-)open at <paramref name="spec"/> — a fresh signed URL/key can be injected here. Returns false on failure.</summary>
    bool TryOpen(in DataSpec spec);
    /// <summary><c>read(2)</c>: &gt;0 bytes (short reads legal), 0 = EOF, &lt;0 = error — the decoder ALWAYS loops.</summary>
    int Read(Span<byte> dst);
    /// <summary>Seek (re-opens a Range under the hood for HTTP); may FAIL (declared via <see cref="Caps"/>).</summary>
    long Seek(long offset);
    /// <summary>Total length — NULLABLE: a streaming/decrypting source may not know until the last chunk.</summary>
    long? Length { get; }
    /// <summary>Declared capabilities (seekability etc.).</summary>
    SourceCaps Caps { get; }
    /// <summary>Cross-thread, NON-BLOCKING abort of an in-flight expensive read (keeps scrub/stop responsive).</summary>
    void Cancel();
    /// <summary>Close and release.</summary>
    void Close();
}

// ── §5.4 DecryptingSource decorator (the PlayPlay exemplar) ──────────────────────────────────────────────────────────

/// <summary>An app-supplied AES-CTR cipher primitive (spec §5.4). The counter re-derives from byte offset so any offset
/// decrypts without replay; the app owns the crypto primitive, the engine owns the decorator seam.</summary>
public interface ICtrCipher
{
    /// <summary>Re-derive the CTR counter for the byte offset <paramref name="bytePosition"/> (counter = base + offset/16).</summary>
    void SeekCounter(long bytePosition);
    /// <summary>XOR the CTR keystream into <paramref name="buffer"/> in place (decrypt).</summary>
    void XorInPlace(Span<byte> buffer);
}

/// <summary>An opaque, app-owned content key handle (PlayPlay); resolved/cached/rotated OUT-OF-BAND, never inside a read.</summary>
public readonly record struct AudioKey(ReadOnlyMemory<byte> Bytes);

/// <summary>Resolves an <see cref="AudioKey"/> for a track (app-side, PlayPlay). NEVER called inside <c>Read</c> — the key
/// is prefetched at Prepare time (spec §5.4, §8.4).</summary>
public interface IAudioKeyProvider
{
    /// <summary>Resolve (prefetch/cache/rotate out-of-band) the key for <paramref name="trackUri"/>.</summary>
    ValueTask<AudioKey> ResolveKeyAsync(StringId trackUri, CancellationToken ct);
}

/// <summary>The portable decrypt-on-read decorator (spec §5.4): wraps a raw encrypted <see cref="IMediaByteSource"/> and a
/// pre-seeded <see cref="ICtrCipher"/> and returns plaintext on <see cref="Read"/> — invisible to the decode/DSP/mixer
/// stages above it. PlayPlay plugs in AT THE FRONT; PlayPlay internals stay app-private (this seam IS the deliverable).</summary>
public sealed class DecryptingSource : IMediaByteSource
{
    private readonly IMediaByteSource _inner;   // the raw encrypted chunk-fetch source (app-provided)
    private readonly ICtrCipher _cipher;        // seeded with the pre-resolved AudioKey; app owns the primitive

    /// <summary>Wrap <paramref name="inner"/> (encrypted) with <paramref name="cipher"/> (pre-seeded from a resolved key).</summary>
    public DecryptingSource(IMediaByteSource inner, ICtrCipher cipher) { _inner = inner; _cipher = cipher; }

    /// <inheritdoc/>
    public bool TryOpen(in DataSpec spec) { bool ok = _inner.TryOpen(spec); _cipher.SeekCounter(spec.Position); return ok; }
    /// <inheritdoc/>
    public int Read(Span<byte> dst) { int n = _inner.Read(dst); if (n > 0) _cipher.XorInPlace(dst[..n]); return n; }
    /// <inheritdoc/>
    public long Seek(long offset) { long p = _inner.Seek(offset); _cipher.SeekCounter(p); return p; }
    /// <inheritdoc/>
    public long? Length => _inner.Length;
    /// <inheritdoc/>
    public SourceCaps Caps => _inner.Caps;
    /// <inheritdoc/>
    public void Cancel() => _inner.Cancel();
    /// <inheritdoc/>
    public void Close() => _inner.Close();
}

// ── §5.2 Push seam — IMediaFeed ──────────────────────────────────────────────────────────────────────────────────────

/// <summary>An MSE-style push/append seam done right (spec §5.2): backpressure is a SIGNAL, buffered ranges are
/// introspectable (never catch an exception to probe fullness), and <see cref="AppendAsync"/> is awaitable (no
/// <c>updating</c> flag). The engine owns eviction/flow control.</summary>
public interface IMediaFeed
{
    /// <summary>The content type of the appended data.</summary>
    MediaContentType ContentType { get; }
    /// <summary>The declarative buffer target (the engine owns eviction to hold it).</summary>
    TimeSpan TargetBuffer { get; }
    /// <summary>Backpressure AS A SIGNAL — the producer just responds when it flips true.</summary>
    FluentGpu.Signals.IReadSignal<bool> NeedData { get; }
    /// <summary>The buffered ranges (introspectable; never an exception-as-probe).</summary>
    IReadOnlyList<TimeRange> BufferedRanges { get; }
    /// <summary>Append encoded data; awaitable (no <c>updating</c> flag).</summary>
    ValueTask AppendAsync(ReadOnlyMemory<byte> data, CancellationToken ct);
    /// <summary>Signal end-of-stream.</summary>
    void Complete();
}

// ── §5.3 Sample seam — IMediaSampleSource (the blessed FFmpeg-class path) ─────────────────────────────────────────────

/// <summary>Per-sample flags (spec §5.3).</summary>
[Flags]
public enum SampleFlags : byte
{
    /// <summary>No flags.</summary>
    None = 0,
    /// <summary>A discontinuity precedes this sample (seek/join).</summary>
    Discontinuity = 1,
    /// <summary>The sample is encrypted (CENC) — sample-level, per the PlayReady EME lesson.</summary>
    Encrypted = 2,
    /// <summary>The sample carries a codec config change.</summary>
    ConfigChange = 4
}

/// <summary>The kind of an elementary stream in an <see cref="IMediaSampleSource"/>.</summary>
public enum StreamKind : byte { Audio, Video, Text }

/// <summary>A typed elementary-stream descriptor surfaced up front (spec §5.3) — never discovered late inside append.</summary>
public readonly record struct StreamDescriptor(
    int Index, StreamKind Kind, MediaContentType Codec, TimeSpan Duration, SizeI NaturalSize,
    string? Language = null, TrackRole Role = TrackRole.Main);

/// <summary>One already-demuxed encoded sample (spec §5.3).</summary>
public readonly record struct MediaSample(
    int StreamIndex, ReadOnlyMemory<byte> Data, TimeSpan Pts, TimeSpan? Duration, bool IsKeyframe, SampleFlags Flags);

/// <summary>The blessed async-native "already demuxed" seam (spec §5.3 — WinUI <c>MediaStreamSource</c> done right,
/// STEAL: ExoPlayer <c>Extractor</c>). First-class, not an escape hatch; this is what <see cref="HeadlessScriptedPlayer"/>
/// is driven by.</summary>
public interface IMediaSampleSource
{
    /// <summary>The typed streams up front.</summary>
    IReadOnlyList<StreamDescriptor> Streams { get; }
    /// <summary>DRM config if the samples are protected (CENC), else null.</summary>
    DrmConfig? Drm { get; }
    /// <summary>Pull the next sample of a stream; <c>null</c> == end-of-stream.</summary>
    ValueTask<MediaSample?> GetSampleAsync(int streamIndex, CancellationToken ct);
    /// <summary>Seek all streams to <paramref name="to"/>.</summary>
    ValueTask SeekAsync(TimeSpan to, CancellationToken ct);
}

// ── §7 Audio graph seams (interfaces/POD only in M0; the graph + WASAPI leaves land in M2/M4) ────────────────────────

/// <summary>The fixed internal mix format (spec §7.2): <c>f32</c> interleaved implied; e.g. <c>{48000, 2}</c>. Every
/// source resamples INTO it at the decode edge so the device opens once.</summary>
public readonly record struct MixFormat(int SampleRate, int Channels);

/// <summary>Per-track/album loudness scanned OFFLINE or from tags (spec §7.7) — NEVER computed live on the mix.</summary>
public readonly record struct ReplayGainInfo(float TrackGainDb, float AlbumGainDb, float TrackPeak, float AlbumPeak);

/// <summary>Encoder-delay/pad trim info for sample-accurate gapless (spec §8.3), populated from container side-metadata
/// (LAME/Xing, iTunSMPB, Opus pre-skip, Vorbis granulepos) — NEVER a hardcoded constant.</summary>
public readonly record struct GaplessInfo(int LeadInFrames, int TrailPadFrames, long ExactFrames, bool TailKnown)
{
    /// <summary>The "lossless / no trim" value (zero lead-in, zero pad, tail known).</summary>
    public static GaplessInfo None => new(0, 0, -1, false);
}

/// <summary>What a decoder reports after sniffing container+codec (spec §5.5).</summary>
public readonly record struct DecodedInfo(MediaContentType Codec, MixFormat SourceFormat, TimeSpan Duration, ReplayGainInfo Loudness);

/// <summary>The decoder/extractor leaf seam (spec §5.5) — Vorbis/AAC/Opus/FLAC/MP3 behind one interface, decoding +
/// resampling INTO the fixed mix format.</summary>
public interface IAudioDecoder
{
    /// <summary>Sniff the container/codec of <paramref name="src"/> and prepare to decode INTO <paramref name="target"/>.</summary>
    bool TryOpen(IMediaByteSource src, MixFormat target, out DecodedInfo info);
    /// <summary>Decode+resample into <paramref name="dst"/> (framesWritten; short reads legal, 0 = EOF, &lt;0 = error).</summary>
    int Read(Span<float> dst);
    /// <summary>Seek to a frame index in the fixed mix-rate domain.</summary>
    long Seek(long frame);
    /// <summary>The parsed gapless trim info.</summary>
    GaplessInfo Gapless { get; }
}

/// <summary>The audio output sink opened ONCE per device session (spec §7, §13). Windows: WASAPI; macOS: CoreAudio.</summary>
public interface IAudioSink
{
    /// <summary>The mix format the device opened at.</summary>
    MixFormat Format { get; }
    /// <summary>Present <paramref name="frames"/> of interleaved <c>f32</c> to the device; returns frames accepted.</summary>
    int Write(ReadOnlySpan<float> src, int frames);
    /// <summary>Start the device stream.</summary>
    void Start();
    /// <summary>Stop the device stream (keeps the session; a track boundary is a splice, not a reopen).</summary>
    void Stop();
}

/// <summary>The played-frames master clock (spec §7.6) — WASAPI <c>IAudioClock</c> / CoreAudio timestamp. Position is
/// derived + QPC-extrapolated off this, never wall-clock, never read on the RT feed thread.</summary>
public interface IAudioClockSource
{
    /// <summary>The cheap high-rate written-frames counter (the estimate).</summary>
    long WrittenFrames { get; }
    /// <summary>The authoritative QPC-correlated played-frames count (a user→kernel→user transition — sampled off-thread).</summary>
    bool TryGetPlayed(out long playedFrames, out long qpc);
    /// <summary>The MEASURED stream latency in frames (re-read on every device rebuild).</summary>
    long StreamLatencyFrames { get; }
    /// <summary>The device mix rate.</summary>
    int MixRate { get; }
}

/// <summary>The device state a <see cref="IDeviceWatcher"/> reports (spec §7.9).</summary>
public enum AudioDeviceState : byte { Building, Running, Reinitializing, Faulted }

/// <summary>Follow-default / device-loss watcher (spec §7.9) — Windows <c>IMMNotificationClient</c>; macOS default-output
/// listener. A default-device change rebuilds ONLY the sink under a live graph.</summary>
public interface IDeviceWatcher
{
    /// <summary>The current device state.</summary>
    FluentGpu.Signals.IReadSignal<AudioDeviceState> State { get; }
    /// <summary>Raised (marshaled to the cold device thread) when the default output device changes.</summary>
    event Action? DefaultDeviceChanged;
}

// ── §4.4/§10 Per-platform VIDEO backend seam (IMediaBackend / IMediaSession) ─────────────────────────────────────────

/// <summary>A single decoded video frame pulled per present time (spec §9.1 Path B — the forward hook). POD; the texture
/// is an opaque cross-seam handle (the engine never reads a video texel).</summary>
public readonly record struct VideoFrame(nuint Texture, TimeSpan Pts, bool IsHdr);

/// <summary>How a backend delivers video (spec §9.1). Path A (composited surface) is the shipping spine; Path B
/// (shared texture pulled per present time) is the forward hook for correct HDR/16-bit.</summary>
public abstract record VideoDelivery
{
    private VideoDelivery() { }

    /// <summary>The audio-only sentinel — no video is delivered.</summary>
    public static VideoDelivery None { get; } = new AudioOnlyDelivery();

    /// <summary>Path A — a composited DirectComposition surface (MF windowless swapchain / AVPlayerLayer): the engine
    /// gets an id and hole-punches at the video rect. SHIPPING.</summary>
    public sealed record CompositedSurface(VideoSurfaceId Id, SizeI NaturalSize, bool IsHdr) : VideoDelivery;

    /// <summary>Path B — a shared texture pulled per present time (STEAL: <c>copyPixelBuffer(forItemTime:)</c>, libmpv).
    /// FORWARD HOOK: the compositor asks "give me the frame for THIS present time" and gets a shared texture + PTS.</summary>
    public sealed record SharedTexture(Func<TimeSpan, VideoFrame?> AcquireForPresentTime, bool IsHdr) : VideoDelivery;

    /// <summary>Audio-only — the backend delivers no video.</summary>
    public sealed record AudioOnlyDelivery : VideoDelivery;
}

/// <summary>Options for opening a session (spec §10). All optional — <see cref="MediaSource"/> carries the primary intent.</summary>
public sealed record MediaOpenOptions
{
    /// <summary>Start paused-and-ready rather than auto-playing.</summary>
    public bool StartPaused { get; init; } = true;
    /// <summary>The initial seek position (before first frame).</summary>
    public TimeSpan StartPosition { get; init; } = TimeSpan.Zero;
    /// <summary>The buffering policy.</summary>
    public BufferPolicy? Buffering { get; init; }
    /// <summary>Network defaults after per-source overrides are applied.</summary>
    public NetworkOptions? Network { get; init; }
    /// <summary>Adaptive bitrate policy for DASH/HLS sources.</summary>
    public IAbrPolicy? Abr { get; init; }
    /// <summary>Live-latency target.</summary>
    public LiveLatencyMode LiveLatency { get; init; } = LiveLatencyMode.Standard;
    /// <summary>The DRM license relay (spec §9.2), if the source is protected.</summary>
    public Func<LicenseRequest, ValueTask<LicenseResponse>>? LicenseRelay { get; init; }
}

/// <summary>What a backend can play/do (spec §5.6/§10). Answered EARLY at query time.</summary>
public sealed record MediaCapabilities(bool SupportsVideo, bool SupportsAudioGraph, bool SupportsDrm)
{
    /// <summary>Does this backend support the given content type? (Query-time capability, never an append failure.)</summary>
    public Func<MediaContentType, bool>? IsSupported { get; init; }
    /// <summary>The inert "nothing supported" capability set.</summary>
    public static MediaCapabilities None { get; } = new(false, false, false);
}

/// <summary>The per-platform backend factory (spec §4.4/§10): resolves a source into an <see cref="IMediaSession"/>. The
/// Windows MF backend and (later) macOS <c>AVPlayer</c> backend both implement this; M1/M5 land the concrete impls.</summary>
public interface IMediaBackend
{
    /// <summary>Open <paramref name="source"/> into a session (async — no COM deferrals).</summary>
    ValueTask<IMediaSession> OpenAsync(MediaSource source, MediaOpenOptions opts, CancellationToken ct);
    /// <summary>What this backend can do (query-time capability).</summary>
    MediaCapabilities Capabilities { get; }
}

/// <summary>A backend's live session (spec §4.4/§10). It drives the player's signals through the <see cref="MediaSignalSink"/>
/// it is connected to (marshaled to the safe context), and receives the transport verbs. Every device ComPtr stays
/// render-thread-/RT-confined behind the concrete impl.</summary>
public interface IMediaSession : IAsyncDisposable
{
    /// <summary>Connect the signal sink the backend writes state INTO (backend → engine, marshaled to the safe context).</summary>
    void ConnectSignals(MediaSignalSink sink);
    /// <summary>Resume.</summary>
    ValueTask PlayAsync();
    /// <summary>Pause.</summary>
    ValueTask PauseAsync();
    /// <summary>Seek.</summary>
    ValueTask SeekAsync(TimeSpan to, SeekMode mode);
    /// <summary>Set the playback rate.</summary>
    void SetRate(double rate);
    /// <summary>Set the volume (0..1).</summary>
    void SetVolume(double volume);
    /// <summary>Mute/unmute.</summary>
    void SetMuted(bool muted);
    /// <summary>Select a backend-discovered audio/video/text track. Null disables text where supported.</summary>
    ValueTask SelectTrackAsync(MediaTrack? track) => ValueTask.CompletedTask;
    /// <summary>Select automatic quality or pin a representation.</summary>
    ValueTask SelectQualityAsync(QualitySelection selection) => ValueTask.CompletedTask;
    /// <summary>Seek to the current live edge.</summary>
    ValueTask GoLiveAsync() => ValueTask.CompletedTask;
    /// <summary>How this session delivers video.</summary>
    VideoDelivery Video { get; }
}

// ── §14 ABR policy seam ──────────────────────────────────────────────────────────────────────────────────────────────

/// <summary>The adaptive-bitrate policy seam (spec §14) — a non-oscillating auto with a manual pin + max-bitrate cap.
/// Interface only in M0; the Shaka/dash.js-class rule pipeline is a later override.</summary>
public interface IAbrPolicy
{
    /// <summary>Pick a variant index from the available bitrates given the current forward buffer + measured throughput.</summary>
    int Choose(ReadOnlySpan<int> variantBitrates, TimeSpan forwardBuffered, double measuredKbps);
}

/// <summary>Built-in ABR policies (spec example D <c>AbrPolicy.Auto</c>).</summary>
public sealed class AbrPolicy : IAbrPolicy
{
    private readonly int _pinnedIndex;   // -1 == auto

    private AbrPolicy(int pinned) => _pinnedIndex = pinned;

    /// <summary>The default non-oscillating auto policy.</summary>
    public static AbrPolicy Auto { get; } = new(-1);
    /// <summary>Pin a specific variant index.</summary>
    public static AbrPolicy Pin(int index) => new(index);

    /// <inheritdoc/>
    public int Choose(ReadOnlySpan<int> variantBitrates, TimeSpan forwardBuffered, double measuredKbps)
    {
        if (variantBitrates.Length == 0) return 0;
        if (_pinnedIndex >= 0) return Math.Min(_pinnedIndex, variantBitrates.Length - 1);
        // Auto: highest variant whose bitrate fits the measured throughput with headroom; never oscillate below index 0.
        int best = 0;
        double budgetKbps = measuredKbps * 0.9;
        for (int i = 0; i < variantBitrates.Length; i++)
            if (variantBitrates[i] / 1000.0 <= budgetKbps) best = i;
        return best;
    }
}
