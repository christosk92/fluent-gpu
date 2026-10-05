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

    // F094 / F097: pacing is per target. A detached pop-out's render motion keeps the loop at display rate and its presents run
    // in extraDrain on ITS swapchain; it must not take the PRIMARY swapchain's present credit, count a motion present, nor mark
    // the parent's tick spent (which deferred every parent frame landing later in the tick by a whole vblank).
    [Fact]
    public void ChildOnlyMotion_TakesNoPrimarySlot_NeverMarksTheParentTick_AndStillDrainsTheChildren()
    {
        ThreadGuard.BindCurrent(ThreadGuard.ThreadRole.Ui);
        var seam = new SceneFramePublisher();
        var display = new VirtualDisplay { Now = 10 };      // one tick, held for the whole test
        byte[] one = [1];
        int takes = 0, lastTimeout = int.MinValue, ticks = 0, drains = 0, submits = 0;
        var rt = new RenderThread(seam, _ => submits++, async: false,
            needsTick: () => true, ownMotion: () => false,   // motion is live, but it is the child's
            tick: () => ticks++, tickPeriod: () => PeriodQpc, displayClock: display,
            takePresentSlot: timeoutMs => { takes++; lastTimeout = timeoutMs; return true; },
            extraDrain: () => drains++);
        try
        {
            for (int i = 0; i < 8; i++) rt.DrainSync();
            Assert.Equal(0, takes);                          // no primary credit for a turn that presents nothing for the parent
            Assert.Equal(0, ticks);                          // RenderMotion never ran: the parent has no motion to present
            Assert.Equal(0, rt.MotionPresents);
            Assert.Equal(0, rt.FreshPresents);
            Assert.Equal(0, rt.SkippedTicks);
            Assert.True(drains >= 8, $"children must drain on every turn, motion-only or not (drains={drains})");

            // A parent publication landing in the SAME tick presents at once: the child's turns did not spend the tick (the old
            // loop skipped it here — SkippedTicks 1, RaceHits 1 — and presented it a whole vblank later).
            seam.Publish(one, default, default);
            rt.DrainSync();
            Assert.Equal(1, submits);
            Assert.Equal(1, rt.FreshPresents);
            Assert.Equal(0, rt.SkippedTicks);
            Assert.Equal(0, rt.RaceHits);
            Assert.Equal(1, takes);
            // the parent's turn is not clock-paced by the child's motion: the liveness-bounded take (negative), no grace, bounded at
            // max(2 x refresh, 34 ms) rather than the backend's 1 s default (F208)
            Assert.Equal(-RenderThread.UnpacedSlotBoundMs(PeriodQpc * 1000.0 / Stopwatch.Frequency), lastTimeout);
        }
        finally { rt.Dispose(); display.Dispose(); }
    }

    [Fact]
    public void TheParentsOwnMotion_StillPresentsEveryTick_WhileAChildAlsoMoves()
    {
        ThreadGuard.BindCurrent(ThreadGuard.ThreadRole.Ui);
        var seam = new SceneFramePublisher();
        var display = new VirtualDisplay();
        int takes = 0, ticks = 0, drains = 0;
        var rt = new RenderThread(seam, _ => { }, async: false,
            needsTick: () => true, ownMotion: () => true,
            tick: () => ticks++, tickPeriod: () => PeriodQpc, displayClock: display,
            takePresentSlot: _ => { takes++; return true; },
            extraDrain: () => drains++);
        try
        {
            for (int k = 0; k < 6; k++)
            {
                display.Now = k;                             // a new tick per turn
                rt.DrainSync();
            }
            Assert.Equal(6, ticks);                          // one motion re-present per tick
            Assert.Equal(6, takes);                          // each took the primary credit
            Assert.Equal(6, rt.MotionPresents);
            Assert.True(drains >= 6);
        }
        finally { rt.Dispose(); display.Dispose(); }
    }

    // The child= section of [render.pace] is the host's callback pair: the window opens once with the line's, and the report
    // is appended to the line (the line itself is a Diag.Line the test cannot read, so what is checked is that both ends run
    // on the render thread while child motion is live).
    [Fact]
    public void ChildPaceWindow_OpensAndReports_WhileChildMotionIsLive()
    {
        ThreadGuard.BindCurrent(ThreadGuard.ThreadRole.Ui);
        var seam = new SceneFramePublisher();
        var display = new VirtualDisplay();
        int begins = 0, reports = 0;
        var rt = new RenderThread(seam, _ => { }, async: false,
            needsTick: () => true, ownMotion: () => false,
            tick: () => { }, tickPeriod: () => PeriodQpc, displayClock: display,
            childPaceBegin: () => begins++, childPaceReport: () => { reports++; return null; });
        try
        {
            rt.DrainSync();                                  // the first turn opens the window
            Assert.True(begins >= 1, $"the pace window must open with child motion live (begins={begins})");
            Assert.True(reports <= begins, "a report only follows an opened window");
        }
        finally { rt.Dispose(); display.Dispose(); }
    }

    // F244: the worst present's work is split into the host's phases and the blocking one is named. The host's stamps arrive through
    // the presentSplit callback (sampled right after the turn that set a new worst); the 1 Hz line prints them next to wake/slot/work.
    [Fact]
    public void WorstPresentSplit_IsPrintedOnThePaceLine_WithTheBlockingPhaseNamed()
    {
        ThreadGuard.BindCurrent(ThreadGuard.ThreadRole.Ui);
        var lines = new List<string>();
        var previousSink = FluentGpu.Foundation.Diag.Sink;
        FluentGpu.Foundation.Diag.Sink = l => { lock (lines) lines.Add(l); };
        var seam = new SceneFramePublisher();
        var display = new VirtualDisplay();
        var split = new PresentSplit(StageMs: 0.1, RecordMs: 0.2, SubmitMs: 0.3, FenceMs: 0, LatencyMs: 0, PresentMs: 91.0, VideoMs: 0.4);
        var rt = new RenderThread(seam, _ => { }, async: false,
            needsTick: () => true, ownMotion: () => true,
            tick: () => { }, tickPeriod: () => PeriodQpc, displayClock: display,
            takePresentSlot: _ => true, presentSplit: () => split);
        try
        {
            display.Now = 0;
            rt.DrainSync();                                  // the first turn opens the 1 s pace window
            display.Now = 1;
            rt.DrainSync();                                  // a motion present inside the window: its split becomes the worst
            Thread.Sleep(1100);
            display.Now = 2;
            rt.DrainSync();                                  // a turn past the window closes it: the line is written
            string? pace;
            lock (lines) pace = lines.Find(l => l.StartsWith("[render.pace]", StringComparison.Ordinal));
            Assert.NotNull(pace);
            Assert.Contains("stage=0.10 rec=0.20 sub=0.30 fence=0.00 lat=0.00 pres=91.00 video=0.40 other=", pace);
            Assert.Contains("blocker=present", pace);
        }
        finally
        {
            rt.Dispose(); display.Dispose();
            FluentGpu.Foundation.Diag.Sink = previousSink;
        }
    }

    // F235: the pace line says WHOSE slot wait timed out and how long a secondary window made the shared thread wait: timeoutTarget=
    // (primary | child | both | none) from the two timeout deltas, and childWaitMax= (the pop-out's longest blocking latency wait,
    // the figure slotWaitMax= never includes).
    [Fact]
    public void ChildSlotWaits_AreAttributedOnThePaceLine_WithTimeoutTargetAndChildWaitMax()
    {
        ThreadGuard.BindCurrent(ThreadGuard.ThreadRole.Ui);
        var lines = new List<string>();
        var previousSink = FluentGpu.Foundation.Diag.Sink;
        FluentGpu.Foundation.Diag.Sink = l => { lock (lines) lines.Add(l); };
        var seam = new SceneFramePublisher();
        var display = new VirtualDisplay();
        bool childStalled = false;
        var rt = new RenderThread(seam, _ => { }, async: false,
            needsTick: () => true, ownMotion: () => true,
            tick: () => { }, tickPeriod: () => PeriodQpc, displayClock: display,
            takePresentSlot: _ => true,
            paceHost: () => new RenderPaceHostState(0, false, default, 1.0, 1,
                SlotLivenessTimeouts: 0, NonPrimaryLatencyTimeouts: childStalled ? 2 : 0, NonPrimaryLatencyWaitMaxMs: childStalled ? 12.5 : 0.0));
        try
        {
            display.Now = 0;
            rt.DrainSync();                                  // the first turn opens the 1 s pace window (child counters at their base)
            display.Now = 1;
            rt.DrainSync();
            childStalled = true;                             // a pop-out's blocking latency wait ran out its bound twice inside the window
            Thread.Sleep(1100);
            display.Now = 2;
            rt.DrainSync();                                  // a turn past the window closes it: the line is written
            string? pace;
            lock (lines) pace = lines.Find(l => l.StartsWith("[render.pace]", StringComparison.Ordinal));
            Assert.NotNull(pace);
            Assert.Contains("slotTimeouts=0 timeoutTarget=child childWaitMax=12.50 childTimeouts=2 ", pace);
        }
        finally
        {
            rt.Dispose(); display.Dispose();
            FluentGpu.Foundation.Diag.Sink = previousSink;
        }
    }

    [Theory]
    [InlineData(0, 0, "none")]
    [InlineData(4, 0, "primary")]
    [InlineData(0, 1, "child")]
    [InlineData(2, 3, "both")]
    public void PaceTimeoutTarget_NamesWhichSwapchainsSlotTimedOut(long primary, long child, string expected)
        => Assert.Equal(expected, RenderThread.PaceTimeoutTarget(primary, child));

    // F215: the detached children's present drain runs after the primary's present, so no worst present's `work` contains it. Its
    // own wall time per turn (avg / max / count in the window) is printed on the pace line, so a stall inside a pop-out's submit,
    // fence wait or present is attributed to the child drain instead of looking like unexplained parent work.
    [Fact]
    public void ChildDrainTime_IsPrintedOnThePaceLine()
    {
        ThreadGuard.BindCurrent(ThreadGuard.ThreadRole.Ui);
        var lines = new List<string>();
        var previousSink = FluentGpu.Foundation.Diag.Sink;
        FluentGpu.Foundation.Diag.Sink = l => { lock (lines) lines.Add(l); };
        var seam = new SceneFramePublisher();
        var display = new VirtualDisplay();
        var rt = new RenderThread(seam, _ => { }, async: false,
            needsTick: () => true, ownMotion: () => true,
            tick: () => { }, tickPeriod: () => PeriodQpc, displayClock: display,
            takePresentSlot: _ => true, extraDrain: () => Thread.Sleep(30));
        try
        {
            display.Now = 0;
            rt.DrainSync();                                  // the first turn opens the 1 s pace window
            display.Now = 1;
            rt.DrainSync();                                  // a turn inside the window: its child drain is counted
            Thread.Sleep(1100);
            display.Now = 2;
            rt.DrainSync();                                  // a turn past the window closes it: the line is written
            string? pace;
            lock (lines) pace = lines.Find(l => l.StartsWith("[render.pace]", StringComparison.Ordinal));
            Assert.NotNull(pace);
            int at = pace.IndexOf("childDrain(avg=", StringComparison.Ordinal);
            Assert.True(at >= 0, pace);
            int maxAt = pace.IndexOf("max=", at, StringComparison.Ordinal);
            string maxText = pace[(maxAt + 4)..pace.IndexOf(' ', maxAt)];
            Assert.True(double.Parse(maxText, System.Globalization.CultureInfo.InvariantCulture) >= 25.0, pace);
        }
        finally
        {
            rt.Dispose(); display.Dispose();
            FluentGpu.Foundation.Diag.Sink = previousSink;
        }
    }

    // F098: a UI wake that was only its video pump parks the registry's snapshot (a video-only post) and wakes the loop; the loop's
    // preTurn applies it. That turn records nothing, takes no present slot and presents nothing, and the post is not a publication
    // (the publish sequence does not move, no frame becomes pending).
    [Fact]
    public void AVideoOnlyPost_IsAppliedInPreTurn_WithoutARecordASlotTakeOrAPublication()
    {
        ThreadGuard.BindCurrent(ThreadGuard.ThreadRole.Ui);
        var seam = new SceneFramePublisher();
        var reg = new FluentGpu.Media.VideoSurfaceRegistry();
        var scratch = new FluentGpu.Media.VideoPresentIntent[FluentGpu.Media.VideoSurfaceRegistry.MaxSurfaces];
        byte[] one = [1];
        int submits = 0, takes = 0, applied = 0, appliedIntents = 0;
        var rt = new RenderThread(seam, _ => Interlocked.Increment(ref submits), async: false,
            takePresentSlot: _ => { Interlocked.Increment(ref takes); return true; },
            preTurn: () =>
            {
                if (!seam.TryTakeVideoOnly(scratch, out int n)) return;
                Volatile.Write(ref appliedIntents, n);
                Interlocked.Increment(ref applied);
            });
        try
        {
            int token = reg.Acquire();
            reg.Place(token, new FluentGpu.Foundation.RectF(0f, 0f, 10f, 10f));
            seam.Publish(one, default, default, video: reg);
            rt.DrainSync();                                  // the baseline frame: one slot take, one submit
            Assert.Equal(1, Volatile.Read(ref submits));
            Assert.Equal(1, Volatile.Read(ref takes));
            ulong seq = seam.PublishSeq;

            reg.Place(token, new FluentGpu.Foundation.RectF(5f, 5f, 10f, 10f));
            Assert.True(seam.TryPostVideoOnly(reg));
            Assert.True(seam.HasVideoOnlyPost);
            Assert.False(seam.HasPendingFrame);              // a post is not a publication
            Assert.Equal(seq, seam.PublishSeq);
            rt.DrainSync();                                  // the wake: one turn, applied by the preTurn hook

            Assert.Equal(1, Volatile.Read(ref applied));
            Assert.Equal(1, Volatile.Read(ref appliedIntents));
            Assert.False(seam.HasVideoOnlyPost);             // exactly once
            Assert.Equal(1, Volatile.Read(ref submits));     // nothing recorded or presented
            Assert.Equal(1, Volatile.Read(ref takes));       // no present slot reserved for it
            Assert.Equal(seq, seam.PublishSeq);
        }
        finally { rt.Dispose(); }
    }

    // F098: the post may never reorder against a full publication. It is refused before the first publication and while one is
    // outstanding (its snapshot is the older state), and a full publication carrying the registry's snapshot supersedes a parked one.
    [Fact]
    public void AVideoOnlyPost_IsRefusedAroundAnOutstandingPublication_AndSupersededByTheNextOne()
    {
        ThreadGuard.BindCurrent(ThreadGuard.ThreadRole.Ui);
        var seam = new SceneFramePublisher();
        var reg = new FluentGpu.Media.VideoSurfaceRegistry();
        var scratch = new FluentGpu.Media.VideoPresentIntent[FluentGpu.Media.VideoSurfaceRegistry.MaxSurfaces];
        byte[] one = [1];
        int token = reg.Acquire();
        reg.Place(token, new FluentGpu.Foundation.RectF(0f, 0f, 10f, 10f));

        Assert.False(seam.TryPostVideoOnly(reg));            // nothing published yet: the first frame carries the table
        seam.Publish(one, default, default, video: reg);
        Assert.False(seam.TryPostVideoOnly(reg));            // that frame is outstanding: it would be applied around the post
        Assert.True(seam.TryAcquire(out _));                 // the renderer adopts it

        reg.Place(token, new FluentGpu.Foundation.RectF(5f, 5f, 10f, 10f));
        Assert.True(reg.HasUnpublishedChanges);
        Assert.True(seam.TryPostVideoOnly(reg));
        Assert.False(reg.HasUnpublishedChanges);             // the post carried the change
        Assert.Equal(1L, seam.VideoOnlyPosts);

        reg.Place(token, new FluentGpu.Foundation.RectF(9f, 9f, 10f, 10f));
        seam.Publish(one, default, default, video: reg);     // a full publication: a newer snapshot than the parked post
        Assert.False(seam.HasVideoOnlyPost);
        Assert.False(seam.TryTakeVideoOnly(scratch, out _));
        Assert.False(seam.TryPostVideoOnly(reg));            // and it is outstanding in turn
    }

    // F098: the parked snapshot is the registry's, intent for intent, stamped with the snapshot sequence the applier orders
    // geometry by, and taking it empties the mailbox.
    [Fact]
    public void ATakenVideoOnlyPost_CarriesTheSnapshot_AndEmptiesTheMailbox()
    {
        ThreadGuard.BindCurrent(ThreadGuard.ThreadRole.Ui);
        var seam = new SceneFramePublisher();
        var reg = new FluentGpu.Media.VideoSurfaceRegistry();
        var scratch = new FluentGpu.Media.VideoPresentIntent[FluentGpu.Media.VideoSurfaceRegistry.MaxSurfaces];
        byte[] one = [1];
        int token = reg.Acquire();
        reg.SetContentSize(token, 1920, 1080);
        seam.Publish(one, default, default, video: reg);
        Assert.True(seam.TryAcquire(out _));

        reg.SetContentSize(token, 1280, 720);
        Assert.True(seam.TryPostVideoOnly(reg));
        Assert.True(seam.TryTakeVideoOnly(scratch, out int count));
        Assert.Equal(1, count);
        Assert.Equal(token, scratch[0].Token);
        Assert.Equal(1280u, scratch[0].ContentW);
        Assert.Equal(720u, scratch[0].ContentH);
        Assert.True(scratch[0].HasGeometry);
        Assert.True(scratch[0].Seq > 1L);                    // newer than the publication's snapshot
        Assert.False(seam.TryTakeVideoOnly(scratch, out _));
    }
}

