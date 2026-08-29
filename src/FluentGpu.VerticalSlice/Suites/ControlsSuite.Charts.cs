using System;
using System.Collections.Generic;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Hosting;
using FluentGpu.Pal;
using FluentGpu.Pal.Headless;
using FluentGpu.Render;
using FluentGpu.Rhi.Headless;
using FluentGpu.Signals;
using FluentGpu.Text.Headless;
using static FluentGpu.VerticalSlice.Harness.Gate;
using static FluentGpu.VerticalSlice.Harness.Asserts;

// ── Charts (FluentGpu.Controls/Charts): the pure maths, the three distribution primitives, the Cartesian frame ─────────
//
// gate.ctl.charts.* — ChartMath is gated without a scene (SplitterMath precedent); the components run headlessly at a
// pinned size (kit sizing rule: finite = pinned) so layout, hit regions, path draws and the memoised geometry can be
// observed on the headless device. No source-text tests: every check is behaviour.

static partial class ControlsSuite
{
    static void ChartsChecks(StringTable strings)
    {
        ChartMathChecks();
        ChartStyleChecks();
        SparkBarsLayoutChecks(strings);
        WaveformLayoutChecks(strings);
        DensityPlotChecks(strings);
        CartesianChartChecks(strings);
        ChartTooltipChecks(strings);
    }

    // ── maths ────────────────────────────────────────────────────────────────────────────────────────────────────

