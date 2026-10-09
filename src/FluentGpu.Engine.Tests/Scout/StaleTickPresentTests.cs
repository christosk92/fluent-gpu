using System;
using System.Diagnostics;
using System.Threading;
using FluentGpu.Hosting.Threading;
using FluentGpu.Pal;
using Xunit;

namespace FluentGpu.Engine.Tests.Scout;

/// <summary>The render thread disarms its display clock while no motion is live, so the clock's last tick stays frozen at the moment
/// it parked. A turn whose own motion comes back with it (an unpark or un-occlusion re-anchors every compositor row at now) must not
/// predict its present time from that pre-park stamp: every row would pose at its anchor (a loop at phase 0, a fade rewound) for one
/// refresh. Serial: the render thread writes process-static diagnostics.</summary>
[Collection(SerialTestCollection.Name)]
public sealed class StaleTickPresentTests
{
    private static readonly long PeriodQpc = Stopwatch.Frequency / 120;

    private sealed class ParkedClock : IRenderDisplayClock
    {
        private long _seq, _qpc;
        private readonly AutoResetEvent _tick = new(false);
        public WaitHandle Tick => _tick;
        public bool IsAvailable => true;   // a parked clock stays available: only its ticks stop
        public void SetActive(bool active) { }
        public long TickSeq => Volatile.Read(ref _seq);
        public long TickQpc => Volatile.Read(ref _qpc);
        // The real clock's order: the stamp first, then the seq (a reader that sees a new seq sees its stamp).
        public void Deliver(long seq, long qpc) { Volatile.Write(ref _qpc, qpc); Volatile.Write(ref _seq, seq); }
        public void Dispose() => _tick.Dispose();
    }

    // needsTick is false until the test says so: the loop sits in its clean-idle wait, exactly where a parked window's loop is.
    private static RenderThread Start(ParkedClock clock, Func<bool> live, Action tick)
        => new(new SceneFramePublisher(), _ => { }, async: false, needsTick: live, ownMotion: () => true, tick: tick,
               tickPeriod: () => PeriodQpc, displayClock: clock);

    [Fact]
    public void TheFirstPresentAfterAPark_IsAnchoredOnNow_NotOnTheClocksPreParkTick()
    {
        ThreadGuard.BindCurrent(ThreadGuard.ThreadRole.Ui);
        var clock = new ParkedClock();
        long parkedTickQpc = Stopwatch.GetTimestamp() - 5 * Stopwatch.Frequency;   // the clock parked 5 s ago
        clock.Deliver(41, parkedTickQpc);
        bool live = false;
        long anchor = long.MinValue;
        RenderThread? self = null;
        // RenderMotion's view: the tick this turn's present time is predicted from (AppHost.RenderTurnTickQpc; 0 = now).
        var rt = Start(clock, () => Volatile.Read(ref live), () => Volatile.Write(ref anchor, Volatile.Read(ref self)!.DisplayTickQpc));
        Volatile.Write(ref self, rt);
        try
        {
            Volatile.Write(ref live, true);   // the unpark: the idle loop's wake finds its rows resumed
            rt.DrainSync();
            Assert.Equal(1, rt.MotionPresents);
            Assert.NotEqual(parkedTickQpc, Volatile.Read(ref anchor));
            Assert.Equal(0, Volatile.Read(ref anchor));   // anchored on now, never 5 s in the past

            // The frozen tick is spent: the next present is for the first CURRENT tick, paced on it as before.
            long current = Stopwatch.GetTimestamp();
            clock.Deliver(42, current);
            rt.DrainSync();
            Assert.Equal(2, rt.MotionPresents);
            Assert.Equal(current, Volatile.Read(ref anchor));
        }
        finally { rt.Dispose(); clock.Dispose(); }
    }

    [Fact]
    public void ACurrentTickAnchorsThePresent_AndAClockThatStopsMidMotion_PresentsNothingMoreForItsSpentTick()
    {
        ThreadGuard.BindCurrent(ThreadGuard.ThreadRole.Ui);
        var clock = new ParkedClock();
        bool live = false;
        long anchor = long.MinValue;
        RenderThread? self = null;
        var rt = Start(clock, () => Volatile.Read(ref live), () => Volatile.Write(ref anchor, Volatile.Read(ref self)!.DisplayTickQpc));
        Volatile.Write(ref self, rt);
        try
        {
            long current = Stopwatch.GetTimestamp();
            clock.Deliver(7, current);
            Volatile.Write(ref live, true);
            rt.DrainSync();
            Assert.Equal(1, rt.MotionPresents);
            Assert.Equal(current, Volatile.Read(ref anchor));   // a live tick still predicts the present

            // The compositor stalls: no new seq, so tick 7's stamp only ages. It is spent; the turn re-presents nothing.
            clock.Deliver(7, current - Stopwatch.Frequency);
            rt.DrainSync();
            Assert.Equal(1, rt.MotionPresents);
            Assert.True(rt.SkippedTicks >= 1);
        }
        finally { rt.Dispose(); clock.Dispose(); }
    }
}
