using System;
using System.Collections.Generic;
using FluentGpu.Scroll.Diag;
using FluentGpu.Scroll.Diag.Analysis;
using FluentGpu.Scroll.Motion;
using Xunit;

namespace FluentGpu.Engine.Tests;

/// <summary>Scroll-lab E4: the metrics catalog (rows 1–7) over SYNTHETIC session series — each test builds the pose/
/// input/present stream a known scroll would produce and checks the value and the verdict the plan's thresholds give.
/// Pure: no host, no window, no rings.</summary>
public sealed class ScrollMetricsTests
{
    const double Hz = 60.0;
    const double Tr = 1.0 / Hz;
    const double NotchT = 1.0;   // frame 60 on the 60 Hz grid — exact in binary

    /// <summary>Poses on the exact 60 Hz grid (T = frame / 60) from <paramref name="fromFrame"/> to <paramref name="toFrame"/>.</summary>
    static PoseSample[] Grid(int fromFrame, int toFrame, Func<double, double> pos)
    {
        var list = new List<PoseSample>();
        for (int j = fromFrame; j <= toFrame; j++)
        {
            double t = j / Hz;
            list.Add(new PoseSample(t, pos(t)));
        }
        return list.ToArray();
    }

    static readonly double RefDip = ScrollMetrics.Reference.WheelNotchDip;
    static readonly double RefDur = ScrollMetrics.Reference.WheelDurationS;

    /// <summary>One notch's reference curve of <paramref name="dip"/> (default: the reference notch) over
    /// <paramref name="durS"/> (default: the reference duration), starting <paramref name="delayS"/> after the notch.</summary>
    static Func<double, double> Cubic(double n, double dip = double.NaN, double durS = double.NaN, double delayS = 0.0)
    {
        if (double.IsNaN(dip)) dip = RefDip;
        if (double.IsNaN(durS)) durS = RefDur;
        return t => t < n + delayS ? 0.0 : dip * ScrollMetrics.ReferenceCurve((t - n - delayS) / durS);
    }

    static InputSample Notch(double t, float notches = 1f) => new(t, ScrollSourceCode.MouseWheel, ScrollProbe.PhaseNotch, 0f, notches);

    static SessionSeries SingleNotch(Func<double, double> pos, MarkerSample[]? markers = null)
        => new(new[] { Notch(NotchT) }, Grid(30, 150, pos), markers: markers, refreshPeriodS: Tr);

    [Fact]
    public void ReferenceCurveIsTheDefaultFeelsIsolatedNotch()
    {
        Assert.Equal(0.0, ScrollMetrics.ReferenceCurve(0.0));
        Assert.Equal(1.0, ScrollMetrics.ReferenceCurve(1.0));
        Assert.Equal(1.0, ScrollMetrics.ReferenceCurve(3.0));
        // The curve the default feel authors for one notch from rest, normalised.
        var accel = new WheelAccelState();
        var plan = PlanAuthor.WheelNotch(ScrollPlan.Idle(0, 0.0, 0.0, 1e6), 0.0, 1.0, ScrollMetrics.Reference, ref accel);
        for (double u = 0.0; u <= 1.0; u += 0.01)
            Assert.Equal(plan.Eval(u * RefDur, out _, out _) / RefDip, ScrollMetrics.ReferenceCurve(u), 9);
        // 1.5u − 0.5u³ = 0.5 at u ≈ 0.3473 (the blend-in is over by then) → t50 ≈ 52.1 ms of the 150 ms curve.
        Assert.InRange(ScrollMetrics.ReferenceTimeToFraction(0.5) * 1000.0, 51.6, 52.6);
        Assert.True(ScrollMetrics.ReferenceTimeToFraction(0.9) > ScrollMetrics.ReferenceTimeToFraction(0.5));
    }

