using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using FluentGpu.Hosting;
using FluentGpu.Hosting.Threading;
using Xunit;

namespace FluentGpu.Engine.Tests;

/// <summary>
/// The display-phase gate (design/subsystems/threading-render-seam.md §11.1) and the wait classification it feeds.
///
/// These exercise the primitive directly rather than through <c>AppHost</c>, and that is deliberate: a Headless window
/// always resolves to <c>RenderLoopMode.SingleThread</c>, so the async render thread — and therefore the gate — never
/// runs headlessly. A host-level test would pass while asserting nothing.
///
/// Time is INJECTED, never read from the clock (the discipline from <c>DirectManipulationPacingTests</c>), so the
/// stall ceiling is deterministic rather than a sleep race. The ack is read through a delegate over a mutable local,
/// which is what lets a test place a present at an exact point inside the arm-then-recheck handshake — the window
/// where the first implementation lost wakes on ~16% of frames.
/// </summary>
public sealed class DisplayPhaseGateTests
{
    private const long Ceiling = 1000;   // arbitrary tick units; these tests never read a real clock

    /// <summary>Nothing unpresented ⇒ the gate is open, does not arm, and counts nothing. The render thread must not
    /// be posting wakes at a loop that is not waiting.</summary>
    [Fact]
    public void DrainedSeam_DoesNotBlockOrArm()
    {
        ulong ack = 5;
        var gate = new DisplayPhaseGate(() => ack);

        Assert.False(gate.Blocks(publishSeq: 5, nowTicks: 100, ceilingTicks: Ceiling));
        Assert.False(gate.IsArmed);
        Assert.Equal(0, gate.GatedFrames);
    }

    /// <summary>A stale ack ⇒ the gate blocks AND arms, so the render thread owes a wake.</summary>
    [Fact]
    public void StaleAck_BlocksAndArms()
    {
        ulong ack = 4;
        var gate = new DisplayPhaseGate(() => ack);

        Assert.True(gate.Blocks(publishSeq: 5, nowTicks: 100, ceilingTicks: Ceiling));
        Assert.True(gate.IsArmed);
        Assert.Equal(1, gate.GatedFrames);
    }

    /// <summary>THE RACE. The present lands between the first ack read and the arm. Before the arm-then-recheck
    /// handshake the gate blocked while the callback had already run and seen "not armed", so no wake was coming and
    /// the UI slept to the stall ceiling. The recheck must notice and open — and must leave the gate DISARMED, or the
    /// render thread posts a wake at a loop that is not waiting.</summary>
    [Fact]
    public void AckRacesBetweenReadAndArm_OpensAndDisarms()
    {
        int reads = 0;
        // First read returns the stale ack (we decide to gate); the present lands; the recheck sees it.
        var gate = new DisplayPhaseGate(() => { reads++; return reads == 1 ? 4UL : 5UL; });

        Assert.False(gate.Blocks(publishSeq: 5, nowTicks: 100, ceilingTicks: Ceiling));
        Assert.False(gate.IsArmed);
        Assert.Equal(2, reads);                 // the recheck actually happened
        Assert.Equal(0, gate.GatedFrames);      // an opened frame is produced, not declined
    }

    /// <summary>The ordinary path: the gate is armed and STAYS armed while the ack remains stale, so a present that
    /// arrives later is guaranteed to observe the armed state and deliver the wake.</summary>
    [Fact]
    public void LaterAck_ObservesArmedState()
    {
        ulong ack = 4;
        var gate = new DisplayPhaseGate(() => ack);

        Assert.True(gate.Blocks(publishSeq: 5, nowTicks: 100, ceilingTicks: Ceiling));
        Assert.True(gate.IsArmed);              // <- what the render thread's callback reads

        // The present lands now. The host's callback sees IsArmed and wakes the UI; the next poll opens the gate.
        ack = 5;
        Assert.False(gate.Blocks(publishSeq: 5, nowTicks: 200, ceilingTicks: Ceiling));
        Assert.False(gate.IsArmed);
    }

