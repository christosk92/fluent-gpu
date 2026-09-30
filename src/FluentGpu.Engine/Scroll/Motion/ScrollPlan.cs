using System;

namespace FluentGpu.Scroll.Motion;

/// <summary>Overscroll handling a <see cref="ScrollPlan"/> applies at <see cref="ScrollPlan.Eval"/> time.</summary>
public enum OverpanPolicy : byte
{
    /// <summary>Positions are hard-clamped to <c>[Min, Max]</c> upstream (wheel, keyboard, programmatic never
    /// overpan) — <see cref="ScrollPlan.Eval"/> applies no correction.</summary>
    None = 0,

    /// <summary>Positions beyond <c>[Min, Max]</c> are compressed by the iOS-style rubber-band map
    /// (touch/touchpad drag and its fling).</summary>
    RubberBand = 1,
}

/// <summary>What produced a <see cref="ScrollPlan"/> — classification only (diagnostics, latch/chain routing); it
/// does not change how <see cref="ScrollPlan.Eval"/> evaluates.</summary>
public enum MotionKind : byte
{
    /// <summary>No open plan — a fresh <see cref="ScrollPlan.Idle"/> at rest.</summary>
    Idle = 0,
    /// <summary>An active wheel notch's Cubic replan.</summary>
    Wheel = 1,
    /// <summary>A live drag contact — the plan carries no segments, only the contact ring.</summary>
    Drag = 2,
    /// <summary>A released contact coasting (Decay, possibly followed by a Spring/Hold edge arrival).</summary>
    Fling = 3,
    /// <summary>An app-initiated move (<c>ScrollTo</c>/<c>BringIntoView</c>/keyboard) — Glide or Hold.</summary>
    Programmatic = 4,
    /// <summary>A scrollbar thumb drag — Hold per pointer sample.</summary>
    Thumb = 5,
}

/// <summary>Which clock a contact's samples are stamped on — decides how the ring is shown at a present time past its
/// newest sample (<see cref="ContactRing.Eval"/>). One live contact is ONE clock for its whole life.</summary>
public enum ContactClock : byte
{
    /// <summary>Device-time samples (touch/pen pointer reports, the touchpad wheel fallback): each is stamped when the
    /// device reported it, so a present time past the newest sample needs a prediction — the bounded Android resampling
    /// rule (<see cref="ContactRing.ResampleMaxPredictionS"/>), never free extrapolation.</summary>
    Device = 0,

    /// <summary>Composition-time samples (DirectManipulation's touchpad output): each was already predicted BY THE OS
    /// (the <c>IDirectManipulationFrameInfoProvider</c> composition hint) and is stamped with the present of the first
    /// render turn guaranteed to see it (<see cref="FluentGpu.Scroll.Runtime.ContactStamp.ForFrame"/> — the next tick's present), so a tick shows
    /// the previous tick's sample whatever the UI-write / render-read order. The ring never predicts again: between
    /// samples it interpolates, past the newest (a UI stall) it holds it. The least-squares velocity is used only for
    /// the fling at the lift.</summary>
    Present = 1,
}

