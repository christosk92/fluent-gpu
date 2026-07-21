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

/// <summary>The single typed, contextual error surfaced on <c>IMediaPlayer.Error</c> (spec §11). <see cref="Message"/> is
/// ALWAYS populated (no nil-error); <see cref="UnderlyingCode"/> preserves the raw HRESULT/CoreMedia int; <see cref="Locus"/>
/// names WHICH item; <see cref="Recovery"/> names the way out. A recoverable stall is NOT an error — it becomes
/// <see cref="PlaybackState.Stalled"/> and clears when the buffer refills.</summary>
public sealed record MediaError(
    MediaErrorCategory Category,
    string Message,
    long? UnderlyingCode = null,
    MediaLocus? Locus = null,
    MediaRecovery Recovery = MediaRecovery.None)
{
    /// <summary>Convenience: a "no backend is registered for this kind" lifecycle error (the honest M0 facade result when
    /// no video/audio backend has been plugged into the router yet).</summary>
    public static MediaError NoBackend(MediaKind kind)
        => new(MediaErrorCategory.Lifecycle, $"No media backend registered for kind '{kind}'.", null, null, MediaRecovery.Fatal);
}