    [Fact]
    public void AReferenceNotchScoresGreenOnShapeDistanceAndStop()
    {
        var s = SingleNotch(Cubic(NotchT));

        var dip = ScrollMetrics.DipPerNotch(s);
        Assert.Equal(MetricVerdict.Green, dip.Verdict);
        Assert.InRange(dip.Value, RefDip - 0.1, RefDip + 0.1);
        Assert.Equal(RefDip, dip.Baseline);

        var shape = ScrollMetrics.NotchCurveShape(s);
        Assert.Equal(MetricVerdict.Green, shape.Verdict);
        Assert.True(shape.Value < 1.0, $"rms {shape.Value}%");

        var t50 = ScrollMetrics.NotchT50(s);
        Assert.Equal(MetricVerdict.Info, t50.Verdict);
        Assert.InRange(t50.Value, t50.Baseline - 8.0, t50.Baseline + 8.0);

        var stop = ScrollMetrics.StopLatency(s);
        Assert.Equal(MetricVerdict.Green, stop.Verdict);
        Assert.Equal(stop.Baseline, stop.Value, 6);

        var first = ScrollMetrics.FirstMotionLatency(s);
        Assert.Equal(MetricVerdict.Green, first.Verdict);
        Assert.InRange(first.Value, 16.0, 17.5);   // the first grid pose after the notch
        Assert.Equal(2.0 * Tr * 1000.0, first.Baseline, 6);
    }

    [Fact]
    public void TooFarPerNotchIsRed()
    {
        var s = SingleNotch(Cubic(NotchT, dip: 1.25 * RefDip));
        var dip = ScrollMetrics.DipPerNotch(s);
        Assert.InRange(dip.Value, 1.25 * RefDip - 0.1, 1.25 * RefDip + 0.1);
        Assert.Equal(MetricVerdict.Red, dip.Verdict);
        // The SHAPE is still the reference cubic (both normalise to their own travel).
        Assert.Equal(MetricVerdict.Green, ScrollMetrics.NotchCurveShape(s).Verdict);
    }

    [Fact]
    public void AFloatyCurveFailsShapeAndStop()
    {
        var s = SingleNotch(Cubic(NotchT, dip: 1.5 * RefDip, durS: 0.6));   // longer AND further: ~0.6 s to rest vs the model's ~0.15 s
        Assert.Equal(MetricVerdict.Red, ScrollMetrics.NotchCurveShape(s).Verdict);
        var stop = ScrollMetrics.StopLatency(s);
        Assert.True(stop.Value > stop.Baseline * 1.5, $"stop {stop.Value} vs model {stop.Baseline}");
        Assert.Equal(MetricVerdict.Red, stop.Verdict);
    }

    [Fact]
    public void DelayedFirstMotionIsRedAndCarriesTheNearbyMarker()
    {
        var markers = new[]
        {
            new MarkerSample(NotchT + 0.3, ProbeMark.UserFelt),   // within ±500 ms of the worst sample
            new MarkerSample(NotchT + 1.2, ProbeMark.UserFelt),   // too far
            new MarkerSample(NotchT + 0.1, ProbeMark.SessionStart), // a fence, never a hit
        };
        var s = SingleNotch(Cubic(NotchT, delayS: 0.060), markers);
        var first = ScrollMetrics.FirstMotionLatency(s);
        Assert.InRange(first.Value, 66.0, 67.5);   // the first grid pose past the 60 ms dead time (frame 64)
        Assert.Equal(MetricVerdict.Red, first.Verdict);
        Assert.Equal(NotchT, first.WorstT);
        Assert.Single(first.MarkerHits);
        Assert.Equal(ProbeMark.UserFelt, first.MarkerHits[0].Code);
    }

    [Fact]
    public void NotchToPoseLagCountsTheFirstPresentAfterTheNotch()
    {
        // Idle poses up to just before the notch, then the poser only wakes 45 ms later.
        var poses = new List<PoseSample>(Grid(30, 59, _ => 0.0));
        for (int j = 0; j <= 40; j++)
        {
            double t = NotchT + 0.045 + j / Hz;
            poses.Add(new PoseSample(t, 3.0 * (j + 1)));
        }
        var s = new SessionSeries(new[] { Notch(NotchT) }, poses.ToArray(), refreshPeriodS: Tr);
        var lag = ScrollMetrics.NotchToPoseLag(s);
        Assert.InRange(lag.Value, 44.9, 45.1);
        Assert.Equal(MetricVerdict.Amber, lag.Verdict);   // 2·T_r < 45 ms ≤ 3·T_r

        var prompt = new SessionSeries(new[] { Notch(NotchT + 0.005) }, Grid(0, 120, t => t * 100.0), refreshPeriodS: Tr);
        Assert.Equal(MetricVerdict.Green, ScrollMetrics.NotchToPoseLag(prompt).Verdict);
    }

