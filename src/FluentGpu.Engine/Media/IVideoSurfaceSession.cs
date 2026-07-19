using FluentGpu.Foundation;

namespace FluentGpu.Media;

/// <summary>
/// An <see cref="IMediaSession"/> that delivers a composited video surface driven by a per-frame, UI-thread pump (the
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
    /// (device px). Safe to call every frame; cheap and idempotent once steady-state.</summary>
    void PumpVideo(VideoBinding binding, RectF videoRect, float scale);
}