    static void ChartMathChecks()
    {
        // KDE: peak normalised to 1 at the data; symmetric input → symmetric output; outliers clamp into the edge;
        // empty → zeros; bandwidth 0 falls back to the default.
        Span<float> d = stackalloc float[71];
        ChartMath.Kde([100f, 100f, 100f], 60f, 200f, 5f, d);
        bool peakOne = Near(d[20], 1f, 1e-4f);                    // x_20 = 60 + 2·20 = 100
        bool tailZero = d[70] < 1e-6f;
        ChartMath.Kde([90f, 110f], 60f, 200f, 5f, d);
        bool symmetric = Near(d[15], d[25], 1e-4f);
        ChartMath.Kde([240f], 60f, 200f, 5f, d);
        bool clamps = Near(d[70], 1f, 1e-4f) && d[0] < 1e-6f;
        ChartMath.Kde(ReadOnlySpan<float>.Empty, 60f, 200f, 5f, d);
        bool empty = d[0] == 0f && d[35] == 0f && d[70] == 0f;
        bool bw = Near(ChartMath.DefaultBandwidth(60f, 200f), 5f, 1e-4f);
        // The binned evaluation must match the direct O(N×samples) sum on a large, spread corpus (the whole point of
        // binning is that a 5 000-track library costs what a 50-track playlist costs, with the same picture).
        var big = new float[5000];
        for (int k = 0; k < big.Length; k++) big[k] = 60f + (k * 7919 % 14000) / 100f;
        ChartMath.Kde(big, 60f, 200f, 5f, d);
        float worst = 0f, directPeak = 0f;
        Span<float> direct = stackalloc float[71];
        for (int i = 0; i < 71; i++)
        {
            float x = 60f + 140f * i / 70f, acc = 0f;
            for (int k = 0; k < big.Length; k++) { float z = (big[k] - x) / 5f; acc += MathF.Exp(-0.5f * z * z); }
            direct[i] = acc; if (acc > directPeak) directPeak = acc;
        }
        for (int i = 0; i < 71; i++) worst = MathF.Max(worst, MathF.Abs(direct[i] / directPeak - d[i]));
        bool binned = worst < 2e-3f;
        Check("gate.ctl.charts.math.kde peak → 1 at the data, symmetric, outliers clamp to the edge, empty → zeros, default bandwidth = span/28, binned == direct on 5 000 values",
            peakOne && tailZero && symmetric && clamps && empty && bw && binned,
            $"peak={peakOne} tail={tailZero} sym={symmetric} clamp={clamps} empty={empty} bw={bw} binnedErr={worst:0.0000}");

        // Rug stacking: same rounded integer → depth 0,1,2,…; 127.6 rounds with 128.
        Span<int> stack = stackalloc int[4];
        int deepest = ChartMath.RugStacks([128f, 128f, 128f, 127.6f], stack);
        Check("gate.ctl.charts.math.rug three 128s stack 0,1,2 and 127.6 rounds onto them",
            stack[0] == 0 && stack[1] == 1 && stack[2] == 2 && stack[3] == 3 && deepest == 3,
            $"stack={stack[0]},{stack[1]},{stack[2]},{stack[3]} deepest={deepest}");

        // Share: top/total, first wins a tie, zero total → 0.
        float share = ChartMath.Share([10, 30, 60], out int top);
        float zero = ChartMath.Share([0, 0], out int topZero);
        Check("gate.ctl.charts.math.share [10,30,60] → 0.6 at index 2; all-zero → 0",
            Near(share, 0.6f, 1e-4f) && top == 2 && zero == 0f && topZero == 0, $"share={share} top={top} zero={zero}");

        // Extent: NaN-safe; stacked sums positives and negatives apart; bars fold in zero; expand is 0..1.
        float[][] v = [[1f, 5f, float.NaN], [2f, -3f, 4f]];
        var plain = ChartMath.Extent(v, ChartStacking.None, includeZero: false);
        var stacked = ChartMath.Extent(v, ChartStacking.Stacked, includeZero: false);
        var bars = ChartMath.Extent([[2f, 4f]], ChartStacking.None, includeZero: true);
        var expand = ChartMath.Extent(v, ChartStacking.Expand, includeZero: false);
        Check("gate.ctl.charts.math.extent plain (-3,5); stacked (-3,5); bars include zero (0,4); expand (0,1)",
            plain == (-3f, 5f) && stacked == (-3f, 5f) && bars == (0f, 4f) && expand == (0f, 1f),
            $"plain={plain} stacked={stacked} bars={bars} expand={expand}");

        // Nice ticks: 1/2/5·10^k steps, widened domain, inclusive ticks; negatives; flat range pads.
        Span<float> ticks = stackalloc float[24];
        int n1 = ChartMath.NiceTicks(0f, 87f, 5, ticks, out float lo1, out float hi1, out float step1);
        bool nice1 = Near(step1, 20f, 1e-4f) && Near(lo1, 0f) && Near(hi1, 100f) && n1 == 6 && Near(ticks[3], 60f, 1e-3f);
        int n2 = ChartMath.NiceTicks(-13f, 42f, 5, ticks, out float lo2, out float hi2, out float step2);
        bool nice2 = Near(step2, 10f, 1e-4f) && Near(lo2, -20f) && Near(hi2, 50f) && n2 == 8;
        int n3 = ChartMath.NiceTicks(7f, 7f, 5, ticks, out float lo3, out float hi3, out _);
        bool nice3 = n3 >= 2 && lo3 < 7f && hi3 > 7f;
        Check("gate.ctl.charts.math.nice 0..87 → step 20 over 0..100 (6 ticks); -13..42 → step 10 over -20..50; a flat range pads",
            nice1 && nice2 && nice3, $"n1={n1} step={step1} lo={lo1} hi={hi1} | n2={n2} step={step2} lo={lo2} hi={hi2} | n3={n3} {lo3}..{hi3}");

        // Stack / expand.
        float[][] src = [[1f, 2f], [3f, 4f]];
        float[][] cum = [new float[2], new float[2]];
        ChartMath.Stack(src, cum);
        bool stackOk = Near(cum[0][0], 1f) && Near(cum[0][1], 2f) && Near(cum[1][0], 4f) && Near(cum[1][1], 6f);
        ChartMath.Expand(src, cum);
        bool expandOk = Near(cum[0][0], 0.25f, 1e-4f) && Near(cum[0][1], 1f / 3f, 1e-4f) && Near(cum[1][0], 1f, 1e-4f) && Near(cum[1][1], 1f, 1e-4f);
        Check("gate.ctl.charts.math.stack cumulative upper edges; expand rows sum to 1", stackOk && expandOk, $"stack={stackOk} expand={expandOk}");

        // Curves: monotone never overshoots (controls stay within the segment's y range); natural is C1 at the joins
        // (mirror-symmetric handles) and anchors every point; step/linear verb counts.
        ReadOnlySpan<Point2> mono = [new(0f, 0f), new(1f, 1f), new(2f, 1f), new(3f, 2f), new(4f, 2f)];
        Span<Point2> ctrl = stackalloc Point2[8];
        ChartMath.CubicControls(mono, ChartCurve.Monotone, ctrl);
        bool noOvershoot = true;
        for (int i = 0; i < 4; i++)
        {
            float lo = MathF.Min(mono[i].Y, mono[i + 1].Y) - 1e-4f, hi = MathF.Max(mono[i].Y, mono[i + 1].Y) + 1e-4f;
            if (ctrl[i * 2].Y < lo || ctrl[i * 2].Y > hi || ctrl[i * 2 + 1].Y < lo || ctrl[i * 2 + 1].Y > hi) noOvershoot = false;
        }
        ReadOnlySpan<Point2> nat = [new(0f, 0f), new(1f, 3f), new(2f, -1f), new(3f, 2f)];
        Span<Point2> nctrl = stackalloc Point2[6];
        ChartMath.CubicControls(nat, ChartCurve.Natural, nctrl);
        bool c1 = true;
        for (int j = 1; j <= 2; j++)
        {
            // handle out of p[j] mirrors the handle into p[j]
            var into = nctrl[(j - 1) * 2 + 1]; var outOf = nctrl[j * 2]; var pj = nat[j];
            if (!Near(pj.X - into.X, outOf.X - pj.X, 1e-3f) || !Near(pj.Y - into.Y, outOf.Y - pj.Y, 1e-3f)) c1 = false;
        }
        var bl = new PathBuilder(); ChartMath.AppendCurve(bl, mono.Slice(0, 4), ChartCurve.Linear, ctrl);
        var bs = new PathBuilder(); ChartMath.AppendCurve(bs, mono.Slice(0, 4), ChartCurve.Step, ctrl);
        var bg = new PathBuilder();
        int contours = ChartMath.AppendCurve(bg, [new(0f, 0f), new(1f, 1f), new(float.NaN, float.NaN), new(3f, 1f), new(4f, 0f)], ChartCurve.Linear, ctrl);
        Check("gate.ctl.charts.math.curve monotone controls never overshoot; natural is C1 at joins; linear M+3L; step M+6L; a NaN splits contours",
            noOvershoot && c1 && bl.Verbs.Count == 4 && bs.Verbs.Count == 7 && contours == 2,
            $"overshoot={!noOvershoot} c1={c1} linear={bl.Verbs.Count} step={bs.Verbs.Count} contours={contours}");

        // Hover + tick thinning.
        bool nearest = ChartMath.NearestIndex(55f, 0f, 10f, 12) == 5 && ChartMath.NearestIndex(-5f, 0f, 10f, 12) == 0
            && ChartMath.NearestIndex(500f, 0f, 10f, 12) == 11 && ChartMath.NearestIndex(5f, 0f, 10f, 0) == -1;
        Span<bool> keep = stackalloc bool[5];
        int kept = ChartMath.DropCollidingTicks([0f, 10f, 20f, 30f, 100f], [20f, 20f, 20f, 20f, 20f], 4f, preserveEnds: true, keep);
        bool thin = kept == 3 && keep[0] && !keep[1] && !keep[2] && keep[3] && keep[4];
        Span<bool> keep2 = stackalloc bool[2];
        int kept2 = ChartMath.DropCollidingTicks([0f, 12f], [20f, 20f], 4f, preserveEnds: true, keep2);
        bool ends = kept2 == 1 && !keep2[0] && keep2[1];
        Check("gate.ctl.charts.math.ticks nearest slot clamps to the range; collision thinning keeps the first/last and drops the colliders",
            nearest && thin && ends, $"nearest={nearest} thin={thin} kept={kept} ends={ends}");
    }

