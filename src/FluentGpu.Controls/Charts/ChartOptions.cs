using System;
using FluentGpu.Dsl;
using FluentGpu.Signals;

namespace FluentGpu.Controls;

/// <summary>The swatch a tooltip row leads with (shadcn <c>ChartTooltipContent.indicator</c>): a filled square, a
/// full-height bar, or a dashed outline.</summary>
public enum ChartTooltipIndicator : byte { Dot = 0, Line = 1, Dashed = 2 }

/// <summary>Bar direction: value up (<see cref="Vertical"/>) or value right (<see cref="Horizontal"/>, Recharts
/// <c>layout="vertical"</c>).</summary>
public enum ChartOrientation : byte { Vertical = 0, Horizontal = 1 }

/// <summary>Which line-chart points get a marker.</summary>
public enum ChartDotMode : byte { None = 0, All = 1, Active = 2 }

/// <summary>Tooltip options (the port of shadcn's <c>ChartTooltipContent</c> props).</summary>
public sealed record ChartTooltipOptions
{
    public ChartTooltipIndicator Indicator { get; init; } = ChartTooltipIndicator.Dot;
    public bool HideLabel { get; init; }
    public bool HideIndicator { get; init; }
    /// <summary>Category index → the tooltip header (default: the category text).</summary>
    public Func<int, string>? LabelFormatter { get; init; }
    /// <summary>Value → text (default: <c>N0</c> in the current culture, tabular digits).</summary>
    public Func<float, string>? ValueFormatter { get; init; }
    /// <summary>A resting tooltip at this index until the pointer first moves (<c>defaultIndex</c>).</summary>
    public int? DefaultIndex { get; init; }
    /// <summary>An extra row under the series rows (a computed Total, a unit note). Null = none.</summary>
    public Func<CartesianData, int, Element?>? Footer { get; init; }
}

/// <summary>One axis of a Cartesian chart.</summary>
public sealed record ChartAxisOptions
{
    public bool Show { get; init; } = true;
    /// <summary>Target tick count for a VALUE axis (nice 1/2/5·10^k steps land near it).</summary>
    public int TickCount { get; init; } = 5;
    /// <summary>Minimum clear gap between category captions before ticks are thinned (Recharts <c>minTickGap</c>).</summary>
    public float MinTickGap { get; init; } = 32f;
    /// <summary>Category text → caption (the <c>value.slice(0, 3)</c> idiom).</summary>
    public Func<string, string>? CategoryFormatter { get; init; }
    /// <summary>Value → caption ("12k").</summary>
    public Func<float, string>? ValueFormatter { get; init; }
    /// <summary>Lane width of a vertical value axis (Recharts <c>width</c>).</summary>
    public float Width { get; init; } = 44f;
    /// <summary>Thinning keeps the first and last category captions (<c>interval="preserveStartEnd"</c>).</summary>
    public bool PreserveStartEnd { get; init; } = true;
}

/// <summary>Frame options shared by <see cref="LineChart"/>, <see cref="AreaChart"/> and <see cref="BarChart"/>.</summary>
public sealed record CartesianChartOptions
{
    /// <summary>Finite = pinned; NaN = stretch to the parent and measure. With only a width known the chart keeps a
    /// 16:9 aspect (the shadcn <c>aspect-video</c> container).</summary>
    public float Width { get; init; } = float.NaN;
    public float Height { get; init; } = float.NaN;
    /// <summary>The bottom axis (categories for vertical charts, values for horizontal bars).</summary>
    public ChartAxisOptions XAxis { get; init; } = new();
    /// <summary>The left axis (values for vertical charts, categories for horizontal bars). Hidden by default, as in
    /// every shadcn example — the tooltip carries the exact numbers.</summary>
    public ChartAxisOptions YAxis { get; init; } = new() { Show = false };
    /// <summary>Dashed value-grid lines at the value ticks.</summary>
    public bool Grid { get; init; } = true;
    /// <summary>The hovered / keyboard-selected category index (−1 = none). Pass your own signal when the active
    /// state must persist or drive other UI (shadcn: keep persistent active shapes in your own state); null = the
    /// chart owns one.</summary>
    public Signal<int>? ActiveIndex { get; init; }
    /// <summary>Tooltip on hover/keyboard; null = none. The hover layer ships by default — an on-screen chart is
    /// interactive.</summary>
    public ChartTooltipOptions? Tooltip { get; init; } = new();
    /// <summary>The plot is focusable and ←/→/Home/End step the active index, Esc clears it (<c>accessibilityLayer</c>).</summary>
    public bool AccessibilityLayer { get; init; } = true;
    /// <summary>Value-axis overrides; null = the data extent, niced.</summary>
    public float? ValueMin { get; init; }
    public float? ValueMax { get; init; }
    /// <summary>Category-range bands across the plot (Recharts <c>ReferenceArea x1/x2</c>), drawn over the grid and
    /// under the series. Empty = none.</summary>
    public ReadOnlyMemory<ChartBand> Bands { get; init; }
    /// <summary>Marks across the plot at a category position (Recharts <c>ReferenceLine x</c>), drawn over the bands
    /// and under the series. Empty = none.</summary>
    public ReadOnlyMemory<ChartMark> Marks { get; init; }
}

