using System;
using FluentGpu.Media;

namespace FluentGpu.Media.Windows;

/// <summary>
/// The minimal boundary <see cref="MfMediaSession"/> drives, extracted from the PROVEN <see cref="VideoMediaEngine"/> so
/// the session's state-mapping / transport / surface-handoff logic is unit-testable WITHOUT standing up a real D3D11 + MF +
/// DirectComposition device (a fake implements this in <c>FluentGpu.Windows.Tests</c>). <see cref="VideoMediaEngine"/> is
/// the production implementation — this seam does not change its behavior, it only makes it injectable.
///
/// <para><b>v2 — snapshot out, commands in</b> (<c>docs/plans/video-smooth-switching-implementation.md</c> §1). The
/// engine's dedicated MTA thread is the ONLY thread that ever touches a <c>ComPtr</c>, and it is the SOLE writer of a POD
/// <see cref="VideoEngineSnapshot"/> published through a single-writer seqlock (<see cref="VideoSnapshotBuffer"/>). Every
/// caller — the UI/pump thread included — only ever READS the snapshot (alloc-free, torn-read-safe, never blocks) and
/// POSTS fire-and-forget, coalesced commands through <see cref="VideoEngineCommandQueue"/>. There is no blocking member
/// left on this seam: the old <c>Invoke&lt;T&gt;</c> marshal-and-wait pattern (a UI-thread round-trip onto the engine
/// thread, bounded at tens of ms, paid up to 7× per pump while a video opened) is gone outright, and with it every
/// property that used to require one. <see cref="Start"/> itself is non-blocking — bring-up failure is reported as the
/// sticky <see cref="VideoEngineFlags.Faulted"/> bit in the snapshot, never a synchronous HRESULT.</para>
/// <para>Threading: every member here is safe to call from any thread, including the UI/pump thread — that is the whole
/// point of the seam. The real engine still marshals every actual COM call onto its own dedicated MTA thread internally
/// (now driven by <see cref="VideoEngineCommandQueue"/> instead of a blocking work queue); a caller never touches a
/// ComPtr off that thread, and never waits for one either.</para>
/// </summary>
internal interface IVideoEngine : IDisposable
{
    /// <summary>Raised BY THE ENGINE THREAD after a snapshot publish whose significant fields changed (state, ready
    /// state, natural size, duration, swap-chain handle, error, seekable window — position-only changes are coalesced to
    /// at most ~1 Hz so a playing video does not wake a pump every refresh tick). Publish-then-raise ordering: a pump
    /// woken by this event is guaranteed to read AT LEAST the state that raised it (never an older snapshot). May run on
    /// the engine's own thread or an MF worker thread; consumers must marshal UI work, never touch COM here.</summary>
    event Action? StateChanged;

    /// <summary>Spin up the dedicated MTA engine thread and stand up the D3D11 video device + DXGI manager + Media
    /// Engine (windowless swap-chain mode). NON-BLOCKING: returns immediately, before bring-up completes. Bring-up
    /// failure is reported as the sticky <see cref="VideoEngineFlags.Faulted"/> bit in <see cref="Snapshot"/> — never a
    /// synchronous HRESULT and never a thrown exception. A caller (<c>MfMediaPlayer.LeaseEngine</c>) that observes
    /// <see cref="VideoEngineFlags.Faulted"/> discards this instance and builds a fresh one; it is not recoverable via
    /// <see cref="PostSetSource"/>.</summary>
    void Start();

    /// <summary>Post a source switch (<c>SetSource</c> on the live engine — no teardown/rebuild, the warm-engine reuse
    /// this seam exists for). Returns the new source epoch immediately (a monotonically increasing generation counter
    /// allocated on the calling thread); the actual COM call happens later, on the engine thread, the next time it
    /// drains its command queue. A caller compares <see cref="VideoEngineSnapshot.SourceEpoch"/> against the returned
    /// epoch to tell whether a given snapshot describes THIS source switch or a stale/earlier one. Fire-and-forget: never
    /// blocks, never throws for an MF-side failure (that surfaces as the <see cref="VideoEngineFlags.Error"/> bit once
    /// the engine thread has processed it).</summary>
    int PostSetSource(string url);

    /// <summary>Post a source unload (<c>SetSource(null)</c>) — used when the engine returns warm to the backend's pool
    /// (<c>MfMediaPlayer.ReturnEngine</c>) instead of being disposed, so it holds no source between checkouts. Some MF
    /// builds fail a null <c>SetSource</c>; that failure is tolerated (logged, not surfaced as a session error — there is
    /// no session listening to a detached engine).</summary>
    void PostDetach();

    /// <summary>One seqlock read of the engine's current state — alloc-free, wait-free, safe from any thread, and never
    /// stale by more than one publish cycle. See <see cref="VideoEngineSnapshot"/> for field semantics.</summary>
    VideoEngineSnapshot Snapshot { get; }

    /// <summary>The command queue this engine drains on its own thread. Callers post transport/seek/rate/volume/mute/
    /// loop/stream-rect/repaint/detach through it (<see cref="PostSetSource"/>/<see cref="PostDetach"/> are the two
    /// commands with a dedicated convenience method because they also need to hand back/consume a source epoch).</summary>
    VideoEngineCommandQueue Commands { get; }

    /// <summary>Whether this machine's Media Foundation can play an HLS master playlist at all
    /// (<c>CanPlayType("application/vnd.apple.mpegurl")</c> != NOT_SUPPORTED), probed once at bring-up; false until
    /// <see cref="Start"/> has stood up the engine. Lets a caller report the real reason a live URL will not open instead
    /// of a generic source failure.</summary>
    bool CanPlayHls { get; }
}
