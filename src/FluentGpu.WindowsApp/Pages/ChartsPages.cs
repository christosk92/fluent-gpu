using System;
using FluentGpu;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Signals;
using static FluentGpu.Dsl.Ui;

// ── Charts (FluentGpu.Controls/Charts) — the kit's data-visualization primitives ──────────────────────────────────

[GalleryPage("Charts", "Charts", "Status & info", Icon = Icons.Equalizer,
    Keywords = ["chart", "graph", "line", "area", "bar", "density", "kde", "sparkline", "waveform", "tooltip", "legend"],
    Level = GalleryLevel.RealWorld,
    WaveeUse = "Playlist facts rail — Tempo card, week/years sparklines; track drawer waveform",
    WaveePath = "src/apps/Wavee/Features/Detail/LikedFactsPanel.cs")]
sealed partial class ChartsPage : Component
{
    // Seeded, deterministic sample data (the shot sweep captures this page as a stable image).
    static readonly string[] Months = ["Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec"];
    static readonly ChartSeries[] TwoSeries = ChartPalette.Series([("desktop", "Desktop"), ("mobile", "Mobile")]);
    static readonly float[][] Traffic =
    [
        [186f, 305f, 237f, 73f, 209f, 214f, 260f, 190f, 240f, 300f, 280f, 320f],
        [80f, 200f, 120f, 190f, 130f, 140f, 170f, 110f, 150f, 210f, 160f, 230f],
    ];
    static readonly CartesianData TrafficData = new(Months, TwoSeries, Traffic);
    static readonly Signal<int> _shared = new(-1);
    static readonly Signal<int> _curve = new(3);
    static readonly Signal<bool> _showMobile = new(true);
    static readonly FloatSignal _progress = new(0.35f);
    static readonly Signal<int> _litBand = new(-1);

    public override Element Render() => GalleryPage.Shell("Charts",
        "Data-visualization primitives: a shared Cartesian frame (axes, dashed grid, hover crosshair + tooltip, keyboard stepping) under Line, Area and Bar families, plus three distribution primitives — DensityPlot, SparkBars and Waveform — the Wavee facts rail is built from.",
        ExampleCard.Show(LineCurvesSample),
        ExampleCard.Show(AreaStackedSample),
        ExampleCard.Show(BarGroupedSample),
        ExampleCard.Show(BarHorizontalSample),
        ExampleCard.Show(TooltipVariantsSample),
        ExampleCard.Show(DensitySample),
        ExampleCard.Show(SparkSample),
        ExampleCard.Show(WaveformStripSample),
        ExampleCard.Show(StatTilesSample));

    [Sample("Line — natural / monotone / step / linear")]
    static Element LineCurves() => VStack(12,
        Segmented.Create([new SegmentedItem("Linear"), new SegmentedItem("Step"), new SegmentedItem("Monotone"), new SegmentedItem("Natural")], _curve),
        LineChart.Create(TrafficData,
            new LineChartOptions { Curve = (ChartCurve)_curve.Value, Dots = ChartDotMode.Active },
            new CartesianChartOptions { Width = 560f, Height = 240f, YAxis = new ChartAxisOptions { Show = true, ValueFormatter = v => v >= 1000f ? $"{v / 1000f:0.#}k" : v.ToString("0") } }),
        ChartLegend.Create(TwoSeries));

    [Sample("Area — stacked, legend toggles filter the data (colour follows the entity)")]
    static Element AreaStacked()
    {
        bool mobile = _showMobile.Value;
        var data = mobile ? TrafficData : new CartesianData(Months, [TwoSeries[0]], [Traffic[0]]);
        return VStack(12,
            AreaChart.Create(data, new AreaChartOptions { Stacking = ChartStacking.Stacked },
                new CartesianChartOptions { Width = 560f, Height = 220f, ActiveIndex = _shared }),
            ChartLegend.Create(TwoSeries, isActive: k => k != "mobile" || mobile, toggleSeries: k => { if (k == "mobile") _showMobile.Value = !_showMobile.Value; }));
    }

