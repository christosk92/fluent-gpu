using System;
using System.Diagnostics;
using System.Threading;
using FluentGpu.Hosting;
using FluentGpu.Hosting.Threading;
using FluentGpu.Pal;
using Xunit;

namespace FluentGpu.Engine.Tests;

/// <summary>
/// Focused coverage for the render-thread seam's shutdown + detached-window routing (the async-render-flip work). The
/// full AppHost render thread is NOT constructible headlessly — AppHost gates the spawn on a non-headless window kind
/// (AppHost.cs), so a HeadlessWindow is always RenderLoopMode.SingleThread and never spawns one (neither ForceSync nor Async).
/// These tests therefore exercise the seam primitives directly (RenderThread + SceneFramePublisher), which is exactly
/// the machinery Change 1 (deterministic stop+join on close) and Change 2 (parent-thread child drain) are built on.
/// Live verification of an actual pop-out under async is separate (needs a real GPU/DRM runtime).
/// </summary>
// Serial: these tests time real render-thread turns and run hosts with process-static seams; beside the parallel suite they
// flaked under load.
[Collection(SerialTestCollection.Name)]
public sealed class RenderThreadLifecycleTests
{
    // Change 1 (shutdown) + Change 2 (child drain via extraDrain) + the TryAcquire dedup the child-wake routing needs.
    [Fact]
    public void ForceSync_DrainSync_SubmitsOncePerPublish_AlwaysRunsExtraDrain_DisposeJoins_AndPostDisposeIsSafe()
    {
        ThreadGuard.BindCurrent(ThreadGuard.ThreadRole.Ui);
        var seam = new SceneFramePublisher();
        int submits = 0, drains = 0;
        // async:false = force-sync, so DrainSync blocks until the render thread's turn completes (deterministic, no sleeps).
        // submitPresent + extraDrain both run ON the render thread; DrainSync's _done wait publishes their writes to us.
        var rt = new RenderThread(seam, _ => submits++, async: false, extraDrain: () => drains++);
        try
        {
            // A published frame is submitted exactly once, and the child-drain callback runs on the same turn.
            Span<byte> one = stackalloc byte[] { 1 };
            seam.Publish(one, default, default);
            rt.DrainSync();
            Assert.Equal(1, submits);
            Assert.True(drains >= 1, $"extraDrain should run on the turn (drains={drains})");

            // A bare wake with NO new publish must NOT re-submit the last frame (TryAcquire dedup — the invariant that lets
            // a detached child wake the parent thread without re-presenting the parent's stale frame), yet extraDrain STILL
            // runs (a child publish rides its own seam, drained by extraDrain even when the parent seam has nothing new).
            int submitsBefore = submits, drainsBefore = drains;
            rt.DrainSync();
            Assert.Equal(submitsBefore, submits);
            Assert.True(drains > drainsBefore, "extraDrain must run every turn, even with no new parent publish");

            // A fresh publish submits again (dedup only suppresses the ALREADY-consumed seq).
            seam.Publish(one, default, default);
            rt.DrainSync();
            Assert.Equal(submitsBefore + 1, submits);
        }
        finally
        {
            // Change 1: Dispose stops + JOINS the render thread deterministically (bounded), and is idempotent.
            rt.Dispose();
            rt.Dispose();
            // Teardown-race safety: a still-armed wake / drain after the thread joined is a no-op, never an
            // ObjectDisposedException (a detached child's last publish can land after the parent thread was disposed).
            rt.WakeAsync();
            rt.DrainSync();
            rt.Quiesce();
            rt.Resume();
        }
    }

