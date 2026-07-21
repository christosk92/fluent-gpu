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
///
/// <para><b>ONE API</b> (<see cref="Create"/>): the value is a caller <see cref="FloatSignal"/> (null ⇒ the control
/// materializes its own — one code path); a drag writes the signal, which drives the value-fill and thumb transforms on
/// the compositor fast path — <b>zero render, zero reconcile, zero relayout per pointer-move, at ANY range</b>. Step
/// snapping applies at the write site; the WinUI keyboard map rides <see cref="HandleKey"/>. The thumb value tooltip
/// ("disambiguation UI") binds the same signal, so its readout updates per-move with no bubble re-render; open/close is
/// per-gesture-edge (press / keyboard / hover-arm / focus). Range, ticks, orientation, header and the tooltip are all
/// configured through <see cref="SliderOptions"/> (null ⇒ 0..1, tooltip enabled — the old lightweight scrub default).</para>
///
/// Keyboard (KeyPress::Slider::KeyDown, SliderKeyProcess.h:28-71): Left/Down decrease and Right/Up increase by
/// SmallChange, Home/End jump to Minimum/Maximum; PageUp/PageDown move by LargeChange (not in XAML's KeyPress::Slider —
/// kept per the parity contract, riding the same Slider::Step math, Slider_Partial.cpp:1713-1783). RTL note: WinUI
/// inverts Left/Right by (FlowDirection==RightToLeft) ^ IsDirectionReversed (SliderKeyProcess.h:26); the engine has no
/// FlowDirection yet — the LTR mapping ships now, the inversion lands with the Wave-6 RTL sweep.
/// </summary>
public static partial class Slider
{
    // Template parts (the WinUI x:Name vocabulary; see TemplateParts). Each part's doc lists the props the control
    // OWNS (re-asserted after any modifier — a Parts customization cannot win those). The single <see cref="Create"/>
    // factory routes this vocabulary through the merged <see cref="SliderCore"/>.
    /// <summary>The interactive full-size track surface (WinUI SliderContainer): the hit target carrying the scrub +
    /// keyboard + tooltip mechanics. Owned: OnPointerDown, OnDrag, OnKeyDown, OnRealized (track ref), Role, Children —
    /// plus, while the tooltip is enabled, the tooltip wiring (OnClick/OnHoverMove/OnPointerExit/OnFocusChanged).</summary>
    public const string PartContainer = "Container";
    /// <summary>The 4px resting rail (WinUI HorizontalTrackRect / VerticalTrackRect). Owned: nothing — pure styling
    /// (the hover/press fill pins are stock state styling a modifier may override).</summary>
    public const string PartRail = "Rail";
    /// <summary>The accent value fill (WinUI HorizontalDecreaseRect / VerticalDecreaseRect). Owned: the value-position
    /// geometry — a composited Transform bound to the signal (grow-from-left / grow-from-bottom over the full track).</summary>
    public const string PartValueFill = "ValueFill";
    /// <summary>The static 22px thumb ring (WinUI HorizontalThumb / VerticalThumb). Owned: Children (the inner dot),
    /// OnRealized (the tooltip anchor, chained), and the composited value-position TransformBind.</summary>
    public const string PartThumb = "Thumb";
    /// <summary>The 12px animated accent dot inside the thumb (WinUI SliderInnerThumb — the only animated thumb part).
    /// Owned: the storyboard scale ramp — ScaleX/ScaleY (the rest/disabled base), HoverScale, PressScale; colors,
    /// size and durations stay restylable.</summary>
    public const string PartInnerDot = "InnerDot";
    /// <summary>Every tick bar (WinUI TopTickBar/BottomTickBar/HorizontalInlineTickBar — one part name, applied to every
    /// mounted instance). Owned: Children (the marks sit at value positions).</summary>
    public const string PartTickBar = "TickBar";

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
    /// the thumb value tooltip ("disambiguation UI"). Null on <see cref="Create"/> ⇒ 0..1 with the tooltip enabled.</summary>
    public sealed record SliderOptions
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

    // ── WinUI keyboard semantics (routed by SliderCore.OnKey) ───────────────────────────────────────────────────────

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

