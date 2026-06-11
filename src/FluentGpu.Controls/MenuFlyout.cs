using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Scene;
using FluentGpu.Signals;

namespace FluentGpu.Controls;

/// <summary>The tri-state of a CheckBox / ToggleButton: cleared, set, or mixed (indeterminate).</summary>
public enum CheckState : byte { Unchecked = 0, Checked = 1, Indeterminate = 2 }

/// <summary>The kind of a <see cref="MenuFlyoutItem"/> row: a plain command, a divider, a checkable
/// <c>ToggleMenuFlyoutItem</c> (E73E check), a mutually-exclusive <c>RadioMenuFlyoutItem</c> (E915 bullet), or a
/// cascading <c>MenuFlyoutSubItem</c> (E974 chevron + nested popup).</summary>
public enum MenuItemKind : byte { Command = 0, Separator = 1, Toggle = 2, Radio = 3, SubMenu = 4 }

/// <summary>A single row in a <see cref="MenuFlyout"/>: a label, an optional leading glyph (icon column), an enabled
/// flag, the action to run when chosen, and an optional trailing keyboard-accelerator hint (e.g. "Ctrl+S"). Use
/// <see cref="Separator"/> for a divider, <see cref="Toggle"/> for a checkable item (E73E check column),
/// <see cref="RadioItem"/> for a mutually-exclusive choice (E915 bullet), or <see cref="SubMenu"/> for a cascading
/// sub-menu (E974 chevron column, hover/Right-arrow opens the nested popup). All non-Command extras are optional so
/// the primary <c>(Label, Glyph, Enabled, Invoke)</c> constructor call-sites are unchanged.</summary>
public readonly record struct MenuFlyoutItem(string Label, string? Glyph = null, bool Enabled = true, Action? Invoke = null)
{
    public MenuItemKind Kind { get; init; }
    public bool IsChecked { get; init; }
    /// <summary>Trailing right-aligned keyboard-accelerator text (WinUI KeyboardAcceleratorTextOverride), e.g. "Ctrl+S".</summary>
    public string? AcceleratorText { get; init; }
    /// <summary>Nested items (Kind == <see cref="MenuItemKind.SubMenu"/> only) — the cascading sub-menu's rows.</summary>
    public IReadOnlyList<MenuFlyoutItem>? SubItems { get; init; }

    public bool IsSeparator => Kind == MenuItemKind.Separator;
    public static MenuFlyoutItem Separator => new("") { Kind = MenuItemKind.Separator };

    /// <summary>A checkable command (WinUI ToggleMenuFlyoutItem): an E73E check column whose glyph paints only when on.</summary>
    public static MenuFlyoutItem Toggle(string label, bool isChecked, Action? invoke = null, string? glyph = null, bool enabled = true)
        => new(label, glyph, enabled, invoke) { Kind = MenuItemKind.Toggle, IsChecked = isChecked };

    /// <summary>A mutually-exclusive command (WinUI RadioMenuFlyoutItem): an E915 bullet column whose glyph paints when selected.</summary>
    public static MenuFlyoutItem RadioItem(string label, bool isChecked, Action? invoke = null, string? glyph = null, bool enabled = true)
        => new(label, glyph, enabled, invoke) { Kind = MenuItemKind.Radio, IsChecked = isChecked };

    /// <summary>A cascading sub-menu (WinUI MenuFlyoutSubItem): an E974 chevron in the trailing column
    /// (MenuFlyout_themeresources.xaml:720 SubItemChevron); hovering the row for MenuShowDelay (400ms default,
    /// MenuFlyout_Partial.h:13), clicking it, or Right-arrow opens the nested menu to the right.</summary>
    public static MenuFlyoutItem SubMenu(string label, IReadOnlyList<MenuFlyoutItem> items, string? glyph = null, bool enabled = true)
        => new(label, glyph, enabled) { Kind = MenuItemKind.SubMenu, SubItems = items };
}