    [Fact]
    public void IrregularityIsZeroForConstantVelocityAndRedForAlternatingSteps()
    {
        var smooth = new SessionSeries(Array.Empty<InputSample>(), Grid(0, 120, t => t * Hz * 5.0), refreshPeriodS: Tr);
        var r = ScrollMetrics.DisplacementIrregularity(smooth);
        Assert.Equal(MetricVerdict.Green, r.Verdict);
        Assert.True(r.Value < 1e-6);

        var poses = new List<PoseSample>();
        double pos = 0.0;
        for (int j = 0; j <= 120; j++)
        {
            poses.Add(new PoseSample(j / Hz, pos));
            pos += (j & 1) == 0 ? 4.0 : 6.0;   // Δp 4,6,4,6… → σ(Δ²p) = 2, mean|Δp| = 5
        }
        var jittery = ScrollMetrics.DisplacementIrregularity(new SessionSeries(Array.Empty<InputSample>(), poses.ToArray(), refreshPeriodS: Tr));
        Assert.InRange(jittery.Value, 0.39, 0.41);
        Assert.Equal(MetricVerdict.Red, jittery.Verdict);
    }

    /// <summary>At present-queue depth 1 a repeated vblank is a missed pose: the pose stream has a 2·T_r gap at frame 200
    /// and the ledger's repeat for it lands on the next present row. One missed vblank → ONE event (1.67 per 10 s).</summary>
    [Fact]
    public void AttestedRepeatsDuringMotionAreCountedPerTenSeconds()
    {
        PoseSample[] Poses(int missingFrame)
        {
            var list = new List<PoseSample>();
            for (int j = 0; j <= 360; j++) if (j != missingFrame) list.Add(new PoseSample(j / Hz, j * 5.0));   // 6 s of motion
            return list.ToArray();
        }
        PresentSample[] Presents(int missingFrame)
        {
            var list = new List<PresentSample>();
            for (int j = 0; j <= 360; j++)
                if (j != missingFrame)
                    list.Add(new PresentSample(j / Hz, j, 0, missingFrame >= 0 && j > missingFrame ? 1 : 0, RefreshIntervalMs: 1000.0 / Hz));
            return list.ToArray();
        }

        var clean = new SessionSeries(Array.Empty<InputSample>(), Poses(-1), Presents(-1));
        var c = ScrollMetrics.RepeatsAndDrops(clean);
        Assert.Equal(MetricVerdict.Green, c.Verdict);
        Assert.Equal(0.0, c.Value);

        var one = ScrollMetrics.RepeatsAndDrops(new SessionSeries(Array.Empty<InputSample>(), Poses(200), Presents(200)));
        Assert.Equal(1, one.Samples);
        Assert.InRange(one.Value, 1.6, 1.75);   // 1 event in 6 s → 1.67 per 10 s
        Assert.Equal(MetricVerdict.Amber, one.Verdict);
        Assert.Equal(201 / Hz, one.WorstT, 9);
    }

    /// <summary>With the render thread's own counter recorded (the frame log's per-UI-frame reading), it IS the
    /// missed-vblank count: the pose gap is not counted again, and the ledger's repeat for the same tick is absorbed.</summary>
    [Fact]
    public void TheRenderThreadCounterIsTheMissedVblankCount()
    {
        var poses = new List<PoseSample>();
        var presents = new List<PresentSample>();
        var pacing = new List<PacingSample>();
        for (int j = 0; j <= 360; j++)
        {
            if (j == 200) continue;
            poses.Add(new PoseSample(j / Hz, j * 5.0));
            presents.Add(new PresentSample(j / Hz, j, 0, j > 200 ? 1 : 0, RefreshIntervalMs: 1000.0 / Hz));
            pacing.Add(new PacingSample(j / Hz + 0.002, j > 200 ? 1 : 0));
        }
        var r = ScrollMetrics.RepeatsAndDrops(new SessionSeries(Array.Empty<InputSample>(), poses.ToArray(), presents.ToArray(),
                                                                pacing: pacing.ToArray()));
        Assert.Equal(1, r.Samples);
        Assert.Contains("per UI frame", r.Detail);
    }