    // ── shared thumb ────────────────────────────────────────────────────────────────────────────────────────────────

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
    internal static BoxEl BuildThumb(Style s, bool isEnabled, Action<NodeHandle>? onRealized, Func<Affine2D>? transformBind, TemplateParts? parts)
    {
        ColorF dot = isEnabled ? s.ThumbFill : s.ThumbFillDisabled;
        ColorF dotHover = isEnabled ? s.ThumbFillPointerOver : s.ThumbFillDisabled;   // SliderThumbBackgroundPointerOver (line 15)
        ColorF dotPress = isEnabled ? s.ThumbFillPressed : s.ThumbFillDisabled;       // SliderThumbBackgroundPressed (line 16)
        float rest = isEnabled ? s.InnerRestScale : s.InnerDisabledScale;
        float hoverScale = isEnabled ? s.InnerHoverScale / s.InnerRestScale : 1f;     // net 1.167 (14px) on hover
        float pressScale = isEnabled ? s.InnerPressScale / s.InnerRestScale : 1f;     // net 0.71 (8.5px) on press
        var inner = new BoxEl
        {
            Width = s.InnerThumbDiameter, Height = s.InnerThumbDiameter,
            Corners = Radii.Circle(s.InnerThumbDiameter),
            Fill = dot, HoverFill = dotHover, PressedFill = dotPress,
            ScaleX = rest, ScaleY = rest,                                  // Normal 0.86 / Disabled 1.167 static base
            HoverScale = hoverScale, PressScale = pressScale,
            HoverDurationMs = 250f, PressDurationMs = 250f,                // ControlNormalAnimationDuration (Common_themeresources_any.xaml:603)
        };
        // PartInnerDot: colors/size/durations restylable; the storyboard scale ramp IS the thumb mechanism.
        if (parts is not null)
            inner = parts.Apply(PartInnerDot, inner) with
            {
                ScaleX = rest, ScaleY = rest, HoverScale = hoverScale, PressScale = pressScale,
            };
        Element[] thumbKids = [inner];
        var ring = new BoxEl
        {
            Width = s.ThumbRingDiameter, Height = s.ThumbRingDiameter,
            AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            Corners = CornerRadius4.All(s.ThumbCornerRadius),
            // Pin hover/press to the resting ring color: SliderOuterThumbBackground has NO per-state variants (line 19),
            // and an unset HoverFill would otherwise trip the recorder's auto-lighten fallback.
            Fill = s.ThumbRing, HoverFill = s.ThumbRing, PressedFill = s.ThumbRing,
            BorderBrush = s.ThumbBorder, BorderWidth = s.ThumbBorderWidth,
            OnRealized = onRealized,
            Transform = transformBind,
            Children = thumbKids,
        };
        // PartThumb: restyle the ring; the inner-dot structure, ref capture and value-position bind always win.
        if (parts is not null)
        {
            var m = parts.Apply(PartThumb, ring);
            ring = m with
            {
                OnRealized = TemplateParts.Chain(onRealized, m.OnRealized),
                // The value-position bind always wins; a modifier's bound Transform survives otherwise
                // (m's Transform carries through the `with` clone — *Bind aliases are write-only).
                Transform = transformBind is not null ? transformBind : m.Transform,
                Children = thumbKids,
            };
        }
        return ring;
    }

    // ── the ONE factory ─────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The unified slider. <paramref name="value"/> is the caller's <see cref="FloatSignal"/> in range units (null ⇒ the
    /// control materializes its own internal signal — "uncontrolled" is just "the control made its own signal", one code
    /// path); a gesture WRITES the signal first, then fires <paramref name="onChange"/> once (a programmatic write
    /// re-skins the slider with NO onChange echo). The signal INSTANCE freezes at mount (bind wiring is mount-only) —
    /// swap it by re-keying. The value-fill and thumb transforms are bound straight to the signal on the compositor fast
    /// path, so a scrub is <b>zero render / reconcile / relayout</b> at ANY range. <paramref name="options"/> null ⇒
    /// 0..1 with the tooltip enabled; non-null carries range / step / ticks / orientation / header / tooltip config
    /// (<see cref="SliderOptions"/>). <paramref name="length"/> is the track length (px) and <paramref name="thickness"/>
    /// the cross size (WinUI SliderHorizontalHeight/SliderVerticalWidth = 32, Slider_themeresources.xaml:167-168).
    ///
    /// <para>Non-value props (options/style/enabled/parts) ride the G4 props channel, RE-PUSHED live to the stateful core
    /// (<c>Embed.Comp(props, …)</c>). The core is a <c>[Props]</c> component (<see cref="SliderCore"/>): the generator
    /// emits the signal-backed storage + <c>SliderCore.PropsData</c> transport + the <c>IPropsHost.ApplyProps</c> sink
    /// (per-field equality-gated; the <c>OnChange</c> delegate is a latest-write forwarder that never re-renders).
    /// PropsData positional order MUST match the <c>[Prop]</c> declaration order.</para>
    /// </summary>
    public static Element Create(FloatSignal? value = null, Action<float>? onChange = null, SliderOptions? options = null,
                                 float length = 200f, float thickness = 32f, Style? style = null, bool isEnabled = true,
                                 TemplateParts? parts = null)
        => Embed.Comp(new SliderCore.PropsData(value, onChange, options, length, thickness, style ?? DefaultStyle, isEnabled, parts),
                      () => new SliderCore());
}

