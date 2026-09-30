using System;
using System.Collections.Generic;
using System.Globalization;
using FluentGpu.Animation;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Render;
using FluentGpu.Signals;

namespace FluentGpu.Controls;

/// <summary>
/// The frame every Cartesian chart in the kit shares (<see cref="LineChart"/>, <see cref="AreaChart"/>,
/// <see cref="BarChart"/>): the measured plot rectangle, the value/category axes and dashed grid, the series layers,
/// and the hover layer that resolves the pointer to a category index. The active-index state is a signal; only the
/// small <c>ActiveOverlay</c> component (crosshair / slot wash, active markers, tooltip) subscribes to it, so a hover
/// never re-renders the series geometry, which is memoised on (data, plot size, options) and minted as
/// <c>PathData</c> exactly once per key.
///
/// <para>Sizing follows the kit rule: finite <see cref="CartesianChartOptions.Width"/>/<c>Height</c> pin; NaN
/// stretches and measures (<c>UseMeasuredBounds</c>), with a 16:9 aspect when only the width is known.</para>
/// </summary>
public static class CartesianChart
{
    public sealed record Style
    {
        public ColorF GridInk { get; init; }
        public float GridDashOn { get; init; } = 3f;
        public float GridDashOff { get; init; } = 6f;
        public ColorF AxisInk { get; init; }
        public float AxisFontSize { get; init; } = 11f;
        public float XAxisHeight { get; init; } = 18f;
        public ColorF LabelInk { get; init; }
        public float LabelFontSize { get; init; } = 10f;
        public ColorF CrosshairInk { get; init; }
        /// <summary>The wash behind the active category (bars) — an accent-subtle band.</summary>
        public ColorF ActiveWash { get; init; }
        public ColorF ActiveRim { get; init; }
        /// <summary>Headroom above the tallest value so a line/dot never kisses the top edge.</summary>
        public float PlotPaddingTop { get; init; } = 8f;
        public float TooltipOffset { get; init; } = 12f;
        public ChartTooltip.Style? Tooltip { get; init; }
        /// <summary>Default ink of a <see cref="ChartMark"/> whose colour is unset.</summary>
        public ColorF MarkInk { get; init; }
        /// <summary>Default fill of a <see cref="ChartBand"/> whose colour is unset.</summary>
        public ColorF BandFill { get; init; }
    }

    public static Style? StyleOverride;
    public static Style DefaultStyle => StyleOverride ?? new Style
    {
        GridInk = Tok.StrokeCardDefault,
        AxisInk = Tok.TextTertiary,
        LabelInk = Tok.TextSecondary,
        CrosshairInk = Tok.StrokeControlStrongDefault,
        ActiveWash = Tok.AccentSubtle,
        ActiveRim = Tok.AccentTextPrimary,
        MarkInk = Tok.TextTertiary,
        BandFill = Tok.AccentSubtle,
    };

    internal enum Kind : byte { Line, Area, Bar }

    /// <summary>Re-pushed props: the chart re-renders in place when any of these change by value.</summary>
    internal sealed record Props(Kind Kind, CartesianData Data, CartesianChartOptions Options,
                                 LineChartOptions? Line, AreaChartOptions? Area, BarChartOptions? Bar, Style Style);

    internal static Element Mount(Props props, string? key)
        => Embed.Comp(props, static () => new CartesianChartComponent()) with { Key = key };
}

/// <summary>The resolved plot frame — every number the layers and the overlay need, computed once per render.</summary>
internal readonly record struct PlotFrame(
    float PlotW, float PlotH, int Points, float Min, float Max, bool Horizontal, float TopPad)
{
    /// <summary>Value → position along the value axis (0 at Min). Vertical charts flip it to a y from the top.</summary>
    public float ValueT(float v) => Max > Min ? (v - Min) / (Max - Min) : 0f;
    public float ValueLength => Horizontal ? PlotW : PlotH - TopPad;
    public float ValuePx(float v) => ValueT(v) * ValueLength;
    /// <summary>y (from the plot top) for a value on a vertical chart.</summary>
    public float Y(float v) => PlotH - ValuePx(v);
    public float Slot => Points > 0 ? (Horizontal ? PlotH : PlotW) / Points : 0f;
    public float Centre(int i) => Slot * (i + 0.5f);
    /// <summary>A fractional CATEGORY position (0 = the first slot centre) → px along the category axis.</summary>
    public float At(float category) => Slot * (category + 0.5f);
    public float ZeroPx => ValuePx(Math.Clamp(0f, Min, Max));
}

internal sealed class CartesianChartComponent : Component
{
    sealed record Geometry(PathData? Grid, PathData?[] Lines, PathData?[] Fills, PathData?[] Dots, float[][]? Cumulative,
                           float[] Ticks, int TickCount);

    /// <summary>The options' bands and marks, one path per distinct colour (memoised like <see cref="Geometry"/>).</summary>
    sealed record Annotations(PathData[] BandPaths, ColorF[] BandColors, PathData[] MarkPaths, ColorF[] MarkColors,
                              (float At, string Label)[] Labels);

