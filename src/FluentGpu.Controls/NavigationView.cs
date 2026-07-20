using System.Linq;
using FluentGpu.Animation;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Scene;
using FluentGpu.Signals;

namespace FluentGpu.Controls;

/// <summary>
/// A NavigationView pane entry: a stable key, an icon glyph, and a label. A parent group sets <see cref="Children"/>
/// (rendered as an expand/collapse subtree with a chevron + indent); <see cref="InitiallyExpanded"/> seeds it open.
/// <see cref="IsSeparator"/> renders the WinUI <c>NavigationViewItemSeparator</c> (1px DividerStroke rule —
/// NavigationView_themeresources.xaml:223 height / :46 foreground / :247 margin 0,3,0,4); <see cref="InfoBadge"/> is
/// the per-item InfoBadgePresenter slot (right-aligned in the row; overlaid on the icon in the compact rail).
/// The positional header is unchanged, so flat <c>new NavItem(key, glyph, label)</c> / <c>IsHeader</c> call sites keep working.
/// </summary>
public sealed record NavItem(string Key, string Glyph, string Label, bool IsHeader = false)
{
    public NavItem[]? Children { get; init; }
    public bool InitiallyExpanded { get; init; }
    /// <summary>Optional visual content for the icon slot. When set, it replaces the font glyph.</summary>
    public Element? IconContent { get; init; }
    /// <summary>Renders as the WinUI NavigationViewItemSeparator instead of an item.</summary>
    public bool IsSeparator { get; init; }
    /// <summary>InfoBadge slot (the WinUI InfoBadgePresenter): pass <c>InfoBadge.Dot(...)/Count(...)</c> etc.</summary>
    public Element? InfoBadge { get; init; }
    /// <summary>Marks the auto-generated settings footer row (routes <see cref="NavigationView.PartSettingsItem"/>).</summary>
    internal bool IsSettings { get; init; }
    internal bool IsExpandable => !IsHeader && !IsSeparator && Children is { Length: > 0 };
}

public enum PaneMode : byte { Expanded = 0, Compact = 1, Minimal = 2 }

/// <summary>WinUI <c>NavigationViewPaneDisplayMode</c> (NavigationView.idl): Auto adapts by width; Left/LeftCompact/
/// LeftMinimal force a left-pane mode; <see cref="Top"/> renders the horizontal top bar with overflow.</summary>
public enum NavPaneDisplayMode : byte { Auto = 0, Left = 1, LeftCompact = 2, LeftMinimal = 3, Top = 4 }

/// <summary>WinUI <c>NavigationView.PaneClosing</c> args — set <see cref="Cancel"/> to keep the pane open
/// (NavigationView.cpp:1708-1714 honors the event's Cancel before closing).</summary>
public sealed class NavPaneClosingArgs
{
    public bool Cancel;
}

/// <summary>
/// Adaptive NavigationView modeled on WinUI's SplitView-backed template: expanded / compact / minimal LEFT modes plus
/// the TOP display mode (horizontal items + overflow flyout), a WinUI-style pane toggle row, section headers,
/// separators, pinned footer items (+ the auto settings item), an AutoSuggestBox slot, per-item InfoBadge slots, and
/// the sliding selection indicator.
///
/// Behavior verified against microsoft-ui-xaml controls\dev\NavigationView\NavigationView.cpp:
/// • Adaptive thresholds (cpp:1448-1458): width ≥ ExpandedModeThresholdWidth → Expanded; 0 &lt; width &lt;
///   CompactModeThresholdWidth → Minimal; else Compact. Defaults 1008 / 641 (NavigationView.idl:176-181
///   MUX_DEFAULT_VALUE). An adaptive transition INTO Compact/Minimal closes the pane (cpp:1477-1483).
/// • Pane close on invoke (cpp:4838-4845 ClosePaneIfNeccessaryAfterItemIsClicked): only when the pane is open AND
///   the display mode is not Expanded AND the clicked item has no children.
/// • Home/End focus the first/last item (cpp:2755-2761 → KeyboardFocusFirstItemFromItem/Last); Up/Down move through
///   the visible rows; Right expands / first child, Left collapses / parent (the hierarchy semantics).
/// • Alt+Left closes a light-dismissible (overlay) pane (cpp:3127-3136; IsLightDismissible cpp:1841-1849 = the
///   SplitView is not Inline/CompactInline — here: the adaptive mode is not the inline Expanded pane).
/// • PaneClosing is CANCELABLE (cpp:1708-1714); DisplayModeChanged fires on mode transitions (SetDisplayMode).
/// • SelectionFollowsFocus (cpp:3408-3411): keyboard focus moves select the focused item.
/// • Settings item: an auto-generated footer item with the Setting symbol (E713) named "Settings"
///   (cpp:1318-1349 CreateAndHookEventsToSettings; appended to the footer source, cpp:868-874). Opt-in here via
///   <see cref="IsSettingsVisible"/> (engine default FALSE — WinUI defaults true, but existing apps author their own
///   settings footer item; enabling both would double it).
/// • Item ramp (NavigationView_themeresources.xaml:9-34): rest=SubtleFillColorTransparent, hover=Secondary,
///   pressed=Tertiary, selected=Secondary, selectedHover=Tertiary, selectedPressed=Secondary; foreground =
///   TextFillColorPrimary in every state EXCEPT Pressed/SelectedPressed → TextFillColorSecondary (:21-34).
/// • Overlay pane background = NavigationViewDefaultPaneBackground = AcrylicInAppFillColorDefaultBrush (:5);
///   the always-visible Expanded pane = SolidBackgroundFillColorTransparent (:6) so the window Mica shows through.
/// • Selection indicator 3×16 @ r2 (:220-222), AccentFillColorDefault (:48), slid by the
///   <see cref="MotionSprings.NavPill"/> spring. Top mode uses the horizontal variant (16×3 under the item).
/// • Pane toggle: 40×36 (PaneToggleButtonWidth/Height :205-206) with the static E700 hamburger — the shipped
///   template's FALLBACK glyph for the AnimatedIcon source (PaneToggleButtonStyle FontIcon); the engine renders the
///   fallback path (press-scale affordance via AnimatedIcon.Glyph), it does not rotate (neither does WinUI's).
/// </summary>
public sealed class NavigationView : Component
{
    // Template parts (the WinUI x:Name vocabulary; see TemplateParts). Each part's doc lists the props the control
    // OWNS (re-asserted after any modifier — a Parts customization cannot win those).
    /// <summary>The left pane column — both the expanded/overlay pane AND the compact icon rail. Owned: Width (the
    /// mode/collapse-driven pane width), ClipToBounds + Animate (the presented-width Reveal) on the full pane,
    /// OnKeyDown on the compact rail, Children.</summary>
    public const string PartPane = "Pane";
    /// <summary>The pane title row (back + hamburger + title). Owned: Children.</summary>
    public const string PartPaneHeader = "PaneHeader";
    /// <summary>The page/content frame beside the pane. Owned: Children, Animate, ClipToBounds.</summary>
    public const string PartContentFrame = "ContentFrame";
    /// <summary>The sliding 3×16 selection pill (WinUI SelectionIndicator). Owned: Key (the NavPill reconcile
    /// identity). The slide spring (<see cref="MotionSprings.NavPill"/>) and opacity fade are HOOK-driven AnimEngine
    /// channels — a modifier cannot clobber them; do not add a Position-channel Animate here.</summary>
    public const string PartIndicator = "Indicator";
    /// <summary>A left-pane item row (WinUI NavigationViewItem). Owned: Key, Role, Focusable, OnClick (the
    /// select/expand activate), Animate (the hierarchy-reflow spec), OnRealized (row capture, chained), Children.
    /// NOTE: pane items are NOT virtualized, but the modifier runs once per item per render — keep it cheap.</summary>
    public const string PartItem = "Item";
    /// <summary>An item's icon column (icon + compact InfoBadge overlay). Owned: Children.</summary>
    public const string PartItemIcon = "ItemIcon";
    /// <summary>An item's label wrapper (the slide+fade enter/exit host). Owned: Animate (the label transition), Children.</summary>
    public const string PartItemLabel = "ItemLabel";
    /// <summary>The auto settings footer row (<see cref="IsSettingsVisible"/>) — applied AFTER <see cref="PartItem"/>
    /// on the same row; same owned props.</summary>
    public const string PartSettingsItem = "SettingsItem";

