using System.Diagnostics;
using System.Threading;
using FluentGpu.Hosting.Threading;
using FluentGpu.Pal;
using Xunit;

namespace FluentGpu.Engine.Tests.Scout;

/// <summary>A compositor clock whose waits time out publishes no tick: its seq freezes while the render loop keeps waking on
/// its backstop with motion live. The UI's production gate already stops gating on such a stale tick (AppHost.GateTickSeq),
/// so its backstop-paced frames are published; the render thread's present decision must follow the same rule, or every
/// turn finds the frozen tick "already presented" and those frames (input feedback, timers) never reach the glass until the
/// clock ticks again. A live clock keeps one present per tick, before the stall and after it. Driven force-sync (one
/// DrainSync = one turn); the loop's own backstop turns may run between drains, so the checks read the seam's pending state.
/// Serial: the present decision writes the scroll diagnostics' static tick.</summary>
[Collection(SerialTestCollection.Name)]
public sealed class StalledClockPresentTests
{
    private sealed class FrozenClock : IRenderDisplayClock
    {
        public long Seq, Qpc;
        private readonly AutoResetEvent _tick = new(false);
        public WaitHandle Tick => _tick;
        public bool IsAvailable => true;   // a stalled clock stays available: only its waits time out
        public void SetActive(bool active) { }
        public long TickSeq => Seq;
        public long TickQpc => Qpc;
        public void Dispose() => _tick.Dispose();
    }

    [Fact]
    public void AStalledClock_NoLongerSpendsItsFrozenTick_SoEveryFreshPublicationPresents()
    {
        ThreadGuard.BindCurrent(ThreadGuard.ThreadRole.Ui);
        var seam = new SceneFramePublisher();
        var clock = new FrozenClock { Seq = 7, Qpc = Stopwatch.GetTimestamp() };
        byte[] one = [1];
        var rt = new RenderThread(seam, _ => { }, async: false, needsTick: () => true, tick: () => { },
                                  tickPeriod: () => Stopwatch.Frequency / 120, displayClock: clock);
        try
        {
            // Live tick 7: the motion turn presents for it, and a fresh frame landing later in the tick waits for the next one.
            clock.Qpc = Stopwatch.GetTimestamp();
            rt.DrainSync();
            seam.Publish(one, default, default);
            rt.DrainSync();
            Assert.True(seam.HasPendingFrame);

            // The clock stops ticking: the seq stays 7 and its stamp ages past two 30 Hz refreshes.
            clock.Qpc = Stopwatch.GetTimestamp() - Stopwatch.Frequency / 5;
            for (int i = 0; i < 3; i++)
            {
                seam.Publish(one, default, default);
                rt.DrainSync();
                Assert.False(seam.HasPendingFrame);   // presented on this wake, not held for a tick that is not coming
            }

            // The clock ticks again: tick 8 presents, and a second frame inside it waits for tick 9 (one present per tick).
            clock.Seq = 8;
            clock.Qpc = Stopwatch.GetTimestamp();
            seam.Publish(one, default, default);
            rt.DrainSync();
            seam.Publish(one, default, default);
            rt.DrainSync();
            Assert.True(seam.HasPendingFrame);
        }
        finally { rt.Dispose(); clock.Dispose(); }
    }
}
