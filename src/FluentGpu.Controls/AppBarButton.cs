using FluentGpu.Dsl;
using FluentGpu.Foundation;

namespace FluentGpu.Controls;

/// <summary>A WinUI AppBarButton: a vertically-stacked command button with a 16px icon glyph over a 12px label
/// (icon size = AppBarButtonContentHeight=16). FullSize metrics: a fixed 68px width and a 64px min height
/// (AppBarThemeMinHeight), so a row of them reads as the WinUI CommandBar. Hover/press use the subtle fill tokens;
/// the whole control is one click target with a 4px corner. Disabled dims fill + foreground.</summary>
public static class AppBarButton
{
    /// <summary>AppBarButton visual style — dimensions and color tokens from WinUI 3 (AppBarButton_themeresources.xaml:
    /// Width 68, MinHeight 64, icon 16px, label 12px, ControlCornerRadius 4, inner-border margin 2,6,2,6).</summary>
    public sealed record Style
    {
        public float Width { get; init; } = 68f;
        public float MinHeight { get; init; } = 64f;                 // AppBarThemeMinHeight
        public float IconHeight { get; init; } = 16f;                // AppBarButtonContentHeight
        public float LabelFontSize { get; init; } = 12f;
        public float Gap { get; init; } = 4f;
        public float CornerRadius { get; init; } = Radii.Control;    // ControlCornerRadius = 4
        public Edges4 Padding { get; init; } = new(2, 6, 2, 6);      // AppBarButtonInnerBorderMargin

        public ColorF RestFill { get; init; }
        public ColorF RestForeground { get; init; }
        public ColorF HoverFill { get; init; }
        public ColorF HoverForeground { get; init; }
        public ColorF PressedFill { get; init; }
        public ColorF PressedForeground { get; init; }
        public ColorF DisabledFill { get; init; }
        public ColorF DisabledForeground { get; init; }
    }

    public static Style? StyleOverride;

    public static Style DefaultStyle => StyleOverride ?? new Style
    {
        RestFill = Tok.FillSubtleTransparent,                        // AppBarButtonBackground = SubtleFillColorTransparent
        RestForeground = Tok.TextPrimary,                           // AppBarButtonForeground = TextFillColorPrimary
        HoverFill = Tok.FillSubtleSecondary,                        // AppBarButtonBackgroundPointerOver
        HoverForeground = Tok.TextPrimary,                          // AppBarButtonForegroundPointerOver = TextFillColorPrimary
        PressedFill = Tok.FillSubtleTertiary,                       // AppBarButtonBackgroundPressed
        PressedForeground = Tok.TextSecondary,                      // AppBarButtonForegroundPressed = TextFillColorSecondary (WinUI dims on press)
        DisabledFill = Tok.FillControlDisabled,                     // AppBarButtonBackgroundDisabled = SubtleFillColorDisabled (no FillSubtleDisabled token; FillControlDisabled is the nearest)
        DisabledForeground = Tok.TextDisabled,                      // AppBarButtonForegroundDisabled = TextFillColorDisabled
    };

    public static BoxEl Create(string glyph, string label, Action onClick, bool enabled = true, Style? style = null)
    {
        var s = style ?? DefaultStyle;
        // WinUI dims the foreground to TextSecondary on press via a per-state foreground; the engine BoxEl carries no
        // per-pointer-state text color, so the surface swap to PressedFill carries the press. Disabled swaps fill +
        // text to the disabled tokens up front (the engine has no IsEnabled gate on BoxEl).
        var foreground = enabled ? s.RestForeground : s.DisabledForeground;
        return new BoxEl
        {
            Direction = 1,
            AlignItems = FlexAlign.Center,
            Justify = FlexJustify.Center,
            Gap = s.Gap,
            Width = s.Width,
            MinHeight = s.MinHeight,
            Padding = s.Padding,                                    // AppBarButtonInnerBorderMargin = 2,6,2,6
            Corners = CornerRadius4.All(s.CornerRadius),
            Fill = enabled ? s.RestFill : s.DisabledFill,
            HoverFill = enabled ? s.HoverFill : s.DisabledFill,
            PressedFill = enabled ? s.PressedFill : s.DisabledFill,
            OnClick = enabled ? onClick : null,
            HitTestVisible = enabled,
            Role = AutomationRole.Button,
            Children =
            [
                new TextEl(glyph) { Size = s.IconHeight, Color = foreground, FontFamily = Theme.IconFont },
                new TextEl(label) { Size = s.LabelFontSize, Color = foreground },
            ],
        };
    }
}