    public NavItem[] Items = [];
    public NavItem[] Footer = [];
    public string Initial = "";
    public Action<string>? OnSelect;
    public Func<string, Element>? Content;
    public string? Header;
    public bool ShowBackButton;
    public Action? OnBack;
    /// <summary>WinUI <c>IsPaneToggleButtonVisible</c>: false hides the pane's own hamburger — the WinUI-gallery
    /// shape, where the <see cref="TitleBar"/> owns the toggle and drives it via <see cref="PaneToggleRequest"/>.</summary>
    public bool ShowPaneToggle = true;
    /// <summary>External pane-toggle seam (the titlebar hamburger): BUMP the value (+1) to request one toggle — the
    /// same mode-aware action as the internal hamburger (expanded↔rail inline; open↔close the overlay otherwise).</summary>
    public Signal<int>? PaneToggleRequest;
    /// <summary>External navigate seam (the titlebar search commit): set a nav-item key to select it (collapsed
    /// ancestor groups expand to reveal it). Ignored when empty or unknown.</summary>
    public Signal<string>? NavigateRequest;
    /// <summary>WinUI <c>PaneDisplayMode</c>: Auto (adaptive), forced Left modes, or Top.</summary>
    public NavPaneDisplayMode PaneDisplayMode = NavPaneDisplayMode.Auto;
    /// <summary>Extra padding inside the content frame. Apps with custom chrome use this to line content up under the toolbar.</summary>
    public Edges4 ContentPadding;
    /// <summary>WinUI <c>SelectionFollowsFocus</c> (cpp:3408-3411): arrow-key focus selects.</summary>
    public bool SelectionFollowsFocus;
    /// <summary>Append the WinUI auto settings footer item (gear E713, key "settings"). See the class doc.</summary>
    public bool IsSettingsVisible;
    public string SettingsLabel = "Settings";
    /// <summary>The AutoSuggestBox slot (WinUI AutoSuggestArea): rendered under the pane title row when the pane is
    /// open; the compact rail shows a search button that opens the pane (the PaneAutoSuggestButton behavior).</summary>
    public Element? AutoSuggest;
    /// <summary>Cancelable PaneClosing (cpp:1708-1714): set <c>args.Cancel</c> to veto a pane close.</summary>
    public Action<NavPaneClosingArgs>? PaneClosing;
    /// <summary>WinUI <c>DisplayModeChanged</c> — fired when the resolved adaptive mode transitions.</summary>
    public Action<PaneMode>? DisplayModeChanged;
    /// <summary>Lightweight per-part styling (CSS ::part): modifiers keyed by the <c>PartXxx</c> consts; see
    /// <see cref="TemplateParts"/> for the contract. Top-mode bar items are not part-routed (different structure).</summary>
    public TemplateParts? Parts;

    /// <summary>Ambient navigate action for descendants that need to drive selection without prop threading.</summary>
    public static readonly Context<Action<string>> Nav = new(static _ => { });

    // The sliding selection pill's target (pane-local): Y = the selected row's indicator centre line; X = the depth
    // indentation (WinUI's SelectionIndicator sits INSIDE the indented PresenterContentRootGrid,
    // NavigationView_themeresources.xaml:600-603 — a depth-d item's pill rides at 4 + 31*d). (-1000,-1000) = hidden.
    internal static readonly Context<Point2> IndicatorTarget = new(new Point2(-1000f, -1000f));

    // Labels slide+fade on enter/exit; their position rides the parent item's projection. A label is a plain child —
    // present when expanded, removed (→ exit orphan that fades out) when collapsed. No wrapper component, no context.
    // Labels fade IN on expand; on collapse they are removed with the layout (no exit orphan) so no text ever lingers
    // over the rail/content. The pane reveal + content slide + staying icons carry the collapse motion.
    static readonly LayoutTransition LabelTransition = new(
        TransitionChannels.Opacity,
        TransitionDynamics.Spring(0.16f, 1f), SizeMode.Reveal,
        Enter: new EnterExit(Dx: -8f, Opacity: 0f, Active: true));

    // Whole-cell list reflow: existing rows below an expanded/collapsed group move as one unit (icon + label +
    // chevron + selection/background), with a small engine-authored stagger. No Size channel: row heights/layout snap
    // structurally; the compositor only carries the cells from their old Y to the new Y.
    static LayoutTransition ItemReflowTransition(int visualIndex) => new(
        TransitionChannels.Position,
        TransitionDynamics.Spring(0.18f, 1f),
        SizeMode.Reveal,
        Enter: new EnterExit(Dx: -8f, Opacity: 0f, Active: true),
        DelayMs: MathF.Min(visualIndex * 8f, 48f));

    // The pane's own width animates as a presented-size Reveal (translate + clip, no relayout); items reveal their
    // background; the content frame slides via the position projection as the pane width changes.
    static readonly LayoutTransition PaneTransition =
        LayoutTransition.BoundsT(SizeMode.Reveal) with { Dynamics = TransitionDynamics.Spring(0.22f, 0.9f) };

    // DIAG: last-reported width/mode so the pane-mode log only fires on change (see Render()).
    static float _diagWidth = float.NaN;
    static PaneMode _diagMode = (PaneMode)255;

    /// <summary>WinUI <c>ExpandedModeThresholdWidth</c> — width ≥ this ⇒ Expanded (default 1008, idl:179-181).</summary>
    public float ExpandedModeThresholdWidth = 1008f;
    /// <summary>WinUI <c>CompactModeThresholdWidth</c> — 0 &lt; width &lt; this ⇒ Minimal (default 641, idl:176-178).</summary>
    public float CompactModeThresholdWidth = 641f;
    public float PaneWidth = 320f;
    public float CompactWidth = 48f;

    const float TopPaneHeight = 48f;      // NavigationViewTopPaneHeight (themeresources:210)
    const float PaneHeaderRowHeight = 40f;
    const float PaneToggleWidth = 40f;    // PaneToggleButtonWidth (:206)
    const float PaneToggleHeight = 36f;   // PaneToggleButtonHeight (:205)
    const float ItemHeight = 36f;         // NavigationViewItemOnLeftMinHeight (:217)
    const float ItemOuterHeight = 36f;    // matches ItemHeight
    const float ItemMarginX = 4f;
    const float ItemMarginY = 0f;
    const float HeaderHeight = 36f;
    const float SeparatorRowHeight = 8f;  // 1px rule + the 0,3,0,4 margin (:247/:223)
    const float IconColumnWidth = 40f;
    const float IconSize = 16f;
    const float IndicatorW = 3f;          // NavigationViewSelectionIndicatorWidth (:220)
    const float IndicatorH = 16f;         // NavigationViewSelectionIndicatorHeight (:221)
    const float OverflowButtonSize = 40f; // TopNavigationViewOverflowButtonWidth/Height (:213-214)

    static readonly CornerRadius4 PaneOverlayCorners = new(0f, Radii.Overlay, Radii.Overlay, 0f);
    static readonly CornerRadius4 ContentLeftTopCorner = new(Radii.Overlay, 0f, 0f, 0f);
    static readonly ColorF LightDismissOverlay = ColorF.FromRgba(0, 0, 0, 0x1A);

