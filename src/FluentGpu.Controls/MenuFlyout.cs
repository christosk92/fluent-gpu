using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Hosting;
using FluentGpu.Scene;
using FluentGpu.Signals;

namespace FluentGpu.Controls;

/// <summary>The tri-state of a CheckBox / ToggleButton: cleared, set, or mixed (indeterminate).</summary>
public enum CheckState : byte { Unchecked = 0, Checked = 1, Indeterminate = 2 }

/// <summary>The kind of a <see cref="MenuFlyoutItem"/> row: a plain command, a divider, a checkable
/// <c>ToggleMenuFlyoutItem</c> (E73E check), a mutually-exclusive <c>RadioMenuFlyoutItem</c> (E915 bullet), or a
/// cascading <c>MenuFlyoutSubItem</c> (E974 chevron + nested popup), or a group <c>Header</c> (a caption the rows under it
/// share, never a stop: the WinUI flyout pattern of a <c>TextBlock</c> in CaptionTextBlockStyle over a radio group).</summary>
public enum MenuItemKind : byte { Command = 0, Separator = 1, Toggle = 2, Radio = 3, SubMenu = 4, Header = 5 }

/// <summary>How a menu style marks a radio row (<see cref="MenuFlyout.Style.RadioMark"/>).</summary>
public enum MenuRadioMark : byte
{
    /// <summary>WinUI's RadioMenuFlyoutItem: the style's <see cref="MenuFlyout.Style.RadioGlyph"/> (E915, a small bullet) on the chosen row, nothing on the others.</summary>
    Glyph = 0,
    /// <summary>A drawn disc of <see cref="MenuFlyout.Style.DotSize"/> in the mark ink on the chosen row; with <see cref="MenuFlyout.Style.RingAtRest"/>
    /// a ring of the same size on every other radio row. Exact at any size (no glyph's side bearings), so a theme can match its board.</summary>
    Dot = 1,
}

