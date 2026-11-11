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

/// <summary>One reading of a session's playback clock: where the engine's clock stood (<see cref="Position"/>), the
/// <see cref="System.Diagnostics.Stopwatch.GetTimestamp"/> it stood there AT (<see cref="Timestamp"/>), the rate it runs at and
/// whether it was running. The sample carries its own instant, so a reader that receives it late extrapolates from when it was
/// TRUE, never from when it arrived: a stale value stamped "now" is a frozen playhead that jumps when the next one lands.
/// A plain value; reading one allocates nothing.</summary>
public readonly struct MediaClockSample
{
    /// <summary>The clock's position at <see cref="Timestamp"/>.</summary>
    public readonly TimeSpan Position;
    /// <summary>The Stopwatch timestamp (QPC ticks) at which <see cref="Position"/> was read from the engine.</summary>
    public readonly long Timestamp;
    /// <summary>The rate the clock advances at while <see cref="Running"/>.</summary>
    public readonly double Rate;
    /// <summary>The clock was advancing when it was read (playing, not seeking, not starved).</summary>
    public readonly bool Running;

    /// <summary>A sample of <paramref name="position"/> read at <paramref name="timestamp"/>.</summary>
    public MediaClockSample(TimeSpan position, long timestamp, double rate, bool running)
    {
        Position = position;
        Timestamp = timestamp;
        Rate = rate > 0 ? rate : 1.0;
        Running = running;
    }

    /// <summary>The clock at <paramref name="nowTimestamp"/>: the sample, advanced by the time since it was read × rate while it
    /// was running. Pure.</summary>
    public TimeSpan At(long nowTimestamp)
    {
        if (!Running || nowTimestamp <= Timestamp) return Position;
        return Position + TimeSpan.FromSeconds((nowTimestamp - Timestamp) * Rate / System.Diagnostics.Stopwatch.Frequency);
    }
}

/// <summary>A session whose playback clock can be read between state pumps. The published <see cref="IMediaPlayer.Position"/>
/// is the clock as of the last pump (a pump runs on an engine event, about once a second while playing); a reader on its own
/// cadence (a position ticker, a hand-off that must land on the clock NOW) reads the engine's latest sample here instead.</summary>
public interface IMediaClockSource
{
    /// <summary>The engine's latest clock sample. Any thread; never blocks, never allocates. False while there is none (not
    /// opened yet, disposed, superseded).</summary>
    bool TryReadClock(out MediaClockSample sample);
}

/// <summary>The renderer's frame counters as the engine last READ them (not as of the last state pump): rendered and dropped
/// frames of the current source, and the <see cref="System.Diagnostics.Stopwatch.GetTimestamp"/> they were read at (0 = never).
/// A reader differences two samples over the time between their <see cref="SampledTimestamp"/>s.</summary>
public readonly record struct MediaFrameCounters(long Rendered, long Dropped, long SampledTimestamp);

/// <summary>A session whose frame counters can be read between state pumps (the published <c>PlaybackStatistics</c> is the
/// counters as of the last pump).</summary>
public interface IMediaFrameCounterSource
{
    /// <summary>The engine's latest counter sample. Any thread; never blocks, never allocates. False while there is none.</summary>
    bool TryReadFrameCounters(out MediaFrameCounters counters);
}

