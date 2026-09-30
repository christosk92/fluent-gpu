using System;
using System.Collections.Generic;
using FluentGpu.Scroll.Motion;
using Xunit;

namespace FluentGpu.Engine.Tests;

/// <summary>The default wheel feel (the first of <see cref="FeelProfiles.All"/>) replayed over the owner's REAL notch
/// traces (the device stamps of two recorded Wavee sessions, 2026-09-24 and 2026-09-25, embedded below as data) and a
/// synthetic cadence sweep, through the pure plan maths (<see cref="PlanAuthor.WheelNotch"/> +
/// <see cref="ScrollPlan.Eval"/>). The owner's complaint ("mud") was the single and slow notch (a 32 DIP step on a
/// gentle 0.257 s curve), not top speed. So: a single notch moves 64 DIP and is front-loaded, a fast spin travels what it
/// did (per-burst totals within 15 % of what the recording's own plan rows show), and no re-plan ever changes the
/// velocity on screen (C1). Simulation and plots: C:\WAVEE\wheel-curve-probe\reports\wheel-snappier-2026-09-25.</summary>
public sealed class WheelFeelTraceTests
{
    static readonly MotionFeel Feel = FeelProfiles.All[0].Feel;

    /// <summary>A notch's plan is anchored at the pose floor: the recording shows plans authored ~14 ms after the device
    /// stamp and posed a present later, so every replayed notch is anchored this long after its stamp.</summary>
    const double AnchorLagS = 0.014 + 1.0 / 120.0;
    const double Origin = 5_000_000.0;

    static ScrollPlan Rest() => ScrollPlan.Idle(0, Origin, 0.0, 2 * Origin, 400.0, Feel.RubberBandC);

    /// <summary>20260925-104434-owner-mud: 62 notch rows (ms from the first, notches).</summary>
    static readonly (double Ms, int Notches)[] Mud0925 = [(0.000, 1), (12.475, 1), (19.468, 1), (27.558, 1), (34.430, 1), (40.475, 1), (46.437, 1), (53.992, 1), (63.975, 1), (1063.448, 1), (1077.881, 1), (1091.913, 1), (1106.422, 1), (1125.476, 1), (1150.973, 1), (1502.977, 1), (1517.981, 1), (1531.953, 1), (1544.956, 1), (1560.638, 1), (1579.448, 1), (1613.467, 1), (1977.492, 1), (1991.910, 1), (2004.468, 1), (2017.958, 1), (2031.555, 1), (2050.456, 1), (2085.830, 1), (2494.039, 1), (2510.678, 1), (2527.321, 1), (2542.080, 1), (2561.488, 1), (2587.480, 1), (2917.054, 1), (2930.570, 1), (2944.030, 1), (2957.129, 1), (2970.606, 1), (2986.451, 1), (3019.511, 1), (3355.582, 1), (3370.064, 1), (3382.097, 1), (3397.085, 1), (3412.630, 1), (3443.128, 1), (3822.661, -1), (3835.896, -1), (3848.104, -1), (3859.596, -1), (3873.077, -1), (3894.189, -1), (5214.617, 1), (5228.156, 1), (5238.580, 1), (5250.584, 1), (5262.555, 1), (5278.105, 1), (5296.071, 1), (5340.101, 1)];
    /// <summary>scroll-20260924-153421.csv: 42 notch rows (ms from the first, notches).</summary>
    static readonly (double Ms, int Notches)[] Spin0924 = [(0.000, 1), (0.190, 1), (17.437, 1), (31.437, 1), (44.007, 1), (56.873, 1), (73.405, 1), (95.413, 1), (1670.637, 1), (1681.656, 1), (1691.136, 1), (1698.798, 1), (1708.008, 1), (1716.136, 1), (1726.611, 1), (1726.818, 1), (1736.617, 1), (1748.014, 1), (1763.009, 1), (2060.938, 1), (2084.231, 1), (2097.828, 1), (2106.839, 1), (2114.860, 1), (2122.097, 1), (2130.305, 1), (2139.054, 1), (2149.165, 1), (2165.616, 1), (2425.757, 1), (2437.285, 1), (2446.694, 1), (2454.898, 1), (2462.958, 1), (2470.282, 1), (2477.770, 1), (2485.657, 1), (2495.265, 1), (3305.305, -1), (3615.001, -1), (3633.525, -1), (3657.029, -1)];