/// <summary>A mark across the plot at category position <paramref name="At"/>, in CATEGORY units: 0 is the first
/// category's slot centre, i the i-th, and fractional positions interpolate between centres (so a uniformly sampled
/// time axis maps t to <c>t / Δ − ½</c>). Default <paramref name="Color"/> (A = 0) = <c>CartesianChart.Style.MarkInk</c>;
/// <paramref name="Label"/> is captioned at the top of the mark.</summary>
public readonly record struct ChartMark(float At, FluentGpu.Foundation.ColorF Color = default, string? Label = null);

/// <summary>A band across the plot from category position <paramref name="From"/> to <paramref name="To"/> (the same
/// units as <see cref="ChartMark.At"/>; order-free, clamped to the plot). Default <paramref name="Color"/> (A = 0) =
/// <c>CartesianChart.Style.BandFill</c>; <paramref name="Label"/> is captioned at the band's leading top corner.</summary>
public readonly record struct ChartBand(float From, float To, FluentGpu.Foundation.ColorF Color = default, string? Label = null);

public sealed record LineChartOptions
{
    public ChartCurve Curve { get; init; } = ChartCurve.Natural;
    public float StrokeWidth { get; init; } = 2f;
    public ChartDotMode Dots { get; init; } = ChartDotMode.Active;
    public float DotRadius { get; init; } = 4f;
    /// <summary>Value captions above every point.</summary>
    public bool Labels { get; init; }
}

public sealed record AreaChartOptions
{
    public ChartCurve Curve { get; init; } = ChartCurve.Natural;
    public ChartStacking Stacking { get; init; } = ChartStacking.None;
    /// <summary>Area fill alpha. 0.32 is shadcn's net top alpha (gradient stop 0.8 × fillOpacity 0.4); a vertical
    /// gradient fill is a roadmap item (a path has one flat fill today).</summary>
    public float FillAlpha { get; init; } = 0.32f;
    public float StrokeWidth { get; init; } = 2f;
    /// <summary>Draw the upper edge as a line over the fill.</summary>
    public bool Line { get; init; } = true;
}

public sealed record BarChartOptions
{
    public ChartOrientation Orientation { get; init; } = ChartOrientation.Vertical;
    /// <summary><see cref="ChartStacking.None"/> = grouped side by side.</summary>
    public ChartStacking Stacking { get; init; } = ChartStacking.None;
    public float CornerRadius { get; init; } = 4f;
    /// <summary>Empty share of a category slot (both sides together).</summary>
    public float GroupGap { get; init; } = 0.2f;
    /// <summary>Gap between grouped bars, and the surface gap between stacked segments (the dataviz 2 px spacer).</summary>
    public float BarGap { get; init; } = 2f;
    /// <summary>Value captions above the bars (single-series or stacked totals).</summary>
    public bool Labels { get; init; }
    /// <summary>Per-cell colour override <c>(series, point, value) → colour</c>; null keeps the series colour
    /// (the negative-bar idiom: <c>v &lt; 0 ? second : first</c>).</summary>
    public Func<int, int, float, FluentGpu.Foundation.ColorF?>? CellColor { get; init; }
    /// <summary>The active category's bars take a subtle wash and a dashed rim.</summary>
    public bool HighlightActive { get; init; } = true;
}
