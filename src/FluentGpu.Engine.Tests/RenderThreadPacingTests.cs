using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using FluentGpu.Hosting;
using FluentGpu.Hosting.Threading;
using FluentGpu.Pal;
using Xunit;

namespace FluentGpu.Engine.Tests;

/// <summary>
/// The pacing cliffs ("half fps after sustained touchpad scrolling"; "one late frame leaves scrolling a frame behind").
/// The render thread is the one presenter; while motion is live its turn is the compositor tick it woke for. It must
/// present FOR THAT TICK:
/// <list type="bullet">
/// <item>never re-charge the present to a later tick because the present-slot wait crossed a vblank — with a present
/// queue that cannot absorb a frame costing slightly more than one refresh, that re-charge turned every such frame into a
/// skipped next tick: exactly HALF the refresh rate;</item>
/// <item>never wait for the UI's in-flight frame — a UI frame that lands late only means the fresh CONTENT reaches the
/// glass a tick later; the render-side poses (scroll, compositor animations) must still be presented on every tick;</item>
/// <item>never queue behind a late frame — a present that missed its vblank retires only at the NEXT vblank, so a turn
/// that waits for its slot presents one vblank late, and so does every later turn while motion continues (09-29: runs of
/// 55–86 turns one tick behind). While frames fit the early phase the turn skips instead (<see cref="SlotCatchUp"/>) and
/// the next tick presents on time; over-budget frames keep the wait (skipping them re-opens the half-rate cliff), and a
/// catch-up that does not hold backs off.</item>
/// </list>
/// Driven deterministically on a VIRTUAL display: time is measured in refreshes and a tick (a vblank) is delivered at
/// every integer instant. The depth-1 frame-latency SEMAPHORE is modelled: a present at instant t of a frame costing c
/// refreshes holds the slot until it retires at ceil(t + c) + 0.02 — the first vblank after the frame is complete, plus
/// the 0.2 ms retire offset the 09-29 captures show. A take waits (virtually) at most its timeout: a slot that frees in
/// time moves the clock to the retire (never backwards) and is held; one that does not moves the clock by the timeout
/// and reports busy, holding nothing. The render thread's tick period is 120 Hz, so its paced grace
/// (<see cref="SlotCatchUp.GraceMs"/> = 2 ms) is 0.24 refresh. The UI publishes one frame per tick that lands a scripted
/// delay after it. The render thread runs force-sync (one DrainSync = one turn = one harness tick); "presents per tick"
/// counts presents per turn the loop was given — the loop's own decisions. The harness does not model the host's
/// present-queue depth policy (depth 2 at GPU ≥ 0.8·refresh), which owns how many over-budget frames reach the glass.
/// </summary>
[Collection(SerialTestCollection.Name)]
public sealed class RenderThreadPacingTests
{
    private static readonly long PeriodQpc = Stopwatch.Frequency / 120;
    // The refresh exactly as the render thread derives it from its tick period — the unit its grace timeouts are in.
    private static readonly double RefreshMs = PeriodQpc * 1000.0 / Stopwatch.Frequency;
    // The paced grace in refreshes (2 ms of 8.33 = 0.24).
    private static readonly double Grace = SlotCatchUp.GraceMs(RefreshMs) / RefreshMs;
    private const double RetireOffset = 0.02;

    private sealed class VirtualDisplay : IRenderDisplayClock
    {
        public double Now;                                  // virtual time, in refreshes
        private readonly AutoResetEvent _tick = new(false);
        public WaitHandle Tick => _tick;
        public bool IsAvailable => true;
        public void SetActive(bool active) { }
        public long TickSeq => (long)Math.Floor(Now) + 1;  // ticks delivered so far (tick k arrives at instant k-1)
        public void Dispose() => _tick.Dispose();
    }

    /// <summary>One harness tick: the display tick the turn woke for, whether it presented, whether it was a catch-up
    /// skip, and how long (refreshes) the turn waited for its slot (NaN when it never held one).</summary>
    private readonly record struct Turn(long Tick, bool Presented, bool CaughtUp, double SlotWait);

    private readonly record struct Outcome(int Ticks, long Fresh, long Motion, long CatchUpSkips, long Missed, Turn[] Turns,
                                           double RealMs)
    {
        public double PresentsPerTick => (Fresh + Motion) / (double)Ticks;
        public int TurnsWithAPresent
        {
            get { int n = 0; foreach (var t in Turns) if (t.Presented) n++; return n; }
        }
    }

