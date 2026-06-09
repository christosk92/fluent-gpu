using FluentGpu.Foundation;
using FluentGpu.Dsl;

namespace FluentGpu.Controls;

public static class PipsPager
{
    const string PipGlyph = "\uEA3B";   // PipsPagerNormalGlyph / PipsPagerSelectedGlyph

    public static BoxEl Create(int count, int selected, Action<int> onSelect)
    {
        var dots = new Element[count < 0 ? 0 : count];
        for (int i = 0; i < dots.Length; i++)
        {
            int index = i;
            bool isSelected = index == selected;
            // WinUI PipsPager_themeresources: both states use glyph EA3B in the icon font; selected is font-size 6,
            // normal is font-size 4, inside a 12x24 selection-indicator button. The foreground is neutral, not accent.
            float glyphSize = isSelected ? 6f : 4f;
            dots[index] = new BoxEl
            {
                Direction = 0,
                Width = 12f,
                Height = 24f,
                AlignItems = FlexAlign.Center,
                Justify = FlexJustify.Center,
                Role = AutomationRole.Pager,
                OnClick = () => onSelect(index),
                Children =
                [
                    new TextEl(PipGlyph)
                    {
                        Size = glyphSize,
                        Color = Tok.FillControlStrong,
                        HoverColor = Tok.TextSecondary,
                        PressedColor = Tok.TextSecondary,
                        FontFamily = Theme.IconFont,
                    },
                ],
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
