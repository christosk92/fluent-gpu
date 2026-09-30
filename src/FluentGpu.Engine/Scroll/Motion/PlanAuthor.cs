using System;

namespace FluentGpu.Scroll.Motion;

/// <summary>Spin-rate acceleration state for a stream of same-direction wheel notches — pure data, no clock/dt
/// machinery. Carried by the caller (one instance per viewport) across <see cref="PlanAuthor.WheelNotch"/> calls.</summary>
public struct WheelAccelState
{
    /// <summary>The previous notch's own device timestamp (seconds) — compared only against another notch
    /// timestamp on the same clock, never against a frame clock.</summary>
    public double LastNotchT;

    /// <summary>Smoothed gap (seconds) between same-direction notches; 0 = no estimate yet.</summary>
    public double GapEmaS;

    /// <summary>Direction of the last notch (+1/−1; 0 = none) — a reversal resets the acceleration.</summary>
    public sbyte LastSign;
}

/// <summary>A step in a keyboard-driven scroll move (Wave-0 keyboard surface — arrows/page/home/end).</summary>
public enum KeyMove : byte
{
    LineUp,
    LineDown,
    PageUp,
    PageDown,
    Home,
    End,
}

/// <summary>Pure, stateless (aside from the caller-owned <see cref="WheelAccelState"/>) authoring statics: each
/// function takes the previous <see cref="ScrollPlan"/> and an absolute time and returns a brand-new plan — never
/// mutates its input, never integrates a per-tick <c>dt</c>. This is the ONLY place new plans are built; everything
/// else (poser, virtualizer) only calls <see cref="ScrollPlan.Eval"/>.</summary>
public static class PlanAuthor
{
    /// <summary>The spin-rate multiplier for a notch arriving <paramref name="gapS"/> after the previous
    /// same-direction one, given the running estimate <paramref name="emaS"/> (0 = none): a pure function of notch
    /// timestamps. With <c>ema</c> the updated gap EMA it is
    /// <c>clamp(max(AccelRefGapS/ema, min(1, ema/AccelUnityGapS)), 0, AccelMax)</c>: a slow roll (<c>ema ≥
    /// AccelUnityGapS</c>) travels the full notch, a medium spin travels at a constant speed
    /// (<c>WheelNotchDip/AccelUnityGapS</c>), and a fast spin accelerates again (<c>AccelRefGapS/ema</c>) — so the scroll
    /// SPEED never drops as the spin gets faster, while a fast spin's distance stays what it is with a smaller notch.
    /// A gap at/above <see cref="MotionFeel.AccelResetGapS"/> ends the spin (1×). Returns the updated estimate through
    /// <paramref name="nextEmaS"/>.</summary>
    public static double AccelFor(double gapS, double emaS, in MotionFeel feel, out double nextEmaS)
    {
        if (!(gapS > 0.0) || gapS >= feel.AccelResetGapS)
        {
            nextEmaS = 0.0;
            return 1.0;
        }
        nextEmaS = emaS <= 0.0 ? gapS : emaS + feel.AccelEmaWeight * (gapS - emaS);
        double roll = feel.AccelUnityGapS > 0.0 ? Math.Min(1.0, nextEmaS / feel.AccelUnityGapS) : 1.0;
        return Math.Min(Math.Max(feel.AccelRefGapS / nextEmaS, roll), feel.AccelMax);
    }

