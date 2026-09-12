namespace FluentGpu.Hosting;

/// <summary>Unclamped wall time for image reveals, owned by the shared image cache, not an individual window.
/// Animation cadence resyncs cannot rewind it. Hidden windows do not render, but their image reveals can finish.</summary>
internal struct ImagePresentationClock
{
    private double _elapsedMs, _lastMs;
    private bool _initialized;

    internal double Sample(double nowMs)
    {
        if (_initialized) _elapsedMs += System.Math.Max(0, nowMs - _lastMs);
        _lastMs = _initialized ? System.Math.Max(_lastMs, nowMs) : nowMs;
        _initialized = true;
        return _elapsedMs;
    }

    internal static float Extrapolate(float clockMs, double sampledAtMs, double nowMs)
        => clockMs + (float)System.Math.Max(0, nowMs - sampledAtMs);
}
