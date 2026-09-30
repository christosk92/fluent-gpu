using System;
using System.Collections.Generic;
using System.Globalization;
using FluentGpu.Scroll.Motion;

namespace FluentGpu.Scroll.Diag.Analysis;

/// <summary>A metric's call against its baseline. <see cref="Info"/> = reported with no pass/fail threshold (no baseline
/// exists yet — phase 2 brings the WinUI/Edge ones); <see cref="NoData"/> = the session held nothing to measure.</summary>
public enum MetricVerdict : byte
{
    NoData = 0,
    Green = 1,
    Amber = 2,
    Red = 3,
    Info = 4,
}

/// <summary>One metric's result (scroll-lab plan §5): the measured <see cref="Value"/> vs its <see cref="Baseline"/>,
/// the <see cref="Verdict"/>, the <see cref="Unit"/>, and the markers (F8 "felt wrong" etc.) within
/// <see cref="ScrollMetrics.MarkerWindowS"/> of the worst sample (<see cref="WorstT"/>, session seconds).</summary>
public sealed record MetricResult(string Name, double Value, double Baseline, MetricVerdict Verdict, string Unit,
                                  MarkerSample[] MarkerHits)
{
    /// <summary>Session time (s) of the worst contributing sample; NaN when there is none.</summary>
    public double WorstT { get; init; } = double.NaN;
    /// <summary>How many samples/events the value aggregates.</summary>
    public int Samples { get; init; }
    /// <summary>A one-line human breakdown (off the hot path).</summary>
    public string Detail { get; init; } = "";

    public static MetricResult Empty(string name, string unit, string why)
        => new(name, double.NaN, double.NaN, MetricVerdict.NoData, unit, Array.Empty<MarkerSample>()) { Detail = why };
}

/// <summary>
/// The scroll-lab metrics catalog, rows 1–7 (<c>docs/plans/scroll-lab-implementation.md</c> §5) as PURE functions of a
/// <see cref="SessionSeries"/>: first-motion latency, notch→pose lag, displacement irregularity, attested
/// repeats/drops, per-notch curve shape (+ t50/t90), DIP per notch + sustained spin speed, and the stop metrics. Times
/// are QPC seconds, distances DIP. Phase-1 baselines are the plan's fixed thresholds and the default feel's own model
/// (<see cref="Reference"/> = <see cref="FeelProfiles.Standard"/>: 64 DIP / 0.15 s front-loaded cubic); phase 2 swaps
/// in recorded WinUI/Edge baselines.
/// <para>Two time references, deliberately: the LATENCY metrics (first-motion, notch→pose lag) measure from the input's
/// device stamp — what the user did — while the CURVE metrics (per-notch shape, t50/t90, DIP per notch) measure from the
/// authored plan's anchor (<see cref="PlanSample.T"/> — a notch stamped before the last posed present is anchored at that
/// pose floor, <c>PlanAuthor.WheelNotch</c>), so the present lead is counted once, as latency, never again as a
/// distorted curve. Without plan rows (a Summary-level recording) the curve metrics fall back to the stamp.</para>
/// </summary>
public static class ScrollMetrics
{
    /// <summary>A pose moved when its position changed by more than this (DIP) — analyze.py's noise floor.</summary>
    public const double MotionEpsilonDip = 0.05;
    /// <summary>Inputs separated by more than this (s) are separate gestures.</summary>
    public const double GestureGapS = 0.30;
    /// <summary>A notch is isolated when no other input lies within this (s) before it.</summary>
    public const double IsolatedGapS = 0.30;
    /// <summary>The per-notch curve window (s) — also the after-gap an isolated notch needs.</summary>
    public const double CurveWindowS = 0.40;
    /// <summary>How far after its stamp (s) a notch's wheel plan anchor may lie (the pose floor is at most a few
    /// refresh periods ahead of the stamp).</summary>
    public const double PlanAnchorSlackS = 0.10;
    /// <summary>Consecutive same-direction notches closer than this (s) are a spin.</summary>
    public const double SpinGapS = 0.12;
    /// <summary>Markers within ± this (s) of a metric's worst sample are attached to it.</summary>
    public const double MarkerWindowS = 0.50;
    /// <summary>Stop criterion: two consecutive frames each moving less than this (DIP per refresh period).</summary>
    public const double StopDipPerFrame = 1.0;

    /// <summary>The reference feel every phase-1 baseline is derived from (the engine's default wheel feel).</summary>
    public static readonly MotionFeel Reference = FeelProfiles.Standard;

    public const string FirstMotionName = "First-motion latency";
    public const string NotchLagName = "Notch→pose lag";
    public const string IrregularityName = "Displacement irregularity";
    public const string RepeatsName = "Repeats / drops";
    public const string MissedTickAttributionName = "Missed-tick attribution";
    public const string CurveShapeName = "Per-notch curve shape";
    public const string T50Name = "Notch t50";
    public const string T90Name = "Notch t90";
    public const string DipPerNotchName = "DIP per notch";
    public const string SpinSpeedName = "Sustained spin speed";
    public const string StopLatencyName = "Stop latency";
    public const string StopDistanceName = "Stop distance";
    public const string DecayName = "Decay rate";

    /// <summary>Every phase-1 metric, in catalog order.</summary>
    public static IReadOnlyList<MetricResult> All(SessionSeries s) => new[]
    {
        FirstMotionLatency(s),
        NotchToPoseLag(s),
        DisplacementIrregularity(s),
        RepeatsAndDrops(s),
        MissedTickAttribution(s),
        NotchCurveShape(s),
        NotchT50(s),
        NotchT90(s),
        DipPerNotch(s),
        SustainedSpinSpeed(s),
        StopLatency(s),
        StopDistance(s),
        DecayRate(s),
    };