    /// <summary>The stall ceiling opens the gate and disarms it. The gate must be an optimization, never a liveness
    /// dependency: an occluded, stalled or device-lost render thread stops acking entirely, and the loop still has to
    /// run input, timers and recovery. Every escape is counted explicitly — never a silent smoothness win.</summary>
    [Fact]
    public void StallCeiling_OpensAndDisarms_WhenAckNeverArrives()
    {
        ulong ack = 4;                          // never advances
        var gate = new DisplayPhaseGate(() => ack);

        Assert.True(gate.Blocks(publishSeq: 5, nowTicks: 1000, ceilingTicks: Ceiling));
        Assert.True(gate.IsArmed);
        // Still inside the ceiling.
        Assert.True(gate.Blocks(publishSeq: 5, nowTicks: 1000 + Ceiling - 1, ceilingTicks: Ceiling));
        Assert.True(gate.IsArmed);
        Assert.Equal(0, gate.CeilingEscapes);
        // Ceiling reached ⇒ produce anyway.
        Assert.False(gate.Blocks(publishSeq: 5, nowTicks: 1000 + Ceiling, ceilingTicks: Ceiling));
        Assert.False(gate.IsArmed);
        Assert.Equal(0, gate.SinceTicksForTest);
        Assert.Equal(1, gate.CeilingEscapes);
    }

    /// <summary>A normal present-ack open must not inflate the ceiling-escape counter.</summary>
    [Fact]
    public void PresentAckOpen_DoesNotCountAsCeilingEscape()
    {
        ulong ack = 4;
        var gate = new DisplayPhaseGate(() => ack);
        Assert.True(gate.Blocks(5, 0, Ceiling));
        ack = 5;
        Assert.False(gate.Blocks(5, 1, Ceiling));
        Assert.Equal(0, gate.CeilingEscapes);
    }

    /// <summary>After the ceiling fires, a still-stale ack re-arms a FRESH stretch rather than opening forever — the
    /// gate degrades to producing one frame per ceiling, not to being permanently disabled.</summary>
    [Fact]
    public void AfterCeiling_ReArmsAFreshStretch()
    {
        ulong ack = 4;
        var gate = new DisplayPhaseGate(() => ack);

        Assert.True(gate.Blocks(5, nowTicks: 0, ceilingTicks: Ceiling));
        Assert.False(gate.Blocks(5, nowTicks: Ceiling, ceilingTicks: Ceiling));      // ceiling opens
        Assert.True(gate.Blocks(5, nowTicks: Ceiling + 1, ceilingTicks: Ceiling));   // new stretch
        Assert.True(gate.IsArmed);
        Assert.Equal(Ceiling + 1, gate.SinceTicksForTest);
    }

    /// <summary>Only DECLINED frames are counted. The census is used to argue that the gate declines exactly the frames
    /// DropOldest would have discarded, so an opened frame must never inflate it.</summary>
    [Fact]
    public void GatedFrames_CountsOnlyDeclinedFrames()
    {
        ulong ack = 0;
        var gate = new DisplayPhaseGate(() => ack);

        Assert.True(gate.Blocks(1, 0, Ceiling));
        Assert.True(gate.Blocks(1, 1, Ceiling));
        Assert.True(gate.Blocks(1, 2, Ceiling));
        Assert.Equal(3, gate.GatedFrames);

        ack = 1;
        Assert.False(gate.Blocks(1, 3, Ceiling));
        Assert.Equal(3, gate.GatedFrames);   // unchanged
    }

    /// <summary>ARM-AT-PUBLISH, the ordinary case. A frame was just handed to the seam and no ack has caught up, so the
    /// gate must arm immediately — otherwise the render thread's present callback sees "not armed" for the very frame it
    /// is acking and elides the wake, which is what left producing cycles paced by the wall clock instead of the
    /// display.</summary>
    [Fact]
    public void ArmAtPublish_ArmsWhenPresentOwed()
    {
        ulong ack = 4;
        var gate = new DisplayPhaseGate(() => ack);

        Assert.True(gate.ArmAtPublish(publishSeq: 5, nowTicks: 100));
        Assert.True(gate.IsArmed);
        Assert.Equal(100, gate.SinceTicksForTest);
    }

