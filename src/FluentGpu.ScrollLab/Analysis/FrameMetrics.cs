using System;
using System.Collections.Generic;
using System.Globalization;
using FluentGpu.Controls;
using FluentGpu.Rhi;
using FluentGpu.ScrollLab.Record;
using FluentGpu.Scroll.Diag.Analysis;

namespace FluentGpu.ScrollLab.Analysis;

/// <summary>
/// The lab-side verdicts over a session's per-frame telemetry (<see cref="FrameSample"/>): the render thread's missed
/// motion ticks (the pacing cliff — 0 on a healthy run), the whole-frame GPU execution against the refresh period, and
/// (when "GPU passes" was on) the pass-granular timeline. Pure functions of the recorded samples.
/// </summary>
public static class FrameMetrics
{
    public const string MissedTicksName = "Missed motion ticks";
    public const string GpuExecutionName = "GPU execution";
    public const string GpuPassesName = "GPU passes";

    public static IReadOnlyList<MetricResult> From(SessionData d, SessionSeries s)
    {
        var list = new List<MetricResult>(3);
        var f = d.Frames;
        double trMs = s.RefreshPeriodS * 1000.0;
        var ci = CultureInfo.InvariantCulture;
        if (f.Length < 2)
        {
            list.Add(MetricResult.Empty(MissedTicksName, "ticks", "no frame telemetry in this session"));
            return list;
        }

        var first = f[0];
        var last = f[^1];
        long missed = last.MissedMotionTicks - first.MissedMotionTicks;
        long skipped = last.SkippedTicks - first.SkippedTicks;
        long races = last.RaceHits - first.RaceHits;
        long motion = last.MotionPresents - first.MotionPresents;
        long freshP = last.FreshPresents - first.FreshPresents;
        list.Add(new MetricResult(MissedTicksName, missed, 0.0, missed == 0 ? MetricVerdict.Green : MetricVerdict.Red, "ticks",
            Array.Empty<MarkerSample>())
        {
            Samples = f.Length,
            Detail = string.Format(ci, "{0} fresh + {1} motion presents, {2} skipped ticks, {3} race hits over {4} frames",
                freshP, motion, skipped, races, f.Length),
        });

        var gpu = new List<double>();
        foreach (var x in f) if (x.GpuExecutionMs > 0.0) gpu.Add(x.GpuExecutionMs);
        if (gpu.Count == 0) list.Add(MetricResult.Empty(GpuExecutionName, "ms", "the backend reported no GPU execution samples"));
        else
        {
            gpu.Sort();
            double med = ScrollMetrics.Median(gpu);
            double p95 = gpu[Math.Min(gpu.Count - 1, (int)(gpu.Count * 0.95))];
            var verdict = p95 <= 0.5 * trMs ? MetricVerdict.Green : p95 <= 0.8 * trMs ? MetricVerdict.Amber : MetricVerdict.Red;
            list.Add(new MetricResult(GpuExecutionName, med, trMs, verdict, "ms", Array.Empty<MarkerSample>())
            {
                Samples = gpu.Count,
                Detail = string.Format(ci, "median {0:0.00} ms, p95 {1:0.00} ms vs the {2:0.00} ms refresh period (G p95 ≤ 50 %, A ≤ 80 %)", med, p95, trMs),
            });
        }

        var passes = PassFrames(d);
        if (passes.Count == 0) list.Add(MetricResult.Empty(GpuPassesName, "ms", "GPU passes was off during this session"));
        else
        {
            var whole = new List<double>(passes.Count);
            var perKind = new List<double>[FrameLog.KindCount];
            for (int k = 0; k < perKind.Length; k++) perKind[k] = new List<double>(passes.Count);
            foreach (int i in passes)
            {
                whole.Add(f[i].PassWholeMs);
                for (int k = 0; k < FrameLog.KindCount; k++) perKind[k].Add(d.FramePassMs[i * FrameLog.KindCount + k]);
            }
            var parts = new List<string>();
            for (int k = 0; k < FrameLog.KindCount; k++)
            {
                double m = ScrollMetrics.Median(perKind[k]);
                if (m > 0.005) parts.Add(((GpuPassKind)k).ToString() + " " + m.ToString("0.00", ci));
            }
            list.Add(new MetricResult(GpuPassesName, ScrollMetrics.Median(whole), trMs, MetricVerdict.Info, "ms", Array.Empty<MarkerSample>())
            {
                Samples = passes.Count,
                Detail = "median per kind: " + string.Join(" · ", parts),
            });
        }
        return list;
    }

    /// <summary>Indices of frames that carried a fresh pass timeline.</summary>
    public static List<int> PassFrames(SessionData d)
    {
        var idx = new List<int>();
        for (int i = 0; i < d.Frames.Length; i++) if (d.Frames[i].PassCount > 0) idx.Add(i);
        return idx;
    }

    /// <summary>The per-frame pass timeline as a stacked bar chart's data: one category per sampled frame (thinned to
    /// ≤ <paramref name="maxBars"/>), one series per pass kind that ever took time. Null when there is none.</summary>
    public static CartesianData? PassTimeline(SessionData d, int maxBars = 160)
    {
        var frames = PassFrames(d);
        if (frames.Count == 0) return null;
        int step = Math.Max(1, (frames.Count + maxBars - 1) / maxBars);
        var picked = new List<int>();
        for (int i = 0; i < frames.Count; i += step) picked.Add(frames[i]);

        var kinds = new List<int>();
        for (int k = 0; k < FrameLog.KindCount; k++)
        {
            foreach (int i in picked)
                if (d.FramePassMs[i * FrameLog.KindCount + k] > 0f) { kinds.Add(k); break; }
        }
        var keys = new (string Key, string Label)[kinds.Count];
        for (int j = 0; j < kinds.Count; j++) keys[j] = (((GpuPassKind)kinds[j]).ToString(), ((GpuPassKind)kinds[j]).ToString());
        var series = ChartPalette.Series(keys);
        var values = new float[kinds.Count][];
        for (int j = 0; j < kinds.Count; j++)
        {
            values[j] = new float[picked.Count];
            for (int c = 0; c < picked.Count; c++) values[j][c] = d.FramePassMs[picked[c] * FrameLog.KindCount + kinds[j]];
        }
        var categories = new string[picked.Count];
        for (int c = 0; c < picked.Count; c++)
            categories[c] = ((d.Frames[picked[c]].Qpc - d.StartQpc) / d.QpcFrequency).ToString("0.00", CultureInfo.InvariantCulture) + " s";
        return new CartesianData(categories, series, values);
    }
}
