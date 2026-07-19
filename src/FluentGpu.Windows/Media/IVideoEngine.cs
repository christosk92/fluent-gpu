using System;

namespace FluentGpu.Media.Windows;

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
    /// <summary>Stand up the engine and set the source (blocking until the engine thread has created it). S_OK (&gt;=0) on
    /// success; a negative HRESULT on failure.</summary>
    int Initialize(string url);

    // ── event state (set on worker threads; read anywhere) ─────────────────────────────────────────────────────────
    bool MetadataLoaded { get; }
    bool CanPlay { get; }
    bool Playing { get; }
    bool Ended { get; }
    bool HasError { get; }
    uint ErrorCode { get; }
    int ErrorHr { get; }
    string LastEventName { get; }

    // ── metadata / geometry ────────────────────────────────────────────────────────────────────────────────────────
    bool TryGetNativeVideoSize(out uint cx, out uint cy);
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
    /// <summary>Seek: set the current presentation time (seconds).</summary>
    void SeekTo(double seconds);
    void SetPlaybackRate(double rate);
    void SetVolume(double volume);
    void SetMuted(bool muted);
    /// <summary>Toggle native looping (a media element defaults OFF; the M3 harness kept a live frame ON).</summary>
    void SetLoop(bool loop);
}
