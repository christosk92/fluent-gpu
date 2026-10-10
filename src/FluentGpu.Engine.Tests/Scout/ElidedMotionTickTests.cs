using System;
using System.Threading;
using FluentGpu.Hosting.Threading;
using FluentGpu.Pal;
using Xunit;

namespace FluentGpu.Engine.Tests.Scout;

/// <summary>A paced motion turn whose poses did not move (a Cadence.At(30) row on a 120 Hz panel, a live row at rest) ELIDES: the
/// host records, submits and presents nothing. That turn must not spend the compositor tick: a UI publication landing later in the
/// same tick presents at once (the vblank is still free and the credit still held) instead of waiting a whole refresh behind
/// nothing, and is no race hit. A bare re-wake on that tick does not re-pose it, and a motion turn that DID present keeps the
/// one-present-per-tick rule. Serial: the render thread writes process-static diagnostics.</summary>
[Collection(SerialTestCollection.Name)]
public sealed class ElidedMotionTickTests
{
    private sealed class FakeClock : IRenderDisplayClock
    {
        private long _seq;
        private readonly AutoResetEvent _tick = new(false);
        public WaitHandle Tick => _tick;
        public bool IsAvailable => true;
        public void SetActive(bool active) { }
        public long TickSeq => Volatile.Read(ref _seq);
        public void Deliver(long seq) => Volatile.Write(ref _seq, seq);
        public void Dispose() => _tick.Dispose();
    }

    // Force-sync: one DrainSync = one turn. The loop's own backstop turns between them land on the same seq and are gated alike.
    private static RenderThread Start(SceneFramePublisher seam, FakeClock clock, bool motionPresents, StrongBox submits, StrongBox ticks)
        => new(seam, _ => Interlocked.Increment(ref submits.Value), async: false, needsTick: () => true,
               tick: () => Interlocked.Increment(ref ticks.Value), displayClock: clock, tickPresented: () => motionPresents);

    private sealed class StrongBox { public int Value; }

    [Fact]
    public void AFreshPublication_AfterAnElidedMotionTurn_PresentsOnTheSameTick()
    {
        ThreadGuard.BindCurrent(ThreadGuard.ThreadRole.Ui);
        var seam = new SceneFramePublisher();
        var clock = new FakeClock();
        clock.Deliver(5);
        StrongBox submits = new(), ticks = new();
        byte[] one = [1];
        var rt = Start(seam, clock, motionPresents: false, submits, ticks);
        try
        {
            rt.DrainSync();
            Assert.Equal(1, Volatile.Read(ref ticks.Value));     // tick 5's motion turn ran, and elided

            seam.Publish(one, default, default);                 // a hover lands mid-tick
            rt.DrainSync();
            Assert.Equal(1, Volatile.Read(ref submits.Value));   // presented on tick 5, not deferred to tick 6
            Assert.Equal(1, rt.FreshPresents);
            Assert.Equal(0, rt.RaceHits);                        // nothing reached the glass ahead of it

            seam.Publish(one, default, default);                 // the fresh present DID spend tick 5
            rt.DrainSync();
            Assert.Equal(1, Volatile.Read(ref submits.Value));

            clock.Deliver(6);
            rt.DrainSync();
            Assert.Equal(2, Volatile.Read(ref submits.Value));   // ...and presents on the next tick
        }
        finally { rt.Dispose(); clock.Dispose(); }
    }

    [Fact]
    public void ABareReWake_OnAnElidedTick_DoesNotRePoseIt()
    {
        ThreadGuard.BindCurrent(ThreadGuard.ThreadRole.Ui);
        var seam = new SceneFramePublisher();
        var clock = new FakeClock();
        clock.Deliver(5);
        StrongBox submits = new(), ticks = new();
        var rt = Start(seam, clock, motionPresents: false, submits, ticks);
        try
        {
            rt.DrainSync();
            rt.DrainSync();
            Assert.Equal(1, Volatile.Read(ref ticks.Value));     // same tick, nothing fresh: no second pose of one vblank
            clock.Deliver(6);
            rt.DrainSync();
            Assert.Equal(2, Volatile.Read(ref ticks.Value));
            Assert.Equal(0, rt.MissedMotionTicks);               // an elided tick still counts as serviced
        }
        finally { rt.Dispose(); clock.Dispose(); }
    }

    [Fact]
    public void APresentedMotionTurn_StillSpendsTheTick()
    {
        ThreadGuard.BindCurrent(ThreadGuard.ThreadRole.Ui);
        var seam = new SceneFramePublisher();
        var clock = new FakeClock();
        clock.Deliver(5);
        StrongBox submits = new(), ticks = new();
        byte[] one = [1];
        var rt = Start(seam, clock, motionPresents: true, submits, ticks);
        try
        {
            rt.DrainSync();
            Assert.Equal(1, Volatile.Read(ref ticks.Value));
            seam.Publish(one, default, default);
            rt.DrainSync();
            Assert.Equal(0, Volatile.Read(ref submits.Value));   // one present per tick: the late publication waits
            Assert.Equal(1, rt.RaceHits);
            clock.Deliver(6);
            rt.DrainSync();
            Assert.Equal(1, Volatile.Read(ref submits.Value));
        }
        finally { rt.Dispose(); clock.Dispose(); }
    }
}
