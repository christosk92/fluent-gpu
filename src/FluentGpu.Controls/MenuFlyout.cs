using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Signals;

namespace FluentGpu.Controls;

/// <summary>The tri-state of a CheckBox / ToggleButton: cleared, set, or mixed (indeterminate).</summary>
public enum CheckState : byte { Unchecked = 0, Checked = 1, Indeterminate = 2 }

/// <summary>The kind of a <see cref="MenuFlyoutItem"/> row: a plain command, a divider, a checkable
/// <c>ToggleMenuFlyoutItem</c> (E73E check), or a mutually-exclusive <c>RadioMenuFlyoutItem</c> (E915 bullet).</summary>
public enum MenuItemKind : byte { Command = 0, Separator = 1, Toggle = 2, Radio = 3 }

/// <summary>A single row in a <see cref="MenuFlyout"/>: a label, an optional leading glyph (icon column), an enabled
/// flag, the action to run when chosen, and an optional trailing keyboard-accelerator hint (e.g. "Ctrl+S"). Use
/// <see cref="Separator"/> for a divider, <see cref="Toggle"/> for a checkable item (E73E check column), or
/// <see cref="RadioItem"/> for a mutually-exclusive choice (E915 bullet). All non-Command extras are optional so the
/// primary <c>(Label, Glyph, Enabled, Invoke)</c> constructor call-sites are unchanged.</summary>
public readonly record struct MenuFlyoutItem(string Label, string? Glyph = null, bool Enabled = true, Action? Invoke = null)
{
    public MenuItemKind Kind { get; init; }
    public bool IsChecked { get; init; }
    /// <summary>Trailing right-aligned keyboard-accelerator text (WinUI KeyboardAcceleratorTextOverride), e.g. "Ctrl+S".</summary>
    public string? AcceleratorText { get; init; }

    public bool IsSeparator => Kind == MenuItemKind.Separator;
    public static MenuFlyoutItem Separator => new("") { Kind = MenuItemKind.Separator };

    /// <summary>A checkable command (WinUI ToggleMenuFlyoutItem): an E73E check column whose glyph paints only when on.</summary>
    public static MenuFlyoutItem Toggle(string label, bool isChecked, Action? invoke = null, string? glyph = null, bool enabled = true)
        => new(label, glyph, enabled, invoke) { Kind = MenuItemKind.Toggle, IsChecked = isChecked };

    /// <summary>A mutually-exclusive command (WinUI RadioMenuFlyoutItem): an E915 bullet column whose glyph paints when selected.</summary>
    public static MenuFlyoutItem RadioItem(string label, bool isChecked, Action? invoke = null, string? glyph = null, bool enabled = true)
        => new(label, glyph, enabled, invoke) { Kind = MenuItemKind.Radio, IsChecked = isChecked };
}

/// <summary>Builds the popup body for a dropdown menu — a vertical list of command rows (each <c>Role = MenuItem</c>).
/// Selecting a row runs its command and closes the overlay. Used by DropDownButton / SplitButton / ToggleSplitButton /
/// MenuBar / TimePicker. The host's <c>FlyoutSurface</c> already supplies the acrylic backdrop, 1px flyout stroke,
/// OverlayCornerRadius, elevation shadow and the (0,2,0,2) presenter padding — this returns INNER content only.</summary>
public static class MenuFlyout
{
    // ── WinUI MenuFlyout_themeresources.xaml constants ────────────────────────────────────────────────────────────
    internal const float ThemeMinWidth   = 96f;                    // FlyoutThemeMinWidth (generic.xaml)
    internal const float ThemeMinHeight  = 32f;                    // MenuFlyoutThemeMinHeight
    internal const float RowHeight        = 36f;                   // 14px line + ItemThemePadding 11,8,11,9 → 36 effective
    internal const float SeparatorHeight  = 1f;                    // MenuFlyoutSeparatorHeight
    internal const float IconGlyphSize    = 16f;                   // IconRoot Viewbox 16×16
    internal const float CheckGlyphSize   = 12f;                   // CheckGlyph FontSize=12
    internal const float PlaceholderWidth = 28f;                   // MenuFlyoutItemPlaceholderThemeThickness 28,0,0,0
    internal const string CheckGlyph      = "\uE73E";              // ToggleMenuFlyoutItem CheckGlyph (Icons.Accept)
    internal const string RadioGlyph      = "\uE915";              // RadioMenuFlyoutItem CheckGlyph (RadioBullet)
    static readonly Edges4 ItemMargin     = new(4, 2, 4, 2);       // MenuFlyoutItemMargin
    static readonly Edges4 ItemPadding    = new(11, 8, 11, 9);     // MenuFlyoutItemThemePadding

    public static Element Build(IReadOnlyList<MenuFlyoutItem> items, Action close, float minWidth = ThemeMinWidth)
        => Embed.Comp(() => new MenuFlyoutPresenter { Items = items, Close = close, MinWidth = minWidth });

