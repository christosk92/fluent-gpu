using System;
using System.Threading;

namespace FluentGpu.Scroll.Diag;

public static partial class ScrollProbe
{
    // ── always-on unrequested-jump counter (2026-09-25, item J) ───────────────────────────────────────────────────────
    // Every jump ScrollJumpRules finds (a viewport at rest whose anchor row moved on screen with no user input and no
    // programmatic move), counted WHATEVER the probe level, with the last one's viewport / time / from / to / cause: what
    // an app's auto evidence bundle triggers on. UI thread writes; any thread reads (torn-free longs).
    private static long s_jumps, s_lastJumpQpc, s_lastJumpFromBits, s_lastJumpToBits;
    private static int s_lastJumpVp, s_lastJumpCause;

    /// <summary>Cumulative count of unrequested scroll jumps (always on). A reader differences it.</summary>
    public static long Jumps => Volatile.Read(ref s_jumps);

    /// <summary>The most recent jump: viewport node, frame present QPC, the shown offsets before/after and its cause.</summary>
    public static (int Vp, long Qpc, double From, double To, ScrollJumpCause Cause) LastJump
        => (Volatile.Read(ref s_lastJumpVp), Volatile.Read(ref s_lastJumpQpc),
            BitConverter.Int64BitsToDouble(Volatile.Read(ref s_lastJumpFromBits)),
            BitConverter.Int64BitsToDouble(Volatile.Read(ref s_lastJumpToBits)),
            (ScrollJumpCause)Volatile.Read(ref s_lastJumpCause));

    /// <summary>UI THREAD. Count one jump. Zero allocation.</summary>
    public static void NoteJump(int vp, long qpc, in ScrollJump jump)
    {
        Volatile.Write(ref s_lastJumpVp, vp);
        Volatile.Write(ref s_lastJumpQpc, qpc);
        Volatile.Write(ref s_lastJumpFromBits, BitConverter.DoubleToInt64Bits(jump.From));
        Volatile.Write(ref s_lastJumpToBits, BitConverter.DoubleToInt64Bits(jump.To));
        Volatile.Write(ref s_lastJumpCause, (int)jump.Cause);
        Interlocked.Increment(ref s_jumps);
    }
}