    public override Element Render()
    {
        var p = UseProps<CartesianChart.Props>();
        var o = p.Options;
        var st = p.Style;
        var data = p.Data;
        int n = data.PointCount;
        int seriesCount = data.Series.Count;

        // ── size ─────────────────────────────────────────────────────────────────────────────────────────────────
        var bounds = UseMeasuredBounds();
        bool pinW = float.IsFinite(o.Width) && o.Width > 0f, pinH = float.IsFinite(o.Height) && o.Height > 0f;
        float W = pinW ? o.Width : bounds.Value.W;
        float H = pinH ? o.Height : pinW ? o.Width * 9f / 16f : bounds.Value.H;
        if (!pinH && !pinW && H <= 0f && W > 0f) H = W * 9f / 16f;

        bool horizontal = p.Kind == CartesianChart.Kind.Bar && p.Bar?.Orientation == ChartOrientation.Horizontal;
        bool xShow = o.XAxis.Show, yShow = o.YAxis.Show;
        float yLane = yShow ? o.YAxis.Width : 0f;
        float xLane = xShow ? st.XAxisHeight : 0f;
        float plotW = MathF.Max(0f, W - yLane), plotH = MathF.Max(0f, H - xLane);
        bool ready = plotW > 8f && plotH > 8f && n > 0;

        // ── domain ───────────────────────────────────────────────────────────────────────────────────────────────
        var stacking = p.Kind == CartesianChart.Kind.Area ? p.Area?.Stacking ?? ChartStacking.None
                     : p.Kind == CartesianChart.Kind.Bar ? p.Bar?.Stacking ?? ChartStacking.None : ChartStacking.None;
        var (min, max) = ChartMath.Extent(data.Values, stacking, includeZero: p.Kind == CartesianChart.Kind.Bar);
        if (o.ValueMin is { } vmin) min = vmin;
        if (o.ValueMax is { } vmax) max = vmax;
        var valueAxis = horizontal ? o.XAxis : o.YAxis;
        Span<float> tickBuf = stackalloc float[24];
        int tickCount = ChartMath.NiceTicks(min, max, Math.Clamp(valueAxis.TickCount, 2, 12), tickBuf, out float niceMin, out float niceMax, out _);
        if (o.ValueMin is not null) niceMin = min;
        if (o.ValueMax is not null) niceMax = max;
        bool labels = p.Kind == CartesianChart.Kind.Bar ? p.Bar?.Labels == true : p.Kind == CartesianChart.Kind.Line && p.Line?.Labels == true;
        float topPad = horizontal ? 0f : labels ? st.LabelFontSize + 6f : st.PlotPaddingTop;
        var frame = new PlotFrame(plotW, plotH, n, niceMin, niceMax, horizontal, topPad);

        // ── active index (never read here — only the overlay subscribes) ─────────────────────────────────────────
        var own = UseSignal(-1);
        var active = o.ActiveIndex ?? own;
        if (o.Tooltip?.DefaultIndex is { } di)
            UseEffect(() => { if (active.Peek() < 0) active.Value = Math.Clamp(di, 0, Math.Max(0, n - 1)); }, DepKey.Empty);

        // ── geometry (memoised: same key ⇒ same PathData ⇒ no re-tessellation) ───────────────────────────────────
        var ticksCopy = tickBuf.Slice(0, tickCount).ToArray();
        var geo = UseMemo(() => ready ? Build(p, frame, ticksCopy) : null,
            DepKey.Combine(
                DepKey.Combine(DepKey.FromRef(data), DepKey.From(plotW, plotH, niceMin, niceMax)),
                DepKey.Combine(DepKey.FromRef(p.Line, p.Area), DepKey.From(p.Bar is null ? 0 : p.Bar.GetHashCode(), (int)p.Kind))));

        var notes = UseMemo(() => ready ? BuildAnnotations(o, frame, st) : null,
            DepKey.Combine(DepKey.FromRef(o, st), DepKey.From(plotW, plotH, n, horizontal ? 1f : 0f)));

        // ── layers ───────────────────────────────────────────────────────────────────────────────────────────────
        var layers = new List<Element>(6);
        if (geo is not null)
        {
            if (o.Grid && geo.Grid is not null)
                layers.Add(PathLayer(geo.Grid, plotW, plotH, default, st.GridInk, new StrokeStyle(1f, LineCap.Butt, LineJoin.Miter, 4f, st.GridDashOn, st.GridDashOff)));
            if (notes is not null)
            {
                for (int b = 0; b < notes.BandPaths.Length; b++) layers.Add(PathLayer(notes.BandPaths[b], plotW, plotH, notes.BandColors[b], default, default));
                for (int m = 0; m < notes.MarkPaths.Length; m++) layers.Add(PathLayer(notes.MarkPaths[m], plotW, plotH, default, notes.MarkColors[m], new StrokeStyle(1f)));
                if (notes.Labels.Length > 0) layers.Add(AnnotationLabels(notes, frame, st));
            }
            if (p.Kind == CartesianChart.Kind.Bar) layers.Add(Bars(p, frame, geo, st));
            else
            {
                for (int s = 0; s < seriesCount; s++)
                {
                    var color = data.Series[s].Color;
                    if (geo.Fills[s] is { } fill && p.Area is { } ao)
                        layers.Add(PathLayer(fill, plotW, plotH, color with { A = ao.FillAlpha }, default, default));
                }
                for (int s = 0; s < seriesCount; s++)
                {
                    var color = data.Series[s].Color;
                    float sw = p.Kind == CartesianChart.Kind.Line ? p.Line?.StrokeWidth ?? 2f : p.Area?.Line == false ? 0f : p.Area?.StrokeWidth ?? 2f;
                    if (sw > 0f && geo.Lines[s] is { } line)
                        layers.Add(PathLayer(line, plotW, plotH, default, color, new StrokeStyle(sw, LineCap.Round, LineJoin.Round)));
                    if (geo.Dots[s] is { } dots) layers.Add(PathLayer(dots, plotW, plotH, color, Tok.FillSolidBase, new StrokeStyle(2f)));
                }
                if (labels && p.Kind == CartesianChart.Kind.Line) layers.Add(LineLabels(p, frame, st));
            }
            layers.Add(ActiveOverlay.Mount(new ActiveOverlay.Props(active, data, o, p.Kind, p.Bar, frame, geo.Cumulative, st)));
            if (o.Tooltip is not null || o.AccessibilityLayer) layers.Add(HoverLayer(active, frame));
        }

        var plot = new BoxEl
        {
            Key = "plot", ZStack = true, Width = plotW, Height = plotH, MinWidth = 0f, ClipToBounds = false,
            Focusable = o.AccessibilityLayer, FocusVisualMargin = new Edges4(2f, 2f, 2f, 2f),
            OnKeyDown = o.AccessibilityLayer ? e => Step(e, active, n) : null,
            Children = layers.ToArray(),
        };

        var row = new List<Element>(2);
        if (yShow) row.Add(horizontal ? CategoryAxisVertical(data, o.YAxis, frame, st, yLane) : ValueAxisVertical(geo, frame, o.YAxis, st, yLane));
        row.Add(plot);

        var kids = new List<Element>(2) { new BoxEl { Key = "plot-row", Direction = 0, Height = plotH, MinWidth = 0f, Children = row.ToArray() } };
        if (xShow) kids.Add(horizontal ? ValueAxisHorizontal(geo, frame, o.XAxis, st, yLane, xLane) : CategoryAxisHorizontal(data, o.XAxis, frame, st, yLane, xLane));

        return new BoxEl
        {
            Key = "chart", Direction = 1, MinWidth = 0f, Role = AutomationRole.None,
            Width = pinW ? o.Width : float.NaN, Height = pinH ? o.Height : pinW ? H : float.NaN,
            Grow = pinW ? 0f : 1f, AlignSelf = pinW ? FlexAlign.Auto : FlexAlign.Stretch,
            AspectRatio = !pinW && !pinH ? 16f / 9f : float.NaN,
            Children = kids.ToArray(),
        };
    }