/// <summary>A fixed-capacity (8 samples), POD ring of raw <c>(t, pos)</c> drag-contact samples — the closed-form-free
/// representation of a live <see cref="MotionKind.Drag"/>. No arrays: all 8 slots are named fields so the type stays
/// unmanaged and can live inline on <see cref="ScrollPlan"/>. Samples are kept in ascending-time order, oldest first;
/// <see cref="WithSample"/> evicts the oldest once full.
/// <para>The contact's velocity <see cref="V"/> is ONE least-squares estimate (Android <c>VelocityTracker</c>'s LSQ
/// strategy): the slope of the best-fit line through every sample within a trailing horizon of the newest, computed
/// once per appended sample: the release velocity (<c>PlanAuthor.FollowEnd</c>) and the reported contact speed. It never
/// drives the SHOWN position (<see cref="Eval"/>): a velocity estimate that changes between frames would move the pose
/// by <c>ΔV·lead</c> — against the finger whenever the estimate drops. A horizon spanning less than a minimum time reads as not moving (0): hi-res
/// packets arrive in near-coincident pairs a fraction of a millisecond apart, and a slope over such a pair (20k–130k
/// DIP/s) is arrival jitter, not finger speed.</para></summary>
public readonly struct ContactRing
{
    private readonly double _t0, _t1, _t2, _t3, _t4, _t5, _t6, _t7;
    private readonly double _p0, _p1, _p2, _p3, _p4, _p5, _p6, _p7;

    /// <summary>Number of valid samples, 0..8.</summary>
    public readonly byte Count;

    /// <summary>The contact's least-squares velocity (DIP/s) as of the newest sample — computed once, when that sample
    /// was appended (<see cref="WithSample"/>), with the horizon and minimum span the author passed. 0 with fewer than
    /// two samples or a horizon narrower than the minimum span.</summary>
    public readonly double V;

    private ContactRing(ReadOnlySpan<double> t, ReadOnlySpan<double> p, byte count, double v)
    {
        _t0 = t[0]; _t1 = t[1]; _t2 = t[2]; _t3 = t[3]; _t4 = t[4]; _t5 = t[5]; _t6 = t[6]; _t7 = t[7];
        _p0 = p[0]; _p1 = p[1]; _p2 = p[2]; _p3 = p[3]; _p4 = p[4]; _p5 = p[5]; _p6 = p[6]; _p7 = p[7];
        Count = count;
        V = v;
    }

    private void CopyTo(Span<double> t, Span<double> p)
    {
        t[0] = _t0; t[1] = _t1; t[2] = _t2; t[3] = _t3; t[4] = _t4; t[5] = _t5; t[6] = _t6; t[7] = _t7;
        p[0] = _p0; p[1] = _p1; p[2] = _p2; p[3] = _p3; p[4] = _p4; p[5] = _p5; p[6] = _p6; p[7] = _p7;
    }

    /// <summary>Appends one <c>(t, pos)</c> sample, zero-alloc (stack-only scratch space), and re-estimates
    /// <see cref="V"/> over the trailing <paramref name="horizonS"/> (a horizon covering less than
    /// <paramref name="minSpanS"/> reads 0). Once at capacity (8) the oldest sample is evicted.</summary>
    public ContactRing WithSample(double t, double pos, double horizonS, double minSpanS)
    {
        Span<double> ts = stackalloc double[8];
        Span<double> ps = stackalloc double[8];
        CopyTo(ts, ps);
        int n;
        if (Count < 8)
        {
            ts[Count] = t;
            ps[Count] = pos;
            n = Count + 1;
        }
        else
        {
            for (int i = 0; i < 7; i++)
            {
                ts[i] = ts[i + 1];
                ps[i] = ps[i + 1];
            }
            ts[7] = t;
            ps[7] = pos;
            n = 8;
        }
        return new ContactRing(ts, ps, (byte)n, LsqVelocity(ts, ps, n, horizonS, minSpanS));
    }

    /// <summary>The least-squares velocity over the current samples with an explicit horizon/minimum span (the same
    /// estimator <see cref="WithSample"/> caches into <see cref="V"/>) — diagnostics and tests.</summary>
    public double Velocity(double horizonS, double minSpanS)
    {
        if (Count < 2) return 0.0;
        Span<double> ts = stackalloc double[8];
        Span<double> ps = stackalloc double[8];
        CopyTo(ts, ps);
        return LsqVelocity(ts, ps, Count, horizonS, minSpanS);
    }

    /// <summary>Slope of the least-squares line through the samples within <paramref name="horizonS"/> of the newest
    /// (times taken relative to the newest, for precision at large QPC-second clocks); 0 with fewer than two samples or
    /// when they span less than <paramref name="minSpanS"/>.</summary>
    private static double LsqVelocity(ReadOnlySpan<double> ts, ReadOnlySpan<double> ps, int n, double horizonS, double minSpanS)
    {
        if (n < 2) return 0.0;
        double tl = ts[n - 1];
        int first = n - 1;
        while (first > 0 && tl - ts[first - 1] <= horizonS) first--;
        double span = tl - ts[first];
        if (!(span > 0.0) || span < minSpanS) return 0.0;
        int m = n - first;
        double mt = 0.0, mp = 0.0;
        for (int i = first; i < n; i++) { mt += ts[i] - tl; mp += ps[i]; }
        mt /= m;
        mp /= m;
        double sxx = 0.0, sxy = 0.0;
        for (int i = first; i < n; i++)
        {
            double d = ts[i] - tl - mt;
            sxx += d * d;
            sxy += d * (ps[i] - mp);
        }
        return sxx > 0.0 ? sxy / sxx : 0.0;
    }

    /// <summary>Android's <c>RESAMPLE_MAX_PREDICTION</c>: a device-time ring never predicts more than this past its newest
    /// sample (<see cref="ContactClock.Device"/>).</summary>
    public const double ResampleMaxPredictionS = 0.008;

    /// <summary>Android's <c>RESAMPLE_MIN_DELTA</c>: the newest two samples must be at least this far apart for their slope
    /// to be a prediction — a closer pair (hi-res packets arrive in near-coincident pairs) is arrival jitter, not motion.</summary>
    public const double ResampleMinDeltaS = 0.002;

    /// <summary>Android's <c>RESAMPLE_MAX_DELTA</c>: newest two samples further apart than this are too slow a stream to
    /// predict from — the ring holds the newest sample.</summary>
    public const double ResampleMaxDeltaS = 0.020;

    /// <summary>Shows the ring at absolute present time <paramref name="t"/> under <paramref name="clock"/>.
    /// Before the first sample or with a single sample: that sample's position (at a time several samples share, the
    /// newest of them). Between two samples: linear
    /// interpolation. Past the newest sample:
    /// <list type="bullet">
    /// <item><see cref="ContactClock.Present"/>: the newest position, held — the OS already predicted each sample, so a
    /// second prediction would double the lead (and every velocity-estimate change would move the shown position against
    /// the finger). With <see cref="FluentGpu.Scroll.Runtime.ContactStamp.ForFrame"/> stamps this happens only when the UI stalls past a refresh;
    /// in steady state every present time lies between two real samples.</item>
    /// <item><see cref="ContactClock.Device"/>: Android's bounded resampling (<c>InputTransport</c>
    /// <c>resampleTouchState</c>): extrapolate along the newest two samples' slope by at most
    /// <c>min(<see cref="ResampleMaxPredictionS"/>, gap/2)</c>, and only when that gap is within
    /// [<see cref="ResampleMinDeltaS"/>, <see cref="ResampleMaxDeltaS"/>]; otherwise hold. Bounding the prediction to half
    /// the newest sample gap bounds how far a fresh sample can pull the shown position back: a new sample moves the
    /// pose by <c>ΔP + (s_new·e_new − s_old·e_old)</c>, and with <c>e ≤ gap/2</c> that is negative only when the finger
    /// slowed to under a third of its speed within one report — the free LSQ extrapolation it replaces stepped back on
    /// any velocity-estimate drop.</item>
    /// </list>
    /// <paramref name="v"/> is the contact's least-squares velocity <see cref="V"/> wherever the contact is live (it sizes
    /// the virtualizer's overscan and reads out as the motion speed), 0 before the second sample.</summary>
    public double Eval(double t, ContactClock clock, out double v)
    {
        if (Count == 0)
        {
            v = 0.0;
            return 0.0;
        }

        Span<double> ts = stackalloc double[8];
        Span<double> ps = stackalloc double[8];
        CopyTo(ts, ps);
        int last = Count - 1;

        if (Count == 1 || t < ts[0])
        {
            v = 0.0;
            return ps[0];
        }

        v = V;
        if (t >= ts[last])
        {
            if (clock == ContactClock.Present) return ps[last];
            double gap = ts[last] - ts[last - 1];
            if (!(gap >= ResampleMinDeltaS) || gap > ResampleMaxDeltaS) return ps[last];
            double maxPredict = gap * 0.5 < ResampleMaxPredictionS ? gap * 0.5 : ResampleMaxPredictionS;
            double e = t - ts[last];
            if (e > maxPredict) e = maxPredict;
            return ps[last] + (ps[last] - ps[last - 1]) / gap * e;
        }

        for (int i = 0; i < last; i++)
        {
            if (t >= ts[i] && t <= ts[i + 1])
            {
                double span = ts[i + 1] - ts[i];
                if (!(span > 0.0)) return ps[i + 1];
                double u = (t - ts[i]) / span;
                return ps[i] + (ps[i + 1] - ps[i]) * u;
            }
        }

        return ps[last];
    }

    /// <summary>The contact's recent report period: the mean spacing of its newest (up to) four intervals, 0 with fewer
    /// than two samples — what "a couple of reports" means for this stream (a 125 Hz touchpad, a 60 Hz DirectManipulation
    /// frame stream, a 240 Hz digitizer), read off the samples themselves.</summary>
    public double ReportPeriodS
    {
        get
        {
            if (Count < 2) return 0.0;
            int k = Count - 1 < 4 ? Count - 1 : 4;
            return (SampleAt(Count - 1).T - SampleAt(Count - 1 - k).T) / k;
        }
    }

    /// <summary>Moves every sample's TIME by <paramref name="dt"/> (positions and <see cref="V"/> untouched) — the
    /// contact-clock resync a device clock that ran ahead of the plan clock needs: the whole history slides so the
    /// newest sample never sits in the future while the device's sample SPACING (and so the velocity) is kept exactly.
    /// Zero-alloc.</summary>
    public ContactRing TimeShifted(double dt)
    {
        if (Count == 0 || dt == 0.0) return this;
        Span<double> ts = stackalloc double[8];
        Span<double> ps = stackalloc double[8];
        CopyTo(ts, ps);
        for (int i = 0; i < Count; i++) ts[i] += dt;
        return new ContactRing(ts, ps, Count, V);
    }

    /// <summary>Moves every sample's POSITION by <paramref name="delta"/> (times and <see cref="V"/> untouched) — a
    /// coordinate-frame correction (<see cref="ScrollPlan.Shifted"/>), not motion. Zero-alloc.</summary>
    public ContactRing PositionShifted(double delta)
    {
        if (Count == 0 || delta == 0.0) return this;
        Span<double> ts = stackalloc double[8];
        Span<double> ps = stackalloc double[8];
        CopyTo(ts, ps);
        for (int i = 0; i < Count; i++) ps[i] += delta;
        return new ContactRing(ts, ps, Count, V);
    }

    /// <summary>Time of the most recent sample, or −∞ if empty.</summary>
    public double LastT => Count == 0 ? double.NegativeInfinity : SampleAt(Count - 1).T;

    /// <summary>The ring's most recent position, or 0 if empty — used as a Follow-only plan's provisional
    /// <see cref="ScrollPlan.Dest"/>.</summary>
    public double LastPos => Count == 0 ? 0.0 : SampleAt(Count - 1).Pos;

    /// <summary>The sample at ring index <paramref name="index"/> (0 = oldest), for diagnostics/tests.</summary>
    public (double T, double Pos) SampleAt(int index)
    {
        Span<double> ts = stackalloc double[8];
        Span<double> ps = stackalloc double[8];
        CopyTo(ts, ps);
        return (ts[index], ps[index]);
    }
}