    public override Element Render()
    {
        var hooks = UseContext(InputHooks.Current);
        var (selected, setSelected) = UseState(Initial.Length > 0 ? Initial : (Items.Length > 0 ? FirstSelectable() : ""));
        var (paneOpen, setPaneOpen) = UseState(false);
        var (collapsed, setCollapsed) = UseState(false);
        var (expanded, setExpanded) = UseState(SeedExpanded(Items));     // expanded parent keys (new array per toggle — value-eq gated)
        var rowHandles = UseMemo(static () => new Dictionary<string, NodeHandle>(), DepKey.Empty);
        var focusedKey = UseRef(selected);                               // keyboard cursor (real roving focus, no fill)
        var lastMode = UseRef((PaneMode)255);
        var overlayService = UseContext(Overlay.Service);                // top-mode overflow flyout host
        var overflowAnchor = UseRef(NodeHandle.Null);

        NavItem[] footerItems = IsSettingsVisible
            ? [.. Footer, new NavItem("settings", Icons.Settings, SettingsLabel) { IsSettings = true }]   // gear E713 (Icons.Settings)
            : Footer;

        float width = UseContext(Viewport.Size).Width;
        // WinUI mode resolution (cpp:1448-1473): forced modes win; Auto adapts by the two thresholds.
        PaneMode autoMode = PaneDisplayMode switch
        {
            NavPaneDisplayMode.Left => PaneMode.Expanded,
            NavPaneDisplayMode.LeftCompact => PaneMode.Compact,
            NavPaneDisplayMode.LeftMinimal => PaneMode.Minimal,
            _ => width <= 0f || width >= ExpandedModeThresholdWidth ? PaneMode.Expanded
               : width > 0f && width < CompactModeThresholdWidth ? PaneMode.Minimal
               : PaneMode.Compact,
        };
        // The hamburger manually collapses the full-width pane to the icon rail (WinUI title-bar toggle).
        PaneMode mode = autoMode == PaneMode.Expanded && collapsed ? PaneMode.Compact : autoMode;
        float openPaneWidth = width > 0f ? MathF.Min(PaneWidth, width) : PaneWidth;
        bool inlinePane = autoMode == PaneMode.Expanded;
        bool lightDismissible = !inlinePane;   // IsLightDismissible (cpp:1841-1849): overlay modes only
        float paneWidth = inlinePane ? (collapsed ? CompactWidth : openPaneWidth)
                        : paneOpen ? openPaneWidth
                        : autoMode == PaneMode.Compact ? CompactWidth : 0f;
        bool labelsVisible = !inlinePane || !collapsed;

        // Adaptive transition INTO Compact/Minimal auto-closes the pane (cpp:1477-1483) + DisplayModeChanged.
        if (lastMode.Value != mode)
        {
            bool first = lastMode.Value == (PaneMode)255;
            lastMode.Value = mode;
            if (!first)
            {
                if (mode != PaneMode.Expanded && paneOpen) setPaneOpen(false);
                DisplayModeChanged?.Invoke(mode);
            }
        }

        // DIAG: report the pane-mode decision whenever the viewport width or resulting mode changes.
        if (Diag.Enabled && (width != _diagWidth || mode != _diagMode))
        {
            _diagWidth = width; _diagMode = mode;
            Diag.Event("NavView", $"viewport.width={width:0.#} autoMode={autoMode} mode={mode} collapsed={collapsed} paneOpen={paneOpen} items={Items.Length} paneW={openPaneWidth:0.#}");
        }

        // Cancelable pane close (PaneClosing — cpp:1708-1714).
        bool TryClosePane()
        {
            if (PaneClosing is not null)
            {
                var args = new NavPaneClosingArgs();
                PaneClosing(args);
                if (args.Cancel) return false;
            }
            setPaneOpen(false);
            return true;
        }

        void Select(string key, bool hasChildren = false)
        {
            setSelected(key);
            focusedKey.Value = key;
            // ClosePaneIfNeccessaryAfterItemIsClicked (cpp:4838-4845): pane open ∧ mode ≠ Expanded ∧ no children.
            if (paneOpen && !inlinePane && !hasChildren) TryClosePane();
            OnSelect?.Invoke(key);
        }

        void ToggleExpand(string key)
            => setExpanded(expanded.Contains(key) ? expanded.Where(k => k != key).ToArray() : expanded.Append(key).ToArray());

        // Click/activate a row: a group navigates to its own (overview) page AND toggles its subtree; a leaf just navigates.
        // (WinUI: a parent with content selects + expands.)
        void Activate(NavItem it)
        {
            focusedKey.Value = it.Key;
            if (it.IsExpandable) { ToggleExpand(it.Key); Select(it.Key, hasChildren: true); }
            else Select(it.Key);
        }

        void ActivateRail(NavItem it)
        {
            focusedKey.Value = it.Key;
            Select(it.Key, hasChildren: it.IsExpandable);
        }

        // The visible, selectable rows in display order (headers/separators excluded) — the keyboard cursor's track.
        var flat = Flatten(Items, expanded);
        var paneFlat = labelsVisible ? flat : RootRows(Items);
        string paneSelected = DisplaySelectionKey(Items, paneFlat, selected);
        var keyboardFlat = labelsVisible || (paneOpen && !inlinePane) ? flat : paneFlat;
        var rows = keyboardFlat.Where(r => !r.Item.IsHeader && !r.Item.IsSeparator).Select(r => r.Item)
                               .Concat(footerItems.Where(f => !f.IsHeader && !f.IsSeparator))
                               .ToArray();

        void FocusRowKey(string key)
        {
            focusedKey.Value = key;
            if (rowHandles.TryGetValue(key, out var h) && Context.Scene is { } s && s.IsLive(h))
                hooks.FocusNode?.Invoke(h, true);
            if (SelectionFollowsFocus)   // cpp:3408-3411 — keyboard focus selects (without toggling groups)
            {
                setSelected(key);
                OnSelect?.Invoke(key);
            }
        }

        void HandleNavKey(KeyEventArgs e)
        {
            // Alt+Left closes a light-dismissible pane (cpp:3127-3136).
            if (e.KeyCode == Keys.Left && e.Alt)
            {
                if (paneOpen && lightDismissible) { TryClosePane(); e.Handled = true; }
                return;
            }
            int idx = Array.FindIndex(rows, r => r.Key == focusedKey.Value);
            bool top = PaneDisplayMode == NavPaneDisplayMode.Top;   // top mode: Left/Right move along the bar
            switch (e.KeyCode)
            {
                case Keys.Down when !top:
                case Keys.Right when top:
                    if (rows.Length > 0) { FocusRowKey(rows[Math.Min(idx < 0 ? 0 : idx + 1, rows.Length - 1)].Key); e.Handled = true; }
                    break;
                case Keys.Up when !top:
                case Keys.Left when top:
                    if (rows.Length > 0) { FocusRowKey(rows[Math.Max(idx < 0 ? 0 : idx - 1, 0)].Key); e.Handled = true; }
                    break;
                case Keys.Home:   // KeyboardFocusFirstItemFromItem (cpp:2755-2757)
                    if (rows.Length > 0) { FocusRowKey(rows[0].Key); e.Handled = true; }
                    break;
                case Keys.End:    // KeyboardFocusLastItemFromItem (cpp:2759-2761)
                    if (rows.Length > 0) { FocusRowKey(rows[^1].Key); e.Handled = true; }
                    break;
                case Keys.Right:
                    if (idx >= 0 && rows[idx].IsExpandable)
                    {
                        if (!expanded.Contains(rows[idx].Key)) ToggleExpand(rows[idx].Key);
                        else FocusRowKey(rows[idx].Children![0].Key);
                        e.Handled = true;
                    }
                    break;
                case Keys.Left:
                    if (idx >= 0 && rows[idx].IsExpandable && expanded.Contains(rows[idx].Key)) { ToggleExpand(rows[idx].Key); e.Handled = true; }
                    else { var p = ParentOf(Items, focusedKey.Value); if (p is not null) { FocusRowKey(p.Key); e.Handled = true; } }
                    break;
                // Enter/Space activate via the dispatcher's focused-clickable activation (rows are clickable).
            }
        }

        // Hamburger action by mode: at full width it collapses to the rail; from a manually-collapsed rail it expands
        // back; in adaptive compact/minimal it toggles the overlay flyout.
        Action toggle = autoMode == PaneMode.Expanded
            ? () => setCollapsed(!collapsed)
            : () => { if (paneOpen) TryClosePane(); else setPaneOpen(true); };

        // External chrome seams (the custom TitleBar): reading subscribes this component; each effect re-runs only
        // when its request value changes and applies the SAME internal action (state writes happen post-present).
        int paneToggleReq = PaneToggleRequest?.Value ?? 0;
        UseEffect(() => { if (paneToggleReq != 0) toggle(); }, paneToggleReq);
        string navigateReq = NavigateRequest?.Value ?? "";
        UseEffect(() =>
        {
            if (navigateReq.Length == 0 || navigateReq == selected) return;
            if (FindItem(Items, navigateReq) is not { } target) return;
            // Reveal the target: expand any collapsed ancestor groups, then select (the WinUI search-commit path).
            string[] reveal = expanded;
            for (NavItem? p = ParentOf(Items, target.Key); p is not null; p = ParentOf(Items, p.Key))
                if (!reveal.Contains(p.Key)) reveal = [.. reveal, p.Key];
            if (!ReferenceEquals(reveal, expanded)) setExpanded(reveal);
            Select(target.Key, hasChildren: target.IsExpandable);
        }, navigateReq);

        Action<NodeHandle> CaptureRow(string key) => h => rowHandles[key] = h;

        if (PaneDisplayMode == NavPaneDisplayMode.Top)
            return TopLayer(width, selected, Activate, Select, footerItems, CaptureRow, HandleNavKey, overlayService, overflowAnchor);

        var content = ContentFrame(selected, Select, mode);
        Action<NavItem> paneActivate = labelsVisible ? Activate : ActivateRail;
        Element baseLayer = inlinePane ? new BoxEl
        {
            Direction = 0,
            Grow = 1,
            Children = [FullPane(paneWidth, paneFlat, footerItems, paneSelected, expanded, paneActivate, HandleNavKey, toggle, CaptureRow, overlay: false, labelsVisible: labelsVisible), content],
        } : mode switch
        {
            PaneMode.Compact => new BoxEl
            {
                Direction = 0,
                Grow = 1,
                Children = [CompactPane(footerItems, selected, Select, toggle, HandleNavKey, CaptureRow), content],
            },
            _ => new BoxEl
            {
                Direction = 1,
                Grow = 1,
                Children = [TopBar(toggle), content],
            },
        };

        if (inlinePane || !paneOpen)
            return baseLayer;

        return Ui.ZStack(
            baseLayer,
            new BoxEl { Fill = LightDismissOverlay, Opacity = 1f, OnClick = () => TryClosePane() },
            FullPane(openPaneWidth, flat, footerItems, DisplaySelectionKey(Items, flat, selected), expanded, Activate, HandleNavKey, () => TryClosePane(), CaptureRow, overlay: true, labelsVisible: true)
        ) with { Grow = 1f };
    }