    // ── keyboard (accessibilityLayer) ─────────────────────────────────────────────────────────────────────────────

    static void Step(KeyEventArgs e, Signal<int> active, int n)
    {
        if (n <= 0) return;
        int cur = active.Peek();
        int next = e.KeyCode switch
        {
            Keys.Right => cur < 0 ? 0 : Math.Min(n - 1, cur + 1),
            Keys.Left => cur < 0 ? n - 1 : Math.Max(0, cur - 1),
            Keys.Home => 0,
            Keys.End => n - 1,
            Keys.Escape => -1,
            _ => int.MinValue,
        };
        if (next == int.MinValue) return;
        active.Value = next;
        e.Handled = true;
    }

    // ── hover layer ───────────────────────────────────────────────────────────────────────────────────────────────

    static Element HoverLayer(Signal<int> active, PlotFrame f) => new BoxEl
    {
        Key = "hover", Width = f.PlotW, Height = f.PlotH,
        OnHoverMove = pt => { int i = ChartMath.NearestIndex(f.Horizontal ? pt.Y : pt.X, 0f, f.Slot, f.Points); if (active.Peek() != i) active.Value = i; },
        OnPointerExit = () => { if (active.Peek() >= 0) active.Value = -1; },
    };

    // ── layers ────────────────────────────────────────────────────────────────────────────────────────────────────

    static Element PathLayer(PathData geometry, float w, float h, ColorF fill, ColorF stroke, StrokeStyle style) => new BoxEl
    {
        Width = w, Height = h, HitTestVisible = false,
        Children = [new PathEl { Geometry = geometry, Width = w, Height = h, Fill = fill, StrokeColor = stroke, Stroke = style }],
    };

