using System;
using System.Collections.Generic;
using System.Globalization;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.ScrollLab.Lab;
using FluentGpu.Scroll.Diag.Analysis;
using static FluentGpu.Dsl.Ui;

namespace FluentGpu.ScrollLab.Analysis;

/// <summary>
/// The Analysis page (scroll-lab plan §3 Analysis, phase 1): one verdict tile per metric (value vs baseline, R/A/G,
/// the F8 markers near its worst sample) and ONE offset-vs-time LineChart whose own marks/bands
/// (<see cref="CartesianChartOptions.Marks"/>/<see cref="CartesianChartOptions.Bands"/>) show the wheel notches and the
/// F8 "felt wrong" presses.
/// <code>
/// session · surface · time                                             [Open folder]
/// 10.2 s · 1 240 poses · 38 notches · T_r 6.94 ms · vp 12 · 0 lost rows
/// ┌ First-motion ┐ ┌ Notch→pose ┐ ┌ Irregularity ┐ ┌ Repeats ┐ ┌ Curve ┐ …   (tiles, wrapping)
/// │ 18.4 ms  G   │ │ 13.1 ms  G │ │ 0.08     G   │ │ 0 /10s G│ │ 2.1% G│
/// └──────────────┘ └────────────┘ └──────────────┘ └─────────┘ └───────┘
/// Offset vs time ───────────────────────────────────────────────────────────
/// ░F8░   |  | ||   |||  |    ╱‾‾‾‾╲___╱‾‾   (F8 bands, notch marks, the posed offset — one chart)
/// </code>
/// </summary>
public sealed class AnalysisPage : Component
{
    public override Element Render()
    {
        var result = LabState.Analysis.Value;
        if (result is null)
        {
            return new BoxEl
            {
                Direction = 1,
                Grow = 1f,
                Gap = 8f,
                Padding = new Edges4(24f, 16f, 24f, 16f),
                Children =
                [
                    new TextEl("No analysis yet") { Size = 16f, Weight = 600 },
                    new TextEl("Record a session (F10 on the Surface page) or pick one on the Record page.") { Size = 13f, Color = Tok.TextSecondary },
                ],
            };
        }

        var s = result.Series;
        int notches = 0;
        foreach (var input in s.Inputs) if (input.IsNotch) notches++;
        var ci = CultureInfo.InvariantCulture;
        string summary = result.DurationS.ToString("0.0", ci) + " s · " + s.Poses.Length.ToString(ci) + " poses · "
                         + notches.ToString(ci) + " notches · " + s.Presents.Length.ToString(ci) + " presents · T_r "
                         + (s.RefreshPeriodS * 1000.0).ToString("0.00", ci) + " ms · viewport " + s.Viewport.ToString(ci)
                         + " · " + s.LostRows.ToString(ci) + " lost rows";

        var tiles = new Element[result.Metrics.Count];
        for (int i = 0; i < tiles.Length; i++) tiles[i] = Tile(result.Metrics[i]);

        string key = result.Folder;
        var trace = result.Trace;
        var data = new CartesianData(trace.Categories,
            new[] { new ChartSeries("offset", "Offset (DIP)", Tok.AccentDefault) },
            new[] { trace.Offsets });

        return ScrollView(new BoxEl
        {
            Direction = 1,
            Gap = 12f,
            Padding = new Edges4(24f, 8f, 24f, 24f),
            Children =
            [
                new BoxEl
                {
                    Direction = 0,
                    Gap = 8f,
                    AlignItems = FlexAlign.Center,
                    Children =
                    [
                        new TextEl(result.Title) { Size = 16f, Weight = 600, Grow = 1f, MinWidth = 0f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
                        Controls.Button.Create("Re-analyze", () => LabState.AnalyzeFolder(result.Folder), ButtonAppearance.Subtle, ControlSize.Small),
                        Controls.Button.Create("Open folder", () => OpenFolder(result.Folder), ButtonAppearance.Standard, ControlSize.Small),
                    ],
                },
                new TextEl(summary) { Size = 12f, Color = Tok.TextSecondary },
                new BoxEl { Direction = 0, Gap = 10f, Wrap = true, Children = tiles },
                new BoxEl
                {
                    Direction = 0,
                    Gap = 16f,
                    AlignItems = FlexAlign.Center,
                    Padding = new Edges4(0f, 8f, 0f, 0f),
                    Children =
                    [
                        new TextEl("Offset vs time") { Size = 14f, Weight = 600 },
                        Legend(Tok.AccentDefault, "posed offset"),
                        Legend(TickColor, "wheel notch"),
                        Legend(BandColor, "F8 felt wrong (±250 ms)"),
                    ],
                },
                LineChart.Create(data,
                    new LineChartOptions { Curve = ChartCurve.Linear, Dots = ChartDotMode.None, StrokeWidth = 1.5f },
                    new CartesianChartOptions
                    {
                        Height = ChartHeight,
                        Tooltip = new ChartTooltipOptions { ValueFormatter = static v => v.ToString("0.0", CultureInfo.InvariantCulture) + " DIP" },
                        XAxis = new ChartAxisOptions { MinTickGap = 64f },
                        Marks = trace.NotchMarks,
                        Bands = trace.FeltBands,
                    },
                    key: "chart-" + key),
                result.PassTimeline is { } passData
                    ? (Element)new BoxEl
                    {
                        Direction = 1,
                        Gap = 8f,
                        Padding = new Edges4(0f, 12f, 0f, 0f),
                        Children =
                        [
                            new TextEl("GPU pass timeline (ms per retired frame, stacked by pass kind)") { Size = 14f, Weight = 600 },
                            BarChart.Create(passData,
                                new BarChartOptions { Stacking = ChartStacking.Stacked, GroupGap = 0.1f, BarGap = 0f, CornerRadius = 0f },
                                new CartesianChartOptions
                                {
                                    Height = 200f,
                                    Tooltip = new ChartTooltipOptions { ValueFormatter = static v => v.ToString("0.00", CultureInfo.InvariantCulture) + " ms" },
                                    XAxis = new ChartAxisOptions { MinTickGap = 64f },
                                },
                                key: "passes-" + key),
                        ],
                    }
                    : new TextEl("GPU pass timeline: turn on “GPU passes” in the HUD before recording to capture it.") { Size = 12f, Color = Tok.TextTertiary },
            ],
        }) with { Grow = 1f };
    }

    const float ChartHeight = 280f;
    internal static ColorF TickColor => Tok.TextSecondary with { A = 0.55f };
    internal static ColorF BandColor => Tok.SystemFillCaution with { A = 0.22f };

    static Element Legend(ColorF color, string label) => new BoxEl
    {
        Direction = 0,
        Gap = 6f,
        AlignItems = FlexAlign.Center,
        Children =
        [
            new BoxEl { Width = 10f, Height = 10f, Corners = CornerRadius4.All(2f), Fill = color },
            new TextEl(label) { Size = 12f, Color = Tok.TextSecondary },
        ],
    };

    static Element Tile(MetricResult m)
    {
        var ci = CultureInfo.InvariantCulture;
        (string verdict, ColorF ink) = m.Verdict switch
        {
            MetricVerdict.Green => ("G", Tok.SystemFillSuccess),
            MetricVerdict.Amber => ("A", Tok.SystemFillCaution),
            MetricVerdict.Red => ("R", Tok.SystemFillCritical),
            MetricVerdict.Info => ("info", Tok.AccentTextPrimary),
            _ => ("no data", Tok.TextTertiary),
        };
        string value = double.IsNaN(m.Value) ? "—" : m.Value.ToString(m.Unit == "ratio" ? "0.000" : "0.0", ci) + " " + m.Unit;
        string baseline = double.IsNaN(m.Baseline) ? "no baseline yet" : "baseline " + m.Baseline.ToString(m.Unit == "ratio" ? "0.00" : "0.0", ci) + " " + m.Unit;
        var children = new List<Element>
        {
            new BoxEl
            {
                Direction = 0,
                Gap = 6f,
                AlignItems = FlexAlign.Center,
                Children =
                [
                    new TextEl(m.Name) { Size = 12f, Color = Tok.TextSecondary, Grow = 1f, MinWidth = 0f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
                    new BoxEl
                    {
                        Padding = new Edges4(8f, 1f, 8f, 1f),
                        Corners = CornerRadius4.All(8f),
                        Fill = ink with { A = 0.18f },
                        Children = [new TextEl(verdict) { Size = 11f, Weight = 700, Color = ink }],
                    },
                ],
            },
            new TextEl(value) { Size = 20f, Weight = 600 },
            new TextEl(baseline) { Size = 11f, Color = Tok.TextSecondary },
        };
        if (m.MarkerHits.Length > 0)
            children.Add(new TextEl("F8 × " + m.MarkerHits.Length.ToString(ci) + " near the worst sample (" + m.WorstT.ToString("0.00", ci) + " s)")
            {
                Size = 11f, Color = Tok.SystemFillCaution, Wrap = TextWrap.Wrap,
            });
        if (m.Detail.Length > 0)
            children.Add(new TextEl(m.Detail) { Size = 11f, Color = Tok.TextTertiary, Wrap = TextWrap.Wrap, MaxLines = 3 });
        return new BoxEl
        {
            Direction = 1,
            Width = 250f,
            Gap = 4f,
            Padding = new Edges4(12f, 10f, 12f, 10f),
            Corners = CornerRadius4.All(8f),
            Fill = Tok.FillCardDefault,
            BorderColor = m.Verdict is MetricVerdict.Red ? Tok.SystemFillCritical with { A = 0.6f } : Tok.StrokeCardDefault,
            BorderWidth = 1f,
            Children = children.ToArray(),
        };
    }

    static void OpenFolder(string dir)
    {
        try
        {
            if (System.IO.Directory.Exists(dir))
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", "\"" + dir + "\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            LabState.Status.Value = "Could not open " + dir + ": " + ex.Message;
        }
    }
}