/// <summary>
/// The one stateful body of <see cref="Slider.Create"/>: the WinUI track/thumb template with the value-fill and thumb
/// bound to the caller's signal on the compositor fast path (zero-re-render scrub at arbitrary ranges — the merged
/// "Bind" geometry), PLUS the cross-render state a static tree can't hold — the thumb value-tooltip overlay (WinUI's
/// "disambiguation UI", the merged "Ranged" machinery). The tooltip reads the SAME signal, so per-move readouts ride the
/// bound text with no bubble re-render; open/close is a per-gesture-edge port of Slider_Partial.cpp:
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
/// The bubble anchors the thumb node; because the thumb moves by a composited Transform, <see cref="Scene.SceneStore"/>'s
/// AbsoluteRect reflects the scrub and OverlayHost.AfterAnimations re-places the open bubble every frame with NO
/// re-render (the live-anchor follow), so a drag stays compositor-only even with the tooltip up.
/// </summary>
[Props]
internal sealed partial class SliderCore : Component
{
    // ── re-pushed props (declared order == PropsData positional order — see Slider.Create). Value is the caller's value
    //    signal (frozen instance) — the compositor binds read it DIRECTLY (live, no props re-push for the value); null ⇒
    //    the control materializes its own internal signal. OnChange is a latest-write forwarder (a fresh lambda never
    //    re-renders; the wired handler invokes the newest). ──
    [Prop] public partial FloatSignal? Value { get; }
    [Prop] public partial Action<float>? OnChange { get; }
    [Prop] public partial Slider.SliderOptions? Options { get; }
    [Prop] public partial float Length { get; }
    [Prop] public partial float Thickness { get; }
    [Prop] public partial Slider.Style Style { get; }
    [Prop] public partial bool IsEnabled { get; }
    [Prop] public partial TemplateParts? Parts { get; }

    private static readonly Slider.SliderOptions DefaultOpts = new();

    /// <summary>The dynamic geometry the compositor binds read (a UseRef cell, overwritten each render): keeps the
    /// mount-time bind thunks current even if range / length / style are re-pushed, while never re-wiring the bind.</summary>
    private struct Geo { public float Min, Range, Length, Thickness, TrackHeight, RingD, Half; }

