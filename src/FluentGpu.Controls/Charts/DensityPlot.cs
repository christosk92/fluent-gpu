using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using FluentGpu.Animation;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Render;

namespace FluentGpu.Controls;

/// <summary>A clickable interval under a <see cref="DensityPlot"/> — a LENS the user can apply (a tempo band, a
/// year range). <paramref name="Lit"/> marks the one currently filtering the caller's list.</summary>
public readonly record struct PlotBand(float Lo, float Hi, string Tip, bool Lit = false, Action? OnClick = null);

/// <summary>One axis caption at a domain value.</summary>
public readonly record struct PlotTick(float Value, string Label);

/// <summary>What a <see cref="DensityPlot"/> draws. <paramref name="Values"/> are the raw samples (already filtered
/// of unknowns by the caller); anything outside <c>[DomainMin, DomainMax]</c> is clamped to the edge, never dropped.</summary>
public sealed record DensityPlotModel(ReadOnlyMemory<float> Values, float DomainMin, float DomainMax)
{
    /// <summary>Gaussian bandwidth in domain units; 0 = <see cref="ChartMath.DefaultBandwidth"/>.</summary>
    public float Bandwidth { get; init; }
    public int Samples { get; init; } = 71;
    public ReadOnlyMemory<PlotBand> Bands { get; init; }
    public ReadOnlyMemory<PlotTick> Ticks { get; init; }
    /// <summary>Vertical hairlines at these domain values (band edges).</summary>
    public ReadOnlyMemory<float> Hairlines { get; init; }
    /// <summary>A vertical marker line (the median).</summary>
    public float? Marker { get; init; }
    /// <summary>Optional per-value ARGB for the rug (same length as <see cref="Values"/>); 0 = the default ink. Empty
    /// = every dot in the default ink. Colours are DATA here (a key, a category), not decoration.</summary>
    public ReadOnlyMemory<uint> ValueColors { get; init; }
    /// <summary>Up to this many values the rug is individual stacked dots; above it, ticks. 0 = no rug.</summary>
    public int RugDotMax { get; init; } = 80;
}

/// <summary>
/// A one-dimensional distribution: a Gaussian-KDE ridge (filled area + line) over a fixed domain, a rug of the actual
/// values beneath it (stacked dots for a small set, ticks for a large one), an optional marker, hairlines, an axis
/// row, and clickable BANDS under the whole stage that act as lenses. The four layers above the bands are
/// hit-test-invisible so every pointer event lands on a band (a <c>PathEl</c> cannot own a click — Element.cs).
///
/// <para>Geometry is authored in DIP against the MEASURED stage width (<c>UseMeasuredWidth</c>) and memoised on
/// (values, width, rug mode): a lit-band change re-renders the component but reuses the same <c>PathData</c>
/// instances, so nothing re-tessellates (a fresh epoch is a fresh tessellation — PathGeometry.cs). The first frame
/// has no width yet and draws the bands alone; the measured width lands next frame.</para>
/// </summary>
public static class DensityPlot
{
    public sealed record Style
    {
        public float Height { get; init; } = 38f;
        public ColorF Ink { get; init; }
        public float AreaAlpha { get; init; } = 0.22f;
        public float LineWidth { get; init; } = 1.5f;
        public float RugAlpha { get; init; } = 0.55f;
        public float RugTickHeight { get; init; } = 3f;
        public float DotSize { get; init; } = 3f;
        public float TopMargin { get; init; } = 3f;
        public ColorF Hairline { get; init; }
        public ColorF Marker { get; init; }
        public float AxisFontSize { get; init; } = 9f;
        public ColorF AxisInk { get; init; }
        public float TipDelayMs { get; init; } = float.NaN;
    }

    public static Style? StyleOverride;
    public static Style DefaultStyle => StyleOverride ?? new Style
    {
        Ink = Tok.AccentDefault,
        Hairline = Tok.StrokeCardDefault,
        Marker = Tok.AccentTextPrimary with { A = 0.6f },
        AxisInk = Tok.TextTertiary,
    };

    /// <summary>The ONE canonical factory.</summary>
    public static Element Create(DensityPlotModel model, Style? style = null, string? key = null)
        => Embed.Comp(new Props(model, style ?? DefaultStyle), static () => new DensityPlotComponent()) with { Key = key };

    /// <summary>Diagnostic: how many times ANY density plot rebuilt its geometry (minted fresh path epochs). A caller
    /// whose values array is re-created on every render shows up here as one rebuild per render — the geometry memo
    /// is keyed on the values ARRAY, so key your inputs on content, not on list identity.</summary>
    public static int GeometryBuilds { get; internal set; }

