namespace FluentGpu.Media;

/// <summary>
/// Pointer-side seek coalescing shared by media rails. The caller supplies monotonic milliseconds and owns the
/// dispatch/timer; this value performs no I/O and allocates nothing. Decoder concurrency belongs to the player.
/// A commit or cancellation clears pending previews so a posted move cannot overwrite a released gesture.
/// </summary>
public struct SeekPreviewScheduler
{
    public const int IntervalMs = 100;
    private bool _pending;
    private bool _hasDispatched;
    private long _targetMs;
    private long _lastDispatchMs;

    public readonly bool HasPending => _pending;

    public void Queue(long targetMs)
    {
        _targetMs = targetMs;
        _pending = true;
    }

    public bool TryTake(long nowMs, out long targetMs)
    {
        targetMs = default;
        if (!_pending || (_hasDispatched && nowMs - _lastDispatchMs < IntervalMs))
            return false;
        targetMs = _targetMs;
        _pending = false;
        _hasDispatched = true;
        _lastDispatchMs = nowMs;
        return true;
    }

    public void Reset() => this = default;
    public void DiscardPending() => _pending = false;
}
