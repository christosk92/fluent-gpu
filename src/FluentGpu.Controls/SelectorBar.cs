using FluentGpu.Foundation;
using FluentGpu.Dsl;

namespace FluentGpu.Controls;

/// <summary>A WinUI SelectorBar: a segmented horizontal row of text items. The selected item gets an accent
/// underline (a ~3px accent bar pinned to the bottom) and <see cref="Tok.TextPrimary"/>; the rest are
/// <see cref="Tok.TextSecondary"/>. Stateless — the caller owns <paramref name="selected"/> and reacts to
/// <paramref name="onSelect"/>.</summary>
public static class SelectorBar
{
    public static BoxEl Create(IReadOnlyList<string> items, int selected, Action<int> onSelect)
    {
        var count = items?.Count ?? 0;
        var tabs = new Element[count];
        for (int i = 0; i < count; i++)
        {
            int index = i;
            bool isSelected = index == selected;
            tabs[index] = new BoxEl
            {
                Direction = 1,
                AlignItems = FlexAlign.Center,
                Padding = new Edges4(12, 10, 12, 7),
                Corners = Radii.ControlAll,
                HoverFill = Tok.FillSubtleSecondary,
                Role = AutomationRole.Tab,
                OnClick = () => onSelect(index),
                Children =
                [
                    new TextEl(items![index])
                    {
                        Size = 14f,
                        Color = isSelected ? Tok.TextPrimary : Tok.TextSecondary,
                        Bold = isSelected,
                    },
                    new BoxEl
                    {
                        Height = 3f,
                        Corners = Radii.Circle(3f),
                        Fill = isSelected ? Tok.AccentDefault : ColorF.Transparent,
                        Margin = new Edges4(0, 4, 0, 0),
                    },
                ],
            };
        }

        return new BoxEl
        {
            Direction = 0,
            Gap = 4f,
            AlignItems = FlexAlign.Stretch,
            Role = AutomationRole.Tab,
            Children = tabs,
        };
    }
}