    // ── 1. first-motion latency ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>Per gesture: <c>t_fm − n₀</c>, where <c>t_fm</c> is the first pose after the gesture's first input
    /// <c>n₀</c> whose position moved more than <see cref="MotionEpsilonDip"/> from the position shown at <c>n₀</c>.
    /// Value = the median over gestures (ms). Baseline = two refresh periods (the present lead); G ≤ base + 4 ms,
    /// A ≤ base + 12 ms.</summary>
    public static MetricResult FirstMotionLatency(SessionSeries s)
    {
        const string unit = "ms";
        if (s.Poses.Length == 0) return MetricResult.Empty(FirstMotionName, unit, "no poses");
        var gestures = Gestures(s);
        var values = new List<double>();
        double worst = double.NegativeInfinity, worstT = double.NaN;
        for (int g = 0; g < gestures.Count; g++)
        {
            double n0 = s.Inputs[gestures[g].First].T;
            double limit = g + 1 < gestures.Count ? s.Inputs[gestures[g + 1].First].T : double.PositiveInfinity;
            limit = Math.Min(limit, n0 + 1.0);
            int before = s.LastPoseAtOrBefore(n0);
            double p0 = before >= 0 ? s.Poses[before].Pos : s.Poses[0].Pos;
            for (int k = s.FirstPoseAtOrAfter(n0); k < s.Poses.Length && s.Poses[k].T <= limit; k++)
            {
                if (Math.Abs(s.Poses[k].Pos - p0) > MotionEpsilonDip)
                {
                    double lat = (s.Poses[k].T - n0) * 1000.0;
                    values.Add(lat);
                    if (lat > worst) { worst = lat; worstT = n0; }
                    break;
                }
            }
        }
        if (values.Count == 0) return MetricResult.Empty(FirstMotionName, unit, "no gesture moved the content");
        double value = Median(values);
        double baseline = 2.0 * s.RefreshPeriodS * 1000.0;
        var verdict = value <= baseline + 4.0 ? MetricVerdict.Green : value <= baseline + 12.0 ? MetricVerdict.Amber : MetricVerdict.Red;
        return new MetricResult(FirstMotionName, value, baseline, verdict, unit, MarkersNear(s, worstT))
        {
            WorstT = worstT,
            Samples = values.Count,
            Detail = Fmt("median {0:0.0} ms over {1} gestures, worst {2:0.0} ms; baseline 2×{3:0.00} ms", value, values.Count, worst, s.RefreshPeriodS * 1000.0),
        };
    }

    // ── 2. notch→pose lag ────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Per notch <c>nᵢ</c>: <c>min{t_k ≥ nᵢ} − nᵢ</c> (the first pose presented at/after it). Value = median (ms).
    /// Baseline = two presents; G ≤ 2·T_r, A ≤ 3·T_r. A notch with no pose within 0.5 s (an idle edge) is skipped.</summary>
    public static MetricResult NotchToPoseLag(SessionSeries s)
    {
        const string unit = "ms";
        var values = new List<double>();
        double worst = double.NegativeInfinity, worstT = double.NaN;
        for (int i = 0; i < s.Inputs.Length; i++)
        {
            if (!s.Inputs[i].IsNotch) continue;
            double n = s.Inputs[i].T;
            int k = s.FirstPoseAtOrAfter(n);
            if (k >= s.Poses.Length) continue;
            double lag = s.Poses[k].T - n;
            if (lag > 0.5) continue;
            values.Add(lag * 1000.0);
            if (lag * 1000.0 > worst) { worst = lag * 1000.0; worstT = n; }
        }
        if (values.Count == 0) return MetricResult.Empty(NotchLagName, unit, "no wheel notches with a following pose");
        double value = Median(values);
        double tr = s.RefreshPeriodS * 1000.0;
        double baseline = 2.0 * tr;
        var verdict = value <= 2.0 * tr ? MetricVerdict.Green : value <= 3.0 * tr ? MetricVerdict.Amber : MetricVerdict.Red;
        return new MetricResult(NotchLagName, value, baseline, verdict, unit, MarkersNear(s, worstT))
        {
            WorstT = worstT,
            Samples = values.Count,
            Detail = Fmt("median {0:0.0} ms over {1} notches, worst {2:0.0} ms (T_r {3:0.00} ms)", value, values.Count, worst, tr),
        };
    }

    // ── 3. displacement irregularity ─────────────────────────────────────────────────────────────────────────────

    /// <summary><c>σ(Δ²p) / mean|Δp|</c> over ACTIVE frames (runs of consecutive poses each moving more than
    /// <see cref="MotionEpsilonDip"/>; Δ²p is taken within a run only). Baseline 0.20 (the probe's own Uneven
    /// threshold); G &lt; 0.15, A &lt; 0.30.</summary>
    public static MetricResult DisplacementIrregularity(SessionSeries s)
    {
        const string unit = "ratio";
        var p = s.Poses;
        var d2 = new List<double>();
        var d2T = new List<double>();
        double absSum = 0.0;
        int absCount = 0;
        double prevD = 0.0;
        bool prevActive = false;
        for (int k = 1; k < p.Length; k++)
        {
            double d = p[k].Pos - p[k - 1].Pos;
            bool active = Math.Abs(d) > MotionEpsilonDip;
            if (active)
            {
                absSum += Math.Abs(d);
                absCount++;
                if (prevActive) { d2.Add(d - prevD); d2T.Add(p[k].T); }
            }
            prevD = d;
            prevActive = active;
        }
        if (d2.Count < 2 || absCount < 3) return MetricResult.Empty(IrregularityName, unit, "fewer than 3 moving frames");
        double mean = 0.0;
        for (int i = 0; i < d2.Count; i++) mean += d2[i];
        mean /= d2.Count;
        double variance = 0.0, worstDev = -1.0, worstT = double.NaN;
        for (int i = 0; i < d2.Count; i++)
        {
            double dev = d2[i] - mean;
            variance += dev * dev;
            if (Math.Abs(dev) > worstDev) { worstDev = Math.Abs(dev); worstT = d2T[i]; }
        }
        double sigma = Math.Sqrt(variance / d2.Count);
        double meanAbs = absSum / absCount;
        double value = sigma / Math.Max(meanAbs, 1e-9);
        var verdict = value < 0.15 ? MetricVerdict.Green : value < 0.30 ? MetricVerdict.Amber : MetricVerdict.Red;
        return new MetricResult(IrregularityName, value, 0.20, verdict, unit, MarkersNear(s, worstT))
        {
            WorstT = worstT,
            Samples = absCount,
            Detail = Fmt("σ(Δ²p) {0:0.000} DIP / mean|Δp| {1:0.000} DIP over {2} moving frames", sigma, meanAbs, absCount),
        };
    }

    // ── 4. attested repeats / drops ──────────────────────────────────────────────────────────────────────────────

    /// <summary>A display repeat within ± this many refresh periods of a missed tick is that tick seen again: the ledger
    /// learns of a displayed present about one flip after its submit, and a UI row reads it up to a frame later.</summary>
    public const double RepeatDedupPeriods = 2.0;