    public override Element Render()
    {
        // Non-value props (subscribing reads): a re-pushed change re-renders the core in place. NOT the value signal's
        // float — reading Value.Value would subscribe the render to the scrub and defeat the zero-re-render contract.
        var o = Options ?? DefaultOpts;
        var s = Style;
        var parts = Parts;
        bool isEnabled = IsEnabled;
        float length = Length, thickness = Thickness;
        float min = o.Min;
        float range = MathF.Max(o.Max - o.Min, 1e-5f);
        float max = o.Min + range;
        bool vertical = o.Vertical;
        float ringD = s.ThumbRingDiameter, half = ringD * 0.5f;
        // SmallChange/LargeChange: 0 = auto — (range)/100 and (range)/10, reproducing WinUI's absolute defaults 1/10 on
        // its default 0–100 range (SLIDER_DEFAULT_SMALL_CHANGE/LARGE_CHANGE/MAXIMUM, Slider_Partial.h:13-15).
        float smallChange = o.SmallChange > 0f ? o.SmallChange : range / 100f;
        float largeChange = o.LargeChange > 0f ? o.LargeChange : range / 10f;

        // ── hooks (stable order, unconditional) ──
        var own = UseFloatSignal(min);                     // auto-materialized internal signal (used iff Value is null)
        var sig = Value ?? own;                            // the ONE value signal — frozen instance; the binds read it live
        var geoRef = UseRef(default(Geo));
        var trackRef = UseRef<NodeHandle>(default);
        var thumbRef = UseRef<NodeHandle>(default);
        var tip = UseRef<OverlayHandle?>(null);
        var tipPhase = UseSignal(0);                       // 0 idle · 1 hover-arm (initial show delay) · 2 open
        var svc = UseContext(Overlay.Service);
        geoRef.Value = new Geo { Min = min, Range = range, Length = length, Thickness = thickness, TrackHeight = s.TrackHeight, RingD = ringD, Half = half };
        var geo = geoRef;

        ColorF railFill = isEnabled ? s.RailFill : s.RailFillDisabled;
        ColorF valueFill = isEnabled ? s.ValueFill : s.ValueFillDisabled;
        ColorF valueHover = isEnabled ? s.ValueFillPointerOver : s.ValueFillDisabled;   // SliderTrackValueFillPointerOver (line 25)
        ColorF valuePress = isEnabled ? s.ValueFillPressed : s.ValueFillDisabled;       // SliderTrackValueFillPressed (line 26)
        ColorF tickFill = isEnabled ? s.TickFill : s.TickFillDisabled;                  // SliderTickBarFill/Disabled (lines 30-31)

        bool tooltipEnabled = o.IsThumbToolTipEnabled && isEnabled;

        string Format(float v) => o.ThumbToolTipValueConverter is { } conv ? conv(v) : Slider.FormatThumbValue(v, o.Step);

        // ── value writes (write the signal FIRST — the compositor binds update — then fire onChange once) ──
        void Set(Point2 p)
        {
            float raw = Math.Clamp(vertical ? 1f - p.Y / MathF.Max(length, 1f) : p.X / MathF.Max(length, 1f), 0f, 1f);
            float v = min + raw * range;
            // StepFrequency drag snapping — value goes to the closest Step multiple at the WRITE site (MoveThumbToPoint,
            // Slider_Partial.cpp:2012-2029 via GetClosestStep).
            if (o.Step > 0f) v = min + MathF.Round((v - min) / o.Step) * o.Step;
            v = Math.Clamp(v, min, max);
            sig.Value = v;              // compositor-only: the bound fill/thumb transforms update, no re-render
            OnChange?.Invoke(v);        // forwarder invokes the NEWEST re-pushed delegate
        }

        // ── tooltip chrome + open/close (bubble binds `sig` directly — live readout, no bubble re-render per move) ──
        Element TipBubble() => new BoxEl
        {
            Fill = ColorF.Transparent,
            Acrylic = Tok.AcrylicFlyout,   // the standard ToolTip surface = AcrylicInAppFillColorDefault (theme-aware)
            BorderColor = Tok.StrokeFlyoutDefault,
            BorderWidth = 1f,
            Corners = Radii.ControlAll,
            Shadow = Elevation.Flyout,
            Padding = new Edges4(8f, 3f, 8f, 5f),          // SLIDER_TOOLTIP_PADDING_* (Slider_Partial.h:17-20)
            Children =
            [
                new TextEl(Format(sig.Peek()))
                {
                    Text = Prop.Of(() => Format(sig.Value)),   // live scrub readout — signal-bound, no bubble re-render
                    Size = 15f,                                // SLIDER_TOOLTIP_DEFAULT_FONT_SIZE (Slider_Partial.h:16)
                    Color = Tok.TextPrimary,
                },
            ],
        };

        void OpenTip()
        {
            if (!tooltipEnabled) return;
            if (Diag.Enabled) Diag.Event("sliderTip", $"OpenTip phase={tipPhase.Peek()} open={tip.Value is { IsOpen: true }}");
            if (tip.Value is { IsOpen: true }) { if (tipPhase.Peek() != 2) tipPhase.Value = 2; return; }
            tipPhase.Value = 2;
            tip.Value = svc.Open(
                () => thumbRef.Value,
                TipBubble,
                vertical ? FlyoutPlacement.Left : FlyoutPlacement.Top,   // PlacementMode Top / Left (Slider_Partial.cpp:2054, 2080-2096)
                new PopupOptions(FocusTrap: false, DismissBehavior: DismissBehavior.None, Chrome: PopupChrome.Raw));
        }

        void CloseTip()
        {
            if (Diag.Enabled) Diag.Event("sliderTip", $"CloseTip phase={tipPhase.Peek()} open={tip.Value is { IsOpen: true }}");
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

        void PressSet(Point2 p)
        {
            Set(p);
            OpenTip();   // press (mouse OR touch) shows the tooltip immediately (Slider_Partial.cpp:509-520)
        }

        void OnKey(KeyEventArgs e)
        {
            float cur = sig.Peek();
            if (Slider.HandleKey(e, cur, min, max, smallChange, largeChange, out float next))
            {
                if (next != cur) { sig.Value = next; OnChange?.Invoke(next); }
                OpenTip();   // a handled navigation key shows the keyboard-mode tooltip (Slider_Partial.cpp:296-311)
            }
        }

        void HoverMove(Point2 p)
        {
            if (!tooltipEnabled || tip.Value is { IsOpen: true }) return;
            // Hovering the THUMB arms the ToolTipService-style delayed open (the WinUI thumb's attached ToolTip part,
            // Slider_Partial.cpp:2037-2120). The ring is 22px, centred at the CURRENT value position, clamped in-track.
            float t = Math.Clamp((sig.Peek() - min) / range, 0f, 1f);
            float cMain = Math.Clamp(vertical ? (1f - t) * length : t * length, half, MathF.Max(half, length - half));
            float mainAxis = vertical ? p.Y : p.X;
            float cross = vertical ? p.X : p.Y;
            bool overThumb = MathF.Abs(mainAxis - cMain) <= half && MathF.Abs(cross - thickness * 0.5f) <= half;
            if (Diag.Enabled) Diag.Event("sliderTip", $"hoverMove p=({p.X:0.#},{p.Y:0.#}) overThumb={overThumb} phase={tipPhase.Peek()}");
            if (overThumb) { if (tipPhase.Peek() == 0) tipPhase.Value = 1; }
            else if (tipPhase.Peek() == 1) tipPhase.Value = 0;
        }

        void Exit()
        {
            if (Diag.Enabled) Diag.Event("sliderTip", $"pointerExit pressed={TrackPressed()} phase={tipPhase.Peek()}");
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

        // ── value-position compositor binds (grow-from-left / grow-from-bottom); center-origin transforms (see the
        //    proven horizontal fill math). The mount-time thunks read the live `geo` cell, so a re-pushed range/length
        //    stays correct without re-wiring. ──
        Func<Affine2D> fillBind = vertical
            ? () => { var g = geo.Value; float t = Math.Clamp((sig.Value - g.Min) / g.Range, 0f, 1f); float v = MathF.Max(t, 1e-4f);
                      return Affine2D.Translation((g.Thickness - g.TrackHeight) * 0.5f, g.Length * (1f - v) * 0.5f).Multiply(Affine2D.Scale(1f, v)); }
            : () => { var g = geo.Value; float t = Math.Clamp((sig.Value - g.Min) / g.Range, 0f, 1f); float v = MathF.Max(t, 1e-4f);
                      return Affine2D.Translation(-g.Length * (1f - v) * 0.5f, (g.Thickness - g.TrackHeight) * 0.5f).Multiply(Affine2D.Scale(v, 1f)); };
        Func<Affine2D> thumbBind = vertical
            ? () => { var g = geo.Value; float t = Math.Clamp((sig.Value - g.Min) / g.Range, 0f, 1f);
                      return Affine2D.Translation(0f, Math.Clamp((1f - t) * g.Length - g.Half, 0f, MathF.Max(0f, g.Length - g.RingD))); }
            : () => { var g = geo.Value; float t = Math.Clamp((sig.Value - g.Min) / g.Range, 0f, 1f);
                      return Affine2D.Translation(Math.Clamp(t * g.Length - g.Half, 0f, MathF.Max(0f, g.Length - g.RingD)), 0f); };

        // ── rail + value fill container ──
        var rail = vertical
            ? new BoxEl { Width = s.TrackHeight, Height = length, Corners = CornerRadius4.All(s.TrackCornerRadius), Fill = railFill, HoverFill = railFill, PressedFill = railFill, OffsetX = (thickness - s.TrackHeight) * 0.5f }
            : new BoxEl { Width = length, Height = s.TrackHeight, Corners = CornerRadius4.All(s.TrackCornerRadius), Fill = railFill, HoverFill = railFill, PressedFill = railFill, OffsetY = (thickness - s.TrackHeight) * 0.5f };
        rail = parts.Apply(Slider.PartRail, rail);
        // The bind OWNS LocalTransform (a bound node's static Offset* is ignored), so the fill's cross-axis centering
        // rides inside the bind's translation, not a static Offset.
        var fill = vertical
            ? new BoxEl { Width = s.TrackHeight, Height = length, Corners = CornerRadius4.All(s.TrackCornerRadius), Fill = valueFill, HoverFill = valueHover, PressedFill = valuePress, Transform = fillBind }
            : new BoxEl { Width = length, Height = s.TrackHeight, Corners = CornerRadius4.All(s.TrackCornerRadius), Fill = valueFill, HoverFill = valueHover, PressedFill = valuePress, Transform = fillBind };
        if (parts is not null)
            fill = parts.Apply(Slider.PartValueFill, fill) with { Transform = fillBind };   // the signal bind + full-track basis always win

        bool hasTicks = o.TickFrequency > 0f && o.TickPlacement != Slider.TickPlacement.None;
        bool inlineTicks = hasTicks && o.TickPlacement == Slider.TickPlacement.Inline;
        bool beforeTicks = hasTicks && o.TickPlacement is Slider.TickPlacement.TopLeft or Slider.TickPlacement.Outside;
        bool afterTicks = hasTicks && o.TickPlacement is Slider.TickPlacement.BottomRight or Slider.TickPlacement.Outside;

        var containerKids = new List<Element> { rail, fill };
        if (inlineTicks) containerKids.Add(InlineTicks());
        var container = vertical
            ? new BoxEl { ZStack = true, Width = thickness, Height = length, Children = containerKids.ToArray() }
            : new BoxEl { ZStack = true, Width = length, Height = thickness, Children = containerKids.ToArray() };

        // ── thumb (child 1): a composited Transform slides it to the value; the row/column centers the cross axis ──
        var thumb = Slider.BuildThumb(s, isEnabled, h => thumbRef.Value = h, thumbBind, parts);
        var thumbHost = vertical
            ? new BoxEl { Direction = 1, Width = thickness, Height = length, AlignItems = FlexAlign.Center, Children = [thumb] }
            : new BoxEl { Direction = 0, Width = length, Height = thickness, AlignItems = FlexAlign.Center, Children = [thumb] };

        Element[] trackKids = [container, thumbHost];
        var track = new BoxEl
        {
            Width = vertical ? thickness : length, Height = vertical ? length : thickness,
            ZStack = true, Role = AutomationRole.Slider, IsEnabled = isEnabled,
            Focusable = true,
            FocusVisualMargin = new Edges4(-7f, 0f, -7f, 0f),   // Slider FocusVisualMargin (Slider_themeresources.xaml:184)
            OnRealized = h => trackRef.Value = h,
            OnPointerDown = PressSet, OnDrag = Set,
            OnClick = tooltipEnabled ? CloseTip : null,         // release → hide (PerformPointerUpAction, cpp:645-659)
            OnKeyDown = OnKey,
            OnHoverMove = tooltipEnabled ? HoverMove : null,
            OnPointerExit = tooltipEnabled ? Exit : null,
            OnFocusChanged = tooltipEnabled ? FocusChanged : null,
            Children = trackKids,
        };
        // PartContainer: restyle the interactive surface; the scrub/keyboard/tooltip mechanics and structure always win.
        if (parts is not null)
        {
            var m = parts.Apply(Slider.PartContainer, track);
            track = m with
            {
                OnRealized = TemplateParts.Chain(h => trackRef.Value = h, m.OnRealized),
                OnPointerDown = PressSet, OnDrag = Set,
                OnClick = tooltipEnabled ? CloseTip : null,
                OnKeyDown = OnKey,
                OnHoverMove = tooltipEnabled ? HoverMove : null,
                OnPointerExit = tooltipEnabled ? Exit : null,
                OnFocusChanged = tooltipEnabled ? FocusChanged : null,
                Role = AutomationRole.Slider,
                Children = trackKids,
            };
        }

        // ── tick marks (TickBar: 1px marks, SliderOutsideTickBarThemeHeight = 4, Slider_themeresources.xaml:5;
        //    outside bars sit 4px off the track, lines 413/415; visibility per TickPlacement, Slider_Partial.cpp:2248-2303).
        //    Marks sit at tt·length — aligned with this engine's raw value mapping (WinUI insets by the half thumb because
        //    ITS pointer mapping is thumb-compensated; ours is not, so full-span keeps ticks on-value). ──
        Element OutsideTicks()
        {
            var marks = new List<Element>();
            for (float tv = min; tv <= max + o.TickFrequency * 0.01f; tv += o.TickFrequency)
            {
                float tt = Math.Clamp((tv - min) / range, 0f, 1f);
                marks.Add(vertical
                    ? new BoxEl { Width = 4f, Height = 1f, Fill = tickFill, HoverFill = tickFill, PressedFill = tickFill, OffsetY = (1f - tt) * length }
                    : new BoxEl { Width = 1f, Height = 4f, Fill = tickFill, HoverFill = tickFill, PressedFill = tickFill, OffsetX = tt * length });
            }
            var bar = vertical
                ? new BoxEl { ZStack = true, Width = 4f, Height = length, Children = marks.ToArray() }
                : new BoxEl { ZStack = true, Width = length, Height = 4f, Children = marks.ToArray() };
            return parts is null ? bar : parts.Apply(Slider.PartTickBar, bar) with { Children = bar.Children };
        }

        Element InlineTicks()
        {
            // SliderInlineTickBarFill = ControlFillColorInputActive (line 32), drawn INSIDE the rail at track height.
            // The Disabled storyboard recolors only the outside bars (lines 350-361) — inline keeps its fill.
            var marks = new List<Element>();
            for (float tv = min; tv <= max + o.TickFrequency * 0.01f; tv += o.TickFrequency)
            {
                float tt = Math.Clamp((tv - min) / range, 0f, 1f);
                marks.Add(vertical
                    ? new BoxEl { Width = s.TrackHeight, Height = 1f, Fill = s.InlineTickFill, HoverFill = s.InlineTickFill, PressedFill = s.InlineTickFill, OffsetY = (1f - tt) * length }
                    : new BoxEl { Width = 1f, Height = s.TrackHeight, Fill = s.InlineTickFill, HoverFill = s.InlineTickFill, PressedFill = s.InlineTickFill, OffsetX = tt * length });
            }
            var bar = vertical
                ? new BoxEl { ZStack = true, Width = s.TrackHeight, Height = length, OffsetX = (thickness - s.TrackHeight) * 0.5f, Children = marks.ToArray() }
                : new BoxEl { ZStack = true, Width = length, Height = s.TrackHeight, OffsetY = (thickness - s.TrackHeight) * 0.5f, Children = marks.ToArray() };
            return parts is null ? bar : parts.Apply(Slider.PartTickBar, bar) with { Children = bar.Children };
        }

        // ── assemble: optional leading/trailing tick bars (4px gap — lines 413/415: Margin 0,0,0,4 / 0,4,0,0), header,
        //    and the hover-arm clock (mounted ONLY during the ToolTipService initial-show delay). ──
        Element body;
        if (beforeTicks || afterTicks)
        {
            var rows = new List<Element>(3);
            if (beforeTicks) rows.Add(OutsideTicks());
            rows.Add(track);
            if (afterTicks) rows.Add(OutsideTicks());
            body = vertical
                ? new BoxEl { Direction = 0, Gap = 4f, AlignItems = FlexAlign.Center, Children = rows.ToArray() }
                : new BoxEl { Direction = 1, Gap = 4f, Children = rows.ToArray() };
        }
        else
        {
            body = track;
        }

        Element withHeader = string.IsNullOrEmpty(o.Header) ? body : new BoxEl
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

        int ph = tipPhase.Value;   // subscribe → re-render on tooltip phase flips (mount/unmount the clock)
        Element? clock = ph == 1
            ? Embed.Comp(() => new ToolTipClock { DurationMs = ToolTip.MouseShowDelayMs, OnElapsed = OpenTip })
            : null;

        // STABLE root shape: the wrapper always exists, so arming/disarming the hover clock only appends/removes the
        // trailing zero-size child — the track subtree at child 0 is never reshaped by the in-place differ.
        return new BoxEl
        {
            Direction = 1,
            Children = clock is null ? [withHeader] : [withHeader, clock],
        };
    }
}
