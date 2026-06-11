using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Scene;
using FluentGpu.Signals;

namespace FluentGpu.Controls;

/// <summary>WinUI <c>TabViewWidthMode</c> (TabView.idl:6-11) — how tab headers size. Default Equal: every tab gets
/// clamp(available/count, 100, 240) (TabView.cpp:1180-1228; TabViewItemMinWidth/MaxWidth,
/// TabView_themeresources.xaml:243-244). SizeToContent: intrinsic. Compact: unselected tabs collapse to icon-only
/// (TabViewItem.cpp:284-295; TabView.xaml:484-490).</summary>
public enum TabViewWidthMode : byte { Equal = 0, SizeToContent = 1, Compact = 2 }

/// <summary>WinUI <c>TabViewCloseButtonOverlayMode</c> (TabView.idl:15-20). Auto/Always show the close button on
/// every closable tab; OnPointerOver shows it only while the tab is selected or hovered (TabViewItem.cpp:251-282).</summary>
public enum TabViewCloseButtonOverlayMode : byte { Auto = 0, OnPointerOver = 1, Always = 2 }

/// <summary>One tab (WinUI <c>TabViewItem</c>, TabView.idl:205-230): a Header string, an optional leading icon glyph
/// (the IconSource slot, idl:214-215), per-tab IsClosable (default true, idl:217-219), and a Content factory rendered
/// in the content area while this tab is selected.</summary>
public sealed record TabViewItem
{
    public string Header { get; init; } = "";
    /// <summary>Segoe Fluent Icons glyph (the IconSource analog): 16px, 10px right margin, foreground follows the
    /// header ramp (TabViewItemHeaderIconSize/IconMargin, TabView_themeresources.xaml:246-247; ramps :95-99).</summary>
    public string? Icon { get; init; }
    /// <summary>WinUI <c>TabViewItem.IsClosable</c> (idl:217-219, default true) — gates the close button, middle-click
    /// close and Ctrl+F4.</summary>
    public bool IsClosable { get; init; } = true;
    /// <summary>Selected-tab content (WinUI TabViewItem.Content); invoked per render while selected.</summary>
    public Func<Element>? Content { get; init; }
}

/// <summary>A WinUI TabView (controls\dev\TabView): a horizontal strip of tab headers atop the selected tab's content.
/// The control OWNS its live tab collection (the WinUI <c>TabItems</c> vector, TabView.idl:135), seeded once from
/// <see cref="Items"/>/<see cref="Tabs"/> at mount: the close button / middle-click / Ctrl+F4 remove the tab (after
/// raising <see cref="OnTabCloseRequested"/>) with WinUI's reselect-at-the-removed-index rule (TabView.cpp:786-812);
/// the add button appends what <see cref="OnAddTabButtonClick"/> returns; pointer drag reorders tabs
/// (<see cref="CanReorderTabs"/>, default true). Equal width mode clamps tabs to 100–240px of the measured strip;
/// overflowing tabs scroll behind 32×24 repeat scroll buttons (±50px, disabled at the extremes) and the selected tab
/// is auto-scrolled into view. The selected header uses the SolidBackgroundFillColorTertiary surface and fuses flush
/// with the content area (the strip's 1px CardStroke baseline is per-tab and omitted under the selected header).
/// Per-part restyling goes through <see cref="Parts"/> (see <see cref="TemplateParts"/> for the contract).</summary>
public sealed class TabView : Component
{
    // Template parts (the WinUI x:Name vocabulary; see TemplateParts). Each part's doc lists the props the control
    // OWNS (re-asserted after any modifier — a Parts customization cannot win those).
    /// <summary>Each tab's clickable header plate (WinUI TabViewItem TabContainer). The SAME modifier runs for every
    /// tab — branch on app state for per-tab styling. Owned: OnClick (select), Role, Children.</summary>
    public const string PartTabItem = "TabItem";
    /// <summary>Each tab's header label — a <see cref="TextEl"/>, so style it via the generic map:
    /// <c>Parts.Set&lt;TextEl&gt;(TabView.PartTabLabel, t =&gt; t with { … })</c>. Owned: nothing (pure styling).</summary>
    public const string PartTabLabel = "TabLabel";
    /// <summary>Each tab's trailing close button (WinUI TabViewItem CloseButton). Owned: OnClick (close), Role.</summary>
    public const string PartTabCloseButton = "TabCloseButton";
    /// <summary>The trailing "+" add button (WinUI AddButton). Owned: OnClick, Role, OnRealized (chained).</summary>
    public const string PartAddButton = "AddButton";
    /// <summary>The header strip row hosting the leading inset + scrolling tabs + add button. Owned: Children.</summary>
    public const string PartStrip = "Strip";
    /// <summary>The selected tab's content area. Owned: Children.</summary>
    public const string PartContent = "Content";

    private const float TabMinWidth = 100f;       // TabViewItemMinWidth (TabView_themeresources.xaml:244)
    private const float TabMaxWidth = 240f;       // TabViewItemMaxWidth (:243)
    private const float ScrollAmount = 50f;       // c_scrollAmount (TabView.cpp:30) — per scroll-button repeat tick
    private const string CaretLeftSolid8 = "";    // ScrollDecreaseButton glyph (TabView.xaml:159)
    private const string CaretRightSolid8 = "";   // ScrollIncreaseButton glyph (TabView.xaml:165)