    /// <summary>Where the missed-vblank count of <see cref="RepeatsAndDrops"/> came from, most authoritative first.</summary>
    public enum MissedTickSource : byte
    {
        /// <summary>The render thread's per-present Turn rows (<see cref="SessionSeries.Turns"/>).</summary>
        TurnRows = 0,
        /// <summary>The missed-motion-tick counter as each UI frame read it (<see cref="SessionSeries.Pacing"/>).</summary>
        FrameCounter = 1,
        /// <summary>No counter recorded: pose gaps over 1.7× the median spacing, each worth its whole missed periods.</summary>
        PoseGaps = 2,
    }

    /// <summary>During motion, one event per vblank the glass did not advance on, each counted ONCE: (a) missed
    /// compositor ticks — the render thread's own counter (<see cref="SessionSeries.Turns"/>, else
    /// <see cref="SessionSeries.Pacing"/>, else late pose gaps), (b) display-side repeats from the attested ledger that no
    /// missed tick within ±<see cref="RepeatDedupPeriods"/>·T_r already explains, (c) presents superseded before display,
    /// and (d) DWM drops, each 1 Hz sample once (<see cref="PresentSample.DwmSeq"/>). A cumulative delta counts only when the interval
    /// it spans lies inside ONE motion window: the first row after an idle stretch holds the idle vblanks and is not a
    /// motion sample. (b) and (c) come from the paired-counter ledger only: an older recording's drop counter is the
    /// lagging-sample artefact and its repeat counter includes every idle and producer-skipped vblank (landing a flip or
    /// two late), so neither is a display attestation — such rows are reported as not attested, never counted.
    /// Value = events per 10 s of motion. Baseline 0; G = 0 events, A ≤ 2 per 10 s.</summary>
    public static MetricResult RepeatsAndDrops(SessionSeries s)
    {
        const string unit = "/10 s";
        double tr = s.RefreshPeriodS;
        var windows = MotionWindows(s, out var dts);
        if (windows.Count == 0) return MetricResult.Empty(RepeatsName, unit, "no motion");
        double motionS = 0.0;
        for (int i = 0; i < windows.Count; i++) motionS += windows[i].B - windows[i].A;
        double medDt = dts.Count > 0 ? Median(dts) : tr;

        // (a) missed vblanks, from the most authoritative source this recording has.
        var missed = MissedTickEvents(s, windows, medDt, out MissedTickSource source);
        var capacity = new long[missed.Count];
        long missedTotal = 0;
        double worstT = double.NaN;
        long worstCount = 0;
        for (int i = 0; i < missed.Count; i++)
        {
            capacity[i] = missed[i].N;
            missedTotal += missed[i].N;
            if (missed[i].N > worstCount) { worstCount = missed[i].N; worstT = missed[i].T; }
        }

        // (b)(c)(d) the attested ledger, per present row.
        long repeats = 0, dropped = 0, dwm = 0, unattestedRows = 0;
        var pr = s.Presents;
        double dedup = RepeatDedupPeriods * tr;
        for (int k = 1; k < pr.Length; k++)
        {
            if (!InMotion(windows, pr[k].T, tr)) continue;
            if (pr[k].DwmSeq != 0 && pr[k].DwmSeq != pr[k - 1].DwmSeq && pr[k].DwmDropped > 0)
            {
                dwm += pr[k].DwmDropped;
                if (pr[k].DwmDropped > worstCount) { worstCount = pr[k].DwmDropped; worstT = pr[k].T; }
            }
            if (!IntervalInMotion(windows, pr[k - 1].T, pr[k].T, tr)) continue;   // the delta spans idle: not a motion sample
            if (!pr[k].PairedLedger || !pr[k - 1].PairedLedger) { unattestedRows++; continue; }

            long rep = Math.Max(0L, pr[k].VblanksRepeated - pr[k - 1].VblanksRepeated);
            for (int i = 0; i < missed.Count && rep > 0; i++)
            {
                if (capacity[i] == 0 || Math.Abs(missed[i].T - pr[k].T) > dedup) continue;
                long take = Math.Min(rep, capacity[i]);
                capacity[i] -= take;
                rep -= take;
            }
            long drop = Math.Max(0L, pr[k].PresentsDropped - pr[k - 1].PresentsDropped);
            repeats += rep; dropped += drop;
            if (rep + drop > worstCount) { worstCount = rep + drop; worstT = pr[k].T; }
        }

        long total = missedTotal + repeats + dropped + dwm;
        double rate = motionS > 0.0 ? total / motionS * 10.0 : 0.0;
        var verdict = total == 0 ? MetricVerdict.Green : rate <= 2.0 ? MetricVerdict.Amber : MetricVerdict.Red;
        string from = source switch
        {
            MissedTickSource.TurnRows => "render-thread counter, per present",
            MissedTickSource.FrameCounter => "render-thread counter, per UI frame",
            _ => Fmt("pose gaps >1.7×{0:0.00} ms", medDt * 1000.0),
        };
        return new MetricResult(RepeatsName, rate, 0.0, verdict, unit, MarkersNear(s, worstT))
        {
            WorstT = worstT,
            Samples = (int)Math.Min(total, int.MaxValue),
            Detail = Fmt("{0} events in {1:0.00} s of motion: {2} missed vblanks ({3}), {4} display repeats, {5} superseded presents, {6} DWM-dropped{7}",
                total, motionS, missedTotal, from, repeats, dropped, dwm,
                unattestedRows > 0 ? " (ledger repeats/drops not attested: recorded before the paired-counter ledger)" : ""),
        };
    }

    /// <summary>The motion windows <see cref="RepeatsAndDrops"/> measures over: each moving frame's interval, merged
    /// across small gaps. A frame that STARTS motion from rest (the previous interval did not move — the poser was idle)
    /// contributes only its own refresh period, never the idle gap before it. <paramref name="dts"/> collects the spacing
    /// of consecutive moving poses.</summary>
    public static List<(double A, double B)> MotionWindows(SessionSeries s, out List<double> dts)
    {
        var p = s.Poses;
        double tr = s.RefreshPeriodS;
        var windows = new List<(double A, double B)>();
        dts = new List<double>();
        bool prevMoving = false;
        for (int k = 1; k < p.Length; k++)
        {
            bool moving = Math.Abs(p[k].Pos - p[k - 1].Pos) > MotionEpsilonDip;
            if (moving)
            {
                double b = p[k].T;
                double a = prevMoving ? p[k - 1].T : b - tr;
                if (prevMoving) dts.Add(b - p[k - 1].T);
                if (windows.Count > 0 && a - windows[^1].B <= 1.5 * tr) windows[^1] = (windows[^1].A, Math.Max(windows[^1].B, b));
                else windows.Add((a, b));
            }
            prevMoving = moving;
        }
        return windows;
    }

