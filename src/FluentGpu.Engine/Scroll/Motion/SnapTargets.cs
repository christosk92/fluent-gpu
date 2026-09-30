using System;

namespace FluentGpu.Scroll.Motion;

/// <summary>A viewport's declared snap grid in the plan's double domain: a uniform interval (WinUI
/// <c>RepeatedScrollSnapPoint</c>) and/or an explicit ascending point list (<c>ScrollSnapPoint</c>). Both empty ⇒
/// <see cref="IsEmpty"/> and a fling coasts freely.</summary>
public readonly record struct SnapGrid(double Interval, double Start, double End, float[]? Points)
{
    public static readonly SnapGrid None = new(0.0, 0.0, 0.0, null);
    public bool IsEmpty => Interval <= 0.0 && (Points is null || Points.Length == 0);
}

/// <summary>
/// WinUI ScrollPresenter "Mandatory" snap-point math (<c>controls\dev\ScrollPresenter\SnapPoint.cpp</c>), pure and
/// double-based: every value falls in some snap point's zone, the boundary between two adjacent points being their
/// midpoint. Used at fling-AUTHORING time only (<see cref="PlanAuthor.FollowEnd"/> + <see cref="ResolveFling"/>):
/// the natural rest of the decay is snapped, then the decay is re-solved so the closed-form curve lands exactly there
/// in finite time — nothing is resolved after layout.
/// </summary>
public static class SnapTargets
{
    /// <summary>The snap value nearest <paramref name="natural"/>. With <paramref name="impulse"/> (a fling that
    /// would rest within half a DIP of where it started), the NEXT snap in the travel direction is taken instead so a
    /// flick always advances one page.</summary>
    public static double Target(double natural, in SnapGrid grid, bool impulse, double fromOffset)
    {
        if (grid.IsEmpty) return natural;

        double best = natural;
        double bestDist = double.PositiveInfinity;
        if (grid.Interval > 0.0)
        {
            double cand = SnapRepeated(natural, grid.Interval, grid.Start, grid.End);
            double d = Math.Abs(cand - natural);
            if (d < bestDist) { best = cand; bestDist = d; }
        }
        if (grid.Points is { Length: > 0 } pts)
        {
            double cand = SnapIrregular(natural, pts);
            double d = Math.Abs(cand - natural);
            if (d < bestDist) { best = cand; bestDist = d; }
        }
        if (!impulse) return best;

        double startSnap = Target(fromOffset, in grid, impulse: false, fromOffset);
        if (Math.Abs(best - startSnap) < 0.5)
        {
            double dir = natural - fromOffset;
            if (Math.Abs(dir) > 0.0001)
                best = NextSnap(startSnap, dir > 0.0, in grid);
        }
        return best;
    }

    /// <summary>Re-authors a freshly authored fling (<see cref="MotionKind.Fling"/>, first segment
    /// <see cref="SegKind.Decay"/>) so it lands EXACTLY on the grid, in finite time, velocity-continuous at the lift
    /// (WinUI re-solves the inertia decay for a snap point the same way). The decay's natural rest <c>P0 + V0/K</c> is
    /// snapped to <c>target</c>; the release velocity <c>V0</c> is kept and the decay RATE re-solved so the coast
    /// arrives at <c>target</c> precisely when its speed has fallen to <see cref="MotionFeel.SettleVelocity"/> (below
    /// perception): with <c>p(t) = P0 + (V0 − v(t))/K</c>, <c>p(tA) = target</c> at <c>v(tA) = vs</c> gives
    /// <c>K = (V0 − vs)/(target − P0)</c> and <c>tA = T0 + ln(V0/vs)/K</c>. A <see cref="SegKind.Hold"/> at
    /// <c>target</c> follows from <c>tA</c>. Both segments are closed forms of absolute time, so the landing is
    /// independent of how the plan is sampled. When the snapped target lies against the release direction (or the
    /// release is already below the arrival speed) a velocity-continuous <see cref="SegKind.Glide"/> to the target is
    /// authored instead. A plan that is not a fling, or an empty grid, is returned unchanged.</summary>
    public static ScrollPlan ResolveFling(in ScrollPlan fling, in SnapGrid grid, double fromOffset, in MotionFeel feel)
    {
        if (grid.IsEmpty || fling.Kind != MotionKind.Fling || fling.Count == 0 || fling.S0.Kind != SegKind.Decay) return fling;
        MotionSeg d = fling.S0;
        if (d.K <= 0.0) return fling;
        double natural = d.P0 + d.V0 / d.K;
        double target = Math.Clamp(Target(natural, in grid, impulse: true, fromOffset), fling.Min, fling.Max);
        double travel = target - d.P0;
        double vs = Math.Abs(feel.SettleVelocity) * Math.Sign(travel);
        bool solvable = travel != 0.0 && Math.Sign(d.V0) == Math.Sign(travel) && Math.Abs(d.V0) > Math.Abs(vs) && vs != 0.0;
        if (!solvable)
        {
            var glide = new MotionSeg(SegKind.Glide, d.T0, double.PositiveInfinity, d.P0, target, d.V0, feel.GlideOmega, 1.0);
            return new ScrollPlan(glide, default, default, default, 1, fling.Vp, fling.Gen, fling.Seq, fling.Min, fling.Max,
                fling.ViewportExtent, fling.RubberC, fling.Clock, OverpanPolicy.None, MotionKind.Fling, default);
        }
        double k = (d.V0 - vs) / travel;
        double tArrive = d.T0 + Math.Log(d.V0 / vs) / k;
        var coast = new MotionSeg(SegKind.Decay, d.T0, tArrive, d.P0, 0.0, d.V0, k);
        var rest = new MotionSeg(SegKind.Hold, tArrive, double.PositiveInfinity, target, target);
        return new ScrollPlan(coast, rest, default, default, 2, fling.Vp, fling.Gen, fling.Seq, fling.Min, fling.Max,
            fling.ViewportExtent, fling.RubberC, fling.Clock, OverpanPolicy.None, MotionKind.Fling, default);
    }

    private static double SnapRepeated(double value, double interval, double start, double end)
    {
        double prev = Math.Floor((value - start) / interval) * interval + start;
        double next = prev + interval;
        double snapped = (value - prev) <= (next - value) ? prev : next;
        if (end > start) snapped = Math.Clamp(snapped, start, end);
        else if (snapped < start) snapped = start;
        return snapped;
    }

    private static double SnapIrregular(double value, float[] pts)
    {
        double best = pts[0];
        double bestDist = Math.Abs(pts[0] - value);
        for (int i = 1; i < pts.Length; i++)
        {
            double d = Math.Abs(pts[i] - value);
            if (d < bestDist) { best = pts[i]; bestDist = d; }
        }
        return best;
    }

    private static double NextSnap(double from, bool forward, in SnapGrid grid)
    {
        double best = from;
        double bestGap = double.PositiveInfinity;
        if (grid.Interval > 0.0)
        {
            double cand = forward ? from + grid.Interval : from - grid.Interval;
            if (grid.End > grid.Start) cand = Math.Clamp(cand, grid.Start, grid.End);
            double gap = Math.Abs(cand - from);
            if (gap > 0.5 && gap < bestGap) { best = cand; bestGap = gap; }
        }
        if (grid.Points is { Length: > 0 } pts)
        {
            for (int i = 0; i < pts.Length; i++)
            {
                double p = pts[i];
                bool side = forward ? p > from + 0.5 : p < from - 0.5;
                if (!side) continue;
                double gap = Math.Abs(p - from);
                if (gap < bestGap) { best = p; bestGap = gap; }
            }
        }
        return best;
    }
}
