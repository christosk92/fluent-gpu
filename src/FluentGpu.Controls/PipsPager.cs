using FluentGpu.Foundation;
using FluentGpu.Dsl;

namespace FluentGpu.Controls;

public static class PipsPager
{
    // Template parts (see TemplateParts). Each part's doc lists the props the control OWNS (re-asserted after any
    // modifier \u2014 a Parts customization cannot win those).
    /// <summary>The horizontal pip strip (the returned root). Owned: Children (the dots), Role.</summary>
    public const string PartRoot = "Root";
    /// <summary>One 12x24 pip button \u2014 the modifier runs PER DOT (a small static count, not virtualized). The
    /// selected/normal glyph sizing is stock per-render styling a modifier may override. Owned: OnClick (select), Role.</summary>
    public const string PartDot = "Dot";

    const string PipGlyph = "\uEA3B";   // PipsPagerNormalGlyph / PipsPagerSelectedGlyph

    public static BoxEl Create(int count, int selected, Action<int> onSelect, TemplateParts? parts = null)
    {
        var dots = new Element[count < 0 ? 0 : count];
        for (int i = 0; i < dots.Length; i++)
        {
            int index = i;
            bool isSelected = index == selected;
            // WinUI PipsPager_themeresources: both states use glyph EA3B in the icon font; selected is font-size 6,
            // normal is font-size 4, inside a 12x24 selection-indicator button. The foreground is neutral, not accent.
            float glyphSize = isSelected ? 6f : 4f;
            Action select = () => onSelect(index);
            var dot = new BoxEl
            {
                Direction = 0,
                Width = 12f,
                Height = 24f,
                AlignItems = FlexAlign.Center,
                Justify = FlexJustify.Center,
                Role = AutomationRole.Pager,
                OnClick = select,
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
            // Parts: restyle each pip (the modifier sees the per-dot selected sizing); the select click always wins.
            dots[index] = parts.Apply(PartDot, dot) with { OnClick = select, Role = AutomationRole.Pager };
        }

        var root = new BoxEl
        {
            Direction = 0,
            Gap = 4f,
            AlignItems = FlexAlign.Center,
            Role = AutomationRole.Pager,
            Children = dots,
        };
        return parts.Apply(PartRoot, root) with { Children = dots, Role = AutomationRole.Pager };
    }
}