    private static bool InMotion(List<(double A, double B)> windows, double t, double tr)
    {
        for (int i = 0; i < windows.Count; i++) if (t >= windows[i].A - tr && t <= windows[i].B + tr) return true;
        return false;
    }

    /// <summary>Whether [<paramref name="a"/>, <paramref name="b"/>] lies inside ONE motion window (± T_r): a cumulative
    /// delta over it is a motion sample, never an idle stretch (windows merge only across gaps under 1.5·T_r).</summary>
    private static bool IntervalInMotion(List<(double A, double B)> windows, double a, double b, double tr)
    {
        for (int i = 0; i < windows.Count; i++) if (a >= windows[i].A - tr && b <= windows[i].B + tr) return true;
        return false;
    }

    /// <summary>Missed vblanks during motion as (time, count) events, from the most authoritative source recorded: the
    /// render thread's Turn rows (its counter per present — idle never charged), else the counter as UI frames read it
    /// (a delta counts only when the two frames lie in one motion window, so an idle stretch never lands on the first
    /// frame after it), else pose gaps (each gap between moving poses over 1.7× the median spacing is worth its whole missed
    /// periods).</summary>
    public static List<(double T, long N)> MissedTickEvents(SessionSeries s, List<(double A, double B)> windows, double medDt,
                                                            out MissedTickSource source)
    {
        double tr = s.RefreshPeriodS;
        var events = new List<(double T, long N)>();
        if (s.Turns.Length > 0)
        {
            source = MissedTickSource.TurnRows;
            foreach (var t in s.Turns)
                if (t.MissedTicks > 0 && InMotion(windows, t.T, tr)) events.Add((t.T, t.MissedTicks));
            return events;
        }
        if (s.Pacing.Length >= 2)
        {
            source = MissedTickSource.FrameCounter;
            var f = s.Pacing;
            for (int k = 1; k < f.Length; k++)
            {
                long d = f[k].MissedMotionTicks - f[k - 1].MissedMotionTicks;
                if (d > 0 && IntervalInMotion(windows, f[k - 1].T, f[k].T, tr)) events.Add((f[k].T, d));
            }
            return events;
        }
        source = MissedTickSource.PoseGaps;
        var p = s.Poses;
        bool prevMoving = false;
        for (int k = 1; k < p.Length; k++)
        {
            bool moving = Math.Abs(p[k].Pos - p[k - 1].Pos) > MotionEpsilonDip;
            double gap = p[k].T - p[k - 1].T;
            if (moving && prevMoving && gap > 1.7 * medDt)
                events.Add((p[k].T, Math.Max(1L, (long)Math.Round(gap / Math.Max(medDt, 1e-9)) - 1)));
            prevMoving = moving;
        }
        return events;
    }

    /// <summary>Why each missed compositor tick was missed (Info; needs the render thread's Turn rows). For a present
    /// that charged m ticks, the previous present's turn (its tick → present returned: wake lag + slot wait + work) is
    /// examined: if it held the render loop past the next m ticks (span ≥ (m + ¾)·T_r) its dominant part names the cause
    /// — <b>slot-wait-bound</b> (the present-queue slot, i.e. the compositor latched the previous present late),
    /// <b>UI-late</b> (the work of presenting a fresh UI publication — its record + submit — crossed the tick),
    /// <b>GPU-bound</b> (a motion re-present's submit / back-buffer fence / Present crossed the tick); a span that did not
    /// cross, or a dominant wake lag, is <b>unattributed</b> (the loop was free — the tick reached it late: scheduler or
    /// display clock). Value = missed ticks during motion.</summary>
    public static MetricResult MissedTickAttribution(SessionSeries s)
    {
        const string unit = "ticks";
        var turns = s.Turns;
        if (turns.Length == 0) return MetricResult.Empty(MissedTickAttributionName, unit, "no per-present render rows (Turn) in this recording");
        double trMs = s.RefreshPeriodS * 1000.0;
        var windows = MotionWindows(s, out _);
        long slot = 0, ui = 0, gpu = 0, unattributed = 0;
        var before = new List<double>();
        var all = new List<double>();
        double worst = -1.0, worstT = double.NaN;
        for (int k = 0; k < turns.Length; k++)
        {
            if (turns[k].Paced) all.Add(turns[k].SlotWaitMs);
            int m = turns[k].MissedTicks;
            if (m <= 0 || !InMotion(windows, turns[k].T, s.RefreshPeriodS)) continue;
            if (m > worst) { worst = m; worstT = turns[k].T; }
            if (k == 0) { unattributed += m; continue; }
            var prev = turns[k - 1];
            before.Add(prev.SlotWaitMs);
            bool held = prev.SpanMs >= (m + 0.75) * trMs;
            if (!held || (prev.WakeLagMs >= prev.SlotWaitMs && prev.WakeLagMs >= prev.WorkMs)) unattributed += m;
            else if (prev.SlotWaitMs >= prev.WorkMs) slot += m;
            else if (prev.Fresh) ui += m;
            else gpu += m;
        }
        long total = slot + ui + gpu + unattributed;
        return new MetricResult(MissedTickAttributionName, total, 0.0, MetricVerdict.Info, unit, MarkersNear(s, worstT))
        {
            WorstT = worstT,
            Samples = (int)Math.Min(total, int.MaxValue),
            Detail = Fmt("missed ticks: {0} slot-wait-bound / {1} UI-late / {2} GPU-bound / {3} unattributed; slot wait before a miss median {4:0.00} ms (all presents {5:0.00} ms, T_r {6:0.00} ms)",
                slot, ui, gpu, unattributed, before.Count > 0 ? Median(before) : double.NaN, all.Count > 0 ? Median(all) : double.NaN, trMs),
        };
    }

    // ── 5. per-notch curve shape (+ t50/t90) ─────────────────────────────────────────────────────────────────────