/// <summary>How big a composited video stream should be. ONE owner for both Windows backends (the clear
/// <c>MfMediaSession</c> and the protected <c>ProtectedMediaSession</c>), so the two can never disagree about the buffer
/// a destination is worth: the sessions size the stream with <see cref="BucketedSizeFor"/> through a
/// <see cref="VideoStreamSizeGate"/> so layout never drives the swap chain. The compositor stretches whatever buffer the swap chain
/// holds to the placed rect, so the stream size decides quality and memory, never where or how big the picture is.
/// <para>The stream follows the on-screen size in BOTH directions (F089): below the natural frame it is a bucket of it, above
/// it the stream is upscaled by Media Foundation's video processor (filtered) instead of by the compositor's stretch,
/// up to <c>max(monitor, natural)</c>, and a fullscreen presentation asks for the monitor-fit size outright. Without a
/// monitor size (<see cref="VideoDisplay.HasMonitor"/> false: headless, or a backend that cannot report one) the cap is the
/// natural size and nothing is ever upscaled, the behaviour before F089.</para></summary>
public static class VideoStreamSizing
{
    // A destination within this fraction of the monitor-fit size, in a fullscreen window, IS the fullscreen video (rounding,
    // a sub-pixel margin). A window that is fullscreen while the video sits in a small dock is not: it keeps its own size.
    private const float FullscreenSnap = 0.95f;
    // An upscaled stream is kept while it stays within [need x 0.97, need x 1.15]: a few percent under is an imperceptible
    // compositor stretch, a few percent over wastes nothing worth a swap-chain re-allocation (the hysteresis of the
    // downscale buckets, for the continuous range above the natural size).
    private const float UpscaleUnder = 0.97f;
    private const float UpscaleOver = 1.15f;

