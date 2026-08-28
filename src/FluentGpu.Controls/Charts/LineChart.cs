using FluentGpu.Dsl;

namespace FluentGpu.Controls;

/// <summary>A categorical line chart (shadcn <c>chart-line-*</c>): one stroked path per series through the category
/// slot centres, interpolated per <see cref="LineChartOptions.Curve"/>, with optional dots and value labels, over
/// the shared <see cref="CartesianChart"/> frame (axes, grid, hover crosshair + tooltip, keyboard stepping).
/// One canonical <see cref="Create"/>.</summary>
public static class LineChart
{
    public static Element Create(CartesianData data, LineChartOptions? line = null, CartesianChartOptions? options = null,
                                 CartesianChart.Style? style = null, string? key = null)
        => CartesianChart.Mount(new CartesianChart.Props(CartesianChart.Kind.Line, data, options ?? new CartesianChartOptions(),
                                                         line ?? new LineChartOptions(), null, null, style ?? CartesianChart.DefaultStyle), key);
}

/// <summary>A categorical area chart (shadcn <c>chart-area-*</c>): each series' upper curve filled down to the
/// previous series (stacked / expand) or the baseline, drawn bottom-up, with the curve line on top. One canonical
/// <see cref="Create"/>.</summary>
public static class AreaChart
{
    public static Element Create(CartesianData data, AreaChartOptions? area = null, CartesianChartOptions? options = null,
                                 CartesianChart.Style? style = null, string? key = null)
        => CartesianChart.Mount(new CartesianChart.Props(CartesianChart.Kind.Area, data, options ?? new CartesianChartOptions(),
                                                         null, area ?? new AreaChartOptions(), null, style ?? CartesianChart.DefaultStyle), key);
}

/// <summary>A categorical bar chart (shadcn <c>chart-bar-*</c>): grouped or stacked, vertical or horizontal, bars
/// growing from the zero line (negatives downward), rounded on the value end, with per-cell colour overrides, value
/// labels and an active-category highlight. One canonical <see cref="Create"/>.</summary>
public static class BarChart
{
    public static Element Create(CartesianData data, BarChartOptions? bar = null, CartesianChartOptions? options = null,
                                 CartesianChart.Style? style = null, string? key = null)
        => CartesianChart.Mount(new CartesianChart.Props(CartesianChart.Kind.Bar, data, options ?? new CartesianChartOptions(),
                                                         null, null, bar ?? new BarChartOptions(), style ?? CartesianChart.DefaultStyle), key);
}
