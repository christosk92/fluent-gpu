using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Reconciler;
using FluentGpu.Scene;
using FluentGpu.Signals;

namespace FluentGpu.Controls;

/// <summary>
/// Imperative handle for an <see cref="ItemsView"/> (the WinUI methods that live on the control object:
/// ItemsView.idl:46-58 — CurrentItemIndex, StartBringItemIntoView, and the selection API via <see cref="Selection"/>).
/// Pass one to <c>ItemsView.Create</c>; the component wires it at mount.
/// </summary>
public sealed class ItemsViewController
{
    internal Action<int, float>? BringIntoViewImpl;
    internal Func<int>? GetCurrent;
    internal Action<float>? ScrollByImpl;

    /// <summary>The live selection model — Select/Deselect/IsSelected/SelectAll/DeselectAll/InvertSelection
    /// (ItemsView.idl:53-58) are its methods; range-based, so they never realize items.</summary>
    public SelectionModel? Selection { get; internal set; }

    /// <summary>WinUI <c>CurrentItemIndex</c> (idl:46-47, default −1) — the keyboard-current item.</summary>
    public int CurrentItemIndex => GetCurrent?.Invoke() ?? -1;

    /// <summary>WinUI <c>StartBringItemIntoView(index, BringIntoViewOptions)</c> (idl:52): realizes the target by
    /// scrolling the virtualized viewport. <paramref name="alignmentRatio"/> NaN = minimal scroll (the default
    /// BringIntoViewOptions); 0 = align item start to viewport start, 1 = end to end (the Home/End ratios,
    /// ItemsViewInteractions.cpp:1013-1016).</summary>
    public void StartBringItemIntoView(int index, float alignmentRatio = float.NaN)
        => BringIntoViewImpl?.Invoke(index, alignmentRatio);

    /// <summary>Nudge the virtualized viewport by <paramref name="delta"/> DIP along its scroll axis (clamped).
    /// The drag-reorder EDGE AUTO-SCROLL seam: a composing list (ListView) calls this while the pointer drags near
    /// the viewport edge (the plan's E5-L3 edge auto-scroll in virtualized lists). No-op for non-virtual hosts.</summary>
    public void ScrollBy(float delta) => ScrollByImpl?.Invoke(delta);
}

/// <summary>Per-item visual state handed to a custom <see cref="ItemContainerFactory"/> (the L4 skin seam).</summary>
public readonly record struct ItemChromeState(
    bool IsSelected, bool IsEnabled, bool ShowCheckbox, bool IsChecked, bool IsCurrent);

/// <summary>
/// Custom item-container factory — the E11-L4 SKIN seam: ListView/GridView/TreeView supply their WinUI item chrome
/// (ListViewItemPresenter / GridView dual-border / TreeViewItem row) around the engine's ONE selection + keyboard
/// substrate. The returned BoxEl must wire <paramref name="onInteraction"/> (press/Enter/Space → the selector) and
/// <paramref name="onFocusChanged"/> (keyboard-current tracking), and should be <c>Focusable</c> so the engine focus
/// ring lands on items. Null ⇒ the default WinUI <see cref="ItemContainer"/> chrome.
/// </summary>
public delegate BoxEl ItemContainerFactory(
    int index, Element content, ItemChromeState state,
    Action<ItemContainerTrigger, KeyModifiers> onInteraction, Action<bool> onFocusChanged);