    /// <summary>The first present row after an idle gap carries every idle vblank as a "repeat" (and an old counter its
    /// idle ticks): it is not a motion sample. Two motion bursts 1 s apart, perfectly paced → 0 events.</summary>
    [Fact]
    public void IdleVblanksAreNotChargedToTheFirstPresentAfterIdle()
    {
        var poses = new List<PoseSample>();
        var presents = new List<PresentSample>();
        var pacing = new List<PacingSample>();
        long repeated = 0;
        for (int j = 0; j <= 240; j++)
        {
            if (j > 60 && j < 180) { repeated++; continue; }   // idle: no presents, no UI frames; the glass repeats
            double pos = j <= 60 ? j * 5.0 : 300.0 + (j - 180) * 5.0;
            poses.Add(new PoseSample(j / Hz, pos));
            presents.Add(new PresentSample(j / Hz, j, 0, repeated, RefreshIntervalMs: 1000.0 / Hz));
            pacing.Add(new PacingSample(j / Hz, j >= 180 ? 119 : 0));   // a counter that charged the idle ticks
        }
        var r = ScrollMetrics.RepeatsAndDrops(new SessionSeries(Array.Empty<InputSample>(), poses.ToArray(), presents.ToArray(),
                                                                pacing: pacing.ToArray()));
        Assert.Equal(0, r.Samples);
        Assert.Equal(MetricVerdict.Green, r.Verdict);
    }

    /// <summary>A DWM sample is re-published on every present until the next 1 Hz sample: it counts once, on the row
    /// whose sample identity changed — and never from a recording without identities.</summary>
    [Fact]
    public void EachDwmSampleCountsOnce()
    {
        var poses = Grid(0, 360, t => t * Hz * 5.0);
        var presents = new List<PresentSample>();
        for (int j = 0; j <= 360; j++)
        {
            byte seq = (byte)(j < 100 ? 1 : 2);
            presents.Add(new PresentSample(j / Hz, j, 0, 0, DwmDropped: j >= 100 ? 1u : 0u, RefreshIntervalMs: 1000.0 / Hz, DwmSeq: seq));
        }
        var r = ScrollMetrics.RepeatsAndDrops(new SessionSeries(Array.Empty<InputSample>(), poses, presents.ToArray()));
        Assert.Equal(1, r.Samples);
        Assert.Equal(100 / Hz, r.WorstT, 9);

        var noIdentity = new List<PresentSample>();
        for (int j = 0; j <= 360; j++) noIdentity.Add(new PresentSample(j / Hz, j, 0, 0, DwmDropped: 1u, RefreshIntervalMs: 1000.0 / Hz));
        Assert.Equal(0, ScrollMetrics.RepeatsAndDrops(new SessionSeries(Array.Empty<InputSample>(), poses, noIdentity.ToArray())).Samples);
    }

    /// <summary>Superseded presents and display repeats count from the paired-counter ledger only; a recording made before
    /// it carries the lagging-sample artefact in its drop counter and idle vblanks in its repeat counter, which are
    /// reported as not attested instead of counted.</summary>
    [Fact]
    public void LedgerEventsCountOnlyFromThePairedCounterLedger()
    {
        var poses = Grid(0, 360, t => t * Hz * 5.0);
        PresentSample[] Presents(bool paired)
        {
            var list = new List<PresentSample>();
            for (int j = 0; j <= 360; j++)
                list.Add(new PresentSample(j / Hz, j, j >= 150 ? 1 : 0, j >= 250 ? 1 : 0, RefreshIntervalMs: 1000.0 / Hz, PairedLedger: paired));
            return list.ToArray();
        }
        var attested = ScrollMetrics.RepeatsAndDrops(new SessionSeries(Array.Empty<InputSample>(), poses, Presents(true)));
        Assert.Equal(2, attested.Samples);
        var old = ScrollMetrics.RepeatsAndDrops(new SessionSeries(Array.Empty<InputSample>(), poses, Presents(false)));
        Assert.Equal(0, old.Samples);
        Assert.Contains("not attested", old.Detail);
    }

