using System.Collections.Generic;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Scene;

namespace FluentGpu.Controls;

/// <summary>A single primary command in a <see cref="CommandBar"/>, rendered as an AppBarButton.</summary>
public sealed record CommandBarButton(string Glyph, string Label, Action OnClick);

/// <summary>A WinUI CommandBar: a horizontal toolbar that holds primary commands (AppBarButtons) flushed
/// to the right on a card surface. Sits at the top of a content region as a Fluent overlay bar.</summary>
public static class CommandBar
{
    public static BoxEl Create(IReadOnlyList<CommandBarButton> commands)
    {
        var children = new List<Element>(commands.Count);
        foreach (var c in commands)
            children.Add(AppBarButton.Create(c.Glyph, c.Label, c.OnClick));

        return new BoxEl
        {
            Direction = 0,
            AlignItems = FlexAlign.Center,
            Justify = FlexJustify.End,
            Gap = 4,
            MinHeight = 48,
            Padding = new Edges4(8, 4, 8, 4),
            Corners = Radii.OverlayAll,
            Fill = Tok.FillCardDefault,
            BorderColor = Tok.StrokeCardDefault,
            BorderWidth = 1,
            Children = children.ToArray(),
        };
    }
}
