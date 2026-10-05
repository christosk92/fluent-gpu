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
/// <para><b>Two pumps, two owners (F132).</b> The pump has two halves with different owners. The STATE half (state,
/// buffering, position, duration, natural size, errors, seek landing, the DRM phase) needs no surface and no element: the
/// facade (<see cref="MediaPlayer"/>) runs it itself on the UI thread whenever the session raises
/// <see cref="IVideoPumpSource.PumpRequested"/>, so the control plane advances while no element is mounted. The GEOMETRY half (bind
/// the surface handle, size the stream, place the child) belongs to whichever element presents the video. Both halves are
/// value-gated, and a session that publishes nothing twice runs them in either order; <see cref="PumpVideo"/> is the two
/// in one call.</para>
/// </summary>
public interface IVideoSurfaceSession
{
    /// <summary>Pump one UI-thread turn, state AND geometry: translate engine state → the connected
    /// <see cref="MediaSignalSink"/>, bind the produced DComp surface handle through <paramref name="binding"/>
    /// (value-gated), place the child at <paramref name="videoRect"/> (DIP) and size the video stream to
    /// <paramref name="videoRect"/>×<paramref name="scale"/> (device px). An inert <paramref name="binding"/> (IsValid false)
    /// skips every surface step and leaves the state half. Called for initial binding and then when a native event,
    /// transport command, activation, or geometry change requests a coalesced turn; it is intentionally not a per-frame
    /// repaint path.</summary>
    void PumpVideo(VideoBinding binding, RectF videoRect, float scale);

    /// <summary>The state half alone: publish engine state, buffering, position, duration, natural size, errors, seek landing and
    /// the DRM phase into the connected sink. Touches no surface and needs no element. UI thread only. The default runs
    /// <see cref="PumpVideo"/> with an inert binding, so a session that has not split its pump still publishes state.</summary>
    void PumpState() => PumpVideo(default, default, 1f);

    /// <summary>The geometry half alone: bind the surface handle through <paramref name="binding"/>, size the stream and place the
    /// child. Publishes no state of its own beyond the surface readiness it owns, and does nothing for an inert binding.
    /// UI thread only. The default runs the whole <see cref="PumpVideo"/>.</summary>
    void PumpGeometry(VideoBinding binding, RectF videoRect, float scale) => PumpVideo(binding, videoRect, scale);
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
/// a destination is worth. <see cref="ContentSizeFor"/> is the continuous ideal; the sessions size the stream with
/// <see cref="BucketedSizeFor"/> through a <see cref="VideoStreamSizeGate"/> so layout never drives the swap chain.
/// <para>The stream follows the on-screen size in BOTH directions (F089): below the natural frame it is a bucket of it, above
/// it the stream is upscaled by Media Foundation's video processor (filtered) instead of by DirectComposition's transform,
/// up to <c>max(monitor, natural)</c>, and a fullscreen presentation asks for the monitor-fit size outright. Without a
/// monitor size (<see cref="VideoDisplay.HasMonitor"/> false: headless, or a backend that cannot report one) the cap is the
/// natural size and nothing is ever upscaled, the behaviour before F089.</para></summary>
public static class VideoStreamSizing
{
    /// <summary>The size (device px) to render the decoded frame at inside the engine's own swap chain, which is ALSO
    /// the content size handed to the compositor — the two must agree or DirectComposition scales by the wrong factor.
    /// <para>It is the destination's size on screen, on both axes by one factor, so the frame's aspect ratio — the input
    /// to the fit — survives exactly. The factor is chosen from the axis that is magnified MOST, so a Fill/UniformToFill
    /// destination never samples a smaller buffer up again. Below 1 it keeps a frame far larger than its rect from
    /// allocating buffers nobody can see (a 4K stream in a 480-px card); above 1 (the destination is bigger than the frame:
    /// fullscreen, a large dock) the stream is upscaled by Media Foundation, capped at <c>max(monitor, natural)</c> so no
    /// buffer is bigger than any screen can show. A fullscreen presentation asks for the monitor-fit size.</para>
    /// <para>A natural size that is not known yet falls back to the destination rect, so the surface still presents
    /// something during the pump or two before the engine answers.</para></summary>
    public static SizeI ContentSizeFor(SizeI natural, RectF videoRect, float scale, VideoDisplay display = default)
    {
        // A rect that is not laid out (the element pumps with an empty one before its area exists) is not a destination:
        // it is worth NO buffer, so it answers the natural size (or "unknown") rather than a 2x1 swap chain (F051).
        if (!IsLaidOut(videoRect)) return natural;
        float s = scale <= 0f ? 1f : scale;
        int dw = Math.Max(1, (int)MathF.Round(videoRect.W * s));
        int dh = Math.Max(1, (int)MathF.Round(videoRect.H * s));
        if (natural.IsEmpty) return new SizeI(dw, dh);
        float factor = NeedFactor(natural, dw, dh, display, out _);
        if (!float.IsFinite(factor)) return natural;
        return ScaledSize(natural, MathF.Min(factor, MaxFactor(natural, display)));
    }