    static void ChartStyleChecks()
    {
        var saved = (SparkBars.StyleOverride, DensityPlot.StyleOverride, CartesianChart.StyleOverride, Waveform.StyleOverride, ChartTooltip.StyleOverride);
        try
        {
            SparkBars.StyleOverride = null; DensityPlot.StyleOverride = null; CartesianChart.StyleOverride = null;
            Waveform.StyleOverride = null; ChartTooltip.StyleOverride = null;
            bool tok = SparkBars.DefaultStyle.Ink.Equals(Tok.AccentDefault) && SparkBars.DefaultStyle.HoverInk.Equals(Tok.AccentTextPrimary)
                && DensityPlot.DefaultStyle.Ink.Equals(Tok.AccentDefault) && DensityPlot.DefaultStyle.Hairline.Equals(Tok.StrokeCardDefault)
                && CartesianChart.DefaultStyle.GridInk.Equals(Tok.StrokeCardDefault) && CartesianChart.DefaultStyle.AxisInk.Equals(Tok.TextTertiary)
                && Waveform.DefaultStyle.Ink.Equals(Tok.TextTertiary) && Waveform.DefaultStyle.PlayedInk.Equals(Tok.AccentDefault)
                && ChartTooltip.DefaultStyle.Fill.Equals(Tok.FillSolidBase);
            var partial = SparkBars.DefaultStyle with { Gap = 5f };
            bool withKeepsTok = partial.Ink.Equals(Tok.AccentDefault) && Near(partial.Gap, 5f);
            SparkBars.StyleOverride = new SparkBars.Style { Ink = Tok.TextPrimary };
            CartesianChart.StyleOverride = new CartesianChart.Style { GridInk = Tok.AccentDefault };
            bool hook = SparkBars.DefaultStyle.Ink.Equals(Tok.TextPrimary) && CartesianChart.DefaultStyle.GridInk.Equals(Tok.AccentDefault);
            var ramp = ChartPalette.Ramp(9);
            bool palette = ramp.Length == 9 && ramp[0].Equals(Tok.AccentDefault) && !ramp[1].Equals(ramp[2]) && ramp[7].Equals(ramp[8]);
            Check("gate.ctl.charts.style every DefaultStyle fills from Tok only; with keeps colours; StyleOverride wins; the palette ramp is one hue in fixed order",
                tok && withKeepsTok && hook && palette, $"tok={tok} with={withKeepsTok} hook={hook} palette={palette}");
        }
        finally
        {
            (SparkBars.StyleOverride, DensityPlot.StyleOverride, CartesianChart.StyleOverride, Waveform.StyleOverride, ChartTooltip.StyleOverride) = saved;
        }
    }

