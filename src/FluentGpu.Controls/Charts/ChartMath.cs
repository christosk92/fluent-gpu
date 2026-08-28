using System;
using FluentGpu.Foundation;
using FluentGpu.Render;

namespace FluentGpu.Controls;

/// <summary>Line/area interpolation. <see cref="Natural"/> (d3 <c>curveNatural</c>) is the shadcn/ui registry default
/// (22 of 29 cartesian examples); <see cref="Monotone"/> is Fritsch–Carlson (no overshoot on monotone data);
/// <see cref="Step"/> is step-after; <see cref="Linear"/> is straight segments.</summary>
public enum ChartCurve : byte { Linear = 0, Step = 1, Monotone = 2, Natural = 3 }

/// <summary>How multiple series combine on the value axis: side by side (<see cref="None"/>), cumulative
/// (<see cref="Stacked"/>, Recharts <c>stackId</c>), or row-normalised to 0..1 (<see cref="Expand"/>,
/// <c>stackOffset="expand"</c>).</summary>
public enum ChartStacking : byte { None = 0, Stacked = 1, Expand = 2 }

/// <summary>
/// Pure chart arithmetic shared by every chart in the kit — density estimation, stacking, nice ticks, curve
/// interpolation, hover resolution. Allocation-free over caller-provided spans so VerticalSlice can gate it without a
/// scene (<c>gate.ctl.charts.math.*</c>), and so the live components never diverge from the tests. No element types.
/// </summary>
public static class ChartMath
{
    // ── Distributions ─────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The default Gaussian bandwidth for a domain: 1/28 of its span (a 60–200 bpm axis → 5 bpm). Under about
    /// 1/50 of the span integer-valued data combs into spikes; over about 1/10 every shape is one hump.</summary>
    public static float DefaultBandwidth(float lo, float hi) => MathF.Max(1e-6f, (hi - lo) / 28f);

    /// <summary>Gaussian kernel density of <paramref name="values"/> sampled uniformly over <c>[lo, hi]</c> into
    /// <paramref name="dst"/> (<c>dst.Length</c> samples), normalised so the peak sample is exactly 1. Values outside the
    /// domain are CLAMPED to the edge rather than dropped — an outlier still raises the edge. Empty input, a non-positive
    /// span or bandwidth ⇒ all zeros.</summary>
    public static void Kde(ReadOnlySpan<float> values, float lo, float hi, float bandwidth, Span<float> dst)
    {
        dst.Clear();
        int n = dst.Length;
        if (n == 0 || values.Length == 0 || !(hi > lo) || !(bandwidth > 0f)) return;

        // BIN FIRST: the cost is then O(occupied bins × samples), independent of how many values there are (a
        // 5 000-track library costs the same as a 50-track playlist). Each bin remembers the exact MEAN of its values,
        // so a bin holding identical values contributes exactly what the direct sum would; the residual error is the
        // in-bin spread, ≤ span/Bins — far under any sensible bandwidth (the default is span/28).
        const int Bins = 1024;
        Span<float> weight = stackalloc float[Bins + 1];
        Span<float> sum = stackalloc float[Bins + 1];
        float span = hi - lo;
        for (int k = 0; k < values.Length; k++)
        {
            float v = values[k];
            if (float.IsNaN(v)) continue;
            v = v < lo ? lo : v > hi ? hi : v;
            int b = (int)((v - lo) / span * Bins);
            if (b > Bins) b = Bins; else if (b < 0) b = 0;
            weight[b] += 1f;
            sum[b] += v;
        }

        float inv = 1f / bandwidth;
        float peak = 0f;
        for (int i = 0; i < n; i++)
        {
            float x = n == 1 ? (lo + hi) * 0.5f : lo + span * i / (n - 1);
            float d = 0f;
            for (int b = 0; b <= Bins; b++)
            {
                float w = weight[b];
                if (w == 0f) continue;
                float z = (sum[b] / w - x) * inv;
                d += w * MathF.Exp(-0.5f * z * z);
            }
            dst[i] = d;
            if (d > peak) peak = d;
        }
        if (peak <= 0f) return;
        float scale = 1f / peak;
        for (int i = 0; i < n; i++) dst[i] *= scale;
    }

    /// <summary>For each value, how many EARLIER values round to the same integer — the dot's stack depth in a rug
    /// (three 128s stack 0, 1, 2). NaN values get depth 0 and are never counted. Returns the deepest stack.</summary>
    public static int RugStacks(ReadOnlySpan<float> values, Span<int> stack)
    {
        int deepest = 0;
        for (int i = 0; i < values.Length && i < stack.Length; i++)
        {
            float v = values[i];
            if (float.IsNaN(v)) { stack[i] = 0; continue; }
            int key = (int)MathF.Round(v);
            int depth = 0;
            for (int j = 0; j < i; j++)
            {
                float u = values[j];
                if (!float.IsNaN(u) && (int)MathF.Round(u) == key) depth++;
            }
            stack[i] = depth;
            if (depth > deepest) deepest = depth;
        }
        return deepest;
    }

