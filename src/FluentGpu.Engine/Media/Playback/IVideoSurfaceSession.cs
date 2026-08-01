using System;
using FluentGpu.Foundation;

namespace FluentGpu.Media;

/// <summary>
/// An <see cref="IMediaSession"/> that delivers a composited video surface driven by an on-demand, UI-thread pump (the
/// Windows Media-Foundation backend). The pump is where the backend translates its (worker-thread) engine events into the
/// player's signals ON THE UI THREAD (so the sole-writer contract holds), binds the produced DirectComposition surface
/// handle into the caller's <see cref="VideoBinding"/> (the single DRM attach point — a protected handle flows through the
/// SAME call), and positions the video child at the laid-out video rect.
/// <para>The facade's <see cref="IMediaPlayer.PumpVideo"/> forwards to this when the routed session implements it; an
/// audio-only or headless session does not, so <c>PumpVideo</c> is then a no-op. The seam is portable (no TerraFX): the
/// Windows session implements it, the control drives it.</para>
/// </summary>
public interface IVideoSurfaceSession
{
    /// <summary>Pump one UI-thread turn: translate engine state → the connected <see cref="MediaSignalSink"/>, bind the
    /// produced DComp surface handle through <paramref name="binding"/> (value-gated), place the child at
    /// <paramref name="videoRect"/> (DIP) and size the video stream to <paramref name="videoRect"/>×<paramref name="scale"/>
    /// (device px). Called for initial binding and then when a native event, transport command, activation, or geometry
    /// change requests a coalesced turn; it is intentionally not a per-frame repaint path.</summary>
    void PumpVideo(VideoBinding binding, RectF videoRect, float scale);
}

/// <summary>Optional event source for a video session/player that needs one UI-thread pump. Native media callbacks may
/// raise this from any thread; the owning control posts and coalesces it before touching the scene.</summary>
public interface IVideoPumpSource
{
    /// <summary>Raised when the source has state, a frame, or a hand-off that needs one settled UI-thread pump.</summary>
    event Action? PumpRequested;
}