    // ── SparkBars / Waveform (layout only) ───────────────────────────────────────────────────────────────────────

    static void SparkBarsLayoutChecks(StringTable strings)
    {
        int clicks = 0;
        var bars = new SparkBar[12];
        for (int i = 0; i < 12; i++) bars[i] = new SparkBar(i == 5 ? 10f : i == 7 ? 0f : 4f, "tip " + i, Lit: i == 3, Accent: i == 5, OnClick: () => clicks++);
        var el = new BoxEl { Width = 240f, Height = 38f, Children = [SparkBars.Create(new SparkBarsModel(bars))] };
        var scene = LayoutTree(strings, el);
        var buttons = Roles(scene, AutomationRole.Button);
        bool twelve = buttons.Count == 12;
        // Column 5 (the peak) holds a bar the full strip height; column 7 (empty) holds the MinBar floor.
        float peakH = 0f, emptyH = 0f, litW = 0f;
        if (twelve)
        {
            peakH = scene.Bounds(Child(scene, buttons[5], 0)).H;
            emptyH = scene.Bounds(Child(scene, buttons[7], 0)).H;
            litW = scene.Bounds(buttons[3]).W;
        }
        var lit = twelve ? FindFillNode(scene, buttons[3], Tok.AccentSubtle) : default;
        Check("gate.ctl.charts.spark.layout 12 Button columns; the peak bar is the full height, an empty bucket keeps the floor, columns share the width, the lit column carries AccentSubtle",
            twelve && Near(peakH, 38f) && Near(emptyH, 3f) && litW > 15f && litW < 20f && !lit.IsNull,
            $"buttons={buttons.Count} peak={peakH} empty={emptyH} litW={litW} lit={!lit.IsNull}");
    }

    static void WaveformLayoutChecks(StringTable strings)
    {
        var peaks = new float[300];
        for (int i = 0; i < peaks.Length; i++) peaks[i] = i % 7 == 0 ? 0f : 0.5f + 0.5f * MathF.Sin(i * 0.1f);
        var el = Waveform.Create(new WaveformModel(peaks, 64));
        var scene = LayoutTree(strings, el);
        var root = scene.Root;               // no progress signal ⇒ the strip IS the root
        int barCount = 0; float minH = float.MaxValue, maxH = 0f; bool centred = true;
        float stripH = scene.Bounds(root).H;
        for (int i = 0; ; i++)
        {
            var bar = Child(scene, root, i);
            if (bar.IsNull) break;
            var b = scene.Bounds(bar);
            barCount++;
            minH = MathF.Min(minH, b.H); maxH = MathF.Max(maxH, b.H);
            if (!Near(b.Y + b.H / 2f, stripH / 2f, 0.6f)) centred = false;
        }
        Check("gate.ctl.charts.waveform.layout 64 mirrored bars centred on the midline; silence keeps the 2 px floor; the strip is Bars·(w+gap)−gap wide",
            barCount == 64 && Near(minH, 2f) && maxH > 20f && centred && Near(scene.Bounds(root).W, 64f * 3f - 1f),
            $"bars={barCount} minH={minH} maxH={maxH} centred={centred} w={scene.Bounds(root).W}");
    }

