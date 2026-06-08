using FluentGpu.Dsl;
using FluentGpu.Foundation;
using System;
using System.Collections.Generic;

namespace FluentGpu.Controls;

/// <summary>One tile in a <see cref="VariableSizedWrapGrid"/>: a label plus how many base cells it spans
/// horizontally (<paramref name="ColSpan"/>) and vertically (<paramref name="RowSpan"/>).</summary>
public sealed record WrapTile(string Label, int ColSpan, int RowSpan);

/// <summary>A WinUI <c>VariableSizedWrapGrid</c>: a grid that wraps tiles of varying sizes. This is a
/// self-contained, non-virtualized demo — fixed-base <paramref name="cell"/> tiles where some span
/// multiple cells. Tiles are greedily packed left-to-right into rows that hold <paramref name="columns"/>
/// cells (by ColSpan sum); a tile that would overflow the current row starts a new one. Cells are
/// contiguous (no inter-cell gap) — an N-span tile is exactly N*cell — matching the WinUI base panel,
/// which is an unstyled layout host (no per-tile chrome). A faint fill is kept only for visibility.</summary>
public static class VariableSizedWrapGrid
{
    public static BoxEl Create(IReadOnlyList<WrapTile> tiles, float cell = 60f, int columns = 4)
    {
        if (columns < 1) columns = 1;

        var rows = new List<Element>();
        var current = new List<Element>();
        int used = 0;

        foreach (var tile in tiles)
        {
            int span = Math.Max(1, tile.ColSpan);
            // Wrap when this tile's colspan would overflow the row's cell budget.
            if (used > 0 && used + span > columns)
            {
                rows.Add(Row(current));
                current = new List<Element>();
                used = 0;
            }

            current.Add(Tile(tile, cell));
            used += span;
        }

        if (current.Count > 0)
            rows.Add(Row(current));

        return new BoxEl
        {
            Direction = 1,
            Children = rows.ToArray(),
        };
    }

    private static BoxEl Row(List<Element> tiles) => new()
    {
        Direction = 0,
        Children = tiles.ToArray(),
    };

    private static BoxEl Tile(WrapTile tile, float cell)
    {
        int colSpan = Math.Max(1, tile.ColSpan);
        int rowSpan = Math.Max(1, tile.RowSpan);
        // Contiguous base cells: an N-span tile is exactly N*cell (no inter-cell gap). This is an unstyled
        // layout panel — no card border, no hover, no Button role; a faint fill is kept only for visibility.
        return new BoxEl
        {
            Width = cell * colSpan,
            Height = cell * rowSpan,
            Fill = Tok.FillSubtleSecondary,
            AlignItems = FlexAlign.Center,
            Justify = FlexJustify.Center,
            Children = [new TextEl(tile.Label) { Size = 13f, Color = Tok.TextPrimary }],
        };
    }
}