    // ── Separator: a 1px DividerStrokeColorDefault line; SeparatorThemePadding -4,1,-4,1 bleeds it past the item
    //    inset to full presenter width (we model the -4 bleed with a negative horizontal margin on the line). ────────
    internal static Element Separator() => new BoxEl
    {
        Direction = 1,
        Justify = FlexJustify.Center,
        Padding = new Edges4(0, 1, 0, 1),                          // SeparatorThemePadding vertical (1 top, 1 bottom)
        Children = [new BoxEl { Height = SeparatorHeight, Margin = new Edges4(-4, 0, -4, 0), Fill = Tok.StrokeDividerDefault }],
    };

    // ── A single command/toggle/radio row. <paramref name="highlighted"/> = the keyboard cursor (Up/Down) → renders the
    //    WinUI PointerOver fill so arrow-roving looks identical to a mouse hover. ───────────────────────────────────
    internal static Element Row(MenuFlyoutItem it, int index, Action close, bool checkColumn, bool iconColumn, bool highlighted, Action<int>? onKeyMove)
    {
        bool enabled = it.Enabled;
        // MenuFlyoutItemForeground = TextPrimary; Disabled = TextDisabled. PointerOver/Pressed foreground stay Primary
        // (the WinUI MenuFlyoutItem hover/pressed states do NOT recolor the label — they only change the row fill).
        ColorF fg = enabled ? Tok.TextPrimary : Tok.TextDisabled;
        // CheckGlyph (toggle E73E / radio E915) foreground = MenuFlyoutSubItemChevron = TextSecondary; Disabled = TextDisabled.
        ColorF checkFg = enabled ? Tok.TextSecondary : Tok.TextDisabled;

        var children = new List<Element>(4);

        // Column 0: check/radio glyph (28-wide), painted only when this toggle/radio is on (CheckGlyph.Opacity 0→1).
        if (checkColumn)
        {
            Element glyph = (it.Kind is MenuItemKind.Toggle or MenuItemKind.Radio) && it.IsChecked
                ? new TextEl(it.Kind == MenuItemKind.Radio ? RadioGlyph : CheckGlyph) { Size = CheckGlyphSize, Color = checkFg, FontFamily = Theme.IconFont }
                : new BoxEl();
            children.Add(new BoxEl { Width = PlaceholderWidth, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, Children = [glyph] });
        }

        // Column 1: 16×16 icon (IconRoot Viewbox), painted when this row carries a glyph.
        if (iconColumn)
        {
            Element icon = it.Glyph is { Length: > 0 } g
                ? new TextEl(g) { Size = IconGlyphSize, Color = fg, FontFamily = Theme.IconFont }
                : new BoxEl();
            children.Add(new BoxEl { Width = PlaceholderWidth, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, Children = [icon] });
        }

        // Column 2: the label (TextBlock, "*" column → grows).
        children.Add(new TextEl(it.Label) { Size = 14f, Color = fg, Grow = 1f });

        // Column 3: optional keyboard-accelerator hint, right-aligned, CaptionTextBlock-ish (12px), TextSecondary.
        if (it.AcceleratorText is { Length: > 0 } acc)
            children.Add(new TextEl(acc) { Size = 12f, Color = enabled ? Tok.TextSecondary : Tok.TextDisabled, Margin = new Edges4(24, 0, 0, 0) });

        // The keyboard cursor (highlight) renders the same brush as PointerOver so arrow-nav matches hover exactly.
        ColorF rest = enabled && highlighted ? Tok.FillSubtleSecondary : Tok.FillSubtleTransparent;

        return new BoxEl
        {
            Direction = 0,
            Height = RowHeight,                                    // 36 (= MinHeight 32 clamped up by the 14px line + 8/9 padding)
            MinHeight = ThemeMinHeight,                            // MenuFlyoutThemeMinHeight = 32
            AlignItems = FlexAlign.Center,
            Margin = ItemMargin,                                   // 4,2,4,2
            Padding = ItemPadding,                                 // 11,8,11,9
            Gap = 0f,                                              // columns own their fixed 28px width; no inter-gap (WinUI grid)
            Corners = Radii.ControlAll,                            // ControlCornerRadius = 4
            // Toggle/radio surface a11y as their WinUI peers (CheckBox/RadioButton patterns); plain command = MenuItem.
            Role = it.Kind switch { MenuItemKind.Toggle => AutomationRole.CheckBox, MenuItemKind.Radio => AutomationRole.RadioButton, _ => AutomationRole.MenuItem },
            // MenuFlyoutItemBackground (explicit rest) / ...PointerOver / ...Pressed. Disabled = transparent (no hover/press).
            Fill = rest,
            HoverFill = enabled ? Tok.FillSubtleSecondary : Tok.FillSubtleTransparent,    // SubtleFillColorSecondary
            PressedFill = enabled ? Tok.FillSubtleTertiary : Tok.FillSubtleTransparent,   // SubtleFillColorTertiary
            IsEnabled = enabled,                                   // engine gate: disabled = no hit-test/focus/keyboard/click
            Focusable = enabled,                                   // roving target (focus trap Tab/Shift-Tab + Enter/Space activation)
            TabIndex = index + 1,                                  // document order within the menu
            OnClick = enabled ? () => { it.Invoke?.Invoke(); close(); } : null,
            // Arrow roving (Up/Down/Home/End) — moves the shared highlight; Enter/Space activation is the engine's
            // focused-clickable contract; Escape light-dismiss is the overlay's PreviewKey. Bubbles when not handled.
            OnKeyDown = onKeyMove is null ? null : a =>
            {
                switch (a.KeyCode)
                {
                    case Keys.Down: onKeyMove(+1); a.Handled = true; break;
                    case Keys.Up:   onKeyMove(-1); a.Handled = true; break;
                    case Keys.Home: onKeyMove(int.MinValue); a.Handled = true; break;
                    case Keys.End:  onKeyMove(int.MaxValue); a.Handled = true; break;
                }
            },
            Children = children.ToArray(),
        };
    }
}