    /// <summary>The curve origin of the notch stamped at <paramref name="stamp"/>: the anchor of the first wheel plan
    /// authored for it (a wheel <see cref="PlanSample"/> in <c>[stamp, stamp + PlanAnchorSlackS]</c>), else the stamp.</summary>
    public static double CurveAnchor(SessionSeries s, double stamp)
    {
        var plans = s.Plans;
        int lo = 0, hi = plans.Length;
        while (lo < hi)
        {
            int mid = (lo + hi) >> 1;
            if (plans[mid].T < stamp - 1e-6) lo = mid + 1; else hi = mid;
        }
        for (int k = lo; k < plans.Length && plans[k].T <= stamp + PlanAnchorSlackS; k++)
            if (plans[k].IsWheel) return plans[k].T;
        return stamp;
    }

    /// <summary>The reference wheel curve: the fraction of one isolated notch's travel covered at normalised time
    /// <paramref name="u"/> = t / WheelDurationS — the <c>SegKind.Cubic</c> <see cref="PlanAuthor.WheelSeg"/> authors
    /// from rest under <see cref="Reference"/> (the ease <c>1.5u − 0.5u³</c> with its <c>WheelRiseS</c> blend-in from
    /// zero velocity), clamped to [0, 1].</summary>
    public static double ReferenceCurve(double u)
    {
        if (u <= 0.0) return 0.0;
        if (u >= 1.0) return 1.0;
        double rise = Math.Min(Math.Max(Reference.WheelRiseS, 0.0), Reference.WheelDurationS) / Reference.WheelDurationS;
        return new MotionSeg(SegKind.Cubic, 0.0, 1.0, 0.0, 1.0, 0.0, rise).Eval(u, out _);
    }

    /// <summary>Seconds after a notch at which the reference curve reaches <paramref name="fraction"/> of its travel.</summary>
    public static double ReferenceTimeToFraction(double fraction)
    {
        fraction = Math.Clamp(fraction, 0.0, 1.0);
        double lo = 0.0, hi = 1.0;
        for (int i = 0; i < 60; i++)
        {
            double mid = 0.5 * (lo + hi);
            if (ReferenceCurve(mid) < fraction) lo = mid; else hi = mid;
        }
        return 0.5 * (lo + hi) * Reference.WheelDurationS;
    }

    /// <summary>RMS of the NORMALISED per-notch displacement curve vs the reference curve over 0..<see cref="CurveWindowS"/>
    /// after each isolated notch's plan anchor (<see cref="CurveAnchor"/>) (both normalised to their own travel at the window end — shape only; the distance is
    /// <see cref="DipPerNotch"/>'s). Value = mean over notches (%). G &lt; 5 %, A &lt; 10 %.</summary>
    public static MetricResult NotchCurveShape(SessionSeries s)
    {
        const string unit = "%";
        var curves = IsolatedNotchCurves(s);
        if (curves.Count == 0) return MetricResult.Empty(CurveShapeName, unit, "no isolated notches (gap > 300 ms)");
        double sum = 0.0, worst = -1.0, worstT = double.NaN;
        for (int i = 0; i < curves.Count; i++)
        {
            sum += curves[i].RmsPct;
            if (curves[i].RmsPct > worst) { worst = curves[i].RmsPct; worstT = curves[i].T; }
        }
        double value = sum / curves.Count;
        var verdict = value < 5.0 ? MetricVerdict.Green : value < 10.0 ? MetricVerdict.Amber : MetricVerdict.Red;
        return new MetricResult(CurveShapeName, value, 0.0, verdict, unit, MarkersNear(s, worstT))
        {
            WorstT = worstT,
            Samples = curves.Count,
            Detail = Fmt("mean RMS {0:0.0}% vs the reference curve over {1} isolated notches, worst {2:0.0}%", value, curves.Count, worst),
        };
    }

    /// <summary>Median time (ms) from an isolated notch's plan anchor to 50 % of its travel. Baseline = the reference curve's. Info.</summary>
    public static MetricResult NotchT50(SessionSeries s) => NotchTFraction(s, 0.5, T50Name);

    /// <summary>Median time (ms) from an isolated notch's plan anchor to 90 % of its travel. Baseline = the reference curve's. Info.</summary>
    public static MetricResult NotchT90(SessionSeries s) => NotchTFraction(s, 0.9, T90Name);

    private static MetricResult NotchTFraction(SessionSeries s, double fraction, string name)
    {
        const string unit = "ms";
        var curves = IsolatedNotchCurves(s);
        var values = new List<double>();
        double worstDev = -1.0, worstT = double.NaN;
        double baseline = ReferenceTimeToFraction(fraction) * 1000.0;
        for (int i = 0; i < curves.Count; i++)
        {
            double t = fraction <= 0.5 ? curves[i].T50 : curves[i].T90;
            if (double.IsNaN(t)) continue;
            values.Add(t * 1000.0);
            double dev = Math.Abs(t * 1000.0 - baseline);
            if (dev > worstDev) { worstDev = dev; worstT = curves[i].T; }
        }
        if (values.Count == 0) return MetricResult.Empty(name, unit, "no isolated notches (gap > 300 ms)");
        double value = Median(values);
        return new MetricResult(name, value, baseline, MetricVerdict.Info, unit, MarkersNear(s, worstT))
        {
            WorstT = worstT,
            Samples = values.Count,
            Detail = Fmt("median {0:0.0} ms over {1} notches; reference curve {2:0.0} ms", value, values.Count, baseline),
        };
    }

    // ── 6. DIP per notch + sustained spin speed ──────────────────────────────────────────────────────────────────

    /// <summary>Per isolated notch: |p(a + window) − p(a)| / |notches| from its plan anchor <c>a</c>. Value = median (DIP). Baseline = the reference
    /// feel's WheelNotchDip; G within ±5 %, A within ±15 %.</summary>
    public static MetricResult DipPerNotch(SessionSeries s)
    {
        const string unit = "DIP";
        var curves = IsolatedNotchCurves(s);
        var values = new List<double>();
        double baseline = Reference.WheelNotchDip;
        double worstDev = -1.0, worstT = double.NaN;
        for (int i = 0; i < curves.Count; i++)
        {
            if (curves[i].Notches == 0.0) continue;
            double v = Math.Abs(curves[i].Travel) / Math.Abs(curves[i].Notches);
            values.Add(v);
            if (Math.Abs(v - baseline) > worstDev) { worstDev = Math.Abs(v - baseline); worstT = curves[i].T; }
        }
        if (values.Count == 0) return MetricResult.Empty(DipPerNotchName, unit, "no isolated notches (gap > 300 ms)");
        double value = Median(values);
        return new MetricResult(DipPerNotchName, value, baseline, RelativeVerdict(value, baseline, 0.05, 0.15), unit, MarkersNear(s, worstT))
        {
            WorstT = worstT,
            Samples = values.Count,
            Detail = Fmt("median {0:0.0} DIP over {1} isolated notches (reference {2:0} DIP)", value, values.Count, baseline),
        };
    }

