using System;
using System.Collections.Generic;
using FluentGpu.Controls;
using FluentGpu.Foundation;
using FluentGpu.ScrollLab.Record;
using FluentGpu.ScrollLab.Surfaces;
using FluentGpu.Scroll.Diag;
using FluentGpu.Scroll.Diag.Analysis;

namespace FluentGpu.ScrollLab.Analysis;

/// <summary>One analyzed session: its series, the metric verdicts, and the chart projections the Analysis page draws
/// (computed once, off the frame path).</summary>
public sealed record AnalysisResult(string Title, string Folder, SessionSeries Series, IReadOnlyList<MetricResult> Metrics,
                                    OffsetTrace Trace, double DurationS, CartesianData? PassTimeline)
{
    public static AnalysisResult From(SessionData d, string folder)
    {
        var series = d.ToSeries();
        return new AnalysisResult(
            d.Name + "  ·  " + ScrollSurfaces.NameOf(d.Surface) + "  ·  " + d.StartedLocal.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture),
            folder,
            series,
            Concat(ScrollMetrics.All(series), FrameMetrics.From(d, series)),
            OffsetTrace.From(series, Math.Max(d.DurationS, series.DurationS), AnalysisPage.TickColor, AnalysisPage.BandColor),
            d.DurationS,
            FrameMetrics.PassTimeline(d));
    }

    static IReadOnlyList<MetricResult> Concat(IReadOnlyList<MetricResult> a, IReadOnlyList<MetricResult> b)
    {
        var all = new List<MetricResult>(a.Count + b.Count);
        all.AddRange(a);
        all.AddRange(b);
        return all;
    }
}

/// <summary>
/// The offset-vs-time chart data: the pose series resampled onto at most <see cref="MaxBuckets"/> uniform time buckets
/// of <see cref="BucketS"/> over [0, <see cref="SpanS"/>] (bucket i = category i, centred at (i + ½)·BucketS), plus the
/// wheel notches as chart marks and the F8 "felt wrong" presses as chart bands — both in the chart's own CATEGORY units
/// (time t ↦ t / BucketS − ½), so the LineChart places them itself.
/// </summary>
public sealed record OffsetTrace(string[] Categories, float[] Offsets, double SpanS, double BucketS,
                                 ChartMark[] NotchMarks, ChartBand[] FeltBands)
{
    public const int MaxBuckets = 600;
    /// <summary>An F8 band spans ± this (s) around the press.</summary>
    public const double FeltHalfWidthS = 0.25;
    public const int MaxNotchMarks = 400;

    public static OffsetTrace From(SessionSeries s, double durationS, ColorF notchInk, ColorF feltFill)
    {
        double span = Math.Max(durationS, 0.1);
        int n = Math.Clamp((int)Math.Ceiling(span / 0.01), 2, MaxBuckets);
        double dt = span / n;
        var categories = new string[n];
        var offsets = new float[n];
        for (int i = 0; i < n; i++)
        {
            double t = (i + 0.5) * dt;
            categories[i] = t.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) + " s";
            double p = s.PosAt(t);
            offsets[i] = double.IsNaN(p) ? float.NaN : (float)p;
        }
        float Category(double t) => (float)(t / dt - 0.5);

        var notchTimes = new List<double>();
        foreach (var input in s.Inputs)
            if (input.IsNotch && input.T >= 0.0 && input.T <= span) notchTimes.Add(input.T);
        int step = Math.Max(1, (notchTimes.Count + MaxNotchMarks - 1) / MaxNotchMarks);   // a long spin thins, never floods
        var marks = new List<ChartMark>();
        for (int i = 0; i < notchTimes.Count; i += step) marks.Add(new ChartMark(Category(notchTimes[i]), notchInk));

        var bands = new List<ChartBand>();
        foreach (var m in s.Markers)
            if (m.Code == ProbeMark.UserFelt)
                bands.Add(new ChartBand(Category(m.T - FeltHalfWidthS), Category(m.T + FeltHalfWidthS), feltFill, "F8"));
        return new OffsetTrace(categories, offsets, span, dt, marks.ToArray(), bands.ToArray());
    }
}
