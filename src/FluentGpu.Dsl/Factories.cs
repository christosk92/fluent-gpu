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

    /// <summary>The default button is the accent (primary) button, matching a WinUI <c>AccentButtonStyle</c>.</summary>
    public static BoxEl Button(string label, Action onClick)
        => FluentButton(Theme.Accent, Theme.AccentBorder, Theme.AccentText, label, onClick);

    /// <summary>A neutral WinUI standard button (subtle fill + stroke).</summary>
    public static BoxEl StandardButton(string label, Action onClick)
        => FluentButton(Theme.ControlFill, Theme.ControlBorder, Theme.ControlText, label, onClick);

    // WinUI 3 button: ControlCornerRadius = 4, content padding ~ (11,5,11,6) → min height ~32 with 14px Body text,
    // a 1px ControlStroke border. Rendered as a bordered rounded rect (border ring + inset fill) + centered label.
    private static BoxEl FluentButton(ColorF fill, ColorF border, ColorF text, string label, Action onClick)
        => new()
        {
            Direction = 0,
            Padding = new Edges4(12, 6, 12, 7),
            Fill = fill,
            BorderColor = border,
            BorderWidth = 1f,
            Corners = CornerRadius4.All(4),
            OnClick = onClick,
            Children = [new TextEl(label) { Size = 14f, Color = text }],
        };
}
