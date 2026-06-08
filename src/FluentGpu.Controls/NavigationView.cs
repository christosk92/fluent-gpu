using System.Linq;
using FluentGpu.Animation;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;

namespace FluentGpu.Controls;

/// <summary>
/// A NavigationView pane entry: a stable key, an icon glyph, and a label. A parent group sets <see cref="Children"/>
/// (rendered as an expand/collapse subtree with a chevron + indent); <see cref="InitiallyExpanded"/> seeds it open.
/// The positional header is unchanged, so flat <c>new NavItem(key, glyph, label)</c> / <c>IsHeader</c> call sites keep working.
/// </summary>
public sealed record NavItem(string Key, string Glyph, string Label, bool IsHeader = false)
{
    public NavItem[]? Children { get; init; }
    public bool InitiallyExpanded { get; init; }
    internal bool IsExpandable => !IsHeader && Children is { Length: > 0 };
}

public enum PaneMode : byte { Expanded = 0, Compact = 1, Minimal = 2 }

/// <summary>
/// Adaptive left NavigationView modeled on WinUI's SplitView-backed template. It supports expanded, compact, and
/// minimal display modes, a WinUI-style pane toggle row, section headers, pinned footer items, and a sliding selection
/// indicator for the primary menu.
/// </summary>
public sealed class NavigationView : Component
{
    public NavItem[] Items = [];
    public NavItem[] Footer = [];
    public string Initial = "";
    public Action<string>? OnSelect;
    public Func<string, Element>? Content;
    public string? Header;
    public bool ShowBackButton;
    public Action? OnBack;

    /// <summary>Ambient navigate action for descendants that need to drive selection without prop threading.</summary>
    public static readonly Context<Action<string>> Nav = new(static _ => { });

    internal static readonly Context<float> IndicatorTarget = new(-1000f);

    // Labels slide+fade on enter/exit; their position rides the parent item's projection. A label is a plain child —
    // present when expanded, removed (→ exit orphan that fades out) when collapsed. No wrapper component, no context.
    // Labels fade IN on expand; on collapse they are removed with the layout (no exit orphan) so no text ever lingers
    // over the rail/content. The pane reveal + content slide + staying icons carry the collapse motion.
    static readonly LayoutTransition LabelTransition = new(
        TransitionChannels.Position | TransitionChannels.Opacity,
        TransitionDynamics.Spring(0.16f, 1f), SizeMode.Reveal,
        Enter: new EnterExit(Dx: -8f, Opacity: 0f, Active: true));

    // The pane's own width animates as a presented-size Reveal (translate + clip, no relayout); items reveal their
    // background; the content frame slides via the position projection as the pane width changes.
    static readonly LayoutTransition PaneTransition =
        LayoutTransition.BoundsT(SizeMode.Reveal) with { Dynamics = TransitionDynamics.Spring(0.22f, 0.9f) };

    // DIAG: last-reported width/mode so the pane-mode log only fires on change (see Render()).
    static float _diagWidth = float.NaN;
    static PaneMode _diagMode = (PaneMode)255;

    public float CompactThreshold = 1008f;
    public float MinimalThreshold = 641f;
    public float PaneWidth = 320f;
    public float CompactWidth = 48f;

    const float TopPaneHeight = 48f;
    const float PaneHeaderRowHeight = 40f;
    const float PaneToggleWidth = 40f;
    const float PaneToggleHeight = 36f;   // WinUI PaneToggleButtonHeight = 36 (was 40)
    const float ItemHeight = 36f;         // WinUI NavigationViewItemOnLeftMinHeight = 36 (was 40)
    const float ItemOuterHeight = 36f;    // matches ItemHeight (was 40)
    const float ItemMarginX = 4f;
    const float ItemMarginY = 0f;
    const float HeaderHeight = 36f;
    const float IconColumnWidth = 40f;
    const float IconSize = 16f;
    const float IndicatorW = 3f;
    const float IndicatorH = 16f;

    static readonly CornerRadius4 PaneOverlayCorners = new(0f, Radii.Overlay, Radii.Overlay, 0f);
    static readonly CornerRadius4 ContentLeftTopCorner = new(Radii.Overlay, 0f, 0f, 0f);
    static readonly ColorF LightDismissOverlay = ColorF.FromRgba(0, 0, 0, 0x1A);

