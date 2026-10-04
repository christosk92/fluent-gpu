using System;
using System.Diagnostics;
using System.Threading;
using FluentGpu.Foundation;
using FluentGpu.Hosting;
using FluentGpu.Hosting.Threading;
using FluentGpu.Pal;
using FluentGpu.Rhi;
using FluentGpu.Rhi.Headless;
using Xunit;

namespace FluentGpu.Engine.Tests;

/// <summary>
/// A detached pop-out is a paced render source (F106): its production is gated to one frame per COMPOSITOR tick of the
/// parent's display clock (it has no clock of its own), and its present may be non-blocking (F085) so the shared render
/// thread never waits inside a secondary window's Present. A real pop-out cannot present headlessly (a Headless window never
/// spawns a render thread), so the DECISIONS are tested where they live: the gate's pure decision, the tick the child
/// samples from the parent render thread's clock, the non-blocking present contract and the child's pace evidence.
/// Serial: it flips a process-static engine switch.
/// </summary>
[Collection(SerialTestCollection.Name)]
public sealed class DetachedChildPacingTests
{
    private sealed class FakeTickClock : IRenderDisplayClock
    {
        public long Seq, Qpc;
        public bool Available = true;
        private readonly AutoResetEvent _tick = new(false);
        public WaitHandle Tick => _tick;
        public bool IsAvailable => Available;
        public void SetActive(bool active) { }
        public long TickSeq => Seq;
        public long TickQpc => Qpc;
        public void Dispose() => _tick.Dispose();
    }

    private static RenderThread NewThread(FakeTickClock? clock)
    {
        ThreadGuard.BindCurrent(ThreadGuard.ThreadRole.Ui);
        return new RenderThread(new SceneFramePublisher(), _ => { }, async: false, displayClock: clock);
    }

    // ── the production gate ─────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Gate_DeclinesOnlyASecondProductionInsideTheSameKnownTick_OfAWaitThatArmedTheClock()
    {
        Assert.True(AppHost.ProductionGateDeclines(frameTickSeq: 7, lastProducedTickSeq: 7, waitWantsDisplayClock: true));
        Assert.False(AppHost.ProductionGateDeclines(8, 7, true));    // a new tick: produce
        Assert.False(AppHost.ProductionGateDeclines(0, 0, true));    // no tick known (no clock / stale): never gate
        Assert.False(AppHost.ProductionGateDeclines(7, 7, false));   // the wait did not arm the clock: its seq counted nothing
    }

    [Fact]
    public void Gate_LetsAChildProduceAtMostOnce_PerParentTick_HoweverManyLoopIterationsRunInIt()
    {
        // The UI loop iterates on every wake (input over either window, a media post, a 200 ms ticker); the child's RunFrame
        // runs on each. Model its gate exactly as RunFrame does: sample the tick, decline when already produced for it,
        // otherwise record the tick and produce (publish -> one bare wake of the shared render thread).
        long lastProduced = 0;
        int produced = 0, declined = 0;
        for (long tick = 1; tick <= 20; tick++)
        {
            for (int iteration = 0; iteration < 5; iteration++)
            {
                if (AppHost.ProductionGateDeclines(tick, lastProduced, waitWantsDisplayClock: true)) { declined++; continue; }
                lastProduced = tick;
                produced++;
            }
        }
        Assert.Equal(20, produced);        // one publication (and one render-thread wake) per tick
        Assert.Equal(20 * 4, declined);    // the other four iterations of every tick were declined, not produced
    }

    [Fact]
    public void CombineWait_ArmsTheDisplayClock_WheneverEitherFiniteRequestWantsIt_WhicheverTimeoutWins()
    {
        // The main window's shorter cadence wait (no clock) must not disarm the compositor clock under a pop-out whose own wait
        // wants the vblank: both hosts' ticks come from the one parent-window clock.
        var cadence = new PlatformWaitRequest(8);
        var video = new PlatformWaitRequest(33, PlatformInputWakePolicy.CoalescePointerMotion, WakeOnDisplayClock: true);
        var combined = AppHost.CombineWait(cadence, video);
        Assert.Equal(8, combined.TimeoutMs);
        Assert.True(combined.WakeOnDisplayClock);
        Assert.Equal(PlatformInputWakePolicy.Immediate, combined.InputWakePolicy);   // the winner's own wake policy
        Assert.Equal(combined, AppHost.CombineWait(video, cadence));                 // order does not matter
        // A "block until a message" side states no preference: the other request passes through unchanged.
        var idle = new PlatformWaitRequest(-1);
        Assert.Equal(video, AppHost.CombineWait(idle, video));
        Assert.Equal(cadence, AppHost.CombineWait(cadence, idle));
    }

    // ── the child's tick comes from the parent render thread's clock ────────────────────────────────────────────────

    [Fact]
    public void ParentRenderThread_SamplesItsCurrentTick_ForTheChild_SeqBeforeStamp()
    {
        long now = Stopwatch.GetTimestamp();
        var clock = new FakeTickClock { Seq = 41, Qpc = now - Stopwatch.Frequency / 1000 };
        var rt = NewThread(clock);
        try
        {
            Assert.True(rt.DisplayClockAvailable);
            Assert.True(rt.TryGetDisplayTick(now, out long seq, out long qpc));
            Assert.Equal(41, seq);
            Assert.Equal(clock.Qpc, qpc);
        }
        finally { rt.Dispose(); clock.Dispose(); }
    }

