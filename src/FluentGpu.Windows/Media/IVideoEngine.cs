using System;

namespace FluentGpu.Media.Windows;

/// <summary>
/// What the engine ANSWERED when asked for the decoded video size. Tri-state on purpose: <see cref="VideoMediaEngine"/>
/// marshals every COM read onto its own MTA thread with a BOUNDED wait, so a zero size cannot mean both "this source has
/// no video" and "the engine thread was busy resolving the source and has not answered yet". Conflating the two is what
/// latched an audio-only verdict onto a playing video and left it under an eternal "Starting playback…" spinner:
/// <see cref="MfMediaSession"/> published the natural size exactly once, at first <c>LOADEDMETADATA</c> — the instant the
/// engine thread is at its busiest — and never asked again. <see cref="NoAnswer"/> means ASK AGAIN on the next pump.
/// </summary>
public enum NativeSizeAnswer : byte
{
    /// <summary>The engine did not answer in time (the bounded <c>Invoke</c> expired). Nothing was learned; retry.
    /// MUST be the default(0) value — a timed-out <c>Invoke&lt;T&gt;</c> returns <c>default</c>.</summary>
    NoAnswer = 0,
    /// <summary>The engine answered and this source has no decoded video size (audio-only). Stop asking.</summary>
    NoVideo,
    /// <summary>The engine answered with a real size (both dimensions &gt; 0).</summary>
    Ok,
}

/// <summary>
/// The minimal boundary <see cref="MfMediaSession"/> drives, extracted from the PROVEN <see cref="VideoMediaEngine"/> so
/// the session's state-mapping / transport / surface-handoff logic is unit-testable WITHOUT standing up a real D3D11 + MF +
/// DirectComposition device (a fake implements this in <c>FluentGpu.Windows.Tests</c>). <see cref="VideoMediaEngine"/> is
/// the production implementation — this seam does not change its behavior, it only makes it injectable.
/// <para>Threading: every member is safe to call from the UI/pump thread. The real engine marshals each COM call onto its
/// dedicated MTA thread internally (its <c>Invoke</c> pattern) and surfaces event state as volatile flags; a caller never
/// touches a ComPtr off that thread.</para>
/// </summary>
internal interface IVideoEngine : IDisposable
{
    /// <summary>Raised when a native media-engine event changes transport or surface state. High-frequency progress and
    /// frame notifications are intentionally excluded: windowless DirectComposition presents decoded frames without a
    /// UI-thread repaint. May run on an MF worker thread; consumers must marshal UI work.</summary>
    event Action? StateChanged;

    /// <summary>Stand up the engine and set the source (blocking until the engine thread has created it). S_OK (&gt;=0) on
    /// success; a negative HRESULT on failure.</summary>
    int Initialize(string url);

    // ── event state (set on worker threads; read anywhere) ─────────────────────────────────────────────────────────
    bool MetadataLoaded { get; }
    bool CanPlay { get; }
    bool Playing { get; }
    bool Ended { get; }
    /// <summary>True while a seek is in flight (MF SEEKING fired, SEEKED not yet) — drives the "Seeking…" buffering UX.</summary>
    bool Seeking { get; }
    bool HasError { get; }
    uint ErrorCode { get; }
    int ErrorHr { get; }
    string LastEventName { get; }
    /// <summary>HTML/MF ready state (0 HAVE_NOTHING through 4 HAVE_ENOUGH_DATA).</summary>
    uint ReadyState { get; }

    // ── metadata / geometry ────────────────────────────────────────────────────────────────────────────────────────
    /// <summary>Native decoded video size (px), valid once metadata has loaded. Returns
    /// <see cref="NativeSizeAnswer.NoAnswer"/> when the engine has not answered yet — the caller must ask again on a
    /// later pump instead of treating the zeroed out-params as "audio-only".</summary>
    NativeSizeAnswer QueryNativeVideoSize(out uint cx, out uint cy);
    /// <summary>Media duration in seconds (0 until known; may be +Inf for a live/looping source — the caller clamps).</summary>
    double DurationSeconds { get; }
    /// <summary>Current presentation time in seconds (the authoritative clock).</summary>
    double CurrentTimeSeconds { get; }

    // ── composited-surface handoff ─────────────────────────────────────────────────────────────────────────────────
    /// <summary>The windowless swap-chain HANDLE (valid after metadata); 0 until ready. Bind via <c>IVideoPresenter.BindSurfaceHandle</c>.</summary>
    nuint GetSwapchainHandle();
    /// <summary>Set the video's destination rect within its own swap chain (swap-chain-local {0,0,w,h}, device px).</summary>
    int SetVideoStreamRect(int w, int h);
    /// <summary>Repaint the most-recently-decoded frame into the swap chain.</summary>
    void RepaintCurrentFrame();

    // ── transport ──────────────────────────────────────────────────────────────────────────────────────────────────
    void Play();
    void Pause();
    /// <summary>Seek: set the current presentation time (seconds). <paramref name="approximate"/> requests MF's
    /// approximate/keyframe seek (fast — snaps to the nearest keyframe, skips the exact-PTS decode) instead of the
    /// default normal/exact seek (decodes to the requested PTS). See <see cref="FluentGpu.Media.SeekMode"/>.</summary>
    void SeekTo(double seconds, bool approximate = false);
    void SetPlaybackRate(double rate);
    void SetVolume(double volume);
    void SetMuted(bool muted);
    /// <summary>Toggle native looping (a media element defaults OFF; the M3 harness kept a live frame ON).</summary>
    void SetLoop(bool loop);
}