    /// <summary>The largest count's share of the total (0 when the total is 0); <paramref name="topIndex"/> is its
    /// index (first wins a tie; −1 when the span is empty).</summary>
    public static float Share(ReadOnlySpan<int> counts, out int topIndex)
    {
        topIndex = -1;
        long total = 0;
        int top = 0;
        for (int i = 0; i < counts.Length; i++)
        {
            int c = counts[i];
            if (c < 0) c = 0;
            total += c;
            if (topIndex < 0 || c > top) { top = c; topIndex = i; }
        }
        return total > 0 ? top / (float)total : 0f;
    }

    // ── Cartesian domains ─────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The value extent of a series table (<c>values[series][point]</c>; NaN = gap, ignored), per the
    /// stacking mode: <see cref="ChartStacking.Stacked"/> measures the per-point cumulative sums (positive and negative
    /// stacks separately), <see cref="ChartStacking.Expand"/> is always 0..1. <paramref name="includeZero"/> folds 0
    /// into the extent (bars always grow from zero). Empty ⇒ (0, 0).</summary>
    public static (float Min, float Max) Extent(float[][] values, ChartStacking stacking, bool includeZero)
    {
        if (stacking == ChartStacking.Expand) return (0f, 1f);
        float min = float.PositiveInfinity, max = float.NegativeInfinity;
        int points = PointCount(values);
        for (int i = 0; i < points; i++)
        {
            float pos = 0f, neg = 0f;
            for (int s = 0; s < values.Length; s++)
            {
                var row = values[s];
                if (row is null || i >= row.Length) continue;
                float v = row[i];
                if (float.IsNaN(v)) continue;
                if (stacking == ChartStacking.Stacked)
                {
                    if (v >= 0f) pos += v; else neg += v;
                }
                else
                {
                    if (v < min) min = v;
                    if (v > max) max = v;
                }
            }
            if (stacking == ChartStacking.Stacked)
            {
                if (pos < min) min = pos; if (pos > max) max = pos;
                if (neg < min) min = neg; if (neg > max) max = neg;
            }
        }
        if (float.IsPositiveInfinity(min)) return (0f, 0f);
        if (includeZero) { if (min > 0f) min = 0f; if (max < 0f) max = 0f; }
        return (min, max);
    }

    /// <summary>Number of points in the widest series row (0 for an empty table).</summary>
    public static int PointCount(float[][] values)
    {
        int n = 0;
        if (values is null) return 0;
        for (int s = 0; s < values.Length; s++) if (values[s] is { Length: var l } && l > n) n = l;
        return n;
    }

    /// <summary>d3's <c>tickIncrement</c>: the 1/2/5·10^k step that yields about <paramref name="count"/> ticks
    /// over <c>[min, max]</c>. Returns 0 for a degenerate range.</summary>
    public static float NiceStep(float min, float max, int count)
    {
        if (count < 1) count = 1;
        float span = max - min;
        if (!(span > 0f)) return 0f;
        float step0 = span / count;
        float power = MathF.Floor(MathF.Log10(step0));
        float p10 = MathF.Pow(10f, power);
        float error = step0 / p10;
        float factor = error >= 7.0710678f ? 10f : error >= 3.1622777f ? 5f : error >= 1.4142135f ? 2f : 1f;
        return factor * p10;
    }

    /// <summary>d3 <c>nice()</c>: widen <c>[min, max]</c> to step multiples (<paramref name="niceMin"/>,
    /// <paramref name="niceMax"/>) and write the ticks from niceMin to niceMax inclusive into <paramref name="dst"/>.
    /// A flat range (min == max) is padded to one step around the value (0 becomes 0..1). Returns the tick count
    /// (never more than <c>dst.Length</c>).</summary>
    public static int NiceTicks(float min, float max, int count, Span<float> dst, out float niceMin, out float niceMax, out float step)
    {
        if (max < min) (min, max) = (max, min);
        if (max - min <= 1e-6f)
        {
            float pad = MathF.Abs(min) > 1e-6f ? MathF.Abs(min) * 0.5f : 1f;
            min -= pad; max += pad;
        }
        step = NiceStep(min, max, count);
        if (!(step > 0f)) { niceMin = min; niceMax = max; if (dst.Length > 0) dst[0] = min; return dst.Length > 0 ? 1 : 0; }
        niceMin = MathF.Floor(min / step + 1e-4f) * step;
        niceMax = MathF.Ceiling(max / step - 1e-4f) * step;
        // Snap float noise (1.2000001 → 1.2) so tick labels format cleanly.
        niceMin = MathF.Round(niceMin / step) * step;
        niceMax = MathF.Round(niceMax / step) * step;
        int n = 0;
        int ticks = (int)MathF.Round((niceMax - niceMin) / step) + 1;
        for (int i = 0; i < ticks && n < dst.Length; i++) dst[n++] = niceMin + i * step;
        return n;
    }

