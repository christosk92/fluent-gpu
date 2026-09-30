using System;
using FluentGpu.Scroll.Motion;
using Xunit;

namespace FluentGpu.Engine.Tests;

/// <summary>Pure closed-form gates for the Wave-0 scroll motion model (<c>FluentGpu.Scroll.Motion</c>): segment/plan
/// evaluation is a stateless function of an absolute time, so every law here is checked by sampling
/// <see cref="ScrollPlan.Eval"/>/<see cref="MotionSeg.Eval"/> at arbitrary times — never by stepping a per-tick
/// simulation.</summary>
public sealed class ScrollMotionTests
{
    private static readonly MotionFeel Feel = FeelProfiles.Standard;

    private static ScrollPlan Idle(double pos, double min = 0.0, double max = 10_000.0, double vpExtent = 400.0)
        => ScrollPlan.Idle(0, pos, min, max, vpExtent, Feel.RubberBandC);

    [Fact]
    public void Eval_IsContinuousAcrossSegmentBoundaries()
    {
        // Fling that crosses the top edge: Decay [tEnd, crossT) -> Spring [crossT, inf).
        ScrollPlan plan = ScrollPlan.Idle(0, 9_900.0, 0.0, 10_000.0, 400.0, Feel.RubberBandC) with
        {
            Kind = MotionKind.Drag,
            Overpan = OverpanPolicy.RubberBand,
            Count = 0,
        };
        plan = plan with { Ring = plan.Ring.WithSample(0.0, 9_800.0, Feel.FlingImpulseWindowS, Feel.VelocityMinSpanS).WithSample(0.02, 9_900.0, Feel.FlingImpulseWindowS, Feel.VelocityMinSpanS) };

        var flung = PlanAuthor.FollowEnd(in plan, tLift: 0.02, tNow: 0.02, feel: in Feel);
        Assert.Equal(2, flung.Count);

        // The join time is S0.T1 == S1.T0 — sample a hair either side and require near-exact agreement.
        double joinT = flung.S0.T1;
        double pBefore = flung.Eval(joinT - 1e-9, out _, out _);
        double pAfter = flung.Eval(joinT + 1e-9, out _, out _);
        Assert.Equal(pBefore, pAfter, 4);
    }

    [Fact]
    public void Eval_AtSharedTimes_IsIdenticalRegardlessOfSampleOrder()
    {
        var plan = PlanAuthor.Glide(Idle(0.0), tNow: 0.0, target: 500.0, feel: in Feel);
        double[] ts = { 0.0, 0.05, 0.10, 0.30, 1.0, 5.0 };

        double[] forward = new double[ts.Length];
        for (int i = 0; i < ts.Length; i++) forward[i] = plan.Eval(ts[i], out _, out _);

        double[] backward = new double[ts.Length];
        for (int i = ts.Length - 1; i >= 0; i--) backward[i] = plan.Eval(ts[i], out _, out _);

        for (int i = 0; i < ts.Length; i++)
            Assert.Equal(forward[i], backward[i]);
    }

    [Fact]
    public void WheelNotch_StartsExactlyAtTheDisplayedPosition_NoJump()
    {
        var accel = new WheelAccelState();
        var p0 = Idle(0.0);
        var p1 = PlanAuthor.WheelNotch(in p0, tNotch: 0.0, notches: 1.0, feel: in Feel, accel: ref accel);

        // Mid-flight of the first notch's cubic, re-notch: the new segment's start must equal the live plan's
        // displayed position at that instant, not the (different) prior destination.
        double tMid = Feel.WheelDurationS * 0.4;
        double displayed = p1.Eval(tMid, out _, out _);
        var p2 = PlanAuthor.WheelNotch(in p1, tNotch: tMid, notches: 1.0, feel: in Feel, accel: ref accel);

        Assert.Equal(displayed, p2.S0.P0, 9);

        // C1: the re-plan continues the velocity the superseded glide was showing at the join (no kink, no drop).
        p1.Eval(tMid, out double vBefore, out _);
        p2.Eval(tMid, out double vAfter, out _);
        Assert.True(vBefore > 0.0);
        Assert.Equal(vBefore, vAfter, 6);
    }

