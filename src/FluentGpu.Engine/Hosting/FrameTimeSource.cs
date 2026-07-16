using System.Diagnostics;

namespace FluentGpu.Hosting;

public interface IFrameTimeSource
{
    float NextDeltaMs();

    /// <summary>Drop the accumulated inter-frame delta so the NEXT <see cref="NextDeltaMs"/> returns ~one frame instead
    /// of a stale gap. The host calls this when the loop steps from a THROTTLED/idle cadence (the ambient 30 Hz cap, or
    /// a fully-blocked idle) up to display rate for interactive or one-shot motion. Without it the first active frame
    /// inherits the whole throttle gap (clamped to 34 ms ≈ 2–4 display frames) and every animation — scroll, hover, a
    /// connected-animation fly — LURCHES forward on frame 1 then glides: the "feels 24 fps then 120 fps" inconsistency.
    /// No-op for the fixed/manual (headless/test) sources, whose cadence never changes.</summary>
    void Resync() { }
}

public sealed class FixedFrameTimeSource(float stepMs = 16f) : IFrameTimeSource
{
    public float StepMs { get; set; } = stepMs;
    public float NextDeltaMs() => StepMs;
}

public sealed class ManualFrameTimeSource : IFrameTimeSource
{
    private float _pendingMs;
    public void Advance(float ms) => _pendingMs += MathF.Max(0f, ms);
    public float NextDeltaMs()
    {
        float dt = _pendingMs;
        _pendingMs = 0f;
        return dt;
    }
}

public sealed class StopwatchFrameTimeSource : IFrameTimeSource
{
    private long _last;

    /// <summary>The last frame's UNCLAMPED wall-clock delta (ms) — diagnostics only (the 34ms clamp below hides the
    /// true magnitude of a hitch from every consumer of <see cref="NextDeltaMs"/>).</summary>
    public float LastRawDeltaMs { get; private set; }

    public float NextDeltaMs()
    {
        long now = Stopwatch.GetTimestamp();
        if (_last == 0)
        {
            _last = now;
            LastRawDeltaMs = 0f;
            return 0f;
        }

        float dt = (now - _last) * 1000f / Stopwatch.Frequency;
        _last = now;
        LastRawDeltaMs = dt;
        // Cap the per-tick advance at ~2 frames (60 Hz). A cold/janky frame can accumulate a large delta (e.g. the first
        // modal-loop tick after WM_ENTERSIZEMOVE, or a GC pause); without a tight cap a 250 ms transition front-loaded by
        // a decelerate curve would LEAP ~0.67 in a single tick — reading as "it just popped, no animation". 34 ms keeps
        // every transition to ≥~7 visible steps while still letting a 30 Hz display run essentially real-time.
        return Math.Clamp(dt, 0f, 34f);
    }

    // Forget the last timestamp; the next NextDeltaMs() re-seeds and returns 0 (one no-advance frame), so a cadence
    // step-up (ambient/idle → display rate) does not feed the stale gap into the animators. See IFrameTimeSource.Resync.
    public void Resync() => _last = 0;
}