    [Sample("Bar — grouped, negative cells recoloured, value labels, active-slot wash")]
    static Element BarGrouped()
    {
        float[][] delta = [[12f, 30f, -8f, 24f, -15f, 18f, 9f, -4f]];
        var data = new CartesianData(Months[..8], [new ChartSeries("delta", "Change", Tok.AccentDefault)], delta);
        return BarChart.Create(data,
            new BarChartOptions { Labels = true, CellColor = (s, i, v) => v < 0f ? Tok.SystemFillCritical : null },
            new CartesianChartOptions { Width = 560f, Height = 220f, Tooltip = new ChartTooltipOptions { Indicator = ChartTooltipIndicator.Line } });
    }

    [Sample("Bar — horizontal, stacked, category axis on the left")]
    static Element BarHorizontal()
    {
        string[] browsers = ["Chrome", "Safari", "Firefox", "Edge", "Other"];
        var series = ChartPalette.Series([("visitors", "Visitors"), ("returning", "Returning")]);
        float[][] v = [[275f, 200f, 187f, 173f, 90f], [120f, 80f, 60f, 50f, 20f]];
        return BarChart.Create(new CartesianData(browsers, series, v),
            new BarChartOptions { Orientation = ChartOrientation.Horizontal, Stacking = ChartStacking.Stacked },
            new CartesianChartOptions { Width = 560f, Height = 200f, XAxis = new ChartAxisOptions { Show = false }, YAxis = new ChartAxisOptions { Show = true, Width = 64f } });
    }

    [Sample("Tooltip — dot / line / dashed indicators, hidden label, formatter + footer total")]
    static Element TooltipVariants() => Wrap(16,
        ChartTooltip.Content(TrafficData, 3, new ChartTooltipOptions()),
        ChartTooltip.Content(TrafficData, 3, new ChartTooltipOptions { Indicator = ChartTooltipIndicator.Line }),
        ChartTooltip.Content(TrafficData, 3, new ChartTooltipOptions { Indicator = ChartTooltipIndicator.Dashed, HideLabel = true }),
        ChartTooltip.Content(TrafficData, 3, new ChartTooltipOptions
        {
            LabelFormatter = i => Months[i] + " 2024",
            ValueFormatter = v => v.ToString("N0") + " visitors",
            Footer = (d, i) => new BoxEl
            {
                Direction = 0, Gap = 8f, Padding = new Edges4(0f, 4f, 0f, 0f),
                Children =
                [
                    new TextEl("Total") { Size = 12f, Color = Tok.TextSecondary, Grow = 1f },
                    new TextEl((d.ValueAt(0, i) + d.ValueAt(1, i)).ToString("N0")) { Size = 12f, Weight = 600, Color = Tok.TextPrimary },
                ],
            },
        }));

    [Sample("DensityPlot — KDE ridge, rug of values coloured by key, marker, clickable bands (lenses)")]
    static Element Density()
    {
        var values = new float[60];
        var colors = new uint[60];
        for (int i = 0; i < 60; i++)
        {
            values[i] = i < 26 ? 96f + (i * 7) % 13 : i < 38 ? 126f + (i * 5) % 7 : 160f + (i * 11) % 14;
            colors[i] = i % 3 == 0 ? 0xFF56D9F8u : i % 3 == 1 ? 0xFFFF80B4u : 0xFF05ECCBu;
        }
        int lit = _litBand.Value;
        var bands = new PlotBand[]
        {
            new(60f, 90f, "under 90 bpm · Slow · 0 tracks", lit == 0, () => _litBand.Value = lit == 0 ? -1 : 0),
            new(90f, 120f, "90 – 119 bpm · Medium · 26 tracks", lit == 1, () => _litBand.Value = lit == 1 ? -1 : 1),
            new(120f, 140f, "120 – 139 bpm · Fast · 12 tracks", lit == 2, () => _litBand.Value = lit == 2 ? -1 : 2),
            new(140f, 200f, "140 bpm and up · Very fast · 22 tracks", lit == 3, () => _litBand.Value = lit == 3 ? -1 : 3),
        };
        var model = new DensityPlotModel(values, 60f, 200f)
        {
            Bands = bands, Marker = 128f, Hairlines = new float[] { 90f, 120f, 140f }, ValueColors = colors,
            Ticks = new PlotTick[] { new(80f, "80"), new(100f, "100"), new(120f, "120"), new(140f, "140"), new(160f, "160"), new(180f, "180") },
        };
        return HStack(12,
            VStack(4,
                new TextEl("128") { Size = 28f, Weight = 600, Color = Tok.TextPrimary },
                new TextEl("bpm · median") { Size = 12f, Color = Tok.TextTertiary }) with { Width = 72f, Shrink = 0f },
            new BoxEl { Width = 220f, Children = [DensityPlot.Create(model)] });
    }