    /// <summary>Bars are plain rounded rects laid out by flex: one slot per category; inside, a positive half above
    /// the zero line and a negative half below it, each holding the grouped bars side by side or the stacked segments
    /// end to end with the 2 px surface gap.</summary>
    static Element Bars(CartesianChart.Props p, PlotFrame f, Geometry geo, CartesianChart.Style st)
    {
        var data = p.Data;
        var bo = p.Bar ?? new BarChartOptions();
        int n = f.Points, sc = data.Series.Count;
        bool stacked = bo.Stacking != ChartStacking.None;
        var values = stacked ? geo.Cumulative! : data.Values;
        float zero = f.ZeroPx;                       // px from the value-axis origin (bottom / left)
        float posLen = f.ValueLength - zero, negLen = zero;
        var slots = new Element[n];
        float pad = MathF.Max(0f, f.Slot * bo.GroupGap * 0.5f);
        var culture = CultureInfo.CurrentCulture;

        for (int i = 0; i < n; i++)
        {
            var pos = new List<Element>(sc);
            var neg = new List<Element>(sc);
            float prevPos = 0f, prevNeg = 0f;
            float total = 0f;
            for (int s = 0; s < sc; s++)
            {
                float v = data.ValueAt(s, i);
                if (float.IsNaN(v)) { if (!stacked) { pos.Add(Ghost()); neg.Add(Ghost()); } continue; }
                total += v;
                float edge = stacked ? values[s][i] : v;
                float len;
                bool positive = v >= 0f;
                if (stacked) { len = positive ? f.ValuePx(edge) - f.ValuePx(0f) - (prevPos) : (f.ValuePx(0f) - f.ValuePx(edge)) - prevNeg; if (positive) prevPos += len; else prevNeg += len; }
                else len = MathF.Abs(f.ValuePx(v) - f.ValuePx(0f));
                len = MathF.Max(0f, len);
                var color = bo.CellColor?.Invoke(s, i, v) ?? data.Series[s].Color;
                var bar = Bar(len, color, bo, f.Horizontal, positive, stacked);
                if (positive) pos.Add(bar); else neg.Add(bar);
                if (!stacked) (positive ? neg : pos).Add(Ghost());
            }
            Element posArea, negArea;
            if (!f.Horizontal)
            {
                if (stacked) pos.Reverse();   // a stacked column lists top→bottom; the first series sits at the zero line
                posArea = new BoxEl { Height = posLen, Direction = (byte)(stacked ? 1 : 0), Justify = stacked ? FlexJustify.End : FlexJustify.Center, AlignItems = stacked ? FlexAlign.Stretch : FlexAlign.End, Gap = bo.BarGap, Padding = new Edges4(pad, 0f, pad, 0f), MinWidth = 0f, Children = pos.ToArray() };
                negArea = new BoxEl { Height = negLen, Direction = (byte)(stacked ? 1 : 0), Justify = stacked ? FlexJustify.Start : FlexJustify.Center, AlignItems = stacked ? FlexAlign.Stretch : FlexAlign.Start, Gap = bo.BarGap, Padding = new Edges4(pad, 0f, pad, 0f), MinWidth = 0f, Children = neg.ToArray() };
                var col = new List<Element>(3);
                if (bo.Labels) col.Add(new BoxEl { Height = f.TopPad, AlignItems = FlexAlign.Center, Justify = FlexJustify.End, Children = [new TextEl(total.ToString("N0", culture)) { Size = st.LabelFontSize, Color = st.LabelInk, MaxLines = 1 }] });
                col.Add(posArea); col.Add(negArea);
                slots[i] = new BoxEl { Key = "slot:" + i, Direction = 1, Grow = 1f, Basis = 0f, MinWidth = 0f, Justify = FlexJustify.End, Children = col.ToArray() };
            }
            else
            {
                if (stacked) neg.Reverse();   // a stacked row lists left→right; the first negative series touches the zero line
                negArea = new BoxEl { Width = negLen, Direction = (byte)(stacked ? 0 : 1), Justify = stacked ? FlexJustify.End : FlexJustify.Center, AlignItems = stacked ? FlexAlign.Stretch : FlexAlign.End, Gap = bo.BarGap, Padding = new Edges4(0f, pad, 0f, pad), MinHeight = 0f, Children = neg.ToArray() };
                posArea = new BoxEl { Width = posLen, Direction = (byte)(stacked ? 0 : 1), Justify = stacked ? FlexJustify.Start : FlexJustify.Center, AlignItems = stacked ? FlexAlign.Stretch : FlexAlign.Start, Gap = bo.BarGap, Padding = new Edges4(0f, pad, 0f, pad), MinHeight = 0f, Children = pos.ToArray() };
                slots[i] = new BoxEl { Key = "slot:" + i, Direction = 0, Grow = 1f, Basis = 0f, MinHeight = 0f, Children = [negArea, posArea] };
            }
        }
        return new BoxEl { Key = "bars", Direction = (byte)(f.Horizontal ? 1 : 0), Width = f.PlotW, Height = f.PlotH, MinWidth = 0f, HitTestVisible = false, Children = slots };

        static Element Ghost() => new BoxEl { Grow = 1f, Basis = 0f, MinWidth = 0f, MinHeight = 0f };
    }

    static Element Bar(float len, ColorF color, BarChartOptions bo, bool horizontal, bool positive, bool stacked)
    {
        float r = bo.CornerRadius;
        // Rounded on the value end only (stacked segments keep square seams; the outermost gets the radius via the last-series rule below).
        var corners = horizontal
            ? (positive ? new CornerRadius4(0f, r, r, 0f) : new CornerRadius4(r, 0f, 0f, r))
            : (positive ? new CornerRadius4(r, r, 0f, 0f) : new CornerRadius4(0f, 0f, r, r));
        return new BoxEl
        {
            Width = horizontal ? len : (stacked ? float.NaN : float.NaN), Height = horizontal ? float.NaN : len,
            Grow = stacked ? 0f : 1f, Basis = stacked ? float.NaN : 0f, Shrink = 0f, MinWidth = 0f, MinHeight = 0f,
            Corners = stacked ? CornerRadius4.All(0f) : corners, Fill = color, HitTestVisible = false,
            AlignSelf = stacked ? FlexAlign.Stretch : FlexAlign.Auto,
        };
    }