/// <summary>
/// WinUI <c>ItemsView</c> (controls\dev\ItemsView) — E11-L3: the L2 repeater substrate + <see cref="SelectionModel"/>
/// + <see cref="ItemContainer"/> + keyboard navigation/typeahead + StartBringItemIntoView, composed. Every item
/// template is wrapped in an ItemContainer (selection visuals, pointer states, multi-select checkbox); the items ride
/// ONE virtualized viewport (<see cref="VirtualListEl"/>) over any <see cref="RepeatLayout"/> — Stack, Grid,
/// LinedFlow (the WinUI photo-wall), Measured, SpanGrid or a custom seam layout.
///
/// Behavior contract (verified against the WinUI sources):
/// • SelectionMode None/Single/Multiple/Extended (ItemsView.idl:6-12; default Single, ItemsView.h s_defaultSelectionMode)
///   with the selector semantics in SelectionModel.OnInteractedAction/OnFocusedAction (Single/Multiple/ExtendedSelector.cpp).
/// • ItemInvoked gating (ItemsView.cpp:404-432 CanRaiseItemInvoked): requires IsItemInvokedEnabled; with
///   SelectionMode None, DoubleTap never invokes; with a selection mode active, Tap and Space select WITHOUT invoking
///   (Enter and DoubleTap invoke).
/// • Ctrl+A selects all in Multiple/Extended only (ItemsViewInteractions.cpp:35-50).
/// • Arrows move the current item per the layout's index orientation (ItemsViewInteractions.cpp:923-1102): a vertical
///   stack maps Up/Down to ±1 (Left/Right no-op), a grid maps Left/Right ±1 and Up/Down ±columns, custom layouts get
///   geometric nearest-in-direction. Every walk skips disabled items (the SharedHelpers::IsFocusableElement gate,
///   cpp:203/:321). Home/End bring item 0 / count−1 into view edge-aligned, then focus the first/last FOCUSABLE
///   element (cpp:990-1044); PageUp/Down run the railed three-phase page navigation (cpp:1103-1242). Keyboard moves
///   run the selector's OnFocusedAction and focus the realized container (engine focus ring).
/// • TabNavigation="Once" (ItemsView.xaml:7): ONE roving tab stop — the keyboard-current container; tab-in with no
///   current lands on the selected item (Single mode) else the first focusable item (the GettingFocus redirect,
///   ItemsViewInteractions.cpp:645-721).
/// • Typeahead: printable chars accumulate (1s reset) and jump to the next prefix-matching item from current+1,
///   wrapping (the ListView typeahead shape; the plan's L3 requirement).
/// • Selection is DECOUPLED from realization: SelectAll over 50k items stores one range; only the realized window
///   re-skins (this component subscribes to <c>SelectionModel.Version</c>).
/// </summary>
public sealed class ItemsView : Component
{
    private const float TypeaheadResetMs = 1000f;
    private const int GeometricScan = 512;   // bounded candidate scan for custom-layout arrow nav

    // ── legacy simple surface (kept source-compatible: ItemsViewPage / MiscPages.cs uses Create(items, columns)) ──
    public IReadOnlyList<string> Items = [];
    public int Columns = 4;

    // ── full surface ──
    /// <summary>Item count when an <see cref="ItemTemplate"/> drives content (−1 ⇒ <see cref="Items"/>.Count).</summary>
    public int ItemCount = -1;
    /// <summary>The item CONTENT template (wrapped in an <see cref="ItemContainer"/> per item).</summary>
    public Func<int, Element>? ItemTemplate;
    /// <summary>Typeahead text per item (defaults to <see cref="Items"/> when it backs the view).</summary>
    public Func<int, string>? ItemText;
    /// <summary>Per-item enabled gate (disabled items dim to 0.3 and don't interact).</summary>
    public Func<int, bool>? IsItemEnabled;
    /// <summary>L4 skin seam: replaces the default <see cref="ItemContainer"/> chrome (ListView/GridView/TreeView).</summary>
    // Per-item chrome customization goes through the ContainerFactory seam, NOT TemplateParts — per-item part
    // modifiers in recycled scroll paths are an allocation/recycling hazard (docs/guide/control-fidelity.md §6).
    public ItemContainerFactory? ContainerFactory;
    /// <summary>Stable per-item keys for the keyed diff (reorder projections need item-identity keys).</summary>
    public Func<int, string>? KeyOf;
    public RepeatLayout Layout;
    public bool HasExplicitLayout;
    public ItemsSelectionMode SelectionMode = ItemsSelectionMode.Single;   // ItemsView.h s_defaultSelectionMode
    /// <summary>External selection model (shared/multi-view); null ⇒ the component owns one.</summary>
    public SelectionModel? Selection;
    public bool IsItemInvokedEnabled;                                      // idl:41-42, default false
    public Action<int>? ItemInvoked;
    public Action? SelectionChanged;
    public ItemsViewController? Controller;
    /// <summary>WinUI <c>ItemTransitionProvider</c> (ItemsView.idl:45, template-bound onto the inner repeater,
    /// ItemsView.xaml:30): the collection transition stamped onto each realized container root — Adds/Removes
    /// fade, Moves FLIP, 167ms decelerate (<see cref="ItemCollectionTransition"/>).</summary>
    public ItemCollectionTransition? Transition;
    public int OverscanItems = 4;
    /// <summary>Flex participation of the view (host box + viewport). 1 (default) = FILL the parent-given size — the
    /// hard-viewport path every big list wants (a Grow viewport never measures its content extent, so 10k rows stay
    /// windowed). 0 = NATURAL size: an unconstrained ItemsView measures to its layout's ContentExtent — WinUI's
    /// unconstrained ScrollView-over-ItemsRepeater shape (ItemsView.xaml template) — the gallery card shape.</summary>
    public float Grow = 1f;

