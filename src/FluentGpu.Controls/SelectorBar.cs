using FluentGpu.Foundation;
using FluentGpu.Dsl;

namespace FluentGpu.Controls;

/// <summary>A WinUI SelectorBar: a segmented horizontal row of text items. The selected item is marked by a SHORT
/// CENTERED accent pill at the bottom (not a full-width underline, not bold); text stays <see cref="Tok.TextPrimary"/>
/// at rest for every item. Stateless — the caller owns <paramref name="selected"/> and reacts to
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
                        Color = Tok.TextPrimary,
                    },
                    // WinUI SelectorBarItem pill: base Width=4, Height=3, RadiusX=0.5/RadiusY=1; when selected it
                    // ScaleX-animates 1→4 (the ~16px shown here is that scaled result) and fades 0→1 opacity. The engine
                    // has no per-item ScaleX state machine, so the selected pill renders at its final 16px width with a
                    // near-flat 1px corner radius (was 1.5 / Radii.Circle(3f)).
                    new BoxEl
                    {
                        Width = 16f,
                        Height = 3f,
                        Corners = CornerRadius4.All(1f),
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
