using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Scene;

namespace FluentGpu.Controls;

/// <summary>A WinUI BreadcrumbBar: a horizontal trail of items separated by right chevrons. The last item is the current
/// page (TextPrimary, bold, non-interactive-looking); earlier items render secondary and are clickable, raising
/// <c>onSelect(index)</c>. Per-part restyling goes through the optional <paramref name="parts"/> (see
/// <see cref="TemplateParts"/> for the contract).</summary>
public static class BreadcrumbBar
{
    // Template parts (the WinUI x:Name vocabulary; see TemplateParts). Each part's doc lists the props the control
    // OWNS (re-asserted after any modifier — a parts customization cannot win those).
    /// <summary>Each crumb's box — the SAME modifier runs for every crumb, the last/current one included. Owned:
    /// OnClick (navigate — re-asserted on the clickable, non-last crumbs only), Role.</summary>
    public const string PartItem = "Item";
    /// <summary>The right-chevron separator between crumbs (WinUI PART_ChevronTextBlock) — a <see cref="TextEl"/>,
    /// so style it via the generic map: <c>parts.Set&lt;TextEl&gt;(BreadcrumbBar.PartSeparator, t =&gt; t with { … })</c>.
    /// Owned: nothing (pure styling).</summary>
    public const string PartSeparator = "Separator";

    public static BoxEl Create(IReadOnlyList<string> items, Action<int>? onSelect = null, TemplateParts? parts = null)
    {
        var children = new List<Element>(items.Count * 2);

        for (int i = 0; i < items.Count; i++)
        {
            bool isLast = i == items.Count - 1;

            if (isLast)
            {
                var crumb = new BoxEl
                {
                    Direction = 0,
                    AlignItems = FlexAlign.Center,
                    // WinUI BreadcrumbBar Button/LastItemContentPresenter Padding="1,3".
                    Padding = new Edges4(1, 3, 1, 3),
                    Corners = Radii.ControlAll,
                    Role = AutomationRole.Button,
                    Children = [new TextEl(items[i]) { Size = 14f, Bold = true, Color = Tok.TextPrimary }],
                };
                children.Add(parts.Apply(PartItem, crumb) with { Role = AutomationRole.Button });
            }
            else
            {
                int index = i;
                Action click = () => onSelect?.Invoke(index);
                var crumb = new BoxEl
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
                    OnClick = click,
                    Children = [new TextEl(items[index]) { Size = 14f, Color = Tok.TextPrimary }],
                };
                // Parts: restyle anything; the navigate mechanics always win.
                children.Add(parts.Apply(PartItem, crumb) with { OnClick = click, Role = AutomationRole.Button });

                children.Add(parts.Apply(PartSeparator, new TextEl(Icons.ChevronRight)
                {
                    Size = 12f,
                    // PART_ChevronTextBlock = BreadcrumbBarNormalForegroundBrush = TextFillColorPrimary (was TextTertiary).
                    Color = Tok.TextPrimary,
                    FontFamily = Theme.IconFont,
                    Margin = new Edges4(2, 0, 2, 0),
                }));
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