    /// <summary>Every burst (same-direction notches each within the 120 ms spin reset): trace, first notch, count and the
    /// total the recording's plan rows show the 32 DIP model travelled.</summary>
    static readonly (int Trace, int First, int Count, double RecordedDip)[] Bursts =
    [
        (0, 0, 9, 621.0430),  // n=9, median gap 7.3 ms
        (0, 9, 6, 238.7534),  // n=6, median gap 14.5 ms
        (0, 15, 7, 279.3542),  // n=7, median gap 15.3 ms
        (0, 22, 7, 289.9328),  // n=7, median gap 14.0 ms
        (0, 29, 6, 218.2221),  // n=6, median gap 16.6 ms
        (0, 35, 7, 299.3482),  // n=7, median gap 13.5 ms
        (0, 42, 6, 244.3637),  // n=6, median gap 15.0 ms
        (0, 48, 6, -272.6035),  // n=6, median gap 13.2 ms
        (0, 54, 8, 357.9423),  // n=8, median gap 13.5 ms
        (1, 0, 8, 0.0000),  // n=8, median gap 14.0 ms  (at the list end: clamped to 0 in the recording)
        (1, 8, 11, 744.2367),  // n=11, median gap 9.6 ms
        (1, 19, 10, 489.1701),  // n=10, median gap 9.0 ms
        (1, 29, 9, 587.0255),  // n=9, median gap 8.1 ms
        (1, 38, 1, -32.0000),  // n=1, median gap 0.0 ms
        (1, 39, 3, -98.5496),  // n=3, median gap 21.0 ms
    ];

    static (double Ms, int Notches)[] Trace(int i) => i == 0 ? Mud0925 : Spin0924;

    /// <summary>One burst replayed from rest with a fresh spin state (every burst starts past the spin reset gap, so the
    /// distance is exactly what the host added): the distance its notches added to the destination.</summary>
    static double BurstTotal(int trace, int first, int count, in MotionFeel feel)
    {
        var notches = Trace(trace);
        var accel = new WheelAccelState();
        ScrollPlan plan = Rest();
        for (int k = first; k < first + count; k++)
        {
            double t = notches[k].Ms / 1000.0;
            plan = PlanAuthor.WheelNotch(in plan, t, notches[k].Notches, in feel, ref accel, t + AnchorLagS);
        }
        return plan.Dest - Origin;
    }

    static double MedianGapMs(int trace, int first, int count)
    {
        if (count < 2) return double.PositiveInfinity;
        var notches = Trace(trace);
        var gaps = new double[count - 1];
        for (int k = 1; k < count; k++) gaps[k - 1] = notches[first + k].Ms - notches[first + k - 1].Ms;
        Array.Sort(gaps);
        return gaps.Length % 2 == 1 ? gaps[gaps.Length / 2] : 0.5 * (gaps[gaps.Length / 2 - 1] + gaps[gaps.Length / 2]);
    }

    static double TimeToFraction(in ScrollPlan plan, double t0, double start, double travel, double fraction)
    {
        double lo = t0, hi = t0 + 2.0;
        for (int i = 0; i < 80; i++)
        {
            double mid = 0.5 * (lo + hi);
            if ((plan.Eval(mid, out _, out _) - start) / travel >= fraction) hi = mid; else lo = mid;
        }
        return hi - t0;
    }

    [Fact]
    public void AnIsolatedOwnerNotch_Travels64Dip_FrontLoaded_AndSettlesWithin220ms()
    {
        // The owner's isolated notch (2026-09-24 at 3305 ms: 310 ms of silence either side).
        var notch = Spin0924[38];
        double t = notch.Ms / 1000.0, anchor = t + AnchorLagS;
        var accel = new WheelAccelState();
        ScrollPlan rest = Rest();
        ScrollPlan plan = PlanAuthor.WheelNotch(in rest, t, notch.Notches, in Feel, ref accel, anchor);
        double travel = plan.Dest - Origin;

        Assert.InRange(Math.Abs(travel), 63.0, 65.0);
        double t50 = TimeToFraction(in plan, anchor, Origin, travel, 0.5);
        double t90 = TimeToFraction(in plan, anchor, Origin, travel, 0.9);
        Assert.True(t50 <= 0.055, $"50 % of the notch after {t50 * 1000:0.0} ms (want <= 55 ms)");
        Assert.True(t90 <= 0.150, $"90 % of the notch after {t90 * 1000:0.0} ms (want <= 150 ms)");

        double settleFromStamp = double.NaN;
        for (double s = anchor; s < anchor + 1.0; s += 0.0005)
        {
            plan.Eval(s, out _, out bool settled);
            if (settled) { settleFromStamp = s - t; break; }
        }
        Assert.True(settleFromStamp <= 0.220, $"settled {settleFromStamp * 1000:0} ms after the notch (want <= 220 ms)");
        Assert.Equal(plan.Dest, plan.Eval(t + 0.220, out double vEnd, out _), 9);
        Assert.Equal(0.0, vEnd, 9);
    }

