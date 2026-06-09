using System.Globalization;
using FluentGpu.Foundation;
using FluentGpu.Dsl;
using FluentGpu.Hooks;
using FluentGpu.Scene;
using FluentGpu.Signals;

namespace FluentGpu.Controls;

/// <summary>
/// A WinUI 3 Slider. Geometry (Slider_themeresources.xaml): a 4px rail (SliderTrackFill = ControlStrongFillColorDefault,
/// radius 2) with an accent value-fill, and the Fluent thumb — a STATIC 22px ring (the 18px Thumb part + its Border's
/// −2 margin, lines 169-172 + 198) of ControlSolidFillColorDefault with a 1px ControlElevationBorder, holding the only
/// animated part: the 12px accent inner dot whose storyboards scale it 0.86 at rest / 1.167 on hover / 0.71 on press /
/// 1.167 disabled (lines 204-254). Value-fill and inner dot recolor with the CONTROL-wide PointerOver/Pressed states
/// (AccentFillColorDefault → Secondary → Tertiary; lines 14-17, 24-27).
/// <list type="bullet">
/// <item><see cref="Create"/>/<see cref="Bind"/> — the lightweight 0..1 scrub primitives (media seek/volume). Keyboard,
///   focus visuals and the optional header are included; the value tooltip is not (see <see cref="Ranged"/>).</item>
/// <item><see cref="Ranged"/> — the full WinUI-parity control over <see cref="Options"/>: arbitrary range, step
///   snapping, ticks (incl. inline), Header, SmallChange/LargeChange keyboard, and the IsThumbToolTipEnabled
///   "disambiguation UI" value tooltip with a ThumbToolTipValueConverter-equivalent formatter.</item>
/// </list>
/// Keyboard (KeyPress::Slider::KeyDown, SliderKeyProcess.h:28-71): Left/Down decrease and Right/Up increase by
/// SmallChange, Home/End jump to Minimum/Maximum; PageUp/PageDown move by LargeChange (not in XAML's KeyPress::Slider —
/// kept per the parity contract, riding the same Slider::Step math, Slider_Partial.cpp:1713-1783). RTL note: WinUI
/// inverts Left/Right by (FlowDirection==RightToLeft) ^ IsDirectionReversed (SliderKeyProcess.h:26); the engine has no
/// FlowDirection yet — the LTR mapping ships now, the inversion lands with the Wave-6 RTL sweep.
/// </summary>
public static partial class Slider
{
    public sealed record Style
    {
        public float TrackHeight { get; init; } = 4f;                    // SliderTrackThemeHeight (Slider_themeresources.xaml:6)
        public float TrackCornerRadius { get; init; } = 2f;             // SliderTrackCornerRadius (line 162)
        public ColorF RailFill { get; init; }                          // SliderTrackFill = ControlStrongFillColorDefault (line 20)
        public ColorF RailFillDisabled { get; init; }                  // SliderTrackFillDisabled = ControlStrongFillColorDisabled (line 23)
        public ColorF ValueFill { get; init; }                         // SliderTrackValueFill = AccentFillColorDefault (line 24)
        public ColorF ValueFillPointerOver { get; init; }              // SliderTrackValueFillPointerOver = AccentFillColorSecondary (line 25)
        public ColorF ValueFillPressed { get; init; }                  // SliderTrackValueFillPressed = AccentFillColorTertiary (line 26)
        public ColorF ValueFillDisabled { get; init; }                 // SliderTrackValueFillDisabled = AccentFillColorDisabled (line 27)
        /// <summary>The thumb's VISUAL diameter: the 18px Thumb part (SliderHorizontalThumbWidth/Height, lines 169-170)
        /// + the template Border's Margin="-2" (line 198) ⇒ a 22px ring on screen.</summary>
        public float ThumbRingDiameter { get; init; } = 22f;
        public float InnerThumbDiameter { get; init; } = 12f;          // SliderInnerThumbWidth/Height (lines 166, 173)
        public ColorF ThumbRing { get; init; }                         // SliderOuterThumbBackground = ControlSolidFillColorDefault (line 19); NO per-state variants
        public ColorF ThumbFill { get; init; }                         // SliderThumbBackground = AccentFillColorDefault (line 14)
        public ColorF ThumbFillPointerOver { get; init; }              // SliderThumbBackgroundPointerOver = AccentFillColorSecondary (line 15)
        public ColorF ThumbFillPressed { get; init; }                  // SliderThumbBackgroundPressed = AccentFillColorTertiary (line 16)
        public ColorF ThumbFillDisabled { get; init; }                 // SliderThumbBackgroundDisabled = AccentFillColorDisabled (line 17)
        public GradientSpec? ThumbBorder { get; init; }                // SliderThumbBorderBrush = ControlElevationBorderBrush (line 18)
        public float ThumbBorderWidth { get; init; } = 1f;             // SliderThumbStyle BorderThickness=1 (line 193)
        public float ThumbCornerRadius { get; init; } = 10f;           // SliderThumbCornerRadius (line 163) — a rounded rect on the 22px ring, not a circle
        // Inner-dot storyboard endpoints (the ONLY animated thumb part; the outer Border is static, lines 198-203).
        public float InnerRestScale { get; init; } = 0.86f;            // Normal state → 0.86 ("relative scale from 14px to 12px", lines 206-217) ⇒ 10.32px on screen
        public float InnerHoverScale { get; init; } = 1.167f;          // PointerOver → 1.167 (12→14px, lines 218-229)
        public float InnerPressScale { get; init; } = 0.71f;           // Pressed → 0.71 (14→8.5px, lines 230-241)
        public float InnerDisabledScale { get; init; } = 1.167f;       // Disabled → 1.167 (lines 242-253)
        public ColorF TickFill { get; init; }                          // SliderTickBarFill = ControlStrongFillColorDefault (line 30)
        public ColorF TickFillDisabled { get; init; }                  // SliderTickBarFillDisabled = ControlStrongFillColorDisabled (line 31)
        public ColorF InlineTickFill { get; init; }                    // SliderInlineTickBarFill = ControlFillColorInputActive (line 32)
    }