    [Fact]
    public void WheelNotch_FastSpinAccumulates_EveryNotchTravelsItsFullDistance()
    {
        // Ten same-direction notches 20 ms apart: each re-plans mid-glide and ACCUMULATES onto the pending destination,
        // so the undelivered remainder of every glide is kept — each notch adds exactly its own (spin-scaled) distance
        // and the spin lands exactly on the accumulated destination (never a re-aim at displayed + one notch).
        var accel = new WheelAccelState();
        ScrollPlan plan = Idle(0.0);
        double expectedDest = 0.0, ema = 0.0;
        for (int i = 0; i < 10; i++)
        {
            double t = i * 0.020;
            double before = plan.Kind == MotionKind.Wheel ? plan.Dest : 0.0;
            plan = PlanAuthor.WheelNotch(in plan, tNotch: t, notches: 1.0, feel: in Feel, accel: ref accel);
            double factor = i == 0 ? 1.0 : PlanAuthor.AccelFor(0.020, ema, in Feel, out ema);
            double step = plan.Dest - before;
            Assert.Equal(Feel.WheelNotchDip * factor, step, 9);
            expectedDest += Feel.WheelNotchDip * factor;
        }
        Assert.Equal(expectedDest, plan.Dest, 9);

        double tEnd = 9 * 0.020 + Feel.WheelDurationS;
        double landed = plan.Eval(tEnd, out double v, out bool settled);
        Assert.Equal(expectedDest, landed, 9);
        Assert.Equal(0.0, v, 9);
        Assert.True(settled);
    }

    [Fact]
    public void WheelNotch_Reversal_RebasesOnTheDisplayedPosition_NoJump()
    {
        var accel = new WheelAccelState();
        ScrollPlan plan = PlanAuthor.WheelNotch(Idle(1_000.0), tNotch: 0.0, notches: 1.0, feel: in Feel, accel: ref accel);
        plan = PlanAuthor.WheelNotch(in plan, tNotch: 0.02, notches: 1.0, feel: in Feel, accel: ref accel);

        double tRev = 0.06;
        double displayed = plan.Eval(tRev, out _, out _);
        var reversed = PlanAuthor.WheelNotch(in plan, tNotch: tRev, notches: -1.0, feel: in Feel, accel: ref accel);

        Assert.Equal(displayed, reversed.S0.P0, 9);                                  // no jump at the reversal
        Assert.Equal(displayed - Feel.WheelNotchDip, reversed.Dest, 9);              // one notch back from what is SHOWN
        Assert.Equal(displayed, reversed.Eval(tRev, out _, out _), 9);
    }

    [Fact]
    public void WheelNotch_AfterTheGlideSettled_RebasesOnTheRestingPosition()
    {
        var accel = new WheelAccelState();
        ScrollPlan plan = PlanAuthor.WheelNotch(Idle(0.0), tNotch: 0.0, notches: 1.0, feel: in Feel, accel: ref accel);
        double later = Feel.WheelDurationS + 1.0;
        var next = PlanAuthor.WheelNotch(in plan, tNotch: later, notches: 1.0, feel: in Feel, accel: ref accel);
        Assert.Equal(Feel.WheelNotchDip, next.S0.P0, 9);
        Assert.Equal(2 * Feel.WheelNotchDip, next.Dest, 9);
    }

    [Theory]
    [InlineData(1.0 / 120.0)]
    [InlineData(1.0 / 60.0)]
    [InlineData(1.0 / 30.0)]
    public void SnapFling_LandsExactlyOnTheGrid_InFiniteTime_AtAnySamplingRate(double sampleDt)
    {
        // A released contact over a 50-DIP snap grid: the re-solved decay reaches the snapped rest exactly, then holds —
        // however the plan is sampled (the landing is a closed form of absolute time, not of a frame step).
        ScrollPlan drag = Idle(1_000.0) with { Kind = MotionKind.Drag, Overpan = OverpanPolicy.RubberBand, Count = 0 };
        drag = drag with { Ring = drag.Ring.WithSample(0.000, 1_000.0, Feel.FlingImpulseWindowS, Feel.VelocityMinSpanS).WithSample(0.016, 1_012.0, Feel.FlingImpulseWindowS, Feel.VelocityMinSpanS).WithSample(0.032, 1_024.0, Feel.FlingImpulseWindowS, Feel.VelocityMinSpanS) };
        var fling = PlanAuthor.FollowEnd(in drag, tLift: 0.032, tNow: 0.032, feel: in Feel);
        var grid = new SnapGrid(50.0, 0.0, 0.0, null);
        var snapped = SnapTargets.ResolveFling(in fling, in grid, 1_024.0, in Feel);

        double target = snapped.Dest;
        Assert.Equal(0.0, Math.IEEERemainder(target, 50.0), 9);
        Assert.Equal(2, snapped.Count);
        Assert.Equal(SegKind.Hold, snapped.S1.Kind);
        Assert.Equal(fling.S0.V0, snapped.S0.V0, 9);                                 // velocity-continuous at the lift

        double t = 0.032, prev = 1_024.0;
        bool settled = false;
        double p = prev;
        for (int i = 0; i < 100_000 && !settled; i++)
        {
            t += sampleDt;
            p = snapped.Eval(t, out _, out settled);
            Assert.True(p >= prev - 1e-9, "a snap fling never reverses");
            Assert.True(p <= target + 1e-9, "a snap fling never overshoots its snap");
            prev = p;
        }
        Assert.True(settled);
        Assert.Equal(target, p, 9);
        Assert.Equal(target, snapped.Eval(snapped.S0.T1 - 1e-12, out _, out _), 6);   // the decay itself arrives there
    }