    /// <summary>Simple path: header strings only (each becomes a closable <see cref="TabViewItem"/>). Mount-only
    /// seed of the control-owned collection; ignored when <see cref="Items"/> is set.</summary>
    public IReadOnlyList<string> Tabs = [];
    /// <summary>Rich path: full per-tab models (header + icon + closability + content). Mount-only seed of the
    /// control-owned collection (the WinUI TabItems vector, TabView.idl:135).</summary>
    public IReadOnlyList<TabViewItem>? Items;
    /// <summary>WinUI <c>TabWidthMode</c> (TabView.idl:105-107, default Equal).</summary>
    public TabViewWidthMode TabWidthMode = TabViewWidthMode.Equal;
    /// <summary>WinUI <c>CloseButtonOverlayMode</c> (TabView.idl:109-111, default Auto).</summary>
    public TabViewCloseButtonOverlayMode CloseButtonOverlayMode = TabViewCloseButtonOverlayMode.Auto;
    /// <summary>WinUI <c>IsAddTabButtonVisible</c> (TabView.idl:119-120, default true).</summary>
    public bool IsAddTabButtonVisible = true;
    /// <summary>WinUI <c>CanReorderTabs</c> (TabView.idl:142-143, default TRUE) — pointer drag reorders the strip.</summary>
    public bool CanReorderTabs = true;
    /// <summary>Controlled selection: a two-way signal (the engine's controlled-prop idiom — plain fields are
    /// mount-only). Null = the control owns selection (WinUI SelectedIndex DP, TabView.idl:159-161).</summary>
    public Signal<int>? SelectedIndex;
    /// <summary>WinUI <c>SelectionChanged</c> (TabView.idl:169) with the new index.</summary>
    public Action<int>? OnSelectionChanged;
    /// <summary>WinUI <c>TabCloseRequested</c> (TabView.idl:124), raised with the closing index BEFORE the control
    /// commits the removal. WinUI leaves removal to the handler; our collection is control-owned (the TabItems
    /// vector), so the control performs the WinUI handler's <c>TabItems.RemoveAt</c> itself after raising.</summary>
    public Action<int>? OnTabCloseRequested;
    /// <summary>WinUI <c>AddTabButtonClick</c> (TabView.idl:128): return the tab to append (the WinUI handler's
    /// <c>TabItems.Add</c>) or null for no-op. Null handler = the button clicks to nothing, like an unhandled
    /// WinUI event.</summary>
    public Func<TabViewItem?>? OnAddTabButtonClick;
    /// <summary>Raised after a drag reorder commits (from, to) — the TabItemsChanged move notification
    /// (TabView.idl:130) so the app can mirror its model.</summary>
    public Action<int, int>? OnTabsReordered;
    /// <summary>Lightweight per-part styling (CSS ::part): modifiers keyed by the <c>PartXxx</c> consts; see
    /// <see cref="TemplateParts"/> for the contract.</summary>
    public TemplateParts? Parts;

    public static Element Create(IReadOnlyList<string> tabs,
                                 Action<int>? onTabCloseRequested = null,
                                 Func<TabViewItem?>? onAddTabButtonClick = null,
                                 Action<int>? onSelectionChanged = null)
        => Embed.Comp(() => new TabView
        {
            Tabs = tabs,
            OnTabCloseRequested = onTabCloseRequested,
            OnAddTabButtonClick = onAddTabButtonClick,
            OnSelectionChanged = onSelectionChanged,
        });

    public static Element Create(IReadOnlyList<TabViewItem> items,
                                 Action<int>? onTabCloseRequested = null,
                                 Func<TabViewItem?>? onAddTabButtonClick = null,
                                 Action<int>? onSelectionChanged = null,
                                 TabViewWidthMode widthMode = TabViewWidthMode.Equal)
        => Embed.Comp(() => new TabView
        {
            Items = items,
            OnTabCloseRequested = onTabCloseRequested,
            OnAddTabButtonClick = onAddTabButtonClick,
            OnSelectionChanged = onSelectionChanged,
            TabWidthMode = widthMode,
        });

    private List<TabViewItem> SeedItems()
    {
        var seeded = new List<TabViewItem>(Items?.Count ?? Tabs.Count);
        if (Items is { } rich)
            for (int i = 0; i < rich.Count; i++) seeded.Add(rich[i]);
        else
            for (int i = 0; i < Tabs.Count; i++) seeded.Add(new TabViewItem { Header = Tabs[i] });
        return seeded;
    }