    /// <summary>The factor of the natural frame the destination asks for: the most magnified axis. In a fullscreen window,
    /// a destination that reaches the monitor-fit size is lifted to exactly it (<paramref name="exact"/>), so the stream
    /// lands on the monitor size and the compositor's remaining stretch is 1.</summary>
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
    /// <c>default</c> for an area that is not laid out yet or has collapsed to zero; no session may size a stream or a
    /// placement from that (F051).</summary>
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
    /// yet answers the destination itself. A full-screen video begins a stay instead: the stay size (<see cref="TryStaySize"/>),
    /// kept for any destination while the window is full screen and it is the size in use. Pure.</summary>
    public static SizeI BucketedSizeFor(SizeI natural, RectF videoRect, float scale, SizeI current, VideoDisplay display = default)
    {
        float s = scale <= 0f ? 1f : scale;
        int dw = Math.Max(1, (int)MathF.Round(videoRect.W * s));
        int dh = Math.Max(1, (int)MathF.Round(videoRect.H * s));
        if (natural.IsEmpty || natural.Width <= 0 || natural.Height <= 0) return new SizeI(dw, dh);
        float need = NeedFactor(natural, dw, dh, display, out bool exact);
        if (!float.IsFinite(need)) return natural;
        float cap = MaxFactor(natural, display);
        // The compositor stretches whatever buffer the swap chain holds, so a size change buys no correctness, only quality, and
        // costs a decoder gap. A full-screen stay has ONE size: it begins where the destination is the full-screen video (exact),
        // and while the window stays full screen the size in use (current) IS the memory, kept through a Theatre toggle, a pane
        // or an aspect-mode change. The stay size is the cap, never below what any picture inside the stay would get otherwise.
        if (TryStaySize(natural, display, out SizeI stay) && (exact || current == stay)) return stay;
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

    /// <summary>The one stream size of a full-screen stay: <c>natural x</c> the cap (the monitor's size on the
    /// more magnified axis, never below the natural frame). False outside a full-screen window with a known monitor, or before
    /// the natural size is known.</summary>
    internal static bool TryStaySize(SizeI natural, VideoDisplay display, out SizeI stay)
    {
        stay = default;
        if (!display.Fullscreen || !display.HasMonitor || natural.Width <= 0 || natural.Height <= 0) return false;
        stay = ScaledSize(natural, MaxFactor(natural, display));
        return true;
    }

    private static SizeI SizeAtFraction(SizeI natural, float fraction)
        => fraction >= 1f
            ? natural
            : new SizeI(Math.Max(1, (int)MathF.Round(natural.Width * fraction)),
                        Math.Max(1, (int)MathF.Round(natural.Height * fraction)));

    /// <summary>True when <paramref name="stream"/> (the size a surface's stream is currently sized to) is still the size
    /// <see cref="BucketedSizeFor"/> would keep for this destination: a geometry-only turn (a drag, a resize inside the
    /// bucket) needs no session pump then, because nothing about the stream would change. Pure.</summary>
    public static bool Serves(SizeI stream, SizeI natural, RectF videoRect, float scale, VideoDisplay display = default)
        => !stream.IsEmpty && BucketedSizeFor(natural, videoRect, scale, stream, display) == stream;
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
    /// <summary>The stream size in use: the last one asked of the backend (this decision's <see cref="Request"/> included). Empty
    /// only before the first laid-out decision.</summary>
    public readonly SizeI Size;
    /// <summary>When positive, nothing else is guaranteed to pump this session again (a settle window is still pending): request one
    /// more pump after this many milliseconds.</summary>
    public readonly int RetryInMs;

    /// <summary>The <c>stream.stay enter|exit ...</c> line when this decision began or ended a full-screen
    /// stay, else null. The session writes it to the [video] timeline (once per edge, never per frame).</summary>
    public string? StayLine { get; init; }
    /// <summary>The <c>stream.hold want=WxH before=first-frame</c> line when this decision held a new
    /// request until the attach's first frame, else null. The session writes it to the [video] timeline.</summary>
    public string? HoldLine { get; init; }

    /// <summary>One decision: what to ask the backend, the stream size in use, and when to look again.</summary>
    public VideoStreamStep(SizeI request, SizeI size, int retryInMs)
    {
        Request = request;
        Size = size;
        RetryInMs = retryInMs;
    }
}

/// <summary>The per-session policy that keeps the decoder's swap chain from following layout (F071), shared by both
/// Windows backends so they can never disagree. It owns two decisions:
/// <list type="number">
/// <item>WHAT size: <see cref="VideoStreamSizing.BucketedSizeFor"/> (natural x {1, 3/4, 1/2, 1/3} below the frame, the
/// on-screen size up to max(monitor, natural) above it, with hysteresis).</item>
/// <item>WHEN: the first size at once and a changed natural size at once; any other change only after the destination
/// has held still for <see cref="SettleMs"/> (never mid-animation: the compositor stretches the old buffer meanwhile). The
/// request that begins a full-screen stay also goes at once, and a request raised between an attach's first request and its
/// first frame is held until that frame, then posted at once.</item>
/// </list>
/// A destination that is not laid out decides nothing. UI-thread only; the clock is passed in so it is testable.
/// <para><b>One stream, many bindings.</b> A session has ONE swap chain, and more than one element can present it at once (the
/// Canvas hand-off from the style picker's card to the rail's picture, a docked video and its full-screen stage while one exits).
/// Each binding that pumps CASTS a vote with its destination; the size is decided from the largest need among the votes that
/// still count (the pumping binding's own, and every other binding whose slot is still live and shown: an exiting, hidden,
/// parked or released host does not vote), so the last binding to pump is never the last word just because it pumped last.
/// And the stream size is the session's, not a binding's: <see cref="PublishSizeToVoters"/> hands it to every binding that
/// shows the picture, so no slot's geometry-only pump judges its rect against a size the session has left.</para></summary>
public sealed class VideoStreamSizeGate
{
    /// <summary>How many bindings of one session can vote at once (a hand-off has two; a slot reused by a new element replaces its
    /// predecessor's vote).</summary>
    public const int MaxVotes = 4;

    private struct Vote
    {
        public bool Used;
        public VideoBinding Binding;
        public long Incarnation;
        public RectF Rect;
        public float Scale;
        public VideoDisplay Display;
    }

    private readonly Vote[] _votes = new Vote[MaxVotes];
    private int _governingToken, _voteCount;

    /// <summary>The token of the binding whose destination decided the size at the last step (0 for an anonymous caller).</summary>
    public int GoverningToken => _governingToken;
    /// <summary>How many bindings counted at the last step.</summary>
    public int VoteCount => _voteCount;
    /// <summary>How long the destination must hold still before a stream-size change is requested.</summary>
    public const int SettleMs = 250;