    // ── tree helpers ──────────────────────────────────────────────────────────────
    /// <summary>Depth-first key lookup over the full item tree (incl. children of collapsed groups).</summary>
    static NavItem? FindItem(NavItem[] items, string key)
    {
        foreach (var it in items)
        {
            if (it.Key == key) return it;
            if (it.Children is { Length: > 0 } kids && FindItem(kids, key) is { } found) return found;
        }
        return null;
    }

    static string[] SeedExpanded(NavItem[] items)
    {
        var seed = new List<string>();
        void Walk(NavItem[] xs)
        {
            foreach (var it in xs)
            {
                if (it.IsExpandable && it.InitiallyExpanded) seed.Add(it.Key);
                if (it.Children is { Length: > 0 } ch) Walk(ch);
            }
        }
        Walk(items);
        return seed.ToArray();
    }

    /// <summary>The visible rows (item + depth) in display order: a group's children follow it only while expanded.</summary>
    static List<(NavItem Item, int Depth)> Flatten(NavItem[] items, string[] expanded)
    {
        var list = new List<(NavItem, int)>(items.Length + 8);
        void Walk(NavItem[] xs, int depth)
        {
            foreach (var it in xs)
            {
                list.Add((it, depth));
                if (it.IsExpandable && expanded.Contains(it.Key)) Walk(it.Children!, depth + 1);
            }
        }
        Walk(items, 0);
        return list;
    }

    /// <summary>Compact/closed-left rail rows: top-level items only. Expanded state is preserved for when the pane
    /// reopens, but children are never emitted inline while labels are hidden.</summary>
    static List<(NavItem Item, int Depth)> RootRows(NavItem[] items)
    {
        var list = new List<(NavItem, int)>(items.Length);
        foreach (var it in items) list.Add((it, 0));
        return list;
    }

    /// <summary>Selection chrome follows the selected item when visible; if the selected item is hidden under a
    /// collapsed parent or an icon-only rail, show the indicator on the lowest visible ancestor.</summary>
    static string DisplaySelectionKey(NavItem[] roots, List<(NavItem Item, int Depth)> visible, string selected)
    {
        if (IsVisible(selected)) return selected;

        string? mapped = null;
        bool Walk(NavItem[] items, string? visibleAncestor)
        {
            foreach (var it in items)
            {
                string? here = IsVisible(it.Key) ? it.Key : visibleAncestor;
                if (it.Key == selected) { mapped = here; return true; }
                if (it.Children is { Length: > 0 } ch && Walk(ch, here)) return true;
            }
            return false;
        }

        Walk(roots, null);
        return mapped ?? selected;

        bool IsVisible(string key)
        {
            foreach (var (it, _) in visible)
                if (it.Key == key) return true;
            return false;
        }
    }

    static NavItem? ParentOf(NavItem[] items, string childKey)
    {
        foreach (var it in items)
        {
            if (it.Children is { Length: > 0 } ch)
            {
                foreach (var c in ch) if (c.Key == childKey) return it;
                var deep = ParentOf(ch, childKey);
                if (deep is not null) return deep;
            }
        }
        return null;
    }

    string FirstSelectable()
    {
        foreach (var it in Items)
            if (!it.IsHeader && !it.IsSeparator)
                return it.Key;

        return Items.Length > 0 ? Items[0].Key : "";
    }

    Element ContentFrame(string selected, Action<string, bool> select, PaneMode mode)
    {
        Action<string> nav = key => select(key, false);
        var child = Ctx.Provide(Nav, nav, Content?.Invoke(selected) ?? new BoxEl());
        var frame = new BoxEl
        {
            Direction = 1,
            Grow = 1,
            Shrink = 1,
            // Slide the content frame as the pane width changes (the dominant collapse motion) — position projection,
            // compositor-only, no relayout of the (potentially huge) page subtree.
            Animate = LayoutTransition.Slide with { Dynamics = TransitionDynamics.Spring(0.22f, 0.9f) },
            Fill = Tok.FillLayerDefault,
            BorderColor = Tok.StrokeCardDefault,
            BorderWidth = 1f,
            Corners = mode == PaneMode.Minimal ? default : ContentLeftTopCorner,
            Padding = ContentPadding,
            // Clip the page subtree to the 8,0,0,0 corner (WinUI's ContentGrid is a Grid+CornerRadius, which clips its
            // content). Without this the corner renders square and an opaque page background overdraws the 1px stroke.
            ClipToBounds = true,
            Children = [child],
        };
        var applied = Parts.Apply(PartContentFrame, frame);
        return applied with
        {
            Children = frame.Children,
            Animate = frame.Animate,
            ClipToBounds = true,
        };
    }

