using System.Threading;

namespace FluentGpu.Hosting;

/// <summary>One coalesced cold deadline, not a periodic timer or a reason to produce a frame.</summary>
internal sealed class ColdMaintenanceDeadline
{
    internal const long DelayMs = 30_000;
    private long _dueMs = long.MaxValue;

    internal bool Arm(long nowMs)
        => Interlocked.CompareExchange(ref _dueMs, nowMs + DelayMs, long.MaxValue) == long.MaxValue;

    internal bool TryConsume(long nowMs)
    {
        long due = Volatile.Read(ref _dueMs);
        return due != long.MaxValue && nowMs >= due
            && Interlocked.CompareExchange(ref _dueMs, long.MaxValue, due) == due;
    }

    internal int ClampWait(int waitMs, long nowMs) => ClampWait(waitMs, nowMs, Volatile.Read(ref _dueMs));

    internal static int ClampWait(int waitMs, long nowMs, long dueMs)
    {
        if (dueMs == long.MaxValue || waitMs == 0) return waitMs;
        int remaining = (int)Math.Clamp(dueMs - nowMs, 1L, int.MaxValue);
        return waitMs < 0 ? remaining : Math.Min(waitMs, remaining);
    }
}
