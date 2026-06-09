using FluentGpu.Foundation;
using FluentGpu.Dsl;
using FluentGpu.Hooks;

namespace FluentGpu.Controls;

/// <summary>
/// A WinUI ToggleSwitch — geometry/colors/motion from the shipped lifted style
/// (microsoft-ui-xaml controls\dev\CommonStyles\ToggleSwitch_themeresources.xaml, "the template"):
/// a 40×20 pill track (OuterBorder/SwitchKnobBounds, template:507-508) + a knob inside a 20×20 positioning host
/// (SwitchKnob grid, template:509) that translates 0→20 on toggle (KnobTranslateTransform To=20, template:445).
/// The knob is 12×12 at rest (template:510/515 + Normal storyboard :231-242), grows to 14×14 on hover
/// (:268-279), and STRETCHES to 17×14 pinned 3px from the near edge while pressed (size :311-322; the Pressed
/// VisualState.Setters pin SwitchKnobOn right / SwitchKnobOff left with a 3px margin, :284-287). All knob size/anchor
/// changes tween 83ms ControlFasterAnimationDuration with the ControlFastOutSlowInKeySpline (0,0,0,1)
/// (Common_themeresources_any.xaml:602/606 — <see cref="Easing.FluentPopOpen"/>); the disabled return is 250ms
/// ControlNormalAnimationDuration (:357-368); knob travel is WinUI's RepositionThemeAnimation (:418-439), mapped per
/// the repo motion canon to <see cref="Motion.ControlFast"/> = 167ms (Dsl/Motion.cs — the PVL reposition timing is
/// system data, not in the XAML).
///
/// Behavior follows dxaml\xcp\dxaml\lib\ToggleSwitch_Partial.cpp: tap toggles (TapHandler :874-905); DRAG-TO-TOGGLE —
/// the knob follows the pointer's horizontal delta clamped to the travel (DragDeltaHandler/MoveDelta :823-841/:579-589,
/// SetTranslations clamp :455-457) and on release toggles iff the knob crossed half the travel range (MoveCompleted
/// :591-619); pointer loss mid-drag cleans up without toggling (OnPointerCaptureLost :728-746). Space toggles via the
/// engine's Space-on-keyup activation → <c>OnClick</c> (HandlesKey = Space/GamepadA only, :1002-1007); arrow/Home/End
/// keys toggle directionally (ToggleSwitchKeyProcess.h:52-71 — WinUI acts on KeyUp; the engine routes OnKeyDown, a
/// known timing-only deviation). Controlled — the caller owns <c>isOn</c>.
/// </summary>
public static partial class ToggleSwitch
{
    /// <summary>Horizontal slop before a press becomes a knob drag — the Win32 drag box (SM_CXDRAG = 4px), the same
    /// base threshold <c>Input.DragController</c> uses (ListViewBaseItem_Partial.cpp:1871-1877).</summary>
    public const float DragThresholdPx = 4f;

    public sealed record Style
    {
        public float TrackWidth { get; init; } = 40f;        // OuterBorder Width (template:507)
        public float TrackHeight { get; init; } = 20f;       // OuterBorder Height (template:507)
        public float KnobHostSize { get; init; } = 20f;      // SwitchKnob grid Width/Height (template:509)
        public float KnobRestSize { get; init; } = 12f;      // SwitchKnobOn/Off Width/Height (template:510/515; Normal :231-242)
        public float KnobHoverSize { get; init; } = 14f;     // PointerOver knob Width/Height (template:268-279)
        public float KnobPressedWidth { get; init; } = 17f;  // Pressed knob Width (template:311-312/317-318)
        public float KnobPressedHeight { get; init; } = 14f; // Pressed knob Height (template:314-315/320-321)
        public float KnobTravel { get; init; } = 20f;        // KnobTranslateTransform X when On (template:445 To="20")
        public float PressedKnobEdgeMargin { get; init; } = 3f; // Pressed pin margin (template:284-287 Margin 3)
        public float FontSize { get; init; } = 14f;          // ControlContentThemeFontSize (template:196)
        public float MinWidth { get; init; } = 154f;         // ToggleSwitchThemeMinWidth (template:188/199)
        public float MinHeight { get; init; } = 40f;         // Pre(10) + track(20) + Post(10) content margins (template:186-187, rows :495-497)
        public float HeaderGap { get; init; } = 4f;          // ToggleSwitchTopHeaderMargin 0,0,0,4 (template:185)
        public float ContentGap { get; init; } = 12f;        // track→content column Width=12 (template:501)
        /// <summary>WinUI FocusVisualMargin −7,−3,−7,−3 (template:200); the engine draws the ring (E1).</summary>
        public Edges4 FocusVisualMargin { get; init; } = new(-7f, -3f, -7f, -3f);