/// <summary>A single row in a <see cref="MenuFlyout"/>: a label, an optional leading <see cref="Icon"/> (icon column), an
/// enabled flag, the action to run when chosen, and an optional trailing keyboard-accelerator hint (e.g. "Ctrl+S"). The
/// <see cref="Icon"/> is a polymorphic <see cref="IconRef"/> — a plain glyph string converts implicitly (existing call
/// sites unchanged), or <c>IconRef.Themed("Name")</c> renders a layered vector icon. Use <see cref="Separator"/> for a
/// divider, <see cref="Toggle"/> for a checkable item (E73E check column), <see cref="RadioItem"/> for a
/// mutually-exclusive choice (E915 bullet), or <see cref="SubMenu"/> for a cascading sub-menu (E974 chevron column,
/// hover/Right-arrow opens the nested popup). All non-Command extras are optional so the primary
/// <c>(Label, Icon, Enabled, Invoke)</c> constructor call-sites are unchanged.</summary>
public readonly record struct MenuFlyoutItem(string Label, IconRef Icon = default, bool Enabled = true, Action? Invoke = null)
{
    public MenuItemKind Kind { get; init; }
    public bool IsChecked { get; init; }
    /// <summary>The label is CONTENT (a playlist's or a device's name), not a command word: a style's <see cref="MenuFlyout.Style.LabelCase"/> leaves it as written.</summary>
    public bool Literal { get; init; }
    /// <summary>Trailing right-aligned keyboard-accelerator text (WinUI KeyboardAcceleratorTextOverride), e.g. "Ctrl+S".</summary>
    public string? AcceleratorText { get; init; }
    /// <summary>Nested items (Kind == <see cref="MenuItemKind.SubMenu"/> only) — the cascading sub-menu's rows.</summary>
    public IReadOnlyList<MenuFlyoutItem>? SubItems { get; init; }

    public bool IsSeparator => Kind == MenuItemKind.Separator;
    public static MenuFlyoutItem Separator => new("") { Kind = MenuItemKind.Separator };

    /// <summary>A group header ("Sort by", "View"): caption text (12/16) in the tertiary ink over the rows it names, inset to the
    /// check column. Not focusable, never invoked, skipped by the keyboard cursor and the first-letter jump (it is disabled).</summary>
    public static MenuFlyoutItem Header(string label) => new(label, default, false) { Kind = MenuItemKind.Header };

    /// <summary>A checkable command (WinUI ToggleMenuFlyoutItem): an E73E check column whose glyph paints only when on.</summary>
    public static MenuFlyoutItem Toggle(string label, bool isChecked, Action? invoke = null, IconRef icon = default, bool enabled = true)
        => new(label, icon, enabled, invoke) { Kind = MenuItemKind.Toggle, IsChecked = isChecked };

    /// <summary>A mutually-exclusive command (WinUI RadioMenuFlyoutItem): an E915 bullet column whose glyph paints when selected.</summary>
    public static MenuFlyoutItem RadioItem(string label, bool isChecked, Action? invoke = null, IconRef icon = default, bool enabled = true)
        => new(label, icon, enabled, invoke) { Kind = MenuItemKind.Radio, IsChecked = isChecked };

    /// <summary>A cascading sub-menu (WinUI MenuFlyoutSubItem): an E974 chevron in the trailing column
    /// (MenuFlyout_themeresources.xaml:720 SubItemChevron); hovering the row for MenuShowDelay (400ms default,
    /// MenuFlyout_Partial.h:13), clicking it, or Right-arrow opens the nested menu to the right.</summary>
    public static MenuFlyoutItem SubMenu(string label, IReadOnlyList<MenuFlyoutItem> items, IconRef icon = default, bool enabled = true)
        => new(label, icon, enabled) { Kind = MenuItemKind.SubMenu, SubItems = items };
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
    /// CascadingMenuHelper.cpp:93-95 fallback, :439/:508 open/close timer intervals). The platform pump writes the
    /// live registry value into <see cref="FluentGpu.Pal.SystemParams"/> at startup; headless stays 400.</summary>
    public static float SubMenuShowDelayMs => FluentGpu.Pal.SystemParams.MenuShowDelayMs;
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

    /// <summary>The row height of <paramref name="size"/> on the kit's size axis, in the active theme's menu style: <see cref="ControlSize.Medium"/>
    /// (the default) is <see cref="Style.RowHeight"/> (WinUI's 36-DIP row: the 14px line inside MenuFlyoutItemThemePadding 11,8,11,9);
    /// <see cref="ControlSize.Large"/> is <see cref="Style.LargeRowHeight"/> (the 40-DIP touch target a compact window asks for, a
    /// documented extension: WinUI has no taller menu row). Small is remapped to Medium (the ClampSize idiom: a row shorter than its
    /// own 14px line plus padding is not a row).</summary>
    public static float RowHeightFor(ControlSize size) => RowHeightFor(size, DefaultStyle);

    /// <summary><see cref="RowHeightFor(ControlSize)"/> in an already-resolved <paramref name="style"/>.</summary>
    public static float RowHeightFor(ControlSize size, Style style) => size == ControlSize.Large ? style.LargeRowHeight : style.RowHeight;

    /// <summary>The menu's look: the plate the host draws under the rows (<see cref="Plate"/>), the rows and the separator. Read at
    /// RENDER through the control style table (<see cref="ControlStyles.MenuFlyoutStyle"/>), so a theme swap restyles the next
    /// open; every default is the WinUI MenuFlyout_themeresources value the menu has always used. It also styles a
    /// <see cref="ContextMenu"/>'s menu body (the context menu's own parts are <see cref="ContextMenu.Style"/>).</summary>
    public sealed record Style
    {
        /// <summary>The presenter card (the host FlyoutSurface plate of <see cref="PopupChrome.Flyout"/>): acrylic, flyout ring,
        /// OverlayCornerRadius, flyout elevation and the MenuFlyoutPresenterThemePadding 0,2,0,2.</summary>
        public PopupPlate Plate { get; init; } = PopupPlate.Fluent();
        /// <summary>The row height (Medium and Small): 36.</summary>
        public float RowHeight { get; init; } = MenuFlyout.RowHeight;
        /// <summary>The Large (touch) row height: the Large control metric, 40.</summary>
        public float LargeRowHeight { get; init; } = ControlMetrics.For(ControlSize.Large).MinHeight;
        /// <summary>A row's margin inside the plate (MenuFlyoutItemMargin 4,2,4,2).</summary>
        public Edges4 ItemMargin { get; init; } = MenuFlyout.ItemMargin;
        /// <summary>A row's padding (MenuFlyoutItemThemePadding 11,8,11,9).</summary>
        public Edges4 ItemPadding { get; init; } = MenuFlyout.ItemPadding;
        /// <summary>A row's hover/press backplate radius (ControlCornerRadius).</summary>
        public float ItemRadius { get; init; } = Radii.Control;
        /// <summary>The row fill on pointer-over, under the keyboard cursor and while its sub-menu is open (SubtleFillColorSecondary).</summary>
        public ColorF ItemHoverFill { get; init; } = Tok.FillSubtleSecondary;
        /// <summary>The row fill while pressed (SubtleFillColorTertiary).</summary>
        public ColorF ItemPressedFill { get; init; } = Tok.FillSubtleTertiary;
        /// <summary>The label's size (ControlContentThemeFontSize 14).</summary>
        public float LabelSize { get; init; } = 14f;
        /// <summary>The separator line's colour (DividerStrokeColorDefault).</summary>
        public ColorF SeparatorColor { get; init; } = Tok.StrokeDividerDefault;
        /// <summary>The separator line's thickness (MenuFlyoutSeparatorHeight 1).</summary>
        public float SeparatorThickness { get; init; } = SeparatorHeight;
        /// <summary>The separator line's margin (SeparatorThemePadding's -4 bleed past the row inset).</summary>
        public Edges4 SeparatorMargin { get; init; } = new(-4, 0, -4, 0);
        /// <summary>The separator row's padding around the line (1 above, 1 below).</summary>
        public Edges4 SeparatorPadding { get; init; } = new(0, 1, 0, 1);
        /// <summary>Whether a row's icon is drawn (and its 28 column reserved). False: the menu is words only. Default true.</summary>
        public bool ShowIcons { get; init; } = true;
        /// <summary>The case transform of a row's label and its trailing accelerator text, applied at shaping (the stored label is never
        /// rewritten). Default <see cref="TextCase.AsWritten"/>.</summary>
        public TextCase LabelCase { get; init; } = TextCase.AsWritten;

        // ── The selection state: how a checked ToggleMenuFlyoutItem and the chosen RadioMenuFlyoutItem read, at rest. Each design
        //    language marks a choice its own way, so this is the theme's, never a call site's. The defaults are WinUI 3's
        //    (MenuFlyout_themeresources.xaml ToggleMenuFlyoutItem CheckGlyph E73E; RadioMenuFlyoutItem_themeresources.xaml CheckGlyph
        //    E915): a 12px glyph in TextFillColorSecondary in the 28 check column, lifted to TextFillColorPrimary under the pointer
        //    (MenuFlyoutItemForegroundPointerOver) and while pressed (MenuFlyoutSubItemForegroundPressed); no row fill, no label tint.
        /// <summary>The check column's width (MenuFlyoutItemPlaceholderThemeThickness 28): the label starts this far past the row padding.</summary>
        public float MarkColumnWidth { get; init; } = PlaceholderWidth;
        /// <summary>A checked toggle row's glyph (ToggleMenuFlyoutItem CheckGlyph E73E).</summary>
        public string CheckGlyph { get; init; } = MenuFlyout.CheckGlyph;
        /// <summary>How a radio row is marked: the <see cref="RadioGlyph"/> on the chosen row (WinUI), or a drawn <see cref="MenuRadioMark.Dot"/>.</summary>
        public MenuRadioMark RadioMark { get; init; } = MenuRadioMark.Glyph;
        /// <summary>The chosen radio row's glyph under <see cref="MenuRadioMark.Glyph"/> (RadioMenuFlyoutItem CheckGlyph E915).</summary>
        public string RadioGlyph { get; init; } = MenuFlyout.RadioGlyph;
        /// <summary>The check and radio glyphs' size (CheckGlyph FontSize 12).</summary>
        public float MarkGlyphSize { get; init; } = CheckGlyphSize;
        /// <summary>The mark's ink at rest (MenuFlyoutSubItemChevron = TextFillColorSecondary).</summary>
        public ColorF MarkColor { get; init; } = Tok.TextSecondary;
        /// <summary>The mark's ink under the pointer (MenuFlyoutItemForegroundPointerOver = TextFillColorPrimary).</summary>
        public ColorF MarkHoverColor { get; init; } = Tok.TextPrimary;
        /// <summary>The mark's ink while pressed (MenuFlyoutSubItemForegroundPressed = TextFillColorPrimary).</summary>
        public ColorF MarkPressedColor { get; init; } = Tok.TextPrimary;
        /// <summary>The disc's diameter under <see cref="MenuRadioMark.Dot"/> (and the ring's, with <see cref="RingAtRest"/>).</summary>
        public float DotSize { get; init; } = 6f;
        /// <summary>Under <see cref="MenuRadioMark.Dot"/>: every radio row that is not chosen draws a ring of <see cref="DotSize"/>, so the group
        /// reads as a choice at rest (the Metro radio). Default false (WinUI marks only the chosen row).</summary>
        public bool RingAtRest { get; init; }
        /// <summary>The ring's stroke under <see cref="RingAtRest"/>.</summary>
        public float RingWidth { get; init; } = 1f;
        /// <summary>The ring's ink under <see cref="RingAtRest"/>.</summary>
        public ColorF RingColor { get; init; } = Tok.TextTertiary;
        /// <summary>The label (and accelerator) ink of a checked toggle or the chosen radio row; null keeps the row's ink (WinUI). A Metro menu
        /// writes the choice in the accent.</summary>
        public ColorF? SelectedLabelColor { get; init; }
        /// <summary>The chosen radio row's fill at rest; transparent in WinUI (the glyph alone marks it).</summary>
        public ColorF SelectedFill { get; init; } = Tok.FillSubtleTransparent;
        /// <summary>The chosen radio row's fill under the pointer and the keyboard cursor; null = <see cref="ItemHoverFill"/>.</summary>
        public ColorF? SelectedHoverFill { get; init; }
        /// <summary>The chosen radio row's fill while pressed; null = <see cref="ItemPressedFill"/>.</summary>
        public ColorF? SelectedPressedFill { get; init; }
        /// <summary>A sub-menu row's trailing glyph (SubItemChevron E974).</summary>
        public string SubMenuGlyph { get; init; } = Icons.ChevronRightMed;
        /// <summary>The sub-menu glyph's size (SubItemChevron FontSize 12).</summary>
        public float SubMenuGlyphSize { get; init; } = ChevronGlyphSize;
        /// <summary>The sub-menu glyph's ink at rest (MenuFlyoutSubItemChevron = TextFillColorSecondary; pressed TextFillColorTertiary).</summary>
        public ColorF SubMenuGlyphColor { get; init; } = Tok.TextSecondary;
    }

    /// <summary>The menu style now: the active theme's (<see cref="ControlStyles.MenuFlyoutStyle"/>), else <see cref="FluentStyle"/>.</summary>
    public static Style DefaultStyle => ControlStyles.Resolve(ControlStyles.MenuFlyoutStyle, FluentStyle);

    /// <summary>The built-in Fluent default (what a theme table without a MenuFlyout entry resolves to). Reads the live Tok on every call.</summary>
    public static Style FluentStyle() => new();

    /// <summary>The one canonical MenuFlyout body factory. <paramref name="focusFirst"/>: put the keyboard cursor +
    /// focus on the first selectable row after mount (WinUI keyboard-invoked menu / context menu opened via Menu key —
    /// <see cref="MenuFlyoutPresenter.FocusFirstOnMount"/>). <paramref name="size"/>: the rows' height on the size axis
    /// (<see cref="RowHeightFor"/>), inherited by every cascading sub-menu.</summary>
    public static Element Create(IReadOnlyList<MenuFlyoutItem> items, Action close, float minWidth = ThemeMinWidth,
        TemplateParts? parts = null, bool focusFirst = false, ControlSize size = ControlSize.Medium)
        => Create(items, close, minWidth, onNavigate: null, parts, focusFirst, size);

    /// <summary><paramref name="onNavigate"/>: Left(-1)/Right(+1) pressed while the menu is open and the focused row is
    /// not a sub-menu boundary — MenuBar uses it to move between adjacent open menus (MenuBarItem.cpp:205-228
    /// OnPresenterKeyDown → OpenFlyoutFrom). <paramref name="focusFirst"/> focuses the first row on mount (keyboard open).</summary>
    public static Element Create(IReadOnlyList<MenuFlyoutItem> items, Action close, float minWidth, Action<int>? onNavigate,
        TemplateParts? parts = null, bool focusFirst = false, ControlSize size = ControlSize.Medium)
        => Embed.Comp(() => new MenuFlyoutPresenter { Items = items, Close = close, MinWidth = minWidth, OnNavigate = onNavigate, Parts = parts, FocusFirstOnMount = focusFirst, Size = size });

    /// <summary>Internal alias retained for in-Controls callers (DropDownButton/SplitButton/ToggleSplitButton/MenuBar/
    /// ContextMenu/CommandBarFlyout sub-menus/MediaPlayerElement) — the public door is <see cref="Create"/>.</summary>
    internal static Element Build(IReadOnlyList<MenuFlyoutItem> items, Action close, float minWidth = ThemeMinWidth,
        TemplateParts? parts = null, bool focusFirst = false)
        => Create(items, close, minWidth, onNavigate: null, parts, focusFirst);

    // ── Separator: a 1px DividerStrokeColorDefault line; SeparatorThemePadding -4,1,-4,1 bleeds it past the item
    //    inset to full presenter width (we model the -4 bleed with a negative horizontal margin on the line). ────────
    /// <summary>A group header row: CaptionTextBlockStyle (12/16) in TextFillColorTertiary, on the rows' margin and their left padding,
    /// 8 above and 4 below the line (a 32 row), so the caption's first letter sits over the check column's glyph.</summary>
    internal static Element HeaderRow(MenuFlyoutItem it, Style st) => new BoxEl
    {
        Direction = 0, AlignItems = FlexAlign.Center, Margin = st.ItemMargin,
        Padding = new Edges4(st.ItemPadding.Left, HeaderPadTop, st.ItemPadding.Right, HeaderPadBottom),
        Role = AutomationRole.Text,
        Children = [new TextEl(it.Label) { Size = HeaderSize, LineHeight = HeaderLine, Color = Tok.TextTertiary, MaxLines = 1, Case = it.Literal ? TextCase.AsWritten : st.LabelCase }],
    };

    internal const float HeaderSize = 12f, HeaderLine = 16f, HeaderPadTop = 8f, HeaderPadBottom = 4f;

    internal static Element Separator(Style st, TemplateParts? parts = null) => parts.Apply(PartSeparator, new BoxEl
    {
        Direction = 1,
        Justify = FlexJustify.Center,
        Padding = st.SeparatorPadding,                             // SeparatorThemePadding vertical (1 top, 1 bottom)
        Children = [new BoxEl { Height = st.SeparatorThickness, Margin = st.SeparatorMargin, Fill = st.SeparatorColor }],
    });

    // ── A single command/toggle/radio/sub-menu row. <paramref name="highlighted"/> = the keyboard cursor (Up/Down) →
    //    renders the WinUI PointerOver fill so arrow-roving looks identical to a mouse hover. <paramref name="subOpen"/>
    //    = this row's cascading sub-menu is open (WinUI SubMenuOpened state = the PointerOver fill held). ────────────
    internal static Element Row(
        MenuFlyoutItem it, int index, Action activate, bool checkColumn, bool iconColumn, bool highlighted,
        Action<int>? onKeyMove, Action<KeyEventArgs>? onRowKey, Action<Point2>? onHover, Action? onExit, Action<NodeHandle>? onRealized,
        Style st, bool subOpen = false, TemplateParts? parts = null, ControlSize size = ControlSize.Medium)
    {
        bool enabled = it.Enabled;
        // The selection state (the style's, so each theme marks a choice in its own language): a checked toggle or the chosen radio.
        bool marked = it.IsChecked && it.Kind is MenuItemKind.Toggle or MenuItemKind.Radio;
        bool chosenRadio = enabled && marked && it.Kind == MenuItemKind.Radio;
        // MenuFlyoutItemForeground = TextPrimary; Disabled = TextDisabled. PointerOver/Pressed foreground stay Primary
        // (the WinUI MenuFlyoutItem hover/pressed states do NOT recolor the label — they only change the row fill). A style may write
        // the chosen row's label in its own ink (SelectedLabelColor: the Metro accent).
        ColorF fg = !enabled ? Tok.TextDisabled : marked && st.SelectedLabelColor is { } selInk ? selInk : Tok.TextPrimary;
        // SubItemChevron foreground = MenuFlyoutSubItemChevron = TextSecondary; Pressed = TextTertiary (MenuFlyoutSubItemChevronPressed);
        // Disabled = TextDisabled (themeresources:26-30).
        ColorF chevronFg = enabled ? st.SubMenuGlyphColor : Tok.TextDisabled;

        var children = new List<Element>(5);

        // Column 0: the check column (MarkColumnWidth): the toggle's check or the radio's mark, painted when on (CheckGlyph.Opacity 0→1),
        // or the style's ring on a radio row that is not chosen. Every row of a checkable menu keeps the column, so a change of choice
        // moves nothing.
        if (checkColumn)
        {
            Element mark = new BoxEl();
            if (it.Kind == MenuItemKind.Radio && st.RadioMark == MenuRadioMark.Dot)
            {
                if (marked || st.RingAtRest) mark = Dot(marked, enabled, st);
            }
            else if (marked)
            {
                mark = new TextEl(it.Kind == MenuItemKind.Radio ? st.RadioGlyph : st.CheckGlyph)
                {
                    Size = st.MarkGlyphSize, FontFamily = Tok.Definition.Fonts.Icon,
                    Color = enabled ? st.MarkColor : Tok.TextDisabled,
                    HoverColor = enabled ? st.MarkHoverColor : default, PressedColor = enabled ? st.MarkPressedColor : default,
                };
            }
            children.Add(new BoxEl { Width = st.MarkColumnWidth, Shrink = 0f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Start, Children = [mark] });
        }

        // Column 1: 16×16 icon (IconRoot Viewbox), painted when this row carries an icon. Glyph vs layered-vector is
        // decided by IconView (the single IconRef render path); disabled dimming rides `fg` for glyphs, the enabled
        // thunk for themed layers.
        if (iconColumn)
        {
            bool rowEnabled = enabled;
            Element icon = IconView.Render(it.Icon, IconGlyphSize, glyphColor: fg, enabled: () => rowEnabled);
            children.Add(new BoxEl { Width = PlaceholderWidth, AlignItems = FlexAlign.Center, Justify = FlexJustify.Start, Children = [icon] });
        }

        // Column 2: the label (TextBlock, "*" column → grows).
        children.Add(new TextEl(it.Label) { Size = st.LabelSize, Color = fg, Grow = 1f, Case = it.Literal ? TextCase.AsWritten : st.LabelCase });

        // Column 3: optional keyboard-accelerator hint, right-aligned (HorizontalAlignment=Right),
        // CaptionTextBlock (12px), MenuFlyoutItemKeyboardAcceleratorTextForeground = TextSecondary / Disabled.
        if (it.AcceleratorText is { Length: > 0 } acc)
            children.Add(new TextEl(acc)
            {
                Size = 12f, Margin = new Edges4(24, 4, 0, 0), Case = it.Literal ? TextCase.AsWritten : st.LabelCase,
                Color = !enabled ? Tok.TextDisabled : marked && st.SelectedLabelColor is { } accInk ? accInk : Tok.TextSecondary,
            });

        // Column 4 (sub-menu rows): the cascade glyph — WinUI E974 @12, MenuFlyoutItemChevronMargin 24,0,0,-1, foreground
        // MenuFlyoutSubItemChevron ramp (MenuFlyout_themeresources.xaml:26-30 + :720 SubItemChevron); the style's glyph and ink.
        if (it.Kind == MenuItemKind.SubMenu)
            children.Add(new TextEl(st.SubMenuGlyph)
            {
                Size = st.SubMenuGlyphSize, FontFamily = Tok.Definition.Fonts.Icon, Margin = ChevronMargin,
                Color = chevronFg, PressedColor = enabled ? Tok.TextTertiary : chevronFg, DisabledColor = Tok.TextDisabled,
            });

        // The keyboard cursor (highlight) and the SubMenuOpened state render the same brush as PointerOver
        // (MenuFlyoutSubItemBackgroundSubMenuOpened = SubtleFillColorSecondary) so they read identically to a hover. The chosen radio row
        // rests on the style's SelectedFill (transparent in WinUI) and takes its own hover/press ramp when the style gives one.
        ColorF hoverFill = chosenRadio && st.SelectedHoverFill is { } sh ? sh : st.ItemHoverFill;
        ColorF pressedFill = chosenRadio && st.SelectedPressedFill is { } sp ? sp : st.ItemPressedFill;
        ColorF rest = !enabled ? Tok.FillSubtleTransparent
            : highlighted || subOpen ? hoverFill
            : chosenRadio ? st.SelectedFill : Tok.FillSubtleTransparent;

        var row = new BoxEl
        {
            Direction = 0,
            Height = RowHeightFor(size, st),                       // 36 (= MinHeight 32 clamped up by the 14px line + 8/9 padding); Large 40
            MinHeight = MathF.Min(ThemeMinHeight, RowHeightFor(size, st)),   // MenuFlyoutThemeMinHeight = 32 (a shorter themed row keeps its height)
            AlignItems = FlexAlign.Center,
            Margin = st.ItemMargin,                                // 4,2,4,2
            Padding = st.ItemPadding,                              // 11,8,11,9
            Gap = 0f,                                              // columns own their fixed 28px width; no inter-gap (WinUI grid)
            Corners = CornerRadius4.All(st.ItemRadius),            // ControlCornerRadius = 4
            // Toggle/radio surface a11y as their WinUI peers (CheckBox/RadioButton patterns); plain command = MenuItem.
            Role = it.Kind switch { MenuItemKind.Toggle => AutomationRole.CheckBox, MenuItemKind.Radio => AutomationRole.RadioButton, _ => AutomationRole.MenuItem },
            // MenuFlyoutItemBackground (explicit rest) / ...PointerOver / ...Pressed. Disabled = transparent (no hover/press).
            Fill = rest,
            HoverFill = enabled ? hoverFill : Tok.FillSubtleTransparent,       // SubtleFillColorSecondary
            PressedFill = enabled ? pressedFill : Tok.FillSubtleTransparent,   // SubtleFillColorTertiary

            IsEnabled = enabled,                                   // engine gate: disabled = no hit-test/focus/keyboard/click
            Focusable = enabled,                                   // roving target (focus trap Tab/Shift-Tab + Enter/Space activation)
            TabIndex = index + 1,                                  // document order within the menu
            OnRealized = onRealized,
            OnHoverMove = onHover,                                 // presenter cascade bookkeeping (sub-menu delay open/close)
            OnPointerExit = onExit,                                // pointer left the row: cancel delay-open / arm delay-close
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
                OnClick = row.OnClick, OnKeyDown = row.OnKeyDown, OnHoverMove = row.OnHoverMove, OnPointerExit = row.OnPointerExit,
                OnRealized = TemplateParts.Chain(onRealized, m.OnRealized),
                Children = row.Children,
            };
        }
        return row;
    }

    /// <summary>The <see cref="MenuRadioMark.Dot"/> mark: a disc in the mark ink when chosen, else the style's ring (<see cref="Style.RingAtRest"/>).
    /// Both are the same box, so choosing another row recolours it in place.</summary>
    static BoxEl Dot(bool chosen, bool enabled, Style st) => new()
    {
        Width = st.DotSize, Height = st.DotSize, Shrink = 0f, Corners = CornerRadius4.All(st.DotSize * 0.5f),
        Fill = chosen ? (enabled ? st.MarkColor : Tok.TextDisabled) : ColorF.Transparent,
        BorderWidth = chosen ? 0f : st.RingWidth, BorderColor = enabled ? st.RingColor : Tok.TextDisabled,
        HitTestVisible = false,
    };
}

