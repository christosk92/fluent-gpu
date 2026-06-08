using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Scene;

namespace FluentGpu.Controls;

/// <summary>A WinUI BreadcrumbBar: a horizontal trail of items separated by right chevrons. The last item is the current
/// page (TextPrimary, bold, non-interactive-looking); earlier items render secondary and are clickable, raising
/// <c>onSelect(index)</c>.</summary>
public static class BreadcrumbBar
{
    public static BoxEl Create(IReadOnlyList<string> items, Action<int>? onSelect = null)
    {
        var children = new List<Element>(items.Count * 2);

        for (int i = 0; i < items.Count; i++)
        {
            bool isLast = i == items.Count - 1;

            if (isLast)
            {
                children.Add(new BoxEl
                {
                    Direction = 0,
                    AlignItems = FlexAlign.Center,
                    // WinUI BreadcrumbBar Button/LastItemContentPresenter Padding="1,3".
                    Padding = new Edges4(1, 3, 1, 3),
                    Corners = Radii.ControlAll,
                    Role = AutomationRole.Button,
                    Children = [new TextEl(items[i]) { Size = 14f, Bold = true, Color = Tok.TextPrimary }],
                });
            }
            else
            {
                int index = i;
                children.Add(new BoxEl
                {
                    Direction = 0,
                    AlignItems = FlexAlign.Center,
                    Padding = new Edges4(1, 3, 1, 3),
                    Corners = Radii.ControlAll,
                    // WinUI breadcrumb buttons keep a transparent background in every state; the PointerOver/Pressed
                    // change is a TEXT-color transition (BreadcrumbBarHoverForegroundBrush = TextFillColorSecondary),
                    // not a fill. The engine has no per-state text color, so non-last items render at the rest
                    // foreground (BreadcrumbBarNormalForegroundBrush = TextFillColorPrimary) with no fill swap.
                    Role = AutomationRole.Button,
                    OnClick = () => onSelect?.Invoke(index),
                    Children = [new TextEl(items[index]) { Size = 14f, Color = Tok.TextPrimary }],
                });

                children.Add(new TextEl(Icons.ChevronRight)
                {
                    Size = 12f,
                    // PART_ChevronTextBlock = BreadcrumbBarNormalForegroundBrush = TextFillColorPrimary (was TextTertiary).
                    Color = Tok.TextPrimary,
                    FontFamily = Theme.IconFont,
                    Margin = new Edges4(2, 0, 2, 0),
                });
            }
        }

        return new BoxEl
        {
            Direction = 0,
            AlignItems = FlexAlign.Center,
            Children = children.ToArray(),
        };
    }
}