    /// <summary>Re-plans a wheel notch: the notch ACCUMULATES onto the pending destination. While the previous plan is a
    /// live (unsettled) wheel glide in the SAME direction, the new destination is
    /// <c>prev.Dest + notches·WheelNotchDip·accel</c> — the undelivered remainder of the glide is kept, so a fast spin
    /// travels every notch's full distance. A first notch, a notch after the glide settled, or a direction reversal
    /// re-bases on the displayed position (a reversal turns immediately).
    /// <para>A hi-res (free-spin) wheel is the same stream in FRACTIONAL notches (Chromium and Firefox smooth-wheel):
    /// 120 raw units travel one notch whether they arrive as one packet or twenty, and the spin rate is judged per WHOLE
    /// notch — a packet of <c>|notches| &lt; 1</c> arriving <c>gap</c> after the previous one is a spin of one notch per
    /// <c>gap / |notches|</c> — so a 3-packets-per-notch wheel is not mistaken for a 3× faster spin.</para>
    /// <para>The new segment (<see cref="WheelSeg"/>) is anchored at <c>t0 = max(tNotch, shownFloorSec)</c> — the latest
    /// present time a frame has already been POSED for (the render poser runs ahead of the clock by the present lead).
    /// Frames posed for presents past the device stamp have already shown the previous plan, so anchoring the curve at the
    /// stamp would make the first moving frame catch up several frames of travel at once (and a mid-glide reversal would
    /// step back past shown frames). The segment starts at the previous plan's position AND velocity at <c>t0</c> (never
    /// a jump, never a velocity drop). The spin-rate state stays on the device stamps
    /// (<see cref="WheelAccelState.LastNotchT"/> = <paramref name="tNotch"/>). Wheel never overpans.</para></summary>
    /// <param name="shownFloorSec">The latest present time already posed on screen (plan clock); −∞ = none (anchor at
    /// the stamp).</param>
    public static ScrollPlan WheelNotch(in ScrollPlan prev, double tNotch, double notches, in MotionFeel feel, ref WheelAccelState accel, double shownFloorSec = double.NegativeInfinity)
    {
        sbyte sign = notches > 0.0 ? (sbyte)1 : notches < 0.0 ? (sbyte)-1 : (sbyte)0;
        bool sameDirection = sign != 0 && sign == accel.LastSign;
        double factor = 1.0;
        if (sameDirection)
        {
            double gap = tNotch - accel.LastNotchT;
            double mag = Math.Abs(notches);
            if (mag < 1.0) gap /= mag;   // a fractional (hi-res) notch: the spin rate per whole notch
            factor = AccelFor(gap, accel.GapEmaS, in feel, out accel.GapEmaS);
        }
        else
            accel.GapEmaS = 0.0;
        accel.LastNotchT = tNotch;
        accel.LastSign = sign;

        double t0 = Math.Max(tNotch, shownFloorSec);
        double start = prev.Eval(t0, out _, out bool settled);
        bool liveWheel = prev.Kind == MotionKind.Wheel && prev.Count > 0 && !settled;
        double baseDest = liveWheel && sameDirection ? prev.Dest : start;
        double dest = Math.Clamp(baseDest + notches * feel.WheelNotchDip * factor, prev.Min, prev.Max);

        MotionSeg seg = WheelSeg(in prev, t0, dest, in feel);
        return new ScrollPlan(seg, default, default, default, 1, prev.Vp, prev.Gen, prev.Seq + 1, prev.Min, prev.Max,
            prev.ViewportExtent, prev.RubberC, prev.Clock, OverpanPolicy.None, MotionKind.Wheel, default);
    }

    /// <summary>The wheel's <see cref="SegKind.Cubic"/> from what <paramref name="prev"/> shows at <paramref name="t0"/>
    /// to <paramref name="dest"/> — the one place a wheel segment is shaped (a notch, and a snap re-target of its
    /// destination). It starts at <c>prev</c>'s position AND velocity at <c>t0</c>, so a re-plan is C1: the velocity
    /// carried in the notch's direction (0 from rest, or against it — a reversal turns at once) blends onto the
    /// front-loaded kick <c>1.5R/D</c> over <see cref="MotionFeel.WheelRiseS"/>. When the carried velocity already
    /// exceeds the kick (a slower notch late in a fast spin, or a destination clamped at an edge), the same cubic is
    /// authored over the SHORTER duration <c>1.5R/v</c> — it starts at exactly the carried velocity, arrives sooner and
    /// never overshoots <paramref name="dest"/>. A destination equal to the start is a Hold-shaped zero-length cubic.</summary>
    public static MotionSeg WheelSeg(in ScrollPlan prev, double t0, double dest, in MotionFeel feel)
    {
        double start = prev.Eval(t0, out double v, out _);
        double r = dest - start;
        double duration = feel.WheelDurationS;
        if (r == 0.0 || !(duration > 0.0))
            return new MotionSeg(SegKind.Cubic, t0, t0, start, dest);
        double carried = v * r > 0.0 ? v : 0.0;   // only velocity in the notch's direction carries
        double kick = 1.5 * r / duration;
        if (Math.Abs(carried) > Math.Abs(kick))
            return new MotionSeg(SegKind.Cubic, t0, t0 + 1.5 * r / carried, start, dest, carried);
        double rise = Math.Min(Math.Max(feel.WheelRiseS, 0.0), duration);
        return new MotionSeg(SegKind.Cubic, t0, t0 + duration, start, dest, carried, rise);
    }