    /// <summary>The same race <see cref="AckRacesBetweenReadAndArm_OpensAndDisarms"/> covers for <c>Blocks</c>, at the
    /// publish site: the present lands between the first ack read and the arm. The recheck must notice and leave the
    /// gate DISARMED, or the render thread posts a wake at a loop that is not parked.</summary>
    [Fact]
    public void ArmAtPublish_AckAlreadyLanded_OpensAndDisarms()
    {
        // Case 1: the ack was already current on the FIRST read (a fast present beat us to the call).
        ulong current = 5;
        var settled = new DisplayPhaseGate(() => current);
        Assert.False(settled.ArmAtPublish(publishSeq: 5, nowTicks: 10));
        Assert.False(settled.IsArmed);
        Assert.Equal(0, settled.SinceTicksForTest);

        // Case 2: THE RACE — stale on the first read, current on the recheck.
        int reads = 0;
        var raced = new DisplayPhaseGate(() => { reads++; return reads == 1 ? 4UL : 5UL; });
        Assert.False(raced.ArmAtPublish(publishSeq: 5, nowTicks: 10));
        Assert.False(raced.IsArmed);
        Assert.Equal(2, reads);              // the recheck actually happened
        Assert.Equal(0, raced.SinceTicksForTest);
    }

    /// <summary>Publish and poll share ONE stretch, and its origin is the publish stamp. The present is owed from the
    /// moment the frame was handed over, so the stall ceiling must be measured from there — measuring it from the next
    /// poll would stretch the liveness backstop by however long the poll took to come round.</summary>
    [Fact]
    public void ArmAtPublish_SharesStretchWithBlocks_CeilingFromPublish()
    {
        ulong ack = 4;                       // never advances
        var gate = new DisplayPhaseGate(() => ack);

        Assert.True(gate.ArmAtPublish(publishSeq: 5, nowTicks: 1000));
        Assert.Equal(1000, gate.SinceTicksForTest);

        // A poll inside the stretch neither re-stamps the origin nor re-arms a second stretch.
        Assert.True(gate.Blocks(5, nowTicks: 1500, ceilingTicks: Ceiling));
        Assert.Equal(1000, gate.SinceTicksForTest);
        Assert.True(gate.IsArmed);

        // ...and the ceiling fires relative to the PUBLISH stamp, not to the first poll.
        Assert.False(gate.Blocks(5, nowTicks: 1000 + Ceiling, ceilingTicks: Ceiling));
        Assert.False(gate.IsArmed);
        Assert.Equal(1, gate.CeilingEscapes);

        // Publishing again inside a still-open stretch (the frame the ceiling escape produced) keeps the arm and does
        // not open the gate: only Blocks owns the ceiling decision.
        Assert.True(gate.Blocks(6, nowTicks: 1000 + Ceiling + 1, ceilingTicks: Ceiling));
        Assert.True(gate.ArmAtPublish(publishSeq: 6, nowTicks: 1000 + Ceiling + 2));
        Assert.True(gate.IsArmed);
        Assert.Equal(1000 + Ceiling + 1, gate.SinceTicksForTest);   // the earlier stretch origin stands
    }

    /// <summary>GatedFrames is the census of frames DECLINED — the argument that the gate drops exactly what DropOldest
    /// would have discarded rests on it. A publish is a frame that WAS produced, so no path through
    /// <c>ArmAtPublish</c> may touch the counter (nor the ceiling-escape counter).</summary>
    [Fact]
    public void ArmAtPublish_DoesNotCountGatedFrames()
    {
        ulong ack = 0;
        var gate = new DisplayPhaseGate(() => ack);

        Assert.True(gate.ArmAtPublish(1, nowTicks: 0));
        Assert.True(gate.ArmAtPublish(2, nowTicks: 1));   // second publish inside the same open stretch
        Assert.Equal(0, gate.GatedFrames);
        Assert.Equal(0, gate.CeilingEscapes);

        // A real decline still counts, and the publish before it did not pre-inflate the census.
        Assert.True(gate.Blocks(2, nowTicks: 2, ceilingTicks: Ceiling));
        Assert.Equal(1, gate.GatedFrames);

        // Opening through the ack does not count either.
        ack = 2;
        Assert.False(gate.ArmAtPublish(2, nowTicks: 3));
        Assert.Equal(1, gate.GatedFrames);
        Assert.Equal(0, gate.CeilingEscapes);
    }

