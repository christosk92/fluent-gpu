using FluentGpu.Dsl;
using FluentGpu.Foundation;

namespace FluentGpu.Controls;

/// <summary>A WinUI AppBarSeparator: a thin vertical divider used between groups of commands
/// in a CommandBar. Renders as a 1px-wide stroke-colored line that STRETCHES to fill the bar's
/// height (AlignSelf = Stretch), with 2px horizontal / 8px vertical margin so it floats clear of
/// the bar edges and adjacent command buttons. Slightly rounded.</summary>
public static class AppBarSeparator
{
    public static BoxEl Create() => new BoxEl
    {
        Width = 1,
        Fill = Tok.StrokeDividerDefault,
        Margin = new Edges4(2, 8, 2, 8),
        AlignSelf = FlexAlign.Stretch,
        Corners = Radii.Circle(1),
    };
}