    /// <summary>Median |v| (DIP/s, pose finite differences) while consecutive same-direction notches arrive less than
    /// <see cref="SpinGapS"/> apart. No phase-1 baseline (the WinUI spin scenario is phase 2) — Info.</summary>
    public static MetricResult SustainedSpinSpeed(SessionSeries s)
    {
        const string unit = "DIP/s";
        var values = new List<double>();
        int prev = -1;
        for (int i = 0; i < s.Inputs.Length; i++)
        {
            if (!s.Inputs[i].IsNotch) continue;
            if (prev >= 0)
            {
                var a = s.Inputs[prev];
                var b = s.Inputs[i];
                if (b.T - a.T < SpinGapS && Math.Sign(a.Amount) == Math.Sign(b.Amount))
                {
                    for (int k = Math.Max(1, s.FirstPoseAtOrAfter(a.T)); k < s.Poses.Length && s.Poses[k].T <= b.T; k++)
                    {
                        double dt = s.Poses[k].T - s.Poses[k - 1].T;
                        if (dt > 0.0 && s.Poses[k - 1].T >= a.T) values.Add(Math.Abs(s.Poses[k].Pos - s.Poses[k - 1].Pos) / dt);
                    }
                }
            }
            prev = i;
        }
        if (values.Count == 0) return MetricResult.Empty(SpinSpeedName, unit, "no spin (notch gaps < 120 ms)");
        double value = Median(values);
        return new MetricResult(SpinSpeedName, value, double.NaN, MetricVerdict.Info, unit, Array.Empty<MarkerSample>())
        {
            Samples = values.Count,
            Detail = Fmt("median {0:0} DIP/s over {1} spin frames", value, values.Count),
        };
    }

    // ── 7. stop metrics ──────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Per gesture: <c>t_stop − t_lift</c>, where <c>t_lift</c> is the gesture's last input and <c>t_stop</c> the
    /// first pose after it that begins two consecutive frames each moving less than <see cref="StopDipPerFrame"/> DIP per
    /// refresh period (a pose-stream gap over 3 frames also ends the motion). Baseline = the same criterion applied to
    /// the reference model driven by the gesture's own notches (wheel gestures); G within 15 %, A within 35 %; Info
    /// when no gesture has a model.</summary>
    public static MetricResult StopLatency(SessionSeries s)
    {
        const string unit = "ms";
        var stops = Stops(s);
        if (stops.Count == 0) return MetricResult.Empty(StopLatencyName, unit, "no gesture came to rest");
        var values = new List<double>();
        var models = new List<double>();
        double worstDev = -1.0, worstT = double.NaN;
        for (int i = 0; i < stops.Count; i++)
        {
            double v = (stops[i].TStop - stops[i].TLift) * 1000.0;
            values.Add(v);
            if (!double.IsNaN(stops[i].ModelStopS))
            {
                double m = stops[i].ModelStopS * 1000.0;
                models.Add(m);
                if (Math.Abs(v - m) > worstDev) { worstDev = Math.Abs(v - m); worstT = stops[i].TLift; }
            }
            else if (double.IsNaN(worstT)) worstT = stops[i].TLift;
        }
        double value = Median(values);
        double baseline = models.Count > 0 ? Median(models) : double.NaN;
        var verdict = double.IsNaN(baseline) ? MetricVerdict.Info : RelativeVerdict(value, baseline, 0.15, 0.35);
        return new MetricResult(StopLatencyName, value, baseline, verdict, unit, MarkersNear(s, worstT))
        {
            WorstT = worstT,
            Samples = values.Count,
            Detail = Fmt("median {0:0} ms lift→rest over {1} gestures; reference model {2:0} ms", value, values.Count, baseline),
        };
    }

    /// <summary>Per gesture: |p(t_stop) − p(t_lift)| (DIP) — how far the content travelled after the last input.
    /// Baseline = the reference model's; G within 15 %, A within 35 %.</summary>
    public static MetricResult StopDistance(SessionSeries s)
    {
        const string unit = "DIP";
        var stops = Stops(s);
        if (stops.Count == 0) return MetricResult.Empty(StopDistanceName, unit, "no gesture came to rest");
        var values = new List<double>();
        var models = new List<double>();
        double worstDev = -1.0, worstT = double.NaN;
        for (int i = 0; i < stops.Count; i++)
        {
            values.Add(stops[i].Distance);
            if (!double.IsNaN(stops[i].ModelDistance))
            {
                models.Add(stops[i].ModelDistance);
                double dev = Math.Abs(stops[i].Distance - stops[i].ModelDistance);
                if (dev > worstDev) { worstDev = dev; worstT = stops[i].TLift; }
            }
        }
        double value = Median(values);
        double baseline = models.Count > 0 ? Median(models) : double.NaN;
        var verdict = double.IsNaN(baseline) ? MetricVerdict.Info
            : baseline < 2.0 ? (Math.Abs(value - baseline) <= 1.0 ? MetricVerdict.Green : Math.Abs(value - baseline) <= 3.0 ? MetricVerdict.Amber : MetricVerdict.Red)
            : RelativeVerdict(value, baseline, 0.15, 0.35);
        return new MetricResult(StopDistanceName, value, baseline, verdict, unit, MarkersNear(s, worstT))
        {
            WorstT = worstT,
            Samples = values.Count,
            Detail = Fmt("median {0:0.0} DIP after the last input over {1} gestures; reference model {2:0.0} DIP", value, values.Count, baseline),
        };
    }