/// <summary>F244: <see cref="PresentSplit"/> names the call a present turn blocked in.</summary>
public sealed class PresentSplitTests
{
    [Fact]
    public void TheLargestPhaseIsTheBlocker_AndTheUncoveredRemainderIsOther()
    {
        var s = new PresentSplit(0.5, 1.0, 2.0, 0.0, 0.0, 88.0, 0.5);
        string line = s.Format(slotMs: 0.2, workMs: 100.0);
        Assert.Contains("pres=88.00", line);
        Assert.Contains("other=8.00", line);          // 100 - (0.5 + 1 + 2 + 88 + 0.5)
        Assert.Contains("blocker=present", line);
        Assert.Equal(92.0, s.TotalMs, 6);
    }

    [Fact]
    public void ASlotTakeThatDominates_IsNamedSlot_AndAFenceWaitIsNamedFence()
    {
        Assert.Contains("blocker=slot", new PresentSplit(0, 1, 1, 0, 0, 1, 0).Format(slotMs: 50, workMs: 3));
        Assert.Contains("blocker=fence", new PresentSplit(0, 1, 1, 40, 0, 1, 0).Format(slotMs: 2, workMs: 43));
        Assert.Contains("blocker=latency", new PresentSplit(0, 1, 1, 0, 70, 1, 0).Format(slotMs: 2, workMs: 73));
        Assert.Contains("blocker=video", new PresentSplit(0, 1, 1, 0, 0, 1, 30).Format(slotMs: 2, workMs: 33));
    }

    [Fact]
    public void WorkThePhasesDoNotCover_IsOther_AndNothingMeasuredIsNone()
    {
        Assert.Contains("blocker=other", new PresentSplit(0, 1, 1, 0, 0, 1, 0).Format(slotMs: 0.1, workMs: 120));   // e.g. a preempted thread
        Assert.Contains("blocker=none", default(PresentSplit).Format(slotMs: 0, workMs: 0));
    }
}
