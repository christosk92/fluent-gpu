namespace FluentGpu.Foundation;

/// <summary>The CALLING thread's consumed CPU cycles — the portable seam the UI-gap decomposition
/// (<c>AppHost.UiGap</c>) and the render thread's worst-present split read to tell "the thread was running" from "the
/// thread was not scheduled". Diagnostics only: never a pacer, never a decision input.
/// <para>The engine is platform-free, so the counter comes from the PAL: the Windows backend installs
/// <see cref="Source"/> once (<c>QueryThreadCycleTime(GetCurrentThread())</c>, a static cached delegate — no allocation per
/// read). Headless and any backend that installs nothing read 0, which every consumer treats as "unknown".</para>
/// <para>Cycles are not time: a consumer converts a delta through a cycles-per-ms rate it calibrates itself over a
/// span it KNOWS the thread was running (a measured work phase).</para></summary>
public static class ThreadCycles
{
    /// <summary>The platform's per-thread cycle counter, or null (then <see cref="Read"/> answers 0 = unknown).</summary>
    public static Func<ulong>? Source { get; set; }

    /// <summary>True when a platform counter is installed.</summary>
    public static bool Available => Source is not null;

    /// <summary>The calling thread's cycle count, or 0 when no counter is installed.</summary>
    public static ulong Read() => Source is { } s ? s() : 0UL;

    /// <summary>Fold one calibration sample (<paramref name="cycles"/> consumed over <paramref name="wallMs"/> of wall time
    /// spent in a phase the thread was MEANT to be running) into the cycles-per-ms rate. The rate is the RUNNING MAXIMUM:
    /// the counter ticks at a fixed rate while the thread runs, and anything inside a sample that was not running (a
    /// pre-emption, a fence wait inside a present) only LOWERS that sample, so the supremum is the true rate. Samples under
    /// 1 ms are ignored (timer granularity). Pure.</summary>
    public static double Calibrate(double rate, ulong cycles, double wallMs)
    {
        if (wallMs < 1.0 || cycles == 0) return rate;
        double sample = cycles / wallMs;
        return sample > rate ? sample : rate;
    }

    /// <summary>A cycle delta as running milliseconds at <paramref name="cyclesPerMs"/>; NaN when either is unknown.</summary>
    public static float ToMs(ulong startCycles, ulong endCycles, double cyclesPerMs)
    {
        if (startCycles == 0 || endCycles == 0 || cyclesPerMs <= 0.0 || endCycles < startCycles) return float.NaN;
        return (float)((endCycles - startCycles) / cyclesPerMs);
    }
}