    [Fact]
    public void OwnerFastSpins_TravelWithin15PercentOfWhatTheRecordingTravelled()
    {
        int fast = 0;
        var misses = new List<string>();
        foreach (var (trace, first, count, recorded) in Bursts)
        {
            if (recorded == 0.0) continue;   // clamped at the list end in the recording: no distance to compare
            double total = BurstTotal(trace, first, count, in Feel);
            double gap = MedianGapMs(trace, first, count);
            if (count >= 5 && gap <= 20.0)
            {
                fast++;
                double ratio = total / recorded;
                if (ratio < 0.85 || ratio > 1.15)
                    misses.Add($"trace {trace} notch {first} x{count} (median gap {gap:0.0} ms): {total:0.0} vs recorded {recorded:0.0} DIP ({ratio:0.000}x)");
            }
            else
            {
                // A single notch or a slow roll is exactly what the owner called slow: it must travel FURTHER, never less.
                Assert.True(Math.Abs(total) > Math.Abs(recorded),
                    $"trace {trace} notch {first} x{count}: {total:0.0} vs recorded {recorded:0.0} DIP");
            }
        }
        Assert.Equal(12, fast);
        Assert.True(misses.Count == 0, string.Join("; ", misses));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void OwnerTrace_EveryReplan_ContinuesTheShownVelocity(int trace)
    {
        // The whole session in order (a burst overlaps the previous glide where they are close), every notch anchored at
        // its pose floor. A same-direction re-plan of a moving glide keeps the velocity on screen:
        // |v_new - v_shown| <= 1 % of v_shown. A reversal turns at once and is not a continuation.
        var notches = Trace(trace);
        var accel = new WheelAccelState();
        ScrollPlan plan = Rest();
        double lastAnchor = double.NegativeInfinity;
        int continued = 0;
        var breaks = new List<string>();
        for (int k = 0; k < notches.Length; k++)
        {
            double t = notches[k].Ms / 1000.0;
            double anchor = Math.Max(t + AnchorLagS, lastAnchor);
            lastAnchor = anchor;
            plan.Eval(anchor, out double vShown, out bool settled);
            ScrollPlan next = PlanAuthor.WheelNotch(in plan, t, notches[k].Notches, in Feel, ref accel, anchor);
            next.Eval(anchor, out double vNew, out _);
            if (!settled && plan.Kind == MotionKind.Wheel && vShown * notches[k].Notches > 0.0)
            {
                continued++;
                if (Math.Abs(vNew - vShown) > 0.01 * Math.Abs(vShown))
                    breaks.Add($"notch {k} @ {notches[k].Ms:0.0} ms: {vShown:0} -> {vNew:0} DIP/s");
            }
            plan = next;
        }
        Assert.True(continued >= 30, $"only {continued} re-plans of a moving glide");
        Assert.True(breaks.Count == 0, $"{breaks.Count}/{continued} re-plans changed the shown velocity: " + string.Join("; ", breaks));
    }

    public static IEnumerable<object[]> Cadences()
    {
        foreach (double gapMs in new[] { 3.0, 5.0, 8.0, 10.0, 12.0, 15.0, 20.0, 25.0, 30.0, 40.0, 50.0, 60.0, 80.0, 100.0 })
            yield return new object[] { gapMs };
    }

    /// <summary>A spin of <paramref name="count"/> notches <paramref name="gapMs"/> apart: the distance it adds, the worst
    /// relative velocity change at a re-plan of a moving glide, and whether the last plan (which carries the burst to
    /// rest) is monotone, never past its destination, and lands on it.</summary>
    static (double Total, double MaxVelocityBreak, bool MonotoneAndLands) Spin(double gapMs, int count)
    {
        var accel = new WheelAccelState();
        ScrollPlan plan = Rest();
        double worst = 0.0, lastAnchor = 0.0;
        for (int k = 0; k < count; k++)
        {
            double t = k * gapMs / 1000.0, anchor = t + AnchorLagS;
            lastAnchor = anchor;
            plan.Eval(anchor, out double vShown, out bool settled);
            ScrollPlan next = PlanAuthor.WheelNotch(in plan, t, 1.0, in Feel, ref accel, anchor);
            next.Eval(anchor, out double vNew, out _);
            if (!settled && plan.Kind == MotionKind.Wheel && vShown > 0.0)
                worst = Math.Max(worst, Math.Abs(vNew - vShown) / vShown);
            plan = next;
        }
        bool ok = true;
        double prev = plan.Eval(lastAnchor, out _, out _);
        for (double s = lastAnchor; s < lastAnchor + Feel.WheelDurationS + 0.05; s += 1.0 / 240.0)
        {
            double p = plan.Eval(s, out _, out _);
            if (p < prev - 1e-9 || p > plan.Dest + 1e-9) ok = false;
            prev = p;
        }
        ok &= Math.Abs(plan.Eval(lastAnchor + Feel.WheelDurationS, out _, out bool landed) - plan.Dest) < 1e-9 && landed;
        return (plan.Dest - Origin, worst, ok);
    }

    [Theory]
    [MemberData(nameof(Cadences))]
    public void CadenceSweep_DistanceIsMonotoneInNotches_Bounded_C1_AndLandsExactly(double gapMs)
    {
        double previous = 0.0;
        foreach (int count in new[] { 1, 3, 10, 50 })
        {
            var (total, velocityBreak, monotoneAndLands) = Spin(gapMs, count);
            Assert.True(total > previous, $"{count} notches at {gapMs} ms travel {total:0.0} DIP, no more than fewer notches ({previous:0.0})");
            Assert.True(total <= count * Feel.WheelNotchDip * Feel.AccelMax + 1e-6, $"{count} notches at {gapMs} ms travel {total:0.0} DIP");
            Assert.True(velocityBreak <= 0.01, $"{count} notches at {gapMs} ms: a re-plan changed the shown velocity by {velocityBreak:P1}");
            Assert.True(monotoneAndLands, $"{count} notches at {gapMs} ms: the glide stepped back, overshot or did not land");
            previous = total;
        }
    }

    [Fact]
    public void CadenceSweep_AFasterSpinNeverScrollsSlower()
    {
        // 50 notches: the burst's mean speed (distance over its span) never drops as the notches come closer together.
        double previousSpeed = double.PositiveInfinity;
        foreach (object[] row in Cadences())
        {
            double gapMs = (double)row[0];
            double speed = Spin(gapMs, 50).Total / (49 * gapMs / 1000.0);
            Assert.True(speed <= previousSpeed * (1.0 + 1e-9), $"{gapMs} ms: {speed:0} DIP/s after {previousSpeed:0} DIP/s at a faster spin");
            previousSpeed = speed;
        }
    }

    [Theory]
    [InlineData(0.25)]
    [InlineData(1.0 / 3.0)]
    [InlineData(0.5)]
    public void HiResFraction_TravelsItsShareOfTheNotch(double fraction)
    {
        // An isolated fractional packet travels fraction x WheelNotchDip; a roll of such packets at a slow whole-notch
        // cadence (one notch per 80 ms, the full-step band) travels exactly its turned notches x WheelNotchDip.
        var accel = new WheelAccelState();
        ScrollPlan rest = Rest();
        ScrollPlan one = PlanAuthor.WheelNotch(in rest, 0.0, fraction, in Feel, ref accel);
        Assert.Equal(fraction * Feel.WheelNotchDip, one.Dest - Origin, 9);

        accel = new WheelAccelState();
        ScrollPlan plan = Rest();
        int packets = (int)Math.Round(4.0 / fraction);
        for (int k = 0; k < packets; k++)
        {
            double t = k * 0.080 * fraction;
            plan = PlanAuthor.WheelNotch(in plan, t, fraction, in Feel, ref accel, t + AnchorLagS);
        }
        Assert.Equal(4.0 * Feel.WheelNotchDip, plan.Dest - Origin, 6);
    }
}