    static Element LineLabels(CartesianChart.Props p, PlotFrame f, CartesianChart.Style st)
    {
        // One caption per point of the FIRST series, centred on its slot, riding above the point.
        var data = p.Data;
        var culture = CultureInfo.CurrentCulture;
        var cells = new Element[f.Points];
        for (int i = 0; i < f.Points; i++)
        {
            float v = data.ValueAt(0, i);
            float y = float.IsNaN(v) ? float.NaN : f.Y(v) - st.LabelFontSize - 6f;
            cells[i] = new BoxEl
            {
                Grow = 1f, Basis = 0f, MinWidth = 0f, Direction = 1, AlignItems = FlexAlign.Center,
                Children = float.IsNaN(y) ? [] :
                [
                    new BoxEl { Height = MathF.Max(0f, y) },
                    new TextEl(v.ToString("N0", culture)) { Size = st.LabelFontSize, Color = st.LabelInk, MaxLines = 1 },
                ],
            };
        }
        return new BoxEl { Key = "labels", Direction = 0, Width = f.PlotW, Height = f.PlotH, HitTestVisible = false, Children = cells };
    }

    // ── axes ──────────────────────────────────────────────────────────────────────────────────────────────────────

    static Element CategoryAxisHorizontal(CartesianData data, ChartAxisOptions ax, PlotFrame f, CartesianChart.Style st, float leftInset, float height)
    {
        int n = f.Points;
        var labels = new string[n];
        Span<float> xs = n <= 256 ? stackalloc float[n] : new float[n];
        Span<float> widths = n <= 256 ? stackalloc float[n] : new float[n];
        Span<bool> keep = n <= 256 ? stackalloc bool[n] : new bool[n];
        for (int i = 0; i < n; i++)
        {
            string raw = i < data.Categories.Count ? data.Categories[i] : "";
            labels[i] = ax.CategoryFormatter is { } cf ? cf(raw) : raw;
            xs[i] = f.Centre(i);
            widths[i] = ChartMath.EstimateTextWidth(labels[i], st.AxisFontSize);
        }
        ChartMath.DropCollidingTicks(xs, widths, ax.MinTickGap, ax.PreserveStartEnd, keep);
        var cells = new Element[n];
        for (int i = 0; i < n; i++)
            cells[i] = new BoxEl
            {
                Grow = 1f, Basis = 0f, MinWidth = 0f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                Children = keep[i] ? [new TextEl(labels[i]) { Size = st.AxisFontSize, Color = st.AxisInk, MaxLines = 1 }] : [],
            };
        return new BoxEl
        {
            Key = "x-axis", Direction = 0, Height = height, MinWidth = 0f, HitTestVisible = false,
            Padding = new Edges4(leftInset, 2f, 0f, 0f), Children = cells,
        };
    }

    static Element CategoryAxisVertical(CartesianData data, ChartAxisOptions ax, PlotFrame f, CartesianChart.Style st, float width)
    {
        int n = f.Points;
        var cells = new Element[n];
        for (int i = 0; i < n; i++)
        {
            string raw = i < data.Categories.Count ? data.Categories[i] : "";
            string label = ax.CategoryFormatter is { } cf ? cf(raw) : raw;
            cells[i] = new BoxEl
            {
                Grow = 1f, Basis = 0f, MinHeight = 0f, AlignItems = FlexAlign.End, Justify = FlexJustify.Center,
                Padding = new Edges4(0f, 0f, 6f, 0f),
                Children = [new TextEl(label) { Size = st.AxisFontSize, Color = st.AxisInk, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MaxWidth = width - 6f }],
            };
        }
        return new BoxEl { Key = "y-axis", Direction = 1, Width = width, Height = f.PlotH, HitTestVisible = false, Children = cells };
    }

    static Element ValueAxisVertical(Geometry? geo, PlotFrame f, ChartAxisOptions ax, CartesianChart.Style st, float width)
    {
        var kids = new List<Element>();
        if (geo is not null)
        {
            var culture = CultureInfo.CurrentCulture;
            float cursor = 0f, half = st.AxisFontSize * 0.65f;
            for (int k = geo.TickCount - 1; k >= 0; k--)          // top → bottom
            {
                float y = f.Y(geo.Ticks[k]);
                float top = y - half;
                if (top < cursor) continue;
                if (top > cursor) kids.Add(new BoxEl { Height = top - cursor });
                kids.Add(new BoxEl
                {
                    Height = half * 2f, AlignItems = FlexAlign.End, Justify = FlexJustify.Center, Padding = new Edges4(0f, 0f, 6f, 0f),
                    Children = [new TextEl(Format(geo.Ticks[k], ax, culture)) { Size = st.AxisFontSize, Color = st.AxisInk, MaxLines = 1 }],
                });
                cursor = y + half;
            }
        }
        return new BoxEl { Key = "y-axis", Direction = 1, Width = width, Height = f.PlotH, HitTestVisible = false, Children = kids.ToArray() };
    }

    static Element ValueAxisHorizontal(Geometry? geo, PlotFrame f, ChartAxisOptions ax, CartesianChart.Style st, float leftInset, float height)
    {
        var kids = new List<Element>();
        if (geo is not null)
        {
            var culture = CultureInfo.CurrentCulture;
            float cursor = 0f;
            for (int k = 0; k < geo.TickCount; k++)
            {
                string text = Format(geo.Ticks[k], ax, culture);
                float w = ChartMath.EstimateTextWidth(text, st.AxisFontSize) + 4f, half = w * 0.5f;
                float x = f.ValuePx(geo.Ticks[k]);
                float left = Math.Clamp(x - half, 0f, MathF.Max(0f, f.PlotW - w));
                if (left < cursor) continue;
                if (left > cursor) kids.Add(new BoxEl { Width = left - cursor });
                kids.Add(new BoxEl { Width = w, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, Children = [new TextEl(text) { Size = st.AxisFontSize, Color = st.AxisInk, MaxLines = 1 }] });
                cursor = left + w;
            }
        }
        return new BoxEl { Key = "x-axis", Direction = 0, Height = height, MinWidth = 0f, HitTestVisible = false, Padding = new Edges4(leftInset, 2f, 0f, 0f), Children = kids.ToArray() };
    }

