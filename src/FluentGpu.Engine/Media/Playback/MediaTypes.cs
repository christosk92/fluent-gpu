using System;
using System.Collections.Generic;

namespace FluentGpu.Media;

// ── Core enums (§4.2) ────────────────────────────────────────────────────────────────────────────────────────────────

/// <summary>The single exhaustive playback state (spec §4.2). One <see cref="FluentGpu.Signals.Signal{T}"/> of this — the
/// backend is the sole writer; everything else (<c>IsPlaying</c>/<c>IsBuffering</c>) is derived from it plus the
/// intent/why-not signals.</summary>
public enum PlaybackState : byte
{
    /// <summary>No source / stopped.</summary>
    Idle,
    /// <summary>Source resolving, metadata not yet available.</summary>
    Opening,
    /// <summary>Opened, filling the initial buffer.</summary>
    Buffering,
    /// <summary>Paused-and-ready (playable, not advancing).</summary>
    Ready,
    /// <summary>Advancing.</summary>
    Playing,
    /// <summary>User-paused after having played.</summary>
    Paused,
    /// <summary>Was playing, ran out of buffer (transient — never becomes <see cref="Failed"/>).</summary>
    Stalled,
    /// <summary>Reached natural end.</summary>
    Ended,
    /// <summary>Terminal error; see <c>IMediaPlayer.Error</c>.</summary>
    Failed
}

/// <summary>Why playback is NOT advancing even though play was requested (STEAL: ExoPlayer
/// <c>playbackSuppressionReason</c>) — a state, never a thrown exception.</summary>
public enum SuppressionReason : byte
{
    /// <summary>No suppression — if play was requested and the source is ready, it advances.</summary>
    None,
    /// <summary>A transient audio-focus loss (a notification ducking, a call).</summary>
    TransientAudioFocusLoss,
    /// <summary>The audio route was lost (headphones unplugged).</summary>
    AudioRouteLost,
    /// <summary>Autoplay without a user gesture — a STATE, never a thrown <c>NotAllowedError</c>.</summary>
    Unattended,
    /// <summary>Ran out of buffer (an underrun) — pairs with <see cref="PlaybackState.Stalled"/>.</summary>
    BufferingUnderrun,
    /// <summary>Backgrounded without background-audio permission.</summary>
    BackgroundedNoPermission
}

/// <summary>Seek fidelity (STEAL: mpv): fast keyframe scrub vs decode-to-exact-PTS.</summary>
public enum SeekMode : byte
{
    /// <summary>Snap to the nearest keyframe — fast scrub.</summary>
    Keyframe,
    /// <summary>Decode to the exact requested PTS — accurate commit.</summary>
    Accurate
}

/// <summary>Routing hint on a <see cref="MediaSource"/> (spec §5). <see cref="Auto"/> lets the router sniff.</summary>
public enum MediaKind : byte
{
    /// <summary>Let the router sniff the source and pick a backend.</summary>
    Auto,
    /// <summary>The custom PCM audio-graph backend (Spotify/PlayPlay, crossfade, EQ, gapless).</summary>
    PcmAudio,
    /// <summary>The Media-Foundation backend (video + self-contained A/V files, DRM via a CDM).</summary>
    MfVideoOrFile
}

/// <summary>The policy applied when the forward buffer underruns (drives the <see cref="PlaybackState.Stalled"/>
/// transition and the fade-to-silence behaviour).</summary>
public enum StallPolicy : byte
{
    /// <summary>Enter <see cref="PlaybackState.Stalled"/> and resume automatically when the buffer refills.</summary>
    Rebuffer,
    /// <summary>Keep advancing off the last-known ranges (live edge) rather than stalling.</summary>
    SkipForward
}

// ── POD state (§4.2) ─────────────────────────────────────────────────────────────────────────────────────────────────

/// <summary>Integer pixel size — video natural size; <c>(0,0)</c> == audio-only. Owned here (Media) as the one
/// definition the whole media surface references.</summary>
public readonly record struct SizeI(int Width, int Height)
{
    /// <summary>The audio-only / no-video sentinel.</summary>
    public static SizeI Zero => default;
    /// <summary>True when there is no video (audio-only or not yet known).</summary>
    public bool IsEmpty => Width == 0 && Height == 0;
}

/// <summary>A buffered range expressed in TIME (never bytes — a streaming source may not know its byte length).</summary>
public readonly record struct TimeRange(TimeSpan Start, TimeSpan End);

/// <summary>Buffer health snapshot (spec §4.2): the buffered ranges (in time), the forward-buffered seconds a seekbar
/// paints, whether the buffer is currently starved, and the stall policy in force.</summary>
public readonly record struct BufferHealth(
    IReadOnlyList<TimeRange> Ranges, TimeSpan ForwardBuffered, bool IsStalled, StallPolicy Policy)
{
    /// <summary>The empty (no ranges buffered) health — the initial value of the <c>Buffer</c> signal.</summary>
    public static BufferHealth Empty { get; } = new(Array.Empty<TimeRange>(), TimeSpan.Zero, false, StallPolicy.Rebuffer);
}

// ── Typed codec/container descriptors (§5.6) ─────────────────────────────────────────────────────────────────────────

/// <summary>Container formats sniffed early at query time (not stringly-typed MIME that fails deep in append).</summary>
public enum Container : byte { Unknown, Mp4, Mkv, WebM, Ogg, Mpeg2Ts, Flac, Wav, Adts, Mp3, Hls, Dash }

/// <summary>Codec ids answered early (never as an opaque append failure).</summary>
public enum CodecId : ushort { None, H264, Hevc, Av1, Vp9, Aac, Opus, Flac, Mp3, Vorbis, Pcm }

/// <summary>A typed container+codec descriptor (spec §5.6). Capability is answered at query time via
/// <see cref="MediaCapabilities"/>, never as an opaque failure inside append.</summary>
public readonly record struct MediaContentType(Container Container, CodecId Video, CodecId Audio)
{
    /// <summary>The "sniff at open" sentinel — an unknown container/codec the backend resolves on open.</summary>
    public static MediaContentType Sniff() => new(Container.Unknown, CodecId.None, CodecId.None);
    /// <summary>True when nothing is known yet (the <see cref="Sniff"/> sentinel).</summary>
    public bool IsUnknown => Container == Container.Unknown && Video == CodecId.None && Audio == CodecId.None;
}

// ── Metadata / now-playing shapes referenced by both §5 (WithMetadata) and §10 (NowPlaying) ──────────────────────────

/// <summary>A single artwork reference at a known resolution (multi-res artwork for now-playing / SMTC).</summary>
public readonly record struct ArtworkRef(string Uri, int Width, int Height);

/// <summary>Source-provided now-playing metadata (spec §10). Seeds <c>NowPlaying</c> without a round-trip when attached
/// to a source via <see cref="MediaSource.WithMetadata"/>.</summary>
public sealed record MediaMetadata(string? Title, string? Artist, string? Album, IReadOnlyList<ArtworkRef> Artwork, MediaKind Kind)
{
    /// <summary>The empty metadata (no title/artist/album/artwork), kind <see cref="MediaKind.Auto"/>.</summary>
    public static MediaMetadata Empty { get; } = new(null, null, null, Array.Empty<ArtworkRef>(), MediaKind.Auto);
}