    /// <summary>Per-present Turn rows make every missed tick attributable: the turn before the miss held the render loop
    /// past the next tick, and its dominant part names the cause.</summary>
    [Fact]
    public void MissedTicksAreAttributedFromThePreviousTurn()
    {
        const double tr = Tr * 1000.0;
        var poses = Grid(0, 400, t => t * Hz * 5.0);
        var turns = new List<TurnSample>();
        for (int j = 0; j <= 400; j++)
        {
            int missed = j is 101 or 201 or 301 ? 1 : 0;
            double slot = 1.0, work = 2.0;
            bool fresh = true;
            if (j == 99) slot = 2.0 * tr;                          // the present queue held the loop: slot-wait-bound
            if (j == 199) { work = 2.0 * tr; fresh = false; }      // a motion re-present's submit crossed: GPU-bound
            // j == 299: nothing held the loop → the tick reached it late: unattributed
            if (missed > 0) { turns.Add(new TurnSample(j / Hz, j, missed, 0.2, 1.0, 2.0, true)); continue; }
            if (j is 100 or 200 or 300) continue;                   // the missed tick: no present
            turns.Add(new TurnSample(j / Hz, j, 0, 0.2, slot, work, fresh));
        }
        var s = new SessionSeries(Array.Empty<InputSample>(), poses, refreshPeriodS: Tr, turns: turns.ToArray());
        var a = ScrollMetrics.MissedTickAttribution(s);
        Assert.Equal(3, a.Samples);
        Assert.Contains("1 slot-wait-bound / 0 UI-late / 1 GPU-bound / 1 unattributed", a.Detail);

        var r = ScrollMetrics.RepeatsAndDrops(s);
        Assert.Equal(3, r.Samples);
        Assert.Contains("per present", r.Detail);
    }

    [Fact]
    public void GesturesSplitOnGapsAndContactEnds()
    {
        var inputs = new[]
        {
            Notch(0.0), Notch(0.1), Notch(0.2),
            Notch(1.0), Notch(1.05),
            new InputSample(2.0, ScrollSourceCode.Touchpad, 1, 0f, 3f),
            new InputSample(2.01, ScrollSourceCode.Touchpad, InputSample.PhaseEnd, 0f, 0f),
            new InputSample(2.02, ScrollSourceCode.Touchpad, 1, 0f, 3f),
        };
        var g = ScrollMetrics.Gestures(new SessionSeries(inputs, Array.Empty<PoseSample>()));
        Assert.Equal(4, g.Count);
        Assert.Equal((0, 2), g[0]);
        Assert.Equal((3, 4), g[1]);
        Assert.Equal((5, 6), g[2]);
        Assert.Equal((7, 7), g[3]);
    }

    [Fact]
    public void SpinSpeedIsTheMedianSpeedBetweenFastNotches()
    {
        var inputs = new List<InputSample>();
        for (int i = 0; i < 10; i++) inputs.Add(Notch(1.0 + i * 0.05));
        var s = new SessionSeries(inputs.ToArray(), Grid(0, 180, t => t * 800.0), refreshPeriodS: Tr);
        var spin = ScrollMetrics.SustainedSpinSpeed(s);
        Assert.Equal(MetricVerdict.Info, spin.Verdict);
        Assert.InRange(spin.Value, 799.0, 801.0);
    }

    [Fact]
    public void EmptySessionsReportNoData()
    {
        var s = new SessionSeries(Array.Empty<InputSample>(), Array.Empty<PoseSample>());
        foreach (var m in ScrollMetrics.All(s))
            Assert.Equal(MetricVerdict.NoData, m.Verdict);
        Assert.Equal(1.0 / 60.0, s.RefreshPeriodS, 9);
    }

