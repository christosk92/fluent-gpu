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
    /// run input, timers and recovery.</summary>
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
        // Ceiling reached ⇒ produce anyway.
        Assert.False(gate.Blocks(publishSeq: 5, nowTicks: 1000 + Ceiling, ceilingTicks: Ceiling));
        Assert.False(gate.IsArmed);
        Assert.Equal(0, gate.SinceTicksForTest);
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

    /// <summary>The handshake under real concurrency — the only check that exercises the actual memory barriers rather
    /// than a scripted delegate. A present thread advances the ack at an unpredictable instant while the UI thread
    /// gates. The invariant is "no lost wake": whenever the gate reports blocked it must be armed, so the render
    /// callback is guaranteed to observe it. The ASSERTION is not timing dependent, so this cannot flake.
    ///
    /// The producer YIELDS rather than spins, deliberately. A hard <c>SpinWait</c> loop here pegged a core and starved
    /// the timing-sensitive tests in other classes — xUnit runs classes in parallel, and this test failed
    /// <c>AudioFeedRaceTests</c> from the outside while passing itself. A test that damages its neighbours is a bad
    /// test even when it is green.</summary>
    [Fact]
    public void UnderConcurrentAcks_BlockedAlwaysImpliesArmed()
    {
        long ack = 0;
        var gate = new DisplayPhaseGate(() => (ulong)Volatile.Read(ref ack));
        var violations = new List<string>();
        using var stop = new ManualResetEventSlim(false);

        var presenter = new Thread(() =>
        {
            while (!stop.IsSet) { Interlocked.Increment(ref ack); Thread.Yield(); }
        }) { IsBackground = true };
        presenter.Start();

        for (int i = 1; i <= 5_000; i++)
        {
            bool blocked = gate.Blocks((ulong)i, nowTicks: i, ceilingTicks: long.MaxValue);
            // The load-bearing invariant. A blocked-but-unarmed gate is precisely the lost wake: the UI parks and
            // nothing will ever nudge it.
            if (blocked && !gate.IsArmed) violations.Add($"iteration {i}: blocked but not armed");
            if (!blocked && gate.IsArmed) violations.Add($"iteration {i}: opened but left armed");
        }
        stop.Set();
        presenter.Join(2000);

        Assert.True(violations.Count == 0, string.Join("; ", violations.GetRange(0, Math.Min(5, violations.Count))));
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
        kind is HostWaitKind.DisplayRate or HostWaitKind.PaceAsync or HostWaitKind.PaceSkipSubmit || w == 0;

    /// <summary>THE COLLISION. Ambient at 60 Hz on a 120 Hz panel can return exactly 17, which is also the phase-gate
    /// ceiling. Numeric classification cannot tell them apart; structural classification must.</summary>
    [Theory]
    [InlineData(17)]   // == PhaseGateCeilingMs() on a 120 Hz panel: the exact aliasing value
    [InlineData(7)]    // == AsyncDisplayPaceMs
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
    [InlineData(HostWaitKind.PaceAsync, 7)]
    [InlineData(HostWaitKind.PaceAsync, 34)]     // clamped ceiling upper bound
    [InlineData(HostWaitKind.PaceSkipSubmit, 7)]
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

    /// <summary>The `w == 0` clause is NOT redundant with the kind check. BakedBlurQueue returns 0 for "due now"; no
    /// throttle gap elapses, so resyncing there would reintroduce the very lurch that kind-based classification is
    /// meant to avoid. This is the one case where the value still carries information the branch does not.</summary>
    [Fact]
    public void ZeroWait_IsDisplayRate_EvenOnAThrottledBranch()
    {
        Assert.True(IsDisplayRateWait(HostWaitKind.Baked, 0));
        Assert.True(IsDisplayRateWait(HostWaitKind.Idle, 0));
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
