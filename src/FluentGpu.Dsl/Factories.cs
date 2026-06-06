using FluentGpu.Foundation;

namespace FluentGpu.Dsl;

/// <summary>Fluent C# markup. <c>using static FluentGpu.Dsl.Ui;</c> to author trees as expressions.</summary>
public static class Ui
{
    private static readonly ColorF Accent = ColorF.FromRgba(0x2B, 0x88, 0xD8);
    private static readonly ColorF OnAccent = ColorF.FromRgba(0xFF, 0xFF, 0xFF);

    public static BoxEl VStack(float gap, params Element[] children)
        => new() { Direction = 1, Gap = gap, Children = children };

    public static BoxEl HStack(float gap, params Element[] children)
        => new() { Direction = 0, Gap = gap, Children = children };

    public static TextEl Heading(string text)
        => new(text) { Size = 24f, Bold = true, Color = ColorF.FromRgba(0xF2, 0xF2, 0xF2) };

    public static TextEl Text(string text)
        => new(text) { Size = 14f };

    public static BoxEl Button(string label, Action onClick)
        => new()
        {
            Direction = 0,
            Padding = new Edges4(14, 8, 14, 8),
            Fill = Accent,
            Corners = CornerRadius4.All(6),
            OnClick = onClick,
            Children = [new TextEl(label) { Size = 16f, Color = OnAccent }],
        };
}
