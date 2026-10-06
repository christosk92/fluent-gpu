using System.Threading;

namespace FluentGpu.Hosting;

/// <summary>One coalesced cold deadline, not a periodic timer or a reason to produce a frame.</summary>
internal sealed class ColdMaintenanceDeadline
{
    internal const long DelayMs = 30_000;

    /// <summary>How long the host must have been free of interaction (input, a scroll in motion, a drag, the post-input warm
    /// hold) before an ALLOCATING cold service runs: the scene slab trim reallocates every SoA column at the new capacity
    /// (812 B a slot: 6.65 MB for 16,384 → 8,192, all large-object arrays) and the snapshot reclaim builds a whole
    /// replacement snapshot. Both are cheap on an idle turn and a visible hitch inside a scroll — the 2026-10-06 real-data
    /// bench caught the 30 s scene deadline landing 2 s into a Liked Songs glide (one frame, 6.66 MB, a gen2 in one run).
    /// Longer than the gap between two flings of one scroll, so a deadline that falls due mid-scroll waits for its end.</summary>
    internal const long QuietMs = 2_000;

    /// <summary>The most an interaction can hold back a due allocating service: a scroll that never stops (an auto-scrolling
    /// list, a test rig) must not keep the slab at its high-water for the session.</summary>
    internal const long MaxDeferMs = 120_000;

    private long _dueMs = long.MaxValue;

    internal bool Arm(long nowMs)
        => Interlocked.CompareExchange(ref _dueMs, nowMs + DelayMs, long.MaxValue) == long.MaxValue;

    internal bool TryConsume(long nowMs) => TryConsume(nowMs, long.MinValue);

    /// <summary><see cref="TryConsume(long)"/> for an allocating service: the deadline is <see cref="Deferred"/> past
    /// <paramref name="quietFromMs"/> (the instant the host's last interaction stops counting).</summary>
    internal bool TryConsume(long nowMs, long quietFromMs)
    {
        long due = Volatile.Read(ref _dueMs);
        return due != long.MaxValue && nowMs >= Deferred(due, quietFromMs)
            && Interlocked.CompareExchange(ref _dueMs, long.MaxValue, due) == due;
    }

    internal int ClampWait(int waitMs, long nowMs) => ClampWait(waitMs, nowMs, Volatile.Read(ref _dueMs));

    /// <summary><see cref="ClampWait(int, long)"/> against the <see cref="Deferred"/> deadline: while an interaction holds the service back
    /// the wait is clamped to the quiet point, never to a 1 ms poll of an overdue deadline.</summary>
    internal int ClampWaitQuiet(int waitMs, long nowMs, long quietFromMs)
        => ClampWait(waitMs, nowMs, Deferred(Volatile.Read(ref _dueMs), quietFromMs));

    internal static int ClampWait(int waitMs, long nowMs, long dueMs)
    {
        if (dueMs == long.MaxValue || waitMs == 0) return waitMs;
        int remaining = (int)Math.Clamp(dueMs - nowMs, 1L, int.MaxValue);
        return waitMs < 0 ? remaining : Math.Min(waitMs, remaining);
    }

    /// <summary>When a deadline due at <paramref name="dueMs"/> may run, given that the host counts as interacting until
    /// <paramref name="quietFromMs"/>: the later of the two, but never more than <see cref="MaxDeferMs"/> past the deadline.
    /// Pure; <see cref="long.MaxValue"/> (nothing armed) stays unarmed.</summary>
    internal static long Deferred(long dueMs, long quietFromMs)
        => dueMs == long.MaxValue || quietFromMs <= dueMs ? dueMs : Math.Min(quietFromMs, dueMs + MaxDeferMs);
}