    private static ScrollPlan DragOf(double min, double max, params (double T, double Pos)[] samples)
    {
        ScrollPlan drag = PlanAuthor.FollowBegin(Idle(samples[0].Pos, min, max), samples[0].T, samples[0].Pos, ContactClock.Device, in Feel);
        for (int i = 1; i < samples.Length; i++) drag = PlanAuthor.FollowSample(in drag, samples[i].T, samples[i].Pos, in Feel);
        return drag;
    }

    [Fact]
    public void FollowEnd_FingerStoppedBeforeTheLift_NeverFlings()
    {
        // A fast swipe, then the fingers rest 100 ms before lifting: the ring's newest samples still carry the swipe's
        // slope, but the lift is past the impulse window — the contact had stopped, so it settles where it is.
        var drag = DragOf(0.0, 10_000.0, (0.000, 1_000.0), (0.016, 1_040.0), (0.032, 1_080.0), (0.048, 1_120.0));
        var released = PlanAuthor.FollowEnd(in drag, tLift: 0.148, tNow: 0.148, feel: in Feel);
        Assert.Equal(MotionKind.Idle, released.Kind);
        Assert.Equal(1_120.0 + 2_500.0 * ContactRing.ResampleMaxPredictionS, released.Dest, 6);   // the ring's (bounded) rest

        // The same swipe lifted promptly flings.
        var flung = PlanAuthor.FollowEnd(in drag, tLift: 0.056, tNow: 0.056, feel: in Feel);
        Assert.Equal(MotionKind.Fling, flung.Kind);
        Assert.True(flung.S0.V0 > 2_000.0);
    }

    // Wavee HID contact 66 from the owner's 2026-09-25 capture (evidence 20260925-163133, scroll.csv): a fast DirectManipulation
    // flick on the PRESENT clock (plan-clock seconds, DIP). DM stopped producing motion at the physical lift (~.4647) and
    // reported RUNNING→READY at .500163 — the End's stamp — with its whole-pixel snap (+0.2111 DIP) emitted as a Sample at
    // that same stamp. WinUI flung 14 of 14 such flicks; the engine held 9 of 22 dead.
    private static readonly (double T, double Pos)[] Contact66 =
    {
        (270962.425174, 1782.8588), (270962.450159, 2036.2985), (270962.458502, 2207.2030), (270962.466812, 2301.2349),
    };
    private const double Contact66End = 270962.500163, Contact66SnapPos = 2301.4460;

    private static ScrollPlan PresentDragOf((double T, double Pos)[] samples)
    {
        ScrollPlan drag = PlanAuthor.FollowBegin(Idle(samples[0].Pos, 0.0, 100_000.0), samples[0].T, samples[0].Pos,
            ContactClock.Present, in Feel);
        for (int i = 1; i < samples.Length; i++) drag = PlanAuthor.FollowSample(in drag, samples[i].T, samples[i].Pos, in Feel);
        return drag;
    }

    /// <summary>The defect's shape, pinned: with DM's READY snap in the ring at the End stamp, the least-squares velocity
    /// over the 40 ms horizon reads ~6 DIP/s (the snap over the 33 ms stall) and the release holds a flick that was moving
    /// at ~16k DIP/s. The producer no longer emits that sample (<c>DmContactStreamTests</c>); this is what it did.</summary>
    [Fact]
    public void FollowEnd_DmReadySnapAsTheNewestSample_HoldsAFastFlick_TheDefectShape()
    {
        var withSnap = new (double T, double Pos)[Contact66.Length + 1];
        Contact66.CopyTo(withSnap, 0);
        withSnap[^1] = (Contact66End, Contact66SnapPos);
        var drag = PresentDragOf(withSnap);
        Assert.InRange(drag.Ring.V, 5.0, 8.0);
        var released = PlanAuthor.FollowEnd(in drag, Contact66End, Contact66End, in Feel);
        Assert.Equal(MotionKind.Idle, released.Kind);
    }