    public static Style? StyleOverride;
    public static Style DefaultStyle => StyleOverride ?? new Style
    {
        RailFill = Tok.FillControlStrong,
        RailFillDisabled = Tok.FillControlStrongDisabled,
        ValueFill = Tok.AccentDefault,
        ValueFillPointerOver = Tok.AccentSecondary,
        ValueFillPressed = Tok.AccentTertiary,
        ValueFillDisabled = Tok.AccentDisabled,
        ThumbRing = Tok.FillControlSolid,
        ThumbFill = Tok.AccentDefault,
        ThumbFillPointerOver = Tok.AccentSecondary,
        ThumbFillPressed = Tok.AccentTertiary,
        ThumbFillDisabled = Tok.AccentDisabled,
        ThumbBorder = Tok.ControlElevationBorder,
        TickFill = Tok.FillControlStrong,
        TickFillDisabled = Tok.FillControlStrongDisabled,
        InlineTickFill = Tok.FillControlInputActive,
    };

    /// <summary>Where tick marks render — WinUI <c>TickPlacement</c> (visibility mapping: Slider_Partial.cpp:2248-2303).
    /// WinUI's default is <see cref="Inline"/> (marks inside the rail, SliderInlineTickBarFill).</summary>
    public enum TickPlacement : byte { None, TopLeft, BottomRight, Outside, Inline }

    /// <summary>Extended slider configuration: range, step snapping, ticks, orientation, header, keyboard step sizes and
    /// the thumb value tooltip ("disambiguation UI").</summary>
    public sealed record Options
    {
        public float Min { get; init; } = 0f;
        public float Max { get; init; } = 1f;
        /// <summary>Pointer-drag snap grid — WinUI StepFrequency (MoveThumbToPoint snaps the value to the closest
        /// multiple, Slider_Partial.cpp:2012-2029). 0 = continuous.</summary>
        public float Step { get; init; }
        public float TickFrequency { get; init; }   // 0 = no ticks
        /// <summary>WinUI TickPlacement (default Inline — ticks drawn inside the rail at SliderInlineTickBarFill;
        /// Slider_Partial.cpp:2248-2303 toggles the matching TickBar parts).</summary>
        public TickPlacement TickPlacement { get; init; } = TickPlacement.Inline;
        public bool Vertical { get; init; }
        /// <summary>Header text above the control (WinUI Slider.Header → HeaderContentPresenter, row 0 of the template,
        /// Slider_themeresources.xaml:396). Null/empty = no header row.</summary>
        public string? Header { get; init; }
        /// <summary>Arrow-key step — WinUI SmallChange. 0 (default) = auto: (Max−Min)/100, which reproduces WinUI's
        /// absolute default SmallChange=1 on its default 0–100 range (SLIDER_DEFAULT_SMALL_CHANGE/MAXIMUM,
        /// Slider_Partial.h:13-14) while staying sane for this type's 0..1 default range.</summary>
        public float SmallChange { get; init; }
        /// <summary>PageUp/PageDown step — WinUI LargeChange. 0 (default) = auto: (Max−Min)/10 (WinUI's absolute default
        /// LargeChange=10 on the default 0–100 range, Slider_Partial.h:13,15).</summary>
        public float LargeChange { get; init; }
        /// <summary>Show the value tooltip over the thumb while pressing/dragging, on keyboard navigation, and on
        /// hover-over-thumb (the WinUI "disambiguation UI"). WinUI default TRUE (DependencyProperty.cpp:305 — the
        /// boolean-TRUE default block).</summary>
        public bool IsThumbToolTipEnabled { get; init; } = true;
        /// <summary>Custom value→text for the thumb tooltip — the WinUI ThumbToolTipValueConverter equivalent. Null =
        /// the default converter (decimal places derived from <see cref="Step"/>, capped at 4 —
        /// DefaultDisambiguationUIConverter, Slider_Partial.cpp:1859-1936).</summary>
        public Func<float, string>? ThumbToolTipValueConverter { get; init; }
    }

    // ── WinUI keyboard semantics (shared by Create/Bind/Ranged) ─────────────────────────────────────────────────────

    /// <summary>
    /// Route one key per KeyPress::Slider::KeyDown (SliderKeyProcess.h:28-71): Left/Down = −SmallChange, Right/Up =
    /// +SmallChange, Home/End = Minimum/Maximum (lines 60-71); PageDown/PageUp = ∓LargeChange (XAML's KeyPress::Slider
    /// has no PageUp/PageDown — added per the parity contract on the same Slider::Step math). Steps snap to the closest
    /// stepDelta multiple via <see cref="ClosestStep"/>. Returns true (and sets <c>e.Handled</c>) when consumed.
    /// RTL inversion ((FlowDirection==RightToLeft) ^ IsDirectionReversed, SliderKeyProcess.h:26) is deferred to the
    /// Wave-6 RTL sweep — the engine has no FlowDirection yet.
    /// </summary>
    internal static bool HandleKey(KeyEventArgs e, float value, float min, float max, float smallChange, float largeChange, out float next)
    {
        next = value;
        switch (e.KeyCode)
        {
            case Keys.Left:                                                       // SliderKeyProcess.h:28-35 (LTR: backward)
            case Keys.Down:                                                       // SliderKeyProcess.h:52-59 (Down = backward)
                next = StepValue(value, min, max, smallChange, forward: false); break;
            case Keys.Right:                                                      // SliderKeyProcess.h:36-43 (LTR: forward)
            case Keys.Up:                                                         // SliderKeyProcess.h:44-51 (Up = forward)
                next = StepValue(value, min, max, smallChange, forward: true); break;
            case Keys.PageDown:
                next = StepValue(value, min, max, largeChange, forward: false); break;
            case Keys.PageUp:
                next = StepValue(value, min, max, largeChange, forward: true); break;
            case Keys.Home: next = min; break;                                    // SliderKeyProcess.h:60-65
            case Keys.End: next = max; break;                                     // SliderKeyProcess.h:66-71
            default: return false;
        }
        e.Handled = true;
        return true;
    }

