using FluentGpu.Foundation;
using FluentGpu.Dsl;

namespace FluentGpu.Controls;

/// <summary>
/// A button that raises its click repeatedly while held (WinUI RepeatButton): the host's RepeatTicker fires the click
/// once on press, then after an initial delay, then at a steady interval, until release. Opting in is just
/// <see cref="BoxEl.Repeats"/> = true on a clickable node; the scheduling lives in the host (see RepeatTicker).
/// </summary>
public static partial class RepeatButton
{
    public sealed record Style
    {
        public ColorF Background { get; init; }
        public ColorF Foreground { get; init; }
        public ColorF HoverBackground { get; init; }
        public ColorF PressedBackground { get; init; }
        public GradientSpec? BorderBrush { get; init; }
        public float BorderWidth { get; init; } = 1f;
        public float CornerRadius { get; init; } = Radii.Control;
        public Edges4 Padding { get; init; } = new(11, 5, 11, 6);
        public float FontSize { get; init; } = 14f;
        public float MinHeight { get; init; } = 32f;
        public float PressScale { get; init; } = 0.985f;
    }

    public static Style? StyleOverride;
    public static Style DefaultStyle => StyleOverride ?? new Style
    {
        Background = Tok.FillControlDefault,
        Foreground = Tok.TextPrimary,
        HoverBackground = Tok.FillControlSecondary,
        PressedBackground = Tok.FillControlTertiary,
        BorderBrush = Tok.ControlElevationBorder,
    };

    public static BoxEl Create(string label, Action onClick, Style? style = null)
    {
        var s = style ?? DefaultStyle;
        return new BoxEl
        {
            Direction = 0,
            Role = AutomationRole.Button,
            Padding = s.Padding,
            MinHeight = s.MinHeight,
            AlignItems = FlexAlign.Center,
            Justify = FlexJustify.Center,
            Corners = CornerRadius4.All(s.CornerRadius),
            BorderWidth = s.BorderWidth,
            BorderBrush = s.BorderBrush,
            Fill = s.Background,
            HoverFill = s.HoverBackground,
            PressedFill = s.PressedBackground,
            PressScale = s.PressScale,
            Repeats = true,
            OnClick = onClick,
            Children = [new TextEl(label) { Size = s.FontSize, Color = s.Foreground }],
        };
    }
}