        public ColorF OffFill { get; init; }                 // ToggleSwitchFillOff → ControlAltFillColorSecondary (template:15/135)
        public ColorF OffHover { get; init; }                // ...OffPointerOver → ControlAltFillColorTertiary (template:16/136)
        public ColorF OffPressed { get; init; }              // ...OffPressed → ControlAltFillColorQuarternary (template:17/137)
        public ColorF OffBorder { get; init; }               // ToggleSwitchStrokeOff → ControlStrongStrokeColorDefault (template:19/139);
                                                             // PointerOver/Pressed stay the SAME stroke (template:20-21/140-141)
        public ColorF OffKnob { get; init; }                 // ToggleSwitchKnobFillOff → TextFillColorSecondary, all interaction states (template:31-33/151-153)
        public ColorF OnFill { get; init; }                  // ToggleSwitchFillOn → AccentFillColorDefault (template:23/143)
        public ColorF OnHover { get; init; }                 // ...OnPointerOver → AccentFillColorSecondary (template:24/144)
        public ColorF OnPressed { get; init; }               // ...OnPressed → AccentFillColorTertiary (template:25/145)
        public ColorF OnKnob { get; init; }                  // ToggleSwitchKnobFillOn → TextOnAccentFillColorPrimary (template:35-37/155-157)
        /// <summary>ToggleSwitchKnobStrokeOn → CircleElevationBorderBrush (template:39/159), bound to SwitchKnobOn's
        /// BorderBrush (template:510). NOTE: WinUI never sets a BorderThickness on SwitchKnobOn and Border defaults to 0
        /// (dxaml\xcp\core\inc\Border.h:247 <c>m_borderThickness = {}</c>), so the stroke is wired but draws at 0px —
        /// <see cref="KnobStrokeOnWidth"/> defaults to 0 to match; opt in with 1 if you want the rim visible.</summary>
        public GradientSpec? KnobStrokeOn { get; init; }
        public float KnobStrokeOnWidth { get; init; }        // 0f — Border.BorderThickness default (Border.h:247)
        public ColorF Foreground { get; init; }              // ToggleSwitchContentForeground → TextFillColorPrimary (template:7/127)
        public ColorF HeaderColor { get; init; }             // ToggleSwitchHeaderForeground → TextFillColorPrimary (template:9/129)
        // Disabled logical state (the WinUI *Disabled brushes; engine IsEnabled gates interaction).
        public ColorF OffFillDisabled { get; init; }         // ToggleSwitchFillOffDisabled → ControlAltFillColorDisabled (template:18/138)
        public ColorF OffBorderDisabled { get; init; }       // ToggleSwitchStrokeOffDisabled → ControlStrongStrokeColorDisabled (template:22/142)
        public ColorF OnFillDisabled { get; init; }          // ToggleSwitchFillOnDisabled → AccentFillColorDisabled (template:26/146)
        public ColorF OffKnobDisabled { get; init; }         // ToggleSwitchKnobFillOffDisabled → TextFillColorDisabled (template:34/154)
        public ColorF OnKnobDisabled { get; init; }          // ToggleSwitchKnobFillOnDisabled → TextOnAccentFillColorDisabled (template:38/158)
        public ColorF ForegroundDisabled { get; init; }      // ToggleSwitchContentForegroundDisabled → TextFillColorDisabled (template:8/128)
        public ColorF HeaderColorDisabled { get; init; }     // ToggleSwitchHeaderForegroundDisabled → TextFillColorDisabled (template:10/130)
    }

    public static Style? StyleOverride;
    public static Style DefaultStyle => StyleOverride ?? new Style
    {
        OffFill = Tok.FillControlAltSecondary, OffHover = Tok.FillControlAltTertiary, OffPressed = Tok.FillControlAltQuaternary,
        OffBorder = Tok.StrokeControlStrongDefault, OffKnob = Tok.TextSecondary,
        OnFill = Tok.AccentDefault, OnHover = Tok.AccentSecondary, OnPressed = Tok.AccentTertiary, OnKnob = Tok.TextOnAccentPrimary,
        KnobStrokeOn = Tok.CircleElevationBorder,
        Foreground = Tok.TextPrimary, HeaderColor = Tok.TextPrimary,
        OffFillDisabled = Tok.FillControlAltDisabled, OffBorderDisabled = Tok.StrokeControlStrongDisabled,
        OnFillDisabled = Tok.AccentDisabled, OffKnobDisabled = Tok.TextDisabled, OnKnobDisabled = Tok.TextOnAccentDisabled,
        ForegroundDisabled = Tok.TextDisabled, HeaderColorDisabled = Tok.TextDisabled,
    };