    [Fact]
    public void FromRowsBuildsOneViewportOnTheSessionClock()
    {
        const long origin = 1_000_000;
        const double freq = 10_000_000.0;
        long Q(double s) => origin + (long)Math.Round(s * freq);

        var ui = new List<ProbeRow>
        {
            ProbeRow.ForMark(Q(0.0), ProbeMark.SessionStart),
            ProbeRow.ForMark(Q(1.5), ProbeMark.UserFelt),
            ProbeRow.ForInput(Q(1.0), ScrollSourceCode.MouseWheel, ScrollProbe.PhaseNotch, 0f, 1f),
            ProbeRow.ForInput(0, ScrollSourceCode.MouseWheel, ScrollProbe.PhaseNotch, 0f, 1f),   // no device stamp → dropped
            ProbeRow.ForPresent(Q(1.0), 10, 0, 0, 0, 0, 0, 0.5, 6.944f, 4f, true, true),
            ProbeRow.ForPresent(Q(1.1), 20, 0, 0, 0, 0, 0, 0.5, 6.944f, 4f, true, true),
        };
        var render = new List<ProbeRow>();
        for (int k = 0; k < 10; k++) render.Add(ProbeRow.ForPose(Q(1.0 + k * 0.01), 7, 10.0 * k, 1000.0));
        for (int k = 0; k < 20; k++) render.Add(ProbeRow.ForPose(Q(1.0 + k * 0.01), 9, 0.0, 0.0));

        var s = SessionSeries.FromRows(ui.ToArray(), render.ToArray(), origin, freq);

        Assert.Equal(7, s.Viewport);
        Assert.Equal(10, s.Poses.Length);
        Assert.Single(s.Inputs);
        Assert.Equal(1.0, s.Inputs[0].T, 9);
        Assert.True(s.Inputs[0].IsNotch);
        Assert.Equal(2, s.Markers.Length);   // SessionStart + UserFelt, straight from the Mark rows
        Assert.Equal(1.5, s.Markers[1].T, 9);
        Assert.Equal(2, s.Presents.Length);
        Assert.Equal(0.006944, s.RefreshPeriodS, 5);
        Assert.Equal(15.0, s.PosAt(1.015), 6);
    }

    /// <summary>The notch curve metrics (shape, t50/t90, DIP per notch) measure the curve from the PLAN's anchor — a notch
    /// stamped before the last posed present is anchored at that pose floor, and the frames in between are latency, not
    /// curve shape. First-motion and notch→pose lag stay on the input stamp: they ARE the latency.</summary>
    [Fact]
    public void NotchCurveMetricsMeasureFromThePlanAnchor_LatencyMetricsFromTheStamp()
    {
        const double floor = 0.030;   // the anchor: 30 ms after the device stamp
        var plans = new[] { new PlanSample(NotchT + floor, PlanSample.KindWheel, 0.0, RefDip) };
        var s = new SessionSeries(new[] { Notch(NotchT) }, Grid(30, 150, Cubic(NotchT, delayS: floor)),
                                  refreshPeriodS: Tr, plans: plans);

        var shape = ScrollMetrics.NotchCurveShape(s);
        Assert.True(shape.Value < 1.0, $"rms {shape.Value}%");
        var t50 = ScrollMetrics.NotchT50(s);
        Assert.InRange(t50.Value, t50.Baseline - 8.0, t50.Baseline + 8.0);
        Assert.InRange(ScrollMetrics.DipPerNotch(s).Value, RefDip - 0.1, RefDip + 0.1);

        var first = ScrollMetrics.FirstMotionLatency(s);
        Assert.True(first.Value >= floor * 1000.0, $"first motion {first.Value} ms is measured from the stamp");
    }

    [Fact] public void OneMissedVblankIsOneEvent()
    { const double hz = 120.0; var poses = new List<PoseSample>(); var presents = new List<PresentSample>();
      for (int j = 0; j <= 360; j++) { if (j == 180) continue;
        poses.Add(new PoseSample(j / hz, j * 5.0));
        presents.Add(new PresentSample(j / hz, j, 0, j > 180 ? 1 : 0, RefreshIntervalMs: 1000.0 / hz)); }
      var r = ScrollMetrics.RepeatsAndDrops(new SessionSeries(Array.Empty<InputSample>(), poses.ToArray(), presents.ToArray()));
      Assert.Equal(1, r.Samples); }
}