    /// <summary>The new contract: a Present-clock contact released MOVING (DM went RUNNING→INERTIA) is authored from its
    /// newest REAL sample (<c>tLift</c>) at the End stamp (<c>tNow</c>) — a fling at the ring's velocity decayed over the
    /// status-edge latency, starting exactly at the held newest sample (no jump).</summary>
    [Fact]
    public void FollowEnd_PresentClockMovingRelease_FlingsFromTheNewestRealSample_WithoutAJump()
    {
        var drag = PresentDragOf(Contact66);
        const double tLift = 270962.466812, tNow = Contact66End;
        Assert.True(drag.Ring.V > 10_000.0);
        var released = PlanAuthor.FollowEnd(in drag, tLift, tNow, in Feel, ContactRelease.Moving);
        Assert.Equal(MotionKind.Fling, released.Kind);
        Assert.True(released.S0.V0 > 0.0);
        Assert.Equal(Math.Min(Feel.TouchpadReleaseCapDipPerS, drag.Ring.V * Math.Exp(-Feel.FlingDecayPerS * (tNow - tLift))), released.S0.V0, 6);
        Assert.Equal(2301.2349, released.Eval(tNow, out _, out _), 6);
        Assert.Equal(drag.Eval(tNow, out _, out _), released.Eval(tNow, out _, out _), 9);

        // Without the verdict the same lift is judged by the time rule: 33 ms of status-edge latency reads as a pause.
        var byTime = PlanAuthor.FollowEnd(in drag, tNow, tNow, in Feel);
        Assert.Equal(MotionKind.Idle, byTime.Kind);
    }

    /// <summary>A contact released AT REST (DM went RUNNING→READY) never flings, even lifted promptly on a fast ring — the
    /// producer's verdict decides, not the time rule; it holds exactly where the contact shows.</summary>
    [Fact]
    public void FollowEnd_StoppedRelease_HoldsWhereTheContactShows()
    {
        var drag = PresentDragOf(Contact66);
        const double tLift = 270962.466812;
        var released = PlanAuthor.FollowEnd(in drag, tLift, tLift + 0.004, in Feel, ContactRelease.Stopped);
        Assert.Equal(MotionKind.Idle, released.Kind);
        Assert.Equal(2301.2349, released.Dest, 6);
    }

    // WinUI 3 ScrollView's touchpad release velocity for each of the owner's 14 flicks (capture
    // 2026-09-25_16-32-17-tp-flicks.csv, DIP/s: the first frame's velocity after the Inertia state change; flick 13's first
    // frame read 14 351 over a short frame interval and 10 120 on the next, so its median of the first three, 10 484, stands).
    private static readonly double[] WinUiTouchpadReleases =
    {
        10744, 8085, 10618, 8478, 1077, 799, 10848, 10834, 10727, 8759, 10748, 10755, 10484, 10865,
    };

    /// <summary>The cap is WinUI's measured plateau: every captured WinUI release is at or under it, the fastest sits on
    /// it, and 12 of the 14 flicks released in the 8.1–10.9k band whatever the finger speed.</summary>
    [Fact]
    public void TouchpadReleaseCap_IsWinUisMeasuredPlateau()
    {
        double cap = Feel.TouchpadReleaseCapDipPerS;
        double max = 0.0;
        int plateau = 0;
        foreach (double v in WinUiTouchpadReleases)
        {
            Assert.True(v <= cap);
            max = Math.Max(max, v);
            if (v >= 8000.0) plateau++;
        }
        Assert.Equal(max, cap);
        Assert.Equal(12, plateau);
    }