/// <summary>The MenuFlyoutPresenter body: a vertical list of <see cref="MenuFlyout.Row"/>s with a keyboard-cursor
/// (highlight) signal driving Up/Down/Home/End roving (focus follows the cursor), first-letter jump-to-item, and
/// cascading <see cref="MenuItemKind.SubMenu"/> rows, whose open/keep/close hover intent is this level's
/// <see cref="MenuCascade"/> (WinUI CascadingMenuHelper delays plus submenu aim). Wrapped by the host's FlyoutSurface
/// (acrylic + stroke + shadow + corners + 0,2,0,2 padding). Tall menus scroll internally. Run-once-ish: re-render
/// triggers are the highlight and the open cascade only, so pointer hover/press never re-renders.</summary>
internal sealed class MenuFlyoutPresenter : Component
{
    public required IReadOnlyList<MenuFlyoutItem> Items;
    public required Action Close;
    public float MinWidth = MenuFlyout.ThemeMinWidth;
    /// <summary>MenuBar adjacent-menu navigation: invoked with -1 (Left) / +1 (Right) when the key is not a
    /// sub-menu boundary action (MenuBarItem.cpp:205-228 OnPresenterKeyDown).</summary>
    public Action<int>? OnNavigate;
    /// <summary>Set on a SUB-menu presenter: the cascade of the level that opened it. Left closes this level through it
    /// (WinUI returns focus to the parent item; the overlay's close-start focus restore does exactly that), and the
    /// pointer anywhere in this level keeps every ancestor's cascade open.</summary>
    internal MenuCascade? ParentCascade;
    /// <summary>Keyboard-opened menus put the cursor + focus on the FIRST selectable row after mount (WinUI
    /// CascadingMenuHelper keyboard open focuses the first MenuFlyoutItem; MenuBar Down-open does the same).</summary>
    public bool FocusFirstOnMount;
    /// <summary>Part modifiers keyed by the <see cref="MenuFlyout"/> <c>PartXxx</c> consts — inherited by every
    /// cascading sub-menu level.</summary>
    public TemplateParts? Parts;
    /// <summary>The rows' height on the size axis (<see cref="MenuFlyout.RowHeightFor"/>) — inherited by every cascading sub-menu level.</summary>
    public ControlSize Size = ControlSize.Medium;

