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
        return Math.Clamp(dt, 0f, 50f);
    }
}