    /// <summary>Slider::Step (Slider_Partial.cpp:1713-1783): move one <paramref name="stepDelta"/> in
    /// <paramref name="forward"/> direction, snapping to the closest stepDelta multiple — with the max-end correction
    /// (lines 1763-1773: stepping back from a Maximum that is not on the grid goes to the last multiple before it).</summary>
    internal static float StepValue(float value, float min, float max, float stepDelta, bool forward)
    {
        if (stepDelta <= 0f) return value;
        float closest;
        float steps = value / stepDelta;
        bool offGrid = MathF.Abs(steps - MathF.Round(steps)) > 1e-4f;
        if (!forward && MathF.Abs(value - max) < 1e-4f && offGrid)
            closest = MathF.Floor(steps) * stepDelta;                             // Slider_Partial.cpp:1768-1773
        else
            closest = ClosestStep(stepDelta, forward ? value + stepDelta : value - stepDelta, min, max);
        return Math.Clamp(closest, min, max);                                     // put_Value coercion (RangeBase)
    }

    /// <summary>Slider::GetClosestStep (Slider_Partial.cpp:1788-1819): the nearest stepDelta multiple, with the
    /// neighbors clamped into [min,max] before comparing.</summary>
    internal static float ClosestStep(float stepDelta, float fromValue, float min, float max)
    {
        float numSteps = fromValue / stepDelta;
        float nextStep = MathF.Min(max, MathF.Ceiling(numSteps) * stepDelta);
        float prevStep = MathF.Max(min, MathF.Floor(numSteps) * stepDelta);
        return (nextStep - fromValue) < (fromValue - prevStep) ? nextStep : prevStep;
    }

    /// <summary>
    /// The default thumb-tooltip text — DefaultDisambiguationUIConverter (Slider_Partial.cpp:1859-1936): the number of
    /// decimal places follows StepFrequency's mantissa, capped at 4; the value is rounded to that digit. A continuous
    /// slider (<paramref name="step"/> ≤ 0) formats like WinUI's default StepFrequency = 1 → no decimals.
    /// </summary>
    public static string FormatThumbValue(float value, float step)
    {
        float s = step <= 0f ? 1f : step;
        int digits = 0;
        while (digits < 4 && MathF.Abs(s - MathF.Round(s)) > 1e-5f) { digits++; s *= 10f; }
        string fmt = digits switch { 0 => "F0", 1 => "F1", 2 => "F2", 3 => "F3", _ => "F4" };
        return MathF.Round(value, digits).ToString(fmt, CultureInfo.InvariantCulture);
    }

    // ── shared template parts ───────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The WinUI thumb: a STATIC 22px ring (SliderOuterThumbBackground + 1px ControlElevationBorder; the template's
    /// outer Border carries no state storyboards — Slider_themeresources.xaml:198-203) around the 12px inner accent dot,
    /// the only animated part. The dot rests at the Normal-state 0.86 scale (10.32px on screen) and eases to 1.167 on
    /// hover / 0.71 on press over ControlNormalAnimationDuration = 250ms (Common_themeresources_any.xaml:603) with
    /// ControlFastOutSlowInKeySpline = cubic-bezier(0,0,0,1) (line 602 — the engine's <c>Easing.FluentPopOpen</c>, the
    /// default Hover/PressEasing). Disabled pins a static 1.167 (the Disabled storyboard, lines 242-253) — interaction
    /// progress can't advance on a disabled, non-hit-testing control. Engine note: the dot's hover/press progress comes
    /// from the TRACK's interaction row (nearest interactive ancestor) — control-wide pointer-over, which is exactly
    /// what drives the WinUI dot COLOR (control CommonStates, lines 285-289/310-314); WinUI's dot SCALE is thumb-local
    /// hover, not reachable while drag handlers must stay on the track (documented engine concession).
    /// </summary>
    private static BoxEl BuildThumb(Style s, bool isEnabled, Action<NodeHandle>? onRealized, Func<Affine2D>? transformBind)
    {
        ColorF dot = isEnabled ? s.ThumbFill : s.ThumbFillDisabled;
        ColorF dotHover = isEnabled ? s.ThumbFillPointerOver : s.ThumbFillDisabled;   // SliderThumbBackgroundPointerOver (line 15)
        ColorF dotPress = isEnabled ? s.ThumbFillPressed : s.ThumbFillDisabled;       // SliderThumbBackgroundPressed (line 16)
        float rest = isEnabled ? s.InnerRestScale : s.InnerDisabledScale;
        return new BoxEl
        {
            Width = s.ThumbRingDiameter, Height = s.ThumbRingDiameter,
            AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            Corners = CornerRadius4.All(s.ThumbCornerRadius),
            // Pin hover/press to the resting ring color: SliderOuterThumbBackground has NO per-state variants (line 19),
            // and an unset HoverFill would otherwise trip the recorder's auto-lighten fallback.
            Fill = s.ThumbRing, HoverFill = s.ThumbRing, PressedFill = s.ThumbRing,
            BorderBrush = s.ThumbBorder, BorderWidth = s.ThumbBorderWidth,
            OnRealized = onRealized,
            TransformBind = transformBind,
            Children =
            [
                new BoxEl
                {
                    Width = s.InnerThumbDiameter, Height = s.InnerThumbDiameter,
                    Corners = Radii.Circle(s.InnerThumbDiameter),
                    Fill = dot, HoverFill = dotHover, PressedFill = dotPress,
                    ScaleX = rest, ScaleY = rest,                                  // Normal 0.86 / Disabled 1.167 static base
                    HoverScale = isEnabled ? s.InnerHoverScale / s.InnerRestScale : 1f,   // net 1.167 (14px) on hover
                    PressScale = isEnabled ? s.InnerPressScale / s.InnerRestScale : 1f,   // net 0.71 (8.5px) on press
                    HoverDurationMs = 250f, PressDurationMs = 250f,                // ControlNormalAnimationDuration (Common_themeresources_any.xaml:603)
                },
            ],
        };
    }

    /// <summary>Optional header row above the control body — WinUI HeaderContentPresenter
    /// (Slider_themeresources.xaml:396): SliderHeaderForeground = TextFillColorPrimary (line 28) /
    /// SliderHeaderForegroundDisabled = TextFillColorDisabled (line 29, control-chosen at build time), FontWeight Normal
    /// (line 9), TextWrapping=Wrap, Margin = SliderTopHeaderMargin 0,0,0,4 (line 161).</summary>
    private static BoxEl WithHeader(string? header, bool isEnabled, BoxEl body)
        => string.IsNullOrEmpty(header) ? body : new BoxEl
        {
            Direction = 1,
            Children =
            [
                new TextEl(header)
                {
                    Size = 14f,                                       // ControlContentThemeFontSize (style setter, line 181)
                    Color = isEnabled ? Tok.TextPrimary : Tok.TextDisabled,
                    Wrap = TextWrap.Wrap,
                    Margin = new Edges4(0f, 0f, 0f, 4f),
                },
                body,
            ],
        };