    /// <summary>Controlled props, carried to the stateful core via context (a ComponentEl reuse never re-runs its
    /// factory — Reconciler.cs:211-219 — so props MUST flow through a provider, the NavigationView pattern).</summary>
    internal sealed record Props(bool IsOn, Action OnToggle, string? Header, string? OnContent, string? OffContent,
                                 bool IsEnabled, Style Style)
    {
        internal static readonly Context<Props?> Channel = new(null);
    }

    public static Element Create(bool isOn, Action onToggle, string? header = null, string? onContent = null,
                                 string? offContent = null, bool isEnabled = true, Style? style = null)
        => Ctx.Provide(Props.Channel,
                       new Props(isOn, onToggle, header, onContent, offContent, isEnabled, style ?? DefaultStyle),
                       Embed.Comp(() => new ToggleSwitchCore()));
}

/// <summary>
/// The stateful core: owns the transient gesture state (hover/press knob growth, drag-to-toggle) that WinUI keeps in
/// VisualStateManager + Thumb drag handlers. The knob is ONE animated node — every commit's geometry change (size,
/// anchor pin, travel) rides its <c>Animate</c> FLIP with per-commit dynamics (83ms size / 167ms travel / 250ms
/// disabled / snap while dragging), so size and travel never double-animate through nested projections.
/// </summary>
internal sealed class ToggleSwitchCore : Component
{
    /// <summary>Mutable mid-gesture state (a UseRef cell): immediate, never stale across the per-move re-renders.</summary>
    private sealed class Gesture
    {
        public bool Pressed;     // pointer down on the control (ToggleSwitch_Partial.cpp m_isDragging)
        public bool Moved;       // crossed the drag box (m_wasDragged, :829-836)
        public float StartX;     // press X (local)
        public float KnobStart;  // knob translation at press (GetTranslations, :421-436)
        public float LastX;      // latest clamped knob translation (m_knobTranslation)
    }

