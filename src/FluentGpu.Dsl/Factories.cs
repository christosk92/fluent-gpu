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

    // Controls (Button, …) live in their own classes under Controls/ — e.g. Button.Accent(...) / Button.Standard(...).
}
