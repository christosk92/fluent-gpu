using System;
using FluentGpu.Animation;
using FluentGpu.Dsl;
using FluentGpu.Foundation;

namespace FluentGpu.Controls;

/// <summary>One column of a <see cref="SparkBars"/> strip: its value (scaled against the strip's peak), the tooltip
/// that names it, whether it is the lit LENS (the column the list is currently filtered by), whether it takes the full
/// accent ink (the peak / the newest bucket), and the click that toggles it.</summary>
public readonly record struct SparkBar(float Value, string Tip, bool Lit = false, bool Accent = false, Action? OnClick = null);

/// <summary>The data a <see cref="SparkBars"/> strip draws. Pass a stable array: the model compares by value and an
/// unchanged model is a no-op re-render for the caller's card.</summary>
public sealed record SparkBarsModel(ReadOnlyMemory<SparkBar> Bars);

/// <summary>
/// An N-column bar strip — the "sparkline" of a stat card (the Liked-Songs rail's twelve-week / release-year strips,
/// which this lifts out of the app). Every column is a full-height hit target with the bar sitting on the baseline, so
/// a two-track week is as clickable as a peak; bars scale to the strip's own peak (floor 1) because the strip's job is
/// the SHAPE of the series, not its absolute rate; an empty bucket is a visible <see cref="Style.MinBar"/> baseline,
/// never a gap. Lit columns take the accent-subtle wash a lit list row takes; the peak column takes the full ink,
/// the rest the resting alpha (one hue, two weights — never a second hue).
///
/// <para>Stateless: one canonical <see cref="Create"/> returning the strip. Heights are static (this is a re-render
/// surface, like the card it lives in); a strip that must animate per frame is a <see cref="ProgressBar"/>-style
/// bind, not this control.</para>
/// </summary>
public static class SparkBars
{
    public sealed record Style
    {
        public float Height { get; init; } = 38f;
        public float Gap { get; init; } = 3f;
        /// <summary>A silent bucket is still a visible baseline, never a gap in the strip.</summary>
        public float MinBar { get; init; } = 3f;
        public ColorF Ink { get; init; }
        /// <summary>Alpha of a non-accent bar's ink (the resting weight).</summary>
        public float RestAlpha { get; init; } = 0.38f;
        public ColorF HoverInk { get; init; }
        public float TipDelayMs { get; init; } = float.NaN;
    }

    public static Style? StyleOverride;
    public static Style DefaultStyle => StyleOverride ?? new Style
    {
        Ink = Tok.AccentDefault,
        HoverInk = Tok.AccentTextPrimary,
    };

    /// <summary>The ONE canonical factory: the strip as a row of full-height columns, one per bar, each a Button with
    /// its tooltip, growing equally to fill the width the caller gives.</summary>
    public static Element Create(SparkBarsModel model, Style? style = null, string? key = null)
    {
        var st = style ?? DefaultStyle;
        var bars = model.Bars.Span;
        float peak = 1f;
        for (int i = 0; i < bars.Length; i++) if (bars[i].Value > peak) peak = bars[i].Value;

        var columns = new Element[bars.Length];
        for (int i = 0; i < bars.Length; i++) columns[i] = Column(in bars[i], peak, st, "bar:" + i);

        return new BoxEl
        {
            Key = key, Direction = 0, Gap = st.Gap, Height = st.Height, Grow = 1f, Basis = 0f, MinWidth = 0f,
            AlignItems = FlexAlign.End, HitTestPassThrough = true, Role = AutomationRole.None,
            Children = columns,
        };
    }

    static Element Column(in SparkBar bar, float peak, Style st, string key)
    {
        bool live = bar.OnClick is not null;
        Element fill = new BoxEl
        {
            Height = MathF.Max(st.MinBar, st.Height * MathF.Max(0f, bar.Value) / peak),
            Corners = new CornerRadius4(2f, 2f, 1f, 1f),
            Fill = bar.Accent || bar.Lit ? st.Ink : st.Ink with { A = st.RestAlpha },
            HoverFill = st.HoverInk,
            HoverDurationMs = MotionTok.ControlFaster.DurationMs, HoverEasing = MotionTok.ControlFaster.Easing,
            HitTestVisible = false,
        };
        var column = new BoxEl
        {
            Direction = 1, Justify = FlexJustify.End, Height = st.Height, Basis = 0f, MinWidth = 0f,
            Role = live ? AutomationRole.Button : AutomationRole.None, Focusable = live,
            Cursor = live ? CursorId.Hand : null,
            FocusVisualMargin = new Edges4(1f, 1f, 1f, 1f),
            Corners = CornerRadius4.All(3f),
            Fill = bar.Lit ? Tok.AccentSubtle : ColorF.Transparent,
            HoverFill = !live ? ColorF.Transparent : bar.Lit ? Tok.AccentSecondary : Tok.FillSubtleSecondary,
            PressedFill = live ? Tok.FillSubtleTertiary : ColorF.Transparent,
            HoverDurationMs = MotionTok.ControlFaster.DurationMs, HoverEasing = MotionTok.ControlFaster.Easing,
            OnClick = bar.OnClick,
            Children = [fill],
        };
        return string.IsNullOrEmpty(bar.Tip)
            ? column with { Key = key, Grow = 1f }
            : ToolTip.Wrap(column, bar.Tip, grow: 1f, showDelayMs: st.TipDelayMs) with { Key = key };
    }
}