    Element FullPane(float width, List<(NavItem Item, int Depth)> flat, NavItem[] footerItems, string selected, string[] expanded,
                     Action<NavItem> activate, Action<KeyEventArgs> handleNavKey, Action toggle,
                     Func<string, Action<NodeHandle>> captureRow, bool overlay, bool labelsVisible)
    {
        var mainItems = BuildItems(flat, selected, expanded, activate, expandedLayout: labelsVisible, ownIndicator: false, labelsVisible, captureRow, Parts);
        var footerRows = BuildItems(footerItems.Select(f => (f, 0)).ToList(), selected, expanded, activate, expandedLayout: labelsVisible, ownIndicator: true, labelsVisible, captureRow, Parts);
        // Pane background, per the shipped WinUI generic.xaml: the EXPANDED (always-visible) pane =
        // NavigationViewExpandedPaneBackground = SolidBackgroundFillColorTransparent → FULLY TRANSPARENT, so DWM's Mica
        // window backdrop shows through. Only the transient OVERLAY (minimal/compact flyout) pane uses in-app acrylic
        // (NavigationViewDefaultPaneBackground = AcrylicInAppFillColorDefaultBrush, themeresources:5) — engine acrylic
        // samples the app canvas, which is correct over content but would override window transparency (kill Mica) if
        // used on the always-visible pane.
        var paneFill = ColorF.Transparent;

        var paneChildren = new List<Element>
        {
            PaneTitleRow(toggle, labelsVisible),
        };

        // AutoSuggestBox slot (WinUI AutoSuggestArea): the field while the pane shows labels; the compact rail path
        // renders the search button instead (see CompactPane).
        if (AutoSuggest is not null && labelsVisible)
        {
            paneChildren.Add(new BoxEl
            {
                Padding = new Edges4(ItemMarginX + 12f, 0, ItemMarginX + 12f, 4f),
                Animate = LabelTransition,
                Children = [AutoSuggest],
            });
        }

        // Menu items scroll (overflow → scrollbar on hover); the footer stays pinned at the bottom. Keys bubble from
        // the focused row to this container's handler (rows are the tab stops — WinUI items are focusable).
        paneChildren.Add(Ui.ScrollView(Ui.ZStack(
            new BoxEl { Direction = 1, OnKeyDown = handleNavKey, Children = mainItems },
            Ctx.Provide(IndicatorTarget, SelectedPos(flat, selected, labelsVisible), Embed.Comp(() => new NavIndicator { Parts = Parts }))
        )));
        paneChildren.Add(new BoxEl { Direction = 1, OnKeyDown = handleNavKey, Children = footerRows });
        paneChildren.Add(new BoxEl { Height = 4 });

        var paneKids = paneChildren.ToArray();
        var pane = new BoxEl
        {
            Width = width,
            Direction = 1,
            Fill = paneFill,
            ClipToBounds = true,
            Animate = PaneTransition,             // presented-width Reveal (model snaps to the final width; no relayout)
            // Overlay pane = the theme-aware WinUI in-app acrylic (NavigationViewDefaultPaneBackground, :5).
            Acrylic = overlay ? Tok.AcrylicFlyout : null,
            // Only the floating OVERLAY pane (minimal-mode flyout) gets a border. WinUI's always-visible expanded pane
            // (NavigationViewExpandedPaneBackground = transparent) is borderless — the seam is the content grid's own
            // left/top stroke + its rounded corner. A pane border here draws a square-cornered light line over that corner.
            BorderColor = overlay ? Tok.StrokeDividerDefault : ColorF.Transparent,
            BorderWidth = overlay ? 1f : 0f,
            Corners = overlay ? PaneOverlayCorners : default,
            Shadow = overlay ? Elevation.Flyout : null,
            Children = paneKids,
        };

        // Intent lives ON the node (BoxEl.Animate) — no wrapper component, no context round-trip. Parts: restyle
        // anything (acrylic, border, corners…); the mode-driven width + reveal mechanics always win.
        return Parts.Apply(PartPane, pane) with { Width = width, ClipToBounds = true, Animate = PaneTransition, Children = paneKids };
    }

    Element CompactPane(NavItem[] footerItems, string selected, Action<string, bool> select, Action toggle,
                        Action<KeyEventArgs> handleNavKey, Func<string, Action<NodeHandle>> captureRow)
    {
        // Compact/Minimal panes show TOP-LEVEL rows only (groups don't expand inline). A selected child maps its
        // selection chrome to the collapsed parent; children are reachable once the pane is opened.
        var compactFlat = RootRows(Items);
        string compactSelected = DisplaySelectionKey(Items, compactFlat, selected);
        var noneExpanded = Array.Empty<string>();
        void CompactActivate(NavItem it)
        {
            select(it.Key, it.IsExpandable);
        }

        var children = new List<Element>();
        if (ShowPaneToggle) children.Add(PaneToggleButton(toggle));
        children.Add(new BoxEl { Height = 4 });

        // The AutoSuggest slot collapses to the search button that opens the pane (WinUI PaneAutoSuggestButton).
        if (AutoSuggest is not null)
            children.Add(PaneGlyphButton(Icons.Search, toggle));

        foreach (var (it, _) in compactFlat)
            children.Add(it.IsHeader ? new BoxEl { Height = 8 }
                       : it.IsSeparator ? SeparatorRow(expandedLayout: false)
                       : Item(it, 0, children.Count, compactSelected, noneExpanded, CompactActivate, expandedLayout: false, ownIndicator: true, labelsVisible: false, captureRow(it.Key), Parts));

        children.Add(new BoxEl { Grow = 1 });

        foreach (var it in footerItems)
            children.Add(it.IsHeader ? new BoxEl { Height = 8 }
                       : it.IsSeparator ? SeparatorRow(expandedLayout: false)
                       : Item(it, 0, children.Count, selected, noneExpanded, CompactActivate, expandedLayout: false, ownIndicator: true, labelsVisible: false, captureRow(it.Key), Parts));

        var railKids = children.ToArray();
        var rail = new BoxEl
        {
            Width = CompactWidth,
            Direction = 1,
            Fill = Tok.AcrylicBase,                    // translucent over Mica (see FullPane note)
            BorderColor = Tok.StrokeDividerDefault,
            BorderWidth = 1f,
            AlignItems = FlexAlign.Center,
            OnKeyDown = handleNavKey,
            Children = railKids,
        };
        // The same PartPane door as the full pane: the rail IS the pane in compact mode.
        return Parts.Apply(PartPane, rail) with { Width = CompactWidth, OnKeyDown = handleNavKey, Children = railKids };
    }

    Element TopBar(Action toggle) => new BoxEl
    {
        Direction = 0,
        Height = TopPaneHeight,
        AlignItems = FlexAlign.Center,
        Fill = Tok.AcrylicBase,
        BorderColor = Tok.StrokeDividerDefault,
        BorderWidth = 1f,
        Children =
        [
            .. ShowPaneToggle ? (Element[])[PaneToggleButton(toggle)] : [],
            Header is null
                ? new BoxEl { Grow = 1 }
                : new BoxEl
                {
                    Direction = 0,
                    Grow = 1,
                    AlignItems = FlexAlign.Center,
                    Padding = new Edges4(4, 0, 16, 0),
                    Children = [new TextEl(Header) { Size = 14f, Bold = true, Color = Tok.TextPrimary }],
                },
        ],
    };