    // A destination within this fraction of the monitor-fit size, in a fullscreen window, IS the fullscreen video (rounding,
    // a sub-pixel margin). A window that is fullscreen while the video sits in a small dock is not: it keeps its own size.
    private const float FullscreenSnap = 0.95f;
    // An upscaled stream is kept while it stays within [need x 0.97, need x 1.15]: a few percent under is an imperceptible
    // DirectComposition scale, a few percent over wastes nothing worth a swap-chain re-allocation (the hysteresis of the
    // downscale buckets, for the continuous range above the natural size).
    private const float UpscaleUnder = 0.97f;
    private const float UpscaleOver = 1.15f;

    /// <summary>The factor of the natural frame the destination asks for: the most magnified axis. In a fullscreen window,
    /// a destination that reaches the monitor-fit size is lifted to exactly it (<paramref name="exact"/>), so the stream
    /// lands on the monitor size and DirectComposition's remaining scale is 1.</summary>
    private static float NeedFactor(SizeI natural, int dw, int dh, VideoDisplay display, out bool exact)
    {
        float need = MathF.Max((float)dw / natural.Width, (float)dh / natural.Height);
        exact = false;
        if (display.Fullscreen && display.HasMonitor)
        {
            float fit = MathF.Min((float)display.MonitorPx.Width / natural.Width, (float)display.MonitorPx.Height / natural.Height);
            if (float.IsFinite(fit) && need >= fit * FullscreenSnap) { need = MathF.Max(need, fit); exact = true; }
        }
        return need;
    }

    /// <summary>The largest factor of the natural frame a stream may be rendered at: <c>max(monitor, natural)</c> on the
    /// more magnified axis, never below 1. 1 (no upscale at all) while the monitor size is unknown.</summary>
    private static float MaxFactor(SizeI natural, VideoDisplay display)
        => display.HasMonitor
            ? MathF.Max(1f, MathF.Max((float)display.MonitorPx.Width / natural.Width, (float)display.MonitorPx.Height / natural.Height))
            : 1f;

    /// <summary><paramref name="natural"/> scaled by one factor on both axes (the ratio survives to rounding); the natural
    /// size itself for a factor of 1.</summary>
    private static SizeI ScaledSize(SizeI natural, float factor)
        => factor >= 1f - CoverEps && factor <= 1f + CoverEps
            ? natural
            : new SizeI(Math.Max(1, (int)MathF.Round(natural.Width * factor)),
                        Math.Max(1, (int)MathF.Round(natural.Height * factor)));

    /// <summary>True when <paramref name="videoRect"/> is a real destination (both extents positive). The element pumps with
    /// <c>default</c> for an area that is not laid out yet or has collapsed to zero; no session may size a stream, a content
    /// size or a placement from that (F051).</summary>
    public static bool IsLaidOut(RectF videoRect) => videoRect.W > 0f && videoRect.H > 0f;

    // The fractions of the natural frame the stream may be rendered at, largest first. A destination never asks for an
    // arbitrary size: it asks for the smallest rung that still covers it, so a continuous resize crosses a rung only a
    // few times instead of re-allocating the decoder's swap chain on every layout (F071).
    private static readonly float[] s_buckets = { 1f, 0.75f, 0.5f, 1f / 3f };
    private const float CoverEps = 1e-4f;
    // A bucket is left for a SMALLER one only when the destination fits it comfortably (10 % under it), so a rect that
    // jitters around a rung boundary does not flip between two sizes.
    private const float ShrinkMargin = 0.9f;