    // ── the full WinUI-parity control (Options-driven; hosts the value tooltip) ─────────────────────────────────────

    /// <summary>
    /// A slider over an arbitrary <see cref="Options.Min"/>..<see cref="Options.Max"/> range, with step snapping, tick
    /// marks (incl. inline), vertical orientation, Header, the full WinUI keyboard map and the IsThumbToolTipEnabled
    /// value tooltip. <paramref name="value"/> is in range units; <paramref name="length"/> is the track length (px) and
    /// <paramref name="thickness"/> the cross size (WinUI SliderHorizontalHeight/SliderVerticalWidth = 32,
    /// Slider_themeresources.xaml:167-168). Controlled: the caller owns the value.
    /// Internally a stateful component (the tooltip's overlay lifetime spans re-renders); props are pushed through a
    /// context provider — the engine's sanctioned prop channel for embedded components.
    /// </summary>
    public static BoxEl Ranged(float value, Action<float> onChange, Options o, float length = 220f, float thickness = 32f, Style? style = null, bool isEnabled = true)
        => new()
        {
            Direction = 1,
            Children =
            [
                Ctx.Provide(
                    RangedSliderProps.Channel,
                    new RangedSliderProps(value, onChange, o, length, thickness, style ?? DefaultStyle, isEnabled),
                    Embed.Comp(() => new RangedSlider())),
            ],
        };

    // ── the lightweight 0..1 primitives (static trees; no tooltip — see the class doc) ─────────────────────────────

    /// <summary>
    /// The minimal 0..1 controlled slider (media seek/volume). Press-to-seek + drag-to-scrub with the raw
    /// <c>x/width</c> mapping, full keyboard (SmallChange 0.01 / LargeChange 0.1 — WinUI's defaults 1/10 re-based from
    /// its 0–100 default range, Slider_Partial.h:13-15), focus ring at FocusVisualMargin −7,0,−7,0
    /// (Slider_themeresources.xaml:184), optional <paramref name="header"/>. No value tooltip (that lives on
    /// <see cref="Ranged"/>, the WinUI-parity surface).
    /// </summary>
    public static BoxEl Create(float value, Action<float> onChange, float width = 200f, float height = 24f, Style? style = null, bool isEnabled = true, string? header = null)
    {
        var s = style ?? DefaultStyle;
        float v = Math.Clamp(value, 0f, 1f);
        float ringD = s.ThumbRingDiameter, half = ringD * 0.5f;
        ColorF railFill = isEnabled ? s.RailFill : s.RailFillDisabled;
        ColorF valueFill = isEnabled ? s.ValueFill : s.ValueFillDisabled;
        ColorF valueHover = isEnabled ? s.ValueFillPointerOver : s.ValueFillDisabled;
        ColorF valuePress = isEnabled ? s.ValueFillPressed : s.ValueFillDisabled;
        void Set(Point2 p) => onChange(Math.Clamp(p.X / MathF.Max(width, 1f), 0f, 1f));   // track-relative (handlers on the outer box)

        void OnKey(KeyEventArgs e)
        {
            // SmallChange 0.01 / LargeChange 0.1: WinUI's 1 / 10 defaults are absolute on its default 0–100 range
            // (Slider_Partial.h:13-15) — identical fractions of this fixed 0..1 range.
            if (HandleKey(e, Math.Clamp(value, 0f, 1f), 0f, 1f, 0.01f, 0.1f, out float next) && next != value)
                onChange(next);
        }

        var track = new BoxEl
        {
            Width = width, Height = height, ZStack = true, Role = AutomationRole.Slider, IsEnabled = isEnabled,
            Focusable = true,
            FocusVisualMargin = new Edges4(-7f, 0f, -7f, 0f),   // Slider FocusVisualMargin (Slider_themeresources.xaml:184)
            OnPointerDown = Set, OnDrag = Set,   // press-to-seek + drag-to-scrub, anywhere on the track
            OnKeyDown = OnKey,
            Children =
            [
                // rail + value fill, vertically centred (composited OffsetY; non-interactive so the hit rect is irrelevant)
                new BoxEl
                {
                    ZStack = true, Width = width, Height = height,
                    Children =
                    [
                        // Rail: PointerOver/Pressed == the resting fill (SliderTrackFillPointerOver/Pressed = SliderTrackFill,
                        // lines 21-22) — pinned explicitly so the recorder's auto-lighten fallback can't drift it.
                        new BoxEl { Width = width, Height = s.TrackHeight, Corners = CornerRadius4.All(s.TrackCornerRadius), Fill = railFill, HoverFill = railFill, PressedFill = railFill, OffsetY = (height - s.TrackHeight) * 0.5f },
                        // Value fill: Accent Default → Secondary (hover) → Tertiary (press) with the CONTROL-wide state
                        // (SliderTrackValueFill*, lines 24-26), eased by the track's interaction progress.
                        new BoxEl { Width = v * width, Height = s.TrackHeight, Corners = CornerRadius4.All(s.TrackCornerRadius), Fill = valueFill, HoverFill = valueHover, PressedFill = valuePress, OffsetY = (height - s.TrackHeight) * 0.5f },
                    ],
                },
                // thumb, layout-positioned by a leading spacer (so it tracks the value) + vertically centred by the row.
                // It is NOT interactive (drag stays on the track); only the inner dot animates (see BuildThumb).
                new BoxEl
                {
                    Direction = 0, Width = width, Height = height, AlignItems = FlexAlign.Center,
                    Children =
                    [
                        new BoxEl { Width = MathF.Max(0f, MathF.Min(v * width - half, width - ringD)) },   // ring centred at the value, clamped inside the track
                        BuildThumb(s, isEnabled, onRealized: null, transformBind: null),
                    ],
                },
            ],
        };
        return WithHeader(header, isEnabled, track);
    }

