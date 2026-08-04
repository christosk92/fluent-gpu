using System.Diagnostics;
using FluentGpu.Pal.Windows;
using Xunit;

namespace FluentGpu.Windows.Tests;

/// <summary>Locks the pure decision seams of the DirectManipulation producer: the manual-update pacer, the wheel
/// arbitration, and the four stall/wedge detectors plus their shared recovery ladder.
///
/// <para>There is deliberately NO VerticalSlice gate for the stall recovery. The slice's transitive closure is
/// contractually TerraFX-free, and neither a DIRECTMANIPULATION_STATUS transition nor a DM_POINTERHITTEST can be
/// synthesized headlessly (<c>InjectSyntheticPointerInput</c> has no PT_TOUCHPAD source). These pure-helper tests plus
/// the ScrollTrace note-107 telemetry acceptance in the plan are the substitute: a normal touchpad session must produce
/// zero 107 rows, and any recurrence must self-recover in ~5s with 107 rows naming detector, rungs and revival.</para></summary>
public sealed class DirectManipulationPacingTests
{
    [Fact]
    public void MessageStorm_IsBoundedByAbsoluteDeadline()
    {
        var pacer = new DmManualUpdatePacer();
        pacer.ArmImmediate(0);

        int updates = 0;
        const int wakesPerSecond = 20_000;
        for (int i = 0; i <= wakesPerSecond; i++)
        {
            long now = (long)(i * (Stopwatch.Frequency / (double)wakesPerSecond));
            if (pacer.TryConsume(now)) updates++;
        }

        Assert.InRange(updates, 142, 144);
    }

    [Fact]
    public void EarlyWakes_DoNotSlideDeadline_AndIdlePreservesHostWait()
    {
        var pacer = new DmManualUpdatePacer();
        long start = Stopwatch.Frequency;
        pacer.ArmImmediate(start);
        Assert.True(pacer.TryConsume(start));

        long oneMsLater = start + Stopwatch.Frequency / 1_000;
        int first = pacer.ClampWait(100, oneMsLater);
        for (int i = 0; i < 100; i++)
            Assert.Equal(first, pacer.ClampWait(100, oneMsLater));
        Assert.InRange(first, 5, 7);
        Assert.Equal(first, pacer.ClampWait(-1, oneMsLater));

        pacer.Disarm();
        Assert.Equal(-1, pacer.ClampWait(-1, oneMsLater));
        Assert.Equal(100, pacer.ClampWait(100, oneMsLater));
    }

    [Fact]
    public void LongGap_AdvancesOnce_WithoutCatchUpBurst()
    {
        var pacer = new DmManualUpdatePacer();
        pacer.ArmImmediate(0);
        Assert.True(pacer.TryConsume(0));

        long afterGap = Stopwatch.Frequency / 10; // 100 ms
        Assert.True(pacer.TryConsume(afterGap));
        Assert.False(pacer.TryConsume(afterGap));
        Assert.InRange(pacer.ClampWait(-1, afterGap), 1, DmManualUpdatePacer.IntervalMs);
    }

    /// <summary>The pump's interval FOLLOWS the panel. Its deadline clamps every host wait while a gesture is live, so a
    /// fixed 7 ms (the 120 Hz answer) truncates the loop's wait twice per refresh on a 60 Hz display and lands a whole
    /// refresh late at 240 — the same "one hardcoded rate" defect as the async pace cap. The default is unchanged, which
    /// is what keeps the tests above meaningful.</summary>
    [Fact]
    public void PacerInterval_FollowsConfiguredRefresh()
    {
        var pacer = new DmManualUpdatePacer();
        Assert.Equal(DmManualUpdatePacer.IntervalMs, pacer.IntervalMsInForce);   // default = the 120 Hz answer

        pacer.SetIntervalMs(1000.0 / 60);
        Assert.Equal(17, pacer.IntervalMsInForce);
        pacer.SetIntervalMs(1000.0 / 144);
        Assert.Equal(7, pacer.IntervalMsInForce);

        // ...and the cadence actually changes: at ~16.7 ms a second's worth of wakes yields ~60 updates, not ~143.
        var slow = new DmManualUpdatePacer();
        slow.SetIntervalMs(1000.0 / 60);
        slow.ArmImmediate(0);
        int updates = 0;
        const int wakesPerSecond = 20_000;
        for (int i = 0; i <= wakesPerSecond; i++)
        {
            long now = (long)(i * (Stopwatch.Frequency / (double)wakesPerSecond));
            if (slow.TryConsume(now)) updates++;
        }
        Assert.InRange(updates, 58, 61);
    }