    // ── TOP display mode: horizontal items + overflow flyout + footer/settings at the right ────────────
    Element TopLayer(float width, string selected, Action<NavItem> activate, Action<string, bool> select,
                     NavItem[] footerItems, Func<string, Action<NodeHandle>> captureRow, Action<KeyEventArgs> handleNavKey,
                     IOverlayService overlayService, Ref<NodeHandle> overflowAnchor)
    {
        // Width estimation for the overflow split (the engine has no per-item measure at render time; the WinUI
        // TopNavigationViewLayoutState math is replaced by a deterministic estimate: paddings 12+12, icon 16+8, ~7px/char).
        float reserved = (ShowBackButton ? PaneToggleWidth : 0f)
                       + (AutoSuggest is not null ? 220f : 0f)
                       + footerItems.Count(f => !f.IsHeader && !f.IsSeparator) * PaneToggleWidth
                       + OverflowButtonSize + 16f;
        float avail = width > 0f ? width - reserved : float.MaxValue;
        var visible = new List<NavItem>();
        var overflow = new List<NavItem>();
        float used = 0f;
        foreach (var it in Items)
        {
            if (it.IsHeader) continue;   // top mode shows no section headers (WinUI top nav drops them)
            if (it.IsSeparator) { (overflow.Count > 0 ? overflow : visible).Add(it); continue; }
            float w = 24f + (it.Glyph.Length > 0 ? 24f : 0f) + it.Label.Length * 7f;
            if (used + w <= avail && overflow.Count == 0) { visible.Add(it); used += w; }
            else overflow.Add(it);
        }

        void OpenOverflow()
        {
            var items = overflow.ToArray();
            overlayService.Open(() => overflowAnchor.Value, () => new BoxEl
            {
                Direction = 1,
                Padding = new Edges4(0, 4, 0, 4),
                MinWidth = 160f,
                Children = items.Where(o => !o.IsSeparator).Select(o => (Element)new BoxEl
                {
                    Direction = 0,
                    MinHeight = ItemHeight,
                    AlignItems = FlexAlign.Center,
                    Padding = new Edges4(12, 0, 12, 0),
                    Margin = new Edges4(4, 2, 4, 2),
                    Corners = Radii.ControlAll,
                    HoverFill = Tok.FillSubtleSecondary,
                    PressedFill = Tok.FillSubtleTertiary,
                    Role = AutomationRole.NavigationItem,
                    OnClick = () => { activate(o); overlayService.CloseTop(); },
                    Children =
                    [
                        o.Glyph.Length > 0 ? Ui.Icon(o.Glyph, IconSize, Tok.TextPrimary) : new BoxEl(),
                        new BoxEl { Width = 8 },
                        new TextEl(o.Label) { Size = 14f, Color = Tok.TextPrimary, PressedColor = Tok.TextSecondary },
                    ],
                }).ToArray(),
            }, FlyoutPlacement.BottomLeft);
        }

        var bar = new List<Element>(visible.Count + 6);
        if (ShowBackButton) bar.Add(PaneGlyphButton(Icons.Back, () => OnBack?.Invoke()));
        foreach (var it in visible)
        {
            if (it.IsSeparator)
            {
                // Top separator: 1px vertical rule (TopNavigationViewItemSeparatorWidth :224, margin 3,0,4,0 :249).
                bar.Add(new BoxEl { Width = 1f, Height = TopPaneHeight - 16f, Margin = new Edges4(3, 0, 4, 0), Fill = Tok.StrokeDividerDefault, AlignSelf = FlexAlign.Center });
                continue;
            }
            bar.Add(TopItem(it, it.Key == selected, () => activate(it), captureRow(it.Key)));
        }
        if (overflow.Any(o => !o.IsSeparator))
        {
            // TopNavOverflowButton "More" (NavigationView.xaml:239) — ellipsis E712, 40×40 (:213-214).
            bar.Add(new BoxEl
            {
                Width = OverflowButtonSize,
                Height = OverflowButtonSize,
                AlignItems = FlexAlign.Center,
                Justify = FlexJustify.Center,
                Corners = Radii.ControlAll,
                HoverFill = Tok.FillSubtleSecondary,
                PressedFill = Tok.FillSubtleTertiary,
                Role = AutomationRole.Button,
                OnRealized = h => overflowAnchor.Value = h,
                OnClick = OpenOverflow,
                Children = [Ui.Icon(Icons.More, IconSize, Tok.TextPrimary)],
            });
        }
        bar.Add(new BoxEl { Grow = 1f });
        if (AutoSuggest is not null)
            bar.Add(new BoxEl { Width = 216f, AlignSelf = FlexAlign.Center, Children = [AutoSuggest] });
        foreach (var f in footerItems)
        {
            if (f.IsHeader || f.IsSeparator) continue;
            // Top-mode settings/footer items are icon-only (settingsItem.Content(nullptr) — cpp:1340-1346).
            bar.Add(TopItem(f with { Label = "" }, f.Key == selected, () => activate(f), captureRow(f.Key)));
        }

        var content = ContentFrame(selected, select, PaneMode.Expanded);
        return new BoxEl
        {
            Direction = 1,
            Grow = 1f,
            Children =
            [
                new BoxEl
                {
                    Direction = 0,
                    Height = TopPaneHeight,            // NavigationViewTopPaneHeight 48 (:210)
                    AlignItems = FlexAlign.Center,
                    Fill = ColorF.Transparent,         // NavigationViewTopPaneBackground = SolidBackgroundFillColorTransparent (:7)
                    BorderColor = Tok.StrokeDividerDefault,
                    BorderWidth = 1f,
                    OnKeyDown = handleNavKey,
                    Children = bar.ToArray(),
                },
                content,
            ],
        };
    }

    /// <summary>A horizontal top-nav item: icon + label with the HORIZONTAL selection indicator (16×3) under it.</summary>
    Element TopItem(NavItem it, bool sel, Action onClick, Action<NodeHandle> capture)
    {
        var rowChildren = new List<Element>(3);
        if (it.IconContent is { } iconContent) rowChildren.Add(iconContent);
        else if (it.Glyph.Length > 0) rowChildren.Add(AnimatedIcon.Glyph(it.Glyph, IconSize, Tok.TextPrimary, Theme.IconFont));
        if (it.Label.Length > 0)
        {
            if (rowChildren.Count > 0) rowChildren.Add(new BoxEl { Width = 8 });
            rowChildren.Add(new TextEl(it.Label)
            {
                Size = 14f,
                Color = Tok.TextPrimary,                 // TopNavigationViewItemForeground (:50)
                PressedColor = Tok.TextSecondary,        // Pressed (:52)
            });
        }

        return new BoxEl
        {
            Key = it.Key,
            Direction = 1,
            AlignSelf = FlexAlign.Center,
            Role = AutomationRole.NavigationItem,
            Margin = new Edges4(ItemMarginX, 0, ItemMarginX, 0),
            Corners = Radii.ControlAll,
            HoverFill = Tok.FillSubtleSecondary,
            PressedFill = Tok.FillSubtleTertiary,
            Fill = sel ? Tok.FillSubtleSecondary : ColorF.Transparent,
            OnClick = onClick,
            OnRealized = capture,
            Children =
            [
                new BoxEl
                {
                    Direction = 0,
                    AlignItems = FlexAlign.Center,
                    MinHeight = OverflowButtonSize,
                    Padding = new Edges4(12, 0, 12, 0),  // TopNavigationViewItemContentPresenterMargin band (:253)
                    Children = rowChildren.ToArray(),
                },
                // Horizontal selection indicator: 16×3 @ r2 centred under the item (the top-mode pill).
                new BoxEl
                {
                    Direction = 0,
                    Justify = FlexJustify.Center,
                    Children =
                    [
                        new BoxEl
                        {
                            Width = IndicatorH,
                            Height = IndicatorW,
                            Corners = CornerRadius4.All(2f),
                            Fill = sel ? Tok.AccentDefault : ColorF.Transparent,
                            Margin = new Edges4(0, 0, 0, 2),
                        },
                    ],
                },
            ],
        };
    }

