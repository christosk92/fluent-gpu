using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Scene;

namespace FluentGpu.Controls;

/// <summary>A WinUI Pivot: a row of large (24px) text headers above a content region. Selection is shown by a 3px accent
/// underline pipe at the bottom (NOT bold weight): the selected header is <see cref="Tok.TextPrimary"/>, the rest are
/// <see cref="Tok.TextSecondary"/>. Clicking a header swaps the content shown below. Owns its own selection state.</summary>
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
                Direction = 1,
                Height = 48f,
                AlignItems = FlexAlign.Center,
                Justify = FlexJustify.Center,
                Padding = new Edges4(12, 0, 12, 0),
                Corners = Radii.ControlAll,
                Role = AutomationRole.Tab,
                OnClick = () => setSel(index),
                Children =
                [
                    new TextEl(Headers[index])
                    {
                        Size = 24f,
                        Color = isSelected ? Tok.TextPrimary : Tok.TextSecondary,
                    },
                    // WinUI PivotHeaderItem SelectedPipe: Height=3, HorizontalAlignment=Stretch (full header width),
                    // CornerRadius=PivotHeaderItemSelectedPipeCornerRadius=1.5, Margin="0,0,0,2" (no right inset).
                    new BoxEl
                    {
                        AlignSelf = FlexAlign.Stretch,   // stretch to full header width (was fixed 24f)
                        Height = 3f,
                        Corners = Radii.Circle(3f),      // diameter 3 → radius 1.5 (PivotHeaderItemSelectedPipeCornerRadius)
                        Fill = isSelected ? Tok.AccentDefault : ColorF.Transparent,
                        Margin = new Edges4(0, 0, 0, 2), // was (0,4,0,2); WinUI margin "0,0,0,2"
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
                    Gap = 4f,
                    AlignItems = FlexAlign.End,
                    Padding = new Edges4(0, 0, 0, 0),
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