    /// <summary>The clamp is the part that must not drift: it is what an absurd or unknown refresh lands on. Below the
    /// floor the pump would poll; above the ceiling it would truncate host waits it has no business truncating. A
    /// non-positive value is "unknown" and must leave the current interval alone rather than resolve to a clamp bound.</summary>
    [Fact]
    public void PacerInterval_ClampsToFloorAndCeiling()
    {
        var pacer = new DmManualUpdatePacer();

        pacer.SetIntervalMs(1000.0 / 480);   // 2.08 ms ⇒ floored
        Assert.Equal(3, pacer.IntervalMsInForce);
        pacer.SetIntervalMs(0.5);
        Assert.Equal(3, pacer.IntervalMsInForce);

        pacer.SetIntervalMs(1000.0 / 24);    // 41.7 ms ⇒ ceilinged
        Assert.Equal(17, pacer.IntervalMsInForce);
        pacer.SetIntervalMs(1000.0);
        Assert.Equal(17, pacer.IntervalMsInForce);

        // Unknown refresh: keep whatever is in force (here the 17 ms ceiling from above), never reset to a bound.
        pacer.SetIntervalMs(0.0);
        Assert.Equal(17, pacer.IntervalMsInForce);
        pacer.SetIntervalMs(-1.0);
        Assert.Equal(17, pacer.IntervalMsInForce);
    }

    [Fact]
    public void StalledLiveGesture_TripsTheInertiaWatchdog_ButIdleAndProgressingOnesDoNot()
    {
        const long t0 = 10_000;
        long timeout = Win32DirectManipulation.DmInertiaStallTimeoutMs;

        // A live gesture (RUNNING or INERTIA) that produced nothing for longer than the timeout is stuck: this is the
        // coast whose fling target a navigation unmounted, which never reaches READY on its own. UpdateIfDue's caller
        // then issues Viewport.Stop(), whose READY callback runs the ordinary terminal path (clearing GestureLive and
        // with it NeedsClockTick, which is what releases the host wait from its due-now 0 clamp).
        Assert.True(DmInertiaStall.IsStalled(gestureLive: true, nowMs: t0 + timeout + 1, lastProgressMs: t0));

        // Boundary: exactly at the timeout is NOT yet stalled (strict >), matching the engage wedge watchdog's shape.
        Assert.False(DmInertiaStall.IsStalled(gestureLive: true, nowMs: t0 + timeout, lastProgressMs: t0));

        // A gesture that is still producing content deltas / status changes is never stalled, however long it coasts.
        Assert.False(DmInertiaStall.IsStalled(gestureLive: true, nowMs: t0 + 8, lastProgressMs: t0));

        // Idle DM (READY between gestures) is never "stalled" no matter how old the last progress stamp is — it holds
        // nothing awake, and Stopping it would be a spurious COM call on every pump.
        Assert.False(DmInertiaStall.IsStalled(gestureLive: false, nowMs: t0 + 10 * timeout, lastProgressMs: t0));
    }

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
    public void DueNowClamp_NeverRepeatsWithoutAnInterveningConsume()
    {
        var pacer = new DmManualUpdatePacer();
        long now = Stopwatch.Frequency;   // arbitrary non-zero epoch
        pacer.ArmImmediate(now);

        // The healthy loop: a due-now 0 wait returns immediately, the pump drains, UpdateIfDue consumes the slot — which
        // advances the absolute deadline, so the next clamp is a real wait again. The run never exceeds 1.
        for (int frame = 0; frame < 50; frame++)
        {
            Assert.Equal(0, pacer.ClampWait(-1, now));
            Assert.True(pacer.ZeroWaitRun <= 1);
            Assert.True(pacer.TryConsume(now));
            Assert.Equal(0, pacer.ZeroWaitRun);
            Assert.True(pacer.ClampWait(-1, now) > 0);   // the consumed slot moved the deadline into the future
            now += Stopwatch.Frequency / 100;            // 10 ms — past the 7 ms interval on any tick frequency
        }

        // The pathology the tripwire names: clamps that keep returning 0 with no consume in between. That is the host
        // polling MsgWaitForMultipleObjectsEx at 0 ms — the 1000-3300 it/s free-run — and the counter climbs with it.
        var stuck = new DmManualUpdatePacer();
        stuck.ArmImmediate(0);
        for (int i = 1; i <= 5; i++)
        {
            Assert.Equal(0, stuck.ClampWait(-1, Stopwatch.Frequency));
            Assert.Equal(i, stuck.ZeroWaitRun);
        }
        Assert.True(DmManualUpdatePacer.MaxZeroWaitRun >= 5);   // surfaced as diag dm.pacer.zeroWaitRun

        // Disarming (the terminal path a Stop() ultimately reaches) clears the run along with the deadline.
        stuck.Disarm();
        Assert.Equal(0, stuck.ZeroWaitRun);
    }