    public override Element Render()
    {
        // Hooks — stable order, unconditionally, before any early-out.
        var props = UseContext(ToggleSwitch.Props.Channel);
        var g = UseRef(new Gesture()).Value;
        var (dragX, setDragX) = UseState(float.NaN);     // NaN = not dragging; else the knob translation (0..travel)
        var (hovered, setHovered) = UseState(false);
        var (pressed, setPressed) = UseState(false);
        var prevOn = UseRef(props?.IsOn ?? false);

        if (props is null) return new BoxEl();
        var s = props.Style;
        bool isOn = props.IsOn;
        bool enabled = props.IsEnabled;
        float travel = s.KnobTravel;
        bool dragging = !float.IsNaN(dragX);
        bool isHovered = hovered && enabled;
        bool isPressed = pressed && enabled;
        // A toggle commit (isOn flipped since the last render) is the knob-TRAVEL commit → 167ms dynamics.
        bool traveled = prevOn.Value != isOn;
        prevOn.Value = isOn;

        // ── gesture handlers (ToggleSwitch_Partial.cpp drag lifecycle over OnPointerDown/OnDrag/OnClick/OnPointerExit;
        //    the engine keeps routing OnDrag to the pressed node while held, so the knob follows even outside bounds) ──
        void Reset()
        {
            g.Pressed = false; g.Moved = false;
            setPressed(false);
            setDragX(float.NaN);
        }

        void Down(Point2 pt)
        {
            g.Pressed = true; g.Moved = false;
            g.StartX = pt.X;
            g.KnobStart = isOn ? travel : 0f;            // GetTranslations at DragStarted (cpp:803-820)
            g.LastX = g.KnobStart;
            setPressed(true);
        }

        void Drag(Point2 pt)
        {
            if (!g.Pressed) return;
            float dx = pt.X - g.StartX;
            if (!g.Moved && MathF.Abs(dx) <= ToggleSwitch.DragThresholdPx) return;   // drag box before the knob detaches
            g.Moved = true;                                                          // m_wasDragged (cpp:829-836)
            float x = Math.Clamp(g.KnobStart + dx, 0f, travel);                      // SetTranslations clamp (cpp:455-457)
            g.LastX = x;
            setDragX(x);
        }

        void Release()
        {
            // Fires on pointer release over the control AND on Space/Enter activation (engine focused-clickable).
            bool toggle = true;                                                      // tap / keyboard → Toggle (cpp:874-905)
            if (g.Pressed && g.Moved)
            {
                // MoveCompleted (cpp:591-619): toggle iff the knob crossed half of the translation range.
                float half = travel / 2f;
                toggle = isOn ? g.LastX <= half : g.LastX >= half;
            }
            Reset();
            if (toggle) props.OnToggle();
        }

        void Exit()
        {
            setHovered(false);
            // Pointer left mid-press → clean up the drag without toggling (the OnPointerCaptureLost "vertical pan"
            // cleanup, cpp:728-746; the engine has no per-node release-outside callback, so exit is the cancel edge).
            if (g.Pressed) Reset();
        }

        void HoverMove(Point2 _)
        {
            // Bare hover (no button) — also the stale-press recovery edge: the dispatcher only delivers OnHoverMove
            // with no button down, so a still-armed gesture here means the release was never seen (PointerCancel).
            if (g.Pressed) Reset();
            if (!hovered) setHovered(true);
        }

        void Arrows(KeyEventArgs a)
        {
            // Directional toggles, LTR (ToggleSwitchKeyProcess.h:52-65); handled ONLY when a toggle happens (:67-71).
            // WinUI acts on KeyUp; the engine routes OnKeyDown — timing-only deviation. Space/GamepadA go through the
            // engine's activation → OnClick (HandlesKey, ToggleSwitch_Partial.cpp:1002-1007).
            if (a.Handled) return;
            bool toOff = a.KeyCode is Keys.Left or Keys.Down or Keys.Home;
            bool toOn = a.KeyCode is Keys.Right or Keys.Up or Keys.End;
            if (!toOff && !toOn) return;
            if ((toOn && !isOn) || (toOff && isOn)) { props.OnToggle(); a.Handled = true; }
        }

        // ── knob geometry for THIS commit (the storyboard targets) ──
        float knobW = isPressed ? s.KnobPressedWidth : isHovered ? s.KnobHoverSize : s.KnobRestSize;
        float knobH = isPressed ? s.KnobPressedHeight : isHovered ? s.KnobHoverSize : s.KnobRestSize;
        // Per-commit dynamics: snap-follow while dragging (keeps BoundsAnimated armed so the RELEASE commit FLIPs from
        // the dragged spot — WinUI DraggingToOn/Off RepositionThemeAnimation, template:374-417); 167ms travel; 250ms
        // disabled return (template:357-368); 83ms FastOutSlowIn for hover/press size+anchor (template:231-322).
        TransitionDynamics dyn =
            dragging ? TransitionDynamics.Tween(1f, Easing.Linear)
            : traveled ? TransitionDynamics.Tween(Motion.ControlFast, Easing.FluentPopOpen)
            : !enabled ? TransitionDynamics.Tween(Motion.ControlNormal, Easing.FluentPopOpen)
            : TransitionDynamics.Tween(Motion.ControlFaster, Easing.FluentPopOpen);
        var knob = new BoxEl
        {
            Width = knobW, Height = knobH,
            // CornerRadius 7 clamps to half the 12-14px extent → circle at rest, capsule when pressed (template:510/515).
            Corners = Radii.Circle(MathF.Min(knobW, knobH)),
            Fill = enabled ? (isOn ? s.OnKnob : s.OffKnob) : (isOn ? s.OnKnobDisabled : s.OffKnobDisabled),
            // On↔Off knob fill swap cross-fades 83ms (the template cross-fades SwitchKnobOn/Off opacity over
            // ControlFasterAnimationDuration, :383-388/:432-437).
            BrushTransitionMs = Motion.ControlFaster,
            // SwitchKnobOn BorderBrush = CircleElevationBorderBrush (template:510/:39) at WinUI's effective 0px
            // (Border.h:247) — wired so themes/apps can opt the rim visible via Style.KnobStrokeOnWidth.
            BorderBrush = isOn ? s.KnobStrokeOn : null,
            BorderWidth = isOn && s.KnobStrokeOn is not null ? s.KnobStrokeOnWidth : 0f,
            // Pressed pins 3px from the near edge (template:284-287); rest/hover center in the 20×20 host. (WinUI's
            // ±1 rest margins put the knob at x=3.5, we center at 4 — a 0.5px layout-rounding deviation.)
            Margin = isPressed || dragging
                ? (isOn ? new Edges4(0f, 0f, s.PressedKnobEdgeMargin, 0f) : new Edges4(s.PressedKnobEdgeMargin, 0f, 0f, 0f))
                : default,
            Animate = new LayoutTransition(TransitionChannels.Bounds, dyn, SizeMode.ScaleCorrect),
        };

        // SwitchKnob 20×20 positioning host (template:509): a leading spacer drives its X = the knob translation.
        var host = new BoxEl
        {
            Width = s.KnobHostSize, Height = s.KnobHostSize,
            Direction = 0, AlignItems = FlexAlign.Center,
            Justify = isPressed || dragging
                ? (isOn ? FlexJustify.End : FlexJustify.Start)    // Pressed setters (template:284-287)
                : FlexJustify.Center,                             // HorizontalAlignment=Center at rest (template:510/515)
            Children = [knob],
        };
        float knobX = dragging ? dragX : (isOn ? travel : 0f);

        var track = new BoxEl
        {
            Direction = 0,
            Width = s.TrackWidth, Height = s.TrackHeight,
            AlignItems = FlexAlign.Center,
            Corners = Radii.Circle(s.TrackHeight),                // RadiusX/Y = 10 (template:507)
            // ToggleSwitchOnStrokeThickness=0 / OuterBorderStrokeThickness=1 (template:5-6/125-126).
            BorderWidth = isOn ? 0f : 1f,
            BorderColor = isOn ? ColorF.Transparent : (enabled ? s.OffBorder : s.OffBorderDisabled),
            // The off stroke does NOT change on hover/press (template:20-21/140-141) — pin both legs to defeat the
            // recorder's A==0 auto-lighten fallback (Element.cs:27-28).
            HoverBorderColor = isOn ? ColorF.Transparent : s.OffBorder,
            PressedBorderColor = isOn ? ColorF.Transparent : s.OffBorder,
            Fill = enabled ? (isOn ? s.OnFill : s.OffFill) : (isOn ? s.OnFillDisabled : s.OffFillDisabled),
            HoverFill = isOn ? s.OnHover : s.OffHover,
            PressedFill = isOn ? s.OnPressed : s.OffPressed,
            // The On↔Off track swap is an 83ms opacity cross-fade of the two overlaid rects in WinUI
            // (OuterBorder/SwitchKnobBounds, template:426-438/:446-457) — one rect + an 83ms brush cross-fade here.
            BrushTransitionMs = Motion.ControlFaster,
            Children =
            [
                new BoxEl { Width = knobX },   // leading spacer = KnobTranslateTransform.X (0..20)
                host,
            ],
        };

        var row = new List<Element> { track };
        string? side = isOn ? props.OnContent : props.OffContent;   // ContentStates Off/OnContent swap (template:461-486)
        if (side is { Length: > 0 })
            row.Add(new TextEl(side) { Size = s.FontSize, Color = s.Foreground, DisabledColor = s.ForegroundDisabled });

        var control = new BoxEl
        {
            Direction = 0,
            AlignItems = FlexAlign.Center,
            Gap = s.ContentGap,                                   // content column Width=12 (template:501)
            MinHeight = s.MinHeight,                              // 10+20+10 content rows (template:186-187/:495-497)
            MinWidth = s.MinWidth,                                // ToggleSwitchThemeMinWidth=154 (template:188/199)
            Role = AutomationRole.ToggleSwitch,
            IsEnabled = enabled,
            Focusable = true,                                     // UseSystemFocusVisuals (template:198)
            FocusVisualMargin = s.FocusVisualMargin,              // −7,−3,−7,−3 (template:200)
            OnClick = Release,
            OnPointerDown = Down,
            OnDrag = Drag,
            OnPointerExit = Exit,
            OnHoverMove = HoverMove,
            OnKeyDown = Arrows,
            Children = row.ToArray(),
        };

        if (props.Header is { Length: > 0 })
            return new BoxEl
            {
                Direction = 1,
                Gap = s.HeaderGap,                                // ToggleSwitchTopHeaderMargin 0,0,0,4 (template:185)
                Children =
                [
                    new TextEl(props.Header) { Size = s.FontSize, Color = enabled ? s.HeaderColor : s.HeaderColorDisabled },
                    control,
                ],
            };
        return control;
    }
}