    /// <summary>Per gesture: the exponential decay rate k (1/s) from a least-squares fit of ln|v| over the post-lift
    /// frames still moving at least <see cref="StopDipPerFrame"/>. Baseline = the reference fling decay for contact
    /// gestures (G within 15 %, A within 35 %); a wheel gesture's cubic is not exponential, so wheel-only sessions
    /// report Info.</summary>
    public static MetricResult DecayRate(SessionSeries s)
    {
        const string unit = "1/s";
        var stops = Stops(s);
        var values = new List<double>();
        bool anyContact = false;
        for (int i = 0; i < stops.Count; i++)
        {
            if (double.IsNaN(stops[i].DecayK)) continue;
            values.Add(stops[i].DecayK);
            if (!stops[i].Wheel) anyContact = true;
        }
        if (values.Count == 0) return MetricResult.Empty(DecayName, unit, "no post-lift coast long enough to fit");
        double value = Median(values);
        double baseline = anyContact ? Reference.FlingDecayPerS : double.NaN;
        var verdict = anyContact ? RelativeVerdict(value, baseline, 0.15, 0.35) : MetricVerdict.Info;
        return new MetricResult(DecayName, value, baseline, verdict, unit, Array.Empty<MarkerSample>())
        {
            Samples = values.Count,
            Detail = Fmt("median k {0:0.00}/s over {1} coasts{2}", value, values.Count, anyContact ? "" : " (wheel cubic — informational)"),
        };
    }

    // ── shared building blocks (public: the tests and the lab's charts use them) ─────────────────────────────────

    /// <summary>Gestures as [first, last] index ranges into <see cref="SessionSeries.Inputs"/>: consecutive inputs no
    /// more than <see cref="GestureGapS"/> apart, a contact End closing its gesture.</summary>
    public static IReadOnlyList<(int First, int Last)> Gestures(SessionSeries s)
    {
        var result = new List<(int, int)>();
        var inputs = s.Inputs;
        int first = -1;
        for (int i = 0; i < inputs.Length; i++)
        {
            if (first < 0) { first = i; continue; }
            bool split = inputs[i].T - inputs[i - 1].T > GestureGapS || inputs[i - 1].IsEnd;
            if (split)
            {
                result.Add((first, i - 1));
                first = i;
            }
        }
        if (first >= 0) result.Add((first, inputs.Length - 1));
        return result;
    }

    /// <summary>One isolated notch's measured curve summary: <see cref="T"/> is the notch's stamp, <see cref="Anchor"/>
    /// the plan anchor its curve was measured from (<see cref="CurveAnchor"/>); <see cref="T50"/>/<see cref="T90"/> are
    /// relative to the anchor.</summary>
    public readonly record struct NotchCurve(double T, double Notches, double Travel, double RmsPct, double T50, double T90, double Anchor);

    /// <summary>Every isolated notch (no input within <see cref="IsolatedGapS"/> before it or <see cref="CurveWindowS"/>
    /// after, both on the stamps) with at least 3 poses in its window and a travel of at least 1 DIP; the curve itself is
    /// measured from the notch's plan anchor (<see cref="CurveAnchor"/>).</summary>
    public static IReadOnlyList<NotchCurve> IsolatedNotchCurves(SessionSeries s)
    {
        var result = new List<NotchCurve>();
        var inputs = s.Inputs;
        double dur = Reference.WheelDurationS;
        for (int i = 0; i < inputs.Length; i++)
        {
            if (!inputs[i].IsNotch || inputs[i].Amount == 0f) continue;
            double n = inputs[i].T;
            if (i > 0 && n - inputs[i - 1].T <= IsolatedGapS) continue;
            if (i + 1 < inputs.Length && inputs[i + 1].T - n <= CurveWindowS) continue;
            double a = CurveAnchor(s, n);
            int shownAtAnchor = s.LastPoseAtOrBefore(a);   // the position on screen when the curve was anchored
            double p0 = shownAtAnchor >= 0 ? s.Poses[shownAtAnchor].Pos : s.PosAt(a);
            double travel = s.PosAt(a + CurveWindowS) - p0;
            if (double.IsNaN(travel) || Math.Abs(travel) < 1.0) continue;

            double errSq = 0.0;
            int count = 0;
            double t50 = double.NaN, t90 = double.NaN;
            double prevT = 0.0, prevY = 0.0;
            for (int k = s.FirstPoseAtOrAfter(a); k < s.Poses.Length && s.Poses[k].T <= a + CurveWindowS; k++)
            {
                double t = s.Poses[k].T - a;
                if (t <= 0.0) continue;
                double y = (s.Poses[k].Pos - p0) / travel;
                double r = ReferenceCurve(t / dur);
                errSq += (y - r) * (y - r);
                count++;
                if (double.IsNaN(t50) && y >= 0.5) t50 = Cross(prevT, prevY, t, y, 0.5);
                if (double.IsNaN(t90) && y >= 0.9) t90 = Cross(prevT, prevY, t, y, 0.9);
                prevT = t;
                prevY = y;
            }
            if (count < 3) continue;
            result.Add(new NotchCurve(n, inputs[i].Amount, travel, Math.Sqrt(errSq / count) * 100.0, t50, t90, a));
        }
        return result;
    }

    /// <summary>One gesture's stop summary: lift/stop times, travel after lift, the fitted decay k, and the reference
    /// model's stop time/distance (NaN when the gesture is not a pure wheel gesture).</summary>
    public readonly record struct GestureStop(double TLift, double TStop, double Distance, double DecayK, bool Wheel,
                                              double ModelStopS, double ModelDistance);

    /// <summary>Every gesture whose content came to rest before the next gesture began (see <see cref="StopLatency"/>).</summary>
    public static IReadOnlyList<GestureStop> Stops(SessionSeries s)
    {
        var result = new List<GestureStop>();
        var p = s.Poses;
        if (p.Length < 2) return result;
        double tr = s.RefreshPeriodS;
        var gestures = Gestures(s);
        for (int g = 0; g < gestures.Count; g++)
        {
            var (first, last) = gestures[g];
            double tLift = s.Inputs[last].T;
            double limit = g + 1 < gestures.Count ? s.Inputs[gestures[g + 1].First].T : double.PositiveInfinity;
            limit = Math.Min(limit, tLift + 3.0);

            // Did anything move after the lift? (A gesture at an edge that moved nothing has no stop to measure.)
            double tStop = double.NaN;
            int k0 = Math.Max(1, s.FirstPoseAtOrAfter(tLift));
            for (int k = k0; k < p.Length && p[k].T <= limit; k++)
            {
                if (p[k - 1].T < tLift) continue;                    // only intervals wholly after the lift
                double dt = p[k].T - p[k - 1].T;
                if (dt > 3.0 * tr) { tStop = p[k - 1].T; break; }   // the poser went idle: motion had ended
                if (FramePace(p[k - 1], p[k], tr) >= StopDipPerFrame) continue;
                bool nextSlow = k + 1 >= p.Length || p[k + 1].T > limit || p[k + 1].T - p[k].T > 3.0 * tr
                                || FramePace(p[k], p[k + 1], tr) < StopDipPerFrame;
                if (nextSlow) { tStop = p[k].T; break; }
            }
            if (double.IsNaN(tStop) || tStop < tLift) continue;
            double pLift = s.PosAt(tLift);
            double distance = Math.Abs(s.PosAt(tStop) - pLift);
            double firstMove = s.PosAt(s.Inputs[first].T);
            if (Math.Abs(s.PosAt(tStop) - firstMove) < MotionEpsilonDip) continue;   // never moved

            double decayK = FitDecay(s, tLift, tStop, tr);

            bool wheel = true;
            for (int i = first; i <= last; i++) if (!s.Inputs[i].IsNotch) { wheel = false; break; }
            double modelStop = double.NaN, modelDist = double.NaN;
            if (wheel) ModelWheelStop(s, first, last, tr, out modelStop, out modelDist);
            result.Add(new GestureStop(tLift, tStop, distance, decayK, wheel, modelStop, modelDist));
        }
        return result;
    }

