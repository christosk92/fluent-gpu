using FluentGpu.Dsl;
using FluentGpu.Foundation;

namespace FluentGpu.Controls;

/// <summary>A WinUI AppBarSeparator: a thin divider between groups of commands in a CommandBar. The default
/// (FullSize/Compact) orientation is a 1px-wide VERTICAL line stretching the bar height with the
/// <c>AppBarSeparatorMargin</c> 2,8,2,8 inset; the <c>Overflow</c> visual state flips it HORIZONTAL — full-width,
/// 1px tall, margin 0,4,0,4 (AppBarSeparator_themeresources.xaml:15-18 margins/sizes; :40-47 Overflow state setters).
/// Fill = AppBarSeparatorForeground = DividerStrokeColorDefault (:5); RadiusX/Y = AppBarSeparatorCornerRadius 0.5 (:19).</summary>
public static class AppBarSeparator
{
    /// <summary>The default vertical separator (FullSize/Compact states).</summary>
    public static BoxEl Create() => Create(overflow: false);

    /// <summary><paramref name="overflow"/> = the Overflow visual state: a horizontal 1px divider row
    /// (SeparatorRectangle Width=NaN/Stretch, Height=AppBarOverflowSeparatorHeight 1, Margin 0,4,0,4).</summary>
    public static BoxEl Create(bool overflow) => overflow
        ? new BoxEl
        {
            Height = 1f,                              // AppBarOverflowSeparatorHeight (AppBarSeparator_themeresources.xaml:18)
            Fill = Tok.StrokeDividerDefault,          // AppBarSeparatorForeground = DividerStrokeColorDefaultBrush (:5)
            Margin = new Edges4(0, 4, 0, 4),          // AppBarOverflowSeparatorMargin (:16)
            AlignSelf = FlexAlign.Stretch,            // HorizontalAlignment = Stretch (:43)
            Corners = CornerRadius4.All(0.5f),        // AppBarSeparatorCornerRadius (:19)
        }
        : new BoxEl
        {
            Width = 1f,                               // AppBarSeparatorWidth (:17)
            Fill = Tok.StrokeDividerDefault,          // AppBarSeparatorForeground (:5)
            Margin = new Edges4(2, 8, 2, 8),          // AppBarSeparatorMargin (:15)
            AlignSelf = FlexAlign.Stretch,            // VerticalAlignment = Stretch (template :50)
            Corners = CornerRadius4.All(0.5f),        // AppBarSeparatorCornerRadius 0.5 (:19) — audit fix (was 1)
        };
}