    /// <summary>The stream size (device px) for a destination. Below the natural frame, a bucket of <c>natural x {1, 3/4,
    /// 1/2, 1/3}</c>: the smallest that still covers the destination on its most magnified axis.
    /// <paramref name="current"/> is the size in use (empty = none): it is kept while it still covers the destination, and
    /// left for a smaller bucket only once the destination fits that comfortably (hysteresis). Above the natural frame (F089)
    /// the destination's own size, upscaled by Media Foundation, capped at <c>max(monitor, natural)</c> and kept while it stays
    /// within a few percent of the destination. A fullscreen window whose video fills the monitor asks for exactly the
    /// monitor-fit size. A <paramref name="display"/> with no monitor size never upscales. A natural size that is not known
    /// yet answers the destination itself. Pure.</summary>
    public static SizeI BucketedSizeFor(SizeI natural, RectF videoRect, float scale, SizeI current, VideoDisplay display = default)
    {
        float s = scale <= 0f ? 1f : scale;
        int dw = Math.Max(1, (int)MathF.Round(videoRect.W * s));
        int dh = Math.Max(1, (int)MathF.Round(videoRect.H * s));
        if (natural.IsEmpty || natural.Width <= 0 || natural.Height <= 0) return new SizeI(dw, dh);
        float need = NeedFactor(natural, dw, dh, display, out bool exact);
        if (!float.IsFinite(need)) return natural;
        float cap = MaxFactor(natural, display);
        if (exact) return ScaledSize(natural, MathF.Min(need, cap));   // the monitor-fit size itself: no bucket, no hysteresis
        if (need > 1f + CoverEps && cap > 1f + CoverEps)
        {
            float want = MathF.Min(need, cap);
            float haveUp = current.Width > 0 ? (float)current.Width / natural.Width : 0f;
            if (haveUp > 1f + CoverEps && haveUp >= want * UpscaleUnder && haveUp <= want * UpscaleOver && haveUp <= cap + CoverEps)
                return current;
            return ScaledSize(natural, want);
        }
        if (need >= 1f - CoverEps) return natural;

        float candidate = 1f;
        for (int i = s_buckets.Length - 1; i >= 0; i--)
            if (s_buckets[i] >= need - CoverEps) { candidate = s_buckets[i]; break; }

        // A size larger than the frame, or none, is not a bucket of THIS natural size: nothing to keep.
        float have = current.Width > 0 ? (float)current.Width / natural.Width : 0f;
        if (have > 0f && have <= 1f + CoverEps && have >= need - CoverEps
            && (have <= candidate + CoverEps || need > candidate * ShrinkMargin))
            return current;
        return SizeAtFraction(natural, candidate);
    }

    private static SizeI SizeAtFraction(SizeI natural, float fraction)
        => fraction >= 1f
            ? natural
            : new SizeI(Math.Max(1, (int)MathF.Round(natural.Width * fraction)),
                        Math.Max(1, (int)MathF.Round(natural.Height * fraction)));

    /// <summary>True when <paramref name="content"/> (the size a surface is currently sized to) is still the size
    /// <see cref="BucketedSizeFor"/> would keep for this destination: a geometry-only turn (a drag, a resize inside the
    /// bucket) needs no session pump then, because nothing about the stream would change. Pure.</summary>
    public static bool Serves(SizeI content, SizeI natural, RectF videoRect, float scale, VideoDisplay display = default)
        => !content.IsEmpty && BucketedSizeFor(natural, videoRect, scale, content, display) == content;
}

/// <summary>What the stream sizing needs to know about the screen the video is shown on (F089): the full size of the monitor
/// (device px, not the work area) and whether the window is fullscreen on it. Read from the presenting window through
/// <see cref="VideoBinding.Display"/> on the UI thread; <c>default</c> (no monitor size) means "unknown", and the sizing then
/// never goes above the natural frame.</summary>
public readonly struct VideoDisplay
{
    /// <summary>The monitor's size in device px; empty when unknown.</summary>
    public readonly SizeI MonitorPx;
    /// <summary>True while the presenting window is borderless-fullscreen on that monitor.</summary>
    public readonly bool Fullscreen;

    /// <summary>The monitor a window is on and whether it is fullscreen there.</summary>
    public VideoDisplay(SizeI monitorPx, bool fullscreen)
    {
        MonitorPx = monitorPx;
        Fullscreen = fullscreen;
    }

    /// <summary>True when the monitor size is known (both extents positive).</summary>
    public bool HasMonitor => MonitorPx.Width > 0 && MonitorPx.Height > 0;
}

/// <summary>What <see cref="VideoStreamSizeGate.Step"/> decided for one pump.</summary>
public readonly struct VideoStreamStep
{
    /// <summary>The stream size to ask the backend for NOW; empty when there is nothing new to ask. A request is posted
    /// at most once per decision (re-posted only when the natural size changed, because a backend that keeps its stream
    /// size per source must hear it again).</summary>
    public readonly SizeI Request;
    /// <summary>The content size the compositor may scale from: the size the backend has CONFIRMED rendering, held at the
    /// previous value while a new request is in flight. Empty only before the first laid-out decision.</summary>
    public readonly SizeI Content;
    /// <summary>When positive, nothing else is guaranteed to pump this session again (a settle window or an echo is still
    /// pending): request one more pump after this many milliseconds.</summary>
    public readonly int RetryInMs;

    /// <summary>One decision: what to ask the backend, what the compositor may scale from, and when to look again.</summary>
    public VideoStreamStep(SizeI request, SizeI content, int retryInMs)
    {
        Request = request;
        Content = content;
        RetryInMs = retryInMs;
    }
}

/// <summary>The per-session policy that keeps the decoder's swap chain from following layout (F071), shared by both
/// Windows backends so they can never disagree. It owns three decisions:
/// <list type="number">
/// <item>WHAT size: <see cref="VideoStreamSizing.BucketedSizeFor"/> (natural x {1, 3/4, 1/2, 1/3} below the frame, the
/// on-screen size up to max(monitor, natural) above it, with hysteresis).</item>
/// <item>WHEN: the first size at once and a changed natural size at once; any other change only after the destination
/// has held still for <see cref="SettleMs"/> (never mid-animation: DirectComposition's scale covers the gap).</item>
/// <item>WHAT THE COMPOSITOR IS TOLD: the content size moves to a newly requested size only when the backend echoes
/// that it APPLIED it, so DirectComposition never scales an old-size buffer by a new-size factor. A backend that
/// cannot echo (or has not by <see cref="EchoTimeoutMs"/> while paused) is trusted after the timeout.</item>
/// </list>
/// A destination that is not laid out decides nothing. UI-thread only; the clock is passed in so it is testable.</summary>
public sealed class VideoStreamSizeGate
{
    /// <summary>How long the destination must hold still before a stream-size change is requested.</summary>
    public const int SettleMs = 250;
    /// <summary>How long a requested size may stay unconfirmed before a paused (or non-echoing) backend is trusted.</summary>
    public const int EchoTimeoutMs = 500;
    /// <summary>The pump cadence while an echo is awaited (the protected runtime raises no event for it).</summary>
    public const int EchoPollMs = 40;

