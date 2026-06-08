using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Scene;

namespace FluentGpu.Controls;

/// <summary>A WinUI <c>GridView</c>: a uniform grid of selectable tiles laid out as a column of horizontal rows.
/// This is a manual (non-virtualized) grid — items are chunked into rows of <c>columns</c> tiles each.
/// At rest each tile is TRANSPARENT with no border (WinUI tiles have no resting card chrome); hover paints
/// <see cref="Tok.FillSubtleSecondary"/>. The selected tile gets a neutral <see cref="Tok.FillSubtleTertiary"/>
/// fill plus a 2px <see cref="Tok.AccentDefault"/> border ring. Single-selection, controlled by an internal index.</summary>
public sealed class GridView : Component
{
    public IReadOnlyList<string> Items = [];
    public int Columns = 4;
    public float TileSize = 96f;

    public static Element Create(IReadOnlyList<string> items, int columns = 4, float tileSize = 96f)
        => Embed.Comp(() => new GridView { Items = items, Columns = columns, TileSize = tileSize });

    public override Element Render()
    {
        var (sel, setSel) = UseState(0);

        int columns = Columns < 1 ? 1 : Columns;

        var rows = new List<Element>();
        for (int start = 0; start < Items.Count; start += columns)
        {
            int end = System.Math.Min(start + columns, Items.Count);
            var tiles = new List<Element>(end - start);
            for (int j = start; j < end; j++)
            {
                int i = j; // capture per-tile index across the row loop
                bool selected = i == sel;
                tiles.Add(new BoxEl
                {
                    Width = TileSize,
                    Height = TileSize,
                    Corners = Radii.ControlAll,
                    Fill = selected ? Tok.FillSubtleTertiary : ColorF.Transparent,
                    BorderColor = selected ? Tok.AccentDefault : ColorF.Transparent,
                    BorderWidth = selected ? 2f : 0f,
                    HoverFill = Tok.FillSubtleSecondary,
                    AlignItems = FlexAlign.Center,
                    Justify = FlexJustify.Center,
                    Role = AutomationRole.Button,
                    OnClick = () => setSel(i),
                    Children = [new TextEl(Items[i]) { Size = 13f, Color = Tok.TextPrimary }],
                });
            }

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
}
