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
    public static Element Build(IReadOnlyList<MenuFlyoutItem> items, Action close, float minWidth = 160f)
    {
        var rows = new Element[items.Count];
        for (int i = 0; i < items.Count; i++)
        {
            var it = items[i];
            rows[i] = it.IsSeparator
                ? new BoxEl { Height = 1f, Margin = new Edges4(8, 4, 8, 4), Fill = Tok.StrokeDividerDefault }
                : Row(it, close);
        }
        return new BoxEl { Direction = 1, MinWidth = minWidth, Padding = new Edges4(4, 4, 4, 4), Children = rows };
    }

    static Element Row(MenuFlyoutItem it, Action close)
    {
        var fg = it.Enabled ? Tok.TextPrimary : Tok.TextDisabled;
        var children = new List<Element>();
        if (it.Glyph is { Length: > 0 } g)
            children.Add(new BoxEl { Width = 24f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, Children = [new TextEl(g) { Size = 14f, Color = fg, FontFamily = Theme.IconFont }] });
        children.Add(new TextEl(it.Label) { Size = 14f, Color = fg });

        return new BoxEl
        {
            Direction = 0,
            Height = 32f,
            AlignItems = FlexAlign.Center,
            Padding = new Edges4(10, 0, 12, 0),
            Gap = 4f,
            Corners = Radii.ControlAll,
            Role = AutomationRole.MenuItem,
            HoverFill = it.Enabled ? Tok.FillSubtleSecondary : ColorF.Transparent,
            PressedFill = it.Enabled ? Tok.FillSubtleTertiary : ColorF.Transparent,
            OnClick = it.Enabled ? () => { it.Invoke?.Invoke(); close(); } : null,
            Children = children.ToArray(),
        };
    }
}