    static string Format(float v, ChartAxisOptions ax, CultureInfo culture)
        => ax.ValueFormatter is { } vf ? vf(v) : MathF.Abs(v - MathF.Round(v)) < 1e-3f ? v.ToString("N0", culture) : v.ToString("0.##", culture);

    // ── geometry (memoised) ───────────────────────────────────────────────────────────────────────────────────────

    // ── annotations (bands + marks) ──────────────────────────────────────────────────────────────────────────────

    /// <summary>Bands and marks in plot px along the CATEGORY axis (x for vertical charts, y for horizontal bars), one
    /// path per colour. Bands are clamped to the plot; marks off it are dropped.</summary>
    static Annotations? BuildAnnotations(CartesianChartOptions o, PlotFrame f, CartesianChart.Style st)
    {
        var bands = o.Bands.Span;
        var marks = o.Marks.Span;
        if (bands.Length == 0 && marks.Length == 0) return null;
        float along = f.Horizontal ? f.PlotH : f.PlotW;   // the category axis length
        float across = f.Horizontal ? f.PlotW : f.PlotH;
        var bandInks = new List<(ColorF Ink, PathBuilder Path)>();
        var markInks = new List<(ColorF Ink, PathBuilder Path)>();
        var labels = new List<(float, string)>();

        foreach (var b in bands)
        {
            float a = Math.Clamp(f.At(MathF.Min(b.From, b.To)), 0f, along);
            float z = Math.Clamp(f.At(MathF.Max(b.From, b.To)), 0f, along);
            if (z - a < 0.5f) continue;
            var path = InkPath(bandInks, b.Color.A > 0f ? b.Color : st.BandFill);
            if (f.Horizontal) { path.MoveTo(0f, a); path.LineTo(across, a); path.LineTo(across, z); path.LineTo(0f, z); }
            else { path.MoveTo(a, 0f); path.LineTo(z, 0f); path.LineTo(z, across); path.LineTo(a, across); }
            path.Close();
            if (!string.IsNullOrEmpty(b.Label)) labels.Add((a, b.Label));
        }
        foreach (var m in marks)
        {
            float x = f.At(m.At);
            if (x < 0f || x > along) continue;
            var path = InkPath(markInks, m.Color.A > 0f ? m.Color : st.MarkInk);
            if (f.Horizontal) { path.MoveTo(0f, x); path.LineTo(across, x); }
            else { path.MoveTo(x, 0f); path.LineTo(x, across); }
            if (!string.IsNullOrEmpty(m.Label)) labels.Add((x, m.Label));
        }

        var bandPaths = new PathData[bandInks.Count];
        var bandColors = new ColorF[bandInks.Count];
        for (int i = 0; i < bandInks.Count; i++)
        {
            bandPaths[i] = bandInks[i].Path.Finish(PathContentEpoch.Mint(), FillRule.NonZero);
            bandColors[i] = bandInks[i].Ink;
        }
        var markPaths = new PathData[markInks.Count];
        var markColors = new ColorF[markInks.Count];
        for (int i = 0; i < markInks.Count; i++)
        {
            markPaths[i] = markInks[i].Path.Finish(PathContentEpoch.Mint(), FillRule.NonZero);
            markColors[i] = markInks[i].Ink;
        }
        return new Annotations(bandPaths, bandColors, markPaths, markColors, labels.ToArray());

        static PathBuilder InkPath(List<(ColorF Ink, PathBuilder Path)> inks, ColorF ink)
        {
            for (int i = 0; i < inks.Count; i++) if (inks[i].Ink == ink) return inks[i].Path;
            var path = new PathBuilder();
            inks.Add((ink, path));
            return path;
        }
    }

    /// <summary>Band/mark captions in the axis font: along the top edge of the plot (vertical charts) or its left edge
    /// (horizontal bars), just past the band's leading edge / the mark.</summary>
    static Element AnnotationLabels(Annotations notes, PlotFrame f, CartesianChart.Style st)
    {
        var kids = new Element[notes.Labels.Length];
        for (int i = 0; i < kids.Length; i++)
        {
            var (at, label) = notes.Labels[i];
            kids[i] = new BoxEl
            {
                OffsetX = f.Horizontal ? 2f : at + 3f,
                OffsetY = f.Horizontal ? at + 2f : 2f,
                HitTestVisible = false,
                Children = [new TextEl(label) { Size = st.AxisFontSize, Color = st.LabelInk, MaxLines = 1 }],
            };
        }
        return new BoxEl { Key = "annotation-labels", ZStack = true, Width = f.PlotW, Height = f.PlotH, HitTestVisible = false, Children = kids };
    }