    // F102: fgpu-render is the deadline thread of the whole process (record, composite, submit, present for every window), so
    // it runs above the Normal-priority decode workers and ThreadPool continuations it shares the cores with. The submit
    // callback runs ON the render thread, so it reads the thread's own priority.
    [Fact]
    public void RenderThread_RunsAtAboveNormalPriority_ForItsLifetime()
    {
        ThreadGuard.BindCurrent(ThreadGuard.ThreadRole.Ui);
        var seam = new SceneFramePublisher();
        ThreadPriority seen = ThreadPriority.Lowest;
        string? name = null;
        var rt = new RenderThread(seam, _ => { seen = Thread.CurrentThread.Priority; name = Thread.CurrentThread.Name; }, async: false);
        try
        {
            Span<byte> one = stackalloc byte[] { 1 };
            seam.Publish(one, default, default);
            rt.DrainSync();   // force-sync: the turn (and its writes) complete before DrainSync returns
            Assert.Equal("fgpu-render", name);
            Assert.Equal(ThreadPriority.AboveNormal, seen);
        }
        finally { rt.Dispose(); }
    }

    // The present-slot take (IGpuDevice.TryTakePresentSlot) must be paid BEFORE the frame is chosen — the whole point
    // of the latency fix: the presented state is then the freshest one that existed when the slot opened, instead of one
    // aged by the wait (the historical order waited inside submit, AFTER the acquire, which on a GPU costing most of a
    // refresh meant every frame carried 5-8 ms of stale input). It must also NOT be paid on a turn that presents nothing:
    // the waitable is a SEMAPHORE, so an unspent reservation would stall the next present by a whole present cycle.
    [Fact]
    public void PresentSlotWait_IsPaidBeforeTheFrameIsChosen_AndOnlyWhenOneIsPending()
    {
        ThreadGuard.BindCurrent(ThreadGuard.ThreadRole.Ui);
        var seam = new SceneFramePublisher();
        int waits = 0, submits = 0;
        // Observed INSIDE the wait: the seam's last-consumed seq. This is the ordering proof, without racing a publish
        // onto the render thread (Publish is UI-asserted, and force-sync has the UI blocked in DrainSync anyway). If the
        // wait precedes TryAcquire this still reads the PREVIOUS seq; had it run after, it would already read this frame's.
        ulong consumedSeenInWait = ulong.MaxValue;
        var rt = new RenderThread(seam, _ => submits++, async: false,
                                  takePresentSlot: _ => { waits++; consumedSeenInWait = seam.LastConsumedSeq; return true; });
        try
        {
            Span<byte> one = stackalloc byte[] { 1 };
            seam.Publish(one, default, default);            // seq 1 pending
            rt.DrainSync();
            Assert.Equal(1, submits);
            Assert.Equal(1, waits);
            Assert.Equal(0UL, consumedSeenInWait);          // nothing consumed yet ⇒ the wait preceded the acquire

            // A bare wake with no publish presents nothing, so no slot may be reserved.
            rt.DrainSync();
            Assert.Equal(1, submits);
            Assert.Equal(1, waits);

            seam.Publish(one, default, default);            // seq 2
            rt.DrainSync();
            Assert.Equal(2, submits);
            Assert.Equal(2, waits);
            Assert.Equal(1UL, consumedSeenInWait);          // seq 1 consumed, seq 2 not yet ⇒ again before the acquire
        }
        finally { rt.Dispose(); }
    }

    // Item C, §1: a motion-tick turn ALSO spends a present, so it must ALSO take the present-slot credit — every
    // present holds one, no exceptions. Before the fix, the wait was gated on HasPendingFrame alone, so a pure
    // motion-due turn (no publication pending) never paid it at all.
    [Fact]
    public void MotionTick_TakesThePresentSlotCredit()
    {
        ThreadGuard.BindCurrent(ThreadGuard.ThreadRole.Ui);
        var seam = new SceneFramePublisher();
        int waits = 0, ticks = 0;
        var rt = new RenderThread(seam, _ => { }, async: false,
                                  needsTick: () => true, tick: () => ticks++,
                                  takePresentSlot: _ => { waits++; return true; });
        try
        {
            // No publication at all — only motion is due — yet the slot wait still runs, and the tick still fires.
            rt.DrainSync();
            Assert.Equal(1, waits);
            Assert.Equal(1, ticks);
        }
        finally { rt.Dispose(); }
    }

