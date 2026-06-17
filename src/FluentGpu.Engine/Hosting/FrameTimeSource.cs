using System.Diagnostics;

namespace FluentGpu.Hosting;

public interface IFrameTimeSource
{
    float NextDeltaMs();
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

    public float NextDeltaMs()
    {
        long now = Stopwatch.GetTimestamp();
        if (_last == 0)
        {
            _last = now;
            return 0f;
        }

        float dt = (now - _last) * 1000f / Stopwatch.Frequency;
        _last = now;
        // Cap the per-tick advance at ~2 frames (60 Hz). A cold/janky frame can accumulate a large delta (e.g. the first
        // modal-loop tick after WM_ENTERSIZEMOVE, or a GC pause); without a tight cap a 250 ms transition front-loaded by
        // a decelerate curve would LEAP ~0.67 in a single tick — reading as "it just popped, no animation". 34 ms keeps
        // every transition to ≥~7 visible steps while still letting a 30 Hz display run essentially real-time.
        return Math.Clamp(dt, 0f, 34f);
    }
}
