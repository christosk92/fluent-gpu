using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Scene;

namespace FluentGpu.Controls;

/// <summary>
/// A WinUI ListView: a vertical list of selectable rows. Each row is a transparent pill that highlights on hover
/// (<see cref="Tok.FillSubtleSecondary"/>) and press (<see cref="Tok.FillSubtleTertiary"/>); the selected row gets an
/// <see cref="Tok.AccentSubtle"/> fill plus a 3px accent left bar. Single-selection, controlled by an internal index.
/// </summary>
public sealed class ListView : Component
{
    public IReadOnlyList<string> Items = [];

    public static Element Create(IReadOnlyList<string> items)
        => Embed.Comp(() => new ListView { Items = items });

    public override Element Render()
    {
        var (sel, setSel) = UseState(0);

        var rows = new Element[Items.Count];
        for (int i = 0; i < Items.Count; i++)
        {
            int idx = i;                  // capture for the click closure
            bool selected = idx == sel;

            // 3px accent left bar — only present on the selected row, vertically centered.
            var accentBar = new BoxEl
            {
                Width = 3f, Height = 20f,
                Corners = Radii.Circle(3f),
                Fill = Tok.AccentDefault,
                AlignSelf = FlexAlign.Center,
            };

            var label = new TextEl(Items[idx])
            {
                Size = 14f,
                Color = Tok.TextPrimary,
                Grow = 1f,
            };

            // Inner row layout: [accent bar?] [label]. Keeping the bar as the first child at the far left.
            var content = new BoxEl
            {
                Direction = 0,
                AlignItems = FlexAlign.Center,
                Gap = 9f,
                Grow = 1f,
                Children = selected ? [accentBar, label] : [label],
            };

            rows[idx] = new BoxEl
            {
                ZStack = true,
                MinHeight = 40f,
                AlignItems = FlexAlign.Center,
                Padding = new Edges4(12, 0, 12, 0),
                Margin = new Edges4(4, 2, 4, 2),
                Corners = Radii.ControlAll,
                Fill = selected ? Tok.AccentSubtle : ColorF.Transparent,
                HoverFill = Tok.FillSubtleSecondary,
                PressedFill = Tok.FillSubtleTertiary,
                Role = AutomationRole.Button,
                OnClick = () => setSel(idx),
                Children = [content],
            };
        }

        return new BoxEl
        {
            Direction = 1,
            Children = rows,
        };
    }
}