    private SizeI _natural, _requested;
    private VideoDisplay _display;
    private int _geomW, _geomH;
    private long _geomSinceMs;
    // The full-screen stay in use (empty = none); whether this attach has posted its first request and presented its first frame;
    // the request held until that frame (and whether it is a natural-size re-post).
    private SizeI _stay, _heldWant;
    private bool _attachRequested, _framePresented, _held, _heldRepost;

    /// <summary>The size last handed to the backend (empty before the first decision).</summary>
    public SizeI Requested => _requested;

    /// <summary>Decide one pump of <paramref name="binding"/>: its destination is its vote, and the size is decided from the largest
    /// need among the votes that count (see the class remarks); an inert binding is one anonymous voter.
    /// <paramref name="framePresented"/> is whether this attach's first frame has been presented (a request raised between the
    /// attach's first request and its first frame is held until then). <paramref name="display"/> is the monitor the video is on
    /// (default = unknown: the stream is never upscaled); a change of it counts as a geometry change, so a fullscreen edge settles
    /// like a resize and is never applied mid-animation.</summary>
    public VideoStreamStep Step(in VideoBinding binding, SizeI natural, RectF videoRect, float scale, bool framePresented, long nowMs, VideoDisplay display = default)
    {
        if (!VideoStreamSizing.IsLaidOut(videoRect)) return new VideoStreamStep(default, _requested, 0);

        bool naturalChanged = natural != _natural;
        _natural = natural;

        // The caller's vote, then the size every counting vote needs: the largest wins. A natural-size re-post held for the first
        // frame has nothing to keep either, as if it had been posted.
        int own = Cast(binding, videoRect, scale, display);
        SizeI current = naturalChanged || _heldRepost ? default : _requested;
        SizeI desired = default;
        int gov = own;
        _voteCount = 0;
        for (int i = 0; i < MaxVotes; i++)
        {
            ref Vote v = ref _votes[i];
            if (!v.Used) continue;
            if (i != own && v.Binding.IsValid && !v.Binding.IsShowing(v.Incarnation)) { v = default; continue; }   // left, hidden or reused: no vote
            _voteCount++;
            SizeI need = VideoStreamSizing.BucketedSizeFor(natural, v.Rect, v.Scale, current, v.Display);
            if (desired.IsEmpty || (long)need.Width * need.Height > (long)desired.Width * desired.Height) { desired = need; gov = i; }
        }
        ref Vote g = ref _votes[gov];
        _governingToken = g.Binding.Token;

        // Settling follows the GOVERNING destination: a change of which binding governs, or of its size or screen, is a geometry change.
        float s = g.Scale <= 0f ? 1f : g.Scale;
        int dw = Math.Max(1, (int)MathF.Round(g.Rect.W * s));
        int dh = Math.Max(1, (int)MathF.Round(g.Rect.H * s));
        bool displayChanged = g.Display.MonitorPx != _display.MonitorPx || g.Display.Fullscreen != _display.Fullscreen;
        _display = g.Display;
        if (dw != _geomW || dh != _geomH || displayChanged) { _geomW = dw; _geomH = dh; _geomSinceMs = nowMs; }

        // The size a full-screen stay keeps for the governing destination (empty outside a full-screen window).
        SizeI stay = VideoStreamSizing.TryStaySize(natural, g.Display, out SizeI st) ? st : default;

        int retry = 0;
        SizeI request = default;
        if (_requested.IsEmpty || naturalChanged)
        {
            request = desired;   // the first size, or a new frame size: not a layout change, nothing to wait out
        }
        else if (desired != _requested && !stay.IsEmpty && desired == stay)
        {
            // The request that BEGINS a full-screen stay goes at once. It is a discrete edge, not a layout
            // settling, and the compositor stretches the old buffer meanwhile, so nothing is drawn at a wrong scale.
            request = desired;
        }
        else if (desired != _requested)
        {
            long stable = nowMs - _geomSinceMs;
            if (stable >= SettleMs) request = desired;
            else retry = (int)(SettleMs - stable);
        }

        string? holdLine = null, stayLine = null;
        // A new attach (its first frame gone again on the same session) starts over: its first request goes at once.
        if (_framePresented && !framePresented) _attachRequested = false;
        _framePresented = framePresented;
        if (!framePresented && _attachRequested)
        {
            // Between the attach's first request (the one Media Foundation builds its swap chain at) and its first frame, a
            // further request costs one more swap-chain rebuild: hold what was raised, and post it with the first frame.
            if (!request.IsEmpty)
            {
                _heldRepost |= naturalChanged;
                if (!_held || request != _heldWant) holdLine = $"stream.hold want={request.Width}x{request.Height} before=first-frame";
                _held = true;
                _heldWant = request;
                request = default;
            }
        }
        else if (_held && framePresented)
        {
            // The first frame is here: the held request goes at once (no settle), for the destination as it is now; a held
            // natural-size re-post is posted even when it asks for the same size again, as it would have been.
            if (desired != _requested || _heldRepost) request = desired;
            _held = false;
            _heldRepost = false;
            _heldWant = default;
        }
        if (!request.IsEmpty) _attachRequested = true;

        // The stay's edges, once each: it begins with the request for the stay size in a full-screen window (a new natural size
        // inside one begins the next), and ends when the window leaves full screen or another size is asked for in it.
        if (!request.IsEmpty && request == stay && request != _stay)
        {
            _stay = stay;
            stayLine = $"stream.stay enter size={stay.Width}x{stay.Height} natural={natural.Width}x{natural.Height} monitor={g.Display.MonitorPx.Width}x{g.Display.MonitorPx.Height}";
        }
        else if (!_stay.IsEmpty && (stay.IsEmpty || (!request.IsEmpty && request != stay)))
        {
            stayLine = $"stream.stay exit size={_stay.Width}x{_stay.Height}";
            _stay = default;
        }
        if (!request.IsEmpty) _requested = request;
        return new VideoStreamStep(request, _requested, retry) { StayLine = stayLine, HoldLine = holdLine };
    }