    /// <summary>
    /// Signals-native slider (the "even better than React" path): the value is a <see cref="FloatSignal"/> bound straight
    /// to the value-fill's composited ScaleX and the thumb's composited OffsetX. A drag writes the signal, which updates
    /// exactly those two node transforms on the compositor fast path — <b>zero render, zero reconcile, zero relayout</b>
    /// per pointer-move (the slider-tank fix). <paramref name="onChange"/> is invoked for side effects (e.g. a value
    /// readout that takes its own scoped re-render); pass null for a purely-visual scrub. Keyboard steps write the
    /// signal the same compositor-only way. No value tooltip: opening/observing one requires a re-render path, which
    /// would break this overload's zero-re-render contract — use <see cref="Ranged"/> for the WinUI-parity surface.
    /// </summary>
    public static BoxEl Bind(FloatSignal value, Action<float>? onChange = null, float width = 200f, float height = 24f, Style? style = null, bool isEnabled = true, string? header = null)
    {
        var s = style ?? DefaultStyle;
        float ringD = s.ThumbRingDiameter, half = ringD * 0.5f;
        ColorF railFill = isEnabled ? s.RailFill : s.RailFillDisabled;
        ColorF valueFill = isEnabled ? s.ValueFill : s.ValueFillDisabled;
        ColorF valueHover = isEnabled ? s.ValueFillPointerOver : s.ValueFillDisabled;
        ColorF valuePress = isEnabled ? s.ValueFillPressed : s.ValueFillDisabled;
        void Set(Point2 p) { float v = Math.Clamp(p.X / MathF.Max(width, 1f), 0f, 1f); value.Value = v; onChange?.Invoke(v); }

        void OnKey(KeyEventArgs e)
        {
            float cur = Math.Clamp(value.Peek(), 0f, 1f);
            // SmallChange 0.01 / LargeChange 0.1 — WinUI's 1/10 defaults re-based from the 0–100 default range
            // (Slider_Partial.h:13-15) onto this fixed 0..1 range.
            if (HandleKey(e, cur, 0f, 1f, 0.01f, 0.1f, out float next) && next != cur)
            {
                value.Value = next;        // compositor-only: the bound fill/thumb transforms update, no re-render
                onChange?.Invoke(next);
            }
        }

        return WithHeader(header, isEnabled, new BoxEl
        {
            Width = width, Height = height, ZStack = true, Role = AutomationRole.Slider, IsEnabled = isEnabled,
            Focusable = true,
            FocusVisualMargin = new Edges4(-7f, 0f, -7f, 0f),   // Slider_themeresources.xaml:184
            OnPointerDown = Set, OnDrag = Set,
            OnKeyDown = OnKey,
            Children =
            [
                // rail + value fill (full width, grown from the left by a composited ScaleX bound to the signal — no layout)
                new BoxEl
                {
                    ZStack = true, Width = width, Height = height,
                    Children =
                    [
                        new BoxEl { Width = width, Height = s.TrackHeight, Corners = CornerRadius4.All(s.TrackCornerRadius), Fill = railFill, HoverFill = railFill, PressedFill = railFill, OffsetY = (height - s.TrackHeight) * 0.5f },
                        new BoxEl
                        {
                            Width = width, Height = s.TrackHeight, Corners = CornerRadius4.All(s.TrackCornerRadius),
                            Fill = valueFill, HoverFill = valueHover, PressedFill = valuePress,   // SliderTrackValueFill* (lines 24-26)
                            OffsetY = (height - s.TrackHeight) * 0.5f,
                            TransformBind = () =>
                            {
                                float v = MathF.Max(Math.Clamp(value.Value, 0f, 1f), 1e-4f);
                                return Affine2D.Translation(-width * (1f - v) * 0.5f, 0f).Multiply(Affine2D.Scale(v, 1f));   // grow from the left
                            },
                        },
                    ],
                },
                // thumb at x=0, slid to the value by a composited OffsetX bound to the signal (no leading spacer, no layout)
                new BoxEl
                {
                    Direction = 0, Width = width, Height = height, AlignItems = FlexAlign.Center,
                    Children =
                    [
                        BuildThumb(s, isEnabled,
                            onRealized: null,
                            transformBind: () => Affine2D.Translation(Math.Clamp(value.Value * width - half, 0f, MathF.Max(0f, width - ringD)), 0f)),
                    ],
                },
            ],
        });
    }
}

/// <summary>
/// Props for the full <see cref="Slider.Ranged"/> control, pushed through a context provider on every parent re-render
/// (the reconciler writes the provider's signal on update — the sanctioned prop channel for autonomous embedded
/// components). The component re-renders granularly when the record changes.
/// </summary>
internal sealed record RangedSliderProps(
    float Value, Action<float> OnChange, Slider.Options O, float Length, float Thickness, Slider.Style S, bool IsEnabled)
{
    public static readonly Context<RangedSliderProps?> Channel = new(null);
}

