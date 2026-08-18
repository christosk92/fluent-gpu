using System.Diagnostics;
using FluentGpu.Pal.Windows;
using Xunit;

namespace FluentGpu.Windows.Tests;

/// <summary>Locks the pure decision seams of the DirectManipulation producer: the wheel arbitration and the minimal
/// recovery ladder's detectors (scroll-v3-plan-2026-08-17.md §5.2 — the manual-update pacer, the idle heartbeat and the
/// dedicated inertia-stall watchdog are DELETED; there is no pacer/heartbeat/inertia-stall left in this file to lock).
///
/// <para>There is deliberately NO VerticalSlice gate for the stall recovery. The slice's transitive closure is
/// contractually TerraFX-free, and neither a DIRECTMANIPULATION_STATUS transition nor a DM_POINTERHITTEST can be
/// synthesized headlessly (<c>InjectSyntheticPointerInput</c> has no PT_TOUCHPAD source). These pure-helper tests plus
/// the ScrollTrace note-107 telemetry acceptance in the plan are the substitute: a normal touchpad session must produce
/// zero 107 rows, and any recurrence must self-recover in ~5s with 107 rows naming detector, rungs and revival.</para></summary>
public sealed class DirectManipulationPacingTests
{
    /// <summary>The live 218-second blackout's first root cause: a READY arriving from ENABLED/BUILDING/SUSPENDED
    /// silently cancelled the engage wedge, so the watchdog never fired for a DM that owned the touchpad and never
    /// engaged. Only a READY that genuinely terminates an engage may disarm it.</summary>
    [Fact]
    public void EngageWedge_SpuriousReadyDoesNotDisarm_TerminalReadyDoes()
    {
        // Terminal: the engage ran and ended (finger lift, or the coast finishing).
        Assert.True(DmEngageWedge.Disarms(Win32DirectManipulation.DM_READY, Win32DirectManipulation.DM_RUNNING));
        Assert.True(DmEngageWedge.Disarms(Win32DirectManipulation.DM_READY, Win32DirectManipulation.DM_INERTIA));

        // Spurious: DM went READY without ever engaging — the wedge must survive and fire at its 120 ms deadline.
        Assert.False(DmEngageWedge.Disarms(Win32DirectManipulation.DM_READY, Win32DirectManipulation.DM_ENABLED));
        Assert.False(DmEngageWedge.Disarms(Win32DirectManipulation.DM_READY, Win32DirectManipulation.DM_BUILDING));
        Assert.False(DmEngageWedge.Disarms(Win32DirectManipulation.DM_READY, Win32DirectManipulation.DM_SUSPENDED));
        Assert.False(DmEngageWedge.Disarms(Win32DirectManipulation.DM_READY, Win32DirectManipulation.DM_DISABLED));
        Assert.False(DmEngageWedge.Disarms(Win32DirectManipulation.DM_READY, Win32DirectManipulation.DM_READY));

        // Nothing but a READY disarms, whatever it came from.
        Assert.False(DmEngageWedge.Disarms(Win32DirectManipulation.DM_RUNNING, Win32DirectManipulation.DM_RUNNING));
        Assert.False(DmEngageWedge.Disarms(Win32DirectManipulation.DM_INERTIA, Win32DirectManipulation.DM_RUNNING));
        Assert.False(DmEngageWedge.Disarms(Win32DirectManipulation.DM_SUSPENDED, Win32DirectManipulation.DM_INERTIA));
    }

    /// <summary>The SUSPENDED half of the design spec (scroll-feel-rework-design §223-224), previously unimplemented:
    /// DM parked a pending contact instead of engaging it, so there is no timeout left worth waiting out. Gated on the
    /// pending engage — an occlusion-SUSPENDED between gestures must record nothing.</summary>
    [Fact]
    public void EngageWedge_SuspendedWedgesImmediately_OnlyWhileAwaitingEngage()
    {
        Assert.True(DmEngageWedge.WedgesImmediately(Win32DirectManipulation.DM_SUSPENDED, awaitingEngage: true));
        Assert.False(DmEngageWedge.WedgesImmediately(Win32DirectManipulation.DM_SUSPENDED, awaitingEngage: false));

        // No other status wedges immediately — the 120 ms timeout owns those.
        Assert.False(DmEngageWedge.WedgesImmediately(Win32DirectManipulation.DM_ENABLED, awaitingEngage: true));
        Assert.False(DmEngageWedge.WedgesImmediately(Win32DirectManipulation.DM_BUILDING, awaitingEngage: true));
        Assert.False(DmEngageWedge.WedgesImmediately(Win32DirectManipulation.DM_READY, awaitingEngage: true));
        Assert.False(DmEngageWedge.WedgesImmediately(Win32DirectManipulation.DM_RUNNING, awaitingEngage: true));
    }