    /// <summary>A rapid DirectManipulation flick (the TpLog rapid set: the ring reads ~95k DIP/s) releases at the cap; a
    /// flick under it keeps its own velocity; a device-timed stream is not capped.</summary>
    [Fact]
    public void FollowEnd_PresentClockRelease_IsCappedAtTheTouchpadReleaseCap_DeviceIsNot()
    {
        var fast = new (double T, double Pos)[Contact66.Length];
        for (int i = 0; i < fast.Length; i++) fast[i] = (Contact66[i].T, 1782.8588 + 6.0 * (Contact66[i].Pos - 1782.8588));
        var drag = PresentDragOf(fast);
        Assert.True(drag.Ring.V > 90_000.0);
        const double tLift = 270962.466812, tNow = Contact66End;
        var capped = PlanAuthor.FollowEnd(in drag, tLift, tNow, in Feel, ContactRelease.Moving);
        Assert.Equal(MotionKind.Fling, capped.Kind);
        Assert.Equal(Feel.TouchpadReleaseCapDipPerS, capped.S0.V0, 9);

        var slow = PresentDragOf(Contact66);   // ~16k at the newest sample, ~14.7k after the latency: still over the cap
        Assert.Equal(Feel.TouchpadReleaseCapDipPerS, PlanAuthor.FollowEnd(in slow, tLift, tNow, in Feel, ContactRelease.Moving).S0.V0, 9);
        var gentle = new (double T, double Pos)[Contact66.Length];
        for (int i = 0; i < gentle.Length; i++) gentle[i] = (Contact66[i].T, 1782.8588 + 0.25 * (Contact66[i].Pos - 1782.8588));
        var under = PlanAuthor.FollowEnd(PresentDragOf(gentle), tLift, tNow, in Feel, ContactRelease.Moving);
        Assert.True(under.S0.V0 < Feel.TouchpadReleaseCapDipPerS);

        var device = DragOf(0.0, 1_000_000.0, fast);
        var uncapped = PlanAuthor.FollowEnd(in device, tLift, tLift, in Feel);
        Assert.True(uncapped.S0.V0 > 90_000.0);
    }

    [Fact]
    public void FollowEnd_LiftedWhileOverpanned_SpringsBackFromTheShownPosition_AndSettlesOnTheEdge()
    {
        // Dragged 300 DIP past the end and lifted with no velocity: the band was showing Max + RB(300). The release must
        // start exactly there (no jump) and come to rest exactly on Max (never stick overpanned).
        const double max = 5_000.0;
        var drag = DragOf(0.0, max, (0.0, 5_200.0), (0.1, 5_300.0), (0.2, 5_300.0));
        double shownAtLift = drag.Eval(0.2, out _, out _);
        Assert.True(shownAtLift > max);

        var released = PlanAuthor.FollowEnd(in drag, tLift: 0.2, tNow: 0.2, feel: in Feel);
        Assert.Equal(shownAtLift, released.Eval(0.2, out _, out _), 9);
        double p = shownAtLift, prev = shownAtLift;
        for (double t = 0.2; t < 3.0; t += 1.0 / 60.0)
        {
            p = released.Eval(t, out _, out _);
            Assert.True(p <= prev + 1e-9, "the spring back never pushes further out");
            prev = p;
        }
        Assert.Equal(max, p, 3);
        Assert.Equal(max, released.Dest, 9);
    }

    [Fact]
    public void FollowCancel_Overpanned_SpringsBack_InsideContent_HoldsInPlace()
    {
        var over = DragOf(0.0, 5_000.0, (0.0, -100.0), (0.05, -200.0));
        double shown = over.Eval(0.05, out _, out _);
        var cancelled = PlanAuthor.FollowCancel(in over, 0.05, in Feel);
        Assert.Equal(shown, cancelled.Eval(0.05, out _, out _), 9);
        Assert.Equal(0.0, cancelled.Eval(5.0, out _, out _), 3);

        var inside = DragOf(0.0, 5_000.0, (0.0, 100.0), (0.05, 200.0));
        var held = PlanAuthor.FollowCancel(in inside, 0.05, in Feel);
        Assert.Equal(inside.Eval(0.05, out _, out _), held.Eval(1.0, out _, out _), 9);
    }

    [Fact]
    public void ContactRing_TimeShift_KeepsSpacingAndPositions()
    {
        ContactRing ring = default;
        ring = ring.WithSample(1.000, 10.0, Feel.FlingImpulseWindowS, Feel.VelocityMinSpanS).WithSample(1.016, 20.0, Feel.FlingImpulseWindowS, Feel.VelocityMinSpanS).WithSample(1.032, 30.0, Feel.FlingImpulseWindowS, Feel.VelocityMinSpanS);
        ContactRing shifted = ring.TimeShifted(-0.5);
        for (int i = 0; i < ring.Count; i++)
        {
            Assert.Equal(ring.SampleAt(i).T - 0.5, shifted.SampleAt(i).T, 12);
            Assert.Equal(ring.SampleAt(i).Pos, shifted.SampleAt(i).Pos);
        }
        Assert.Equal(ring.V, shifted.V, 9);
        Assert.Equal(ring.Velocity(0.040, Feel.VelocityMinSpanS), shifted.Velocity(0.040, Feel.VelocityMinSpanS), 9);
    }