    /// <summary>Starts a drag contact: a Follow-only plan (no closed-form segments — <see cref="ScrollPlan.Count"/>
    /// == 0), rubber-band overpan armed, seeded with the first contact sample. <paramref name="clock"/> is the clock the
    /// contact's samples are stamped on (<see cref="ContactClock"/>) — it decides how the ring is shown past its newest
    /// sample for the contact's whole life.</summary>
    public static ScrollPlan FollowBegin(in ScrollPlan prev, double t, double pos, ContactClock clock, in MotionFeel feel)
    {
        ContactRing ring = default;
        ring = ring.WithSample(t, pos, feel.FlingImpulseWindowS, feel.VelocityMinSpanS);
        return new ScrollPlan(default, default, default, default, 0, prev.Vp, prev.Gen, prev.Seq + 1, prev.Min, prev.Max,
            prev.ViewportExtent, prev.RubberC, clock, OverpanPolicy.RubberBand, MotionKind.Drag, ring);
    }

    /// <summary>Appends one contact sample to a live Follow plan, re-estimating the contact's least-squares velocity
    /// (<see cref="ContactRing.V"/>) over the feel's <see cref="MotionFeel.FlingImpulseWindowS"/> horizon with its
    /// <see cref="MotionFeel.VelocityMinSpanS"/> minimum span.</summary>
    public static ScrollPlan FollowSample(in ScrollPlan prev, double t, double pos, in MotionFeel feel)
        => prev with { Ring = prev.Ring.WithSample(t, pos, feel.FlingImpulseWindowS, feel.VelocityMinSpanS), Seq = prev.Seq + 1 };