/// <summary>Builds the popup body for a dropdown menu — a vertical list of command rows (each <c>Role = MenuItem</c>).
/// Selecting a row runs its command and closes the overlay. Used by DropDownButton / SplitButton / ToggleSplitButton /
/// MenuBar / TimePicker. The host's <c>FlyoutSurface</c> already supplies the acrylic backdrop, 1px flyout stroke,
/// OverlayCornerRadius, elevation shadow and the (0,2,0,2) presenter padding — this returns INNER content only.</summary>
public static class MenuFlyout
{
    // ── WinUI MenuFlyout_themeresources.xaml constants ────────────────────────────────────────────────────────────
    internal const float ThemeMinWidth   = 96f;                    // FlyoutThemeMinWidth (generic.xaml)
    internal const float ThemeMinHeight  = 32f;                    // MenuFlyoutThemeMinHeight (themeresources:256)
    internal const float RowHeight        = 36f;                   // 14px line + ItemThemePadding 11,8,11,9 → 36 effective
    internal const float SeparatorHeight  = 1f;                    // MenuFlyoutSeparatorHeight (themeresources:254)
    internal const float IconGlyphSize    = 16f;                   // IconRoot Viewbox 16×16
    internal const float CheckGlyphSize   = 12f;                   // CheckGlyph FontSize=12
    internal const float ChevronGlyphSize = 12f;                   // SubItemChevron FontSize=12 (themeresources:720)
    internal const float PlaceholderWidth = 28f;                   // MenuFlyoutItemPlaceholderThemeThickness 28,0,0,0
    internal const string CheckGlyph      = "";              // ToggleMenuFlyoutItem CheckGlyph (Icons.Accept)
    internal const string RadioGlyph      = "";              // RadioMenuFlyoutItem CheckGlyph (Icons.RadioBullet)
    /// <summary>Sub-menu hover-open AND delay-close interval — WinUI CascadingMenuHelper reads the user's
    /// HKCU MenuShowDelay, defaulting to <c>DefaultMenuShowDelay = 400</c>ms (MenuFlyout_Partial.h:13;
    /// CascadingMenuHelper.cpp:93-95 fallback, :439/:508 open/close timer intervals).</summary>
    public const float SubMenuShowDelayMs = 400f;
    static readonly Edges4 ItemMargin     = new(4, 2, 4, 2);       // MenuFlyoutItemMargin (themeresources:260)
    static readonly Edges4 ItemPadding    = new(11, 8, 11, 9);     // MenuFlyoutItemThemePadding (themeresources:261)
    static readonly Edges4 ChevronMargin  = new(24, 0, 0, 0);      // MenuFlyoutItemChevronMargin 24,0,0,-1 (themeresources:257; the -1 optical nudge is sub-px here)

    // ── Template parts (see TemplateParts) — threaded into EVERY presenter level, so cascading sub-menus inherit the
    //    same modifiers. The popup body is popup-built each open: modifiers run inside the presenter's render. Each
    //    part's doc lists the props the control OWNS (re-asserted after any modifier — a modifier cannot win those).
    /// <summary>The scrollable viewport (WinUI MenuFlyoutPresenterScrollViewer) — a <see cref="ScrollEl"/>, so style
    /// it via the generic map: <c>Parts.Set&lt;ScrollEl&gt;(MenuFlyout.PartScrollViewer, s => s with { MaxHeight = … })</c>.
    /// Owned: Content (the items column), ContentSized.</summary>
    public const string PartScrollViewer = "ScrollViewer";
    /// <summary>The items column inside the viewport (WinUI ItemsPresenter). Owned: OnCharInput (first-letter jump),
    /// Children (the rows + cascade timers).</summary>
    public const string PartPresenter = "Presenter";
    /// <summary>EVERY command/toggle/radio/sub-menu row — ONE modifier restyles all rows (popup-built each open, not
    /// recycled). Owned: Role, TabIndex, OnClick, OnKeyDown (roving), OnHoverMove (cascade bookkeeping), OnRealized
    /// (row-node capture, chained), Children (the check/icon/label/accelerator/chevron columns).</summary>
    public const string PartItem = "Item";
    /// <summary>A separator row (pure chrome — no owned props).</summary>
    public const string PartSeparator = "Separator";

