namespace FluentGpu.Hosting.Threading;

/// <summary>Scalar-only sustained-slack policy; eligibility never allocates or authorizes touching a reader slot.</summary>
internal struct SceneCapacityReclaimPolicy
{
    internal const long LowWaterMs = 30_000, CooldownMs = 120_000;
    internal const int MinimumCapacity = 256, Headroom = 32;
    private bool _hasLowWater, _hasReclaimed;
    private long _lowSince, _lastReclaim;
    private int _peakRequired;

    internal void Observe(int capacity, int required, long nowMs)
    {
        int target = Target(required);
        if ((long)target * 4 > capacity)
        {
            _hasLowWater = false;
            return;
        }
        if (!_hasLowWater || nowMs < _lowSince)
        {
            _hasLowWater = true;
            _lowSince = nowMs;
            _peakRequired = required;
        }
        else _peakRequired = Math.Max(_peakRequired, required);
    }

    internal bool TryTarget(int capacity, long nowMs, out int target)
    {
        target = Target(_peakRequired);
        return _hasLowWater && nowMs >= _lowSince && nowMs - _lowSince >= LowWaterMs
            && (!_hasReclaimed || (nowMs >= _lastReclaim && nowMs - _lastReclaim >= CooldownMs))
            && (long)target * 4 <= capacity;
    }

    internal long NextDeadlineMs(int capacity)
        => !_hasLowWater || (long)Target(_peakRequired) * 4 > capacity ? long.MaxValue
            : Math.Max(_lowSince + LowWaterMs, _hasReclaimed ? _lastReclaim + CooldownMs : long.MinValue);

    internal void NoteAttempt(long nowMs)
    {
        _hasLowWater = false;
        _hasReclaimed = true;
        _lastReclaim = nowMs;
    }

    internal static int Target(int required)
    {
        long need = Math.Max(MinimumCapacity, (long)required + Headroom);
        int capacity = MinimumCapacity;
        while (capacity < need && capacity <= int.MaxValue / 2) capacity *= 2;
        return capacity < need ? int.MaxValue : capacity;
    }
}
