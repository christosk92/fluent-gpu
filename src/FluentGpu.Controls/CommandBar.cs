using System.Collections.Generic;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Scene;

namespace FluentGpu.Controls;

/// <summary>A single primary command in a <see cref="CommandBar"/>, rendered as an AppBarButton.</summary>
public sealed record CommandBarButton(string Glyph, string Label, Action OnClick);

/// <summary>A WinUI CommandBar: a horizontal toolbar that holds primary commands (AppBarButtons). The CLOSED bar is
/// chromeless — fully transparent with no border at rest; the acrylic card + 1px border + 8px corner only appear when
/// the overflow opens. Primary commands are LEFT-aligned, with a trailing "..." More button pinned to the right.</summary>
public static class CommandBar
{
    /// <summary>CommandBar visual style — from WinUI DefaultCommandBarStyle (CommandBar_themeresources.xaml):
    /// closed Background=ControlFillColorTransparent, CornerRadius=ControlCornerRadius=4, AppBarThemeMinHeight=48,
    /// Padding 4,0,0,0. Open-state surface/border (AcrylicInApp + StrokeCardDefault) is carried for reference; the
    /// closed bar is chromeless. Disabled foreground = TextFillColorDisabled.</summary>
    public sealed record Style
    {
        public ColorF ClosedFill { get; init; }
        public ColorF OpenFill { get; init; }
        public ColorF OpenBorderBrush { get; init; }
        public ColorF Foreground { get; init; }
        public ColorF DisabledForeground { get; init; }
        public float BorderWidth { get; init; } = 1f;
        public float CornerRadius { get; init; } = Radii.Control;   // ControlCornerRadius = 4
        public Edges4 Padding { get; init; } = new(4, 0, 0, 0);
        public float MinHeight { get; init; } = 48f;                // AppBarThemeMinHeight
        public float Gap { get; init; } = 4f;
    }

    public static Style? StyleOverride;

    public static Style DefaultStyle => StyleOverride ?? new Style
    {
        ClosedFill = Tok.FillSubtleTransparent,                     // CommandBarBackground = ControlFillColorTransparent
        OpenFill = Tok.FillCardDefault,                            // approximates AcrylicInAppFillColorDefault on open
        OpenBorderBrush = Tok.StrokeCardDefault,                   // CommandBarBorderBrushOpen approximation
        Foreground = Tok.TextPrimary,
        DisabledForeground = Tok.TextDisabled,                     // CommandBarEllipsisIconForegroundDisabled
    };

    public static BoxEl Create(IReadOnlyList<CommandBarButton> commands)
    {
        var s = DefaultStyle;
        var children = new List<Element>(commands.Count + 2);
        foreach (var c in commands)
            children.Add(AppBarButton.Create(c.Glyph, c.Label, c.OnClick));

        // Spacer pushes the trailing "..." More button to the right edge of the chromeless bar.
        children.Add(new BoxEl { Grow = 1 });
        children.Add(AppBarButton.Create(Icons.More, "", () => { }));

        return new BoxEl
        {
            Direction = 0,
            AlignItems = FlexAlign.Center,
            Justify = FlexJustify.Start,
            Gap = s.Gap,
            MinHeight = s.MinHeight,
            Padding = s.Padding,
            Corners = CornerRadius4.All(s.CornerRadius),            // ControlCornerRadius = 4 (was unset)
            // Chromeless at rest: ControlFillColorTransparent surface, no border. (Open-state acrylic card + 1px border +
            // 8px corner appear only when the overflow opens — that animated open template is out of scope for one BoxEl.)
            Fill = s.ClosedFill,
            Children = children.ToArray(),
        };
    }
}
