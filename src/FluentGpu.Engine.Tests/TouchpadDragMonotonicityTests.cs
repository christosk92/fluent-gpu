using System;
using System.Diagnostics;
using FluentGpu.Pal;
using FluentGpu.Scroll.Motion;
using FluentGpu.Scroll.Runtime;
using Xunit;

namespace FluentGpu.Engine.Tests;

/// <summary>A touchpad drag must never step the content back against an advancing finger. The failure these pin was a
/// DOUBLE prediction: DirectManipulation already composes each sample for the present it will show at, and the contact
/// ring then extrapolated past it again with a least-squares velocity that moves between frames — every drop of the
/// estimate moved the pose by <c>ΔV·lead</c> against the finger (−1.96 / −8.3 / −2.84 DIP in the three streams below).
///
/// <para>The DirectManipulation stream (<see cref="ContactClock.Present"/>) is modelled the way the producer now emits
/// it: each frame's sample is stamped with the present of the first render turn that sees it
/// (<see cref="ContactStamp.ForFrame"/>, the next tick's present), a frame whose content did not change emits NO sample,
/// and the plan interpolates between real samples and holds the newest (no look-ahead). The device-timed stream (the touchpad wheel
/// fallback, <see cref="ContactClock.Device"/>) keeps its packet stamps and is shown through Android's bounded
/// resampling.</para></summary>
public sealed class TouchpadDragMonotonicityTests
{
    static readonly MotionFeel Feel = FeelProfiles.Standard;
    const double Origin = 5000.0;

    static ScrollPlan Begin(double t, ContactClock clock)
        => PlanAuthor.FollowBegin(ScrollPlan.Idle(1, Origin, 0, 1e6, 800, Feel.RubberBandC), t, Origin, clock, in Feel);

    /// <summary>The DirectManipulation stream, stamped the way production stamps it: frame k's UI clock is
    /// <c>FrameQpc = t</c>, <c>PresentQpc = t + lead</c>, <c>RefreshQpc = frame</c>, and its sample carries
    /// <see cref="ContactStamp.ForFrame"/> — the NEXT tick's present, <c>t + lead + frame</c>; an unchanged position is no
    /// sample. The compositor poses every tick at that tick's present (<c>t + lead</c> — exactly the previous frame's
    /// stamp) and once more half a frame later (between two samples) — both must be monotone.</summary>
    static double WorstBackStep(double frameS, double leadS, Func<double, double> packetPos, Func<double, double> finger, double durS)
    {
        ScrollPlan plan = Begin(DmStampS(UiClock(0, frameS, leadS)), ContactClock.Present);
        double worst = 0, prev = double.NaN, lastPos = Origin;
        for (int k = 1; k * frameS <= durS; k++)
        {
            double t = k * frameS;
            FrameClock clock = UiClock(k, frameS, leadS);
            double present = clock.PresentQpc / (double)Stopwatch.Frequency;
            double pos = Origin + packetPos(t);
            if (pos != lastPos) { plan = PlanAuthor.FollowSample(in plan, DmStampS(clock), pos, in Feel); lastPos = pos; }
            bool advancing = finger(t) > finger(t - frameS);
            for (int tick = 0; tick < 2; tick++)
            {
                double pose = plan.Eval(present + tick * frameS * 0.5, out _, out _);
                if (!double.IsNaN(prev) && advancing) worst = Math.Max(worst, prev - pose);
                prev = pose;
            }
        }
        return worst;
    }

    /// <summary>Frame <paramref name="k"/>'s UI clock on the model's lattice (QPC ticks): tick <c>k·frame</c>, present
    /// <paramref name="leadS"/> after it, refresh one frame.</summary>
    static FrameClock UiClock(int k, double frameS, double leadS)
    {
        double f = Stopwatch.Frequency;
        long frameQpc = (long)Math.Round(k * frameS * f);
        return new FrameClock(frameQpc, frameQpc + (long)Math.Round(leadS * f), (long)Math.Round(frameS * f), frameQpc,
                              (ulong)k, FrameClockFlags.LatticeValid);
    }

    /// <summary>The production stamp of a sample produced on <paramref name="clock"/>'s frame, in plan seconds
    /// (<c>qpc / Stopwatch.Frequency</c> — the host's <c>ScrollFrameQpcToSec</c>).</summary>
    static double DmStampS(in FrameClock clock) => ContactStamp.ForFrame(in clock, clock.NowQpc) / (double)Stopwatch.Frequency;