    // ── DensityPlot (headless host: measured width, bands, path layers, memo) ────────────────────────────────────

    sealed class DensityProbe : Component
    {
        public readonly float[] Values;
        public readonly Signal<int> Lit = new(-1);
        public int Clicks;
        public DensityProbe()
        {
            Values = new float[50];
            for (int i = 0; i < 50; i++) Values[i] = i < 25 ? 100f + (i % 5) : 165f + (i % 7);
        }
        public override Element Render()
        {
            int lit = Lit.Value;
            var bands = new PlotBand[]
            {
                new(60f, 90f, "under 90", lit == 0, () => Clicks++),
                new(90f, 120f, "90-119", lit == 1, () => Clicks++),
                new(120f, 140f, "120-139", lit == 2, () => Clicks++),
                new(140f, 200f, "140+", lit == 3, () => Clicks++),
            };
            var model = new DensityPlotModel(Values, 60f, 200f)
            {
                Bands = bands, Hairlines = new float[] { 90f, 120f, 140f }, Marker = 128f,
                Ticks = new PlotTick[] { new(80f, "80"), new(100f, "100"), new(120f, "120"), new(140f, "140"), new(160f, "160"), new(180f, "180") },
            };
            return new BoxEl { Width = 200f, Padding = Edges4.All(10f), Children = [DensityPlot.Create(model)] };
        }
    }

    static void DensityPlotChecks(StringTable strings)
    {
        using var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("charts-density", new Size2(240, 120), 1f));
        window.Show();
        var device = new HeadlessGpuDevice();
        var probe = new DensityProbe();
        using var host = new AppHost(app, window, device, new HeadlessFontSystem(strings), strings, probe);
        // Frame 1 lays out (bands only — no width yet); the measured width lands and frame 2 draws the ridge.
        host.RunFrame(); host.RunFrame(); host.RunFrame();

        var bands = Roles(host.Scene, AutomationRole.Button);
        bool four = bands.Count == 4;
        float w0 = four ? host.Scene.Bounds(bands[0]).W : 0f, w3 = four ? host.Scene.Bounds(bands[3]).W : 0f;
        // 180 px stage: 30/140 → 38.6, 60/140 → 77.1
        bool shares = Near(w0, 180f * 30f / 140f, 1f) && Near(w3, 180f * 60f / 140f, 1f);
        int fills = device.LastFillPaths.Count, strokes = device.LastStrokePaths.Count;
        // area + one rug group (all values share the default ink) = 2 fills; hairlines + line + marker = 3 strokes.
        bool layers = fills == 2 && strokes == 3;
        int fillId = fills > 0 ? device.LastFillPaths[0].RealizationId : -1;

        // Toggle the lit band: the component re-renders (a band takes the AccentSubtle wash) but the geometry key is
        // unchanged, so the same realizations are replayed — no re-tessellation.
        probe.Lit.Value = 2;
        host.RunFrame(); host.RunFrame();
        bool litWash = !FindFillNode(host.Scene, host.Scene.Root, Tok.AccentSubtle).IsNull;
        bool sameRealization = device.LastFillPaths.Count == 2 && device.LastFillPaths[0].RealizationId == fillId;

        if (four) ClickNode(host, window, bands[2]);
        Check("gate.ctl.charts.density.layers four band Buttons sized by domain share; area+rug fills and hairline+line+marker strokes once the width is measured; a lit change re-inks without re-tessellating; a band click fires once",
            four && shares && layers && litWash && sameRealization && probe.Clicks == 1,
            $"bands={bands.Count} w0={w0:0.0} w3={w3:0.0} fills={fills} strokes={strokes} lit={litWash} sameRealization={sameRealization} clicks={probe.Clicks}");

