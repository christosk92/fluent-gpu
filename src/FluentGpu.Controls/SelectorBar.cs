using FluentGpu.Foundation;
using FluentGpu.Dsl;

namespace FluentGpu.Controls;

/// <summary>A WinUI SelectorBar: a segmented horizontal row of text items. The selected item is marked by a SHORT
/// CENTERED accent pill at the bottom (not a full-width underline, not bold); text stays <see cref="Tok.TextPrimary"/>
/// at rest for every item. Stateless — the caller owns <paramref name="selected"/> and reacts to
/// <paramref name="onSelect"/>. Per-part restyling goes through the optional <paramref name="parts"/> (see
/// <see cref="TemplateParts"/> for the contract).</summary>
public static class SelectorBar
{
    // Template parts (the WinUI x:Name vocabulary; see TemplateParts). Each part's doc lists the props the control
    // OWNS (re-asserted after any modifier — a parts customization cannot win those).
    /// <summary>Each item's clickable box (WinUI SelectorBarItem). The SAME modifier runs for every item — branch on
    /// caller state for per-item styling. Owned: OnClick (select), Role.</summary>
    public const string PartItem = "Item";
    /// <summary>The short centered selection pill under each item (rendered transparent when unselected). Owned:
    /// nothing — pure styling.</summary>
    public const string PartPill = "Pill";

    public static BoxEl Create(IReadOnlyList<string> items, int selected, Action<int> onSelect, TemplateParts? parts = null)
    {
        var count = items?.Count ?? 0;
        var tabs = new Element[count];
        for (int i = 0; i < count; i++)
        {
            int index = i;
            bool isSelected = index == selected;
            Action select = () => onSelect(index);

            // WinUI SelectorBarItem pill: base Width=4, Height=3, RadiusX=0.5/RadiusY=1; when selected it
            // ScaleX-animates 1→4 (the ~16px shown here is that scaled result) and fades 0→1 opacity. The engine
            // has no per-item ScaleX state machine, so the selected pill renders at its final 16px width with a
            // near-flat 1px corner radius (was 1.5 / Radii.Circle(3f)).
            var pill = new BoxEl
            {
                Width = 16f,
                Height = 3f,
                Corners = CornerRadius4.All(1f),
                Fill = isSelected ? Tok.AccentDefault : ColorF.Transparent,
                Margin = new Edges4(0, 4, 0, 0),
            };

            var item = new BoxEl
            {
                Direction = 1,
                AlignItems = FlexAlign.Center,
                Padding = new Edges4(12, 10, 12, 7),
                Corners = Radii.ControlAll,
                HoverFill = Tok.FillSubtleSecondary,
                Role = AutomationRole.Tab,
                OnClick = select,
                Children =
                [
                    new TextEl(items![index])
                    {
                        Size = 14f,
                        Color = Tok.TextPrimary,
                    },
                    parts.Apply(PartPill, pill),
                ],
            };
            // Parts: restyle anything; the select mechanics always win.
            tabs[index] = parts.Apply(PartItem, item) with { OnClick = select, Role = AutomationRole.Tab };
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