    /// <summary>The reference model driven by gesture [<paramref name="first"/>, <paramref name="last"/>]'s own notches
    /// (<see cref="PlanAuthor.WheelNotch"/> with <see cref="Reference"/>, no edges), sampled every refresh period after
    /// the lift with the SAME stop criterion as the measurement.</summary>
    public static void ModelWheelStop(SessionSeries s, int first, int last, double refreshS, out double stopS, out double distance)
    {
        var inputs = s.Inputs;
        double p0 = s.PosAt(inputs[first].T);
        if (double.IsNaN(p0)) p0 = 0.0;
        MotionFeel feel = Reference;
        var plan = ScrollPlan.Idle(0, p0, -1e12, 1e12);
        var accel = default(WheelAccelState);
        for (int i = first; i <= last; i++)
            plan = PlanAuthor.WheelNotch(in plan, inputs[i].T, inputs[i].Amount, in feel, ref accel);
        double tLift = inputs[last].T;
        double pLift = plan.Eval(tLift, out _, out _);
        stopS = double.NaN;
        distance = double.NaN;
        double prev = pLift;
        double paceA = double.PositiveInfinity;
        for (int j = 1; j <= 600; j++)
        {
            double t = tLift + j * refreshS;
            double pos = plan.Eval(t, out _, out _);
            double paceB = Math.Abs(pos - prev);
            if (paceA < StopDipPerFrame && paceB < StopDipPerFrame)
            {
                double tStop = tLift + (j - 1) * refreshS;
                stopS = tStop - tLift;
                distance = Math.Abs(plan.Eval(tStop, out _, out _) - pLift);
                return;
            }
            paceA = paceB;
            prev = pos;
        }
    }

    public static double Median(List<double> xs)
    {
        if (xs.Count == 0) return double.NaN;
        var copy = xs.ToArray();
        Array.Sort(copy);
        int n = copy.Length;
        return (n & 1) == 1 ? copy[n / 2] : 0.5 * (copy[n / 2 - 1] + copy[n / 2]);
    }

    /// <summary>Markers (excluding session fences) within ±<see cref="MarkerWindowS"/> of <paramref name="t"/>.</summary>
    public static MarkerSample[] MarkersNear(SessionSeries s, double t)
    {
        if (double.IsNaN(t) || s.Markers.Length == 0) return Array.Empty<MarkerSample>();
        List<MarkerSample>? hits = null;
        for (int i = 0; i < s.Markers.Length; i++)
        {
            var m = s.Markers[i];
            if (m.Code is ProbeMark.SessionStart or ProbeMark.SessionEnd or ProbeMark.BurstBoundary or ProbeMark.None) continue;
            if (Math.Abs(m.T - t) <= MarkerWindowS) (hits ??= new List<MarkerSample>()).Add(m);
        }
        return hits is null ? Array.Empty<MarkerSample>() : hits.ToArray();
    }

    // ── internals ────────────────────────────────────────────────────────────────────────────────────────────────

    private static MetricVerdict RelativeVerdict(double value, double baseline, double green, double amber)
    {
        if (double.IsNaN(value) || double.IsNaN(baseline)) return MetricVerdict.Info;
        double denom = Math.Max(Math.Abs(baseline), 1e-9);
        double rel = Math.Abs(value - baseline) / denom;
        return rel <= green ? MetricVerdict.Green : rel <= amber ? MetricVerdict.Amber : MetricVerdict.Red;
    }

    /// <summary>DIP moved per refresh period between two poses.</summary>
    private static double FramePace(in PoseSample a, in PoseSample b, double refreshS)
    {
        double dt = b.T - a.T;
        if (!(dt > 0.0)) return 0.0;
        return Math.Abs(b.Pos - a.Pos) / dt * refreshS;
    }

    private static double Cross(double t0, double y0, double t1, double y1, double level)
    {
        if (!(y1 > y0)) return t1;
        double f = (level - y0) / (y1 - y0);
        return t0 + (t1 - t0) * Math.Clamp(f, 0.0, 1.0);
    }

    private static double FitDecay(SessionSeries s, double tLift, double tStop, double refreshS)
    {
        var p = s.Poses;
        double sx = 0, sy = 0, sxx = 0, sxy = 0;
        int n = 0;
        for (int k = Math.Max(1, s.FirstPoseAtOrAfter(tLift)); k < p.Length && p[k].T <= tStop; k++)
        {
            double dt = p[k].T - p[k - 1].T;
            if (!(dt > 0.0) || p[k - 1].T < tLift) continue;
            double v = Math.Abs(p[k].Pos - p[k - 1].Pos) / dt;
            if (v * refreshS < StopDipPerFrame || v <= 0.0) continue;
            double x = 0.5 * (p[k].T + p[k - 1].T) - tLift;
            double y = Math.Log(v);
            sx += x; sy += y; sxx += x * x; sxy += x * y;
            n++;
        }
        if (n < 4) return double.NaN;
        double den = n * sxx - sx * sx;
        if (Math.Abs(den) < 1e-12) return double.NaN;
        double slope = (n * sxy - sx * sy) / den;
        return -slope;
    }

    private static string Fmt(string format, params object[] args) => string.Format(CultureInfo.InvariantCulture, format, args);
}