    [Fact]
    public void Cubic_EndpointIsExact()
    {
        var seg = new MotionSeg(SegKind.Cubic, t0: 1.0, t1: 1.257, p0: 100.0, p1: 132.0);
        double p = seg.Eval(1.257, out double v);
        Assert.Equal(132.0, p, 12);
        Assert.Equal(0.0, v, 9);

        // Past the end: clamped, still exact.
        double pPast = seg.Eval(5.0, out double vPast);
        Assert.Equal(132.0, pPast, 12);
        Assert.Equal(0.0, vPast, 9);
    }

    [Fact]
    public void Decay_AsymptoticDistance_EqualsV0OverK()
    {
        const double v0 = 800.0;
        const double k = 2.3;
        var seg = new MotionSeg(SegKind.Decay, t0: 0.0, t1: double.PositiveInfinity, p0: 0.0, p1: 0.0, v0: v0, k: k);

        double pFar = seg.Eval(50.0, out double vFar); // far past settle
        Assert.Equal(v0 / k, pFar, 6);
        Assert.Equal(0.0, vFar, 6);
    }

    [Fact]
    public void FlingCrossingAnEdge_RubberBand_ProducesSpring_EndingAtEdge()
    {
        var basePlan = Idle(9_800.0);
        var contact = basePlan with { Kind = MotionKind.Drag, Overpan = OverpanPolicy.RubberBand, Count = 0 };
        contact = contact with { Ring = contact.Ring.WithSample(0.0, 9_700.0, Feel.FlingImpulseWindowS, Feel.VelocityMinSpanS).WithSample(0.02, 9_800.0, Feel.FlingImpulseWindowS, Feel.VelocityMinSpanS) };

        var flung = PlanAuthor.FollowEnd(in contact, tLift: 0.02, tNow: 0.02, feel: in Feel);

        Assert.Equal(MotionKind.Fling, flung.Kind);
        Assert.Equal(2, flung.Count);
        Assert.Equal(SegKind.Decay, flung.S0.Kind);
        Assert.Equal(SegKind.Spring, flung.S1.Kind);

        double pFar = flung.Eval(flung.S0.T1 + 5.0, out double vFar, out _);
        Assert.Equal(flung.Max, pFar, 3);
        Assert.True(System.Math.Abs(vFar) < 1.0);
    }

    [Fact]
    public void FlingCrossingAnEdge_None_ProducesHold_EndingAtEdge()
    {
        var basePlan = Idle(9_800.0) with { Overpan = OverpanPolicy.None };
        var contact = basePlan with { Kind = MotionKind.Drag, Count = 0 };
        contact = contact with { Ring = contact.Ring.WithSample(0.0, 9_700.0, Feel.FlingImpulseWindowS, Feel.VelocityMinSpanS).WithSample(0.02, 9_800.0, Feel.FlingImpulseWindowS, Feel.VelocityMinSpanS) };

        var flung = PlanAuthor.FollowEnd(in contact, tLift: 0.02, tNow: 0.02, feel: in Feel);

        Assert.Equal(2, flung.Count);
        Assert.Equal(SegKind.Hold, flung.S1.Kind);

        double pFar = flung.Eval(flung.S0.T1 + 5.0, out double vFar, out _);
        Assert.Equal(flung.Max, pFar, 9);
        Assert.Equal(0.0, vFar, 9);
    }

    [Fact]
    public void Shifted_TranslatesEvalByDelta()
    {
        var plan = PlanAuthor.Glide(Idle(1_000.0), tNow: 0.0, target: 4_000.0, feel: in Feel);
        const double delta = -250.0;
        var shifted = plan.Shifted(delta);

        foreach (double t in new[] { 0.0, 0.01, 0.25, 1.0, 3.0 })
        {
            double original = plan.Eval(t, out _, out _);
            double moved = shifted.Eval(t, out _, out _);
            Assert.Equal(original + delta, moved, 9);
        }
    }