    /// <summary>Ends a drag contact. The release velocity is the contact ring's least-squares velocity
    /// (<see cref="ContactRing.V"/>) over the trailing <see cref="MotionFeel.FlingImpulseWindowS"/> horizon — or 0 when
    /// the finger had already stopped (<see cref="StoppedAfterS"/>): a contact stream reports only while the fingers
    /// move, so a lift arriving a couple of report periods after the newest sample carries no momentum (Android's
    /// <c>ASSUME_POINTER_STOPPED_TIME</c> rule, scaled to the stream's own cadence), and a pause-then-lift never flings
    /// on a stale velocity.
    /// <list type="bullet">
    /// <item>Lifted PAST an edge (the rubber band was showing): a <see cref="SegKind.Spring"/> back to that edge from the
    /// lift's raw position and velocity, shown through the same rubber band — no jump at the lift, and the spring settles
    /// exactly on the edge.</item>
    /// <item>Below <see cref="MotionFeel.FlingMinVelocity"/>: settles in place (a Hold).</item>
    /// <item>Otherwise a <see cref="SegKind.Decay"/> coast; if its asymptote <c>P0 + V0/K</c> would cross
    /// <see cref="ScrollPlan.Min"/>/<see cref="ScrollPlan.Max"/>, the crossing time is solved analytically and a second
    /// segment is appended at the edge — a <see cref="SegKind.Spring"/> release back to the edge under
    /// <see cref="OverpanPolicy.RubberBand"/>, or a firm <see cref="SegKind.Hold"/> under
    /// <see cref="OverpanPolicy.None"/>.</item>
    /// </list></summary>
    /// <param name="tLift">When the contact actually lifted — the release velocity and the stopped-finger rule are
    /// judged here (the touchpad wheel fallback stamps its End at the LAST packet, the true lift).</param>
    /// <param name="tNow">When the lift is being authored (&gt;= <paramref name="tLift"/>). A lift DETECTED late (the touchpad
    /// fallback only knows after 50-120 ms of packet silence) is authored from what the Follow plan SHOWS at this
    /// instant, carrying the velocity the coast released at <paramref name="tLift"/> would have decayed to by now — so
    /// the plan's position at <paramref name="tNow"/> is exactly the displayed one (no forward jump at the lift), and
    /// the coast continues from there. For an immediate lift (<c>tNow == tLift</c>) this is the plain release. A
    /// composition-timed contact whose producer knows the release (<paramref name="release"/> not
    /// <see cref="ContactRelease.Unknown"/>) passes its NEWEST REAL SAMPLE as <paramref name="tLift"/> and the End stamp
    /// as <paramref name="tNow"/> (<c>ScrollHandle.ContactEnd</c>): DirectManipulation stops producing motion at the
    /// physical lift and reports the status edge one to five frames later, so the ring's velocity is read where the
    /// fingers last moved and decayed over that latency, from the held newest sample (no jump).</param>
    /// <param name="release">The producer's verdict at the lift. <see cref="ContactRelease.Moving"/> releases the ring's
    /// velocity and <see cref="ContactRelease.Stopped"/> none — the producer decided, so the time rule
    /// (<see cref="StoppedAfterS"/>) is not consulted; <see cref="ContactRelease.Unknown"/> (every stream without a
    /// verdict) is judged by that time rule. A Present-clock release is capped at <see cref="MotionFeel.TouchpadReleaseCapDipPerS"/>.</param>
    public static ScrollPlan FollowEnd(in ScrollPlan prev, double tLift, double tNow, in MotionFeel feel,
        ContactRelease release = ContactRelease.Unknown)
    {
        if (tNow < tLift) tNow = tLift;
        bool stopped = release switch
        {
            ContactRelease.Moving => false,
            ContactRelease.Stopped => true,
            _ => prev.Ring.Count > 0 && tLift - prev.Ring.LastT > StoppedAfterS(prev.Ring, in feel),
        };
        double vLift = stopped ? 0.0 : prev.Ring.V;
        double v0 = tNow > tLift ? vLift * Math.Exp(-feel.FlingDecayPerS * (tNow - tLift)) : vLift;
        // A composition-timed (DirectManipulation) touchpad release starts no faster than WinUI's measured plateau
        // (MotionFeel.TouchpadReleaseCapDipPerS): DM's pan gain reads 50-100k DIP/s on a rapid flick, WinUI releases the
        // same flicks at <= 10.9k. Device-timed streams (touch, pen, the wheel fallback) are not covered by that evidence.
        double cap = feel.TouchpadReleaseCapDipPerS;
        if (prev.Clock == ContactClock.Present && cap > 0.0 && Math.Abs(v0) > cap) v0 = v0 > 0.0 ? cap : -cap;
        // The RAW contact position the Follow plan shows at tNow (its rubber band maps it the same way this plan will).
        double p0 = prev.Ring.Eval(tNow, prev.Clock, out _);
        double tEnd = tNow;

        if (prev.Overpan == OverpanPolicy.RubberBand && (p0 < prev.Min || p0 > prev.Max))
            return OverpanRelease(in prev, tEnd, p0, v0, in feel);

        if (Math.Abs(v0) < feel.FlingMinVelocity)
        {
            var hold = new MotionSeg(SegKind.Hold, tEnd, double.PositiveInfinity, p0, p0);
            return new ScrollPlan(hold, default, default, default, 1, prev.Vp, prev.Gen, prev.Seq + 1, prev.Min, prev.Max,
                prev.ViewportExtent, prev.RubberC, prev.Clock, prev.Overpan, MotionKind.Idle, default);
        }

        double k = feel.FlingDecayPerS;
        double edge = v0 > 0.0 ? prev.Max : prev.Min;
        double asymptote = p0 + v0 / k;
        bool willCross = v0 > 0.0 ? asymptote > prev.Max : asymptote < prev.Min;

        if (!willCross)
        {
            var decayOpen = new MotionSeg(SegKind.Decay, tEnd, double.PositiveInfinity, p0, 0.0, v0, k);
            return new ScrollPlan(decayOpen, default, default, default, 1, prev.Vp, prev.Gen, prev.Seq + 1, prev.Min, prev.Max,
                prev.ViewportExtent, prev.RubberC, prev.Clock, prev.Overpan, MotionKind.Fling, default);
        }

        // Solve p0 + V0/K*(1 - e^-K*dt) = edge  =>  e^-K*dt = 1 - K*(edge - p0)/V0
        double ratio = 1.0 - k * (edge - p0) / v0;
        double crossDt = ratio > 0.0 ? Math.Max(0.0, -Math.Log(ratio) / k) : 0.0;
        double crossT = tEnd + crossDt;
        double crossV = v0 * Math.Exp(-k * crossDt);

        var decayClamped = new MotionSeg(SegKind.Decay, tEnd, crossT, p0, 0.0, v0, k);
        MotionSeg second = prev.Overpan == OverpanPolicy.RubberBand
            ? new MotionSeg(SegKind.Spring, crossT, double.PositiveInfinity, edge, edge, crossV, feel.SpringOmega, feel.SpringZeta)
            : new MotionSeg(SegKind.Hold, crossT, double.PositiveInfinity, edge, edge);

        return new ScrollPlan(decayClamped, second, default, default, 2, prev.Vp, prev.Gen, prev.Seq + 1, prev.Min, prev.Max,
            prev.ViewportExtent, prev.RubberC, prev.Clock, prev.Overpan, MotionKind.Fling, default);
    }