    Element PaneTitleRow(Action toggle, bool labelsVisible)
    {
        var children = new List<Element>();

        if (ShowBackButton)
            children.Add(PaneGlyphButton(Icons.Back, () => OnBack?.Invoke()));

        if (ShowPaneToggle)
            children.Add(PaneToggleButton(toggle));

        // WinUI hides the PaneTitle when the pane is collapsed (icon rail) — only emit it while labels show, the same
        // way Item() gates its own text label on expandedLayout. Otherwise it renders clipped ("fluent-") in the rail.
        if (labelsVisible && Header is { Length: > 0 } title)
            children.Add(AnimatedLabel(labelsVisible, new NavLabelSpec(
                new TextEl(title) { Size = 14f, Bold = true, Color = Tok.TextPrimary },
                PaneHeaderRowHeight, 1f, new Edges4(4, 0, 16, 0))));

        var rowKids = children.ToArray();
        // Nothing to show (e.g. the gallery puts the toggle in the titlebar and the title hides when collapsed) → reclaim
        // the header band instead of reserving an empty TopPaneHeight row that pushes the rail items down.
        if (rowKids.Length == 0)
            return new BoxEl { Height = 4 };
        var row = new BoxEl
        {
            Direction = 0,
            Height = TopPaneHeight,
            AlignItems = FlexAlign.Center,
            Children = rowKids,
        };
        return Parts.Apply(PartPaneHeader, row) with { Children = rowKids };
    }

    static Element PaneToggleButton(Action onClick) => PaneGlyphButton(Icons.Menu, onClick);

    static Element PaneGlyphButton(string glyph, Action onClick) => new BoxEl
    {
        Width = PaneToggleWidth,
        Height = PaneToggleHeight,
        Margin = new Edges4(ItemMarginX, ItemMarginY, ItemMarginX, ItemMarginY),
        Direction = 0,
        Role = AutomationRole.Button,
        AlignItems = FlexAlign.Center,
        Justify = FlexJustify.Center,
        Corners = Radii.ControlAll,
        HoverFill = Tok.FillSubtleSecondary,
        PressedFill = Tok.FillSubtleTertiary,
        OnClick = onClick,
        PressScale = 0.985f,
        Children = [AnimatedIcon.Glyph(glyph, IconSize, Tok.TextPrimary, Theme.IconFont)],
    };

    const float IndentStep = 31f;   // per-depth left indent for nested group children (expanded layout only) —
                                    // WinUI c_itemIndentation = 31 px/level (NavigationViewItemBase.h:63, applied
                                    // Depth() * c_itemIndentation, NavigationViewItem.cpp:895-902)

    static Element[] BuildItems(List<(NavItem Item, int Depth)> flat, string selected, string[] expanded,
                                Action<NavItem> activate, bool expandedLayout, bool ownIndicator, bool labelsVisible,
                                Func<string, Action<NodeHandle>> captureRow, TemplateParts? parts)
    {
        var result = new Element[flat.Count];
        for (int i = 0; i < flat.Count; i++)
            result[i] = flat[i].Item.IsSeparator
                ? SeparatorRow(expandedLayout, flat[i].Depth)
                : Item(flat[i].Item, flat[i].Depth, i, selected, expanded, activate, expandedLayout, ownIndicator, labelsVisible,
                       captureRow(flat[i].Item.Key), parts);

        return result;
    }

    /// <summary>NavigationViewItemSeparator: a 1px DividerStroke rule (height :223, foreground :46, margin 0,3,0,4 :247).
    /// Nested separators indent 31/level like items (NavigationViewItemSeparator.cpp:74-83 rootGrid Margin.Left).</summary>
    static Element SeparatorRow(bool expandedLayout, int depth = 0) => new BoxEl
    {
        Height = SeparatorRowHeight,
        Direction = 1,
        Justify = FlexJustify.Center,
        Padding = expandedLayout
            ? new Edges4(ItemMarginX + depth * IndentStep, 0, ItemMarginX, 0)
            : new Edges4(8, 0, 8, 0),
        Children = [new BoxEl { Height = 1f, Fill = Tok.StrokeDividerDefault }],
    };

    static Point2 SelectedPos(List<(NavItem Item, int Depth)> flat, string selected, bool labelsVisible)
    {
        float y = 0f;
        foreach (var (it, depth) in flat)
        {
            if (it.IsHeader)
            {
                y += labelsVisible ? HeaderHeight : 8f;
                continue;
            }
            if (it.IsSeparator)
            {
                y += SeparatorRowHeight;
                continue;
            }

            if (it.Key == selected)
                // X = the depth indentation (the pill lives inside WinUI's indented content grid — 31/level);
                // Y = the row's indicator centre line.
                return new Point2(depth * IndentStep, y + ItemMarginY + (ItemHeight - IndicatorH) * 0.5f);

            y += ItemOuterHeight;
        }

        return new Point2(-1000f, -1000f);
    }

