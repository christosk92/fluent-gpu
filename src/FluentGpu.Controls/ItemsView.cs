using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Scene;

namespace FluentGpu.Controls;

/// <summary>A WinUI <c>ItemsView</c>: a modern, single-selectable collection presented as a uniform grid of tiles.
/// Items are chunked into rows of <see cref="Columns"/> tiles. At rest each tile is TRANSPARENT with no border;
/// hover paints <see cref="Tok.FillSubtleSecondary"/>. Clicking a tile selects it; the selected tile gets a 3px
/// <see cref="Tok.AccentDefault"/> border ring (no accent fill). Each tile is exposed as a button to a11y.</summary>
public sealed class ItemsView : Component
{
    public IReadOnlyList<string> Items = [];
    public int Columns = 4;

    public static Element Create(IReadOnlyList<string> items, int columns = 4)
        => Embed.Comp(() => new ItemsView { Items = items, Columns = columns });

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
                string label = Items[i];
                bool selected = i == sel;
                tiles.Add(new BoxEl
                {
                    Width = 110f,
                    Height = 80f,
                    Corners = Radii.ControlAll,
                    Fill = ColorF.Transparent,
                    BorderColor = selected ? Tok.AccentDefault : ColorF.Transparent,
                    BorderWidth = selected ? 3f : 0f,
                    HoverFill = Tok.FillSubtleSecondary,
                    AlignItems = FlexAlign.Center,
                    Justify = FlexJustify.Center,
                    Role = AutomationRole.Button,
                    OnClick = () => setSel(i),
                    Children = [new TextEl(label) { Size = 13f, Color = Tok.TextPrimary }],
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