    /// <summary>The detector the live capture demanded: DM owns the touchpad with a non-live status while contacts keep
    /// hit-testing the window and none of them engages. One unserved hit-test is an ordinary two-finger tap and must
    /// never fire; the boundaries are strict on the timeout and inclusive on hit-test recency.</summary>
    [Fact]
    public void SilentOwner_RequiresRepeatedRecentUnservedHitTests()
    {
        const long engage = 1_000;
        const long now = 5_000;                        // 4 s since DM last manipulated — well past TimeoutMs
        long hit = now - 10;                           // ...and the user is contacting us right now

        Assert.True(DmSilentOwner.IsSilentOwner(statusLive: false, now, hit, engage,
            DmSilentOwner.MinUnservedHitTests));
        Assert.True(DmSilentOwner.IsSilentOwner(statusLive: false, now, hit, engage, 17));

        // A single tap DM correctly declines is not a stall.
        Assert.False(DmSilentOwner.IsSilentOwner(statusLive: false, now, hit, engage, 1));
        Assert.False(DmSilentOwner.IsSilentOwner(statusLive: false, now, hit, engage, 0));

        // Timeout boundary is strict (>), matching both older watchdogs.
        long atTimeout = engage + DmSilentOwner.TimeoutMs;
        Assert.False(DmSilentOwner.IsSilentOwner(statusLive: false, atTimeout, atTimeout - 1, engage, 4));
        Assert.True(DmSilentOwner.IsSilentOwner(statusLive: false, atTimeout + 1, atTimeout, engage, 4));

        // Hit-test recency boundary is inclusive (<=): exactly RecentHitTestMs old still counts as "trying now".
        Assert.True(DmSilentOwner.IsSilentOwner(statusLive: false, now, now - DmSilentOwner.RecentHitTestMs, engage, 4));
        Assert.False(DmSilentOwner.IsSilentOwner(statusLive: false, now, now - DmSilentOwner.RecentHitTestMs - 1, engage, 4));
    }

    /// <summary>The false-positive gate. A live manipulation is DM working, not DM stuck; a stale hit-test means the
    /// user stopped trying; and a session that engaged healthily and then went idle must stay quiet however long it
    /// idles — the last hit-test is older than the last engage there, so nothing fires no matter how old the engage.</summary>
    [Fact]
    public void SilentOwner_LiveStatusOrStaleHitTestOrRecentEngage_NeverFires()
    {
        const long engage = 1_000;
        const long now = 5_000;

        // RUNNING / INERTIA: the inertia stall watchdog owns that state, and DM is by definition not silent.
        Assert.False(DmSilentOwner.IsSilentOwner(statusLive: true, now, now - 10, engage, 8));

        // The user stopped trying: no hit-test inside the recency window.
        Assert.False(DmSilentOwner.IsSilentOwner(statusLive: false, now, engage + 1, engage, 8));

        // DM manipulated recently — this is the gap between two ordinary gestures, not a blackout.
        Assert.False(DmSilentOwner.IsSilentOwner(statusLive: false, now, now - 10, now - DmSilentOwner.TimeoutMs, 8));

        // Healthy-then-idle: the last hit-test predates the last engage, so the ordering clause holds however ancient
        // the engage is. (With RecentHitTestMs <= TimeoutMs the clause is also implied by the two windows — it is kept
        // as a defensive belt should the constants ever diverge.)
        Assert.False(DmSilentOwner.IsSilentOwner(statusLive: false, 1_000_000, engage - 100, engage, 8));
        Assert.False(DmSilentOwner.IsSilentOwner(statusLive: false, 1_000_000, engage, engage, 8));
    }

    /// <summary>One counter, four feeders, three rungs: Stop (the historical behavior), recycle the DM input session
    /// (the code-level alt-tab), then session-disable to the §3.3 heuristic fallback.</summary>
    [Fact]
    public void RecoveryLadder_EscalatesStopThenRecycleThenDisable()
    {
        var ladder = new DmRecoveryLadder();
        Assert.Equal(0, ladder.Strikes);

        Assert.Equal(DmRecoveryAction.Stop, ladder.Record(1_000));
        Assert.Equal(1, ladder.Strikes);
        Assert.Equal(DmRecoveryAction.Recycle, ladder.Record(2_500));
        Assert.Equal(2, ladder.Strikes);
        Assert.Equal(DmRecoveryAction.Disable, ladder.Record(4_000));
        Assert.Equal(DmRecoveryLadder.StrikesToDisable, ladder.Strikes);
    }

    /// <summary>A healthy engage clears the record — the fix for the latent never-reset wedge counter, where three tap
    /// wedges spread across a whole session permanently disabled DM.</summary>
    [Fact]
    public void RecoveryLadder_HealthyEngageResetsStrikes()
    {
        var ladder = new DmRecoveryLadder();
        Assert.Equal(DmRecoveryAction.Stop, ladder.Record(1_000));
        Assert.Equal(DmRecoveryAction.Recycle, ladder.Record(1_500));

        ladder.Reset();
        Assert.Equal(0, ladder.Strikes);

        // Back to rung 1, and the reset also cleared the strike stamp, so the decay window starts fresh.
        Assert.Equal(DmRecoveryAction.Stop, ladder.Record(1_600));
        Assert.Equal(1, ladder.Strikes);
    }