    /// <summary>Cumulative upper edges per series (<c>cumulative[s][i] = Σ_{k≤s} values[k][i]</c>), positive and
    /// negative values stacking away from zero independently (Recharts <c>stackId</c> semantics). NaN reads as 0 in
    /// the running sum but stays NaN in its own cell so the point still renders as a gap.</summary>
    public static void Stack(float[][] values, float[][] cumulative)
    {
        int points = PointCount(values);
        for (int i = 0; i < points; i++)
        {
            float pos = 0f, neg = 0f;
            for (int s = 0; s < values.Length; s++)
            {
                var row = values[s];
                var dst = cumulative[s];
                if (row is null || i >= row.Length) { if (dst is not null && i < dst.Length) dst[i] = float.NaN; continue; }
                float v = row[i];
                if (float.IsNaN(v)) { dst[i] = float.NaN; continue; }
                if (v >= 0f) { pos += v; dst[i] = pos; } else { neg += v; dst[i] = neg; }
            }
        }
    }

    /// <summary>Row-normalised stacking (<c>stackOffset="expand"</c>): each point's values divided by that point's
    /// total of absolute values, then stacked, so every column's upper edge is 1. An all-zero column stays 0.</summary>
    public static void Expand(float[][] values, float[][] normalised)
    {
        int points = PointCount(values);
        for (int i = 0; i < points; i++)
        {
            float total = 0f;
            for (int s = 0; s < values.Length; s++)
            {
                var row = values[s];
                if (row is null || i >= row.Length) continue;
                float v = row[i];
                if (!float.IsNaN(v)) total += MathF.Abs(v);
            }
            float acc = 0f;
            for (int s = 0; s < values.Length; s++)
            {
                var row = values[s];
                var dst = normalised[s];
                if (row is null || i >= row.Length) { dst[i] = float.NaN; continue; }
                float v = row[i];
                if (float.IsNaN(v)) { dst[i] = float.NaN; continue; }
                acc += total > 0f ? MathF.Abs(v) / total : 0f;
                dst[i] = acc;
            }
        }
    }

    // ── Curves ────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Append <paramref name="pts"/> to <paramref name="b"/> as one open contour interpolated per
    /// <paramref name="kind"/>. A NaN coordinate splits the contour (a gap in the series). Returns the number of
    /// contours started. Cubic control points come from <see cref="CubicControls"/>, so the tessellator receives true
    /// curves (gpu-renderer.md §5 — subdivision is its job, not ours).</summary>
    public static int AppendCurve(PathBuilder b, ReadOnlySpan<Point2> pts, ChartCurve kind, Span<Point2> scratch)
    {
        int contours = 0;
        int start = 0;
        while (start < pts.Length)
        {
            while (start < pts.Length && IsGap(pts[start])) start++;
            int end = start;
            while (end < pts.Length && !IsGap(pts[end])) end++;
            if (end > start)
            {
                AppendRun(b, pts.Slice(start, end - start), kind, scratch);
                contours++;
            }
            start = end;
        }
        return contours;
    }

    static bool IsGap(in Point2 p) => float.IsNaN(p.X) || float.IsNaN(p.Y);

    static void AppendRun(PathBuilder b, ReadOnlySpan<Point2> run, ChartCurve kind, Span<Point2> scratch)
    {
        b.MoveTo(run[0].X, run[0].Y);
        if (run.Length == 1) return;
        switch (kind)
        {
            case ChartCurve.Linear:
                for (int i = 1; i < run.Length; i++) b.LineTo(run[i].X, run[i].Y);
                return;
            case ChartCurve.Step:
                // step-after: hold the previous value until the next x, then rise.
                for (int i = 1; i < run.Length; i++) { b.LineTo(run[i].X, run[i - 1].Y); b.LineTo(run[i].X, run[i].Y); }
                return;
            default:
                if (run.Length == 2) { b.LineTo(run[1].X, run[1].Y); return; }
                int need = (run.Length - 1) * 2;
                Span<Point2> ctrl = scratch.Length >= need ? scratch.Slice(0, need) : new Point2[need];
                CubicControls(run, kind, ctrl);
                for (int i = 1; i < run.Length; i++)
                {
                    var c1 = ctrl[(i - 1) * 2];
                    var c2 = ctrl[(i - 1) * 2 + 1];
                    b.CubicTo(c1.X, c1.Y, c2.X, c2.Y, run[i].X, run[i].Y);
                }
                return;
        }
    }