    public override Element Render()
    {
        var (selected, setSelected) = UseState(Initial.Length > 0 ? Initial : (Items.Length > 0 ? FirstSelectable() : ""));
        var (paneOpen, setPaneOpen) = UseState(false);
        var (collapsed, setCollapsed) = UseState(false);
        var (expanded, setExpanded) = UseState(SeedExpanded(Items));     // expanded parent keys (new array per toggle — value-eq gated)
        var (focusedKey, setFocusedKey) = UseState(selected);            // keyboard cursor (visual focus highlight)

        float width = UseContext(Viewport.Size).Width;
        PaneMode autoMode = width <= 0f || width >= CompactThreshold ? PaneMode.Expanded
                          : width >= MinimalThreshold ? PaneMode.Compact
                          : PaneMode.Minimal;
        // The hamburger manually collapses the full-width pane to the icon rail (WinUI title-bar toggle).
        PaneMode mode = autoMode == PaneMode.Expanded && collapsed ? PaneMode.Compact : autoMode;
        float openPaneWidth = width > 0f ? MathF.Min(PaneWidth, width) : PaneWidth;
        bool inlinePane = autoMode == PaneMode.Expanded;
        float paneWidth = inlinePane ? (collapsed ? CompactWidth : openPaneWidth)
                        : paneOpen ? openPaneWidth
                        : autoMode == PaneMode.Compact ? CompactWidth : 0f;
        bool labelsVisible = !inlinePane || !collapsed;

        // DIAG: report the pane-mode decision whenever the viewport width or resulting mode changes.
        if (Diag.Enabled && (width != _diagWidth || mode != _diagMode))
        {
            _diagWidth = width; _diagMode = mode;
            Diag.Event("NavView", $"viewport.width={width:0.#} autoMode={autoMode} mode={mode} collapsed={collapsed} paneOpen={paneOpen} items={Items.Length} paneW={openPaneWidth:0.#}");
        }

        void Select(string key)
        {
            setSelected(key);
            setFocusedKey(key);
            setPaneOpen(false);
            OnSelect?.Invoke(key);
        }

        void ToggleExpand(string key)
            => setExpanded(expanded.Contains(key) ? expanded.Where(k => k != key).ToArray() : expanded.Append(key).ToArray());

        // Click/activate a row: a group navigates to its own (overview) page AND toggles its subtree; a leaf just navigates.
        // (WinUI: a parent with content selects + expands.)
        void Activate(NavItem it)
        {
            setFocusedKey(it.Key);
            if (it.IsExpandable) { ToggleExpand(it.Key); Select(it.Key); }
            else Select(it.Key);
        }

        // The visible, selectable rows in display order (headers excluded) — the arrow-key cursor moves through these.
        var flat = Flatten(Items, expanded);
        var rows = flat.Where(r => !r.Item.IsHeader).Select(r => r.Item).ToArray();

        void HandleNavKey(KeyEventArgs e)
        {
            int idx = Array.FindIndex(rows, r => r.Key == focusedKey);
            switch (e.KeyCode)
            {
                case Keys.Down:
                    if (rows.Length > 0) { setFocusedKey(rows[Math.Min(idx < 0 ? 0 : idx + 1, rows.Length - 1)].Key); e.Handled = true; }
                    break;
                case Keys.Up:
                    if (rows.Length > 0) { setFocusedKey(rows[Math.Max(idx < 0 ? 0 : idx - 1, 0)].Key); e.Handled = true; }
                    break;
                case Keys.Right:
                    if (idx >= 0 && rows[idx].IsExpandable)
                    {
                        if (!expanded.Contains(rows[idx].Key)) ToggleExpand(rows[idx].Key);
                        else setFocusedKey(rows[idx].Children![0].Key);
                        e.Handled = true;
                    }
                    break;
                case Keys.Left:
                    if (idx >= 0 && rows[idx].IsExpandable && expanded.Contains(rows[idx].Key)) { ToggleExpand(rows[idx].Key); e.Handled = true; }
                    else { var p = ParentOf(Items, focusedKey); if (p is not null) { setFocusedKey(p.Key); e.Handled = true; } }
                    break;
                case Keys.Enter:
                case Keys.Space:
                    if (idx >= 0) { Activate(rows[idx]); e.Handled = true; }
                    break;
            }
        }

        // Hamburger action by mode: at full width it collapses to the rail; from a manually-collapsed rail it expands
        // back; in adaptive compact/minimal it toggles the overlay flyout.
        Action toggle = autoMode == PaneMode.Expanded
            ? () => setCollapsed(!collapsed)
            : () => setPaneOpen(!paneOpen);

        var content = ContentFrame(selected, Select, mode);
        Element baseLayer = inlinePane ? new BoxEl
        {
            Direction = 0,
            Grow = 1,
            Children = [FullPane(paneWidth, flat, selected, focusedKey, expanded, Activate, HandleNavKey, toggle, overlay: false, labelsVisible: labelsVisible), content],
        } : mode switch
        {
            PaneMode.Compact => new BoxEl
            {
                Direction = 0,
                Grow = 1,
                Children = [CompactPane(selected, Select, toggle, paneOpen), content],
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
            new BoxEl { Fill = LightDismissOverlay, Opacity = 1f, OnClick = () => setPaneOpen(false) },
            FullPane(openPaneWidth, flat, selected, focusedKey, expanded, Activate, HandleNavKey, () => setPaneOpen(false), overlay: true, labelsVisible: true)
        ) with { Grow = 1f };
    }

    // ── tree helpers ──────────────────────────────────────────────────────────────
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
            if (!it.IsHeader)
                return it.Key;

        return Items.Length > 0 ? Items[0].Key : "";
    }

