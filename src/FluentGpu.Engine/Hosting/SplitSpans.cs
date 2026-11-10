using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace FluentGpu.Hosting;

/// <summary>
/// <c>--fg sub-split</c>, the host half: spans of a render turn's record and submit that run in the engine (the device's own
/// phases are in D3D12Device.SubmitSplit.cs, which prints both and clears these). Render-thread-owned plain ticks; a span is a
/// no-op while the switch is off.
/// </summary>
public static class SplitSpans
{
    public const int AnimTick = 0, RecBeginPass = 1, RecSetup = 2, RecWalk = 3, RecEndPass = 4, RecPlace = 5, Build = 6, EndComposite = 7, Count = 8;
    public static readonly string[] Names =
        ["host.animTick", "rec.beginPass", "rec.setup", "rec.walk", "rec.endPass", "rec.place", "host.buildComposite", "host.endComposite"];
    public static readonly long[] Ticks = new long[Count];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long Now() => EngineSwitches.SubmitSplit ? Stopwatch.GetTimestamp() : 0;

    /// <summary>Add the time since <paramref name="t"/> to a span and restart it.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Add(int span, ref long t)
    {
        if (t == 0) return;
        long now = Stopwatch.GetTimestamp();
        Ticks[span] += now - t;
        t = now;
    }
}
