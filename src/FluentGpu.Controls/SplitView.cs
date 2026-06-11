using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Scene;

namespace FluentGpu.Controls;

/// <summary>A WinUI SplitView: a fixed-width side <c>pane</c> and a flexible <c>content</c> area laid out in a row,
/// separated by a 1px divider. Purely structural layout — toggle <paramref name="isPaneOpen"/> to collapse the pane,
/// leaving the content to fill the whole control.</summary>
public static class SplitView
{
    // Template parts (see TemplateParts). Each part's doc lists the props the control OWNS (re-asserted after any
    // modifier — a Parts customization cannot win those).
    /// <summary>The fixed-width side pane (WinUI PaneRoot) — built only while the pane is open. Owned: Children
    /// (the pane content slot).</summary>
    public const string PartPane = "Pane";
    /// <summary>The flexible content area (WinUI ContentRoot). Owned: Children (the content slot).</summary>
    public const string PartContent = "Content";

    public static BoxEl Create(Element pane, Element content, float paneWidth = 240f, bool isPaneOpen = true,
                               TemplateParts? parts = null)
    {
        var contentBox = new BoxEl
        {
            Grow = 1f,
            ClipToBounds = true,
            Children = [content],
        };
        contentBox = parts.Apply(PartContent, contentBox) with { Children = contentBox.Children };   // structure = the content slot

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
        paneBox = parts.Apply(PartPane, paneBox) with { Children = paneBox.Children };   // structure = the pane slot

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