    // Item C, §1: TryAcquire is tried FIRST even on a motion-due turn — a publication that lands DURING the present-
    // slot wait wins the credit just taken over the stale motion re-present, so the freshest state reaches the glass
    // instead of the tick's retained frame.
    [Fact]
    public void PublicationArrivingDuringTheSlotWait_WinsOverTheMotionTick()
    {
        ThreadGuard.BindCurrent(ThreadGuard.ThreadRole.Ui);
        // reverse: true so Publish asserts the RENDER role, not UI — takePresentSlot runs ON the render thread (that is
        // the whole point: simulating a publication that lands DURING its wait, before this same thread's TryAcquire),
        // and SceneFramePublisher.Publish's ThreadGuard assertion would otherwise reject the "wrong" caller thread.
        // The reverse flag changes nothing else this test exercises (TryAcquire/dedup are role-agnostic).
        var seam = new SceneFramePublisher(reverse: true);
        int submits = 0, ticks = 0;
        byte[] one = [1];
        var rt = new RenderThread(seam, _ => submits++, async: false,
                                  needsTick: () => true, tick: () => ticks++,
                                  // Simulates a publish landing while this thread is blocked in the present-slot wait.
                                  takePresentSlot: _ => { seam.Publish(one, default, default); return true; });
        try
        {
            rt.DrainSync();
            Assert.Equal(1, submits);   // TryAcquire adopted the publication that arrived during the wait...
            Assert.Equal(0, ticks);     // ...so the motion tick never ran for this turn.
        }
        finally { rt.Dispose(); }
    }

    // ONE present per compositor tick (PresentCadence is the sole presenter, compositor-scroll plan §2.3/§5.1). A fake
    // IRenderDisplayClock stands in for the display clock subscription: the thread reads its TickSeq at the top of the
    // turn, presents at most once per seq while motion is live, and services a NEW seq normally.
    private sealed class FakeDisplayClock : IRenderDisplayClock
    {
        public long Seq;
        private readonly AutoResetEvent _tick = new(false);
        public WaitHandle Tick => _tick;
        public bool IsAvailable => true;
        public void SetActive(bool active) { }
        public long TickSeq => Seq;
        public void Dispose() => _tick.Dispose();
    }

    [Fact]
    public void AtMostOnePresentPerCompositorTick()
    {
        ThreadGuard.BindCurrent(ThreadGuard.ThreadRole.Ui);
        var seam = new SceneFramePublisher();
        var clock = new FakeDisplayClock { Seq = 5 };
        int ticks = 0;
        var rt = new RenderThread(seam, _ => { }, async: false,
                                  needsTick: () => true, tick: () => ticks++,
                                  displayClock: clock);
        try
        {
            rt.DrainSync();
            Assert.Equal(1, ticks);   // first turn for TickSeq=5 services it (a motion re-present)

            rt.DrainSync();
            Assert.Equal(1, ticks);   // a repeat turn for the SAME TickSeq (still the same vblank) does not re-present
            Assert.True(rt.SkippedTicks >= 1, "a turn with motion due on an already-presented tick is a skipped tick");

            clock.Seq = 6;            // the compositor actually advanced to a new vblank
            rt.DrainSync();
            Assert.Equal(2, ticks);   // the new tick is serviced
        }
        finally { rt.Dispose(); }
    }