    Element ContentFrame(string selected, Action<string> select, PaneMode mode)
    {
        var child = Ctx.Provide(Nav, (Action<string>)select, Content?.Invoke(selected) ?? new BoxEl());
        return new BoxEl
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
            Children = [child],
        };
    }

    Element FullPane(float width, List<(NavItem Item, int Depth)> flat, string selected, string focusedKey, string[] expanded,
                     Action<NavItem> activate, Action<KeyEventArgs> handleNavKey, Action toggle, bool overlay, bool labelsVisible)
    {
        var mainItems = BuildItems(flat, selected, focusedKey, expanded, activate, expandedLayout: labelsVisible, ownIndicator: false, labelsVisible);
        var footerItems = BuildItems(Footer.Select(f => (f, 0)).ToList(), selected, focusedKey, expanded, activate, expandedLayout: labelsVisible, ownIndicator: true, labelsVisible);
        // Pane background, per the shipped WinUI generic.xaml: the EXPANDED (always-visible) pane =
        // NavigationViewExpandedPaneBackground = SolidBackgroundFillColorTransparent → FULLY TRANSPARENT, so DWM's Mica
        // window backdrop shows through. Only the transient OVERLAY (minimal/compact flyout) pane uses in-app acrylic
        // (NavigationViewDefaultPaneBackground) — engine acrylic samples the app canvas, which is correct over content
        // but would override window transparency (kill Mica) if used on the always-visible pane.
        var paneFill = ColorF.Transparent;

        var pane = new BoxEl
        {
            Width = width,
            Direction = 1,
            Fill = paneFill,
            ClipToBounds = true,
            Animate = PaneTransition,             // presented-width Reveal (model snaps to the final width; no relayout)
            Acrylic = overlay ? AcrylicSpec.InAppDefault : null,
            BorderColor = Tok.StrokeDividerDefault,
            BorderWidth = 1f,
            Corners = overlay ? PaneOverlayCorners : default,
            Shadow = overlay ? Elevation.Flyout : null,
            Children =
            [
                PaneTitleRow(toggle, labelsVisible),
                // Menu items scroll (overflow → scrollbar on hover); the footer stays pinned at the bottom. The list is the
                // single keyboard focus owner: arrows/Enter bubble here from the focused item (it has no ClickBit, so the
                // dispatcher's Enter/Space "activate focused clickable" never intercepts the arrow keys).
                Ui.ScrollView(Ui.ZStack(
                    new BoxEl { Direction = 1, Focusable = true, OnKeyDown = handleNavKey, Children = mainItems },
                    Ctx.Provide(IndicatorTarget, SelectedY(flat, selected), Embed.Comp(() => new NavIndicator()))
                )),
                new BoxEl { Direction = 1, Children = footerItems },
                new BoxEl { Height = 4 },
            ],
        };

        return pane;   // intent lives ON the node (BoxEl.Animate) — no wrapper component, no context round-trip
    }

    Element CompactPane(string selected, Action<string> select, Action toggle, bool paneOpen)
    {
        // v1: Compact/Minimal panes show TOP-LEVEL rows only (groups don't expand inline); a group navigates to its first
        // child so its icon still leads somewhere. Children are reachable in the Expanded pane.
        var noneExpanded = Array.Empty<string>();
        void CompactActivate(NavItem it)
        {
            if (it.IsExpandable && it.Children is { Length: > 0 } ch) select(ch[0].Key);
            else select(it.Key);
        }

        var children = new List<Element>
        {
            PaneToggleButton(toggle),
            new BoxEl { Height = 4 },
        };

        foreach (var it in Items)
            children.Add(it.IsHeader ? new BoxEl { Height = 8 } : Item(it, 0, selected, selected, noneExpanded, CompactActivate, expandedLayout: false, ownIndicator: true, labelsVisible: false));

        children.Add(new BoxEl { Grow = 1 });

        foreach (var it in Footer)
            children.Add(it.IsHeader ? new BoxEl { Height = 8 } : Item(it, 0, selected, selected, noneExpanded, CompactActivate, expandedLayout: false, ownIndicator: true, labelsVisible: false));

        return new BoxEl
        {
            Width = CompactWidth,
            Direction = 1,
            Fill = Tok.AcrylicBase,                    // translucent over Mica (see FullPane note)
            BorderColor = Tok.StrokeDividerDefault,
            BorderWidth = 1f,
            AlignItems = FlexAlign.Center,
            Children = children.ToArray(),
        };
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
            PaneToggleButton(toggle),
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

    Element PaneTitleRow(Action toggle, bool labelsVisible)
    {
        var children = new List<Element>();

        if (ShowBackButton)
            children.Add(PaneGlyphButton(Icons.Back, () => OnBack?.Invoke()));

        children.Add(PaneToggleButton(toggle));

        if (Header is { Length: > 0 } title)
            children.Add(AnimatedLabel(labelsVisible, new NavLabelSpec(
                new TextEl(title) { Size = 14f, Bold = true, Color = Tok.TextPrimary },
                PaneHeaderRowHeight, 1f, new Edges4(4, 0, 16, 0))));

        return new BoxEl
        {
            Direction = 0,
            Height = TopPaneHeight,
            AlignItems = FlexAlign.Center,
            Children = children.ToArray(),
        };
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

    const float IndentStep = 24f;   // per-depth left indent for nested group children (expanded layout only)

    static Element[] BuildItems(List<(NavItem Item, int Depth)> flat, string selected, string focusedKey, string[] expanded,
                                Action<NavItem> activate, bool expandedLayout, bool ownIndicator, bool labelsVisible)
    {
        var result = new Element[flat.Count];
        for (int i = 0; i < flat.Count; i++)
            result[i] = Item(flat[i].Item, flat[i].Depth, selected, focusedKey, expanded, activate, expandedLayout, ownIndicator, labelsVisible);

        return result;
    }

    static float SelectedY(List<(NavItem Item, int Depth)> flat, string selected)
    {
        float y = 0f;
        foreach (var (it, _) in flat)
        {
            if (it.IsHeader)
            {
                y += HeaderHeight;
                continue;
            }

            if (it.Key == selected)
                return y + ItemMarginY + (ItemHeight - IndicatorH) * 0.5f;

            y += ItemOuterHeight;
        }

        return -1000f;
    }

    static Element Item(NavItem it, int depth, string selected, string focusedKey, string[] expanded,
                        Action<NavItem> activate, bool expandedLayout, bool ownIndicator, bool labelsVisible)
    {
        if (it.IsHeader)
            return HeaderItem(it, expandedLayout, labelsVisible);

        bool sel = it.Key == selected;
        bool focused = it.Key == focusedKey;
        bool isExpanded = it.IsExpandable && expanded.Contains(it.Key);
        var foreground = sel ? Tok.TextPrimary : Tok.TextSecondary;
        float indent = expandedLayout ? depth * IndentStep : 0f;

        // Uniform structure across expanded/compact: an indicator sliver + the icon column are ALWAYS present (so the
        // keyed item node and its icon are reused, not remounted), plus the label ONLY when expanded. Collapsing removes
        // the label → it exits (fades + slides) while the icon stays put; the item's own background width reveals.
        var children = new List<Element>
        {
            new BoxEl
            {
                Width = IndicatorW,
                Height = IndicatorH,
                Corners = CornerRadius4.All(2f),
                Fill = ownIndicator && sel ? Tok.AccentDefault : ColorF.Transparent,
                AlignSelf = FlexAlign.Center,
            },
            new BoxEl
            {
                Width = IconColumnWidth - IndicatorW,
                Height = ItemHeight,
                Direction = 0,
                AlignItems = FlexAlign.Center,
                Justify = FlexJustify.Center,
                Children = [AnimatedIcon.Glyph(it.Glyph, IconSize, foreground, Theme.IconFont)],
            },
        };
        if (expandedLayout)
        {
            children.Add(AnimatedLabel(labelsVisible, new NavLabelSpec(
                new TextEl(it.Label) { Size = 14f, Color = foreground },
                ItemHeight, 1f, new Edges4(4, 0, 8, 0))));
            if (it.IsExpandable)
                children.Add(ChevronCell(isExpanded));
        }

        return new BoxEl
        {
            Key = it.Key,                       // keyed → reused across expanded/compact (icon glides, only the label exits)
            Direction = 0,
            Role = AutomationRole.NavigationItem,
            Animate = PaneTransition,           // reveal the item's background width as the pane collapses (position is a no-op)
            Width = expandedLayout ? float.NaN : PaneToggleWidth,
            Height = ItemHeight,
            Margin = new Edges4(ItemMarginX + indent, ItemMarginY, ItemMarginX, ItemMarginY),
            AlignItems = FlexAlign.Center,
            Corners = Radii.OverlayAll,   // WinUI nav items round to OverlayCornerRadius (8), not ControlCornerRadius (4)
            Fill = sel ? Tok.FillSubtleSecondary : (focused ? Tok.FillSubtleTertiary : ColorF.Transparent),
            HoverFill = sel ? Tok.FillSubtleTertiary : Tok.FillSubtleSecondary,
            PressedFill = sel ? Tok.FillSubtleSecondary : Tok.FillSubtleTertiary,
            OnClick = () => activate(it),
            Children = children.ToArray(),
        };
    }

    // A group's expand/collapse chevron — glyph-swapped (ChevronRight ⇄ ChevronDown) on toggle. (Glyph swap rather than a
    // rotated child component, since a reused child's props freeze at mount and wouldn't pick up the new expand state.)
    static Element ChevronCell(bool expanded) => new BoxEl
    {
        Width = 28f,
        Height = ItemHeight,
        AlignItems = FlexAlign.Center,
        Justify = FlexJustify.Center,
        Children = [new TextEl(expanded ? Icons.ChevronDown : Icons.ChevronRight) { Size = 11f, Color = Tok.TextSecondary, FontFamily = Theme.IconFont }],
    };

    static Element HeaderItem(NavItem it, bool expandedLayout, bool labelsVisible)
    {
        if (!expandedLayout)
            return new BoxEl { Height = 8 };

        return AnimatedLabel(labelsVisible, new NavLabelSpec(
            new TextEl(it.Label) { Size = 14f, Bold = true, Color = Tok.TextSecondary },
            HeaderHeight, 0f, new Edges4(16, 0, 16, 0)));
    }

    // A pane label: a plain child whose appearance/disappearance is the general enter/exit animation. Intent lives on the
    // node (BoxEl.Animate) — no wrapper component, no context. The `visible` arg is kept for call-site compatibility.
    static Element AnimatedLabel(bool visible, NavLabelSpec spec) => new BoxEl
    {
        Direction = 0,
        Grow = spec.Grow,
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
    public override Element Render()
    {
        float target = UseContext(NavigationView.IndicatorTarget);
        bool visible = target > -500f;
        UseSpring(AnimChannel.TranslateY, visible ? target : 0f, SpringParams.FromResponse(0.30f, 0.85f), visible ? MathF.Round(target) : -1f);
        UseTransition(AnimChannel.Opacity, visible ? 0f : 1f, visible ? 1f : 0f, 150f, Easing.EaseOut, visible);
        return new BoxEl
        {
            Width = 3f,
            Height = 16f,
            Margin = new Edges4(4f, 0f, 0f, 0f),
            Corners = CornerRadius4.All(2f),
            Fill = Tok.AccentDefault,
            AlignSelf = FlexAlign.Start,
        };
    }
}
