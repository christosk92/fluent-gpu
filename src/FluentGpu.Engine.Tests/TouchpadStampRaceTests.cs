using System;
using System.Collections.Generic;
using System.Diagnostics;
using FluentGpu.Hosting;
using FluentGpu.Pal;
using FluentGpu.Scroll.Diag.Analysis;
using FluentGpu.Scroll.Motion;
using FluentGpu.Scroll.Runtime;
using Xunit;

namespace FluentGpu.Engine.Tests;

/// <summary>The touchpad sample race (scroll-jitter plan A.0/A.4, 2026-09-29 RCA). DirectManipulation's samples are
/// produced on the UI thread once per frame, and the UI writes frame k's sample into <see cref="PlanSlots"/> within a
/// fraction of a millisecond of the render thread reading them for the SAME compositor tick — so either side can win.
/// Stamped for the tick that reads it (the old <c>now + round(clamp(PresentQpc − now, 0, refresh))</c> ≈ tick + one
/// refresh, which the render pose at tick + 2·refresh is always past), a present-clock ring held its newest sample and
/// tick k showed sample k when the write won and sample k−1 when the read did: +2/0 sample steps. Stamped by
/// <see cref="ContactStamp.ForFrame"/> — the NEXT tick's present — render tick k poses at exactly sample k−1's stamp and
/// shows it whatever the order: the shown position is a function of time, never of thread order.
///
/// <para>The model: a 120 Hz lattice built by the production builder (<see cref="RefreshLattice.Build"/>, depth 1 —
/// <c>PresentQpc = tick + 2T</c>), the UI writing sample k (position 10·k) at a jittered instant up to 0.5 ms after
/// tick k, and the render read of tick k (<see cref="PlanSlots"/> → <see cref="ScrollPlan.Eval"/> at the
/// <c>RenderPresentSec</c> law, <c>tick + 2T</c>) taken BEFORE or AFTER that write.</para></summary>
public sealed class TouchpadStampRaceTests
{
    private static readonly ScrollViewportId Vp = new(11, 1);
    private const int Frames = 120;
    private const double StepDip = 10.0;

    [Fact]
    public void RenderTick_ShowsTheSameSample_WhetherThisTicksSampleLandedBeforeOrAfterItsRead()
    {
        var runs = new List<(string Name, double[] Shown, int Befores)>();
        double[] allBefore = Drag(_ => true, out int allBeforeCount);
        runs.Add(("all-before", allBefore, allBeforeCount));
        double[] allAfter = Drag(_ => false, out int allAfterCount);
        runs.Add(("all-after", allAfter, allAfterCount));
        foreach (int seed in new[] { 1, 29, 20260929 })
        {
            var rng = new Random(seed);
            var readFirst = new bool[Frames];
            for (int k = 0; k < Frames; k++) readFirst[k] = rng.Next(2) == 0;
            double[] mixed = Drag(k => readFirst[k], out int mixedCount);
            runs.Add(($"seed {seed}", mixed, mixedCount));
        }

        Assert.Equal(Frames, allBeforeCount);
        Assert.Equal(0, allAfterCount);
        double[] reference = allBefore;
        double refreshS = (Stopwatch.Frequency / 120) / (double)Stopwatch.Frequency;
        foreach (var (name, shown, befores) in runs)
        {
            if (name.StartsWith("seed", StringComparison.Ordinal))
                Assert.True(befores > 0 && befores < Frames, $"{name}: the ordering must mix reads before and after the write ({befores}/{Frames} before)");

            // Identical: the tick's read never depends on whether this tick's sample landed first.
            Assert.Equal(reference.Length, shown.Length);
            for (int k = 0; k < shown.Length; k++)
                Assert.True(Math.Abs(shown[k] - reference[k]) <= 1e-9,
                    $"{name}: tick {k} showed {shown[k]:0.###} but the all-before ordering showed {reference[k]:0.###}");

            // Exactly one sample per tick: tick k shows sample k−1.
            for (int k = 1; k < shown.Length; k++)
                Assert.True(Math.Abs(shown[k] - StepDip * (k - 1)) <= 1e-9,
                    $"{name}: tick {k} showed {shown[k]:0.###}, expected sample {k - 1} = {StepDip * (k - 1):0.###}");

            // Regular: σ(Δ²p)/mean|Δp| over the moving ticks is Green (it is 0 — every step is one sample).
            var poses = new PoseSample[shown.Length];
            for (int k = 0; k < shown.Length; k++) poses[k] = new PoseSample(k * refreshS, shown[k]);
            var irregularity = ScrollMetrics.DisplacementIrregularity(
                new SessionSeries(Array.Empty<InputSample>(), poses, refreshPeriodS: refreshS));
            Assert.Equal(MetricVerdict.Green, irregularity.Verdict);
            Assert.True(irregularity.Value < 0.15, $"{name}: irregularity {irregularity.Value:0.###}");
        }
    }