    /// <summary>The floor of the stopped-finger window (<see cref="StoppedAfterS"/>): two reports of a 100 Hz-class
    /// contact stream.</summary>
    public const double ContactStoppedMinS = 0.020;

    /// <summary>How long after its newest sample a contact counts as STOPPED: two of the stream's own report periods
    /// (<see cref="ContactRing.ReportPeriodS"/> — "no position change for a couple of reports"), no less than
    /// <see cref="ContactStoppedMinS"/> and no more than the velocity horizon <see cref="MotionFeel.FlingImpulseWindowS"/>
    /// (beyond which the least-squares velocity would have no sample to stand on anyway). A stationary frame is never a
    /// sample (a contact stream reports movement, not frames), so this time rule is what tells a paused finger from a
    /// moving one at the lift — for a stream WITHOUT a producer verdict (<see cref="ContactRelease.Unknown"/>). A
    /// DirectManipulation lift carries the verdict instead: its status edge trails the last motion by one to five
    /// frames, which this rule would read as a pause.</summary>
    public static double StoppedAfterS(in ContactRing ring, in MotionFeel feel)
    {
        double s = Math.Max(ContactStoppedMinS, 2.0 * ring.ReportPeriodS);
        return Math.Min(s, Math.Max(feel.FlingImpulseWindowS, ContactStoppedMinS));
    }

    /// <summary>Drops a live contact without a fling (a cancelled pan, a hand-off to another scroller): in place when the
    /// shown position is inside the content, else a zero-velocity <see cref="SegKind.Spring"/> back to the edge through the
    /// same rubber band — a cancelled overpan never sticks and never jumps.</summary>
    public static ScrollPlan FollowCancel(in ScrollPlan prev, double t, in MotionFeel feel)
    {
        if (prev.Count == 0)
        {
            double raw = prev.Ring.Eval(t, prev.Clock, out _);
            if (prev.Overpan == OverpanPolicy.RubberBand && (raw < prev.Min || raw > prev.Max))
                return OverpanRelease(in prev, t, raw, 0.0, in feel);
        }
        return Immediate(in prev, t, prev.Eval(t, out _, out _));
    }

    /// <summary>The spring back to the edge from an overpanned contact position <paramref name="raw"/> moving at raw
    /// velocity <paramref name="rawV"/>. Authored in the contact's RAW coordinate under the same
    /// <see cref="OverpanPolicy.RubberBand"/> map the drag was shown through (exactly like a fling's edge-crossing
    /// spring): position and velocity are continuous with the drag, so the first posed frame after the lift equals the
    /// last one before it, and the band relaxes onto the edge as the raw excess springs to 0.</summary>
    private static ScrollPlan OverpanRelease(in ScrollPlan prev, double t, double raw, double rawV, in MotionFeel feel)
    {
        double edge = raw < prev.Min ? prev.Min : prev.Max;
        var spring = new MotionSeg(SegKind.Spring, t, double.PositiveInfinity, raw, edge, rawV, feel.SpringOmega, feel.SpringZeta);
        return new ScrollPlan(spring, default, default, default, 1, prev.Vp, prev.Gen, prev.Seq + 1, prev.Min, prev.Max,
            prev.ViewportExtent, prev.RubberC, prev.Clock, OverpanPolicy.RubberBand, MotionKind.Fling, default);
    }