    /// <param name="cost">The cost of the n-th present's frame, in refreshes, from its present until it is complete;
    /// the flag says the present is the first after a catch-up skip. 0.8 = the GPU keeps up; 1.05 = a frame costs slightly
    /// more than a refresh (heat-induced clock drops push a 5 ms frame there on a 120 Hz panel).</param>
    /// <param name="publishDelay">When the UI's frame for tick k lands, in refreshes after that tick.</param>
    private static Outcome Run(Func<int, bool, double> cost, double publishDelay, int ticks = 120)
    {
        ThreadGuard.BindCurrent(ThreadGuard.ThreadRole.Ui);
        // reverse: publications are made from INSIDE the render thread's slot take (a UI frame landing while the render
        // thread is blocked there) — the seam then asserts the render role for Publish.
        var seam = new SceneFramePublisher(reverse: true);
        var display = new VirtualDisplay();
        byte[] one = [1];
        int nextPublish = 0;                                 // UI frame index next to land
        int presentsThisTurn = 0, presentIndex = 0;
        double slotFreeAt = 0;                               // when the in-flight present retires (the semaphore re-signals)
        bool creditHeld = false, firstAfterCatchUp = false;
        double lastGpuMs = 0;
        double turnTakeAt = double.NaN, turnSlotWait = double.NaN;
        void Deliver()
        {
            // Every UI frame whose landing instant has passed is published now (DropOldest keeps the newest).
            while (nextPublish + publishDelay <= display.Now) { seam.Publish(one, default, default); nextPublish++; }
        }
        bool TakeSlot(int timeoutMs)
        {
            Deliver();
            if (double.IsNaN(turnTakeAt)) turnTakeAt = display.Now;
            if (!creditHeld)
            {
                double limit = timeoutMs < 0 ? double.PositiveInfinity : display.Now + timeoutMs / RefreshMs;
                if (slotFreeAt > limit)
                {
                    display.Now = limit;                     // the bounded wait ran out: nothing taken
                    Deliver();
                    return false;
                }
                if (slotFreeAt > display.Now) display.Now = slotFreeAt;   // the wait itself (it may cross the next vblank)
                creditHeld = true;
            }
            turnSlotWait = display.Now - turnTakeAt;
            Deliver();                                       // a frame that landed during the wait wins this present
            return true;
        }
        void Present()
        {
            presentsThisTurn++;
            double c = cost(presentIndex++, firstAfterCatchUp);
            firstAfterCatchUp = false;
            slotFreeAt = Math.Ceiling(display.Now + c) + RetireOffset;   // retires after the first vblank past completion
            creditHeld = false;                                          // the Present spent the credit
            lastGpuMs = c * RefreshMs;
        }
        var rt = new RenderThread(seam, _ => Present(), async: false,
            needsTick: () => true, tick: Present, tickPeriod: () => PeriodQpc, displayClock: display,
            takePresentSlot: TakeSlot,
            // The catch-up's frame cost is render work + GPU (wake lag is not cost). Here the real work is microseconds,
            // so the scripted cost reaches the policy as the frame's GPU execution — as on device, where the GPU dominates
            // a scroll frame. Without it every scripted frame would read as free and the
            // over-budget case would skip.
            paceHost: () => new RenderPaceHostState(0, false, default, lastGpuMs, 1));
        var turns = new Turn[ticks];
        var sw = Stopwatch.StartNew();
        try
        {
            for (int k = 0; k < ticks; k++)
            {
                if (display.Now < k) display.Now = k;        // tick k+1 arrives at instant k (or is already pending)
                long tick = display.TickSeq;
                long skipsBefore = rt.CatchUpSkips;
                presentsThisTurn = 0;
                turnTakeAt = turnSlotWait = double.NaN;
                rt.DrainSync();
                bool caughtUp = rt.CatchUpSkips != skipsBefore;
                if (caughtUp) firstAfterCatchUp = true;
                turns[k] = new Turn(tick, presentsThisTurn > 0, caughtUp, turnSlotWait);
            }
            return new Outcome(ticks, rt.FreshPresents, rt.MotionPresents, rt.CatchUpSkips, rt.MissedMotionTicks, turns,
                               sw.Elapsed.TotalMilliseconds);
        }
        finally { rt.Dispose(); display.Dispose(); }
    }

    [Theory]
    [InlineData(0.5)]
    [InlineData(0.9)]
    [InlineData(1.1)]
    [InlineData(1.5)]
    public void AGpuThatKeepsUpPresentsOnEveryTick_WhateverTheUiLatency(double publishDelay)
    {
        var o = Run((_, _) => 0.8, publishDelay);
        Assert.True(o.PresentsPerTick >= 0.99, $"presents/tick={o.PresentsPerTick:0.000} fresh={o.Fresh} motion={o.Motion}");
        Assert.Equal(0, o.CatchUpSkips);                    // every frame makes its vblank: the slot is never busy past the grace
    }

    [Theory]
    [InlineData(0.5)]
    [InlineData(0.9)]
    [InlineData(1.1)]
    [InlineData(1.5)]
    public void AFrameCostingSlightlyMoreThanARefreshLosesOnlyThatFraction_NeverHalfTheRate(double publishDelay)
    {
        var o = Run((_, _) => 1.05, publishDelay);
        // Every turn finds the slot held by the previous frame and waits for it; the present still belongs to the tick the
        // turn woke for, so the next turn finds the NEXT tick delivered and presents at once. The defect (re-charging the
        // present to the tick the wait crossed) made every following turn find its tick spent and skip it: 0.5. What
        // reaches the glass per vblank beyond that is the queue's business (the depth policy), not the loop's.
        Assert.True(o.PresentsPerTick >= 0.93, $"presents/tick={o.PresentsPerTick:0.000} fresh={o.Fresh} motion={o.Motion}");
    }