/// <summary>An immutable, POD scroll motion plan: up to 4 <see cref="MotionSeg"/> arcs (open-ended validity chained
/// contiguously) plus, for a live drag with no closed form yet, a <see cref="ContactRing"/>. Positions are double
/// precision throughout — the render thread poses <c>(float)(windowOrigin − Eval(presentSec))</c>, keeping the
/// float cast at the very last step so a 100k-row list's large offsets never lose precision inside the plan
/// itself.</summary>
public readonly record struct ScrollPlan(
    MotionSeg S0, MotionSeg S1, MotionSeg S2, MotionSeg S3,
    byte Count,
    int Vp, uint Gen, ulong Seq,
    double Min, double Max,
    double ViewportExtent, double RubberC, ContactClock Clock,
    OverpanPolicy Overpan, MotionKind Kind,
    ContactRing Ring)
{
    /// <summary>The plan's final destination: the last active segment's target (its <c>P1</c>, or <c>P0</c> for a
    /// Hold), or — for a Follow-only plan (<see cref="Count"/> == 0) — the ring's most recent raw position (a
    /// provisional value; a live drag has no fixed destination).</summary>
    public double Dest
    {
        get
        {
            if (Count == 0) return Ring.LastPos;
            MotionSeg last = SegAt(Count - 1);
            return last.Kind switch
            {
                SegKind.Hold => last.P0,
                SegKind.Decay when last.K > 0.0 => last.P0 + last.V0 / last.K,   // the open coast's asymptote
                _ => last.P1,
            };
        }
    }

    /// <summary>Evaluates the plan at absolute time <paramref name="t"/>: picks the segment whose validity covers
    /// <paramref name="t"/> (or the contact ring, for a Follow-only plan), applies the rubber-band overpan
    /// correction when <see cref="Overpan"/> is <see cref="OverpanPolicy.RubberBand"/>, and reports whether the plan
    /// has structurally settled (a <see cref="SegKind.Hold"/> tail, or a finite-duration segment already past its
    /// end — open-ended Decay/Spring/Glide asymptote and are never reported settled here; the author is responsible
    /// for replacing them with a Hold once they cross the feel's settle epsilon).</summary>
    public double Eval(double t, out double v, out bool settled)
    {
        double p;
        if (Count == 0)
        {
            p = Ring.Eval(t, Clock, out v);
            settled = false;
        }
        else
        {
            int idx = 0;
            for (int i = 0; i < Count; i++)
            {
                idx = i;
                if (t < SegAt(i).T1) break;
            }
            MotionSeg seg = SegAt(idx);
            double te = t < seg.T0 ? seg.T0 : t;
            p = seg.Eval(te, out v);
            settled = idx == Count - 1
                && (seg.Kind == SegKind.Hold || (!double.IsPositiveInfinity(seg.T1) && t >= seg.T1));
        }

        if (Overpan == OverpanPolicy.RubberBand)
            p = ApplyRubberBand(p);

        return p;
    }

    /// <summary>Shifts the plan's coordinate frame by <paramref name="delta"/> — an anchor/measured-extent correction,
    /// not motion: every position (segment endpoints, the ring's samples) moves by <paramref name="delta"/>; every time
    /// field, velocity, rate and shape constant is untouched, so inside the content
    /// <c>Shifted(delta).Eval(t, ...) == Eval(t, ...) + delta</c> exactly for every <c>t</c>. The content START is
    /// invariant (<see cref="Min"/> is kept); the end moves with the content that grew or shrank above the anchor
    /// (<see cref="Max"/> + delta) — the host re-asserts both from the laid-out extent in the same frame
    /// (<c>ScrollHandle.SetExtent</c>).</summary>
    public ScrollPlan Shifted(double delta)
    {
        static MotionSeg Shift(MotionSeg s, double d)
            => new(s.Kind, s.T0, s.T1, s.P0 + d, s.P1 + d, s.V0, s.K, s.Zeta);

        return this with
        {
            S0 = Count > 0 ? Shift(S0, delta) : S0,
            S1 = Count > 1 ? Shift(S1, delta) : S1,
            S2 = Count > 2 ? Shift(S2, delta) : S2,
            S3 = Count > 3 ? Shift(S3, delta) : S3,
            Max = Max + delta,
            Ring = Ring.PositionShifted(delta),
        };
    }

    /// <summary>A fresh, at-rest plan: a single open-ended <see cref="SegKind.Hold"/> at <paramref name="pos"/>.</summary>
    public static ScrollPlan Idle(int vp, double pos, double min, double max, double viewportExtent = 0.0, double rubberC = 0.0)
    {
        var hold = new MotionSeg(SegKind.Hold, double.NegativeInfinity, double.PositiveInfinity, pos, pos);
        return new ScrollPlan(hold, default, default, default, 1, vp, 0, 0, min, max, viewportExtent, rubberC, ContactClock.Device,
            OverpanPolicy.None, MotionKind.Idle, default);
    }

    private MotionSeg SegAt(int i) => i switch
    {
        0 => S0,
        1 => S1,
        2 => S2,
        _ => S3,
    };

    /// <summary>The overpan the rubber band asymptotes to, as a fraction of the viewport (WinUI's ~10% overpan).</summary>
    public const double OverpanCapFraction = 0.10;

    private double ApplyRubberBand(double p)
    {
        double cap = ViewportExtent * OverpanCapFraction;
        if (p < Min)
        {
            double excess = Min - p;
            return Min - RubberBand(excess, cap, RubberC);
        }
        if (p > Max)
        {
            double excess = p - Max;
            return Max + RubberBand(excess, cap, RubberC);
        }
        return p;
    }

    /// <summary>The iOS-style rubber-band map: <c>c·excess·vp/(vp + c·excess)</c> — monotone increasing in
    /// <paramref name="excess"/>, asymptotic to <paramref name="vp"/> (never reaches, let alone exceeds, one
    /// viewport of overpan).</summary>
    public static double RubberBand(double excess, double vp, double c)
    {
        if (!(excess > 0.0) || !(vp > 0.0)) return 0.0;
        return c * excess * vp / (vp + c * excess);
    }
}
