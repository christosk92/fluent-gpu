using FluentGpu.Dsl;
using FluentGpu.Foundation;

namespace FluentGpu.Controls;

/// <summary>The tri-state of a CheckBox / ToggleButton: cleared, set, or mixed (indeterminate).</summary>
public enum CheckState : byte { Unchecked = 0, Checked = 1, Indeterminate = 2 }

/// <summary>A single command in a <see cref="MenuFlyout"/>: a label, an optional leading glyph, an enabled flag, and the
/// action to run when chosen. Use <see cref="Separator"/> for a divider row.</summary>
public readonly record struct MenuFlyoutItem(string Label, string? Glyph = null, bool Enabled = true, Action? Invoke = null)
{
    public bool IsSeparator { get; init; }
    public static MenuFlyoutItem Separator => new("") { IsSeparator = true };
}

/// <summary>Builds the popup body for a dropdown menu — a vertical list of command rows (each <c>Role = MenuItem</c>).
/// Selecting a row runs its command and closes the overlay. Used by DropDownButton / SplitButton / ToggleSplitButton.</summary>
public static class MenuFlyout
{
    public static Element Build(IReadOnlyList<MenuFlyoutItem> items, Action close, float minWidth = 96f)
    {
        bool hasIconColumn = false;
        for (int i = 0; i < items.Count; i++)
            if (!items[i].IsSeparator && items[i].Glyph is { Length: > 0 }) { hasIconColumn = true; break; }

        var rows = new Element[items.Count];
        for (int i = 0; i < items.Count; i++)
        {
            var it = items[i];
            rows[i] = it.IsSeparator
                ? Separator()
                : Row(it, close, hasIconColumn);
        }
        return new BoxEl
        {
            Direction = 1,
            MinWidth = minWidth, // FlyoutThemeMinWidth; final width is content-driven.
            Children = rows,
        };
    }

    static Element Separator() => new BoxEl
    {
        Direction = 1,
        Height = 9f,
        Justify = FlexJustify.Center,
        Padding = new Edges4(12, 0, 12, 0),
        Children = [new BoxEl { Height = 1f, Fill = Tok.StrokeDividerDefault }],
    };

    static Element Row(MenuFlyoutItem it, Action close, bool hasIconColumn)
    {
        var fg = it.Enabled ? Tok.TextPrimary : Tok.TextDisabled;
        var children = new List<Element>();
        if (hasIconColumn)
        {
            Element icon = it.Glyph is { Length: > 0 } g
                ? new TextEl(g) { Size = 15f, Color = fg, FontFamily = Theme.IconFont }
                : new BoxEl();
            children.Add(new BoxEl { Width = 28f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, Children = [icon] });
        }
        children.Add(new TextEl(it.Label) { Size = 14f, Color = fg, Grow = 1f });

        return new BoxEl
        {
            Direction = 0,
            Height = 36f,   // MenuFlyoutItem rendered height (text line + 11,8,11,9 padding)
            AlignItems = FlexAlign.Center,
            Margin = new Edges4(4, 2, 4, 2),
            Padding = new Edges4(11, 8, 11, 9),
            Gap = hasIconColumn ? 8f : 0f,
            Corners = Radii.ControlAll,
            Role = AutomationRole.MenuItem,
            HoverFill = it.Enabled ? Tok.FillSubtleSecondary : ColorF.Transparent,
            PressedFill = it.Enabled ? Tok.FillSubtleTertiary : ColorF.Transparent,
            OnClick = it.Enabled ? () => { it.Invoke?.Invoke(); close(); } : null,
            Children = children.ToArray(),
        };
    }
}