    // Fresh wins over motion on the same tick: with motion live AND a publication already pending when the tick turn
    // runs, the publication is presented and the motion re-present does not run (the fresh frame carries this tick's
    // compositor state — the recorder samples it).
    [Fact]
    public void FreshPublicationWinsOverMotionOnTheSameTick()
    {
        ThreadGuard.BindCurrent(ThreadGuard.ThreadRole.Ui);
        var seam = new SceneFramePublisher();
        var clock = new FakeDisplayClock { Seq = 5 };
        int submits = 0, ticks = 0;
        var rt = new RenderThread(seam, _ => submits++, async: false,
                                  needsTick: () => true, tick: () => ticks++,
                                  displayClock: clock);
        try
        {
            Span<byte> one = stackalloc byte[] { 1 };
            seam.Publish(one, default, default);
            rt.DrainSync();
            Assert.Equal(1, submits);
            Assert.Equal(0, ticks);
            Assert.Equal(1, rt.FreshPresents);
            Assert.Equal(0, rt.MotionPresents);
        }
        finally { rt.Dispose(); }
    }

    // A publication that lands AFTER this tick's decision waits for the next tick: the motion re-present took tick 5,
    // the late publish is deferred (a skipped tick + the race hit that names it), and it is presented on tick 6 — where
    // it wins over motion. At most one present per tick throughout.
    [Fact]
    public void LatePublicationWaitsForTheNextTick_AndThenWins()
    {
        ThreadGuard.BindCurrent(ThreadGuard.ThreadRole.Ui);
        var seam = new SceneFramePublisher();
        var clock = new FakeDisplayClock { Seq = 5 };
        int submits = 0, ticks = 0;
        var rt = new RenderThread(seam, _ => submits++, async: false,
                                  needsTick: () => true, tick: () => ticks++,
                                  displayClock: clock);
        try
        {
            rt.DrainSync();                                  // tick 5: nothing fresh ⇒ motion re-present
            Assert.Equal(1, ticks);
            Assert.Equal(0, submits);

            Span<byte> one = stackalloc byte[] { 1 };
            seam.Publish(one, default, default);             // lands after tick 5's decision
            rt.DrainSync();
            Assert.Equal(0, submits);                        // not presented on the spent tick
            Assert.Equal(1, ticks);                          // and no second motion present either
            Assert.True(rt.SkippedTicks >= 1);
            Assert.Equal(1, rt.RaceHits);                    // one fresh frame queued behind a stale re-present, charged once

            clock.Seq = 6;
            rt.DrainSync();                                  // tick 6: fresh wins over motion
            Assert.Equal(1, submits);
            Assert.Equal(1, ticks);
            Assert.Equal(1, rt.FreshPresents);
            Assert.Equal(1, rt.MotionPresents);

            rt.DrainSync();                                  // tick 6 again: nothing fresh, tick spent ⇒ nothing
            Assert.Equal(1, submits);
            Assert.Equal(1, ticks);
        }
        finally { rt.Dispose(); }
    }

    // The slot catch-up (SlotCatchUp). A CLOCK-PACED turn takes the present slot with a bounded grace; a slot still busy
    // past it means the previous present missed its vblank and owns this one. While frames fit the early phase the turn
    // presents NOTHING — no credit was taken (nothing to undo) and the tick stays un-presented, so the next present charges
    // it as the one missed tick — and the next tick takes its slot and presents normally. These tests run a 30 Hz tick
    // period (grace 5 ms, fit 23 ms) so real scheduling noise in the measured render work can never read as an over-budget
    // frame; the policy's GPU share comes from the pace host (1 ms — cheap frames).
    private static readonly long CatchUpPeriodQpc = Stopwatch.Frequency / 30;
    private static int CatchUpGraceMs => SlotCatchUp.GraceMs(CatchUpPeriodQpc * 1000.0 / Stopwatch.Frequency);
    private static RenderPaceHostState CheapFrames() => new(0, false, default, 1.0, 1);