    /// <summary>One 120-tick drag at 10 DIP per frame. Frame k's UI clock comes from <see cref="RefreshLattice.Build"/>
    /// (tick = origin + k·T, T = one 120 Hz refresh in QPC ticks, depth 1); its sample is stamped
    /// <see cref="ContactStamp.ForFrame"/> and written through the real <see cref="ScrollHandle"/> contact path
    /// (<see cref="ScrollHandle.ContactBeginHere"/> on <see cref="ContactClock.Present"/> grabbing the shown 0, then
    /// <see cref="ScrollHandle.ContactDelta"/>). The render read of tick k is the plan table's plan evaluated at the tick's
    /// present, taken before the write when <paramref name="readBeforeWrite"/> says so, else after. Plan seconds are
    /// <c>qpc / Stopwatch.Frequency</c> — the host's <c>ScrollFrameQpcToSec</c>.</summary>
    private static double[] Drag(Func<int, bool> readBeforeWrite, out int befores)
    {
        long f = Stopwatch.Frequency;
        long refresh = f / 120;
        long origin = 1_000 * f;
        var slots = new PlanSlots();
        double nowSec = origin / (double)f;
        var handle = new ScrollHandle(slots, Vp, () => nowSec);
        handle.SetExtent(1_000_000.0, 500.0);
        var jitter = new Random(7);   // the same UI timing in every ordering (the stamp must not read it anyway)
        var shown = new double[Frames];
        long lastFrame = 0;
        befores = 0;
        for (int k = 0; k < Frames; k++)
        {
            long tick = origin + k * refresh;
            long now = tick + jitter.Next(0, (int)(f / 2_000));   // the UI writes within 0.5 ms after the tick
            FrameClock clock = RefreshLattice.Build(tickAvailable: true, tickQpc: tick, refreshQpc: refresh, nowQpc: now,
                                                    lastFrameQpc: lastFrame, seq: (ulong)(k + 1));
            lastFrame = clock.FrameQpc;
            nowSec = now / (double)f;
            double presentSec = clock.PresentQpc / (double)f;   // render tick k's present (RenderPresentSec: tick + 2·refresh)
            double stampSec = ContactStamp.ForFrame(in clock, now) / (double)f;

            bool before = readBeforeWrite(k);
            if (before) { shown[k] = RenderRead(slots, presentSec); befores++; }
            if (k == 0) handle.ContactBeginHere(stampSec, ContactClock.Present);
            else handle.ContactDelta(stampSec, StepDip);
            if (!before) shown[k] = RenderRead(slots, presentSec);
        }
        return shown;
    }

    /// <summary>The render poser's read: the viewport's plan from the table, evaluated at the tick's present.</summary>
    private static double RenderRead(PlanSlots slots, double presentSec)
        => slots.TryRead(Vp, out ScrollPlan plan) ? plan.Eval(presentSec, out _, out _) : double.NaN;
}