    static Geometry Build(CartesianChart.Props p, PlotFrame f, float[] ticks)
    {
        var data = p.Data;
        int sc = data.Series.Count, n = f.Points;

        PathData? grid = null;
        if (p.Options.Grid && ticks.Length > 0)
        {
            var b = new PathBuilder();
            foreach (float t in ticks)
            {
                if (f.Horizontal) { float x = f.ValuePx(t); b.MoveTo(x, 0f); b.LineTo(x, f.PlotH); }
                else { float y = f.Y(t); b.MoveTo(0f, y); b.LineTo(f.PlotW, y); }
            }
            grid = b.Finish(PathContentEpoch.Mint(), FillRule.NonZero);
        }

        var lines = new PathData?[sc];
        var fills = new PathData?[sc];
        var dots = new PathData?[sc];
        float[][]? cumulative = null;
        var stacking = p.Kind == CartesianChart.Kind.Area ? p.Area?.Stacking ?? ChartStacking.None
                     : p.Kind == CartesianChart.Kind.Bar ? p.Bar?.Stacking ?? ChartStacking.None : ChartStacking.None;
        if (stacking != ChartStacking.None)
        {
            cumulative = new float[sc][];
            for (int s = 0; s < sc; s++) cumulative[s] = new float[n];
            if (stacking == ChartStacking.Stacked) ChartMath.Stack(data.Values, cumulative); else ChartMath.Expand(data.Values, cumulative);
        }
        if (p.Kind == CartesianChart.Kind.Bar) return new Geometry(grid, lines, fills, dots, cumulative, ticks, ticks.Length);

        var curve = p.Kind == CartesianChart.Kind.Line ? p.Line?.Curve ?? ChartCurve.Natural : p.Area?.Curve ?? ChartCurve.Natural;
        var pts = new Point2[n];
        var lower = new Point2[n];
        var scratch = new Point2[Math.Max(2, (n - 1) * 2)];
        for (int s = 0; s < sc; s++)
        {
            var src = cumulative ?? data.Values;
            for (int i = 0; i < n; i++)
            {
                float v = s < src.Length && i < src[s].Length ? src[s][i] : float.NaN;
                pts[i] = float.IsNaN(v) ? new Point2(float.NaN, float.NaN) : new Point2(f.Centre(i), f.Y(v));
            }
            var lb = new PathBuilder();
            ChartMath.AppendCurve(lb, pts, curve, scratch);
            lines[s] = lb.Finish(PathContentEpoch.Mint(), FillRule.NonZero);

            if (p.Kind == CartesianChart.Kind.Area)
            {
                // Lower edge: the previous series' upper curve (stacked) or the zero baseline, traversed backwards.
                for (int i = 0; i < n; i++)
                {
                    float lv = cumulative is not null && s > 0 ? cumulative[s - 1][i] : 0f;
                    if (float.IsNaN(lv)) lv = 0f;
                    lower[n - 1 - i] = float.IsNaN(pts[i].X) ? new Point2(float.NaN, float.NaN) : new Point2(f.Centre(i), f.Y(lv));
                }
                fills[s] = AreaFill(pts, lower, curve, scratch, f);
            }
            if (p.Kind == CartesianChart.Kind.Line && p.Line?.Dots == ChartDotMode.All)
            {
                var db = new PathBuilder();
                float r = p.Line.DotRadius;
                for (int i = 0; i < n; i++) if (!float.IsNaN(pts[i].X)) Circle(db, pts[i].X, pts[i].Y, r);
                dots[s] = db.Finish(PathContentEpoch.Mint(), FillRule.NonZero);
            }
        }
        return new Geometry(grid, lines, fills, dots, cumulative, ticks, ticks.Length);
    }

    /// <summary>Upper curve forward + lower curve backward, closed — one contour per gap-free run.</summary>
    static PathData AreaFill(Point2[] upper, Point2[] lowerReversed, ChartCurve curve, Point2[] scratch, PlotFrame f)
    {
        var b = new PathBuilder();
        int n = upper.Length;
        int start = 0;
        while (start < n)
        {
            while (start < n && float.IsNaN(upper[start].X)) start++;
            int end = start;
            while (end < n && !float.IsNaN(upper[end].X)) end++;
            if (end > start)
            {
                var up = upper.AsSpan(start, end - start);
                ChartMath.AppendCurve(b, up, curve, scratch);
                // lowerReversed[j] pairs with upper[n-1-j]; the run's lower edge is indices (n-end) .. (n-1-start).
                var low = lowerReversed.AsSpan(n - end, end - start);
                // Continue the contour: line down to the lower edge's first point, then the lower curve back.
                b.LineTo(low[0].X, low[0].Y);
                AppendContinuing(b, low, curve, scratch);
                b.Close();
            }
            start = end;
        }
        return b.Finish(PathContentEpoch.Mint(), FillRule.NonZero);
    }