    /// <summary>The device-timed fallback stream: packets every <paramref name="packetS"/> stamped at their own time
    /// (<paramref name="drop"/> suppresses one), delivered at the next frame and shown at <c>t + lead</c>.</summary>
    static double WorstBackStepDevice(double frameS, double leadS, double packetS, Func<double, double> finger, double durS,
                                      Func<double, bool>? drop = null)
    {
        ScrollPlan plan = Begin(0.0, ContactClock.Device);
        double worst = 0, prev = double.NaN;
        int next = 1;
        for (int k = 1; k * frameS <= durS; k++)
        {
            double t = k * frameS;
            for (; next * packetS <= t + 1e-12; next++)
            {
                double tp = next * packetS;
                if (drop is not null && drop(tp)) continue;
                plan = PlanAuthor.FollowSample(in plan, tp, Origin + finger(tp), in Feel);
            }
            double pose = plan.Eval(t + leadS, out _, out _);
            if (!double.IsNaN(prev) && finger(t) > finger(t - frameS)) worst = Math.Max(worst, prev - pose);
            prev = pose;
        }
        return worst;
    }

    static Func<double, double> Packets(Func<double, double> f, double periodS) => t => f(Math.Floor(t / periodS) * periodS);

    static double Decel(double t)
    {
        const double v0 = 1500, T = 0.08;
        return t < 0.2 ? v0 * t : v0 * 0.2 + v0 * (2 * T / Math.PI) * Math.Sin(Math.PI * Math.Min((t - 0.2) / T, 1) / 2);
    }

    [Fact]
    public void Steady_finger_144Hz_125Hz_packets_never_steps_back()
    {
        Func<double, double> f = t => 1000 * t;
        double worst = WorstBackStep(1 / 144.0, 0.0119, Packets(f, 0.008), f, 0.5);
        Assert.True(worst <= 0.5, $"worst back-step {worst:0.###} DIP");
    }

    [Fact]
    public void One_packetless_frame_at_60Hz_never_steps_back()
    {
        Func<double, double> f = t => 1000 * t;
        Func<double, double> dm = t => Math.Abs(t - 10 / 60.0) < 1e-9 ? f(9 / 60.0) : f(t);
        double worst = WorstBackStep(1 / 60.0, 0.031, dm, f, 0.4);
        Assert.True(worst <= 0.5, $"worst back-step {worst:0.###} DIP");
    }

    [Fact]
    public void Decelerating_finger_never_steps_back_while_advancing()
    {
        double worst = WorstBackStep(1 / 60.0, 0.031, Packets(Decel, 0.008), Decel, 0.45);
        Assert.True(worst <= 0.5, $"worst back-step {worst:0.###} DIP");
    }

    [Fact]
    public void Present_clock_shows_the_newest_sample_and_interpolates_between_samples()
    {
        ScrollPlan plan = Begin(0.010, ContactClock.Present);
        plan = PlanAuthor.FollowSample(in plan, 0.020, Origin + 10.0, in Feel);
        plan = PlanAuthor.FollowSample(in plan, 0.030, Origin + 30.0, in Feel);
        Assert.Equal(Origin + 20.0, plan.Eval(0.025, out _, out _), 9);           // between: interpolated
        Assert.Equal(Origin + 30.0, plan.Eval(0.030, out _, out _), 9);
        Assert.Equal(Origin + 30.0, plan.Eval(0.080, out double v, out _), 9);    // past the newest: held, never predicted
        Assert.True(v > 0.0);                                                     // the contact still reports its speed
    }

    // ── the device-timed fallback: Android's bounded resampling ─────────────────────────────────────────────────

    [Fact]
    public void Device_steady_finger_144Hz_125Hz_packets_never_steps_back()
    {
        double worst = WorstBackStepDevice(1 / 144.0, 0.0119, 0.008, t => 1000 * t, 0.5);
        Assert.True(worst <= 0.5, $"worst back-step {worst:0.###} DIP");
    }

    [Fact]
    public void Device_one_dropped_packet_at_60Hz_never_steps_back()
    {
        double worst = WorstBackStepDevice(1 / 60.0, 0.031, 1 / 60.0, t => 1000 * t, 0.4, drop: tp => Math.Abs(tp - 10 / 60.0) < 1e-9);
        Assert.True(worst <= 0.5, $"worst back-step {worst:0.###} DIP");
    }

    [Fact]
    public void Device_decelerating_finger_never_steps_back_while_advancing()
    {
        double worst = WorstBackStepDevice(1 / 60.0, 0.031, 0.008, Decel, 0.45);
        Assert.True(worst <= 0.5, $"worst back-step {worst:0.###} DIP");
    }