    /// <summary>The two cubic Bézier control points per segment (<c>ctrl[2k]</c>, <c>ctrl[2k+1]</c> for the segment
    /// <c>pts[k] → pts[k+1]</c>) for <see cref="ChartCurve.Monotone"/> (Fritsch–Carlson tangents — never overshoots
    /// monotone data) or <see cref="ChartCurve.Natural"/> (the C2 natural cubic spline, a tridiagonal solve per axis).
    /// Other kinds produce the linear controls (1/3, 2/3 along the chord). Needs ≥ 2 points; <c>ctrl.Length ≥ 2(n−1)</c>.</summary>
    public static void CubicControls(ReadOnlySpan<Point2> pts, ChartCurve kind, Span<Point2> ctrl)
    {
        int n = pts.Length;
        if (n < 2) return;
        if (kind == ChartCurve.Natural && n >= 3) { NaturalControls(pts, ctrl); return; }
        if (kind == ChartCurve.Monotone && n >= 3) { MonotoneControls(pts, ctrl); return; }
        for (int i = 0; i < n - 1; i++)
        {
            var p0 = pts[i]; var p1 = pts[i + 1];
            ctrl[i * 2] = new Point2(p0.X + (p1.X - p0.X) / 3f, p0.Y + (p1.Y - p0.Y) / 3f);
            ctrl[i * 2 + 1] = new Point2(p0.X + 2f * (p1.X - p0.X) / 3f, p0.Y + 2f * (p1.Y - p0.Y) / 3f);
        }
    }

    // d3-shape curveMonotoneX: slopes via the Fritsch–Carlson three-point rule, end slopes via slope2.
    static void MonotoneControls(ReadOnlySpan<Point2> p, Span<Point2> ctrl)
    {
        int n = p.Length;
        float tPrev = Slope3(p[0], p[1], p[2]);
        float t0 = Slope2(p[0], p[1], tPrev);
        Emit(ctrl, 0, p[0], p[1], t0, tPrev);
        for (int i = 1; i < n - 2; i++)
        {
            float t1 = Slope3(p[i], p[i + 1], p[i + 2]);
            Emit(ctrl, i, p[i], p[i + 1], tPrev, t1);
            tPrev = t1;
        }
        float tEnd = Slope2(p[n - 2], p[n - 1], tPrev);
        Emit(ctrl, n - 2, p[n - 2], p[n - 1], tPrev, tEnd);

        static void Emit(Span<Point2> ctrl, int k, in Point2 a, in Point2 b, float ta, float tb)
        {
            float dx = (b.X - a.X) / 3f;
            ctrl[k * 2] = new Point2(a.X + dx, a.Y + dx * ta);
            ctrl[k * 2 + 1] = new Point2(b.X - dx, b.Y - dx * tb);
        }
        static float Slope3(in Point2 a, in Point2 b, in Point2 c)
        {
            float h0 = b.X - a.X, h1 = c.X - b.X;
            float s0 = (b.Y - a.Y) / (h0 != 0f ? h0 : (h1 < 0f ? -0f : 0f));
            float s1 = (c.Y - b.Y) / (h1 != 0f ? h1 : (h0 < 0f ? -0f : 0f));
            float pp = (s0 * h1 + s1 * h0) / (h0 + h1);
            float m = (MathF.Sign(s0) + MathF.Sign(s1)) * MathF.Min(MathF.Min(MathF.Abs(s0), MathF.Abs(s1)), 0.5f * MathF.Abs(pp));
            return float.IsNaN(m) ? 0f : m;
        }
        static float Slope2(in Point2 a, in Point2 b, float t)
        {
            float h = b.X - a.X;
            return h != 0f ? (3f * (b.Y - a.Y) / h - t) / 2f : t;
        }
    }