    /// <summary>Like <see cref="ChartMath.AppendCurve"/> but continuing the current contour (no MoveTo).</summary>
    static void AppendContinuing(PathBuilder b, ReadOnlySpan<Point2> run, ChartCurve curve, Point2[] scratch)
    {
        if (run.Length < 2) return;
        switch (curve)
        {
            case ChartCurve.Linear:
                for (int i = 1; i < run.Length; i++) b.LineTo(run[i].X, run[i].Y);
                return;
            case ChartCurve.Step:
                // reversed run: step-after on the forward axis is step-before here, so hold then drop mirrors the upper edge.
                for (int i = 1; i < run.Length; i++) { b.LineTo(run[i - 1].X, run[i].Y); b.LineTo(run[i].X, run[i].Y); }
                return;
            default:
                if (run.Length == 2) { b.LineTo(run[1].X, run[1].Y); return; }
                int need = (run.Length - 1) * 2;
                Span<Point2> ctrl = scratch.Length >= need ? scratch.AsSpan(0, need) : new Point2[need];
                ChartMath.CubicControls(run, curve, ctrl);
                for (int i = 1; i < run.Length; i++)
                {
                    var c1 = ctrl[(i - 1) * 2]; var c2 = ctrl[(i - 1) * 2 + 1];
                    b.CubicTo(c1.X, c1.Y, c2.X, c2.Y, run[i].X, run[i].Y);
                }
                return;
        }
    }

    internal static void Circle(PathBuilder b, float cx, float cy, float r)
    {
        const float K = 0.5523f;
        float k = r * K;
        b.MoveTo(cx + r, cy);
        b.CubicTo(cx + r, cy + k, cx + k, cy + r, cx, cy + r);
        b.CubicTo(cx - k, cy + r, cx - r, cy + k, cx - r, cy);
        b.CubicTo(cx - r, cy - k, cx - k, cy - r, cx, cy - r);
        b.CubicTo(cx + k, cy - r, cx + r, cy - k, cx + r, cy);
        b.Close();
    }
}

/// <summary>The one layer that follows the active index: crosshair (line/area) or slot wash + rim (bars), active
/// markers, and the tooltip. Its own component so a hover re-renders THIS and nothing else.</summary>
internal sealed class ActiveOverlay : Component
{
    internal sealed record Props(Signal<int> Active, CartesianData Data, CartesianChartOptions Options, CartesianChart.Kind Kind,
                                 BarChartOptions? Bar, PlotFrame Frame, float[][]? Cumulative, CartesianChart.Style Style);

    internal static Element Mount(Props props) => Embed.Comp(props, static () => new ActiveOverlay()) with { Key = "active" };

    public override Element Render()
    {
        var p = UseProps<Props>();
        int i = p.Active.Value;                // THE subscription
        var f = p.Frame;
        var st = p.Style;
        if (i < 0 || i >= f.Points || f.PlotW <= 0f) return new BoxEl { Width = f.PlotW, Height = f.PlotH, HitTestVisible = false };

        var layers = new List<Element>(4);
        float c = f.Centre(i);
        if (p.Kind == CartesianChart.Kind.Bar)
        {
            if (p.Bar?.HighlightActive != false)
                layers.Add(f.Horizontal
                    ? Row(0f, new BoxEl { Height = f.Slot, Width = f.PlotW, Fill = st.ActiveWash, Corners = CornerRadius4.All(3f) }, c - f.Slot * 0.5f, vertical: true)
                    : Row(c - f.Slot * 0.5f, new BoxEl { Width = f.Slot, Height = f.PlotH, Fill = st.ActiveWash, Corners = CornerRadius4.All(3f) }, 0f, vertical: false));
        }
        else
        {
            layers.Add(Row(c - 0.5f, new BoxEl { Width = 1f, Height = f.PlotH, Fill = st.CrosshairInk }, 0f, vertical: false));
            // Active markers: one dot per series at this category.
            var src = p.Cumulative ?? p.Data.Values;
            for (int s = 0; s < p.Data.Series.Count; s++)
            {
                float v = s < src.Length && i < src[s].Length ? src[s][i] : float.NaN;
                if (float.IsNaN(v)) continue;
                float y = f.Y(v);
                layers.Add(Row(c - 5f, new BoxEl
                {
                    Width = 10f, Height = 10f, Corners = CornerRadius4.All(5f), Fill = p.Data.Series[s].Color,
                    BorderWidth = 2f, BorderColor = Tok.FillSolidBase,
                }, y - 5f, vertical: false));
            }
        }

        if (p.Options.Tooltip is { } to)
        {
            const float EstimatedTipW = 168f;
            var tip = ChartTooltip.Content(p.Data, i, to, st.Tooltip);
            float x = f.Horizontal ? f.PlotW * 0.5f - EstimatedTipW * 0.5f
                    : c + st.TooltipOffset + EstimatedTipW <= f.PlotW ? c + st.TooltipOffset : MathF.Max(0f, c - st.TooltipOffset - EstimatedTipW);
            float y = f.Horizontal ? MathF.Min(MathF.Max(0f, c + f.Slot * 0.5f + 6f), MathF.Max(0f, f.PlotH - 80f)) : 8f;
            layers.Add(Row(x, tip, y, vertical: false));
        }

        return new BoxEl { Width = f.PlotW, Height = f.PlotH, ZStack = true, HitTestVisible = false, Children = layers.ToArray() };
    }

    /// <summary>Place <paramref name="child"/> at (x, y) inside the plot with spacers — the ZStack has no absolute
    /// positioning, and a spacer pair is a plain layout, so this costs no bind.</summary>
    static Element Row(float x, Element child, float y, bool vertical) => new BoxEl
    {
        Direction = 1, HitTestVisible = false,
        Children =
        [
            new BoxEl { Height = MathF.Max(0f, y) },
            new BoxEl { Direction = 0, Children = [new BoxEl { Width = MathF.Max(0f, x) }, child] },
        ],
    };
}
