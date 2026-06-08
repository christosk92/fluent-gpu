using FluentGpu.Foundation;
using FluentGpu.Dsl;

namespace FluentGpu.Controls;

public static class PipsPager
{
    public static BoxEl Create(int count, int selected, Action<int> onSelect)
    {
        var dots = new Element[count < 0 ? 0 : count];
        for (int i = 0; i < dots.Length; i++)
        {
            int index = i;
            bool isSelected = index == selected;
            // WinUI: selection is shown by a LARGER neutral dot, NOT an accent color. Both states use
            // PipsPagerSelectionIndicatorForeground = ControlStrongFillColorDefault (rest/selected) and shift to
            // TextFillColorSecondary on pointer-over (PipsPagerSelectionIndicatorForegroundPointerOver/Pressed).
            float dotSize = isSelected ? 6f : 4f;
            var glyph = new BoxEl
            {
                Width = dotSize,
                Height = dotSize,
                Corners = Radii.Circle(dotSize),
                Fill = Tok.FillControlStrong,
                HoverFill = Tok.TextSecondary,
            };
            dots[index] = new BoxEl
            {
                Direction = 0,
                Width = 12f,
                Height = 24f,
                AlignItems = FlexAlign.Center,
                Justify = FlexJustify.Center,
                Role = AutomationRole.Pager,
                OnClick = () => onSelect(index),
                Children = [glyph],
            };
        }

        return new BoxEl
        {
            Direction = 0,
            Gap = 4f,
            AlignItems = FlexAlign.Center,
            Role = AutomationRole.Pager,
            Children = dots,
        };
    }
}