    public static Element Build(IReadOnlyList<MenuFlyoutItem> items, Action close, float minWidth = ThemeMinWidth,
        TemplateParts? parts = null)
        => Build(items, close, minWidth, onNavigate: null, parts);

    /// <summary><paramref name="onNavigate"/>: Left(-1)/Right(+1) pressed while the menu is open and the focused row is
    /// not a sub-menu boundary — MenuBar uses it to move between adjacent open menus (MenuBarItem.cpp:205-228
    /// OnPresenterKeyDown → OpenFlyoutFrom).</summary>
    public static Element Build(IReadOnlyList<MenuFlyoutItem> items, Action close, float minWidth, Action<int>? onNavigate,
        TemplateParts? parts = null)
        => Embed.Comp(() => new MenuFlyoutPresenter { Items = items, Close = close, MinWidth = minWidth, OnNavigate = onNavigate, Parts = parts });

    // ── Separator: a 1px DividerStrokeColorDefault line; SeparatorThemePadding -4,1,-4,1 bleeds it past the item
    //    inset to full presenter width (we model the -4 bleed with a negative horizontal margin on the line). ────────
    internal static Element Separator(TemplateParts? parts = null) => parts.Apply(PartSeparator, new BoxEl
    {
        Direction = 1,
        Justify = FlexJustify.Center,
        Padding = new Edges4(0, 1, 0, 1),                          // SeparatorThemePadding vertical (1 top, 1 bottom)
        Children = [new BoxEl { Height = SeparatorHeight, Margin = new Edges4(-4, 0, -4, 0), Fill = Tok.StrokeDividerDefault }],
    });

