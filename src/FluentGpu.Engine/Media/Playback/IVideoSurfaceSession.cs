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

/// <summary>How big a composited video stream should be. ONE owner for both Windows backends (the clear
/// <c>MfMediaSession</c> and the protected <c>ProtectedMediaSession</c>), so the two can never disagree about the buffer
/// a destination is worth.</summary>
public static class VideoStreamSizing
{
    /// <summary>The size (device px) to render the decoded frame at inside the engine's own swap chain, which is ALSO
    /// the content size handed to the compositor — the two must agree or DirectComposition scales by the wrong factor.
    /// <para>It is the natural frame size, capped so a frame far larger than the rect it is being shown in does not
    /// allocate buffers nobody can see (a 4K stream in a 480-px card). The cap scales BOTH axes by one factor, so the
    /// frame's aspect ratio — the input to the fit — survives exactly; the factor is chosen from the axis that is
    /// magnified MOST, so a Fill/UniformToFill destination never samples a downscaled buffer up again.</para>
    /// <para>A natural size that is not known yet falls back to the destination rect, so the surface still presents
    /// something during the pump or two before the engine answers.</para></summary>
    public static SizeI ContentSizeFor(SizeI natural, RectF videoRect, float scale)
    {
        float s = scale <= 0f ? 1f : scale;
        int dw = Math.Max(1, (int)MathF.Round(videoRect.W * s));
        int dh = Math.Max(1, (int)MathF.Round(videoRect.H * s));
        if (natural.IsEmpty) return new SizeI(dw, dh);
        float factor = MathF.Max((float)dw / natural.Width, (float)dh / natural.Height);
        if (!float.IsFinite(factor) || factor >= 1f) return natural;
        return new SizeI(
            Math.Max(1, (int)MathF.Round(natural.Width * factor)),
            Math.Max(1, (int)MathF.Round(natural.Height * factor)));
    }
}