    [Fact]
    public void APacedTurnWhoseSlotStaysBusy_PresentsNothing_AndTheNextTickPresents()
    {
        ThreadGuard.BindCurrent(ThreadGuard.ThreadRole.Ui);
        var seam = new SceneFramePublisher();
        var clock = new FakeDisplayClock { Seq = 5 };
        int ticks = 0, takes = 0, lastTimeout = int.MinValue;
        bool busy = false;
        var rt = new RenderThread(seam, _ => { }, async: false,
                                  needsTick: () => true, tick: () => ticks++,
                                  tickPeriod: () => CatchUpPeriodQpc, displayClock: clock,
                                  // A busy slot times out every bounded take; the liveness-bounded take (−1) always proceeds.
                                  takePresentSlot: timeoutMs => { takes++; lastTimeout = timeoutMs; return !(busy && timeoutMs >= 0); },
                                  paceHost: CheapFrames);
        try
        {
            // Seed the policy with cheap paced presents (ticks 5 and 6), each taking its slot at once.
            rt.DrainSync();
            clock.Seq = 6;
            rt.DrainSync();
            Assert.Equal(2, ticks);
            Assert.Equal(2, takes);
            Assert.Equal(CatchUpGraceMs, lastTimeout);       // a paced turn's take is bounded by the grace

            // Tick 7: the slot is still busy past the grace — the previous present missed its vblank.
            busy = true;
            clock.Seq = 7;
            rt.DrainSync();
            Assert.Equal(2, ticks);                          // presented nothing
            Assert.Equal(3, takes);                          // one bounded take; no fallback to the unbounded wait
            Assert.Equal(CatchUpGraceMs, lastTimeout);
            Assert.Equal(1, rt.CatchUpSkips);
            Assert.Equal(0, rt.MissedMotionTicks);           // not charged yet: the next present charges it

            // Tick 8: the late frame retired at the vblank; the slot is free at the tick and the turn presents.
            busy = false;
            clock.Seq = 8;
            rt.DrainSync();
            Assert.Equal(3, ticks);
            Assert.Equal(4, takes);
            Assert.Equal(1, rt.CatchUpSkips);
            Assert.Equal(1, rt.MissedMotionTicks);           // tick 7 — the one vblank the late frame owned
        }
        finally { rt.Dispose(); }
    }

    // A wake that re-runs the turn on a tick the catch-up already gave to the late frame — in production a UI publication
    // landing mid-tick — skips it again without touching the slot or the policy, and does not count it twice. Retaking
    // would either be charged as a catch-up that did not hold (a busy slot "again" within HoldTicks — it is the same tick)
    // and back off for BackoffTicks, or open the slot just after the next vblank and present this tick's frame there: the
    // late phase the skip escaped. The pending publication waits for the next tick, like one on a spent tick; and a later
    // late frame is still caught up (the re-run armed no backoff).
    [Fact]
    public void AWakeOnTheSkippedTick_SkipsItAgain_WithoutRetakingTheSlot()
    {
        ThreadGuard.BindCurrent(ThreadGuard.ThreadRole.Ui);
        var seam = new SceneFramePublisher();
        var clock = new FakeDisplayClock { Seq = 5 };
        int ticks = 0, submits = 0, takes = 0;
        bool busy = false;
        var rt = new RenderThread(seam, _ => submits++, async: false,
                                  needsTick: () => true, tick: () => ticks++,
                                  tickPeriod: () => CatchUpPeriodQpc, displayClock: clock,
                                  takePresentSlot: timeoutMs => { takes++; return !(busy && timeoutMs >= 0); },
                                  paceHost: CheapFrames);
        try
        {
            rt.DrainSync();                                  // tick 5 (seeds the policy)
            clock.Seq = 6;
            rt.DrainSync();                                  // tick 6
            busy = true;
            clock.Seq = 7;
            rt.DrainSync();                                  // tick 7: caught up
            Assert.Equal(1, rt.CatchUpSkips);
            Assert.Equal(3, takes);

            Span<byte> one = stackalloc byte[] { 1 };
            seam.Publish(one, default, default);             // a UI frame lands mid-tick and wakes the loop
            rt.DrainSync();                                  // re-run on tick 7, the slot still busy
            Assert.Equal(3, takes);                          // the slot was not asked for again
            Assert.Equal(2, ticks);
            Assert.Equal(0, submits);                        // the fresh frame waits for the next tick
            Assert.Equal(1, rt.CatchUpSkips);                // one late frame, one skip

            busy = false;
            clock.Seq = 8;
            rt.DrainSync();                                  // tick 8: the fresh frame is presented on time
            Assert.Equal(1, submits);
            Assert.Equal(4, takes);

            busy = true;
            clock.Seq = 8 + SlotCatchUp.HoldTicks + 3;       // a new late frame, well past the hold window
            rt.DrainSync();
            Assert.Equal(2, rt.CatchUpSkips);                // caught up again: the re-run armed no backoff
            Assert.Equal(2, ticks);
            Assert.Equal(1, submits);
        }
        finally { rt.Dispose(); }
    }