    // ── A single command/toggle/radio/sub-menu row. <paramref name="highlighted"/> = the keyboard cursor (Up/Down) →
    //    renders the WinUI PointerOver fill so arrow-roving looks identical to a mouse hover. <paramref name="subOpen"/>
    //    = this row's cascading sub-menu is open (WinUI SubMenuOpened state = the PointerOver fill held). ────────────
    internal static Element Row(
        MenuFlyoutItem it, int index, Action activate, bool checkColumn, bool iconColumn, bool highlighted,
        Action<int>? onKeyMove, Action<KeyEventArgs>? onRowKey, Action<Point2>? onHover, Action<NodeHandle>? onRealized,
        bool subOpen = false, TemplateParts? parts = null)
    {
        bool enabled = it.Enabled;
        // MenuFlyoutItemForeground = TextPrimary; Disabled = TextDisabled. PointerOver/Pressed foreground stay Primary
        // (the WinUI MenuFlyoutItem hover/pressed states do NOT recolor the label — they only change the row fill).
        ColorF fg = enabled ? Tok.TextPrimary : Tok.TextDisabled;
        // CheckGlyph (toggle E73E / radio E915) + SubItemChevron foreground = MenuFlyoutSubItemChevron = TextSecondary;
        // Pressed = TextTertiary (MenuFlyoutSubItemChevronPressed); Disabled = TextDisabled (themeresources:26-30).
        ColorF checkFg = enabled ? Tok.TextSecondary : Tok.TextDisabled;

        var children = new List<Element>(5);

        // Column 0: check/radio glyph (28-wide), painted only when this toggle/radio is on (CheckGlyph.Opacity 0→1).
        if (checkColumn)
        {
            Element glyph = (it.Kind is MenuItemKind.Toggle or MenuItemKind.Radio) && it.IsChecked
                ? new TextEl(it.Kind == MenuItemKind.Radio ? RadioGlyph : CheckGlyph) { Size = CheckGlyphSize, Color = checkFg, PressedColor = enabled ? Tok.TextTertiary : checkFg, FontFamily = Theme.IconFont }
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

        // Column 3: optional keyboard-accelerator hint, right-aligned (HorizontalAlignment=Right),
        // CaptionTextBlock (12px), MenuFlyoutItemKeyboardAcceleratorTextForeground = TextSecondary / Disabled.
        if (it.AcceleratorText is { Length: > 0 } acc)
            children.Add(new TextEl(acc) { Size = 12f, Color = enabled ? Tok.TextSecondary : Tok.TextDisabled, Margin = new Edges4(24, 0, 0, 0) });

        // Column 4 (sub-menu rows): the cascade chevron — E974 @12, MenuFlyoutItemChevronMargin 24,0,0,-1, foreground
        // MenuFlyoutSubItemChevron ramp (MenuFlyout_themeresources.xaml:26-30 + :720 SubItemChevron).
        if (it.Kind == MenuItemKind.SubMenu)
            children.Add(new TextEl(Icons.ChevronRightMed)
            {
                Size = ChevronGlyphSize, FontFamily = Theme.IconFont, Margin = ChevronMargin,
                Color = checkFg, PressedColor = enabled ? Tok.TextTertiary : checkFg, DisabledColor = Tok.TextDisabled,
            });

        // The keyboard cursor (highlight) and the SubMenuOpened state render the same brush as PointerOver
        // (MenuFlyoutSubItemBackgroundSubMenuOpened = SubtleFillColorSecondary) so they read identically to a hover.
        ColorF rest = enabled && (highlighted || subOpen) ? Tok.FillSubtleSecondary : Tok.FillSubtleTransparent;

        var row = new BoxEl
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
            OnRealized = onRealized,
            OnHoverMove = onHover,                                 // presenter cascade bookkeeping (sub-menu delay open/close)
            OnClick = enabled ? activate : null,
            // Arrow roving (Up/Down/Home/End) — moves the shared highlight AND keyboard focus; Enter/Space activation
            // is the engine's focused-clickable contract; Escape light-dismiss is the overlay's PreviewKey. Left/Right
            // (sub-menu open/close, MenuBar adjacent-menu nav) route through onRowKey. Bubbles when not handled.
            OnKeyDown = (onKeyMove is null && onRowKey is null) ? null : a =>
            {
                switch (a.KeyCode)
                {
                    case Keys.Down: onKeyMove?.Invoke(+1); a.Handled = onKeyMove is not null; break;
                    case Keys.Up:   onKeyMove?.Invoke(-1); a.Handled = onKeyMove is not null; break;
                    case Keys.Home: onKeyMove?.Invoke(int.MinValue); a.Handled = onKeyMove is not null; break;
                    case Keys.End:  onKeyMove?.Invoke(int.MaxValue); a.Handled = onKeyMove is not null; break;
                    default: onRowKey?.Invoke(a); break;
                }
            },
            Children = children.ToArray(),
        };
        // [PartItem]: one modifier restyles EVERY row; the roving/cascade mechanics and the column structure always
        // win. The state-driven rest fill (highlight/SubMenuOpened) sits in the stock build, so a modifier may
        // override it off `b.Fill`.
        if (parts is { } rp)
        {
            var m = rp.Apply(PartItem, row);
            row = m with
            {
                Role = row.Role, TabIndex = row.TabIndex,
                OnClick = row.OnClick, OnKeyDown = row.OnKeyDown, OnHoverMove = row.OnHoverMove,
                OnRealized = TemplateParts.Chain(onRealized, m.OnRealized),
                Children = row.Children,
            };
        }
        return row;
    }
}