    /// <summary>The measured-width quantum handed to <c>UseMeasuredWidth</c>: the hook rounds the stage width to this
    /// grid BEFORE its exact-compare signal write, so sub-quantum layout wobble never reaches the component at all (no
    /// re-render, let alone a re-minted geometry). The render then steps half a quantum DOWN from that rounded value,
    /// because the measured width is what the parent offered and a layer must never be wider than the stage.</summary>
    public const float WidthQuantum = 4f;

    internal sealed record Props(DensityPlotModel Model, Style Style);
}

internal sealed class DensityPlotComponent : Component
{
    /// <summary>The memoised geometry for one (values, width) pair.</summary>
    sealed record Geometry(PathData? Area, PathData? Line, PathData? Hairlines, PathData? Marker, List<(uint Argb, PathData Path)> Rug);

    public override Element Render()
    {
        var p = UseProps<DensityPlot.Props>();
        var m = p.Model;
        var st = p.Style;
        // The hook rounds the measured width to the 4 DIP grid before it writes, so a sub-quantum wobble is coalesced at
        // the signal and this render never runs for it (with quantum 0 every wobble re-rendered the whole chart: the
        // bands, six layers and the axis — cheap per frame, but per frame). Round-to-nearest can land up to Quantum/2
        // ABOVE the stage's true width, and a layer must never be wider than the stage, so step half a quantum down:
        // w ∈ (stage − Quantum, stage], the same band the old floor snap produced, and just as stable a geometry key.
        float w = MathF.Max(0f, UseMeasuredWidth(DensityPlot.WidthQuantum).Value - DensityPlot.WidthQuantum * 0.5f);
        bool dots = m.RugDotMax > 0 && m.Values.Length <= m.RugDotMax;
        int rugMode = m.RugDotMax <= 0 ? 0 : dots ? 1 : 2;

        // Keyed on the VALUES ARRAY (not the model record — a lit-band change rebuilds the model with the same values),
        // the measured width and the geometry-relevant scalars. Same key ⇒ the same PathData instances ⇒ no re-tessellation.
        MemoryMarshal.TryGetArray(m.Values, out var valuesSegment);
        var geo = UseMemo(() => w > 0f ? Build(m, st, w, rugMode) : null,
            DepKey.Combine(
                DepKey.Combine(DepKey.FromRef(valuesSegment.Array), DepKey.From(m.Values.Length, rugMode)),
                DepKey.Combine(DepKey.From(w, m.DomainMin, m.DomainMax, st.Height),
                               DepKey.From(m.Marker ?? float.NaN, m.Hairlines.Length))));

        var layers = new List<Element>(6) { Bands(m, st, w) };
        if (geo is not null)
        {
            if (geo.Hairlines is not null) layers.Add(Layer(geo.Hairlines, w, st.Height, default, st.Hairline, new StrokeStyle(1f)));
            if (geo.Area is not null) layers.Add(Layer(geo.Area, w, st.Height, st.Ink with { A = st.AreaAlpha }, default, default));
            if (geo.Line is not null) layers.Add(Layer(geo.Line, w, st.Height, default, st.Ink, new StrokeStyle(st.LineWidth, LineCap.Round, LineJoin.Round)));
            foreach (var (argb, path) in geo.Rug)
            {
                var ink = argb == 0 ? st.Ink with { A = st.RugAlpha } : Argb(argb) with { A = 0.85f };
                layers.Add(Layer(path, w, st.Height, ink, default, default));
            }
            if (geo.Marker is not null) layers.Add(Layer(geo.Marker, w, st.Height, default, st.Marker, new StrokeStyle(1f)));
        }

        var stage = new BoxEl
        {
            Key = "stage", ZStack = true, Height = st.Height, MinWidth = 0f, HitTestPassThrough = true,
            Children = layers.ToArray(),
        };
        var kids = new List<Element>(2) { stage };
        if (m.Ticks.Length > 0) kids.Add(Axis(m, st));
        // Kit sizing rule: the plot STRETCHES to the width its parent offers (Grow in a row, Stretch in a column) — that
        // offered width is what UseMeasuredWidth reads back, so a plot given no width would otherwise never draw.
        return new BoxEl
        {
            Key = "density", Direction = 1, MinWidth = 0f, Grow = 1f, Basis = 0f, AlignSelf = FlexAlign.Stretch,
            Role = AutomationRole.None, Children = kids.ToArray(),
        };
    }

    // ── layers ────────────────────────────────────────────────────────────────────────────────────────────────────

    static Element Layer(PathData geometry, float w, float h, ColorF fill, ColorF stroke, StrokeStyle style) => new BoxEl
    {
        Width = w, Height = h, HitTestVisible = false,
        Children =
        [
            new PathEl { Geometry = geometry, Width = w, Height = h, Fill = fill, StrokeColor = stroke, Stroke = style },
        ],
    };