        DensityRugCapChecks(strings);
    }

    sealed class DenseRugProbe : Component
    {
        public override Element Render()
        {
            // 3 000 values crowding 60–200: without de-duplication that is 3 000 tick contours per rebuild.
            var values = new float[3000];
            for (int i = 0; i < values.Length; i++) values[i] = 60f + (i * 37 % 1400) / 10f;
            return new BoxEl { Width = 220f, Padding = Edges4.All(10f), Children = [DensityPlot.Create(new DensityPlotModel(values, 60f, 200f))] };
        }
    }

    static void DensityRugCapChecks(StringTable strings)
    {
        using var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("charts-density-rug", new Size2(240, 100), 1f));
        window.Show();
        var device = new HeadlessGpuDevice();
        int builds0 = DensityPlot.GeometryBuilds;
        using var host = new AppHost(app, window, device, new HeadlessFontSystem(strings), strings, new DenseRugProbe());
        host.RunFrame(); host.RunFrame(); host.RunFrame(); host.RunFrame();
        // The rug is the fill at RugAlpha (the area is at AreaAlpha). The tessellator emits 16 vertices per tick
        // contour (the 4 corners + the anti-aliasing fringe); the stage is 200 px wide, so a de-duplicated rug has at
        // most ~201 ticks (~3 216 vertices) — 3 000 un-deduplicated ticks would be 48 000.
        int rugVtx = -1;
        for (int i = 0; i < device.LastFillPaths.Count; i++)
            if (MathF.Abs(device.LastFillPaths[i].Fill.A - DensityPlot.DefaultStyle.RugAlpha) < 0.02f) rugVtx = device.LastFillPaths[i].VtxCount;
        int builds = DensityPlot.GeometryBuilds - builds0;
        Check("gate.ctl.charts.density.rug-cap a 3 000-value tick rug is de-duplicated per pixel column (≤ ~200 ticks), and the geometry was built once for the measured width",
            rugVtx > 0 && rugVtx <= 204 * 16 && builds == 1, $"rugVtx={rugVtx} builds={builds} fills={device.LastFillPaths.Count}");
    }

    // ── Cartesian frame (Line / Area / Bar) ──────────────────────────────────────────────────────────────────────

    static CartesianData SampleData(int points = 6)
    {
        string[] cats = ["Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug"];
        var series = new ChartSeries[] { new("desktop", "Desktop", Tok.AccentDefault), new("mobile", "Mobile", Tok.AccentTextPrimary), new("other", "Other", Tok.TextTertiary) };
        var values = new float[3][];
        for (int s = 0; s < 3; s++)
        {
            values[s] = new float[points];
            for (int i = 0; i < points; i++) values[s][i] = 40f + 30f * MathF.Sin(i * 0.9f + s) + 10f * s;
        }
        return new CartesianData(cats[..points], series, values);
    }

    sealed class CartesianProbe : Component
    {
        public readonly CartesianChart.Kind Kind;
        public readonly Signal<int> Active = new(-1);
        public readonly CartesianData Data;
        public readonly BarChartOptions? Bar;
        public CartesianProbe(CartesianChart.Kind kind, CartesianData data, BarChartOptions? bar = null) { Kind = kind; Data = data; Bar = bar; }
        public override Element Render()
        {
            var o = new CartesianChartOptions { Width = 320f, Height = 180f, ActiveIndex = Active, XAxis = new ChartAxisOptions { Show = true }, YAxis = new ChartAxisOptions { Show = false } };
            return Kind switch
            {
                CartesianChart.Kind.Line => LineChart.Create(Data, new LineChartOptions { Curve = ChartCurve.Monotone, Dots = ChartDotMode.None }, o),
                CartesianChart.Kind.Area => AreaChart.Create(Data, new AreaChartOptions { Stacking = ChartStacking.Stacked }, o),
                _ => BarChart.Create(Data, Bar ?? new BarChartOptions(), o),
            };
        }
    }

    static void CartesianChartChecks(StringTable strings)
    {
        // Line: grid + 3 series strokes; hover → nearest category; keyboard stepping; exit clears.
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("charts-line", new Size2(360, 220), 1f));
            window.Show();
            var device = new HeadlessGpuDevice();
            var probe = new CartesianProbe(CartesianChart.Kind.Line, SampleData());
            using var host = new AppHost(app, window, device, new HeadlessFontSystem(strings), strings, probe);
            host.RunFrame(); host.RunFrame();
            int strokes = device.LastStrokePaths.Count;                 // 1 grid + 3 lines
            bool gridDashed = false;
            for (int i = 0; i < device.LastStrokePaths.Count; i++) if (device.LastStrokePaths[i].DashOn > 0f) gridDashed = true;
            bool xAxis = HasGlyph(device, strings, "Jan") && HasGlyph(device, strings, "Jun");

            // Plot = 320 wide (Y axis hidden) × 162 tall; slot = 53.33 → x = 3·53.33 + 10 lands in category 3.
            window.QueueInput(new InputEvent(InputKind.PointerMove, new Point2(3 * 320f / 6f + 10f, 60f), 0, 0));
            host.RunFrame(); host.RunFrame();
            int hovered = probe.Active.Value;
            bool tooltip = HasGlyph(device, strings, "Desktop") && HasGlyph(device, strings, "Apr");
            int strokesWhileHovered = device.LastStrokePaths.Count;    // the series geometry is unchanged: same 4 strokes
            window.QueueInput(new InputEvent(InputKind.PointerMove, new Point2(100f, 215f), 0, 0));   // below the plot → exit
            host.RunFrame(); host.RunFrame();
            int afterExit = probe.Active.Value;

            // Keyboard: click the plot to focus it, then step.
            var plot = host.Scene.Root;
            window.QueueInput(new InputEvent(InputKind.PointerDown, new Point2(20f, 100f), 0, 0));
            window.QueueInput(new InputEvent(InputKind.PointerUp, new Point2(20f, 100f), 0, 0));
            host.RunFrame();
            window.QueueInput(new InputEvent(InputKind.PointerMove, new Point2(100f, 215f), 0, 0));
            host.RunFrame();
            window.QueueInput(new InputEvent(InputKind.Key, default, 0, Keys.Right)); host.RunFrame();
            int k1 = probe.Active.Value;
            window.QueueInput(new InputEvent(InputKind.Key, default, 0, Keys.Right)); host.RunFrame();
            int k2 = probe.Active.Value;
            window.QueueInput(new InputEvent(InputKind.Key, default, 0, Keys.End)); host.RunFrame();
            int kEnd = probe.Active.Value;
            window.QueueInput(new InputEvent(InputKind.Key, default, 0, Keys.Escape)); host.RunFrame();
            int kEsc = probe.Active.Value;

            Check("gate.ctl.charts.cartesian.line dashed grid + one stroke per series; category captions; hover resolves the slot and shows the tooltip without touching series geometry; exit clears; ←/→/End/Esc step the index",
                strokes == 4 && gridDashed && xAxis && hovered == 3 && tooltip && strokesWhileHovered == 4 && afterExit == -1
                && k1 == 0 && k2 == 1 && kEnd == 5 && kEsc == -1,
                $"strokes={strokes} dashed={gridDashed} axis={xAxis} hover={hovered} tip={tooltip} strokesHover={strokesWhileHovered} exit={afterExit} keys={k1},{k2},{kEnd},{kEsc}");
        }

        // Area (stacked): one fill per series, drawn bottom-up, fills are translucent series colours.
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("charts-area", new Size2(360, 220), 1f));
            window.Show();
            var device = new HeadlessGpuDevice();
            var probe = new CartesianProbe(CartesianChart.Kind.Area, SampleData());
            using var host = new AppHost(app, window, device, new HeadlessFontSystem(strings), strings, probe);
            host.RunFrame(); host.RunFrame();
            int fills = device.LastFillPaths.Count;
            bool translucent = fills == 3 && device.LastFillPaths[0].Fill.A < 0.5f && device.LastFillPaths[0].Fill.A > 0.1f;
            // Stacked upper edges: the top series' path bounds rise above the bottom series' bounds.
            bool ordered = fills == 3 && device.LastFillPaths[2].Rect.Y <= device.LastFillPaths[0].Rect.Y + 0.5f;
            Check("gate.ctl.charts.cartesian.area stacked: one translucent fill per series, painted bottom-up with cumulative upper edges",
                fills == 3 && translucent && ordered, $"fills={fills} translucent={translucent} ordered={ordered}");
        }

        // Bars: grouped → categories × series rects; negative values grow below the zero line; stacked → one column per category.
        {
            var data = SampleData(4);
            data.Values[1][2] = -20f;    // one negative cell
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("charts-bar", new Size2(360, 220), 1f));
            window.Show();
            var device = new HeadlessGpuDevice();
            var probe = new CartesianProbe(CartesianChart.Kind.Bar, data, new BarChartOptions { CellColor = (s, i, v) => v < 0f ? Tok.SystemFillCritical : null });
            using var host = new AppHost(app, window, device, new HeadlessFontSystem(strings), strings, probe);
            host.RunFrame(); host.RunFrame();
            var scene = host.Scene;
            int accentBars = CountFills(scene, scene.Root, Tok.AccentDefault);
            var negative = FindFillNode(scene, scene.Root, Tok.SystemFillCritical);
            var positive = FindFillNode(scene, scene.Root, Tok.AccentDefault);
            bool negBelow = !negative.IsNull && !positive.IsNull && scene.AbsoluteRect(negative).Y > scene.AbsoluteRect(positive).Y + scene.AbsoluteRect(positive).H - 0.5f;
            // Hover category 1 → the active wash appears (AccentSubtle) over that slot.
            window.QueueInput(new InputEvent(InputKind.PointerMove, new Point2(1 * 320f / 4f + 20f, 60f), 0, 0));
            host.RunFrame(); host.RunFrame();
            bool wash = probe.Active.Value == 1 && !FindFillNode(scene, scene.Root, Tok.AccentSubtle).IsNull;
            Check("gate.ctl.charts.cartesian.bar grouped bars: one rect per (category, series) in the series colour; a negative cell is recoloured and hangs below the zero line; hover washes the active slot",
                accentBars == 4 && negBelow && wash, $"accentBars={accentBars} negBelow={negBelow} wash={wash}");
        }
    }

    static void ChartTooltipChecks(StringTable strings)
    {
        var data = SampleData(3);
        var full = LayoutTree(strings, ChartTooltip.Content(data, 1, new ChartTooltipOptions()));
        bool rows = !FindTextNode(full, strings, full.Root, "Desktop").IsNull && !FindTextNode(full, strings, full.Root, "Mobile").IsNull
            && !FindTextNode(full, strings, full.Root, "Feb").IsNull && !FindFillNode(full, full.Root, Tok.AccentDefault).IsNull;
        var noInd = LayoutTree(strings, ChartTooltip.Content(data, 1, new ChartTooltipOptions { HideIndicator = true }));
        bool hidden = FindFillNode(noInd, noInd.Root, Tok.AccentDefault).IsNull && !FindTextNode(noInd, strings, noInd.Root, "Desktop").IsNull;
        var single = new CartesianData(data.Categories, [data.Series[0]], [data.Values[0]]);
        var nested = LayoutTree(strings, ChartTooltip.Content(single, 1, new ChartTooltipOptions { Indicator = ChartTooltipIndicator.Line }));
        // nestLabel: the header is folded into the row — still present as text, but the row holds both the label and the series.
        bool nest = !FindTextNode(nested, strings, nested.Root, "Feb").IsNull && !FindTextNode(nested, strings, nested.Root, "Desktop").IsNull;
        var formatted = LayoutTree(strings, ChartTooltip.Content(data, 1, new ChartTooltipOptions { LabelFormatter = i => "Week " + i, ValueFormatter = v => v.ToString("0.0") + " k" }));
        bool fmt = !FindTextNode(formatted, strings, formatted.Root, "Week 1").IsNull;
        Check("gate.ctl.charts.tooltip header + one row per series with a swatch; HideIndicator drops the swatches; a single non-dot series nests the label; formatters apply",
            rows && hidden && nest && fmt, $"rows={rows} hidden={hidden} nest={nest} fmt={fmt}");
    }

    static int CountFills(FluentGpu.Scene.SceneStore s, NodeHandle n, ColorF fill)
    {
        int count = 0;
        Walk(s, n);
        return count;
        void Walk(FluentGpu.Scene.SceneStore sc, NodeHandle h)
        {
            if (h.IsNull) return;
            if (sc.Paint(h).Fill.Equals(fill)) count++;
            for (int i = 0; ; i++) { var c = Child(sc, h, i); if (c.IsNull) break; Walk(sc, c); }
        }
    }
}