/// <summary>The MenuFlyoutPresenter body: a vertical list of <see cref="MenuFlyout.Row"/>s with a keyboard-cursor
/// (highlight) signal driving Up/Down/Home/End roving (focus follows the cursor), first-letter jump-to-item, and
/// cascading <see cref="MenuItemKind.SubMenu"/> rows — hover-open after the 400ms MenuShowDelay, click/Right-arrow
/// immediate open, hover-away delay-close, Left-arrow/Escape close (WinUI CascadingMenuHelper). Wrapped by the host's
/// FlyoutSurface (acrylic + stroke + shadow + corners + 0,2,0,2 padding). Tall menus scroll internally. Run-once-ish:
/// re-render triggers are the highlight + cascade signals only, so pointer hover/press never re-renders.</summary>
internal sealed class MenuFlyoutPresenter : Component
{
    public required IReadOnlyList<MenuFlyoutItem> Items;
    public required Action Close;
    public float MinWidth = MenuFlyout.ThemeMinWidth;
    /// <summary>MenuBar adjacent-menu navigation: invoked with -1 (Left) / +1 (Right) when the key is not a
    /// sub-menu boundary action (MenuBarItem.cpp:205-228 OnPresenterKeyDown).</summary>
    public Action<int>? OnNavigate;
    /// <summary>Set on a SUB-menu presenter: closes this cascade level (Left arrow — WinUI returns focus to the
    /// parent item; the overlay's close-start focus restore does exactly that).</summary>
    public Action? CloseSelf;
    /// <summary>Set on a SUB-menu presenter: notifies the PARENT presenter that the pointer is inside this child,
    /// cancelling the parent's pending delay-close (CascadingMenuHelper.cpp OnPointerEntered stops m_delayCloseMenuTimer).</summary>
    public Action? OnChildHover;
    /// <summary>Keyboard-opened menus put the cursor + focus on the FIRST selectable row after mount (WinUI
    /// CascadingMenuHelper keyboard open focuses the first MenuFlyoutItem; MenuBar Down-open does the same).</summary>
    public bool FocusFirstOnMount;
    /// <summary>Part modifiers keyed by the <see cref="MenuFlyout"/> <c>PartXxx</c> consts — inherited by every
    /// cascading sub-menu level.</summary>
    public TemplateParts? Parts;

    // WinUI MenuFlyoutPresenter ScrollViewer max before internal scroll (mirrors the flyout's content cap; AutoSuggest
    // uses 374 — menus share the same overlay sizing). Content shorter than this never scrolls.
    const float MaxHeight = 468f;