    static Element Item(NavItem it, int depth, int visualIndex, string selected, string[] expanded,
                        Action<NavItem> activate, bool expandedLayout, bool ownIndicator, bool labelsVisible,
                        Action<NodeHandle> capture, TemplateParts? parts)
    {
        if (it.IsHeader)
            return HeaderItem(it, expandedLayout, labelsVisible, depth);

        bool sel = it.Key == selected;
        bool isExpanded = it.IsExpandable && expanded.Contains(it.Key);
        // NavigationViewItemForeground = TextFillColorPrimary in every state EXCEPT Pressed → TextFillColorSecondary
        // (NavigationView_themeresources.xaml:21-34) — the old engine TextSecondary-at-rest was a drift.
        var foreground = Tok.TextPrimary;

        // Depth indentation lands on the CONTENT, never the row: WinUI sets Depth()*31 as the left margin of the
        // presenter's content grid (NavigationViewItem.cpp:894-902 → NavigationViewItemPresenter.cpp:264-277
        // UpdateMargin) — the item plate (background/hover) stays full-row width at the pane edge, while the
        // SELECTION INDICATOR + icon + label all shift together (the indicator lives INSIDE that grid,
        // NavigationView_themeresources.xaml:600-603). Indenting the whole row (the old Margin approach) shrank the
        // plate and visibly detached the sliding pill from the row.
        float contentIndent = expandedLayout ? depth * IndentStep : 0f;

        Element iconVisual = it.IconContent ?? (it.Glyph.Length > 0
            ? AnimatedIcon.Glyph(it.Glyph, IconSize, foreground, Theme.IconFont)
            : new BoxEl { Width = IconSize, Height = IconSize });

        var iconCell = new BoxEl
        {
            Width = IconColumnWidth - IndicatorW,
            Height = ItemHeight,
            Direction = 0,
            AlignItems = FlexAlign.Center,
            Justify = FlexJustify.Center,
            Children = it.InfoBadge is not null && !expandedLayout
                ?
                [
                    // Compact rail: the badge overlays the icon's top-right corner (the WinUI compact InfoBadge).
                    new BoxEl
                    {
                        ZStack = true,
                        Children =
                        [
                            iconVisual,
                            new BoxEl { OffsetX = 10f, OffsetY = -6f, HitTestVisible = false, Children = [it.InfoBadge] },
                        ],
                    },
                ]
                : [iconVisual],
        };
        if (parts is not null)
            iconCell = parts.Apply(PartItemIcon, iconCell) with { Children = iconCell.Children };   // structure = icon + badge mount

        // Uniform structure across expanded/compact: an indicator sliver + the icon column are ALWAYS present (so the
        // keyed item node and its icon are reused, not remounted), plus the label ONLY when expanded. Collapsing removes
        // the label → it exits (fades + slides) while the icon stays put; the item's own background width reveals.
        var children = new List<Element>
        {
            new BoxEl
            {
                Width = IndicatorW,
                Height = IndicatorH,
                // The depth indent rides the indicator sliver (the first content-grid child): sliver + icon + label
                // shift 31/level together while the plate spans the full row (UpdateContentLeftIndentation).
                Margin = new Edges4(contentIndent, 0, 0, 0),
                Corners = CornerRadius4.All(2f),   // NavigationViewSelectionIndicatorRadius (:222)
                Fill = ownIndicator && sel ? Tok.AccentDefault : ColorF.Transparent,
                AlignSelf = FlexAlign.Center,
            },
            iconCell,
        };
        if (expandedLayout)
        {
            var label = AnimatedLabel(labelsVisible, new NavLabelSpec(
                new TextEl(it.Label)
                {
                    Size = 14f,
                    Color = foreground,
                    PressedColor = Tok.TextSecondary,
                    Grow = 1f,
                    Shrink = 1f,
                    Trim = TextTrim.CharacterEllipsis,
                    MaxLines = 1,
                },
                ItemHeight, 1f, new Edges4(4, 0, 8, 0)));
            if (parts is not null && label is BoxEl lb)
            {
                var m = parts.Apply(PartItemLabel, lb);
                label = m with { Animate = lb.Animate, Children = lb.Children };
            }
            children.Add(label);
            if (it.InfoBadge is not null)
                children.Add(new BoxEl
                {
                    AlignSelf = FlexAlign.Center,
                    Margin = new Edges4(0, 0, it.IsExpandable ? 0 : 12f, 0),
                    HitTestVisible = false,
                    Children = [it.InfoBadge],
                });
            if (it.IsExpandable)
                children.Add(ChevronCell(isExpanded));
        }

        return new BoxEl
        {
            Key = it.Key,                       // keyed → reused across expanded/compact (icon glides, only the label exits)
            Direction = 0,
            Role = AutomationRole.NavigationItem,
            Animate = ItemReflowTransition(visualIndex),   // hierarchy reflow animates the entire cell, not subparts
            Width = expandedLayout ? float.NaN : PaneToggleWidth,
            Height = ItemHeight,
            Margin = new Edges4(ItemMarginX, ItemMarginY, ItemMarginX, ItemMarginY),   // constant — depth indents the CONTENT (iconCell), not the row
            AlignItems = FlexAlign.Center,
            Corners = Radii.OverlayAll,   // WinUI nav items round to OverlayCornerRadius (8), not ControlCornerRadius (4)
            // Backplate ramp (themeresources:9-20): rest Transparent/Selected=Secondary; hover Secondary/SelectedPointerOver=Tertiary;
            // pressed Tertiary/SelectedPressed=Secondary. (The old focused-fill cue is gone — keyboard focus draws the
            // engine focus ring on the focused row instead.)
            Fill = sel ? Tok.FillSubtleSecondary : ColorF.Transparent,
            HoverFill = sel ? Tok.FillSubtleTertiary : Tok.FillSubtleSecondary,
            PressedFill = sel ? Tok.FillSubtleSecondary : Tok.FillSubtleTertiary,
            Focusable = true,
            OnClick = () => activate(it),
            OnRealized = capture,
            Children = children.ToArray(),
        };
    }

    // A group's expand/collapse chevron — glyph-swapped (ChevronRight ⇄ ChevronDown) on toggle, the FALLBACK shape of
    // WinUI's AnimatedChevronUpDownSmallVisualSource (the AnimatedIcon's FallbackIconSource is the static E70D glyph —
    // NavigationView_themeresources.xaml RotatedChevron block). (Glyph swap rather than a rotated child component,
    // since a reused child's props freeze at mount and wouldn't pick up the new expand state.)
    static Element ChevronCell(bool expanded) => new BoxEl
    {
        Width = 28f,
        Height = ItemHeight,
        AlignItems = FlexAlign.Center,
        Justify = FlexJustify.Center,
        Children = [new TextEl(expanded ? Icons.ChevronDown : Icons.ChevronRight) { Size = 8f, Color = Tok.TextSecondary, PressedColor = Tok.TextSecondary, FontFamily = Theme.IconFont }],
    };

    static Element HeaderItem(NavItem it, bool expandedLayout, bool labelsVisible, int depth = 0)
    {
        if (!expandedLayout)
            return new BoxEl { Height = 8 };

        // Nested headers indent 31/level like items (NavigationViewItemHeader.cpp:76-84 rootGrid Margin.Left).
        return AnimatedLabel(labelsVisible, new NavLabelSpec(
            new TextEl(it.Label) { Size = 14f, Bold = true, Color = Tok.TextSecondary },
            HeaderHeight, 0f, new Edges4(16 + depth * IndentStep, 0, 16, 0)));
    }

    // A pane label: a plain child whose appearance/disappearance is the general enter/exit animation. Intent lives on the
    // node (BoxEl.Animate) — no wrapper component, no context. The `visible` arg is kept for call-site compatibility.
    static Element AnimatedLabel(bool visible, NavLabelSpec spec) => new BoxEl
    {
        Direction = 0,
        Grow = spec.Grow,
        Shrink = spec.Grow > 0f ? 1f : 0f,
        Height = spec.Height,
        AlignItems = FlexAlign.Center,
        Padding = spec.Padding,
        Animate = LabelTransition,
        Children = [spec.Child],
    };
}

internal readonly record struct NavLabelSpec(Element Child, float Height, float Grow, Edges4 Padding);

internal sealed class NavIndicator : Component
{
    public TemplateParts? Parts;

    public override Element Render()
    {
        Point2 target = UseContext(NavigationView.IndicatorTarget);
        bool visible = target.Y > -500f;
        // The WinUI selection-indicator slide: the named NavPill composition-spring preset (MotionSprings.NavPill).
        // X rides the same spring — the pill glides into a child's 31/level indentation (it lives inside WinUI's
        // indented content grid, so depth changes move it horizontally too).
        UseSpring(AnimChannel.TranslateY, visible ? target.Y : 0f, MotionSprings.NavPill, visible ? MathF.Round(target.Y) : -1f);
        UseSpring(AnimChannel.TranslateX, visible ? target.X : 0f, MotionSprings.NavPill, visible ? MathF.Round(target.X) : -1f);
        UseTransition(AnimChannel.Opacity, visible ? 0f : 1f, visible ? 1f : 0f, 150f, Easing.EaseOut, visible);
        // Parts: pure styling — the slide/fade springs ride the component's own node regardless of the modifier.
        return Parts.Apply(NavigationView.PartIndicator, new BoxEl
        {
            Width = 3f,
            Height = 16f,
            Margin = new Edges4(4f, 0f, 0f, 0f),
            Corners = CornerRadius4.All(2f),
            Fill = Tok.AccentDefault,
            AlignSelf = FlexAlign.Start,
            // State-dependent RESTING opacity: the fade transition owns the channel while animating (phase-7 fold
            // wins the frame), but a settled track frees WITHOUT resetting Opacity — so any later re-render
            // re-asserted the default 1f and a hidden pill snapped visible. The static must equal the terminal.
            Opacity = visible ? 1f : 0f,
        });
    }
}