    /// <summary>The handshake under real concurrency — the only check that exercises the actual memory barriers rather
    /// than a scripted delegate. A present thread advances the ack at an unpredictable instant while the UI thread
    /// gates. The invariant is "no lost wake": whenever the gate reports blocked it must be armed, so the render
    /// callback is guaranteed to observe it. The ASSERTION is not timing dependent, so this cannot flake.
    ///
    /// The producer YIELDS rather than spins, deliberately. A hard <c>SpinWait</c> loop here pegged a core and starved
    /// the timing-sensitive tests in other classes — xUnit runs classes in parallel, and this test failed
    /// <c>AudioFeedRaceTests</c> from the outside while passing itself. A test that damages its neighbours is a bad
    /// test even when it is green.
    ///
    /// The loop mirrors the real frame shape: poll the gate, and on the frames it lets through, PUBLISH — so
    /// <c>ArmAtPublish</c> runs against the same live presenter and is held to the same invariant. Arming at publish is
    /// the point where a lost wake would be most expensive (the ack for the frame just handed over is the one the loop
    /// is about to sleep on), so it must be covered by the concurrent check, not only by scripted delegates.</summary>
    [Fact]
    public void UnderConcurrentAcks_BlockedAlwaysImpliesArmed()
    {
        long ack = 0;
        long published = 0;
        var gate = new DisplayPhaseGate(() => (ulong)Volatile.Read(ref ack));
        var violations = new List<string>();
        using var stop = new ManualResetEventSlim(false);

        // The render thread can only acknowledge frames it was actually handed, so the ack CHASES the publish seq at an
        // unpredictable instant. A free-running counter would race so far ahead of the loop that the gate never armed
        // and the arm path would go untested.
        var presenter = new Thread(() =>
        {
            while (!stop.IsSet) { Volatile.Write(ref ack, Volatile.Read(ref published)); Thread.Yield(); }
        }) { IsBackground = true };
        presenter.Start();

        for (int i = 1; i <= 5_000; i++)
        {
            bool blocked = gate.Blocks((ulong)Volatile.Read(ref published), nowTicks: i, ceilingTicks: long.MaxValue);
            // The load-bearing invariant. A blocked-but-unarmed gate is precisely the lost wake: the UI parks and
            // nothing will ever nudge it.
            if (blocked && !gate.IsArmed) violations.Add($"iteration {i}: blocked but not armed");
            if (!blocked && gate.IsArmed) violations.Add($"iteration {i}: opened but left armed");

            if (blocked) continue;
            // The gate opened ⇒ the frame is produced and PUBLISHED; arm for the present it now owes. Same invariant,
            // at the site where losing a wake is most expensive: this is the ack the loop is about to sleep on.
            bool owed = gate.ArmAtPublish((ulong)Interlocked.Increment(ref published), nowTicks: i);
            if (owed && !gate.IsArmed) violations.Add($"iteration {i}: publish owes a present but is not armed");
            if (!owed && gate.IsArmed) violations.Add($"iteration {i}: publish acked but left armed");
        }
        stop.Set();
        presenter.Join(2000);

        Assert.True(violations.Count == 0, string.Join("; ", violations.GetRange(0, Math.Min(5, violations.Count))));
    }
}

/// <summary>
/// The armed gate's blind spot and its escape (design/subsystems/threading-render-seam.md §11.1.4).
///
/// The gate above restores phase by sleeping on the present-ack. That is correct until one vblank slips: the render
/// thread's acks then land one refresh apart (16.67 ms on a 120 Hz panel), which is UNDER the 17 ms stall ceiling — so
/// <see cref="DisplayPhaseGate.CeilingEscapes"/> structurally never fires — ack-paced production follows at 60 Hz, and
/// the next present is late for the same reason. Nothing inside the gate can see it: every publish IS presented, one
/// refresh late, forever. The interval between presents is the only place the signature exists.
///
/// Time is INJECTED here for the same reason it is above — the bands and the hysteresis are then deterministic instead
/// of a sleep race. R is one 120 Hz refresh at a 10 MHz QPC.
/// </summary>
public sealed class PresentSlipDetectorTests
{
    private const long R = 83333;
    private const long T0 = 1_000_000;

    /// <summary>The first present has no predecessor, so there is no interval to classify.</summary>
    [Fact]
    public void FirstPresent_ClassifiesNothing()
    {
        var d = new PresentSlipDetector();
        d.OnPresent(T0, R);
        Assert.False(d.RephaseWanted);
        Assert.Equal(0, d.SlipStreakForTest);
        Assert.Equal(0, d.Episode);
    }