    static Element Bands(DensityPlotModel m, DensityPlot.Style st, float w)
    {
        var bands = m.Bands.Span;
        float span = MathF.Max(1e-6f, m.DomainMax - m.DomainMin);
        var kids = new Element[bands.Length];
        for (int i = 0; i < bands.Length; i++)
        {
            var b = bands[i];
            float lo = Math.Clamp(b.Lo, m.DomainMin, m.DomainMax), hi = Math.Clamp(b.Hi, m.DomainMin, m.DomainMax);
            float share = MathF.Max(0.0001f, (hi - lo) / span);
            bool live = b.OnClick is not null;
            var hit = new BoxEl
            {
                Height = st.Height, Basis = 0f, MinWidth = 0f, Corners = CornerRadius4.All(3f),
                Role = live ? AutomationRole.Button : AutomationRole.None, Focusable = live,
                Cursor = live ? CursorId.Hand : null, FocusVisualMargin = new Edges4(1f, 1f, 1f, 1f),
                Fill = b.Lit ? Tok.AccentSubtle : ColorF.Transparent,
                HoverFill = !live ? ColorF.Transparent : b.Lit ? Tok.AccentSecondary : Tok.FillSubtleSecondary,
                PressedFill = live ? Tok.FillSubtleTertiary : ColorF.Transparent,
                HoverDurationMs = MotionTok.ControlFaster.DurationMs, HoverEasing = MotionTok.ControlFaster.Easing,
                OnClick = b.OnClick,
            };
            kids[i] = string.IsNullOrEmpty(b.Tip)
                ? hit with { Key = "band:" + i, Grow = share }
                : ToolTip.Wrap(hit, b.Tip, grow: share, showDelayMs: st.TipDelayMs) with { Key = "band:" + i };
        }
        return new BoxEl
        {
            Key = "bands", Direction = 0, Height = st.Height, Grow = 1f, Basis = 0f, AlignSelf = FlexAlign.Stretch,
            MinWidth = 0f, HitTestPassThrough = true, Children = kids,
        };
    }

    /// <summary>The axis row needs no width: half-cell spacers at both ends and one equal cell per tick keep every
    /// caption centred on its tick when the ticks are evenly spaced (80…180 by 20 on 60–200 is the shipped case).</summary>
    static Element Axis(DensityPlotModel m, DensityPlot.Style st)
    {
        var ticks = m.Ticks.Span;
        float span = MathF.Max(1e-6f, m.DomainMax - m.DomainMin);
        var kids = new List<Element>(ticks.Length * 2 + 1);
        float prevEdge = 0f;
        for (int i = 0; i < ticks.Length; i++)
        {
            float t = (Math.Clamp(ticks[i].Value, m.DomainMin, m.DomainMax) - m.DomainMin) / span;
            // Each caption owns the interval halfway to its neighbours; spacers absorb the remainder.
            float nextMid = i + 1 < ticks.Length
                ? (t + (Math.Clamp(ticks[i + 1].Value, m.DomainMin, m.DomainMax) - m.DomainMin) / span) * 0.5f
                : 1f;
            float cellStart = i == 0 ? MathF.Max(0f, 2f * t - nextMid) : prevEdge;
            if (i == 0 && cellStart > 0f) kids.Add(new BoxEl { Grow = cellStart, Basis = 0f, MinWidth = 0f });
            kids.Add(new BoxEl
            {
                Grow = MathF.Max(0.0001f, nextMid - cellStart), Basis = 0f, MinWidth = 0f,
                AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                Children = [new TextEl(ticks[i].Label) { Size = st.AxisFontSize, Color = st.AxisInk, MaxLines = 1 }],
            });
            prevEdge = nextMid;
        }
        return new BoxEl { Key = "axis", Direction = 0, Height = st.AxisFontSize + 2f, MinWidth = 0f, HitTestVisible = false, Children = kids.ToArray() };
    }

    // ── geometry (memoised) ───────────────────────────────────────────────────────────────────────────────────────