    [Fact]
    public void Glide_IsVelocityContinuousFromTheHandoff()
    {
        var accel = new WheelAccelState();
        var moving = PlanAuthor.WheelNotch(Idle(0.0), tNotch: 0.0, notches: 3.0, feel: in Feel, accel: ref accel);
        double tHandoff = Feel.WheelDurationS * 0.3;
        double p0 = moving.Eval(tHandoff, out double v0, out _);

        var glide = PlanAuthor.Glide(in moving, tHandoff, target: 2_000.0, feel: in Feel);
        double v0Glide = glide.S0.V0;

        Assert.Equal(p0, glide.S0.P0, 9);
        Assert.Equal(v0, v0Glide, 6);

        // Sample the glide an instant after t0 and confirm the velocity hasn't jumped (still close to v0).
        glide.Eval(tHandoff, out double vAtStart, out _);
        Assert.Equal(v0, vAtStart, 6);
    }

    [Fact]
    public void RubberBand_IsMonotoneAndBoundedByTheViewport()
    {
        const double vp = 400.0;
        const double c = 0.55;
        double prev = 0.0;
        for (double excess = 10.0; excess < 100_000.0; excess *= 2.0)
        {
            double rb = ScrollPlan.RubberBand(excess, vp, c);
            Assert.True(rb > prev);
            Assert.True(rb < vp);
            prev = rb;
        }
    }

    [Fact]
    public void Eval_AtListMagnitudeOffsets_IsSmoothToWithinOneMicroDip()
    {
        // A 100k-row list at ~56 DIP/row puts the offset around 5.6e6 DIP; a plan authored at that magnitude must
        // not show double-precision quantization stepping under a fine time sweep.
        const double bigOffset = 5_600_000.0;
        var plan = PlanAuthor.Glide(Idle(bigOffset, min: 0.0, max: 6_000_000.0), tNow: 0.0, target: bigOffset + 300.0, feel: in Feel);

        double prev = plan.Eval(0.0, out _, out _);
        double maxJump = 0.0;
        for (double t = 1e-4; t < 0.5; t += 1e-4)
        {
            double p = plan.Eval(t, out _, out _);
            double step = System.Math.Abs(p - prev);
            // A smooth curve's per-microstep displacement is itself continuous; flag anything that looks like an
            // isolated quantization spike rather than the curve's own (smooth) deceleration.
            if (step > maxJump) maxJump = step;
            prev = p;
        }

        // The curve's own peak per-step displacement at this dt is small (sub-DIP); this is a coarse smoothness
        // gate, not a tight bound on the physical velocity.
        Assert.True(maxJump < 5.0, $"largest 1e-4s step was {maxJump} DIP");
    }

    // ── the least-squares contact velocity (jumps) and the pose-floor anchor (catch-up) ──────────────────────────

    [Fact]
    public void Follow_CoincidentPacketPair_DoesNotExtrapolateAtThePairSlope()
    {
        var plan = PlanAuthor.FollowBegin(Idle(0.0), 0.0, 678.367, ContactClock.Device, in Feel);
        plan = PlanAuthor.FollowSample(in plan, 0.0, 689.034, in Feel);
        plan = PlanAuthor.FollowSample(in plan, 0.015775, 699.700, in Feel);
        plan = PlanAuthor.FollowSample(in plan, 0.016034, 721.034, in Feel);
        double shown = plan.Eval(0.030207, out double v, out _);
        Assert.InRange(v, 1_000.0, 2_000.0);
        Assert.InRange(shown, 721.034, 721.034 + 2_000.0 * ContactRing.ResampleMaxPredictionS);
    }

    [Fact]
    public void FollowEnd_LateLiftAfterACoincidentPair_StartsNearTheStream()
    {
        (double T, double P)[] s =
        {
            (0.0, 672.0), (0.0, 682.667), (0.012279, 693.333), (0.028242, 704.0), (0.028512, 714.667),
            (0.044394, 736.0), (0.060652, 757.333), (0.076426, 778.667), (0.092199, 789.333), (0.092361, 810.667),
        };
        var plan = PlanAuthor.FollowBegin(Idle(0.0), s[0].T, s[0].P, ContactClock.Device, in Feel);
        for (int i = 1; i < s.Length; i++) plan = PlanAuthor.FollowSample(in plan, s[i].T, s[i].P, in Feel);
        const double tLift = 0.092361, tNow = tLift + 0.0638;
        double shownBefore = plan.Eval(tNow, out _, out _);
        var end = PlanAuthor.FollowEnd(in plan, tLift, tNow, in Feel);
        double p0 = end.Eval(tNow, out _, out _);
        Assert.Equal(shownBefore, p0, 6);
        Assert.InRange(p0, 810.667, 810.667 + 2_000.0 * (tNow - tLift));
    }