    /// <summary>THE HYSTERESIS. Two one-slip intervals must not engage; the third must. Engaging on the first would
    /// fire on every isolated scheduling hiccup and force the loop to panel rate for no reason at all.</summary>
    [Fact]
    public void ThreeConsecutiveOneSlips_Engage_TwoDoNot()
    {
        var d = new PresentSlipDetector();
        long t = T0;
        d.OnPresent(t, R);
        t += 2 * R; d.OnPresent(t, R);
        Assert.False(d.RephaseWanted);
        t += 2 * R; d.OnPresent(t, R);
        Assert.False(d.RephaseWanted);
        Assert.Equal(2, d.SlipStreakForTest);
        Assert.Equal(0, d.Episode);

        t += 2 * R; d.OnPresent(t, R);
        Assert.True(d.RephaseWanted);
        Assert.Equal(1, d.Episode);
    }

    /// <summary>Disengaging is asymmetric ON PURPOSE: the FIRST healthy interval stands the escape down. The escape has
    /// then done its job, and keeping the compositor tick armed past that point is exactly the busywork the armed
    /// branch's rationale objects to.</summary>
    [Fact]
    public void OneHealthyInterval_Disengages()
    {
        var d = Engaged(out long t);
        t += R; d.OnPresent(t, R);
        Assert.False(d.RephaseWanted);
        Assert.Equal(0, d.SlipStreakForTest);
        Assert.Equal(1, d.Episode);      // disengaging does not bump; only engaging does
    }

    /// <summary>A re-lock is a NEW episode, so a consumer budgeting escapes per episode starts over instead of
    /// inheriting a budget the previous lock already spent.</summary>
    [Fact]
    public void ReEngaging_BumpsEpisode()
    {
        var d = Engaged(out long t);
        t += R; d.OnPresent(t, R);                                      // disengage
        for (int i = 0; i < 3; i++) { t += 2 * R; d.OnPresent(t, R); }   // re-engage
        Assert.True(d.RephaseWanted);
        Assert.Equal(2, d.Episode);
    }

    /// <summary>Real present intervals jitter. The band is 1.5R..2.5R, so 2R ± 0.3R is still a one-slip and holds the
    /// engagement; 1.2R is healthy and stands it down.</summary>
    [Fact]
    public void JitteredSlipIntervals_StayEngaged_UntilAHealthyOne()
    {
        var d = Engaged(out long t);
        t += (long)(2.3 * R); d.OnPresent(t, R);
        Assert.True(d.RephaseWanted);
        t += (long)(1.7 * R); d.OnPresent(t, R);
        Assert.True(d.RephaseWanted);
        Assert.Equal(1, d.Episode);      // still the SAME lock, not a new one

        t += (long)(1.2 * R); d.OnPresent(t, R);
        Assert.False(d.RephaseWanted);
    }

    /// <summary>Intervals at or beyond 2.5R are idle gaps, occlusion, a minimized window, or a GPU that genuinely
    /// cannot hold the panel rate. None is a phase problem, and forcing a deliberately-slow loop to panel rate is the
    /// one way this could make things worse — so they never engage AND they reset an in-progress streak.</summary>
    [Fact]
    public void LongIntervals_NeverEngage_AndResetTheStreak()
    {
        var d = new PresentSlipDetector();
        long t = T0;
        d.OnPresent(t, R);
        t += 2 * R; d.OnPresent(t, R);
        t += 2 * R; d.OnPresent(t, R);
        Assert.Equal(2, d.SlipStreakForTest);   // one short of engaging

        t += 40 * R; d.OnPresent(t, R);         // an idle gap lands instead
        Assert.Equal(0, d.SlipStreakForTest);
        for (int i = 0; i < 6; i++) { t += 3 * R; d.OnPresent(t, R); }
        Assert.False(d.RephaseWanted);
        Assert.Equal(0, d.Episode);
    }

    /// <summary>A long gap while ENGAGED disengages too: an occluded or stalled chain is not a phase lock, and holding
    /// the escape armed across it would keep waking a loop that has nothing to re-anchor.</summary>
    [Fact]
    public void LongIntervalWhileEngaged_Disengages()
    {
        var d = Engaged(out long t);
        t += 40 * R; d.OnPresent(t, R);
        Assert.False(d.RephaseWanted);
        Assert.Equal(0, d.SlipStreakForTest);
    }