    /// <summary>The decay separates the two populations: a real episode strikes every 120-1500 ms and escalates; an
    /// isolated tap wedge an hour later starts over at rung 1 forever.</summary>
    [Fact]
    public void RecoveryLadder_IsolatedStrikesDecay_AndEpisodeStrikesDoNot()
    {
        var isolated = new DmRecoveryLadder();
        long t = 1_000;
        for (int i = 0; i < 10; i++)
        {
            Assert.Equal(DmRecoveryAction.Stop, isolated.Record(t));
            Assert.Equal(1, isolated.Strikes);
            t += DmRecoveryLadder.StrikeDecayMs + 1;
        }

        // Boundary: exactly at the decay window has NOT decayed yet (strict >).
        var edge = new DmRecoveryLadder();
        Assert.Equal(DmRecoveryAction.Stop, edge.Record(1_000));
        Assert.Equal(DmRecoveryAction.Recycle, edge.Record(1_000 + DmRecoveryLadder.StrikeDecayMs));

        // A real episode: symptoms inside the window, whichever detector sees them, escalate to the hammer.
        var episode = new DmRecoveryLadder();
        Assert.Equal(DmRecoveryAction.Stop, episode.Record(1_000));      // engage wedge at 120 ms
        Assert.Equal(DmRecoveryAction.Recycle, episode.Record(1_120));   // silent owner 1.5 s later
        Assert.Equal(DmRecoveryAction.Disable, episode.Record(2_620));   // and again — hand the touchpad to the fallback
    }

    [Fact]
    public void OnlyKnownPhysicalMouse_PreemptsLiveDirectManipulation()
    {
        Assert.Equal(DmWheelRoute.StopDmAndPass,
            DmWheelArbitration.Decide(dmLive: true, DmWheelSourceEvidence.PhysicalMouse));
        Assert.Equal(DmWheelRoute.DmOwned,
            DmWheelArbitration.Decide(dmLive: true, DmWheelSourceEvidence.Touchpad));
        Assert.Equal(DmWheelRoute.ExistingClassifier,
            DmWheelArbitration.Decide(dmLive: true, DmWheelSourceEvidence.Unknown));

        Assert.Equal(DmWheelRoute.ExistingClassifier,
            DmWheelArbitration.Decide(dmLive: false, DmWheelSourceEvidence.PhysicalMouse));
        Assert.Equal(DmWheelRoute.ExistingClassifier,
            DmWheelArbitration.Decide(dmLive: false, DmWheelSourceEvidence.Touchpad));
        Assert.Equal(DmWheelRoute.ExistingClassifier,
            DmWheelArbitration.Decide(dmLive: false, DmWheelSourceEvidence.Unknown));
    }

    [Fact]
    public void DisplayPacedWait_DefersOnlyPointerMotionCompanions()
    {
        Assert.True(PacedInputWaitClassifier.IsDeferrable(PacedInputWaitClassifier.WmPointerUpdate));
        Assert.True(PacedInputWaitClassifier.IsDeferrable(PacedInputWaitClassifier.WmNcPointerUpdate));
        Assert.True(PacedInputWaitClassifier.IsDeferrable(PacedInputWaitClassifier.WmMouseMove));
        Assert.True(PacedInputWaitClassifier.IsDeferrable(PacedInputWaitClassifier.WmNcMouseMove));
        Assert.True(PacedInputWaitClassifier.IsDeferrable(PacedInputWaitClassifier.WmSetCursor));

        Assert.False(PacedInputWaitClassifier.IsDeferrable(0x0246)); // WM_POINTERDOWN
        Assert.False(PacedInputWaitClassifier.IsDeferrable(0x0247)); // WM_POINTERUP
        Assert.False(PacedInputWaitClassifier.IsDeferrable(0x024E)); // WM_POINTERWHEEL
        Assert.False(PacedInputWaitClassifier.IsDeferrable(0x0100)); // WM_KEYDOWN
        Assert.False(PacedInputWaitClassifier.IsDeferrable(0x0113)); // WM_TIMER
        Assert.False(PacedInputWaitClassifier.IsDeferrable(0x0000)); // WM_NULL / explicit Wake
    }

    [Fact]
    public void DisplayPacedWait_MotionStormCannotSlideAbsoluteDeadline()
    {
        long start = Stopwatch.Frequency * 10L;
        long deadline = start + (long)Math.Ceiling(8.0 * Stopwatch.Frequency / 1000.0);
        int previous = int.MaxValue;

        // Four thousand early motion wakes all consult the same deadline. Remaining time can only decrease and reaches
        // zero at the original 8 ms boundary; no wake restarts an 8 ms relative timeout.
        for (int i = 0; i < 4_000; i++)
        {
            long now = start + (deadline - start) * i / 4_000;
            int remaining = PacedInputWaitClassifier.RemainingMilliseconds(deadline, now);
            Assert.InRange(remaining, 1, previous);
            previous = remaining;
        }
        Assert.Equal(0, PacedInputWaitClassifier.RemainingMilliseconds(deadline, deadline));
        Assert.Equal(0, PacedInputWaitClassifier.RemainingMilliseconds(deadline, deadline + Stopwatch.Frequency));
    }
}
