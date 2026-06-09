using FluentGpu.Foundation;
using FluentGpu.Dsl;

namespace FluentGpu.Controls;

/// <summary>
/// A WinUI ToggleSwitch: a pill track + a knob that slides across on toggle (the slide is a layout-projection FLIP — the
/// knob's laid-out position changes and the host animates the residual, no per-frame re-render). Accent track when on.
/// Optional header + On/Off content labels. Controlled.
/// </summary>
public static partial class ToggleSwitch
{
    public sealed record Style
    {
        public float TrackWidth { get; init; } = 44f;          // OuterBorder Width (generic.xaml:11484)
        public float TrackHeight { get; init; } = 20f;         // OuterBorder Height
        public float OffKnobSize { get; init; } = 10f;         // SwitchKnobOff Width/Height (rest, off)
        public float NormalKnobSize { get; init; } = 12f;      // knob at rest, on
        public float HoverKnobSize { get; init; } = 14f;       // pointer-over grow
        public float PressedKnobSize { get; init; } = 17f;     // pressed grow
        public float FontSize { get; init; } = 14f;            // ControlContentThemeFontSize
        public ColorF OffFill { get; init; }                   // ToggleSwitchFillOff → ControlAltFillColorSecondary
        public ColorF OffHover { get; init; }                  // ...OffPointerOver → ControlAltFillColorTertiary
        public ColorF OffPressed { get; init; }                // ...OffPressed → ControlAltFillColorQuaternary
        public ColorF OffBorder { get; init; }                 // ToggleSwitchStrokeOff → ControlStrongStrokeColorDefault
        public ColorF OffKnob { get; init; }                   // ToggleSwitchKnobFillOff → TextSecondary
        public ColorF OnFill { get; init; }                    // ToggleSwitchFillOn → AccentDefault
        public ColorF OnHover { get; init; }                   // ...OnPointerOver → AccentSecondary
        public ColorF OnPressed { get; init; }                 // ...OnPressed → AccentTertiary
        public ColorF OnKnob { get; init; }                    // ToggleSwitchKnobFillOn → TextOnAccentPrimary
        public ColorF Foreground { get; init; }
        public ColorF HeaderColor { get; init; }
        // Disabled logical state (WinUI ToggleSwitch *Disabled brushes). The engine IsEnabled gate stops interaction;
        // these are the resting visuals the control swaps in for that state.
        public ColorF OffFillDisabled { get; init; }           // ToggleSwitchFillOffDisabled → ControlAltFillColorDisabled
        public ColorF OffBorderDisabled { get; init; }         // ToggleSwitchStrokeOffDisabled → StrokeControlStrongDisabled
        public ColorF OnFillDisabled { get; init; }            // ToggleSwitchFillOnDisabled → AccentFillColorDisabled
        public ColorF OffKnobDisabled { get; init; }           // ToggleSwitchKnobFillOffDisabled → TextDisabled
        public ColorF OnKnobDisabled { get; init; }            // ToggleSwitchKnobFillOnDisabled → TextOnAccentDisabled
        public ColorF ForegroundDisabled { get; init; }        // ToggleSwitchContentForegroundDisabled → TextDisabled
        public ColorF HeaderColorDisabled { get; init; }       // ToggleSwitchHeaderForegroundDisabled → TextDisabled
    }

    public static Style? StyleOverride;
    public static Style DefaultStyle => StyleOverride ?? new Style
    {
        OffFill = Tok.FillControlAltSecondary, OffHover = Tok.FillControlAltTertiary, OffPressed = Tok.FillControlAltQuaternary,
        OffBorder = Tok.StrokeControlStrongDefault, OffKnob = Tok.TextSecondary,
        OnFill = Tok.AccentDefault, OnHover = Tok.AccentSecondary, OnPressed = Tok.AccentTertiary, OnKnob = Tok.TextOnAccentPrimary,
        Foreground = Tok.TextPrimary, HeaderColor = Tok.TextSecondary,
        OffFillDisabled = Tok.FillControlAltDisabled, OffBorderDisabled = Tok.StrokeControlStrongDisabled,
        OnFillDisabled = Tok.AccentDisabled, OffKnobDisabled = Tok.TextDisabled, OnKnobDisabled = Tok.TextOnAccentDisabled,
        ForegroundDisabled = Tok.TextDisabled, HeaderColorDisabled = Tok.TextDisabled,
    };

    public static BoxEl Create(bool isOn, Action onToggle, string? header = null, string? onContent = null, string? offContent = null, bool isEnabled = true, Style? style = null)
    {
        var s = style ?? DefaultStyle;
        // Knob: 10px off / 12px on at rest, grown to 14 (hover) / 17 (press) via composited scale (no per-frame re-render).
        float knob = isOn ? s.NormalKnobSize : s.OffKnobSize;
        float pad = (s.TrackHeight - knob) / 2f;
        float travel = s.TrackWidth - knob - 2f * pad;          // 44 - 12 - 2*4 = 20 (WinUI KnobTranslateTransform X = 20)

        // Disabled is a logical state: resting track/knob fill + border swap to the WinUI *Disabled tokens. The engine
        // IsEnabled gate (on the control row) stops hit-test/focus/keyboard, so HoverFill/PressedFill stay wired but inert.
        var track = new BoxEl
        {
            Direction = 0,
            Width = s.TrackWidth,
            Height = s.TrackHeight,
            AlignItems = FlexAlign.Center,
            Padding = new Edges4(pad, 0, pad, 0),
            Corners = Radii.Circle(s.TrackHeight),
            BorderWidth = isOn ? 0f : 1f,                       // ToggleSwitchOnStrokeThickness=0 / OuterBorderStrokeThickness=1
            BorderColor = isOn ? ColorF.Transparent : (isEnabled ? s.OffBorder : s.OffBorderDisabled),
            Fill = isEnabled ? (isOn ? s.OnFill : s.OffFill) : (isOn ? s.OnFillDisabled : s.OffFillDisabled),
            HoverFill = isOn ? s.OnHover : s.OffHover,
            PressedFill = isOn ? s.OnPressed : s.OffPressed,
            Children =
            [
                new BoxEl { Width = isOn ? travel : 0f },   // leading spacer positions the knob; its width change drives the FLIP slide
                new BoxEl
                {
                    Width = knob, Height = knob, Corners = Radii.Circle(knob),
                    Fill = isEnabled ? (isOn ? s.OnKnob : s.OffKnob) : (isOn ? s.OnKnobDisabled : s.OffKnobDisabled),
                    HoverScale = s.HoverKnobSize / s.NormalKnobSize, PressScale = s.PressedKnobSize / s.NormalKnobSize,
                    Animate = LayoutTransition.Slide with { Dynamics = TransitionDynamics.Spring(0.20f, 0.85f) },
                },
            ],
        };

        var row = new List<Element> { track };
        string? side = isOn ? onContent : offContent;
        if (side is { Length: > 0 })
            row.Add(new TextEl(side) { Size = s.FontSize, Color = s.Foreground, DisabledColor = s.ForegroundDisabled });

        var control = new BoxEl
        {
            Direction = 0,
            AlignItems = FlexAlign.Center,
            Gap = 12f,
            MinHeight = 32f,
            Role = AutomationRole.ToggleSwitch,
            IsEnabled = isEnabled,
            OnClick = onToggle,
            Children = row.ToArray(),
        };

        if (header is { Length: > 0 })
            return new BoxEl { Direction = 1, Gap = 6f, Children = [new TextEl(header) { Size = s.FontSize, Color = isEnabled ? s.HeaderColor : s.HeaderColorDisabled }, control] };
        return control;
    }
}