    /// <summary>A velocity-continuous programmatic move: a single <see cref="SegKind.Glide"/> (critically damped
    /// spring, <c>Zeta = 1</c>) from the plan's displayed position/velocity at <paramref name="tNow"/> to
    /// <paramref name="target"/> (clamped to the plan's extent). Never overpans.</summary>
    public static ScrollPlan Glide(in ScrollPlan prev, double tNow, double target, in MotionFeel feel)
    {
        double p0 = prev.Eval(tNow, out double v0, out _);
        double clampedTarget = Math.Clamp(target, prev.Min, prev.Max);
        var seg = new MotionSeg(SegKind.Glide, tNow, double.PositiveInfinity, p0, clampedTarget, v0, feel.GlideOmega, 1.0);
        return new ScrollPlan(seg, default, default, default, 1, prev.Vp, prev.Gen, prev.Seq + 1, prev.Min, prev.Max,
            prev.ViewportExtent, prev.RubberC, prev.Clock, OverpanPolicy.None, MotionKind.Programmatic, default);
    }

    /// <summary>An instant jump: a single open-ended <see cref="SegKind.Hold"/> at <paramref name="target"/>
    /// (clamped to the plan's extent).</summary>
    public static ScrollPlan Immediate(in ScrollPlan prev, double tNow, double target)
    {
        double clamped = Math.Clamp(target, prev.Min, prev.Max);
        var seg = new MotionSeg(SegKind.Hold, tNow, double.PositiveInfinity, clamped, clamped);
        return new ScrollPlan(seg, default, default, default, 1, prev.Vp, prev.Gen, prev.Seq + 1, prev.Min, prev.Max,
            prev.ViewportExtent, prev.RubberC, prev.Clock, OverpanPolicy.None, MotionKind.Programmatic, default);
    }

    /// <summary>A scrollbar thumb drag: a Hold at the pointer-mapped position (clamped), re-authored per pointer
    /// sample.</summary>
    public static ScrollPlan Thumb(in ScrollPlan prev, double tNow, double pos)
    {
        double clamped = Math.Clamp(pos, prev.Min, prev.Max);
        var seg = new MotionSeg(SegKind.Hold, tNow, double.PositiveInfinity, clamped, clamped);
        return new ScrollPlan(seg, default, default, default, 1, prev.Vp, prev.Gen, prev.Seq + 1, prev.Min, prev.Max,
            prev.ViewportExtent, prev.RubberC, prev.Clock, OverpanPolicy.None, MotionKind.Thumb, default);
    }

    /// <summary>A held constant-velocity move (drag-reorder edge auto-scroll): the content travels at
    /// <paramref name="velocity"/> DIP/s from the plan's displayed position until it reaches the edge in that direction,
    /// where a firm Hold is appended (the crossing time solved at authoring, never after layout). Zero velocity holds in
    /// place. Authored as a near-zero-rate Decay (K → 0 ⇒ p = P0 + V0·t) so it rides the same closed-form evaluator as
    /// every other segment.</summary>
    public static ScrollPlan Constant(in ScrollPlan prev, double tNow, double velocity)
    {
        double p0 = Math.Clamp(prev.Eval(tNow, out _, out _), prev.Min, prev.Max);
        if (velocity == 0.0) return Immediate(in prev, tNow, p0);
        const double k = 1e-9;
        double edge = velocity > 0.0 ? prev.Max : prev.Min;
        double crossDt = Math.Max(0.0, (edge - p0) / velocity);
        var run = new MotionSeg(SegKind.Decay, tNow, tNow + crossDt, p0, 0.0, velocity, k);
        var hold = new MotionSeg(SegKind.Hold, tNow + crossDt, double.PositiveInfinity, edge, edge);
        return new ScrollPlan(run, hold, default, default, 2, prev.Vp, prev.Gen, prev.Seq + 1, prev.Min, prev.Max,
            prev.ViewportExtent, prev.RubberC, prev.Clock, OverpanPolicy.None, MotionKind.Programmatic, default);
    }

    /// <summary>A keyboard move — arrows/page/home/end — authored as a <see cref="Glide"/> from the plan's current
    /// displayed position.</summary>
    public static ScrollPlan Key(in ScrollPlan prev, double tNow, KeyMove move, in MotionFeel feel, double viewportExtent)
    {
        double p0 = prev.Eval(tNow, out _, out _);
        double target = move switch
        {
            KeyMove.LineUp => p0 - feel.KeyLineDip,
            KeyMove.LineDown => p0 + feel.KeyLineDip,
            KeyMove.PageUp => p0 - feel.PageFraction * viewportExtent,
            KeyMove.PageDown => p0 + feel.PageFraction * viewportExtent,
            KeyMove.Home => prev.Min,
            KeyMove.End => prev.Max,
            _ => p0,
        };
        return Glide(in prev, tNow, target, in feel);
    }
}