    [Sample("SparkBars — the stat-card strip (full-height hit columns, peak in accent, empty buckets keep a floor)")]
    static Element Spark()
    {
        float[] weeks = [3f, 5f, 2f, 7f, 4f, 0f, 6f, 3f, 8f, 5f, 7f, 12f];
        var bars = new SparkBar[12];
        int lit = _shared.Value;
        for (int i = 0; i < 12; i++)
        {
            int k = i;
            bars[i] = new SparkBar(weeks[i], $"week {i + 1} · {weeks[i]:0} songs", Lit: lit == i, Accent: i == 11, OnClick: () => _shared.Value = lit == k ? -1 : k);
        }
        return HStack(12,
            VStack(4,
                new TextEl("+12") { Size = 28f, Weight = 600, Color = Tok.TextPrimary },
                new TextEl("songs liked") { Size = 12f, Color = Tok.TextTertiary }) with { Shrink = 0f },
            new BoxEl { Width = 220f, Height = 38f, Children = [SparkBars.Create(new SparkBarsModel(bars))] });
    }

    [Sample("Waveform — mirrored bars; the played portion is a bound clip (no re-render per tick)")]
    static Element WaveformStrip()
    {
        var peaks = new float[400];
        for (int i = 0; i < peaks.Length; i++) peaks[i] = 0.15f + 0.85f * MathF.Abs(MathF.Sin(i * 0.07f) * MathF.Cos(i * 0.013f));
        return VStack(12,
            Waveform.Create(new WaveformModel(peaks, 96), _progress),
            Slider.Create(_progress, v => _progress.Value = v, length: 300f));
    }

    [Sample("Stat tiles + interactive area (the shadcn dashboard-01 shape)")]
    static Element StatTiles()
    {
        static Element Tile(string label, string value, string delta, string footer) => new BoxEl
        {
            Direction = 1, Gap = 6f, Grow = 1f, Basis = 0f, Padding = Edges4.All(14f),
            Corners = CornerRadius4.All(Radii.Card), Fill = Tok.FillCardDefault, BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault,
            Children =
            [
                new TextEl(label) { Size = 12f, Color = Tok.TextSecondary },
                HStack(8, new TextEl(value) { Size = 24f, Weight = 600, Color = Tok.TextPrimary, Grow = 1f }, InfoBadge.Create(InfoBadgeKind.Icon, glyph: Icons.ChevronUp)),
                new TextEl(delta) { Size = 12f, Weight = 600, Color = Tok.TextPrimary },
                new TextEl(footer) { Size = 12f, Color = Tok.TextTertiary },
            ],
        };
        return VStack(12,
            HStack(12,
                Tile("Total revenue", "$1,250.00", "Trending up this month", "Visitors for the last 6 months"),
                Tile("New customers", "1,234", "Down 20% this period", "Acquisition needs attention"),
                Tile("Active accounts", "45,678", "Strong user retention", "Engagement exceeds targets")),
            AreaChart.Create(TrafficData, new AreaChartOptions { Stacking = ChartStacking.Stacked, Curve = ChartCurve.Natural },
                new CartesianChartOptions { Width = 560f, Height = 200f, ActiveIndex = _shared }));
    }
}
