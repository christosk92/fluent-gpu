using System;
using System.Collections.Generic;
using System.Globalization;
using FluentGpu.Dsl;
using FluentGpu.Foundation;

namespace FluentGpu.Controls;

/// <summary>
/// The tooltip body a Cartesian chart shows for one category (the port of shadcn's <c>ChartTooltipContent</c>): a
/// header label, one row per series — indicator swatch · series label · value (tabular, right-aligned) — and an
/// optional footer. The "nested label" rule survives the port: with a single series and a non-dot indicator the
/// header collapses into the row. Purely presentational (no hit-testing); the chart positions it.
/// </summary>
public static class ChartTooltip
{
    public sealed record Style
    {
        public float MinWidth { get; init; } = 128f;
        public float CornerRadius { get; init; } = 8f;
        public ColorF Fill { get; init; }
        public ColorF Stroke { get; init; }
        public ColorF LabelInk { get; init; }
        public ColorF ValueInk { get; init; }
        public float FontSize { get; init; } = 12f;
        public ShadowSpec? Shadow { get; init; }
    }

    public static Style? StyleOverride;
    public static Style DefaultStyle => StyleOverride ?? new Style
    {
        Fill = Tok.FillSolidBase,
        Stroke = Tok.StrokeFlyoutDefault,
        LabelInk = Tok.TextSecondary,
        ValueInk = Tok.TextPrimary,
        Shadow = Elevation.Tooltip,
    };

    /// <summary>The ONE canonical builder: the tooltip for category <paramref name="index"/>.</summary>
    public static Element Content(CartesianData data, int index, ChartTooltipOptions? options = null, Style? style = null)
    {
        var o = options ?? new ChartTooltipOptions();
        var st = style ?? DefaultStyle;
        var culture = CultureInfo.CurrentCulture;
        string Value(float v) => o.ValueFormatter is { } f ? f(v) : float.IsNaN(v) ? "–" : v.ToString("N0", culture);
        string label = o.LabelFormatter is { } lf ? lf(index)
            : index >= 0 && index < data.Categories.Count ? data.Categories[index] : "";

        int count = data.Series.Count;
        bool nestLabel = count == 1 && o.Indicator != ChartTooltipIndicator.Dot;
        var rows = new List<Element>(count + 2);
        if (!o.HideLabel && !nestLabel && label.Length > 0)
            rows.Add(new TextEl(label) { Size = st.FontSize, Weight = 600, Color = st.ValueInk, MaxLines = 1 });

        for (int s = 0; s < count; s++)
        {
            var series = data.Series[s];
            float v = data.ValueAt(s, index);
            var cells = new List<Element>(4);
            if (!o.HideIndicator) cells.Add(Indicator(o.Indicator, series));
            var name = new TextEl(series.Label) { Size = st.FontSize, Color = st.LabelInk, MaxLines = 1, Grow = 1f, Basis = 0f, MinWidth = 0f };
            if (nestLabel && !o.HideLabel && label.Length > 0)
            {
                cells.Add(new BoxEl
                {
                    Direction = 1, Grow = 1f, Basis = 0f, MinWidth = 0f,
                    Children =
                    [
                        new TextEl(label) { Size = st.FontSize, Color = st.LabelInk, MaxLines = 1 },
                        new TextEl(series.Label) { Size = st.FontSize, Weight = 600, Color = st.ValueInk, MaxLines = 1 },
                    ],
                });
            }
            else cells.Add(name);
            cells.Add(new TextEl(Value(v))
            {
                Size = st.FontSize, Weight = 600, Color = st.ValueInk, MaxLines = 1, Shrink = 0f,
                AlignSelf = nestLabel ? FlexAlign.End : FlexAlign.Auto,
            });
            rows.Add(new BoxEl
            {
                Key = "row:" + series.Key, Direction = 0, Gap = 8f, AlignItems = nestLabel ? FlexAlign.End : FlexAlign.Center,
                MinWidth = 0f, Children = cells.ToArray(),
            });
        }
        if (o.Footer?.Invoke(data, index) is { } footer) rows.Add(footer);

        return new BoxEl
        {
            Direction = 1, Gap = 6f, MinWidth = st.MinWidth, Padding = new Edges4(10f, 6f, 10f, 7f),
            Corners = CornerRadius4.All(st.CornerRadius), Fill = st.Fill, BorderWidth = 1f, BorderColor = st.Stroke,
            Shadow = st.Shadow, HitTestVisible = false, Children = rows.ToArray(),
        };
    }