    // Before any paced present has been observed nothing says frames fit the early phase: a busy slot keeps the wait —
    // the bounded take times out, the liveness-bounded one (−1) proceeds, and the turn presents for its tick.
    [Fact]
    public void ABusySlotBeforeAnyPacedPresent_KeepsTheWait()
    {
        ThreadGuard.BindCurrent(ThreadGuard.ThreadRole.Ui);
        var seam = new SceneFramePublisher();
        var clock = new FakeDisplayClock { Seq = 5 };
        int ticks = 0;
        var timeouts = new System.Collections.Generic.List<int>();
        var rt = new RenderThread(seam, _ => { }, async: false,
                                  needsTick: () => true, tick: () => ticks++,
                                  tickPeriod: () => CatchUpPeriodQpc, displayClock: clock,
                                  takePresentSlot: timeoutMs => { timeouts.Add(timeoutMs); return timeoutMs < 0; },
                                  paceHost: CheapFrames);
        try
        {
            rt.DrainSync();
            Assert.Equal(new[] { CatchUpGraceMs, -1 }, timeouts.ToArray());
            Assert.Equal(1, ticks);
            Assert.Equal(0, rt.CatchUpSkips);
        }
        finally { rt.Dispose(); }
    }

    // (The production-wait handshake these slots used to test is DELETED: the present decision never waits for the UI's
    // in-flight frame — RenderThreadPacingTests pins that, and LatePublicationWaitsForTheNextTick above pins where a
    // late frame goes instead.)

    // With NO motion live the tick rule does not apply: a publish wake presents immediately, even twice on the same
    // TickSeq (the display clock is not delivering ticks while idle — gating on it would freeze the idle page's click).
    [Fact]
    public void WithoutMotion_PublishWakePresentsImmediatelyRegardlessOfTheTick()
    {
        ThreadGuard.BindCurrent(ThreadGuard.ThreadRole.Ui);
        var seam = new SceneFramePublisher();
        var clock = new FakeDisplayClock { Seq = 5 };
        int submits = 0, ticks = 0;
        var rt = new RenderThread(seam, _ => submits++, async: false,
                                  needsTick: () => false, tick: () => ticks++,
                                  displayClock: clock);
        try
        {
            Span<byte> one = stackalloc byte[] { 1 };
            seam.Publish(one, default, default);
            rt.DrainSync();
            Assert.Equal(1, submits);
            seam.Publish(one, default, default);             // same TickSeq, no motion ⇒ still immediate
            rt.DrainSync();
            Assert.Equal(2, submits);
            Assert.Equal(0, ticks);
            Assert.Equal(0, rt.SkippedTicks);
        }
        finally { rt.Dispose(); }
    }

