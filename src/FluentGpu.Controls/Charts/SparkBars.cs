using System;
using FluentGpu.Animation;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Signals;

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
///
/// <para><see cref="Style.Scrub"/> makes the strip ONE target instead (a touch chart's scrub, as Apple's Health and
/// Fitness charts do): the whole strip takes the pointer, the bar under it is PICKED (its wash and its hover ink) and
/// its tip shows at once in a readout bubble over the strip's top edge, sliding along with the pick and never past the
/// strip's ends. Hover, a press and a drag pick; Left/Right (Home/End) move the pick while the strip has focus; a tap
/// on a bar (press and release on the same bar) and Enter/Space run the picked bar's click. A touch lift drops the pick.
/// The pick lives in the strip, so it re-renders only when the picked bar changes.</para>
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
        /// <summary>A non-accent bar's ink; null = <see cref="Ink"/> at <see cref="RestAlpha"/> (one hue, two weights). A theme that wants
        /// the rest in a neutral tint (a quiet grey under one accent bar) sets it.</summary>
        public ColorF? RestInk { get; init; }
        /// <summary>The bar's corners; null = 2 on the value end, 1 on the baseline (the Fluent bar). <c>CornerRadius4.All(0f)</c> = square.</summary>
        public CornerRadius4? BarCorners { get; init; }
        /// <summary>The column's (the hit target's) corners, which round its lit / hover wash; null = 3.</summary>
        public CornerRadius4? ColumnCorners { get; init; }
        /// <summary>The lit column's wash; null = <c>Tok.AccentSubtle</c>.</summary>
        public ColorF? LitWash { get; init; }
        /// <summary>The lit column's wash under the pointer; null = <c>Tok.AccentSecondary</c>.</summary>
        public ColorF? LitHoverWash { get; init; }
        /// <summary>A column's wash under the pointer; null = <c>Tok.FillSubtleSecondary</c>.</summary>
        public ColorF? HoverWash { get; init; }
        /// <summary>A column's wash while pressed; null = <c>Tok.FillSubtleTertiary</c>.</summary>
        public ColorF? PressedWash { get; init; }
        /// <summary>The strip is one scrub target (see the class remarks) instead of a row of per-bar buttons with tooltips:
        /// a narrow strip whose columns fall under a touch target's size keeps every bar reachable this way.</summary>
        public bool Scrub { get; init; }
        /// <summary>The scrub readout's gap over the strip's top edge.</summary>
        public float ReadoutGap { get; init; } = 4f;
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
        if (st.Scrub) return Embed.Comp(new ScrubProps(model, st), static () => new ScrubStrip()) with { Key = key };
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
            Corners = st.BarCorners ?? new CornerRadius4(2f, 2f, 1f, 1f),
            Fill = bar.Accent || bar.Lit ? st.Ink : st.RestInk ?? st.Ink with { A = st.RestAlpha },
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
            Corners = st.ColumnCorners ?? CornerRadius4.All(3f),
            Fill = bar.Lit ? st.LitWash ?? Tok.AccentSubtle : ColorF.Transparent,
            HoverFill = !live ? ColorF.Transparent : bar.Lit ? st.LitHoverWash ?? Tok.AccentSecondary : st.HoverWash ?? Tok.FillSubtleSecondary,
            PressedFill = live ? st.PressedWash ?? Tok.FillSubtleTertiary : ColorF.Transparent,
            HoverDurationMs = MotionTok.ControlFaster.DurationMs, HoverEasing = MotionTok.ControlFaster.Easing,
            OnClick = bar.OnClick,
            Children = [fill],
        };
        return string.IsNullOrEmpty(bar.Tip)
            ? column with { Key = key, Grow = 1f }
            : ToolTip.Wrap(column, bar.Tip, grow: 1f, showDelayMs: st.TipDelayMs) with { Key = key };
    }

    static ColorF RestFill(in SparkBar bar, Style st) => bar.Accent || bar.Lit ? st.Ink : st.RestInk ?? st.Ink with { A = st.RestAlpha };

    sealed record ScrubProps(SparkBarsModel Model, Style Style);

    /// <summary>The scrub strip (<see cref="Style.Scrub"/>): one focusable target over the columns, which only draw.</summary>
    sealed class ScrubStrip : Component
    {
        Signal<int>? _pick;
        ReadOnlyMemory<SparkBar> _bars;
        int _pressAt = -1;
        bool _down, _touch;
        float _width;

        public override Element Render()
        {
            var (model, st) = UseProps<ScrubProps>();
            var pick = _pick = UseSignal(-1);
            _bars = model.Bars;
            var bars = model.Bars.Span;
            int n = bars.Length;
            int at = pick.Value;
            if (at >= n) at = -1;
            float peak = 1f;
            for (int i = 0; i < n; i++) if (bars[i].Value > peak) peak = bars[i].Value;

            var columns = new Element[n];
            for (int i = 0; i < n; i++)
            {
                ref readonly var bar = ref bars[i];
                bool picked = i == at;
                columns[i] = new BoxEl
                {
                    Key = "bar:" + i, Direction = 1, Justify = FlexJustify.End, Height = st.Height, Grow = 1f, Basis = 0f, MinWidth = 0f,
                    Corners = st.ColumnCorners ?? CornerRadius4.All(3f), HitTestVisible = false,
                    Fill = picked
                        ? bar.Lit ? st.LitHoverWash ?? Tok.AccentSecondary : st.HoverWash ?? Tok.FillSubtleSecondary
                        : bar.Lit ? st.LitWash ?? Tok.AccentSubtle : ColorF.Transparent,
                    Children =
                    [
                        new BoxEl
                        {
                            Height = MathF.Max(st.MinBar, st.Height * MathF.Max(0f, bar.Value) / peak),
                            Corners = st.BarCorners ?? new CornerRadius4(2f, 2f, 1f, 1f),
                            Fill = picked ? st.HoverInk : RestFill(in bar, st),
                        },
                    ],
                };
            }

            return new BoxEl
            {
                ZStack = true, Height = st.Height, Grow = 1f, Basis = 0f, MinWidth = 0f, IsolateLayout = true,
                Role = AutomationRole.Slider, Focusable = true, Cursor = CursorId.Hand, DragYieldsToPan = true,
                FocusVisualMargin = new Edges4(1f, 1f, 1f, 1f),
                OnBoundsChanged = r => _width = r.W,
                OnHoverMove = Hover, OnPointerExit = Exit, OnPointerPressed = Press, OnPointerDown = Sweep, OnDrag = Sweep,
                OnClick = Release, OnKeyDown = Key, OnFocusChanged = Focus,
                Children =
                [
                    new BoxEl
                    {
                        Direction = 0, Gap = st.Gap, Height = st.Height, AlignItems = FlexAlign.End, HitTestVisible = false,
                        Children = columns,
                    },
                    Readout(bars, at, st),
                ],
            };
        }

        /// <summary>The picked bar's tip in the theme's tooltip bubble, over the strip's top edge. The two spacers split the free width in the
        /// pick's proportion, so the bubble slides from the strip's left end (the first bar) to its right end (the last) and always spans its
        /// bar's centre: placed by layout in the pick's own frame, never measured after it.</summary>
        static Element Readout(ReadOnlySpan<SparkBar> bars, int at, Style st)
        {
            var tip = ToolTip.DefaultStyle;
            float line = MathF.Ceiling(tip.TextSize * 4f / 3f);
            float h = tip.Padding.Top + line + tip.Padding.Bottom;
            float lead = at < 0 ? 0f : (at + 0.5f) / bars.Length;
            return new BoxEl
            {
                Key = "spark:readout", Direction = 0, AlignSelf = FlexAlign.Start, Height = h, OffsetY = -(h + st.ReadoutGap),
                HitTestVisible = false, Visible = at >= 0,
                Children =
                [
                    new BoxEl { Grow = lead, Basis = 0f },
                    new BoxEl
                    {
                        Shrink = 0f, Height = h, Padding = tip.Padding, AlignItems = FlexAlign.Center,
                        Fill = tip.Acrylic is null ? tip.Fill : Tok.FillSolidTertiary, Corners = CornerRadius4.All(tip.Radius),
                        BorderColor = tip.Border, BorderWidth = tip.BorderWidth, Shadow = tip.Shadow,
                        Children = [new TextEl(at < 0 ? "" : bars[at].Tip) { Size = tip.TextSize, LineHeight = line, Color = tip.TextColor, MaxLines = 1 }],
                    },
                    new BoxEl { Grow = 1f - lead, Basis = 0f },
                ],
            };
        }

        int At(float x)
        {
            int n = _bars.Length;
            if (n == 0 || _width <= 0f) return -1;
            return Math.Clamp((int)(x / _width * n), 0, n - 1);
        }

        void Pick(int i) { if (_pick is { } p && p.Peek() != i) p.Value = i; }

        void Hover(Point2 p) { if (!_down) Pick(At(p.X)); }

        void Exit() { if (!_down) Pick(-1); }

        void Press(PointerEventArgs e) { _down = true; _touch = e.Kind == PointerKind.Touch; }

        void Sweep(Point2 p)
        {
            int i = At(p.X);
            if (_pressAt < 0) _pressAt = i;
            Pick(i);
        }

        /// <summary>The release (or Enter/Space): a tap runs its bar's click; a scrub that ended on another bar runs none.</summary>
        void Release()
        {
            int at = _pick?.Peek() ?? -1;
            bool pointer = _down;
            bool tap = !pointer || at == _pressAt;
            _down = false;
            _pressAt = -1;
            if (tap && at >= 0 && at < _bars.Length) _bars.Span[at].OnClick?.Invoke();
            if (pointer && _touch) Pick(-1);
        }

        void Key(KeyEventArgs e)
        {
            int n = _bars.Length;
            if (n == 0) return;
            int at = _pick?.Peek() ?? -1;
            if (at < 0) at = Rest();
            switch (e.KeyCode)
            {
                case Keys.Left: Pick(Math.Max(0, at - 1)); e.Handled = true; break;
                case Keys.Right: Pick(Math.Min(n - 1, at + 1)); e.Handled = true; break;
                case Keys.Home: Pick(0); e.Handled = true; break;
                case Keys.End: Pick(n - 1); e.Handled = true; break;
            }
        }

        void Focus(bool on)
        {
            if (!on) { _down = false; _pressAt = -1; Pick(-1); }
            else if (!_down && (_pick?.Peek() ?? -1) < 0) Pick(Rest());
        }

        /// <summary>Where a keyboard pick starts: the lit bar, else the last.</summary>
        int Rest()
        {
            var bars = _bars.Span;
            for (int i = 0; i < bars.Length; i++) if (bars[i].Lit) return i;
            return bars.Length - 1;
        }
    }
}