    /// <summary>The row swatch: dot = 10×10 r2 fill · line = 4 px full-height bar · dashed = outline only.</summary>
    static Element Indicator(ChartTooltipIndicator kind, ChartSeries series)
    {
        if (series.Glyph is { Length: > 0 } glyph) return Ui.Icon(glyph, 12f, series.Color) with { Shrink = 0f };
        return kind switch
        {
            ChartTooltipIndicator.Line => new BoxEl { Width = 4f, AlignSelf = FlexAlign.Stretch, MinHeight = 12f, Corners = CornerRadius4.All(2f), Fill = series.Color, Shrink = 0f },
            ChartTooltipIndicator.Dashed => new BoxEl
            {
                Width = 10f, Height = 10f, Corners = CornerRadius4.All(2f), Shrink = 0f,
                BorderWidth = 1.5f, BorderColor = series.Color, BorderDashOn = 2f, BorderDashOff = 2f,
            },
            _ => new BoxEl { Width = 10f, Height = 10f, Corners = CornerRadius4.All(2f), Fill = series.Color, Shrink = 0f },
        };
    }
}

/// <summary>A legend row: one swatch (or glyph) + label per series (shadcn <c>ChartLegendContent</c>). Toggling is
/// the APP's state — pass <paramref name="isActive"/>/<paramref name="onToggle"/> and hand the chart a filtered
/// <see cref="CartesianData"/>; an inactive entry dims, its colour never changes (colour follows the entity).</summary>
public static class ChartLegend
{
    public sealed record Style
    {
        public float Swatch { get; init; } = 8f;
        public float Gap { get; init; } = 16f;
        public float FontSize { get; init; } = 12f;
        public ColorF Ink { get; init; }
        public float InactiveOpacity { get; init; } = 0.4f;
    }

    public static Style? StyleOverride;
    public static Style DefaultStyle => StyleOverride ?? new Style { Ink = Tok.TextSecondary };

    /// <summary>The ONE canonical factory. <paramref name="toggleSeries"/> receives the clicked series key (the app
    /// flips its own visibility state and passes the chart a filtered table); null = a static legend.</summary>
    public static Element Create(IReadOnlyList<ChartSeries> series, Func<string, bool>? isActive = null,
                                 Action<string>? toggleSeries = null, Style? style = null, string? key = null)
    {
        var st = style ?? DefaultStyle;
        var items = new Element[series.Count];
        for (int i = 0; i < series.Count; i++)
        {
            var s = series[i];
            bool active = isActive?.Invoke(s.Key) ?? true;
            bool live = toggleSeries is not null;
            string k = s.Key;
            items[i] = new BoxEl
            {
                Key = "legend:" + k, Direction = 0, Gap = 6f, AlignItems = FlexAlign.Center,
                Padding = new Edges4(4f, 2f, 4f, 2f), Corners = CornerRadius4.All(4f),
                Role = live ? AutomationRole.Button : AutomationRole.None, Focusable = live,
                Cursor = live ? CursorId.Hand : null,
                HoverFill = live ? Tok.FillSubtleSecondary : ColorF.Transparent,
                PressedFill = live ? Tok.FillSubtleTertiary : ColorF.Transparent,
                Opacity = active ? 1f : st.InactiveOpacity,
                OnClick = live ? (Action)(() => toggleSeries!(k)) : null,
                Children =
                [
                    s.Glyph is { Length: > 0 } g
                        ? Ui.Icon(g, st.Swatch + 4f, s.Color)
                        : new BoxEl { Width = st.Swatch, Height = st.Swatch, Corners = CornerRadius4.All(2f), Fill = s.Color, Shrink = 0f },
                    new TextEl(s.Label) { Size = st.FontSize, Color = st.Ink, MaxLines = 1 },
                ],
            };
        }
        return new BoxEl { Key = key, Direction = 0, Wrap = true, Gap = st.Gap, Justify = FlexJustify.Center, MinWidth = 0f, Children = items };
    }
}