    // F207: the UI's park rendezvous must not wait out a present-slot wait. The fake take blocks like the real device's 1 s
    // liveness wait - until the slot opens (never, here) OR the park-request event fires - and answers false WITHOUT taking
    // the credit when the park request ended it. Quiesce must then return as soon as the loop reaches its gate, the aborted
    // turn must have presented nothing (nothing to undo, nothing retaken), and after Resume the still-pending publication
    // presents on a fresh take.
    [Fact]
    public void Quiesce_InterruptsABlockedSlotTake_AndTheAbortedTurnPresentsNothing()
    {
        ThreadGuard.BindCurrent(ThreadGuard.ThreadRole.Ui);
        var seam = new SceneFramePublisher();
        int submits = 0, takes = 0;
        using var entered = new ManualResetEventSlim(false);
        RenderThread? rt = null;
        rt = new RenderThread(seam, _ => Interlocked.Increment(ref submits), async: false,
                              takePresentSlot: _ =>
                              {
                                  if (Interlocked.Increment(ref takes) != 1) return true;   // the turn after Resume takes its credit at once
                                  entered.Set();
                                  bool aborted = rt!.ParkRequested.WaitOne(10_000);
                                  return !aborted;   // false = the park request ended the wait and nothing was taken
                              });
        try
        {
            Span<byte> one = stackalloc byte[] { 1 };
            seam.Publish(one, default, default);
            rt.WakeAsync();
            Assert.True(entered.Wait(5_000), "the render thread never reached the blocked slot take");

            var sw = Stopwatch.StartNew();
            rt.Quiesce();
            sw.Stop();
            try
            {
                Assert.True(sw.ElapsedMilliseconds < 1_000, $"Quiesce must not wait out the slot take; took {sw.ElapsedMilliseconds} ms");
                Assert.Equal(0, Volatile.Read(ref submits));   // the aborted turn presented nothing
                Assert.Equal(1, Volatile.Read(ref takes));     // and did not retake the slot behind the UI's back
                Assert.Equal(1L, rt.QuiesceCount);
                Assert.True(rt.QuiesceWaitMsMax > 0.0 && rt.QuiesceWaitMsMax < 1_000.0, $"QuiesceWaitMsMax={rt.QuiesceWaitMsMax}");
            }
            finally { rt.Resume(); }

            Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref submits) == 1, 5_000), "the pending publication never presented after Resume");
            Assert.False(rt.ParkRequested.WaitOne(0), "the gate must consume the park request");
        }
        finally { rt.Dispose(); }
    }

    // The dedup contract in isolation: the consumer is idempotent across bare acquires (no intervening publish).
    [Fact]
    public void SceneFramePublisher_TryAcquire_DedupsAnAlreadyConsumedFrame()
    {
        ThreadGuard.BindCurrent(ThreadGuard.ThreadRole.Ui);
        var seam = new SceneFramePublisher();

        Assert.False(seam.TryAcquire(out _), "nothing published yet");

        Span<byte> one = stackalloc byte[] { 7 };
        seam.Publish(one, default, default);
        Assert.True(seam.TryAcquire(out var f1), "first acquire after publish");
        Assert.Equal(1UL, f1.PublishSeq);

        // No new publish → the latest published frame is the one we already consumed → dedup returns false.
        Assert.False(seam.TryAcquire(out _), "bare re-acquire with no new publish must dedup");

        // A new publish is acquirable again.
        seam.Publish(one, default, default);
        Assert.True(seam.TryAcquire(out var f2));
        Assert.Equal(2UL, f2.PublishSeq);
        Assert.False(seam.TryAcquire(out _), "and dedups again");
    }

    // F208: the host's early (structural) video drain is the loop's preTurn hook. It must run BEFORE the present-slot take (a
    // take may block for a long while) and on a bare wake, which presents nothing and so reaches no slot at all.
    [Fact]
    public void PreTurn_RunsBeforeThePresentSlotTake_AndOnABareWake()
    {
        ThreadGuard.BindCurrent(ThreadGuard.ThreadRole.Ui);
        var seam = new SceneFramePublisher();
        // The loop thread starts in the ctor: a Publish landing before its first HasPendingFrame check lets it run a turn on its
        // own, and DrainSync then adds a bare turn. So record under a lock and assert relative order / membership, not exact
        // sequences (the turns themselves are serial, so a returned DrainSync means every earlier turn has finished).
        var order = new System.Collections.Generic.List<string>();
        var gate = new object();
        void Record(string s) { lock (gate) order.Add(s); }
        string[] Snapshot() { lock (gate) return order.ToArray(); }
        var rt = new RenderThread(seam, _ => Record("submit"), async: false,
                                  takePresentSlot: _ => { Record("take"); return true; },
                                  preTurn: () => Record("pre"));
        try
        {
            Span<byte> one = stackalloc byte[] { 1 };
            seam.Publish(one, default, default);
            rt.DrainSync();
            var first = Snapshot();
            Assert.Equal(1, Array.FindAll(first, s => s == "take").Length);
            Assert.Equal(1, Array.FindAll(first, s => s == "submit").Length);
            int take = Array.IndexOf(first, "take");
            Assert.True(take > 0 && first[take - 1] == "pre", $"the preTurn hook must run right before the slot take: [{string.Join(",", first)}]");
            Assert.True(Array.IndexOf(first, "submit") > take, $"the present must follow the slot take: [{string.Join(",", first)}]");

            int seen = first.Length;
            rt.DrainSync();                                  // a bare wake: no publication, no motion
            var after = Snapshot();
            var added = new string[after.Length - seen];
            Array.Copy(after, seen, added, 0, added.Length);
            Assert.Contains("pre", added);
            Assert.DoesNotContain("take", added);
            Assert.DoesNotContain("submit", added);
        }
        finally { rt.Dispose(); }
    }

    // F208: a surface handle arriving wakes the loop with no publication, so the early drain runs on the next turn instead of
    // waiting for a UI frame (or for another window's slot wait) to come round.
    [Fact]
    public void WakeForVideo_RunsPreTurnWithoutAPublication_AndTakesNoSlot()
    {
        ThreadGuard.BindCurrent(ThreadGuard.ThreadRole.Ui);
        var seam = new SceneFramePublisher();
        using var ran = new ManualResetEventSlim(false);
        int takes = 0;
        var rt = new RenderThread(seam, _ => { }, async: false,
                                  takePresentSlot: _ => { Interlocked.Increment(ref takes); return true; },
                                  preTurn: () => ran.Set());
        try
        {
            rt.WakeForVideo();
            Assert.True(ran.Wait(5_000), "the wake never ran the preTurn hook");
            Assert.Equal(0, Volatile.Read(ref takes));       // a bare wake presents nothing: no slot is reserved
        }
        finally { rt.Dispose(); }
        rt.WakeForVideo();                                   // after Dispose: dropped, never throws
    }

    // F208: an UNPACED turn's slot take is the liveness-bounded form, bounded at max(2 x refresh, 34 ms) - not the plain 1 s
    // wait a minimized / cloaked primary used to cost on every turn.
    [Fact]
    public void AnUnpacedTurnsSlotTake_IsLivenessBoundedAtTwoRefreshes_NotOneSecond()
    {
        ThreadGuard.BindCurrent(ThreadGuard.ThreadRole.Ui);
        var seam = new SceneFramePublisher();
        long period = Stopwatch.Frequency / 60;
        int lastTimeout = int.MinValue;
        var rt = new RenderThread(seam, _ => { }, async: false, tickPeriod: () => period,
                                  takePresentSlot: ms => { lastTimeout = ms; return true; });
        try
        {
            Span<byte> one = stackalloc byte[] { 1 };
            seam.Publish(one, default, default);
            rt.DrainSync();
            Assert.Equal(-RenderThread.UnpacedSlotBoundMs(period * 1000.0 / Stopwatch.Frequency), lastTimeout);
            Assert.InRange(lastTimeout, -100, -34);          // a 60 Hz panel: 2 x 16.7 ms rounds up to 34 ms
        }
        finally { rt.Dispose(); }

        Assert.Equal(34, RenderThread.UnpacedSlotBoundMs(8.33));    // 120 Hz: the 34 ms floor
        Assert.Equal(67, RenderThread.UnpacedSlotBoundMs(33.3));    // 30 Hz: two refreshes
    }
}
