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
        public float TrackWidth { get; init; } = 40f;
        public float TrackHeight { get; init; } = 20f;
        public float KnobSize { get; init; } = 12f;
        public float FontSize { get; init; } = 14f;
        public ColorF OffFill { get; init; }
        public ColorF OffHover { get; init; }
        public ColorF OffBorder { get; init; }
        public ColorF OffKnob { get; init; }
        public ColorF OnFill { get; init; }
        public ColorF OnHover { get; init; }
        public ColorF OnKnob { get; init; }
        public ColorF Foreground { get; init; }
        public ColorF HeaderColor { get; init; }
    }

    public static Style? StyleOverride;
    public static Style DefaultStyle => StyleOverride ?? new Style
    {
        OffFill = Tok.FillControlSolid, OffHover = Tok.FillControlSecondary, OffBorder = Tok.StrokeControlSecondary,
        OffKnob = Tok.TextSecondary, OnFill = Tok.AccentDefault, OnHover = Tok.AccentSecondary, OnKnob = Tok.TextOnAccentPrimary,
        Foreground = Tok.TextPrimary, HeaderColor = Tok.TextSecondary,
    };

    public static BoxEl Create(bool isOn, Action onToggle, string? header = null, string? onContent = null, string? offContent = null, Style? style = null)
    {
        var s = style ?? DefaultStyle;
        float pad = (s.TrackHeight - s.KnobSize) / 2f;
        float travel = s.TrackWidth - s.KnobSize - 2f * pad;

        var track = new BoxEl
        {
            Direction = 0,
            Width = s.TrackWidth,
            Height = s.TrackHeight,
            AlignItems = FlexAlign.Center,
            Padding = new Edges4(pad, 0, pad, 0),
            Corners = Radii.Circle(s.TrackHeight),
            BorderWidth = isOn ? 0f : 1f,
            BorderColor = isOn ? ColorF.Transparent : s.OffBorder,
            Fill = isOn ? s.OnFill : s.OffFill,
            HoverFill = isOn ? s.OnHover : s.OffHover,
            Children =
            [
                new BoxEl { Width = isOn ? travel : 0f },   // leading spacer positions the knob; its width change drives the FLIP slide
                new BoxEl
                {
                    Width = s.KnobSize, Height = s.KnobSize, Corners = Radii.Circle(s.KnobSize), Fill = isOn ? s.OnKnob : s.OffKnob,
                    Animate = LayoutTransition.Slide with { Dynamics = TransitionDynamics.Spring(0.20f, 0.85f) },
                },
            ],
        };

        var row = new List<Element> { track };
        string? side = isOn ? onContent : offContent;
        if (side is { Length: > 0 })
            row.Add(new TextEl(side) { Size = s.FontSize, Color = s.Foreground });

        var control = new BoxEl
        {
            Direction = 0,
            AlignItems = FlexAlign.Center,
            Gap = 12f,
            MinHeight = 32f,
            Role = AutomationRole.ToggleSwitch,
            OnClick = onToggle,
            Children = row.ToArray(),
        };

        if (header is { Length: > 0 })
            return new BoxEl { Direction = 1, Gap = 6f, Children = [new TextEl(header) { Size = s.FontSize, Color = s.HeaderColor }, control] };
        return control;
    }
}