/// <summary>The MenuFlyoutPresenter body: a vertical list of <see cref="MenuFlyout.Row"/>s with a keyboard-cursor
/// (highlight) signal driving Up/Down/Home/End roving. Wrapped by the host's FlyoutSurface (acrylic + stroke + shadow +
/// corners + 0,2,0,2 padding). Tall menus scroll internally (ScrollViewer.VerticalScrollMode=Auto). Run-once-ish: the
/// only re-render trigger is the highlight signal (the keyboard cursor), so pointer hover/press never re-renders.</summary>
internal sealed class MenuFlyoutPresenter : Component
{
    public required IReadOnlyList<MenuFlyoutItem> Items;
    public required Action Close;
    public float MinWidth = MenuFlyout.ThemeMinWidth;

    // WinUI MenuFlyoutPresenter ScrollViewer max before internal scroll (mirrors the flyout's content cap; AutoSuggest
    // uses 374 — menus share the same overlay sizing). Content shorter than this never scrolls.
    const float MaxHeight = 468f;

    public override Element Render()
    {
        var highlight = UseSignal(-1);                             // keyboard cursor index; -1 = none (pointer-only)
        int hi = highlight.Value;                                 // subscribe → re-highlight on arrow move

        bool checkColumn = false, iconColumn = false;
        for (int i = 0; i < Items.Count; i++)
        {
            var it = Items[i];
            if (it.IsSeparator) continue;
            if (it.Kind is MenuItemKind.Toggle or MenuItemKind.Radio) checkColumn = true;
            if (it.Glyph is { Length: > 0 }) iconColumn = true;
        }

        // Move the keyboard cursor to the next/prev focusable (enabled, non-separator) row, wrapping; delta == ±1, or
        // int.MinValue/MaxValue for Home/End. Pure index math over Items (no scene/dispatcher dependency).
        void Move(int delta)
        {
            int n = Items.Count;
            if (n == 0) return;
            int cur = highlight.Peek();
            if (delta == int.MinValue) { int f = FirstSelectable(); if (f >= 0) highlight.Value = f; return; }
            if (delta == int.MaxValue) { int l = LastSelectable();  if (l >= 0) highlight.Value = l; return; }
            int start = cur < 0 ? (delta > 0 ? -1 : n) : cur;
            for (int step = 0; step < n; step++)
            {
                start = (start + delta + n) % n;
                if (IsSelectable(start)) { highlight.Value = start; return; }
            }
        }

        bool IsSelectable(int i) => i >= 0 && i < Items.Count && !Items[i].IsSeparator && Items[i].Enabled;
        int FirstSelectable() { for (int i = 0; i < Items.Count; i++) if (IsSelectable(i)) return i; return -1; }
        int LastSelectable()  { for (int i = Items.Count - 1; i >= 0; i--) if (IsSelectable(i)) return i; return -1; }

        var rows = new Element[Items.Count];
        for (int i = 0; i < Items.Count; i++)
        {
            var it = Items[i];
            rows[i] = it.IsSeparator
                ? MenuFlyout.Separator()
                : MenuFlyout.Row(it, i, Close, checkColumn, iconColumn, highlighted: i == hi, onKeyMove: Move);
        }

        var column = new BoxEl
        {
            Direction = 1,
            MinWidth = MinWidth,                                  // FlyoutThemeMinWidth; final width is content-driven
            Children = rows,
        };

        // Size to content (the common case) and cap at MaxHeight with internal scroll, matching
        // MenuFlyoutPresenterScrollViewer. The layout engine content-sizes auto scroll viewports, then clamps by MaxHeight.
        return new ScrollEl
        {
            Content = column,
            ContentSized = true,
            MinWidth = MinWidth,
            MaxHeight = MaxHeight,
        };
    }
}
