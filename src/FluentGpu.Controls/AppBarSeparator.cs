using FluentGpu.Dsl;
using FluentGpu.Foundation;

namespace FluentGpu.Controls;

/// <summary>A WinUI AppBarSeparator: a thin vertical divider used between groups of commands
/// in a CommandBar. Renders as a 1px-wide, 24px-tall stroke-colored line, centered vertically,
/// with 8px of horizontal margin on each side to space it from adjacent command buttons.</summary>
public static class AppBarSeparator
{
    public static BoxEl Create() => new BoxEl
    {
        Width = 1,
        Height = 24,
        Fill = Tok.StrokeDividerDefault,
        Margin = new Edges4(8, 0, 8, 0),
        AlignSelf = FlexAlign.Center,
    };
}
