using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Scene;

namespace FluentGpu.Controls;

/// <summary>A WinUI SplitView: a fixed-width side <c>pane</c> and a flexible <c>content</c> area laid out in a row,
/// separated by a 1px divider. Purely structural layout — toggle <paramref name="isPaneOpen"/> to collapse the pane,
/// leaving the content to fill the whole control.</summary>
public static class SplitView
{
    public static BoxEl Create(Element pane, Element content, float paneWidth = 240f, bool isPaneOpen = true)
    {
        var contentBox = new BoxEl
        {
            Grow = 1f,
            ClipToBounds = true,
            Children = [content],
        };

        if (!isPaneOpen)
        {
            return new BoxEl
            {
                Direction = 0,
                Grow = 1f,
                Children = [contentBox],
            };
        }

        var paneBox = new BoxEl
        {
            Width = paneWidth,
            Fill = Tok.FillSolidBaseAlt,
            ClipToBounds = true,
            Children = [pane],
        };

        var divider = new BoxEl
        {
            Width = 1f,
            AlignSelf = FlexAlign.Stretch,
            Fill = Tok.StrokeDividerDefault,
        };

        return new BoxEl
        {
            Direction = 0,
            Grow = 1f,
            Children = [paneBox, divider, contentBox],
        };
    }
}