    // d3-shape curveNatural: controlPoints(x) — the classic tridiagonal solve, run once per axis.
    static void NaturalControls(ReadOnlySpan<Point2> p, Span<Point2> ctrl)
    {
        int n = p.Length - 1;                       // segments
        Span<float> a = n <= 64 ? stackalloc float[n] : new float[n];
        Span<float> bb = n <= 64 ? stackalloc float[n] : new float[n];
        Span<float> r = n <= 64 ? stackalloc float[n] : new float[n];
        Span<float> px1 = n <= 64 ? stackalloc float[n] : new float[n];
        Span<float> px2 = n <= 64 ? stackalloc float[n] : new float[n];
        Span<float> py1 = n <= 64 ? stackalloc float[n] : new float[n];
        Span<float> py2 = n <= 64 ? stackalloc float[n] : new float[n];
        Solve(p, n, a, bb, r, px1, px2, static (in Point2 q) => q.X);
        Solve(p, n, a, bb, r, py1, py2, static (in Point2 q) => q.Y);
        for (int i = 0; i < n; i++)
        {
            ctrl[i * 2] = new Point2(px1[i], py1[i]);
            ctrl[i * 2 + 1] = new Point2(px2[i], py2[i]);
        }

        static void Solve(ReadOnlySpan<Point2> p, int n, Span<float> a, Span<float> b, Span<float> r,
                          Span<float> c1, Span<float> c2, Axis axis)
        {
            a[0] = 0f; b[0] = 2f; r[0] = axis(p[0]) + 2f * axis(p[1]);
            for (int i = 1; i < n - 1; i++) { a[i] = 1f; b[i] = 4f; r[i] = 4f * axis(p[i]) + 2f * axis(p[i + 1]); }
            a[n - 1] = 2f; b[n - 1] = 7f; r[n - 1] = 8f * axis(p[n - 1]) + axis(p[n]);
            for (int i = 1; i < n; i++) { float m = a[i] / b[i - 1]; b[i] -= m; r[i] -= m * r[i - 1]; }
            c1[n - 1] = r[n - 1] / b[n - 1];
            for (int i = n - 2; i >= 0; i--) c1[i] = (r[i] - c1[i + 1]) / b[i];
            c2[n - 1] = (axis(p[n]) + c1[n - 1]) / 2f;
            for (int i = 0; i < n - 1; i++) c2[i] = 2f * axis(p[i + 1]) - c1[i + 1];
        }
    }

    delegate float Axis(in Point2 p);

    // ── Hover / ticks ─────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The category whose slot centre is nearest <paramref name="x"/> when <paramref name="count"/> slots of
    /// <paramref name="step"/> start at <paramref name="x0"/>; −1 when there are no slots.</summary>
    public static int NearestIndex(float x, float x0, float step, int count)
    {
        if (count <= 0 || !(step > 0f)) return -1;
        int i = (int)MathF.Floor((x - x0) / step);
        return i < 0 ? 0 : i >= count ? count - 1 : i;
    }

    /// <summary>Greedy left-to-right tick thinning (Recharts <c>minTickGap</c> + <c>interval="preserveStartEnd"</c>):
    /// a tick is kept when its box (<c>xs[i] ± widths[i]/2</c>) clears the previously kept tick by
    /// <paramref name="minGap"/>. With <paramref name="preserveEnds"/> the LAST tick is always kept and whichever kept
    /// tick it would collide with is dropped instead. Returns the kept count.</summary>
    public static int DropCollidingTicks(ReadOnlySpan<float> xs, ReadOnlySpan<float> widths, float minGap, bool preserveEnds, Span<bool> keep)
    {
        int n = xs.Length, kept = 0;
        float lastRight = float.NegativeInfinity;
        int lastKept = -1;
        for (int i = 0; i < n; i++)
        {
            float half = (i < widths.Length ? widths[i] : 0f) * 0.5f;
            float left = xs[i] - half;
            bool ok = left - lastRight >= minGap || lastKept < 0;
            keep[i] = ok;
            if (ok) { lastRight = xs[i] + half; lastKept = i; kept++; }
        }
        if (preserveEnds && n > 1 && !keep[n - 1])
        {
            keep[n - 1] = true; kept++;
            float halfLast = (n - 1 < widths.Length ? widths[n - 1] : 0f) * 0.5f;
            float leftLast = xs[n - 1] - halfLast;
            for (int i = n - 2; i >= 0; i--)
            {
                if (!keep[i]) continue;
                float half = (i < widths.Length ? widths[i] : 0f) * 0.5f;
                if (leftLast - (xs[i] + half) < minGap) { keep[i] = false; kept--; }
                else break;
            }
        }
        return kept;
    }

    /// <summary>A layout-time estimate of a label's advance: ~0.55 em per character at <paramref name="fontSize"/>.
    /// Used only to thin axis ticks before text is shaped; the shaped run may differ by a few px.</summary>
    public static float EstimateTextWidth(string? text, float fontSize) => (text?.Length ?? 0) * fontSize * 0.55f;
}