    static Geometry Build(DensityPlotModel m, DensityPlot.Style st, float w, int rugMode)
    {
        DensityPlot.GeometryBuilds++;
        float h = st.Height;
        float span = MathF.Max(1e-6f, m.DomainMax - m.DomainMin);
        float X(float v) => (Math.Clamp(v, m.DomainMin, m.DomainMax) - m.DomainMin) / span * w;
        var values = m.Values.Span;
        float rugH = rugMode == 2 ? st.RugTickHeight : 0f;
        float baseY = h - rugH;

        PathData? area = null, line = null;
        if (values.Length > 0 && m.Samples >= 2)
        {
            Span<float> d = m.Samples <= 256 ? stackalloc float[m.Samples] : new float[m.Samples];
            ChartMath.Kde(values, m.DomainMin, m.DomainMax, m.Bandwidth > 0f ? m.Bandwidth : ChartMath.DefaultBandwidth(m.DomainMin, m.DomainMax), d);
            var ba = new PathBuilder();
            var bl = new PathBuilder();
            ba.MoveTo(0f, baseY);
            for (int i = 0; i < d.Length; i++)
            {
                float x = w * i / (d.Length - 1);
                float y = baseY - (baseY - st.TopMargin) * d[i];
                ba.LineTo(x, y);
                if (i == 0) bl.MoveTo(x, y); else bl.LineTo(x, y);
            }
            ba.LineTo(w, baseY);
            ba.Close();
            area = ba.Finish(PathContentEpoch.Mint(), FillRule.NonZero);
            line = bl.Finish(PathContentEpoch.Mint(), FillRule.NonZero);
        }

        PathData? hair = null;
        if (m.Hairlines.Length > 0)
        {
            var b = new PathBuilder();
            foreach (var v in m.Hairlines.Span) { float x = X(v); b.MoveTo(x, 2f); b.LineTo(x, h); }
            hair = b.Finish(PathContentEpoch.Mint(), FillRule.NonZero);
        }

        PathData? marker = null;
        if (m.Marker is { } mv)
        {
            var b = new PathBuilder();
            float x = X(mv);
            b.MoveTo(x, 0f); b.LineTo(x, h);
            marker = b.Finish(PathContentEpoch.Mint(), FillRule.NonZero);
        }

        var rug = new List<(uint, PathData)>();
        if (rugMode != 0 && values.Length > 0)
        {
            var colors = m.ValueColors.Span;
            Span<int> stack = values.Length <= 512 ? stackalloc int[values.Length] : new int[values.Length];
            if (rugMode == 1) ChartMath.RugStacks(values, stack);
            // One path per distinct colour (a PathEl has one fill) — typically a handful, at most a few dozen.
            var groups = new Dictionary<uint, PathBuilder>();
            // Tick mode is DE-DUPLICATED per device pixel column: five hundred tracks at 128 bpm are one tick, so the
            // contour count is bounded by the stage width, never by the list (every contour is tessellated on the UI
            // thread when the geometry is new). One bit per (column, colour) pair.
            int columns = Math.Max(1, (int)MathF.Ceiling(w)) + 1;
            HashSet<long>? seen = rugMode == 2 ? new HashSet<long>() : null;
            for (int i = 0; i < values.Length; i++)
            {
                if (float.IsNaN(values[i])) continue;
                uint argb = i < colors.Length ? colors[i] : 0u;
                float x = X(values[i]);
                if (rugMode == 2)
                {
                    int col = Math.Clamp((int)MathF.Round(x), 0, columns - 1);
                    if (!seen!.Add(((long)argb << 20) | (uint)col)) continue;
                    x = col;
                }
                if (!groups.TryGetValue(argb, out var b)) groups[argb] = b = new PathBuilder();
                if (rugMode == 1)
                {
                    float s = st.DotSize, half = s * 0.5f, r = half * 0.9f;
                    float cy = h - 2f - stack[i] * (s + 0.4f);
                    RoundedSquare(b, x - half, cy - half, s, r);
                }
                else
                {
                    b.MoveTo(x - 0.75f, h - rugH); b.LineTo(x + 0.75f, h - rugH); b.LineTo(x + 0.75f, h); b.LineTo(x - 0.75f, h); b.Close();
                }
            }
            foreach (var kv in groups) rug.Add((kv.Key, kv.Value.Finish(PathContentEpoch.Mint(), FillRule.NonZero)));
        }
        return new Geometry(area, line, hair, marker, rug);
    }

    /// <summary>Opaque-or-not 0xAARRGGBB → <see cref="ColorF"/> (the wire's colour packing).</summary>
    static ColorF Argb(uint argb) => new(((argb >> 16) & 255) / 255f, ((argb >> 8) & 255) / 255f, (argb & 255) / 255f, ((argb >> 24) & 255) / 255f);

    /// <summary>A rounded square as four cubic corners (k = 0.5523 · r) — one closed contour per dot.</summary>
    static void RoundedSquare(PathBuilder b, float x, float y, float s, float r)
    {
        const float K = 0.5523f;
        float k = r * K, x1 = x + s, y1 = y + s;
        b.MoveTo(x + r, y);
        b.LineTo(x1 - r, y);
        b.CubicTo(x1 - r + k, y, x1, y + r - k, x1, y + r);
        b.LineTo(x1, y1 - r);
        b.CubicTo(x1, y1 - r + k, x1 - r + k, y1, x1 - r, y1);
        b.LineTo(x + r, y1);
        b.CubicTo(x + r - k, y1, x, y1 - r + k, x, y1 - r);
        b.LineTo(x, y + r);
        b.CubicTo(x, y + r - k, x + r - k, y, x + r, y);
        b.Close();
    }
}