/// <summary>
/// The stateful body of <see cref="Slider.Ranged"/>: the WinUI track/thumb template plus the cross-render state the
/// static factories can't hold — the thumb value-tooltip overlay (WinUI's "disambiguation UI"). Tooltip behavior is a
/// faithful port of Slider_Partial.cpp:
/// <list type="bullet">
/// <item>Press (mouse or touch) shows it immediately and the value scrubs live (OnPointerPressed →
///   UpdateThumbToolTipVisibility(TRUE), lines 478-543); it stays up while pressed even when the pointer leaves the
///   control (lines 413-425/454-472).</item>
/// <item>Release hides it (PerformPointerUpAction, lines 645-659) — including a release outside the control, which the
///   dispatcher delivers to the captured drag target's click handler.</item>
/// <item>A handled navigation key shows it (keyboard mode, lines 296-311); getting KEYBOARD focus shows it
///   (OnGotFocus, lines 236-250); losing focus hides it (OnLostFocus, lines 256-268).</item>
/// <item>Hovering the THUMB opens it after the standard ToolTipService initial delay — the WinUI thumb carries a real
///   ToolTip part (SetDefaultThumbToolTip, lines 2037-2120), so hover-open rides ToolTipService timing
///   (<see cref="ToolTip.MouseShowDelayMs"/>).</item>
/// </list>
/// </summary>
internal sealed class RangedSlider : Component
{
    public override Element Render()
    {
        var props = UseContext(RangedSliderProps.Channel);
        if (props is null) return new BoxEl();   // defensive: the factory always wraps in a provider

        var o = props.O;
        var s = props.S;
        bool isEnabled = props.IsEnabled;
        float length = props.Length, thickness = props.Thickness;
        float min = o.Min;
        float range = MathF.Max(o.Max - o.Min, 1e-5f);
        float max = o.Min + range;
        float t = Math.Clamp((props.Value - min) / range, 0f, 1f);
        float ringD = s.ThumbRingDiameter, half = ringD * 0.5f;
        // SmallChange/LargeChange: 0 = auto — (range)/100 and (range)/10, reproducing WinUI's absolute defaults 1/10 on
        // its default 0–100 range (SLIDER_DEFAULT_SMALL_CHANGE/LARGE_CHANGE/MAXIMUM, Slider_Partial.h:13-15).
        float smallChange = o.SmallChange > 0f ? o.SmallChange : range / 100f;
        float largeChange = o.LargeChange > 0f ? o.LargeChange : range / 10f;

        ColorF railFill = isEnabled ? s.RailFill : s.RailFillDisabled;
        ColorF valueFill = isEnabled ? s.ValueFill : s.ValueFillDisabled;
        ColorF valueHover = isEnabled ? s.ValueFillPointerOver : s.ValueFillDisabled;   // SliderTrackValueFillPointerOver (line 25)
        ColorF valuePress = isEnabled ? s.ValueFillPressed : s.ValueFillDisabled;       // SliderTrackValueFillPressed (line 26)
        ColorF tickFill = isEnabled ? s.TickFill : s.TickFillDisabled;                  // SliderTickBarFill/Disabled (lines 30-31)

        bool tooltipEnabled = o.IsThumbToolTipEnabled && isEnabled;

        // ── cross-render state ──
        var svc = UseContext(Overlay.Service);
        var trackRef = UseRef<NodeHandle>(default);
        var thumbRef = UseRef<NodeHandle>(default);
        var tip = UseRef<OverlayHandle?>(null);
        var tipPhase = UseSignal(0);                       // 0 idle · 1 hover-arm (initial show delay) · 2 open
        var tipValue = UseFloatSignal(props.Value);        // live value for the bubble's TextBind (granular, no re-render)
        tipValue.Value = props.Value;                      // follow programmatic/keyboard updates

        string Format(float v) => o.ThumbToolTipValueConverter is { } conv ? conv(v) : Slider.FormatThumbValue(v, o.Step);

        // WinUI thumb tooltip chrome: created in code by SetDefaultThumbToolTip (Slider_Partial.cpp:2037-2120) — the
        // standard ToolTip surface with SLIDER_TOOLTIP_PADDING 8,3,8,5 and SLIDER_TOOLTIP_DEFAULT_FONT_SIZE 15
        // (Slider_Partial.h:16-20); placed PlacementMode_Top (horizontal) / Left (vertical), centered on the thumb.
        Element TipBubble() => new BoxEl
        {
            Fill = ColorF.Transparent,
            Acrylic = AcrylicSpec.Flyout,
            BorderColor = Tok.StrokeFlyoutDefault,
            BorderWidth = 1f,
            Corners = Radii.ControlAll,
            Shadow = Elevation.Flyout,
            Padding = new Edges4(8f, 3f, 8f, 5f),          // SLIDER_TOOLTIP_PADDING_* (Slider_Partial.h:17-20)
            Children =
            [
                new TextEl(Format(tipValue.Peek()))
                {
                    TextBind = () => Format(tipValue.Value),   // live scrub readout — signal-bound, no bubble re-render
                    Size = 15f,                                // SLIDER_TOOLTIP_DEFAULT_FONT_SIZE (Slider_Partial.h:16)
                    Color = Tok.TextPrimary,
                },
            ],
        };

        void OpenTip()
        {
            if (!tooltipEnabled) return;
            if (tip.Value is { IsOpen: true }) { if (tipPhase.Peek() != 2) tipPhase.Value = 2; return; }
            tipPhase.Value = 2;
            tip.Value = svc.Open(
                () => thumbRef.Value,
                TipBubble,
                o.Vertical ? FlyoutPlacement.Left : FlyoutPlacement.Top,   // PlacementMode Top / Left (Slider_Partial.cpp:2054, 2080-2096)
                new PopupOptions(FocusTrap: false, DismissBehavior: DismissBehavior.None, Chrome: PopupChrome.Raw));
        }

        void CloseTip()
        {
            if (tip.Value is { IsOpen: true } h) h.Close();
            tip.Value = null;
            if (tipPhase.Peek() != 0) tipPhase.Value = 0;
        }

        bool TrackPressed()
        {
            var sc = Context.Scene;
            return sc is not null && !trackRef.Value.IsNull && sc.IsLive(trackRef.Value)
                   && (sc.Flags(trackRef.Value) & NodeFlags.Pressed) != 0;
        }

        // ── value writes ──
        void Commit(float v)
        {
            tipValue.Value = v;
            props.OnChange(v);
        }

        void Set(Point2 p)
        {
            float raw = Math.Clamp(o.Vertical ? 1f - p.Y / MathF.Max(length, 1f) : p.X / MathF.Max(length, 1f), 0f, 1f);
            float v = min + raw * range;
            // StepFrequency drag snapping — value goes to the closest Step multiple (MoveThumbToPoint,
            // Slider_Partial.cpp:2012-2029 via GetClosestStep).
            if (o.Step > 0f) v = min + MathF.Round((v - min) / o.Step) * o.Step;
            Commit(Math.Clamp(v, min, max));
        }

        void PressSet(Point2 p)
        {
            Set(p);
            OpenTip();   // press (mouse OR touch) shows the tooltip immediately (Slider_Partial.cpp:509-520)
        }

        void OnKey(KeyEventArgs e)
        {
            if (Slider.HandleKey(e, props.Value, min, max, smallChange, largeChange, out float next))
            {
                if (next != props.Value) Commit(next);
                OpenTip();   // a handled navigation key shows the keyboard-mode tooltip (Slider_Partial.cpp:296-311)
            }
        }

        void HoverMove(Point2 p)
        {
            if (!tooltipEnabled || tip.Value is { IsOpen: true }) return;
            // Hovering the THUMB arms the ToolTipService-style delayed open (the WinUI thumb's attached ToolTip part,
            // Slider_Partial.cpp:2037-2120). The ring is 22px, centred at the value position, clamped inside the track.
            float cMain = Math.Clamp(o.Vertical ? (1f - t) * length : t * length, half, MathF.Max(half, length - half));
            float main = o.Vertical ? p.Y : p.X;
            float cross = o.Vertical ? p.X : p.Y;
            bool overThumb = MathF.Abs(main - cMain) <= half && MathF.Abs(cross - thickness * 0.5f) <= half;
            if (overThumb) { if (tipPhase.Peek() == 0) tipPhase.Value = 1; }
            else if (tipPhase.Peek() == 1) tipPhase.Value = 0;
        }

        void Exit()
        {
            // The tooltip stays visible while the slider is pressed — capture keeps the drag alive outside the bounds
            // (Slider_Partial.cpp:454-472); otherwise pointer-leave cancels a pending open / closes an open bubble.
            if (!TrackPressed()) CloseTip();
        }

        void FocusChanged(bool on)
        {
            if (!tooltipEnabled) return;
            if (!on) { CloseTip(); return; }   // OnLostFocus → hide (Slider_Partial.cpp:256-268)
            // OnGotFocus shows it only for KEYBOARD focus (FocusState_Keyboard, Slider_Partial.cpp:236-250); the
            // dispatcher sets NodeFlags.FocusVisual before delivering OnFocusChanged(true).
            var sc = Context.Scene;
            if (sc is not null && !trackRef.Value.IsNull && sc.IsLive(trackRef.Value)
                && (sc.Flags(trackRef.Value) & NodeFlags.FocusVisual) != 0)
                OpenTip();
        }

        // ── tick marks (TickBar: 1px marks, SliderOutsideTickBarThemeHeight = 4, Slider_themeresources.xaml:5;
        //    outside bars sit 4px off the track, lines 413/415; visibility per TickPlacement, Slider_Partial.cpp:2248-2303).
        //    Marks sit at tt·length — aligned with this engine's raw x/length value mapping (WinUI insets by the half
        //    thumb because ITS pointer mapping is thumb-compensated; ours is not, so full-span keeps ticks on-value). ──
        Element OutsideTicks()
        {
            var marks = new List<Element>();
            for (float tv = min; tv <= max + o.TickFrequency * 0.01f; tv += o.TickFrequency)
            {
                float tt = Math.Clamp((tv - min) / range, 0f, 1f);
                marks.Add(o.Vertical
                    ? new BoxEl { Width = 4f, Height = 1f, Fill = tickFill, HoverFill = tickFill, PressedFill = tickFill, OffsetY = (1f - tt) * length }
                    : new BoxEl { Width = 1f, Height = 4f, Fill = tickFill, HoverFill = tickFill, PressedFill = tickFill, OffsetX = tt * length });
            }
            return o.Vertical
                ? new BoxEl { ZStack = true, Width = 4f, Height = length, Children = marks.ToArray() }
                : new BoxEl { ZStack = true, Width = length, Height = 4f, Children = marks.ToArray() };
        }

        Element InlineTicks()
        {
            // SliderInlineTickBarFill = ControlFillColorInputActive (line 32), drawn INSIDE the rail at track height.
            // The Disabled storyboard recolors only the outside bars (lines 350-361) — inline keeps its fill.
            var marks = new List<Element>();
            for (float tv = min; tv <= max + o.TickFrequency * 0.01f; tv += o.TickFrequency)
            {
                float tt = Math.Clamp((tv - min) / range, 0f, 1f);
                marks.Add(o.Vertical
                    ? new BoxEl { Width = s.TrackHeight, Height = 1f, Fill = s.InlineTickFill, HoverFill = s.InlineTickFill, PressedFill = s.InlineTickFill, OffsetY = (1f - tt) * length }
                    : new BoxEl { Width = 1f, Height = s.TrackHeight, Fill = s.InlineTickFill, HoverFill = s.InlineTickFill, PressedFill = s.InlineTickFill, OffsetX = tt * length });
            }
            return o.Vertical
                ? new BoxEl { ZStack = true, Width = s.TrackHeight, Height = length, OffsetX = (thickness - s.TrackHeight) * 0.5f, Children = marks.ToArray() }
                : new BoxEl { ZStack = true, Width = length, Height = s.TrackHeight, OffsetY = (thickness - s.TrackHeight) * 0.5f, Children = marks.ToArray() };
        }

        bool hasTicks = o.TickFrequency > 0f && o.TickPlacement != Slider.TickPlacement.None;
        bool inlineTicks = hasTicks && o.TickPlacement == Slider.TickPlacement.Inline;
        bool beforeTicks = hasTicks && o.TickPlacement is Slider.TickPlacement.TopLeft or Slider.TickPlacement.Outside;
        bool afterTicks = hasTicks && o.TickPlacement is Slider.TickPlacement.BottomRight or Slider.TickPlacement.Outside;

        // ── the track ──
        BoxEl track;
        if (o.Vertical)
        {
            var children = new List<Element>
            {
                new BoxEl { Width = s.TrackHeight, Height = length, Corners = CornerRadius4.All(s.TrackCornerRadius), Fill = railFill, HoverFill = railFill, PressedFill = railFill, OffsetX = (thickness - s.TrackHeight) * 0.5f },
                new BoxEl { Width = s.TrackHeight, Height = t * length, Corners = CornerRadius4.All(s.TrackCornerRadius), Fill = valueFill, HoverFill = valueHover, PressedFill = valuePress, OffsetX = (thickness - s.TrackHeight) * 0.5f, OffsetY = (1f - t) * length },
            };
            if (inlineTicks) children.Add(InlineTicks());
            children.Add(BuildThumbAt(o.Vertical, t));

            track = new BoxEl
            {
                Width = thickness, Height = length, ZStack = true, Role = AutomationRole.Slider, IsEnabled = isEnabled,
                Focusable = true,
                FocusVisualMargin = new Edges4(-7f, 0f, -7f, 0f),   // the style setter applies to BOTH orientations (Slider_themeresources.xaml:184)
                OnRealized = h => trackRef.Value = h,
                OnPointerDown = PressSet, OnDrag = Set,
                OnClick = tooltipEnabled ? CloseTip : null,         // release → hide (PerformPointerUpAction, cpp:645-659)
                OnKeyDown = OnKey,
                OnHoverMove = tooltipEnabled ? HoverMove : null,
                OnPointerExit = tooltipEnabled ? Exit : null,
                OnFocusChanged = tooltipEnabled ? FocusChanged : null,
                Children = children.ToArray(),
            };
        }
        else
        {
            var children = new List<Element>
            {
                new BoxEl { Width = length, Height = s.TrackHeight, Corners = CornerRadius4.All(s.TrackCornerRadius), Fill = railFill, HoverFill = railFill, PressedFill = railFill, OffsetY = (thickness - s.TrackHeight) * 0.5f },
                new BoxEl { Width = t * length, Height = s.TrackHeight, Corners = CornerRadius4.All(s.TrackCornerRadius), Fill = valueFill, HoverFill = valueHover, PressedFill = valuePress, OffsetY = (thickness - s.TrackHeight) * 0.5f },
            };
            if (inlineTicks) children.Add(InlineTicks());
            children.Add(BuildThumbAt(o.Vertical, t));

            track = new BoxEl
            {
                Width = length, Height = thickness, ZStack = true, Role = AutomationRole.Slider, IsEnabled = isEnabled,
                Focusable = true,
                FocusVisualMargin = new Edges4(-7f, 0f, -7f, 0f),   // Slider_themeresources.xaml:184
                OnRealized = h => trackRef.Value = h,
                OnPointerDown = PressSet, OnDrag = Set,
                OnClick = tooltipEnabled ? CloseTip : null,
                OnKeyDown = OnKey,
                OnHoverMove = tooltipEnabled ? HoverMove : null,
                OnPointerExit = tooltipEnabled ? Exit : null,
                OnFocusChanged = tooltipEnabled ? FocusChanged : null,
                Children = children.ToArray(),
            };
        }

        BoxEl BuildThumbAt(bool vertical, float frac)
        {
            if (vertical)
            {
                return ThumbCore() with
                {
                    OffsetX = (thickness - ringD) * 0.5f,
                    OffsetY = Math.Clamp((1f - frac) * length - half, 0f, MathF.Max(0f, length - ringD)),
                };
            }
            return new BoxEl
            {
                Direction = 0, Width = length, Height = thickness, AlignItems = FlexAlign.Center,
                Children =
                [
                    new BoxEl { Width = MathF.Max(0f, MathF.Min(frac * length - half, length - ringD)) },   // ring centred at the value, clamped inside
                    ThumbCore(),
                ],
            };

            BoxEl ThumbCore()
            {
                ColorF dot = isEnabled ? s.ThumbFill : s.ThumbFillDisabled;
                ColorF dotHover = isEnabled ? s.ThumbFillPointerOver : s.ThumbFillDisabled;
                ColorF dotPress = isEnabled ? s.ThumbFillPressed : s.ThumbFillDisabled;
                float rest = isEnabled ? s.InnerRestScale : s.InnerDisabledScale;
                return new BoxEl
                {
                    Width = ringD, Height = ringD,
                    AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                    Corners = CornerRadius4.All(s.ThumbCornerRadius),
                    Fill = s.ThumbRing, HoverFill = s.ThumbRing, PressedFill = s.ThumbRing,   // no per-state ring variants (line 19)
                    BorderBrush = s.ThumbBorder, BorderWidth = s.ThumbBorderWidth,
                    OnRealized = h => thumbRef.Value = h,   // tooltip anchor
                    Children =
                    [
                        new BoxEl
                        {
                            Width = s.InnerThumbDiameter, Height = s.InnerThumbDiameter,
                            Corners = Radii.Circle(s.InnerThumbDiameter),
                            Fill = dot, HoverFill = dotHover, PressedFill = dotPress,           // SliderThumbBackground* (lines 14-17)
                            ScaleX = rest, ScaleY = rest,                                       // Normal 0.86 / Disabled 1.167 (lines 206-217, 242-253)
                            HoverScale = isEnabled ? s.InnerHoverScale / s.InnerRestScale : 1f, // → net 1.167 (lines 218-229)
                            PressScale = isEnabled ? s.InnerPressScale / s.InnerRestScale : 1f, // → net 0.71 (lines 230-241)
                            HoverDurationMs = 250f, PressDurationMs = 250f,                     // ControlNormalAnimationDuration (Common_themeresources_any.xaml:603)
                        },
                    ],
                };
            }
        }

        // ── assemble: optional leading/trailing tick bars (4px gap — lines 413/415: Margin 0,0,0,4 / 0,4,0,0), header,
        //    and the hover-arm clock (mounted ONLY during the 800ms ToolTipService initial-show delay). ──
        int ph = tipPhase.Value;   // subscribe → re-render on tooltip phase flips (mount/unmount the clock)
        Element? clock = ph == 1
            ? Embed.Comp(() => new ToolTipClock { DurationMs = ToolTip.MouseShowDelayMs, OnElapsed = OpenTip })
            : null;

        BoxEl body;
        if (beforeTicks || afterTicks)
        {
            var rows = new List<Element>(3);
            if (beforeTicks) rows.Add(OutsideTicks());
            rows.Add(track);
            if (afterTicks) rows.Add(OutsideTicks());
            body = o.Vertical
                ? new BoxEl { Direction = 0, Gap = 4f, AlignItems = FlexAlign.Center, Children = rows.ToArray() }
                : new BoxEl { Direction = 1, Gap = 4f, Children = rows.ToArray() };
        }
        else
        {
            body = track;
        }

        var withHeader = string.IsNullOrEmpty(o.Header) ? body : new BoxEl
        {
            Direction = 1,
            Children =
            [
                new TextEl(o.Header!)
                {
                    Size = 14f,                                            // ControlContentThemeFontSize (line 181)
                    Color = isEnabled ? Tok.TextPrimary : Tok.TextDisabled, // SliderHeaderForeground (line 28) / Disabled (line 29)
                    Wrap = TextWrap.Wrap,                                   // HeaderContentPresenter TextWrapping=Wrap (line 396)
                    Margin = new Edges4(0f, 0f, 0f, 4f),                    // SliderTopHeaderMargin (line 161)
                },
                body,
            ],
        };

        return clock is null ? withHeader : new BoxEl
        {
            Direction = 1,
            Children = [withHeader, clock],
        };
    }
}
