using System;
using System.Diagnostics;

namespace FluentGpu.Media;

/// <summary>
/// The audio RT block tripwire (spec §7.9) — SEPARATE from the phases-6–13 tripwire. In M4 it runs in PER-CALLBACK mode:
/// around each block the RT feed thread pulls, it asserts <b>0 managed allocations</b>
/// (<c>GC.GetAllocatedBytesForCurrentThread()</c> delta == 0), <b>0 managed lock acquisitions</b>, <b>0 blocking calls</b>,
/// and a <b>bounded duration</b>. Every call is <see cref="ConditionalAttribute">[Conditional("FG_AUDIO_TRIPWIRE")]</see>-
/// erased from the shipping AOT binary (production safety == CI coverage); a test build defines the symbol (or measures the
/// GC delta directly around the deterministic "pull N blocks" loop). Copy+mix ONLY on this path — no managed alloc, no
/// locks, no blocking, no logging. The RT thread instruments its (non-existent) lock/blocking sites via
/// <see cref="NoteLock"/>/<see cref="NoteBlockingCall"/>, so a regression that introduced one would trip the wire.
/// </summary>
public static class AudioTripwire
{
    /// <summary>The per-callback duration ceiling. A well-behaved copy+mix block finishes in microseconds; this bound only
    /// catches a gross regression (a syscall/blocking call sneaking onto the RT path). Generous to avoid CI flakiness.</summary>
    public static double MaxBlockMilliseconds = 50.0;

    [ThreadStatic] private static long s_before;
    [ThreadStatic] private static long s_startTicks;
    [ThreadStatic] private static int s_locks;
    [ThreadStatic] private static int s_blocking;
    [ThreadStatic] private static bool s_armed;

    /// <summary>Arm the tripwire at the start of an RT block (records the alloc/lock/blocking/time baseline).</summary>
    [Conditional("FG_AUDIO_TRIPWIRE")]
    public static void BeginBlock()
    {
        s_locks = 0;
        s_blocking = 0;
        s_startTicks = Stopwatch.GetTimestamp();
        s_before = GC.GetAllocatedBytesForCurrentThread();
        s_armed = true;
    }

    /// <summary>Instrument a managed lock acquisition on the RT path — MUST never fire (the RT thread is lock-free).</summary>
    [Conditional("FG_AUDIO_TRIPWIRE")]
    public static void NoteLock() => s_locks++;

    /// <summary>Instrument a blocking call on the RT path — MUST never fire (the RT thread never blocks/syscalls).</summary>
    [Conditional("FG_AUDIO_TRIPWIRE")]
    public static void NoteBlockingCall() => s_blocking++;

    /// <summary>Assert the block did copy+mix ONLY (spec §7.9): 0 managed bytes, 0 locks, 0 blocking calls, bounded duration.
    /// Throws on violation — the caller compiled the symbol in only under test.</summary>
    [Conditional("FG_AUDIO_TRIPWIRE")]
    public static void EndBlock()
    {
        if (!s_armed) return;
        s_armed = false;

        long delta = GC.GetAllocatedBytesForCurrentThread() - s_before;
        if (delta != 0)
            throw new InvalidOperationException($"AudioTripwire: RT block allocated {delta} managed bytes (must be 0).");
        if (s_locks != 0)
            throw new InvalidOperationException($"AudioTripwire: RT block acquired {s_locks} managed lock(s) (must be 0).");
        if (s_blocking != 0)
            throw new InvalidOperationException($"AudioTripwire: RT block made {s_blocking} blocking call(s) (must be 0).");

        double ms = (Stopwatch.GetTimestamp() - s_startTicks) * 1000.0 / Stopwatch.Frequency;
        if (ms > MaxBlockMilliseconds)
            throw new InvalidOperationException($"AudioTripwire: RT block took {ms:F2} ms (bound {MaxBlockMilliseconds:F2} ms).");
    }
}