    /// <summary>The idle heartbeat's cadence decision. Manual-update DM surfaces its queue ONLY inside an
    /// <c>UpdateManager.Update</c>, and before the heartbeat that call was issued only while the pacer was armed — so a
    /// DM that still owned a contact stream while our status read READY sat on its queue for seconds (the live
    /// 1.5-4 s input blackouts). Inclusive on the period: a strictly-greater test drifts one pump later every beat.</summary>
    [Fact]
    public void IdleHeartbeat_FiresOnThePeriod_AndNotBefore()
    {
        const long t0 = 100_000;
        long period = Win32DirectManipulation.IdleHeartbeatMs;

        Assert.False(DmIdleHeartbeat.Due(t0, t0));
        Assert.False(DmIdleHeartbeat.Due(t0 + period - 1, t0));
        Assert.True(DmIdleHeartbeat.Due(t0 + period, t0));          // boundary is inclusive — no per-beat drift
        Assert.True(DmIdleHeartbeat.Due(t0 + 10 * period, t0));     // a long idle beats once, not ten times (one stamp)

        // Never beaten (stamp 0) is always due: TickCount64 is machine uptime, so the first idle pump beats and then
        // settles onto the period.
        Assert.True(DmIdleHeartbeat.Due(t0, 0));

        // The note-109 rate limit shares the shape but keeps its own period + its own stamp.
        Assert.False(DmIdleHeartbeat.DueEvery(t0 + Win32DirectManipulation.HitTestNoteMinGapMs - 1, t0,
            Win32DirectManipulation.HitTestNoteMinGapMs));
        Assert.True(DmIdleHeartbeat.DueEvery(t0 + Win32DirectManipulation.HitTestNoteMinGapMs, t0,
            Win32DirectManipulation.HitTestNoteMinGapMs));
        Assert.True(DmIdleHeartbeat.DueEvery(t0, 0, Win32DirectManipulation.HitTestNoteMinGapMs));
    }

    /// <summary>The heartbeat must reach ONLY the disarmed branch. <c>UpdateIfDue</c> skips its update in exactly three
    /// states — torn-down/disabled, pacer disarmed (<c>!NeedsClockTick</c>), and armed-but-not-due — and the heartbeat
    /// belongs to the middle one alone: a disabled producer may not be called at all, and an armed pacer already
    /// guarantees an update within one display interval, so beating there would double-pump a live gesture. This pins
    /// the branch predicate the way the file expresses it (<c>NeedsClockTick = _enabled &amp;&amp; (awaitingEngage ||
    /// RUNNING || INERTIA)</c>) so a future edit to the pacer arming cannot silently move the heartbeat.</summary>
    [Fact]
    public void IdleHeartbeat_BranchIsReachedOnlyWhileThePacerIsDisarmed()
    {
        // Disarmed = idle DM: no engage pending and a non-live status. This is the state that produced the blackout —
        // and precisely the state whose _status can be READY while DM still owns a contact stream nobody pumps.
        foreach (int idle in new[] { Win32DirectManipulation.DM_READY, Win32DirectManipulation.DM_ENABLED,
                                     Win32DirectManipulation.DM_BUILDING, Win32DirectManipulation.DM_SUSPENDED,
                                     Win32DirectManipulation.DM_DISABLED })
        {
            Assert.False(DmIdleHeartbeat.NeedsClockTick(enabled: true, awaitingEngage: false, idle));
            Assert.True(DmIdleHeartbeat.BeatsInsteadOfPacing(enabled: true, awaitingEngage: false, idle));
        }

        // Armed: the display-paced path owns these, and the heartbeat branch is unreachable — no double-pumping a live
        // gesture, and the armed pacer's absolute-deadline cadence is untouched.
        Assert.True(DmIdleHeartbeat.NeedsClockTick(enabled: true, awaitingEngage: true, Win32DirectManipulation.DM_READY));
        Assert.True(DmIdleHeartbeat.NeedsClockTick(enabled: true, awaitingEngage: false, Win32DirectManipulation.DM_RUNNING));
        Assert.True(DmIdleHeartbeat.NeedsClockTick(enabled: true, awaitingEngage: false, Win32DirectManipulation.DM_INERTIA));
        Assert.False(DmIdleHeartbeat.BeatsInsteadOfPacing(enabled: true, awaitingEngage: true, Win32DirectManipulation.DM_READY));
        Assert.False(DmIdleHeartbeat.BeatsInsteadOfPacing(enabled: true, awaitingEngage: false, Win32DirectManipulation.DM_RUNNING));
        Assert.False(DmIdleHeartbeat.BeatsInsteadOfPacing(enabled: true, awaitingEngage: false, Win32DirectManipulation.DM_INERTIA));

        // A disabled/torn-down producer reaches NEITHER branch — the heartbeat must never call into released COM.
        Assert.False(DmIdleHeartbeat.NeedsClockTick(enabled: false, awaitingEngage: true, Win32DirectManipulation.DM_RUNNING));
        Assert.False(DmIdleHeartbeat.BeatsInsteadOfPacing(enabled: false, awaitingEngage: true, Win32DirectManipulation.DM_RUNNING));
        Assert.False(DmIdleHeartbeat.BeatsInsteadOfPacing(enabled: false, awaitingEngage: false, Win32DirectManipulation.DM_READY));
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
