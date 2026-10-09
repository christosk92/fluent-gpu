using System.Diagnostics;
using FluentGpu.Hosting;
using Xunit;

namespace FluentGpu.Engine.Tests.Scout;

/// <summary>The async production gate declines a second frame inside one compositor tick. When the clock STOPS ticking (its
/// waits time out: a wedged compositor, a display that went dark) the seq freezes while the host keeps waking on its tick
/// backstop with due-now work. A frozen seq must stop gating once its tick is stale, or every backstop wake is declined and
/// Paint (timers, the reactive flush, the re-render of dispatched input) never runs until the clock ticks again. A live clock
/// keeps the one-frame-per-tick rule. The headless host never samples a display clock, so the decision is driven the way
/// RunFrame drives it: sample the gate seq, decline or produce and record it.</summary>
public sealed class StalledClockGateTests
{
    private static readonly long Freq = Stopwatch.Frequency;
    private static readonly long MaxAge = Freq / 15;   // the host's stale-tick bound (RenderThread.TryGetDisplayTick's rule)

    [Fact]
    public void AStalledClock_StopsGating_OnceItsTickIsStale_SoBackstopWakesProduce()
    {
        // 120 Hz: the clock published tick 7 at t0 and then only timed out; the host keeps waking on its 17 ms backstop.
        long t0 = 1_000 * Freq, backstop = Freq * 17 / 1000;
        long lastProduced = 7;   // the frame for tick 7 was produced
        int produced = 0, declined = 0;
        for (int wake = 1; wake <= 30; wake++)
        {
            long seq = AppHost.GateTickSeq(available: true, tickSeq: 7, tickQpc: t0, nowQpc: t0 + wake * backstop, maxAgeQpc: MaxAge);
            if (AppHost.ProductionGateDeclines(seq, lastProduced, waitWantsDisplayClock: true)) { declined++; continue; }
            lastProduced = seq;
            produced++;
        }
        Assert.Equal(3, declined);    // 17, 34 and 51 ms: still a current tick (a late or missed vblank), gated
        Assert.Equal(27, produced);   // from 68 ms on the backstop paces production: timers, flush and re-render keep running
    }

    [Fact]
    public void ALiveClock_StillGatesASecondProductionInsideItsTick_AndABackstopWakeOnOneMissedTick()
    {
        long t0 = 1_000 * Freq, refresh = Freq / 60;
        // an input wake half a refresh after the tick the frame was produced for: declined
        Assert.True(AppHost.ProductionGateDeclines(AppHost.GateTickSeq(true, 7, t0, t0 + refresh / 2, MaxAge), 7, true));
        // one missed tick at 60 Hz: the 34 ms backstop ends the wait about two refreshes after it, still current, still gated
        Assert.True(AppHost.ProductionGateDeclines(AppHost.GateTickSeq(true, 7, t0, t0 + 2 * refresh + Freq / 1000, MaxAge), 7, true));
        // the next tick produces
        Assert.False(AppHost.ProductionGateDeclines(AppHost.GateTickSeq(true, 8, t0 + refresh, t0 + refresh + Freq / 1000, MaxAge), 7, true));
        Assert.Equal(0, AppHost.GateTickSeq(available: false, tickSeq: 7, tickQpc: t0, nowQpc: t0, maxAgeQpc: MaxAge));   // no clock
        Assert.Equal(0, AppHost.GateTickSeq(available: true, tickSeq: 0, tickQpc: 0, nowQpc: t0, maxAgeQpc: MaxAge));     // not ticked yet
    }
}