    [Theory]
    [InlineData(1.1)]
    [InlineData(1.5)]
    public void WhileTheUiIsLate_MotionIsPresentedOnEveryTick_WithoutWaitingForTheUiFrame(double publishDelay)
    {
        var o = Run((_, _) => 0.5, publishDelay, ticks: 60);
        Assert.Equal(60, o.TurnsWithAPresent);              // every tick presented something (fresh or a motion re-pose)
        Assert.True(o.Motion > 0, "a tick whose fresh frame has not landed still re-poses motion");
        // No turn blocked for the UI's in-flight frame. The old handshake waited up to a refresh of REAL time on every
        // tick whose frame was late (60 ticks ⇒ ~1 s at the 60 Hz fallback period); a presenter that never waits
        // finishes 60 force-sync turns in milliseconds (the slot takes here are virtual).
        Assert.True(o.RealMs < 400, $"the render thread waited for the UI: {o.RealMs:0} ms for 60 turns");
    }

    // One frame misses its vblank among cheap ones (0.3 refresh = 2.5 ms at 120 Hz). It retires only after the NEXT
    // vblank, so the turn after it finds the slot busy past the grace: it skips (the late frame owns that vblank) and the
    // turn after that finds the slot free 0.02 after its tick — the early phase again. On the pre-catch-up loop (an
    // unbounded take every turn) turn late+1 waited 1.02 refreshes and presented after the vblank of the next tick, and
    // from then on every turn found the slot held by its predecessor for a whole refresh: SlotWait ≈ 1 on every later
    // turn to the end of the run and no skip — the 09-29 "waited runs of 55–86 turns".
    [Fact]
    public void ALateFrameCostsOneVblank_ThenTheLoopIsBackInTheEarlyPhase()
    {
        const int late = 60;
        var o = Run((n, _) => n == late ? 1.1 : 0.3, publishDelay: 0.5);
        Assert.Equal(1, o.CatchUpSkips);
        int skip = Array.FindIndex(o.Turns, t => t.CaughtUp);
        Assert.Equal(late + 1, skip);                        // the turn right after the late present (turn k presents present k)
        Assert.False(o.Turns[skip].Presented);               // nothing was queued behind the late frame
        Assert.Equal(1, o.Missed);                           // exactly the one vblank the late frame owned, charged once
        for (int k = 0; k < o.Ticks; k++)
        {
            if (k == skip) continue;
            Assert.True(o.Turns[k].Presented, $"turn {k} (tick {o.Turns[k].Tick}) presented nothing");
            Assert.True(o.Turns[k].SlotWait <= Grace,
                $"turn {k} waited {o.Turns[k].SlotWait:0.00} refreshes for its slot (grace {Grace:0.00}) — the loop is in the late phase");
        }
    }

    // Every frame costs slightly more than a refresh: the slot is busy past the grace on EVERY turn, so an unconditional
    // skip would take every other tick — the half-rate cliff reopened. The frames do not fit the early phase (their cost
    // EMA is over FitFraction of the refresh), so the policy never skips and every turn presents.
    [Theory]
    [InlineData(0.5)]
    [InlineData(1.5)]
    public void ChronicallyOverBudgetFrames_NeverCatchUp_AndKeepFullRate(double publishDelay)
    {
        var o = Run((_, _) => 1.05, publishDelay);
        Assert.Equal(0, o.CatchUpSkips);
        Assert.True(o.PresentsPerTick >= 0.99, $"presents/tick={o.PresentsPerTick:0.000} fresh={o.Fresh} motion={o.Motion}");
    }

    // Cheap frames (0.1 refresh) with one late frame at present 20 — and every present that follows a catch-up is late
    // too, so the early phase a skip buys never holds. Without the backoff the loop would skip every other tick (each
    // catch-up is followed by a busy slot two ticks later): the half-rate cliff by another road. With it: one skip, a busy
    // slot two ticks later that is NOT skipped (backoff for BackoffTicks; the loop waits as before), then one more try
    // once the window is over — at most one skip per backoff window.
    [Fact]
    public void ACatchUpThatDoesNotHold_BacksOff()
    {
        var o = Run((n, firstAfterCatchUp) => firstAfterCatchUp || n == 20 ? 1.1 : 0.1, publishDelay: 0.5,
                    ticks: 3 * SlotCatchUp.BackoffTicks);
        var skipTicks = new List<long>();
        foreach (var t in o.Turns) if (t.CaughtUp) skipTicks.Add(t.Tick);
        string at = string.Join(",", skipTicks);
        Assert.Equal(o.CatchUpSkips, skipTicks.Count);
        Assert.True(skipTicks.Count >= 2, $"the policy must try again once the backoff window is over (skips at ticks {at})");
        for (int i = 1; i < skipTicks.Count; i++)
            Assert.True(skipTicks[i] - skipTicks[i - 1] >= SlotCatchUp.BackoffTicks,
                $"two catch-ups inside one backoff window (skips at ticks {at})");
        Assert.True(o.PresentsPerTick >= 0.98, $"presents/tick={o.PresentsPerTick:0.000} (skips at ticks {at})");
    }
}