    /// <summary>Garbage samples classify nothing and destroy nothing: no measured refresh period (the headless/unknown
    /// case) and a non-monotonic stamp both stamp and return, leaving an in-progress streak intact.</summary>
    [Fact]
    public void MissingRefreshOrBackwardsClock_ClassifiesNothing()
    {
        var d = new PresentSlipDetector();
        long t = T0;
        d.OnPresent(t, R);
        t += 2 * R; d.OnPresent(t, R);
        t += 2 * R; d.OnPresent(t, R);
        Assert.Equal(2, d.SlipStreakForTest);

        d.OnPresent(t + 2 * R, 0);        // no refresh period reported
        Assert.Equal(2, d.SlipStreakForTest);
        Assert.False(d.RephaseWanted);
        d.OnPresent(t, R);                // the clock went backwards relative to the stamp above
        Assert.Equal(2, d.SlipStreakForTest);
        Assert.False(d.RephaseWanted);
    }

    /// <summary>The handshake under real concurrency, in the shape the host actually runs it: the PRESENT thread is the
    /// only writer and the UI thread only ever reads the two volatile ints. Unlike the gate there is no StoreLoad pair
    /// to fence — nothing here is a wake — so the invariant is simply that the engagement becomes VISIBLE to a reader
    /// that never touches the state. The assertion is a bounded wait, not a timing measurement.</summary>
    [Fact]
    public void EngagementIsVisibleToAReadingUiThread()
    {
        var d = new PresentSlipDetector();
        using var stop = new ManualResetEventSlim(false);

        var presenter = new Thread(() =>
        {
            long t = T0;
            d.OnPresent(t, R);
            while (!stop.IsSet)
            {
                t += 2 * R;                 // a sustained one-vblank slip, forever
                d.OnPresent(t, R);
                Thread.Yield();             // yield, never spin: a core-pegging test starves its neighbours (xUnit
            }                               // runs classes in parallel — see UnderConcurrentAcks_BlockedAlwaysImpliesArmed)
        }) { IsBackground = true };
        presenter.Start();

        var sw = Stopwatch.StartNew();
        bool seen = false;
        while (sw.ElapsedMilliseconds < 5000 && !(seen = d.RephaseWanted)) Thread.Yield();
        int episode = d.Episode;
        stop.Set();
        presenter.Join(2000);

        Assert.True(seen, "the UI-side read never observed the engagement the present thread published");
        Assert.True(episode >= 1, $"episode should have been bumped on engage; saw {episode}");
    }

    /// <summary>Three one-slip intervals ⇒ engaged, with the stamp cursor left where the caller can continue.</summary>
    private static PresentSlipDetector Engaged(out long t)
    {
        var d = new PresentSlipDetector();
        t = T0;
        d.OnPresent(t, R);
        for (int i = 0; i < 3; i++) { t += 2 * R; d.OnPresent(t, R); }
        Assert.True(d.RephaseWanted);
        return d;
    }
}

/// <summary>
/// Wait classification must be STRUCTURAL (which branch chose the wait), never numeric (what integer it returned).
/// The value-based form aliased: on a 120 Hz panel the phase-gate ceiling is 17 ms and an Ambient 60-on-120 wait
/// returns integers 1..17, so an Ambient-throttled frame that happened to compute 17 was classified as display-rate.
/// That skipped both the timer clamp and the frame-clock step-up Resync — the cadence-lurch this guard exists to
/// prevent.
/// </summary>
public sealed class HostWaitKindClassificationTests
{
    // Mirror of AppHost.IsDisplayRateWait, which is private. Kept in lockstep by
    // MirrorsAppHostRule_ForEveryKind below, which enumerates the enum so a newly added kind fails loudly here rather
    // than silently defaulting to "throttled" in one place and "display-rate" in the other.
    private static bool IsDisplayRateWait(HostWaitKind kind, int w) =>
        kind is HostWaitKind.DisplayRate or HostWaitKind.PaceAsync or HostWaitKind.PaceSkipSubmit
            || (kind is HostWaitKind.Baked && w == 0);