    /// <summary>Hand the session's stream size to every OTHER binding that votes (live and shown), so a binding that is not pumping
    /// right now never judges its geometry-only turns against a size the session has left. <paramref name="pumping"/> writes its own.
    /// UI thread.</summary>
    public void PublishSizeToVoters(in VideoBinding pumping)
    {
        if (_requested.IsEmpty) return;
        for (int i = 0; i < MaxVotes; i++)
        {
            ref Vote v = ref _votes[i];
            if (!v.Used || !v.Binding.IsValid || v.Binding.SameSlot(pumping)) continue;
            if (!v.Binding.IsShowing(v.Incarnation)) { v = default; continue; }
            v.Binding.SetStreamSize(_requested);
        }
    }

    // Records the pumping binding's destination as its vote (replacing its slot's earlier one, or a predecessor's on a reused token)
    // and returns its index. With every seat taken by live voters, the oldest-indexed other seat is given up.
    private int Cast(in VideoBinding binding, RectF rect, float scale, VideoDisplay display)
    {
        long incarnation = binding.Incarnation;
        int seat = -1, free = -1;
        for (int i = 0; i < MaxVotes; i++)
        {
            if (!_votes[i].Used) { if (free < 0) free = i; continue; }
            if (_votes[i].Binding.SameSlot(binding)) { seat = i; break; }
        }
        if (seat < 0) seat = free >= 0 ? free : 0;
        _votes[seat] = new Vote { Used = true, Binding = binding, Incarnation = incarnation, Rect = rect, Scale = scale, Display = display };
        return seat;
    }
}