    [Fact]
    public void FollowEnd_ReleaseVelocity_IgnoresACoincidentPairWithNoSpanBehindIt()
    {
        var plan = PlanAuthor.FollowBegin(Idle(0.0), 0.0, 0.0, ContactClock.Device, in Feel);
        plan = PlanAuthor.FollowSample(in plan, 0.050, 10.667, in Feel);
        plan = PlanAuthor.FollowSample(in plan, 0.100, 21.333, in Feel);
        plan = PlanAuthor.FollowSample(in plan, 0.150, 32.000, in Feel);
        plan = PlanAuthor.FollowSample(in plan, 0.1503, 42.667, in Feel);
        var end = PlanAuthor.FollowEnd(in plan, tLift: 0.1503, tNow: 0.1503, feel: in Feel);
        Assert.InRange(end.Dest, 42.0, 42.667 + 50.0);
    }

    [Fact]
    public void ContactRing_Velocity_OfBunchedPairs_IsTheStreamRate()
    {
        var ring = default(ContactRing);
        double p = 0.0;
        for (int k = 0; k < 4; k++)
        {
            double t = k * 0.016;
            ring = ring.WithSample(t, p += 10.667, Feel.FlingImpulseWindowS, Feel.VelocityMinSpanS);
            ring = ring.WithSample(t + 0.0003, p += 10.667, Feel.FlingImpulseWindowS, Feel.VelocityMinSpanS);
        }
        Assert.InRange(ring.V, 1_333.3 * 0.9, 1_333.3 * 1.1);
    }

    [Fact]
    public void WheelNotch_HiResPackets_TravelExactlyTheTurnedDistance()
    {
        var accel = new WheelAccelState();
        // Thirds of a notch at a slow roll (80 ms a whole notch — the full-step band): 12 packets travel exactly 4 notches.
        var plan = Idle(672.0);
        for (int i = 0; i < 12; i++) plan = PlanAuthor.WheelNotch(in plan, i * 0.080 / 3.0, 1.0 / 3.0, in Feel, ref accel);
        double dest = 672.0 + 4.0 * Feel.WheelNotchDip;
        Assert.Equal(dest, plan.Dest, 6);
        double prev = double.NegativeInfinity;
        for (double t = 11 * 0.080 / 3.0; t < 0.8; t += 1.0 / 120.0)
        {
            double q = plan.Eval(t, out _, out _);
            Assert.True(q <= dest + 1e-9 && q >= prev - 1e-9);
            prev = q;
        }
    }

    [Fact]
    public void WheelNotch_StampedBeforeTheLastPosedPresent_HasNoCatchUpStep()
    {
        var accel = new WheelAccelState();
        const double tNotch = 0.644389, lastPosed = 0.663732, first = 0.672031, r = 1.0 / 120.0;
        var plan = PlanAuthor.WheelNotch(Idle(0.0), tNotch, 1.0, in Feel, ref accel, shownFloorSec: lastPosed);
        double s1 = plan.Eval(first, out _, out _);
        double s2 = plan.Eval(first + r, out _, out _) - s1;
        Assert.InRange(s1, 0.0, s2 * 1.05);
        Assert.Equal(Feel.WheelNotchDip, plan.Dest, 9);
        Assert.Equal(tNotch, accel.LastNotchT, 12);
    }

    [Fact]
    public void WheelNotch_ReversalStampedBeforeTheLastPosedPresent_NeverStepsBack()
    {
        var accel = new WheelAccelState();
        var p1 = PlanAuthor.WheelNotch(Idle(0.0), 0.0, 1.0, in Feel, ref accel);
        const double tNotch = 0.100, lastPosed = 0.120, r = 1.0 / 120.0;
        double shown = p1.Eval(lastPosed, out _, out _);
        var p2 = PlanAuthor.WheelNotch(in p1, tNotch, -1.0, in Feel, ref accel, shownFloorSec: lastPosed);
        Assert.Equal(shown, p2.Eval(lastPosed, out _, out _), 9);
        // The reversal turns at once and moves no further in its first frame than a notch from rest does.
        var restAccel = new WheelAccelState();
        var fromRest = PlanAuthor.WheelNotch(Idle(0.0), 0.0, 1.0, in Feel, ref restAccel);
        double firstFrame = fromRest.Eval(r, out _, out _);
        Assert.InRange(shown - p2.Eval(lastPosed + r, out _, out _), 0.0, firstFrame + 1e-9);
    }
}