    /// <summary>THE COLLISION. Ambient at 60 Hz on a 120 Hz panel can return exactly 17, which is also the phase-gate
    /// ceiling. Numeric classification cannot tell them apart; structural classification must.</summary>
    [Theory]
    [InlineData(17)]   // == PhaseGateCeilingMs() on a 120 Hz panel: the exact aliasing value
    [InlineData(7)]    // == DeriveAsyncPaceMs at 120 Hz
    [InlineData(15)]   // == DeriveAsyncPaceMs at 60 Hz — the pace cap is refresh-derived, so it aliases at MORE values now
    [InlineData(16)]
    [InlineData(1)]
    public void Ambient_IsNeverDisplayRate_EvenWhenItsTimeoutCollides(int ms)
    {
        Assert.False(IsDisplayRateWait(HostWaitKind.Ambient, ms));
        // ...while the phase-gate branch returning the SAME integer is display-rate.
        Assert.True(IsDisplayRateWait(HostWaitKind.PaceAsync, ms));
    }

    /// <summary>The three display-rate branches, at every value they can actually return.</summary>
    [Theory]
    [InlineData(HostWaitKind.DisplayRate, 0)]
    [InlineData(HostWaitKind.PaceAsync, 7)]      // DeriveAsyncPaceMs at 120 Hz
    [InlineData(HostWaitKind.PaceAsync, 15)]     // ...and at 60 Hz: the cap is derived, so the VALUE varies with the panel
    [InlineData(HostWaitKind.PaceAsync, 34)]     // clamped ceiling upper bound
    [InlineData(HostWaitKind.PaceSkipSubmit, 7)]
    [InlineData(HostWaitKind.PaceSkipSubmit, 15)]
    public void DisplayRateBranches_AreDisplayRate(HostWaitKind kind, int ms)
        => Assert.True(IsDisplayRateWait(kind, ms));

    /// <summary>Throttled and idle branches are not display-rate, so the step-up Resync still fires when the loop
    /// climbs out of them.</summary>
    [Theory]
    [InlineData(HostWaitKind.Idle, -1)]
    [InlineData(HostWaitKind.Hud, 100)]
    [InlineData(HostWaitKind.Ambient, 16)]
    [InlineData(HostWaitKind.Baked, 33)]
    [InlineData(HostWaitKind.Baked, 500)]
    public void ThrottledBranches_AreNotDisplayRate(HostWaitKind kind, int ms)
        => Assert.False(IsDisplayRateWait(kind, ms));

    /// <summary>The `w == 0` clause is NOT redundant with the kind check, but it is SCOPED to the one kind that
    /// legitimately produces a 0: <c>BakedBlurQueue.RecommendedWaitMs</c> returns 0 for "due now" under
    /// <see cref="HostWaitKind.Baked"/>; no throttle gap elapses, so resyncing there would reintroduce the very lurch
    /// that kind-based classification is meant to avoid.
    ///
    /// Unscoped it aliased in the OTHER direction — the same hazard as the 17 ms collision above, just by value 0: an
    /// Idle/Ambient wait that <c>ClampWaitToTimers</c> had rewritten down to 0 for a due timer then read as
    /// display-rate, which suppressed the step-up Resync on precisely the frames that HAD over-slept. The clamp floors
    /// at 1 ms now, and <c>AppHost.IsDisplayRateWait</c> accepts a 0 from no branch but Baked.</summary>
    [Fact]
    public void ZeroWait_IsDisplayRate_OnlyOnTheBakedBranch()
    {
        Assert.True(IsDisplayRateWait(HostWaitKind.Baked, 0));
        Assert.False(IsDisplayRateWait(HostWaitKind.Idle, 0));
        Assert.False(IsDisplayRateWait(HostWaitKind.Ambient, 0));
    }

    /// <summary>Every enum member is classified deliberately. A new HostWaitKind added without a decision here would
    /// otherwise silently inherit "throttled", which resyncs the frame clock on every frame it paces.</summary>
    [Fact]
    public void EveryKind_IsClassifiedDeliberately()
    {
        var displayRate = new HashSet<HostWaitKind>
        {
            HostWaitKind.DisplayRate, HostWaitKind.PaceAsync, HostWaitKind.PaceSkipSubmit,
        };
        foreach (HostWaitKind k in Enum.GetValues<HostWaitKind>())
            Assert.Equal(displayRate.Contains(k), IsDisplayRateWait(k, 5));
    }
}
