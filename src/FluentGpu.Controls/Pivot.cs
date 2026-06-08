using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Scene;

namespace FluentGpu.Controls;

/// <summary>A WinUI Pivot: a row of large text headers above a content region. The selected header is bold and
/// <see cref="Tok.TextPrimary"/>; the rest are <see cref="Tok.TextSecondary"/>. Clicking a header swaps the content
/// shown below to that pivot's content. Owns its own selection state.</summary>
public sealed class Pivot : Component
{
    public IReadOnlyList<string> Headers = [];

    public static Element Create(IReadOnlyList<string> headers)
        => Embed.Comp(() => new Pivot { Headers = headers });

    public override Element Render()
    {
        var (sel, setSel) = UseState(0);

        var count = Headers.Count;
        if (count == 0)
            return new BoxEl { Direction = 1, Grow = 1f };

        int selected = sel < 0 ? 0 : (sel >= count ? count - 1 : sel);

        var headerItems = new Element[count];
        for (int i = 0; i < count; i++)
        {
            int index = i;
            bool isSelected = index == selected;
            headerItems[index] = new BoxEl
            {
                Direction = 0,
                AlignItems = FlexAlign.Center,
                Padding = new Edges4(2, 4, 2, 4),
                Corners = Radii.ControlAll,
                HoverFill = Tok.FillSubtleSecondary,
                Role = AutomationRole.Tab,
                OnClick = () => setSel(index),
                Children =
                [
                    new TextEl(Headers[index])
                    {
                        Size = 18f,
                        Bold = isSelected,
                        Color = isSelected ? Tok.TextPrimary : Tok.TextSecondary,
                    },
                ],
            };
        }

        return new BoxEl
        {
            Direction = 1,
            Grow = 1f,
            Children =
            [
                new BoxEl
                {
                    Direction = 0,
                    Gap = 20f,
                    AlignItems = FlexAlign.Center,
                    Padding = new Edges4(4, 4, 4, 12),
                    Children = headerItems,
                },
                new BoxEl
                {
                    Direction = 1,
                    Grow = 1f,
                    Padding = Edges4.All(8),
                    Children =
                    [
                        new TextEl($"Content for {Headers[selected]}")
                        {
                            Size = 14f,
                            Color = Tok.TextPrimary,
                        },
                    ],
                },
            ],
        };
    }
}