    private SizeI _natural, _requested, _published;
    private VideoDisplay _display;
    private int _geomW, _geomH;
    private long _geomSinceMs, _requestedAtMs;

    /// <summary>The size last handed to the backend (empty before the first decision).</summary>
    public SizeI Requested => _requested;
    /// <summary>The content size last handed to the compositor (empty before the first decision).</summary>
    public SizeI Published => _published;

    /// <summary>Decide one pump. <paramref name="applied"/> is the backend's echo of the stream size it last applied
    /// (empty = none yet / not reported); <paramref name="playing"/> is whether frames are being presented right now.
    /// <paramref name="display"/> is the monitor the video is on (default = unknown: the stream is never upscaled); a change of
    /// it counts as a geometry change, so a fullscreen edge settles like a resize and is never applied mid-animation.</summary>
    public VideoStreamStep Step(SizeI natural, RectF videoRect, float scale, SizeI applied, bool playing, long nowMs, VideoDisplay display = default)
    {
        if (!VideoStreamSizing.IsLaidOut(videoRect)) return new VideoStreamStep(default, _published, 0);

        float s = scale <= 0f ? 1f : scale;
        int dw = Math.Max(1, (int)MathF.Round(videoRect.W * s));
        int dh = Math.Max(1, (int)MathF.Round(videoRect.H * s));
        bool displayChanged = display.MonitorPx != _display.MonitorPx || display.Fullscreen != _display.Fullscreen;
        _display = display;
        if (dw != _geomW || dh != _geomH || displayChanged) { _geomW = dw; _geomH = dh; _geomSinceMs = nowMs; }

        bool naturalChanged = natural != _natural;
        _natural = natural;

        SizeI desired = VideoStreamSizing.BucketedSizeFor(natural, videoRect, scale, naturalChanged ? default : _requested, display);
        int retry = 0;
        SizeI request = default;
        if (_requested.IsEmpty || naturalChanged)
        {
            request = desired;   // the first size, or a new frame size: not a layout change, nothing to wait out
        }
        else if (desired != _requested)
        {
            long stable = nowMs - _geomSinceMs;
            if (stable >= SettleMs) request = desired;
            else retry = (int)(SettleMs - stable);
        }
        if (!request.IsEmpty)
        {
            _requested = request;
            _requestedAtMs = nowMs;
        }

        if (_published.IsEmpty)
        {
            // Nothing to keep: the buffer the backend already holds (its echo) is what the compositor must scale from, else
            // the size just requested (a backend with no buffer yet creates it at that size).
            _published = !applied.IsEmpty ? applied : _requested;
        }
        // Also right after the first publish: a backend that already holds a buffer (the protected runtime's natural-size
        // swap chain) is published at that buffer while a smaller bucket is requested, and it raises no event for the echo.
        if (_published != _requested)
        {
            long waited = nowMs - _requestedAtMs;
            bool echoed = applied == _requested;
            bool trust = (!playing || applied.IsEmpty) && waited >= EchoTimeoutMs;
            if (echoed || trust) _published = _requested;
            else if (waited < EchoTimeoutMs)
            {
                int poll = (int)Math.Min(EchoPollMs, EchoTimeoutMs - waited);
                retry = retry > 0 ? Math.Min(retry, poll) : poll;
            }
        }
        return new VideoStreamStep(request, _published, retry);
    }
}