    public override Element Render()
    {
        var svc = UseContext(Overlay.Service);
        var hooks = UseContext(InputHooks.Current);
        var highlight = UseSignal(-1);                            // keyboard cursor index; -1 = none (pointer-only)
        var subOpenIdx = UseSignal(-1);                           // index of the row whose cascade popup is open
        var pendingOpenIdx = UseSignal(-1);                       // sub-item row hovered, waiting MenuShowDelay
        var pendingClose = UseSignal(false);                      // hover left the open sub-item row → delay-close armed
        var subHandle = UseRef<OverlayHandle?>(null);
        var rowNodes = UseRef<NodeHandle[]>([]);
        if (rowNodes.Value.Length != Items.Count) rowNodes.Value = new NodeHandle[Items.Count];

        int hi = highlight.Value;                                 // subscribe → re-highlight on arrow move
        int subOpen = subOpenIdx.Value;                           // subscribe → re-render the SubMenuOpened row fill
        int pendingOpen = pendingOpenIdx.Value;                   // subscribe → mount/unmount the open-delay clock
        bool closePending = pendingClose.Value;                   // subscribe → mount/unmount the close-delay clock

        bool checkColumn = false, iconColumn = false;
        for (int i = 0; i < Items.Count; i++)
        {
            var it = Items[i];
            if (it.IsSeparator) continue;
            if (it.Kind is MenuItemKind.Toggle or MenuItemKind.Radio) checkColumn = true;
            if (it.Glyph is { Length: > 0 }) iconColumn = true;
        }

        bool IsSelectable(int i) => i >= 0 && i < Items.Count && !Items[i].IsSeparator && Items[i].Enabled;
        int FirstSelectable() { for (int i = 0; i < Items.Count; i++) if (IsSelectable(i)) return i; return -1; }
        int LastSelectable()  { for (int i = Items.Count - 1; i >= 0; i--) if (IsSelectable(i)) return i; return -1; }

        // Move the keyboard cursor (and FOCUS — Enter/Space activate the focused row, so the cursor must carry focus,
        // exactly WinUI's menu roving) to the next/prev selectable row, wrapping; Home/End via the sentinels.
        void SetCursor(int i)
        {
            if (!IsSelectable(i)) return;
            highlight.Value = i;
            var node = rowNodes.Value[i];
            if (!node.IsNull) hooks.MoveFocusVisual?.Invoke(node);
        }

        void Move(int delta)
        {
            int n = Items.Count;
            if (n == 0) return;
            int cur = highlight.Peek();
            if (delta == int.MinValue) { int f = FirstSelectable(); if (f >= 0) SetCursor(f); return; }
            if (delta == int.MaxValue) { int l = LastSelectable();  if (l >= 0) SetCursor(l); return; }
            int start = cur < 0 ? (delta > 0 ? -1 : n) : cur;
            for (int step = 0; step < n; step++)
            {
                start = (start + delta + n) % n;
                if (IsSelectable(start)) { SetCursor(start); return; }
            }
        }

        // First-letter jump: typing a character moves the cursor to the next item (cycling past the current cursor)
        // whose label starts with it, case-insensitively — the WinUI/Win32 menu text-search.
        void HandleChar(CharEventArgs e)
        {
            if (e.Codepoint < 0x20 || e.Codepoint == 0x7F || Items.Count == 0) return;
            string ch = char.ConvertFromUtf32(e.Codepoint);
            int from = highlight.Peek();
            for (int k = 1; k <= Items.Count; k++)
            {
                int idx = ((from < 0 ? -1 : from) + k + Items.Count) % Items.Count;
                if (IsSelectable(idx) && Items[idx].Label.StartsWith(ch, StringComparison.OrdinalIgnoreCase))
                {
                    SetCursor(idx);
                    e.Handled = true;
                    return;
                }
            }
        }

        void CloseSub()
        {
            pendingClose.Value = false;
            pendingOpenIdx.Value = -1;
            if (subHandle.Value is { IsOpen: true } h) h.Close();
            subHandle.Value = null;
            subOpenIdx.Value = -1;
        }

        // Open the cascade popup for the sub-item row at <paramref name="i"/>. Anchored to the ROW node so the E4
        // nested-overlay plumbing resolves the parent chain (z-order above this popup; cascade close with it).
        // Placement = right of the item, top edges aligned (falls back left per the positioner's fallback walk).
        // Windowed (ConstrainToRootBounds=false): WinUI menus are windowed popups (FlyoutBase SetIsWindowedPopup).
        void OpenSub(int i, bool focusFirst)
        {
            if (!IsSelectable(i) || Items[i].Kind != MenuItemKind.SubMenu || Items[i].SubItems is not { } subItems) return;
            if (subOpenIdx.Peek() == i && subHandle.Value is { IsOpen: true }) return;
            CloseSub();
            var node = rowNodes.Value[i];
            if (node.IsNull) return;
            var handle = svc.Open(
                () => rowNodes.Value[i],
                () => Embed.Comp(() => new MenuFlyoutPresenter
                {
                    Items = subItems,
                    Close = Close,                       // invoking a sub item closes the WHOLE chain (cascade close)
                    MinWidth = MenuFlyout.ThemeMinWidth,
                    CloseSelf = () => CloseSub(),        // Left arrow closes one cascade level
                    OnChildHover = () => pendingClose.Value = false,   // pointer inside the child cancels delay-close
                    FocusFirstOnMount = focusFirst,      // keyboard open → cursor + focus on the first sub item
                    Parts = Parts,                       // part modifiers cascade into every sub-menu level
                }),
                FlyoutPlacement.RightEdgeAlignedTop,
                new PopupOptions(Chrome: PopupChrome.Flyout) { ConstrainToRootBounds = false });
            handle.ClosedAction = () =>
            {
                if (ReferenceEquals(subHandle.Value, handle)) { subHandle.Value = null; subOpenIdx.Value = -1; }
            };
            subHandle.Value = handle;
            subOpenIdx.Value = i;
        }

        // Keyboard-opened menus: cursor + focus land on the first selectable row once the rows have realized
        // (post-layout, exactly once) — WinUI CascadingMenuHelper keyboard open / MenuBar Down-open semantics.
        var focusedFirst = UseRef(false);
        UseLayoutEffect(() =>
        {
            if (!FocusFirstOnMount || focusedFirst.Value) return;
            int f = FirstSelectable();
            if (f < 0 || rowNodes.Value[f].IsNull) return;
            focusedFirst.Value = true;
            SetCursor(f);
        }, FocusFirstOnMount);

        // Per-row hover bookkeeping — the CascadingMenuHelper timers:
        //  • hovering a sub-item row arms the 400ms delay-OPEN (cancelled by leaving it);
        //  • hovering any OTHER row while a cascade is open arms the 400ms delay-CLOSE (cancelled by re-entering the
        //    open sub-item row or by the pointer reaching the child popup — OnChildHover).
        void OnRowHover(int i)
        {
            OnChildHover?.Invoke();   // bubbling: pointer in THIS presenter also cancels the parent's delay-close
            var it = Items[i];
            if (it.Kind == MenuItemKind.SubMenu && it.Enabled)
            {
                if (subOpenIdx.Peek() == i) { pendingClose.Value = false; pendingOpenIdx.Value = -1; return; }
                if (pendingOpenIdx.Peek() != i) pendingOpenIdx.Value = i;
                if (subOpenIdx.Peek() >= 0) pendingClose.Value = true;
            }
            else
            {
                if (pendingOpenIdx.Peek() >= 0) pendingOpenIdx.Value = -1;
                if (subOpenIdx.Peek() >= 0 && !pendingClose.Peek()) pendingClose.Value = true;
            }
        }

        // Row-level non-roving keys: Right opens a cascade (focus-first), Left closes a cascade level (sub menus) or
        // navigates the MenuBar; Enter on a sub-item row opens its cascade (the OnClick path handles activation).
        void OnRowKey(KeyEventArgs a)
        {
            int cur = highlight.Peek();
            if (a.KeyCode == Keys.Right)
            {
                if (IsSelectable(cur) && Items[cur].Kind == MenuItemKind.SubMenu) { OpenSub(cur, focusFirst: true); a.Handled = true; }
                else if (OnNavigate is { } nav) { nav(+1); a.Handled = true; }
            }
            else if (a.KeyCode == Keys.Left)
            {
                if (CloseSelf is { } closeSelf) { closeSelf(); a.Handled = true; }
                else if (OnNavigate is { } nav) { nav(-1); a.Handled = true; }
            }
        }

        var rows = new Element[Items.Count];
        for (int i = 0; i < Items.Count; i++)
        {
            var it = Items[i];
            int idx = i;
            if (it.IsSeparator)
            {
                rows[i] = MenuFlyout.Separator(Parts);
                continue;
            }
            // Activation: a sub-menu row OPENS its cascade (WinUI MenuFlyoutSubItem click); other rows invoke + close.
            Action activate = it.Kind == MenuItemKind.SubMenu
                ? () => OpenSub(idx, focusFirst: false)
                : () => { it.Invoke?.Invoke(); Close(); };
            rows[i] = MenuFlyout.Row(
                it, i, activate, checkColumn, iconColumn,
                highlighted: i == hi,
                onKeyMove: Move,
                onRowKey: OnRowKey,
                onHover: _ => OnRowHover(idx),
                onRealized: h => rowNodes.Value[idx] = h,
                subOpen: i == subOpen,
                parts: Parts);
        }

        var children = new List<Element>(rows.Length + 2);
        children.AddRange(rows);

        // Cascade timers (mounted only while pending, keyed so a re-arm remounts a fresh countdown). ToolTipClock is
        // the shared per-frame countdown primitive (AnimEngine-driven, wall-accurate, unmounts when idle).
        if (pendingOpen >= 0)
            children.Add(Embed.Comp(() => new ToolTipClock
            {
                DurationMs = MenuFlyout.SubMenuShowDelayMs,
                OnElapsed = () => { if (pendingOpenIdx.Peek() >= 0) { OpenSub(pendingOpenIdx.Peek(), focusFirst: false); pendingOpenIdx.Value = -1; } },
            }) with { Key = "submenu-open:" + pendingOpen });
        if (closePending)
            children.Add(Embed.Comp(() => new ToolTipClock
            {
                DurationMs = MenuFlyout.SubMenuShowDelayMs,
                OnElapsed = () => { if (pendingClose.Peek()) CloseSub(); },
            }) with { Key = "submenu-close" });

        var column = new BoxEl
        {
            Direction = 1,
            MinWidth = MinWidth,                                  // FlyoutThemeMinWidth; final width is content-driven
            OnCharInput = HandleChar,                             // first-letter jump (bubbles from the focused row)
            Children = children.ToArray(),
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