    /// <summary>Legacy demo factory (compat): a single-selectable grid of labeled tiles, now riding the full
    /// L0–L3 substrate (virtualized grid + ItemContainer chrome + keyboard nav). Natural-sized (Grow 0): the demo
    /// grid sits in an auto-height gallery card, so the view measures to its grid's ContentExtent.</summary>
    public static Element Create(IReadOnlyList<string> items, int columns = 4)
        => Embed.Comp(() => new ItemsView { Items = items, Columns = columns, Grow = 0f });

    /// <summary>The full WinUI-shaped factory: templated items over any <see cref="RepeatLayout"/>.</summary>
    public static Element Create(int itemCount, Func<int, Element> itemTemplate, RepeatLayout layout,
                                 ItemsSelectionMode selectionMode = ItemsSelectionMode.Single,
                                 SelectionModel? selection = null,
                                 bool isItemInvokedEnabled = false,
                                 Action<int>? itemInvoked = null,
                                 Action? selectionChanged = null,
                                 Func<int, string>? itemText = null,
                                 Func<int, bool>? isItemEnabled = null,
                                 ItemsViewController? controller = null,
                                 int overscan = 4,
                                 ItemContainerFactory? containerFactory = null,
                                 Func<int, string>? keyOf = null,
                                 float grow = 1f,
                                 ItemCollectionTransition? transition = null)
        => Embed.Comp(() => new ItemsView
        {
            ItemCount = itemCount,
            ItemTemplate = itemTemplate,
            Layout = layout,
            HasExplicitLayout = true,
            SelectionMode = selectionMode,
            Selection = selection,
            IsItemInvokedEnabled = isItemInvokedEnabled,
            ItemInvoked = itemInvoked,
            SelectionChanged = selectionChanged,
            ItemText = itemText,
            IsItemEnabled = isItemEnabled,
            Controller = controller,
            OverscanItems = overscan,
            ContainerFactory = containerFactory,
            KeyOf = keyOf,
            Grow = grow,
            Transition = transition,
        });