    [Fact]
    public void Device_prediction_is_bounded_to_half_the_gap_and_8ms_and_skipped_outside_2_to_20ms()
    {
        // 16 ms gap at 1000 DIP/s: at most min(8 ms, 8 ms) of prediction however late the present.
        ScrollPlan a = Begin(0.000, ContactClock.Device);
        a = PlanAuthor.FollowSample(in a, 0.016, Origin + 16.0, in Feel);
        Assert.Equal(Origin + 16.0 + 4.0, a.Eval(0.020, out _, out _), 9);
        Assert.Equal(Origin + 16.0 + 8.0, a.Eval(0.100, out _, out _), 9);

        // 6 ms gap: half the gap (3 ms) caps it.
        ScrollPlan b = Begin(0.000, ContactClock.Device);
        b = PlanAuthor.FollowSample(in b, 0.006, Origin + 6.0, in Feel);
        Assert.Equal(Origin + 6.0 + 3.0, b.Eval(0.050, out _, out _), 9);

        // A near-coincident pair (1 ms) and a slow stream (30 ms) are not predicted from: the newest sample holds.
        ScrollPlan c = Begin(0.000, ContactClock.Device);
        c = PlanAuthor.FollowSample(in c, 0.016, Origin + 16.0, in Feel);
        c = PlanAuthor.FollowSample(in c, 0.017, Origin + 30.0, in Feel);
        Assert.Equal(Origin + 30.0, c.Eval(0.040, out _, out _), 9);
        ScrollPlan d = Begin(0.000, ContactClock.Device);
        d = PlanAuthor.FollowSample(in d, 0.030, Origin + 30.0, in Feel);
        Assert.Equal(Origin + 30.0, d.Eval(0.060, out _, out _), 9);
    }

    // ── the lift: "fingers stationary" is a time rule, never a zero-delta sample ──────────────────────────────────

    static ScrollPlan DmSwipe(out double tLast)
    {
        // A 60 Hz DirectManipulation stream moving at 1500 DIP/s: one present-stamped sample per frame.
        const double frame = 1 / 60.0;
        ScrollPlan plan = Begin(frame, ContactClock.Present);
        tLast = 0;
        for (int k = 2; k <= 12; k++)
        {
            tLast = k * frame;
            plan = PlanAuthor.FollowSample(in plan, tLast, Origin + 1500.0 * (tLast - frame), in Feel);
        }
        return plan;
    }

    [Fact]
    public void Present_clock_lift_after_a_pause_does_not_fling()
    {
        // The fingers rest 50 ms (three frames with no content change — no samples) and lift: the contact had stopped.
        var drag = DmSwipe(out double tLast);
        double shown = drag.Eval(tLast + 0.050, out _, out _);
        var released = PlanAuthor.FollowEnd(in drag, tLast + 0.050, tLast + 0.050, in Feel);
        Assert.Equal(MotionKind.Idle, released.Kind);
        Assert.Equal(shown, released.Dest, 9);   // settles exactly where it was shown
    }

    [Fact]
    public void Present_clock_prompt_lift_flings_on_the_contact_velocity()
    {
        var drag = DmSwipe(out double tLast);
        var flung = PlanAuthor.FollowEnd(in drag, tLast + 1 / 60.0, tLast + 1 / 60.0, in Feel);
        Assert.Equal(MotionKind.Fling, flung.Kind);
        Assert.InRange(flung.S0.V0, 1400.0, 1600.0);
        Assert.Equal(drag.Eval(tLast + 1 / 60.0, out _, out _), flung.Eval(tLast + 1 / 60.0, out _, out _), 9);   // no jump at the lift
    }

    [Fact]
    public void StoppedAfter_is_two_report_periods_within_the_floor_and_the_velocity_horizon()
    {
        ContactRing r125 = default, r60 = default, r30 = default;
        for (int i = 0; i < 6; i++)
        {
            r125 = r125.WithSample(i * 0.008, i, Feel.FlingImpulseWindowS, Feel.VelocityMinSpanS);
            r60 = r60.WithSample(i / 60.0, i, Feel.FlingImpulseWindowS, Feel.VelocityMinSpanS);
            r30 = r30.WithSample(i / 30.0, i, Feel.FlingImpulseWindowS, Feel.VelocityMinSpanS);
        }
        Assert.Equal(PlanAuthor.ContactStoppedMinS, PlanAuthor.StoppedAfterS(in r125, in Feel), 9);   // 2×8 ms → the 20 ms floor
        Assert.Equal(2.0 / 60.0, PlanAuthor.StoppedAfterS(in r60, in Feel), 9);                       // two 60 Hz frames
        Assert.Equal(Feel.FlingImpulseWindowS, PlanAuthor.StoppedAfterS(in r30, in Feel), 9);         // capped at the horizon
    }
}
