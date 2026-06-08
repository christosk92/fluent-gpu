using FluentGpu.Dsl;
using FluentGpu.Foundation;

namespace FluentGpu.Controls;

/// <summary>A WinUI <c>GridView</c>: a uniform grid of card tiles laid out as a column of horizontal rows.
/// This is a manual (non-virtualized) grid — items are chunked into rows of <paramref name="columns"/> tiles each.
/// Each tile is a square card with a centered label, card fill/stroke, and a hover state, exposed as a button to a11y.</summary>
public static class GridView
{
    public static BoxEl Create(IReadOnlyList<string> items, int columns = 4, float tileSize = 96f)
    {
        if (columns < 1) columns = 1;

        var rows = new List<Element>();
        for (int start = 0; start < items.Count; start += columns)
        {
            int end = System.Math.Min(start + columns, items.Count);
            var tiles = new List<Element>(end - start);
            for (int i = start; i < end; i++)
                tiles.Add(Tile(items[i], tileSize));

            rows.Add(new BoxEl
            {
                Direction = 0,
                Gap = 8f,
                Children = tiles.ToArray(),
            });
        }

        return new BoxEl
        {
            Direction = 1,
            Gap = 8f,
            Children = rows.ToArray(),
        };
    }

    private static BoxEl Tile(string label, float tileSize) => new()
    {
        Width = tileSize,
        Height = tileSize,
        Corners = Radii.OverlayAll,
        Fill = Tok.FillCardDefault,
        BorderColor = Tok.StrokeCardDefault,
        BorderWidth = 1f,
        HoverFill = Tok.FillCardSecondary,
        AlignItems = FlexAlign.Center,
        Justify = FlexJustify.Center,
        Role = AutomationRole.Button,
        Children = [new TextEl(label) { Size = 13f, Color = Tok.TextPrimary }],
    };
}