    public override Element Render()
    {
        var hooks = UseContext(InputHooks.Current);
        var ownModel = UseMemo(static () => new SelectionModel());
        var current = UseSignal(-1);                       // CurrentItemIndex (idl:46-47, default −1)
        var viewportNode = UseRef(NodeHandle.Null);        // the VirtualListEl scene node (OnRealized capture)
        var subscribed = UseRef<SelectionModel?>(null);
        var typeBuffer = UseRef(new System.Text.StringBuilder());
        var typeLastMs = UseRef(0L);
        var pendingFocus = UseRef(-1);

        var model = Selection ?? ownModel;
        int count = ItemCount >= 0 ? ItemCount : Items.Count;
        model.ItemCount = count;
        model.Mode = SelectionMode;
        _ = model.Version.Value;                           // subscribe — a selection change re-skins just this window
        int cur = current.Value;                           // subscribe — current moves re-render (focus visuals)

        if (!ReferenceEquals(subscribed.Value, model))     // forward the model's event once per model instance
        {
            subscribed.Value = model;
            model.SelectionChanged += () => SelectionChanged?.Invoke();
        }

        // Resolve the layout spec → a (hoisted) IVirtualLayout. Stateful layout objects must be stable across
        // renders, so the instance is memoized on the spec's identity fields.
        RepeatLayout spec = HasExplicitLayout ? Layout : RepeatLayout.Grid(Math.Max(1, Columns), 80f, 8f);
        IVirtualLayout? layout = UseMemo<IVirtualLayout?>(
            () => spec.Kind switch
            {
                RepeatKind.Stack => new StackVirtualLayout(spec.Extent, spec.Horizontal),
                RepeatKind.Grid => new GridVirtualLayout(spec.Columns, spec.Extent, spec.Gap),
                RepeatKind.Custom => spec.CustomLayout,
                _ => null,   // Wrap/Inline — non-virtual fallback
            },
            spec.Kind, spec.Extent, spec.Gap, spec.Columns, spec.Horizontal, spec.CustomLayout ?? (object)0);
        bool horizontal = spec.Horizontal;

        var sceneRef = Context.Scene;

        // ── helpers (close over the locals above) ───────────────────────────────────────────────────

        float ViewportExtent()
        {
            if (sceneRef is null || viewportNode.Value.IsNull || !sceneRef.IsLive(viewportNode.Value)) return 0f;
            return sceneRef.TryGetScroll(viewportNode.Value, out var sc) ? (horizontal ? sc.ViewportW : sc.ViewportH) : 0f;
        }

        float CrossExtent()
        {
            if (sceneRef is null || viewportNode.Value.IsNull || !sceneRef.IsLive(viewportNode.Value)) return 0f;
            return sceneRef.TryGetScroll(viewportNode.Value, out var sc) ? (horizontal ? sc.ViewportH : sc.ViewportW) : 0f;
        }

        // The IsFocusableElement gate (SharedHelpers::IsFocusableElement; every WinUI adjacent/corner walk consults
        // it, ItemsViewInteractions.cpp:203/:321) — disabled items are skipped by keyboard navigation and typeahead.
        bool ItemEnabled(int i) => IsItemEnabled?.Invoke(i) != false;

        // First enabled item walking from <paramref name="start"/> by <paramref name="step"/> (±1); −1 = none.
        int FirstEnabled(int start, int step)
        {
            for (int i = start; (uint)i < (uint)count; i += step)
                if (ItemEnabled(i)) return i;
            return -1;
        }

        // Adjacent index walk that skips disabled items (the cpp GetAdjacentFocusableElementByIndex shape,
        // ItemsViewInteractions.cpp:296-330): step until an enabled item; hitting the edge stays put.
        int StepEnabled(int from, int step)
        {
            if (step == 0) return from;
            for (int i = from + step; (uint)i < (uint)count; i += step)
                if (ItemEnabled(i)) return i;
            return from;
        }

        // The dispatcher's SetScrollOffset idiom (InputDispatcher.cs:388-433): write Offset+Target, apply the
        // layout-free -offset transform, dirty the realize window, request a render.
        void BringIntoView(int index, float alignmentRatio)
        {
            if (sceneRef is null || layout is null || (uint)index >= (uint)count) return;
            var vp = viewportNode.Value;
            if (vp.IsNull || !sceneRef.IsLive(vp) || !sceneRef.HasScroll(vp)) return;   // non-virtual host: no-op
            ref ScrollState sc = ref sceneRef.ScrollRef(vp);
            float viewport = horizontal ? sc.ViewportW : sc.ViewportH;
            float cross = horizontal ? sc.ViewportH : sc.ViewportW;
            var rect = layout.ItemRect(index, cross);
            float itemStart = horizontal ? rect.X : rect.Y;
            float itemExtent = horizontal ? rect.W : rect.H;
            float offset = horizontal ? sc.OffsetX : sc.OffsetY;

            float target;
            if (float.IsNaN(alignmentRatio))
            {
                // Minimal scroll (default BringIntoViewOptions): only move when the item is outside the viewport.
                if (itemStart < offset) target = itemStart;
                else if (itemStart + itemExtent > offset + viewport) target = itemStart + itemExtent - viewport;
                else return;
            }
            else
            {
                // Home/End edge alignment (ItemsViewInteractions.cpp:1013-1016).
                target = itemStart - alignmentRatio * MathF.Max(0f, viewport - itemExtent);
            }

            float content = horizontal ? sc.ContentW : sc.ContentH;
            target = Math.Clamp(target, 0f, MathF.Max(0f, content - viewport));
            if (horizontal) { sc.OffsetX = target; sc.TargetX = target; }
            else { sc.OffsetY = target; sc.TargetY = target; }

            var contentNode = sc.ContentNode;
            if (!contentNode.IsNull && sceneRef.IsLive(contentNode))
            {
                sceneRef.Paint(contentNode).LocalTransform = Affine2D.Translation(horizontal ? -target : 0f, horizontal ? 0f : -target);
                sceneRef.Mark(contentNode, NodeFlags.TransformDirty | NodeFlags.PaintDirty);
            }
            sceneRef.Mark(vp, NodeFlags.VirtualRangeDirty);
            Context.RequestRerender();
        }

        // Keyboard focus the REALIZED container for an index: ord = index − FirstRealized → the ord-th window child.
        // Non-virtual hosts (Wrap/Inline fallback) have no scroll state: every container is a direct child of the
        // captured host box, so ord == index.
        void FocusIndex(int index, bool visual)
        {
            var focusNode = hooks.FocusNode;
            if (sceneRef is null || focusNode is null) return;
            var vp = viewportNode.Value;
            if (vp.IsNull || !sceneRef.IsLive(vp)) return;
            NodeHandle first;
            int ord;
            if (sceneRef.TryGetScroll(vp, out var sc))
            {
                ord = index - sc.FirstRealized;
                if (ord < 0 || index >= sc.LastRealized) return;
                first = sceneRef.FirstChild(sc.ContentNode);
            }
            else
            {
                ord = index;
                first = sceneRef.FirstChild(vp);
            }
            var n = first;
            for (int k = 0; k < ord && !n.IsNull; k++) n = sceneRef.NextSibling(n);
            if (!n.IsNull && sceneRef.IsLive(n)) focusNode(n, visual);
        }

        // Edge auto-scroll seam (drag reorder near the viewport edge): nudge Offset/Target by a clamped delta.
        void ScrollByDelta(float delta)
        {
            if (sceneRef is null) return;
            var vp = viewportNode.Value;
            if (vp.IsNull || !sceneRef.IsLive(vp) || !sceneRef.HasScroll(vp)) return;
            ref ScrollState sc = ref sceneRef.ScrollRef(vp);
            float viewport = horizontal ? sc.ViewportW : sc.ViewportH;
            float content = horizontal ? sc.ContentW : sc.ContentH;
            float offsetNow = horizontal ? sc.OffsetX : sc.OffsetY;
            float target = Math.Clamp(offsetNow + delta, 0f, MathF.Max(0f, content - viewport));
            if (target == offsetNow) return;
            if (horizontal) { sc.OffsetX = target; sc.TargetX = target; }
            else { sc.OffsetY = target; sc.TargetY = target; }
            var contentNode = sc.ContentNode;
            if (!contentNode.IsNull && sceneRef.IsLive(contentNode))
            {
                sceneRef.Paint(contentNode).LocalTransform = Affine2D.Translation(horizontal ? -target : 0f, horizontal ? 0f : -target);
                sceneRef.Mark(contentNode, NodeFlags.TransformDirty | NodeFlags.PaintDirty);
            }
            sceneRef.Mark(vp, NodeFlags.VirtualRangeDirty);
            Context.RequestRerender();
        }

        void MoveCurrent(int next, bool ctrl, bool shift, float alignmentRatio = float.NaN)
        {
            if ((uint)next >= (uint)count || !ItemEnabled(next)) return;   // disabled = not focusable (cpp:203/:321)
            BringIntoView(next, alignmentRatio);
            model.OnFocusedAction(next, ctrl, shift);      // selection follows keyboard per mode (SelectorBase trio)
            if (current.Peek() != next)
            {
                pendingFocus.Value = next;                 // focus the (re-realized) container post-render/layout
                current.Value = next;
            }
            else
            {
                // No re-render coming — focus the realized node now. The latch MUST be cleared here: a stale
                // pendingFocus would re-fire on the NEXT current change (e.g. a click on another item) and yank
                // keyboard-visual focus back to this index (WinUI focuses synchronously and keeps no latch,
                // SetFocusElementIndex, ItemsViewInteractions.cpp:1313-1354).
                pendingFocus.Value = -1;
                FocusIndex(next, visual: true);
            }
        }

        int NavigateIndex(int from, int dx, int dy)
        {
            if (count == 0) return -1;
            if (from < 0) return FirstEnabled(0, +1);   // first arrow with no current → first focusable item
            switch (spec.Kind)
            {
                case RepeatKind.Stack:
                    // Index-based on the layout's scroll orientation only (ItemsViewInteractions.cpp:1051-1067);
                    // the walk skips disabled items (IsFocusableElement gate).
                    int dStack = spec.Horizontal ? dx : dy;
                    return dStack == 0 ? from : StepEnabled(from, dStack);
                case RepeatKind.Grid:
                    // Left/Right = index ±1 (may wrap rows — the cpp index-based path); Up/Down = ±columns
                    // (column-railed), both walking past disabled items.
                    return StepEnabled(from, dx != 0 ? dx : dy * spec.Columns);
                case RepeatKind.Custom:
                    return NavigateGeometric(from, dx, dy);
                default:
                    // Wrap/Inline (non-virtual) — linear index step on any arrow, skipping disabled.
                    return StepEnabled(from, dx + dy);
            }
        }

        // Direction-based nearest-center scan for custom layouts (the cpp GetAdjacentFocusableElementByDirection
        // shape, bounded to ±GeometricScan candidates).
        int NavigateGeometric(int from, int dx, int dy)
        {
            float cross = CrossExtent();
            if (cross <= 0f || layout is null)   // pre-layout fallback: index step (skipping disabled)
                return StepEnabled(from, dx + dy);
            var r = layout.ItemRect(from, cross);
            float cx = r.X + r.W * 0.5f, cy = r.Y + r.H * 0.5f;
            int best = from;
            float bestDist = float.MaxValue;
            int lo = Math.Max(0, from - GeometricScan), hi = Math.Min(count, from + GeometricScan + 1);
            for (int i = lo; i < hi; i++)
            {
                if (i == from || !ItemEnabled(i)) continue;   // IsFocusableElement gate (cpp:203)
                var c = layout.ItemRect(i, cross);
                float ix = c.X + c.W * 0.5f, iy = c.Y + c.H * 0.5f;
                bool inDirection =
                    (dx < 0 && ix < cx - 0.5f) || (dx > 0 && ix > cx + 0.5f) ||
                    (dy < 0 && iy < cy - 0.5f) || (dy > 0 && iy > cy + 0.5f);
                if (!inDirection) continue;
                // Favor the movement axis strongly, then the perpendicular offset (rail to the same column/row).
                float d = dx != 0
                    ? MathF.Abs(ix - cx) * 4096f + MathF.Abs(iy - cy)
                    : MathF.Abs(iy - cy) * 4096f + MathF.Abs(ix - cx);
                if (d < bestDist) { bestDist = d; best = i; }
            }
            return best;
        }

        void OnRootKey(KeyEventArgs e)
        {
            if (count == 0) return;
            bool ctrl = e.Ctrl, shift = e.Shift;
            int from = current.Peek();
            switch (e.KeyCode)
            {
                case Keys.A when ctrl:
                    // Ctrl+A — Multiple/Extended only (ItemsViewInteractions.cpp:35-50).
                    if (SelectionMode is ItemsSelectionMode.Multiple or ItemsSelectionMode.Extended)
                    {
                        model.SelectAll();
                        e.Handled = true;
                    }
                    return;
                case Keys.Home or Keys.End:
                {
                    // Scroll the list end into view corner-aligned (item 0 / count−1, alignment ratios 0/1,
                    // cpp:1009-1016), then make the first/last FOCUSABLE element current — WinUI focuses
                    // FindFirst/LastFocusableElement, not blindly index 0/count−1 (cpp:1028-1040).
                    bool home = e.KeyCode == Keys.Home;
                    BringIntoView(home ? 0 : count - 1, home ? 0f : 1f);
                    int t = home ? FirstEnabled(0, +1) : FirstEnabled(count - 1, -1);
                    if (t >= 0) MoveCurrent(t, ctrl, shift);   // minimal scroll keeps the edge alignment above
                    e.Handled = true;
                    return;
                }
                case Keys.Left or Keys.Right or Keys.Up or Keys.Down:
                {
                    int dx = e.KeyCode == Keys.Left ? -1 : e.KeyCode == Keys.Right ? 1 : 0;
                    int dy = e.KeyCode == Keys.Up ? -1 : e.KeyCode == Keys.Down ? 1 : 0;
                    int next = NavigateIndex(from, dx, dy);
                    if (next >= 0 && next != from) MoveCurrent(next, ctrl, shift);
                    e.Handled = true;   // nav keys never fall through to an outer scroller (cpp:806-807)
                    return;
                }
                case Keys.PageUp or Keys.PageDown:
                {
                    // Railed page navigation (ItemsViewInteractions.cpp:1103-1242): move one viewport from the
                    // current item's main-axis position while keeping the current cross-axis rail. If the jump
                    // falls past the realized/content edge, unrail to the first/last focusable element.
                    if (layout is null) return;
                    float cross = CrossExtent(); float page = ViewportExtent();
                    if (cross <= 0f || page <= 0f) return;
                    bool pageUp = e.KeyCode == Keys.PageUp;
                    int fromIdx = Math.Max(0, from);
                    var rect = layout.ItemRect(fromIdx, cross);
                    float rail = horizontal ? rect.Y + rect.H * 0.5f : rect.X + rect.W * 0.5f;
                    float main = horizontal ? rect.X : rect.Y;
                    int target = PageTargetNear(main + (pageUp ? -page : page), page, rail, cross);
                    if (target == from || target < 0)
                        target = pageUp ? FirstEnabled(0, +1) : FirstEnabled(count - 1, -1);
                    if (target >= 0 && target != from) MoveCurrent(target, ctrl, shift);
                    e.Handled = true;
                    return;
                }
            }
        }

        // The GetItemInternal shape (cpp:1146-1155) bounded to the control side: the nearest focusable item to a
        // one-page target main-axis position, preserving the keyboardNavigationReference cross-axis rail.
        int PageTargetNear(float targetMain, float windowExtent, float rail, float cross)
        {
            if (layout is null) return -1;
            float windowStart = MathF.Max(0f, targetMain - windowExtent * 0.5f);
            layout.Window(count, cross, windowExtent, MathF.Max(0f, windowStart), 0, out int f, out int l);
            int best = -1;
            float bestCross = float.MaxValue, bestMain = float.MaxValue;
            for (int i = Math.Max(0, f); i < Math.Min(count, l); i++)
            {
                if (!ItemEnabled(i)) continue;                 // forFocusableItemsOnly (cpp:1154)
                var r = layout.ItemRect(i, cross);
                float s = horizontal ? r.X : r.Y, ext = horizontal ? r.W : r.H;
                if (s < windowStart - 0.5f || s + ext > windowStart + windowExtent + 0.5f) continue;
                float cc = horizontal ? r.Y + r.H * 0.5f : r.X + r.W * 0.5f;
                float cd = MathF.Abs(cc - rail), md = MathF.Abs(s - targetMain);
                if (cd < bestCross - 0.5f || (cd < bestCross + 0.5f && md < bestMain))
                {
                    bestCross = cd; bestMain = md; best = i;
                }
            }
            return best;
        }

        void OnRootChar(CharEventArgs e)
        {
            if (count == 0 || e.Codepoint < 32) return;
            Func<int, string>? textOf = ItemText;
            if (textOf is null && Items.Count == count) { var items = Items; textOf = i => items[i]; }
            if (textOf is null) return;
            long now = Environment.TickCount64;
            var buf = typeBuffer.Value;
            if (now - typeLastMs.Value > (long)TypeaheadResetMs) buf.Clear();
            // Space never STARTS a search — it is selection-only in WinUI (the SpaceKey trigger,
            // ItemContainer.cpp:548-551; the engine routes chars independently of KeyDown.Handled) and the Win32
            // list rule keeps it out of an empty typeahead buffer. Mid-prefix spaces still match ("Bell La…").
            if (e.Codepoint == 32 && buf.Length == 0) return;
            typeLastMs.Value = now;
            buf.Append(char.ConvertFromUtf32(e.Codepoint));
            string prefix = buf.ToString();
            int start = Math.Max(0, current.Peek());
            for (int k = 1; k <= count; k++)
            {
                int i = (start + k) % count;
                if (!ItemEnabled(i)) continue;   // disabled items can't take current/selection
                if (textOf(i).StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    MoveCurrent(i, ctrl: false, shift: false);
                    e.Handled = true;
                    return;
                }
            }
        }

        // ItemContainer interaction → selector + ItemInvoked, per ItemsViewInteractions.cpp:820-919 and the
        // CanRaiseItemInvoked matrix (ItemsView.cpp:423-426).
        void OnItemInteraction(int i, ItemContainerTrigger trigger, KeyModifiers mods)
        {
            bool ctrl = (mods & KeyModifiers.Ctrl) != 0, shift = (mods & KeyModifiers.Shift) != 0;
            bool pointer = trigger is ItemContainerTrigger.Tap or ItemContainerTrigger.DoubleTap;
            // Pointer interactions bring a partially-visible item fully into view: ProcessInteraction passes
            // startBringIntoView = (focusState == FocusState::Pointer) into SetCurrentElementIndex →
            // element.StartBringIntoView() with default (minimal-scroll) options (ItemsViewInteractions.cpp:894-895,
            // :1340-1345). Keyboard triggers don't (the nav keys handle their own scrolling).
            if (pointer) BringIntoView(i, float.NaN);
            if (current.Peek() != i) current.Value = i;
            // Roving tab stop: a press on a non-current container can't take pointer focus at the dispatch edge
            // (only the current container is in the tab order), so land focus here — FocusState::Pointer shows no
            // focus ring (visual: false). Key triggers arrive with the container already focused.
            if (pointer) FocusIndex(i, visual: false);
            // Every interaction runs the selector — WinUI raises ProcessInteraction per PointerReleased
            // (ItemsViewInteractions.cpp:831-834), so a double-click's SECOND release toggles AGAIN in Multiple
            // mode (net unchanged, MultipleSelector.cpp:55-62) and re-selects idempotently in Single/Extended.
            model.OnInteractedAction(i, ctrl, shift);
            if (IsItemInvokedEnabled && ItemInvoked is not null)
            {
                bool cannotInvoke =
                    (SelectionMode == ItemsSelectionMode.None && trigger == ItemContainerTrigger.DoubleTap) ||
                    (SelectionMode != ItemsSelectionMode.None &&
                     trigger is ItemContainerTrigger.Tap or ItemContainerTrigger.SpaceKey);
                if (!cannotInvoke) ItemInvoked(i);
            }
        }

        if (Controller is { } ctl)
        {
            // WinUI StartBringItemIntoView scrolls/realizes but does NOT move focus (ItemsView.cpp:119-127).
            ctl.BringIntoViewImpl = BringIntoView;
            ctl.GetCurrent = current.Peek;
            ctl.Selection = model;
            ctl.ScrollByImpl = ScrollByDelta;
        }

        // Post-layout: focus the (now realized) keyboard-current container so the engine ring lands on it.
        UseLayoutEffect(() =>
        {
            int target = pendingFocus.Value;
            if (target >= 0) { pendingFocus.Value = -1; FocusIndex(target, visual: true); }
        }, cur);

        // ── item template: content wrapped in the WinUI ItemContainer chrome (or the L4 skin's chrome) ──
        bool multi = SelectionMode == ItemsSelectionMode.Multiple;
        Func<int, Element> content = ItemTemplate ?? DefaultTile;
        ItemContainerFactory? skin = ContainerFactory;

        // TabNavigation="Once" (ItemsView.xaml:7): the view exposes ONE tab stop — the keyboard-current container.
        // Tab-in with no current lands on the selected item when SelectionMode is Single (the GettingFocus redirect
        // conditions, ItemsViewInteractions.cpp:662-684), else on the first focusable item (GetCornerFocusableItem,
        // cpp:705-710). Implemented as a roving TabStop (the RadioButtons IsTabStop pattern).
        int tabStop = cur;
        if (tabStop < 0 && SelectionMode == ItemsSelectionMode.Single)
        {
            int sel = model.FirstSelectedIndex;
            if (sel >= 0 && sel < count && ItemEnabled(sel)) tabStop = sel;
        }
        if (tabStop < 0) tabStop = FirstEnabled(0, +1);

        Func<int, Element> containerTemplate = i =>
        {
            bool selected = model.IsSelected(i);
            bool enabled = IsItemEnabled?.Invoke(i) ?? true;
            Action<ItemContainerTrigger, KeyModifiers> interact = (t, m) => OnItemInteraction(i, t, m);
            Action<bool> focusChanged = got => { if (got && current.Peek() != i) current.Value = i; };
            return skin is not null
                ? skin(i, content(i), new ItemChromeState(selected, enabled, multi, multi && selected, i == cur),
                       interact, focusChanged)
                : ItemContainer.Build(
                    content(i),
                    isSelected: selected,
                    onInteraction: interact,
                    isEnabled: enabled,
                    showSelectionCheckbox: multi,
                    isChecked: multi && selected,
                    onFocusChanged: focusChanged,
                    isTabStop: i == tabStop);
        };

        // ItemTransitionProvider (ItemsView.idl:45 → the inner repeater, ItemsView.xaml:30): stamp the collection
        // transition onto each realized container root. The non-virtual fallback passes it to ItemsRepeater instead.
        Func<int, Element> realizeTemplate = Transition is { } tr
            ? Repeater.WrapTransition(containerTemplate, tr.ToSpec())
            : containerTemplate;

        Element itemsHost = layout is not null
            ? new VirtualListEl
            {
                ItemCount = count,
                Layout = layout,
                RenderItem = realizeTemplate,
                KeyOf = KeyOf,
                Overscan = OverscanItems,
                Horizontal = horizontal,
                // Grow rides through to the viewport: 1 = fill the parent (hard viewport, never content-measured);
                // 0 = natural — FlexLayout.MeasureViewport sizes a non-flexing viewport to the layout's ContentExtent
                // (the gallery card shape; D1).
                Grow = Grow,
                OnRealized = h => viewportNode.Value = h,
            }
            // Wrap/Inline small-collection fallback (always a BoxEl) — capture the host box so FocusIndex can
            // walk its children (ord == index; no scroll state).
            : ((BoxEl)Repeater.ItemsRepeater(count, containerTemplate, in spec, keyOf: KeyOf, transition: Transition))
                with { OnRealized = h => viewportNode.Value = h };

        return new BoxEl
        {
            // The root stacks along the LIST axis (D1 hygiene): a vertical view is a column so Grow distributes the
            // missing axis to the viewport; a horizontal shelf stays a row. Cross axis fills via the default stretch.
            Direction = horizontal ? (byte)0 : (byte)1,
            Grow = Grow,
            OnKeyDown = OnRootKey,      // bubbles up from the focused ItemContainer (dispatcher key routing)
            OnCharInput = OnRootChar,   // typeahead
            Children = [itemsHost],
        };
    }

    /// <summary>The legacy demo tile (label centered in the grid cell) — used when no <see cref="ItemTemplate"/> is set.</summary>
    private Element DefaultTile(int i)
        => new BoxEl
        {
            Grow = 1f,
            AlignItems = FlexAlign.Center,
            Justify = FlexJustify.Center,
            Children = [new TextEl(i < Items.Count ? Items[i] : string.Empty) { Size = 13f, Color = Tok.TextPrimary }],
        };
}