    [Fact]
    public void ParentRenderThread_ReportsNoTick_WhenTheClockIsAbsent_Unavailable_Unsaid_OrStale()
    {
        long now = Stopwatch.GetTimestamp();

        var none = NewThread(null);
        try
        {
            Assert.False(none.DisplayClockAvailable);
            Assert.False(none.TryGetDisplayTick(now, out long s0, out long q0));
            Assert.Equal(0, s0);
            Assert.Equal(0, q0);
        }
        finally { none.Dispose(); }

        var clock = new FakeTickClock { Seq = 5, Qpc = now };
        var rt = NewThread(clock);
        try
        {
            clock.Available = false;
            Assert.False(rt.TryGetDisplayTick(now, out _, out _));      // a disposed / failed clock delivers nothing
            clock.Available = true;

            clock.Qpc = 0;
            Assert.False(rt.TryGetDisplayTick(now, out _, out _));      // a backend that cannot say its stamp
            clock.Qpc = now;

            clock.Seq = 0;
            Assert.False(rt.TryGetDisplayTick(now, out _, out _));      // a backend that cannot say its seq
            clock.Seq = 5;

            Assert.True(rt.TryGetDisplayTick(now, out _, out _));
            // A tick older than ~two refreshes of a 30 Hz panel is the clock's parked past, not its current beat: a child
            // gating on it would decline forever, so it reads as "no clock" and the child falls back to unpaced.
            Assert.False(rt.TryGetDisplayTick(now + Stopwatch.Frequency, out _, out _));
        }
        finally { rt.Dispose(); clock.Dispose(); }
    }

    // ── the non-blocking secondary present ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void NonBlockingPresent_IsOnlyForADetachedChildOnAParentThread_BehindTheSwitch()
    {
        Assert.True(AppHost.UsesNonBlockingPresent(isDetachedChild: true, hasParentRenderThread: true, switchOn: true));
        Assert.False(AppHost.UsesNonBlockingPresent(true, true, switchOn: false));                          // default off
        Assert.False(AppHost.UsesNonBlockingPresent(isDetachedChild: false, true, true));                   // the primary keeps its contract
        Assert.False(AppHost.UsesNonBlockingPresent(true, hasParentRenderThread: false, true));             // inline child: UI-thread present
    }

    [Fact]
    public void EngineSwitch_PresentNoWait_ArmsTheFlag()
    {
        bool before = EngineSwitches.NonBlockingSecondaryPresent;
        try
        {
            EngineSwitches.NonBlockingSecondaryPresent = false;
            EngineSwitches.ApplyList("present-nowait");
            Assert.True(EngineSwitches.NonBlockingSecondaryPresent);
        }
        finally { EngineSwitches.NonBlockingSecondaryPresent = before; }
    }

    [Fact]
    public void HeadlessSwapchain_PresentNoWait_RefusesWithoutPresenting_ThenPresentsOnceTheQueueDrains()
    {
        var sc = new HeadlessSwapchain(new Size2(64, 64));
        ISwapchain target = sc;

        sc.RefusePresentNoWait = true;
        Assert.False(target.PresentNoWait());                  // refused: nothing presented, the frame stays owed
        Assert.False(target.PresentNoWait());
        Assert.Equal(0, sc.PresentCount);
        Assert.Equal(2, sc.RefusedPresents);
        Assert.False(sc.HasPresentedContent);

        sc.RefusePresentNoWait = false;
        Assert.True(target.PresentNoWait());                   // the queue drained: the owed frame goes out
        Assert.Equal(1, sc.PresentCount);
        Assert.True(sc.HasPresentedContent);
    }

    [Fact]
    public void DefaultPresentNoWait_IsThePlainPresent()
    {
        var sc = new PlainSwapchain();
        Assert.True(((ISwapchain)sc).PresentNoWait());         // a backend with no queue to refuse from presents as ever
        Assert.Equal(1, sc.Presents);
    }

    private sealed class PlainSwapchain : ISwapchain
    {
        public int Presents;
        public Size2 SizePx => new(8, 8);
        public void Resize(Size2 px) { }
        public void Present() => Presents++;
        public void Dispose() { }
    }

    [Fact]
    public void ChildPace_CountsRefusedPresents_InItsOwnWindow()
    {
        var pace = new ChildPresentPace(11);
        pace.BeginWindow();
        Assert.Null(pace.Describe());

        pace.NoteSkipped();                                    // refused with nothing else this window: still evidence
        string? line = pace.Describe();
        Assert.NotNull(line);
        Assert.Contains("skipped=1", line);
        Assert.Contains("presents=0", line);

        pace.NotePresent(1, 1);                                // the owed frame went out on a later turn
        pace.NoteSkipped();
        Assert.Equal(2, pace.Skipped);
        Assert.Contains("skipped=2", pace.Describe());

        pace.BeginWindow();
        Assert.Null(pace.Describe());                          // rebased: an idle child adds nothing
        Assert.Equal(2, pace.Skipped);                         // the cumulative total keeps counting
    }
}
