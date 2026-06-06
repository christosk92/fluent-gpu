using FluentGpu.Foundation;

namespace FluentGpu.Dsl;

/// <summary>Fluent C# markup. <c>using static FluentGpu.Dsl.Ui;</c> to author trees as expressions.</summary>
public static class Ui
{
    public static BoxEl VStack(float gap, params Element[] children)
        => new() { Direction = 1, Gap = gap, Children = children };

    public static BoxEl HStack(float gap, params Element[] children)
        => new() { Direction = 0, Gap = gap, Children = children };

    /// <summary>A padded panel (container with inner padding) — gives content WinUI-like breathing room.</summary>
    public static BoxEl Panel(Edges4 padding, float gap, params Element[] children)
        => new() { Direction = 1, Gap = gap, Padding = padding, Children = children };

    public static TextEl Heading(string text)
        => new(text) { Size = 28f, Bold = true, Color = Theme.WindowText };

    public static TextEl Text(string text)
        => new(text) { Size = 14f, Color = Theme.WindowText };

    /// <summary>A button. Barebone behavior (clickable + focusable) + a style on top — defaults to the Fluent accent
    /// style, fully overrideable by passing your own <see cref="ButtonStyle"/> (or chaining modifiers on the result).</summary>
    public static BoxEl Button(string label, Action onClick, ButtonStyle? style = null)
    {
        var s = style ?? Theme.AccentButton;
        return new BoxEl
        {
            Direction = 0,
            Padding = s.Padding,
            Fill = s.Background,
            HoverFill = s.HoverBackground,
            PressedFill = s.PressedBackground,
            BorderColor = s.Border,
            BorderWidth = s.BorderWidth,
            Corners = CornerRadius4.All(s.CornerRadius),
            OnClick = onClick,
            Children = [new TextEl(label) { Size = s.FontSize, Bold = s.Bold, Color = s.Foreground }],
        };
    }

    /// <summary>A neutral WinUI standard button (subtle fill + stroke) — i.e. Button with the standard style.</summary>
    public static BoxEl StandardButton(string label, Action onClick) => Button(label, onClick, Theme.StandardButton);
}