    public override Element Render()
    {
        var hooks = UseContext(InputHooks.Current);
        // Subscribe to the client size: a resize re-clamps Equal widths and re-evaluates overflow, WinUI's
        // MeasureOverride → UpdateTabWidths on width change (TabView.cpp:1119-1128).
        var viewportSize = UseContext(Viewport.Size);

        // The control-owned live collection (WinUI TabItems, TabView.idl:135) — seeded once, mutated by
        // close/add/reorder; the version signal re-renders consumers of the mutation.
        var list = UseMemo(SeedItems);
        var itemsVersion = UseSignal(0);
        int version = itemsVersion.Value;   // subscribe

        var internalSel = UseSignal(0);
        var selSig = SelectedIndex ?? internalSel;
        int count = list.Count;
        int sel = count == 0 ? -1 : Math.Clamp(selSig.Value, 0, count - 1);

        var hoveredSig = UseSignal(-1);     // hovered tab index — drives separator/close-overlay visibility
        int hovered = hoveredSig.Value;

        var stripAreaNode = UseRef(NodeHandle.Null);   // the box wrapping the strip's scroll viewport
        var addButtonNode = UseRef(NodeHandle.Null);
        var focusSlot = UseRef(-1);          // 2i = tab i, 2i+1 = its close button, 2*count = the add button

        // Post-layout measure feedback (UpdateTabWidths, TabView.cpp:1130-1244): the tab area's width drives the
        // Equal clamp; content-vs-viewport drives the scroll buttons (ComputedHorizontalScrollBarVisibility,
        // TabView.xaml:157/:163); the offset drives their enabled states (TabView.cpp:689-734).
        var tabAreaW = UseSignal(0f);
        var overflowSig = UseSignal(false);
        var edgeSig = UseSignal(0);          // bit0 = can scroll back, bit1 = can scroll forward
        float measuredW = tabAreaW.Value;
        bool overflowing = overflowSig.Value;
        int edges = edgeSig.Value;

        var scene = Context.Scene;

        float equalW = TabWidthMode == TabViewWidthMode.Equal && count > 0 && measuredW > 0f
            ? Math.Clamp(measuredW / count, TabMinWidth, TabMaxWidth)   // std::clamp (TabView.cpp:1205, :1228)
            : float.NaN;

        // ── drag reorder (CanReorderTabs default TRUE, TabView.idl:142-143) — the engine E5-L3 strip ─────────────
        var ro = UseMemo(() => new Reorderable("tabview-strip"));
        ro.Scene = scene;
        ro.RequestRender = Context.RequestRerender;
        ro.ItemCount = count;
        ro.Horizontal = true;
        ro.Spacing = 0f;
        ro.ItemExtent = float.IsNaN(equalW) ? TabMinWidth : equalW;
        ro.ExtentOf = i =>
        {
            // SizeToContent/Compact tabs vary — sample the realized width (cold, once at lift).
            var n = WrapperNode(i);
            if (!n.IsNull && scene is { } s)
            {
                var r = s.AbsoluteRect(n);
                if (r.W > 0f) return r.W;
            }
            return float.IsNaN(equalW) ? TabMinWidth : equalW;
        };
        ro.OnReorder = CommitReorder;

        // ── scene helpers (cold paths: clicks/keys/effects) ───────────────────────────────────────────────────────

        NodeHandle ViewportNode()
        {
            if (scene is null || stripAreaNode.Value.IsNull || !scene.IsLive(stripAreaNode.Value)) return NodeHandle.Null;
            var vp = scene.FirstChild(stripAreaNode.Value);
            return !vp.IsNull && scene.HasScroll(vp) ? vp : NodeHandle.Null;
        }

        NodeHandle WrapperNode(int i)
        {
            var vp = ViewportNode();
            if (vp.IsNull || !scene!.TryGetScroll(vp, out var sc) || sc.ContentNode.IsNull) return NodeHandle.Null;
            var n = scene.FirstChild(sc.ContentNode);
            for (int k = 0; k < i && !n.IsNull; k++) n = scene.NextSibling(n);
            return n;
        }

        NodeHandle LastChildOf(NodeHandle n)
        {
            if (n.IsNull || scene is null) return NodeHandle.Null;
            var last = NodeHandle.Null;
            for (var c = scene.FirstChild(n); !c.IsNull; c = scene.NextSibling(c)) last = c;
            return last;
        }

        NodeHandle PlateNode(int i)
        {
            var n = WrapperNode(i);
            if (n.IsNull || scene is null) return NodeHandle.Null;
            if (CanReorderTabs) n = scene.FirstChild(n);   // the Reorderable.Item wrapper hosts our ZStack wrapper
            return LastChildOf(n);                         // the plate is the wrapper's LAST (topmost) child
        }

        bool CloseVisible(int i)
        {
            var it = list[i];
            if (!it.IsClosable) return false;   // !IsClosable collapses the button (TabViewItem.cpp:253-256)
            // OnPointerOver shows it only while selected or hovered (TabViewItem.cpp:261-273); Auto/Always = visible.
            return CloseButtonOverlayMode != TabViewCloseButtonOverlayMode.OnPointerOver
                   || i == sel || i == hoveredSig.Peek();
        }

        // ── scrolling (the WinUI strip ScrollViewer, TabView.xaml:114-127) ───────────────────────────────────────

        void UpdateEdges(in ScrollState sc)
        {
            // Enabled/disabled at the extremes, 0.1 threshold (TabView.cpp:689-734).
            float scrollable = MathF.Max(0f, sc.ContentW - sc.ViewportW);
            int e = (sc.OffsetX > 0.1f ? 1 : 0) | (sc.OffsetX < scrollable - 0.1f ? 2 : 0);
            if (edgeSig.Peek() != e) edgeSig.Value = e;
        }

        void ApplyScroll(NodeHandle vp, float target)
        {
            ref ScrollState sc = ref scene!.ScrollRef(vp);
            target = Math.Clamp(target, 0f, MathF.Max(0f, sc.ContentW - sc.ViewportW));
            if (target != sc.OffsetX)
            {
                sc.OffsetX = target;
                sc.TargetX = target;
                var contentNode = sc.ContentNode;
                if (!contentNode.IsNull && scene.IsLive(contentNode))
                {
                    scene.Paint(contentNode).LocalTransform = Affine2D.Translation(-target, 0f);
                    scene.Mark(contentNode, NodeFlags.TransformDirty | NodeFlags.PaintDirty);
                }
            }
            UpdateEdges(in sc);
            Context.RequestRerender();
        }

        // ±50px per repeat tick (OnScrollDecreaseClick/OnScrollIncreaseClick, TabView.cpp:1097-1117).
        void ScrollTabs(float delta)
        {
            var vp = ViewportNode();
            if (vp.IsNull || scene is null) return;
            ApplyScroll(vp, scene.ScrollRef(vp).OffsetX + delta);
        }

        // Minimal scroll bringing the tab fully into view, targeting a slightly wider rect so its end is not cut
        // off (StartBringTabIntoView, TabViewItem.cpp:625-631; selection-driven, :151-155).
        void BringTabIntoView(int idx)
        {
            var vp = ViewportNode();
            if (vp.IsNull || scene is null) return;
            var item = WrapperNode(idx);
            if (item.IsNull) return;
            ref ScrollState sc = ref scene.ScrollRef(vp);
            var ir = scene.AbsoluteRect(item);
            var vr = scene.AbsoluteRect(vp);
            float start = ir.X - vr.X + sc.OffsetX;
            float end = start + ir.W + 2f;
            float target = sc.OffsetX;
            if (start < target) target = start;
            else if (end > target + sc.ViewportW) target = end - sc.ViewportW;
            else return;
            ApplyScroll(vp, target);
        }

        void MeasureStrip()
        {
            if (scene is null) return;
            var vp = ViewportNode();
            if (vp.IsNull || !scene.TryGetScroll(vp, out var sc)) return;
            if (MathF.Abs(tabAreaW.Peek() - sc.ViewportW) > 0.5f) tabAreaW.Value = sc.ViewportW;
            bool of = sc.ContentW > sc.ViewportW + 0.5f;
            if (overflowSig.Peek() != of) overflowSig.Value = of;
            UpdateEdges(in sc);
        }
        UseLayoutEffect(MeasureStrip, version, count, (int)TabWidthMode, viewportSize, measuredW, overflowing, edges);

        // ── selection / close / add / reorder ────────────────────────────────────────────────────────────────────

        void Select(int idx)
        {
            if ((uint)idx >= (uint)list.Count || idx == selSig.Peek()) return;
            selSig.Value = idx;
            OnSelectionChanged?.Invoke(idx);
            BringTabIntoView(idx);   // selecting scrolls the tab into view (TabViewItem.cpp:151-155)
            // A tapped TabViewItem takes focus (ListViewItem semantics) — explicit because unselected plates are
            // TabStop=false (the roving single strip stop), which also opts them out of press-focus.
            var plate = PlateNode(idx);
            if (!plate.IsNull) hooks.FocusNode?.Invoke(plate, false);
        }

        void RequestClose(int idx)
        {
            if ((uint)idx >= (uint)list.Count || !list[idx].IsClosable) return;
            OnTabCloseRequested?.Invoke(idx);   // TabCloseRequested first (RequestCloseTab, TabView.cpp:1017)
            // The collection is control-owned (WinUI TabItems): WinUI's handler removes; here the control commits
            // the removal itself so closing works without app wiring.
            list.RemoveAt(idx);
            int s = selSig.Peek();
            if (list.Count > 0)
            {
                if (idx < s)
                {
                    selSig.Value = s - 1;       // items shifted left beneath the selection — same item, new index
                    OnSelectionChanged?.Invoke(s - 1);
                }
                else if (idx == s)
                {
                    // Reselect AT the removed index — the next tab — clamped to the new tail (OnItemsChanged,
                    // TabView.cpp:786-812; the enabled/visible forward walk degenerates to this clamp here).
                    int ns = Math.Min(idx, list.Count - 1);
                    if (selSig.Peek() != ns) selSig.Value = ns;
                    OnSelectionChanged?.Invoke(ns);
                }
            }
            itemsVersion.Value = itemsVersion.Peek() + 1;
        }

        void AddTab()
        {
            var made = OnAddTabButtonClick?.Invoke();   // AddTabButtonClick (TabView.idl:128)
            if (made is null) return;
            list.Add(made);
            itemsVersion.Value = itemsVersion.Peek() + 1;
        }

        void CommitReorder(int from, int to)
        {
            ReorderList.Move(list, from, to);
            int s = selSig.Peek();
            int ns = s == from ? to
                : from < s && to >= s ? s - 1
                : from > s && to <= s ? s + 1
                : s;
            if (ns != s) selSig.Value = ns;   // the selection FOLLOWS its tab (WinUI keeps SelectedItem on reorder)
            OnTabsReordered?.Invoke(from, to);
            itemsVersion.Value = itemsVersion.Peek() + 1;
        }

        // ── keyboard (root OnKeyDown sees keys only while focus is INSIDE — the ScopeOwner(*this) semantics of
        //    WinUI's TabView accelerators, TabView.cpp:77-98) ─────────────────────────────────────────────────────

        bool SlotExists(int slot)
            => slot == 2 * count ? IsAddTabButtonVisible
               : (slot & 1) == 0 || CloseVisible(slot >> 1);   // hidden close buttons are skipped (cpp:1449-1459)

        NodeHandle SlotNode(int slot)
            => slot == 2 * count ? addButtonNode.Value
               : (slot & 1) == 0 ? PlateNode(slot >> 1)
               : LastChildOf(PlateNode(slot >> 1));            // the close button is the plate's last child

        // Tab 1 → Tab 1 close → Tab 2 → … → Tab N close → add button → Tab 1, wrapping, skipping anything not
        // focusable (TabView::MoveFocus, TabView.cpp:1427-1515).
        bool MoveFocusChain(bool forward)
        {
            if (scene is null || hooks.FocusNode is not { } focusNode) return false;
            int total = 2 * count + 1;
            int cur = focusSlot.Value;
            if (cur < 0 || cur >= total || !SlotExists(cur)) return false;   // focus not on a strip element (cpp:1472-1478)
            int step = forward ? 1 : -1;
            for (int n = cur + step; ; n += step)
            {
                if (n < 0) n = total - 1;
                else if (n >= total) n = 0;
                if (n == cur) return false;
                if (!SlotExists(n)) continue;
                var h = SlotNode(n);
                if (h.IsNull || !scene.IsLive(h)) continue;
                focusNode(h, true);              // Focus(FocusState::Keyboard) (cpp:1509-1513)
                focusSlot.Value = n;
                return true;
            }
        }

        void OnRootKey(KeyEventArgs e)
        {
            // Ctrl+F4 closes the selected tab when closable; handled IFF it closed (the TabView-scoped accelerator,
            // TabView.cpp:77-82 + RequestCloseCurrentTab :1547-1561, OnCtrlF4Invoked :1563-1566).
            // NOTE Ctrl+Tab / Ctrl+Shift+Tab selection cycling (MoveSelection, cpp:1517-1545) is NOT implementable
            // control-side today: the dispatcher consumes EVERY Tab for focus movement before key routing and
            // before accelerator dispatch (InputDispatcher.cs OnKey's Tab branch).
            if (e.KeyCode == Keys.F4 && e.Ctrl && !e.IsRepeat)
            {
                if (sel >= 0 && list[sel].IsClosable)
                {
                    RequestClose(sel);
                    e.Handled = true;
                }
                return;
            }
            // Left/Right walk the strip focus chain; Alt+Shift+Arrow is reserved (WinUI tab reorder chord) and falls
            // through (TabViewItem.cpp:578-603 routes the arrows to TabView::MoveFocus).
            if ((e.KeyCode == Keys.Left || e.KeyCode == Keys.Right) && !(e.Alt && e.Shift))
            {
                if (MoveFocusChain(e.KeyCode == Keys.Right)) e.Handled = true;
            }
        }

        // ── tab headers ──────────────────────────────────────────────────────────────────────────────────────────

        var slots = new Element[count];
        for (int s = 0; s < count; s++)
        {
            // Mid-drag, render the PROJECTED order (displaced tabs FLIP through their LayoutTransition).
            int idx = CanReorderTabs ? ro.ItemAt(s) : s;
            var it = list[idx];
            bool isSel = idx == sel;
            bool dragging = CanReorderTabs && ro.IsLifted && ro.LiftedIndex == idx;
            bool compact = TabWidthMode == TabViewWidthMode.Compact && !isSel;   // Compact = icon-only UNSELECTED (TabViewItem.cpp:284-295)
            bool closeVisible = CloseVisible(idx);
            int tabIdx = idx;
            Action select = () => Select(tabIdx);
            Action close = () => RequestClose(tabIdx);

            var kids = new List<Element>(3);
            if (it.Icon is { } glyph)
            {
                kids.Add(new TextEl(glyph)
                {
                    Size = 16f,                          // TabViewItemHeaderIconSize 16 viewbox (themeresources:246; TabView.xaml:568)
                    FontFamily = Theme.IconFont,
                    // Compact zeroes the icon margin (TabView.xaml:486); standard = 0,0,10,0 (themeresources:247).
                    Margin = compact ? default : new Edges4(0, 0, 10, 0),
                    Color = isSel ? Tok.TextPrimary : Tok.TextSecondary,           // TabViewItemIconForeground (:95) / Selected (:97)
                    PressedColor = isSel ? Tok.TextPrimary : Tok.TextTertiary,     // IconForegroundPressed (:96); selected keeps primary (TabView.xaml:385)
                    DisabledColor = Tok.TextDisabled,                              // IconForegroundDisabled (:99)
                });
            }
            if (!compact)   // Compact collapses the label (TabView.xaml:487)
            {
                var label = new TextEl(it.Header)
                {
                    Size = 12f,                                                    // TabViewItemHeaderFontSize (themeresources:245)
                    Weight = isSel ? (ushort)600 : (ushort)0,                      // Selected sets FontWeight=SemiBold (TabView.xaml:351)
                    Color = isSel ? Tok.TextPrimary : Tok.TextSecondary,           // TabViewItemHeaderForeground (:90) / Selected (:92); PointerOver stays Secondary (:93)
                    PressedColor = isSel ? Tok.TextPrimary : Tok.TextTertiary,     // ForegroundPressed = TextFillColorTertiary (:91); PressedSelected keeps Selected (TabView.xaml:384)
                    DisabledColor = Tok.TextDisabled,                              // ForegroundDisabled (:94)
                    Grow = 1f, Shrink = 1f,
                    Trim = TextTrim.Clip,   // width-capped tabs clip like the WinUI ContentPresenter (no TextTrimming set)
                };
                kids.Add(Parts.Apply(PartTabLabel, label));
            }
            if (closeVisible)
            {
                var closeButton = new BoxEl
                {
                    Direction = 0, Width = 32f, Height = 24f,   // TabViewItemHeaderCloseButtonWidth/Height (themeresources:249/:248)
                    AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                    Margin = new Edges4(4, 0, 0, 0),            // TabViewItemHeaderCloseMargin 4,0,0,0 (:252)
                    Corners = Radii.ControlAll,                 // ControlCornerRadius (TabView.xaml:179)
                    HoverFill = Tok.FillSubtleSecondary,        // CloseButtonBackgroundPointerOver (:127)
                    PressedFill = Tok.FillSubtleTertiary,       // CloseButtonBackgroundPressed (:126)
                    Role = AutomationRole.Button,
                    OnClick = close,
                    // IsTabStop=False (TabView.xaml:576) — Tab skips it; the Left/Right chain still reaches it
                    // (WinUI's temporary-IsTabStop focus dance, TabView.cpp:1496-1513).
                    TabStop = false,
                    OnFocusChanged = got =>
                    {
                        if (got) focusSlot.Value = 2 * tabIdx + 1;
                        else if (focusSlot.Value == 2 * tabIdx + 1) focusSlot.Value = -1;
                    },
                    Children =
                    [
                        new TextEl(Icons.Cancel)                // E711 (TabView.xaml:576)
                        {
                            Size = 12f,                         // TabViewItemHeaderCloseFontSize (themeresources:251)
                            FontFamily = Theme.IconFont,
                            Color = Tok.TextPrimary,            // CloseButtonForeground (:132)
                            HoverColor = Tok.TextPrimary,       // PointerOver (:134)
                            PressedColor = Tok.TextSecondary,   // Pressed = TextFillColorSecondary (:133)
                            DisabledColor = Tok.TextDisabled,   // Disabled (:138)
                        },
                    ],
                };
                kids.Add(Parts.Apply(PartTabCloseButton, closeButton) with { OnClick = close, Role = AutomationRole.Button });
            }

            var plate = new BoxEl
            {
                Direction = 0, AlignItems = FlexAlign.Center, Gap = 0f,
                // TabViewItemHeaderPadding 8,3,4,3 with a close button; without one the right inset widens to 8
                // (PaddingWithCloseButton/WithoutCloseButton, themeresources:253-254; the CloseButtonCollapsed
                // padding swap, TabView.xaml:498-503). TabViewSelectedItemHeaderPadding 9,3,5,4 is defined (:241)
                // but never applied by either WinUI template — the Selected state only swaps the Margin.
                Padding = closeVisible ? new Edges4(8, 3, 4, 3) : new Edges4(8, 3, 8, 3),
                // Selected: TabViewSelectedItemHeaderMargin -1,0,-1,1 (:269) — 1px overlap onto both neighbors.
                // The 1px bottom lift belongs to WinUI's TabContainer border only; the visible selected surface
                // (SelectedBackgroundPath, TabGeometry spanning the full height — TabViewItem.cpp:106-123) is
                // bottom-FLUSH so it fuses with the content. Our plate IS that surface, so its bottom stays flush.
                Margin = isSel && !dragging ? new Edges4(-1, 0, -1, 0) : default,
                MinHeight = 32f,                                // TabViewItemMinHeight (themeresources:242)
                // OverlayCornerRadius top-filtered (TabView.xaml:562, TopCornerRadiusFilterConverter); the drag
                // plate keeps all four corners (TabDragVisualContainer, TabView.xaml:554-561).
                Corners = dragging ? Radii.OverlayAll : Radii.OverlayTop,
                // Selected/drag surface: SolidBackgroundFillColorTertiary (TabViewItemHeaderBackgroundSelected :85,
                // TabViewItemHeaderDragBackground :86) — the same token the content area uses, so they fuse.
                // Unselected rest: LayerOnMicaBaseAltFillColorTransparent (:84); hover/pressed
                // LayerOnMicaBaseAltFillColorSecondary/Default (:87-88) → nearest engine tokens
                // FillControlSecondary/Tertiary (no LayerOnMicaBaseAlt ramp in Tok).
                Fill = isSel || dragging ? Tok.FillSolidTertiary : ColorF.Transparent,
                HoverFill = isSel || dragging ? Tok.FillSolidTertiary : Tok.FillControlSecondary,
                PressedFill = isSel || dragging ? Tok.FillSolidTertiary : Tok.FillControlTertiary,
                BorderColor = dragging ? Tok.StrokeCardDefault : default,   // drag plate border = TabViewBorderBrush (TabView.xaml:558)
                BorderWidth = dragging ? 1f : 0f,
                Role = AutomationRole.Tab,
                OnClick = select,
                // ONE strip tab stop — the selected tab (TabViewListView TabNavigation="Once", TabView.xaml:54);
                // Left/Right rove across the rest (TabView.cpp:1427-1515).
                TabStop = isSel ? null : false,
                OnPointerPressed = e =>
                {
                    // Middle-click close: WinUI captures on MiddleButtonPressed and closes a closable tab on
                    // middle-release over it (TabViewItem.cpp:418-425, :449-462); the dispatcher delivers
                    // Button==2 on middle-release-over-the-same-node.
                    if (e.Button == 2 && list[tabIdx].IsClosable)
                    {
                        RequestClose(tabIdx);
                        e.Handled = true;
                    }
                },
                OnHoverMove = _ => { if (hoveredSig.Peek() != tabIdx) hoveredSig.Value = tabIdx; },
                OnPointerExit = () => { if (hoveredSig.Peek() == tabIdx) hoveredSig.Value = -1; },
                OnFocusChanged = got =>
                {
                    if (got) focusSlot.Value = 2 * tabIdx;
                    else if (focusSlot.Value == 2 * tabIdx) focusSlot.Value = -1;
                },
                Children = kids.ToArray(),
            };
            // Parts: restyle anything; the select mechanics always win.
            plate = Parts.Apply(PartTabItem, plate) with { OnClick = select, Role = AutomationRole.Tab, Children = plate.Children };

            // Overlays around the plate (paint order mirrors the WinUI item template, TabView.xaml:546-562:
            // BottomBorderLine, then the curve-out feet, then TabSeparator, then the container on top).
            var overlay = new List<Element>(4);
            if (!isSel && !dragging)
            {
                // The strip baseline runs UNDER unselected tabs only — Selected/drag collapse it
                // (BottomBorderLine, TabView.xaml:546; collapsed :337, drag :541) so the selected tab fuses
                // with the content area below.
                overlay.Add(new BoxEl
                {
                    Direction = 1, Justify = FlexJustify.End, HitTestVisible = false,
                    Children =
                    [
                        new BoxEl
                        {
                            Height = 1f,
                            Fill = Tok.StrokeCardDefault,   // TabViewBorderBrush = CardStrokeColorDefault (themeresources:68)
                            // 2px clearance toward a selected neighbor so the line stops short of its curve-out
                            // feet (LeftOfSelectedTab/RightOfSelectedTab, TabView.xaml:516-525).
                            Margin = tabIdx == sel - 1 ? new Edges4(0, 0, 2, 0)
                                : tabIdx == sel + 1 ? new Edges4(2, 0, 0, 0)
                                : default,
                        },
                    ],
                });
            }
            if (isSel && !dragging)
            {
                // 4px curve-out "feet": WinUI's TabGeometry flares the selected background 4px outward at both
                // bottom corners (TabViewItem.cpp:106-123; Left/RightRadiusRenderArc, TabView.xaml:547-548). No
                // Path primitive here — a 4×4 plate in the selected fill rounded on its top-OUTER corner is the
                // same quarter-arc flare. WinUI paints the selected item above its neighbors (ZIndex 20,
                // TabViewItem.cpp:153); without a z channel the RIGHT neighbor's hover fill may tint the right
                // foot — accepted.
                overlay.Add(new BoxEl
                {
                    Direction = 0, AlignItems = FlexAlign.End, HitTestVisible = false,
                    Children =
                    [
                        new BoxEl { Width = 4f, Height = 4f, Margin = new Edges4(-4, 0, 0, 0), Corners = new CornerRadius4(4f, 0f, 0f, 0f), Fill = Tok.FillSolidTertiary },
                        new BoxEl { Grow = 1f },
                        new BoxEl { Width = 4f, Height = 4f, Margin = new Edges4(0, 0, -4, 0), Corners = new CornerRadius4(0f, 4f, 0f, 0f), Fill = Tok.FillSolidTertiary },
                    ],
                });
            }
            // Vertical separator at the tab's right edge: hidden for the selected tab and its left neighbor
            // (SetTabSeparatorOpacity, TabView.cpp:217-231), on hover/press of the tab itself (TabView.xaml:322,
            // :332) and when its RIGHT neighbor is hovered (HideLeftAdjacentTabSeparator, TabViewItem.cpp:503-536)
            // — computed declaratively per render, immune to the stale-opacity hazard WinUI documents around its
            // imperative pokes (TabView.cpp:203-208).
            bool sepVisible = !dragging && tabIdx != sel && tabIdx + 1 != sel && tabIdx != hovered && tabIdx + 1 != hovered;
            overlay.Add(new BoxEl
            {
                Direction = 0, Justify = FlexJustify.End, HitTestVisible = false,
                Children =
                [
                    new BoxEl
                    {
                        Width = 1f,                          // TabSeparator Width=1, right-aligned (TabView.xaml:553)
                        Margin = new Edges4(0, 8, 0, 8),     // TabViewItemSeparatorMargin 0,8,0,8 (themeresources:266)
                        Fill = Tok.StrokeDividerDefault,     // TabViewItemSeparator = DividerStrokeColorDefaultBrush (:46/:124)
                        Opacity = sepVisible ? 1f : 0f,
                    },
                ],
            });
            overlay.Add(plate);

            var wrapper = new BoxEl
            {
                ZStack = true,
                Width = equalW,   // Equal: clamp(available/count, 100, 240) (TabView.cpp:1180-1228); NaN = intrinsic
                MinWidth = TabWidthMode == TabViewWidthMode.Equal ? TabMinWidth : float.NaN,
                MaxWidth = TabWidthMode == TabViewWidthMode.Equal ? TabMaxWidth : float.NaN,
                Children = overlay.ToArray(),
            };

            slots[s] = CanReorderTabs
                // Reorderable.Item wires the drag gesture + FLIP displacement; it is NOT a focus stop (the plate
                // inside is) — its keyboard-lift mode would double the strip's tab stops, and WinUI reorders tabs
                // by pointer here.
                ? ((BoxEl)ro.Item(idx, wrapper, key: "tab#" + idx)) with { Focusable = false, TabStop = false }
                : wrapper with { Key = "tab#" + idx };
        }

        // ── the strip: leading inset · scrolling tabs (± repeat scroll buttons) · add button ─────────────────────

        Element BottomLineHost() => new BoxEl
        {
            // The strip baseline outside the tab region (LeftBottomBorderLine/RightBottomBorderLine,
            // TabView.xaml:36-37 and the scroller chrome :155-156) — TabViewBorderBrush = CardStrokeColorDefault.
            Direction = 1, Justify = FlexJustify.End, HitTestVisible = false,
            Children = [new BoxEl { Height = 1f, Fill = Tok.StrokeCardDefault }],
        };

        Element MakeScrollButton(bool back)
        {
            bool enabled = back ? (edges & 1) != 0 : (edges & 2) != 0;
            return new BoxEl
            {
                Key = back ? "dec" : "inc",
                // Left/RightScrollButtonContainerPadding 8,0,3,3 / 3,0,8,3, bottom-aligned (themeresources:259-260;
                // TabView.xaml:157/:163 VerticalAlignment="Bottom").
                Padding = back ? new Edges4(8, 0, 3, 3) : new Edges4(3, 0, 8, 3),
                AlignSelf = FlexAlign.End,
                Children =
                [
                    new BoxEl
                    {
                        Direction = 0, Width = 32f, Height = 24f,   // TabViewItemScrollButtonWidth/Height (themeresources:255-256)
                        AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                        Corners = Radii.ControlAll,                 // ControlCornerRadius (TabView.xaml:586)
                        Fill = ColorF.Transparent,                  // TabViewScrollButtonBackground (:112)
                        HoverFill = Tok.FillSubtleSecondary,        // PointerOver (:114)
                        PressedFill = Tok.FillSubtleTertiary,       // Pressed (:113)
                        IsEnabled = enabled,                        // disabled at the extremes (TabView.cpp:689-734)
                        TabStop = false,                            // IsTabStop=False (TabView.xaml:158/:164)
                        Repeats = true,
                        RepeatDelayMs = 50f, RepeatIntervalMs = 100f,   // RepeatButton Delay="50" Interval="100" (TabView.xaml:158/:164)
                        Role = AutomationRole.Button,
                        OnClick = back ? () => ScrollTabs(-ScrollAmount) : () => ScrollTabs(+ScrollAmount),
                        Children =
                        [
                            new TextEl(back ? CaretLeftSolid8 : CaretRightSolid8)
                            {
                                Size = 8f,                          // TabViewItemScrollButonFontSize (themeresources:257)
                                FontFamily = Theme.IconFont,
                                Color = Tok.TextSecondary,          // ScrollButtonForeground — same token rest/hover/pressed (:116-118)
                                DisabledColor = Tok.TextDisabled,   // Disabled (:119)
                            },
                        ],
                    },
                ],
            };
        }

        var scrollHost = new ScrollEl
        {
            Horizontal = true,
            ContentSized = true,   // size to the tabs; Shrink turns on scrolling once they exceed the strip
            Shrink = 1f,
            Content = new BoxEl { Direction = 0, Gap = 0f, Children = slots },   // tabs are ADJACENT (ItemsStackPanel, TabView.xaml:79); the selected -1 margins overlap (themeresources:269)
        };
        Element scrollArea = new BoxEl
        {
            Direction = 0, Shrink = 1f,
            OnRealized = h => stripAreaNode.Value = h,
            Children = [scrollHost],
        };
        if (CanReorderTabs)
            scrollArea = ((BoxEl)ro.List(scrollArea)) with { Key = "tabs", Grow = 0f, Shrink = 1f };
        else
            scrollArea = ((BoxEl)scrollArea) with { Key = "tabs" };

        // Leading inset: LeftContentColumn MinWidth=2 (TabView.xaml:31) + the ItemsPresenter's 4px header cell
        // (:117-120) — static here (WinUI's header cell scrolls with the tabs).
        var headRow = new BoxEl
        {
            Direction = 0,
            Children = overflowing
                ? [new BoxEl { Width = 6f }, MakeScrollButton(back: true)]
                : [new BoxEl { Width = 6f }],
        };
        var headGroup = new BoxEl { Key = "head", ZStack = true, Children = [BottomLineHost(), headRow] };

        Action addClick = AddTab;
        Action<NodeHandle> addCapture = h => addButtonNode.Value = h;
        var addButton = new BoxEl
        {
            Direction = 0, Width = 32f, Height = 24f,   // TabViewItemAddButtonWidth/Height (themeresources:261-262)
            AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            Corners = Radii.ControlAll,                 // ControlCornerRadius (TabView.xaml:231)
            Fill = Tok.FillSubtleTransparent,           // TabViewButtonBackground (:100)
            HoverFill = Tok.FillSubtleSecondary,        // PointerOver (:102)
            PressedFill = Tok.FillSubtleTertiary,       // Pressed (:101)
            Role = AutomationRole.Button,
            OnClick = addClick,
            OnRealized = addCapture,
            OnFocusChanged = got =>
            {
                if (got) focusSlot.Value = 2 * count;
                else if (focusSlot.Value == 2 * count) focusSlot.Value = -1;
            },
            Children =
            [
                new TextEl(Icons.Add)                   // E710 (TabView.xaml:41)
                {
                    Size = 12f,                         // TabViewItemAddButtonFontSize (themeresources:263)
                    FontFamily = Theme.IconFont,
                    Color = Tok.TextPrimary,            // TabViewButtonForeground (:104)
                    HoverColor = Tok.TextPrimary,       // PointerOver (:106)
                    PressedColor = Tok.TextSecondary,   // Pressed = TextFillColorSecondary (:105)
                    DisabledColor = Tok.TextDisabled,   // Disabled (:107)
                },
            ],
        };
        var addStyled = Parts.Apply(PartAddButton, addButton);
        addButton = addStyled with
        {
            OnClick = addClick,
            Role = AutomationRole.Button,
            OnRealized = TemplateParts.Chain(addCapture, addStyled.OnRealized),
        };

        var tailKids = new List<Element>(4)
        {
            new BoxEl { Width = 4f },   // the ItemsPresenter's 4px footer cell (TabView.xaml:121-125)
        };
        if (overflowing) tailKids.Add(MakeScrollButton(back: false));
        if (IsAddTabButtonVisible)      // IsAddTabButtonVisible gates the container (TabView.xaml:40)
        {
            tailKids.Add(new BoxEl
            {
                Key = "add",
                Padding = new Edges4(3, 0, 0, 3),   // TabViewItemAddButtonContainerPadding (themeresources:264)
                AlignSelf = FlexAlign.End,          // container VerticalAlignment="Bottom" (TabView.xaml:40)
                Children = [addButton],
            });
        }
        tailKids.Add(new BoxEl { Grow = 1f });      // RightContentColumn (TabView.xaml:34) — carries the baseline to the edge
        var tailRow = new BoxEl { Direction = 0, Grow = 1f, Children = tailKids.ToArray() };
        var tailGroup = new BoxEl { Key = "tail", ZStack = true, Grow = 1f, Children = [BottomLineHost(), tailRow] };

        var stripChildren = new Element[] { headGroup, scrollArea, tailGroup };
        var strip = new BoxEl
        {
            Direction = 0,
            Padding = new Edges4(0, 8, 0, 0),   // TabViewHeaderPadding 0,8,0,0 (themeresources:239)
            Children = stripChildren,
        };
        strip = Parts.Apply(PartStrip, strip) with { Children = stripChildren };

        // ── content (TabContentPresenter, TabView.xaml:45) ───────────────────────────────────────────────────────

        Element? body = sel >= 0 ? list[sel].Content?.Invoke() : null;
        var content = new BoxEl
        {
            Grow = 1f,
            // The SAME surface as the selected header (SolidBackgroundFillColorTertiary, themeresources:85) — the
            // selected tab fuses flush with its content.
            Fill = Tok.FillSolidTertiary,
            Children = body is null ? [] : [body],
        };
        content = Parts.Apply(PartContent, content) with { Children = content.Children };

        return new BoxEl
        {
            Direction = 1, Grow = 1f,
            // Root key hook ≈ the accelerators' ScopeOwner(*this) (TabView.cpp:81/:90/:97): keys bubble here only
            // while focus is anywhere inside the TabView (strip OR tab content), exactly the WinUI scope.
            OnKeyDown = OnRootKey,
            Children = [strip, content],
        };
    }
}
