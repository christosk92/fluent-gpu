using System;

namespace FluentGpu.Media;

/// <summary>The broad category of a media failure (spec §11). Silent DRM downgrade is <b>unrepresentable</b> — a DRM
/// shortfall is <see cref="Drm"/> + a <see cref="MediaRecovery"/>, never a quiet drop to black/480p.</summary>
public enum MediaErrorCategory : byte
{
    /// <summary>A network fetch failed (DNS/TLS/timeout/HTTP status).</summary>
    Network,
    /// <summary>Decode failed (corrupt bitstream, unexpected sample).</summary>
    Decode,
    /// <summary>DRM/protection failure (license, provisioning, protected-path).</summary>
    Drm,
    /// <summary>The container/codec is not supported by any available backend.</summary>
    UnsupportedCodec,
    /// <summary>A storage/quota limit was hit (append buffer full, disk).</summary>
    Quota,
    /// <summary>The source itself is invalid/unavailable (missing file, bad URI, empty stream).</summary>
    Source,
    /// <summary>A lifecycle/threading fault (opened after dispose, no backend registered).</summary>
    Lifecycle,
    /// <summary>The audio/video OUTPUT device faulted (device lost, sink open failed).</summary>
    Output
}

/// <summary>Which item/segment/sample a <see cref="MediaError"/> refers to (spec §11) — every field nullable because a
/// given failure only knows some of them.</summary>
public readonly record struct MediaLocus(
    int? QueueIndex, MediaSource? Item, TimeSpan? Position, int? StreamIndex, long? ByteOffset);

/// <summary>The recovery hint carried by every <see cref="MediaError"/> (spec §11) — so the UI can offer the right
/// action (retry / sign-in gesture / reconnect / re-license / pick lower quality) instead of a dead end.</summary>
public enum MediaRecovery : byte
{
    /// <summary>No recovery applies.</summary>
    None,
    /// <summary>Retrying the same operation may succeed (transient).</summary>
    Retryable,
    /// <summary>A user gesture is required (autoplay policy).</summary>
    NeedsUserGesture,
    /// <summary>Network connectivity is required.</summary>
    NeedsNetwork,
    /// <summary>A (fresh) DRM license is required.</summary>
    NeedsLicense,
    /// <summary>The current quality is unplayable; a lower variant may work.</summary>
    PickLowerQuality,
    /// <summary>Terminal — no recovery.</summary>
    Fatal
}

/// <summary>The platform's own classification of a media failure, preserved VERBATIM alongside
/// <see cref="MediaErrorCategory"/> so an app can act on the exact code the backend reported instead of
/// re-deriving it. The values are the HTML5 <c>MediaError.code</c> vocabulary, which is also what Media
/// Foundation's <c>MF_MEDIA_ENGINE_ERR</c> reports 1:1 (1 ABORTED, 2 NETWORK, 3 DECODE, 4 SRC_NOT_SUPPORTED,
/// 5 ENCRYPTED) — a backend that has no such code leaves <see cref="Unknown"/>.
/// <para>Why this exists on top of <see cref="MediaErrorCategory"/>: the category is the ENGINE's verdict
/// (what kind of failure this is, which recovery applies); the kind is the SOURCE's verdict (what the
/// platform actually said). A live-stream host distinguishes an expired URL (NETWORK → re-resolve the
/// locator) from a mid-stream container switch (DECODE → reload the same locator) from "this machine cannot
/// play HLS" (SOURCE_NOT_SUPPORTED → a terminal, explainable fault) — three different policies behind what
/// would otherwise be one generic open failure. The raw HRESULT stays in
/// <see cref="MediaError.UnderlyingCode"/>.</para></summary>
public enum MediaErrorKind : byte
{
    /// <summary>The backend reported no platform error code (or none applies). MUST be the default(0) value.</summary>
    Unknown = 0,
    /// <summary>Loading was aborted (MF_MEDIA_ENGINE_ERR_ABORTED / HTML5 code 1).</summary>
    Aborted = 1,
    /// <summary>A network fetch failed after the source was accepted (code 2).</summary>
    Network = 2,
    /// <summary>Decoding failed on an accepted source (code 3).</summary>
    Decode = 3,
    /// <summary>The source itself was rejected: unsupported container/codec/protocol (code 4).</summary>
    SourceNotSupported = 4,
    /// <summary>The content is encrypted and no usable license/CDM was available (code 5).</summary>
    Encrypted = 5,
}

/// <summary>The single typed, contextual error surfaced on <c>IMediaPlayer.Error</c> (spec §11). <see cref="Message"/> is
/// ALWAYS populated (no nil-error); <see cref="UnderlyingCode"/> preserves the raw HRESULT/CoreMedia int; <see cref="Locus"/>
/// names WHICH item; <see cref="Recovery"/> names the way out; <see cref="Kind"/> preserves the platform's own
/// error code verbatim (see <see cref="MediaErrorKind"/>). A recoverable stall is NOT an error — it becomes
/// <see cref="PlaybackState.Stalled"/> and clears when the buffer refills.</summary>
public sealed record MediaError(
    MediaErrorCategory Category,
    string Message,
    long? UnderlyingCode = null,
    MediaLocus? Locus = null,
    MediaRecovery Recovery = MediaRecovery.None,
    MediaErrorKind Kind = MediaErrorKind.Unknown)
{
    /// <summary>Convenience: a "no backend is registered for this kind" lifecycle error (the honest M0 facade result when
    /// no video/audio backend has been plugged into the router yet).</summary>
    public static MediaError NoBackend(MediaKind kind)
        => new(MediaErrorCategory.Lifecycle, $"No media backend registered for kind '{kind}'.", null, null, MediaRecovery.Fatal);
}