    // WinUI MenuFlyoutPresenter ScrollViewer max before internal scroll (mirrors the flyout's content cap; AutoSuggest
    // uses 374 — menus share the same overlay sizing). Content shorter than this never scrolls. A placement with less room
    // caps it lower (PopupRoom).
    const float MaxHeight = 468f;

    /// <summary>The height this level's list may take: <see cref="MaxHeight"/>, or the room its placement has
    /// (<see cref="PopupRoom.Height"/>) when that is less.</summary>
    static float ListCap(IOverlayService svc, Signal<OverlayPlacementInfo>? placement)
        => MathF.Min(MaxHeight, PopupRoom.Height(svc, placement, MenuFlyout.DefaultStyle.Plate));

    MenuCascade? _cascade;
    NodeHandle[] _rowNodes = [];
    NodeHandle _selfNode;

    public override Element Render()
    {
        var svc = UseContext(Overlay.Service);
        var hooks = UseContext(InputHooks.Current);
        var timers = UseContext(HostTimers.Current);
        var highlight = UseSignal(-1);                            // keyboard cursor index; -1 = none (pointer-only)
        // The list's cap: the room this level's placement has (PopupRoom), re-read whenever the overlay places it (a
        // window resize, the first measured placement that decides window or not) and re-rendered only when it changes.
        var placement = UseContext(Overlay.Placement);
        var listCap = UseSignal(ListCap(svc, placement));
        UseEffect(() =>
        {
            _ = placement?.Value;
            float cap = ListCap(svc, placement);
            if (MathF.Abs(cap - listCap.Peek()) >= 0.5f) listCap.Value = cap;
        });
        if (_rowNodes.Length != Items.Count) _rowNodes = new NodeHandle[Items.Count];

        // This level's cascade, wired once (the items and the parent are mount-time fields of this presenter).
        var cascade = _cascade;
        if (cascade is null)
        {
            cascade = _cascade = new MenuCascade
            {
                Parent = ParentCascade,
                Parts = Parts,
                Size = Size,
                CloseAll = Close,
            };
            cascade.Scene = () => Context.Scene;
            cascade.SelfNode = () => _selfNode;
            cascade.RowNode = i => (uint)i < (uint)_rowNodes.Length ? _rowNodes[i] : default;
            cascade.CascadeOf = i => (uint)i < (uint)Items.Count && Items[i] is { Kind: MenuItemKind.SubMenu, Enabled: true, SubItems: { Count: > 0 } sub } ? sub : null;
        }
        cascade.Overlay = svc;
        cascade.Hooks = hooks;
        cascade.Timers = timers;
        UseEffect(() => (Action?)(() => cascade.Dispose()), DepKey.Empty);   // unmount: no timer outlives the level

        int hi = highlight.Value;                                 // subscribe → re-highlight on arrow move
        int subOpen = cascade.OpenIndex.Value;                    // subscribe → re-render the SubMenuOpened row fill

        // The menu's look, read now (the control style table): a theme swap restyles the next open, nothing is frozen.
        var st = MenuFlyout.DefaultStyle;

        bool checkColumn = false, iconColumn = false;
        for (int i = 0; i < Items.Count; i++)
        {
            var it = Items[i];
            if (it.IsSeparator || it.Kind == MenuItemKind.Header) continue;
            if (it.Kind is MenuItemKind.Toggle or MenuItemKind.Radio) checkColumn = true;
            if (it.Icon.HasContent && st.ShowIcons) iconColumn = true;
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
            var node = _rowNodes[i];
            if (node.IsNull) return;
            hooks.MoveFocusVisual?.Invoke(node);
            ScrollReveal.Row(Context.Scene, node);   // a row past the capped list's viewport scrolls into view
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

        // Keyboard-opened menus: cursor + focus land on the first selectable row once the rows have realized
        // (post-layout, exactly once) — WinUI CascadingMenuHelper keyboard open / MenuBar Down-open semantics.
        var focusedFirst = UseRef(false);
        UseLayoutEffect(() =>
        {
            if (!FocusFirstOnMount || focusedFirst.Value) return;
            int f = FirstSelectable();
            if (f < 0 || _rowNodes[f].IsNull) return;
            focusedFirst.Value = true;
            SetCursor(f);
        }, FocusFirstOnMount);

        // Row-level non-roving keys: Right opens a cascade (focus-first), Left closes a cascade level (sub menus) or
        // navigates the MenuBar; Enter on a sub-item row opens its cascade (the OnClick path handles activation).
        void OnRowKey(KeyEventArgs a)
        {
            int cur = highlight.Peek();
            if (a.KeyCode == Keys.Right)
            {
                if (IsSelectable(cur) && Items[cur].Kind == MenuItemKind.SubMenu) { cascade.Open(cur, focusFirst: true); a.Handled = true; }
                else if (OnNavigate is { } nav) { nav(+1); a.Handled = true; }
            }
            else if (a.KeyCode == Keys.Left)
            {
                // Left closes the innermost open level. Focus on THIS level while a cascade of it is open (hover opened it, so
                // focus never moved into it): close that cascade and stay on this row, as WinUI's Left does from inside the
                // submenu (close it, focus back on its parent row). Only with no cascade of its own does a submenu level close
                // itself (and a root level hand Left to the MenuBar).
                if (cascade.OpenIndex.Peek() >= 0) { cascade.CloseChild(); a.Handled = true; }
                else if (ParentCascade is { } parent) { parent.CloseChild(); a.Handled = true; }
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
                rows[i] = MenuFlyout.Separator(st, Parts);
                continue;
            }
            if (it.Kind == MenuItemKind.Header)
            {
                rows[i] = MenuFlyout.HeaderRow(it, st);
                continue;
            }
            // Activation: a sub-menu row OPENS its cascade (WinUI MenuFlyoutSubItem click, a touch or pen tap); other
            // rows invoke + close.
            Action activate = it.Kind == MenuItemKind.SubMenu
                ? () => cascade.Open(idx, focusFirst: false)
                : () => { it.Invoke?.Invoke(); Close(); };
            rows[i] = MenuFlyout.Row(
                it, i, activate, checkColumn, iconColumn,
                highlighted: i == hi,
                onKeyMove: Move,
                onRowKey: OnRowKey,
                onHover: _ => cascade.RowHover(idx),
                onExit: () => cascade.RowExit(idx),
                onRealized: h => _rowNodes[idx] = h,
                st: st,
                subOpen: i == subOpen,
                parts: Parts,
                size: Size);
        }

        var column = new BoxEl
        {
            Direction = 1,
            MinWidth = MinWidth,                                  // FlyoutThemeMinWidth; final width is content-driven
            OnCharInput = HandleChar,                             // first-letter jump (bubbles from the focused row)
            // Every move over this level — the rows AND the gaps, separators and headers between them — feeds the
            // cascade's aim (MenuCascade.Sample). A press on a gap moves no focus (the row under the keyboard keeps it).
            OnPointerMoveWithin = _ => cascade.Sample(),
            OnPointerExit = cascade.LevelExit,                    // left the gaps: maybe the whole menu
            AllowFocusOnInteraction = false,
            Children = rows,
        };

        // Size to content (the common case) and cap at the room the placement has (at most MaxHeight) with internal scroll,
        // matching MenuFlyoutPresenterScrollViewer. The layout engine content-sizes auto scroll viewports, then clamps by
        // MaxHeight; a clipped list feathers the edge it overflows (the scroll edge cue), so the rows below read as more.
        var parentCascade = ParentCascade;
        return new ScrollEl
        {
            Content = column,
            ContentSized = true,
            MinWidth = MinWidth,
            MaxHeight = listCap.Value,
            EdgeCues = ScrollEdgeCues.Auto,
            // This level's rect: its own "pointer left the menu" test, and for a cascaded level the level that opened it
            // (the aim triangle's far edge, the chain test).
            OnRealized = h =>
            {
                _selfNode = h;
                parentCascade?.ChildMounted(cascade, h);
            },
        };
    }
}
