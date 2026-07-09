using FluentGpu.Animation;
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
    internal Action<int, float, bool>? BringIntoViewImpl;
    internal Func<int>? GetCurrent;
    internal Action<float>? ScrollByImpl;
    internal Func<float>? GetOffsetImpl;

    /// <summary>The live scroll offset along the view's scroll axis (DIP; 0 before the viewport realizes / for a
    /// non-virtual host). The scroll-anchoring read: pair with <see cref="ScrollBy"/> to keep the visible content
    /// stationary across a data insert/remove above the viewport.</summary>
    public float ScrollOffset => GetOffsetImpl?.Invoke() ?? 0f;

    /// <summary>The live selection model — Select/Deselect/IsSelected/SelectAll/DeselectAll/InvertSelection
    /// (ItemsView.idl:53-58) are its methods; range-based, so they never realize items.</summary>
    public SelectionModel? Selection { get; internal set; }

    /// <summary>WinUI <c>CurrentItemIndex</c> (idl:46-47, default −1) — the keyboard-current item.</summary>
    public int CurrentItemIndex => GetCurrent?.Invoke() ?? -1;

    /// <summary>WinUI <c>StartBringItemIntoView(index, BringIntoViewOptions)</c> (idl:52): realizes the target by
    /// scrolling the virtualized viewport. <paramref name="alignmentRatio"/> NaN = minimal scroll (the default
    /// BringIntoViewOptions); 0 = align item start to viewport start, 1 = end to end (the Home/End ratios,
    /// ItemsViewInteractions.cpp:1013-1016). <paramref name="animate"/> true = SMOOTH-scroll to the target (the
    /// ScrollIntegrator eases the offset, matching WinUI's <c>BringIntoViewOptions.AnimationDesired</c>); false (default) =
    /// snap immediately. Animated paging (e.g. a PagedShelf's chevrons) passes true.</summary>
    public void StartBringItemIntoView(int index, float alignmentRatio = float.NaN, bool animate = false)
        => BringIntoViewImpl?.Invoke(index, alignmentRatio, animate);

    /// <summary>Nudge the virtualized viewport by <paramref name="delta"/> DIP along its scroll axis (clamped).
    /// The drag-reorder EDGE AUTO-SCROLL seam: a composing list (ListView) calls this while the pointer drags near
    /// the viewport edge (the plan's E5-L3 edge auto-scroll in virtualized lists). No-op for non-virtual hosts.</summary>
    public void ScrollBy(float delta) => ScrollByImpl?.Invoke(delta);
}

/// <summary>Per-item visual state handed to a custom <see cref="ItemContainerFactory"/> (the L4 skin seam).</summary>
public readonly record struct ItemChromeState(
    bool IsSelected, bool IsEnabled, bool ShowCheckbox, bool IsChecked, bool IsCurrent);

/// <summary>
/// Custom item-container factory — the E11-L4 SKIN seam: the List/Grid presets + TreeView supply their WinUI item
/// chrome (ListViewItemPresenter / GridView dual-border / TreeViewItem row) around the engine's ONE selection + keyboard
/// substrate. The returned BoxEl must wire <paramref name="onInteraction"/> (press/Enter/Space → the selector) and
/// <paramref name="onFocusChanged"/> (keyboard-current tracking), and should be <c>Focusable</c> so the engine focus
/// ring lands on items. Null ⇒ the default WinUI <see cref="ItemContainer"/> chrome.
/// </summary>
public delegate BoxEl ItemContainerFactory(
    int index, Element content, ItemChromeState state,
    Action<ItemContainerTrigger, KeyModifiers> onInteraction, Action<bool> onFocusChanged);

/// <summary>
/// THE premiere collection control (a deliberate, documented SUPERSET of WinUI <c>ItemsView</c>,
/// controls\dev\ItemsView) — E11-L3: the L2 repeater substrate + <see cref="SelectionModel"/> + the
/// <see cref="SelectorVisual"/> chrome presets + keyboard navigation/typeahead + StartBringItemIntoView + BUILT-IN
/// drag-reorder, composed. <see cref="List(System.Collections.Generic.IReadOnlyList{string}, Signal{int}, System.Action{int})"/>
/// and <see cref="Grid(System.Collections.Generic.IReadOnlyList{string}, int, float)"/> are the built-in presets (the
/// former ListView/GridView controls, folded onto ItemsView); the goal is no WinUI-style capability cliffs — every
/// layout × every selection mode × every selector × reorder works in any combination.
///
/// Three pluggable axes, each available with every other (the superset over WinUI's fixed ListView/GridView pairings):
/// • LAYOUT preset — any <see cref="RepeatLayout"/>: Stack, Grid, HorizontalStrip, LinedFlow (the WinUI photo-wall),
///   Measured, SpanGrid or a custom seam layout, over ONE virtualized viewport (<see cref="VirtualListEl"/>).
/// • SELECTION mode — None/Single/Multiple/Extended (<see cref="SelectionModel"/>, range-based: decoupled from realization).
/// • SELECTOR VISUAL — <see cref="Selector"/>: AccentPill (the WinUI ListView accent bar), Check (GridView corner check),
///   FullRow, Border (the default <see cref="ItemContainer"/>), None, or a custom <see cref="ContainerFactory"/> hook.
/// Every item template is wrapped in the chosen selector chrome (selection visuals, pointer states, multi-select
/// checkbox). Reorder (the WinUI live "siblings part to make room") rides the ONE substrate via
/// <see cref="ItemDisplacement"/> + <see cref="DisplacementVersion"/> — a capability WinUI's own ItemsView lacks.
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
    // Reorder-displacement glide duration. WinUI's MoveItemsForLiveReorder uses TAS_REPOSITION timing, which is a
    // build-only theme artifact (no readable token), so use the Reposition-class ControlNormal (250ms) with
    // FluentDecelerate — the closest documented "reposition" cadence (Common_themeresources ControlNormalAnimationDuration).
    private const float DisplacementAnimMs = Motion.ControlNormal;   // 250ms
    private const float DisplacementEpsilon = 0.5f;   // sub-pixel: don't re-seed a track that is already at target

    /// <summary>Default list slot stride: ListViewItemMinHeight 40 + the 2+2 backplate margins {4,2,4,2}; cp1.a pins 8×44.
    /// (The default main-axis extent for <see cref="List(int, Func{int, Element}, ItemsSelectionMode, SelectionModel, Action{int}, Action{int}, Action{int}, bool, Action{int, int}, Func{int, string}, Func{int, bool}, ItemsViewController, Func{int, string}, float, float, float, float)"/>;
    /// the uniform virtualization stride for the List preset.)</summary>
    public const float ListItemExtent = 44f;

    // ── legacy simple surface (kept source-compatible: ItemsViewPage / MiscPages.cs uses Create(items, columns)) ──
    public IReadOnlyList<string> Items = [];
    public int Columns = 4;

    // ── full surface ──
    /// <summary>Item count when an <see cref="ItemTemplate"/> drives content (−1 ⇒ <see cref="Items"/>.Count).</summary>
    public int ItemCount = -1;
    public IReadSignal<int>? ItemCountSignal;
    /// <summary>The item CONTENT template (wrapped in an <see cref="ItemContainer"/> per item).</summary>
    public Func<int, Element>? ItemTemplate;
    /// <summary>The SIGNALS-FIRST bound row template (<see cref="CreateBound"/>): the row is built ONCE per slot from a
    /// <see cref="RowScope"/> of per-row read-signals, then recycled by a signal write (no rebuild, no remount, so a
    /// row containing a Component/Marquee/bound leaf never replays its Enter transition). Mutually exclusive with
    /// <see cref="ItemTemplate"/>/<see cref="ContainerFactory"/>; gated by <see cref="BoundMode"/>.</summary>
    public Func<RowScope, Element>? RowTemplate;
    /// <summary>True ⇒ the bound realize path (<see cref="RowTemplate"/> + <see cref="VirtualListEl.RowBind"/>): rows are
    /// persistent slots; selection/current/now-playing re-skin in place via per-row binds, never a list re-render.</summary>
    public bool BoundMode;
    /// <summary>Opt-in cold-mount stagger for the bound realize path (see <see cref="VirtualListEl.StaggerColdRealize"/>):
    /// a heavy list realizes its initial window a few rows/frame instead of all at once, killing the mount spike.</summary>
    public bool StaggerColdRealize;
    /// <summary>Typeahead text per item (defaults to <see cref="Items"/> when it backs the view).</summary>
    public Func<int, string>? ItemText;
    /// <summary>Per-item enabled gate (disabled items dim to 0.3 and don't interact).</summary>
    public Func<int, bool>? IsItemEnabled;
    /// <summary>L4 skin seam: replaces the default <see cref="ItemContainer"/> chrome (the List/Grid presets + TreeView).</summary>
    // Per-item chrome SKIN goes through the ContainerFactory/SelectorVisual seam; per-item VARIATION goes through the
    // PartDelta value seam (fill/fg/opacity/corner/padding/glyph as values, applied during construction — shape-stable,
    // 0-alloc, CI-enforced; docs/guide/control-fidelity.md §6).
    public ItemContainerFactory? ContainerFactory;
    /// <summary>Per-item VARIATION (fill/foreground/opacity/corner/padding/glyph as VALUES) baked into the chrome
    /// during construction — the legal per-item-customization seam (supersedes per-item TemplateParts in recycled
    /// scroll paths). Resolved ONCE per realized item and passed by value into every selector builder / ItemContainer.
    /// Must be a pure-value Func (no new/box/LINQ per call) — CI-enforced (control-fidelity §6).</summary>
    public Func<int, ItemChromeState, PartDelta>? PartDelta;
    /// <summary>The built-in selector-VISUAL preset (the user-pickable item chrome). Default <see cref="SelectorVisual.Border"/>
    /// = the existing <see cref="ItemContainer"/> chrome (current behavior). When <see cref="ContainerFactory"/> is set it
    /// wins (a custom skin overrides the preset); otherwise this picks one of the <see cref="SelectorVisuals"/> builders —
    /// AccentPill (ListView accent bar), Check (GridView corner check), FullRow, None — so any selector works with any
    /// layout × any selection mode (no WinUI capability cliffs). The List preset uses AccentPill, the Grid preset uses Check.</summary>
    public SelectorVisual Selector = SelectorVisual.Border;
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

    // ── drag-reorder displacement channel (the WinUI "siblings part to make room" over the positional recycler) ──
    /// <summary>Resting-index → target displacement in DIP at the current dwell-committed reorder target. The owning
    /// reorder substrate (the ListView/GridView/TreeView preset, via ReorderList.OffsetFor / OffsetFor2D over RESTING
    /// indices) supplies it; returns (0,0) for the dragged item and every non-displaced item. ItemsView seeds each
    /// realized row's AnimEngine TranslateX/Y track from this so displaced siblings glide aside (WinUI
    /// MoveItemsForLiveReorder), and the motion survives recycling because it is re-seeded each realize.</summary>
    public Func<int, (float dx, float dy)>? ItemDisplacement;
    /// <summary>Bumped by the owner on every drag-delta / dwell-commit; ItemsView subscribes (its <c>.Value</c>) so the
    /// frozen-ComponentEl boundary (Reconciler.cs:220-221 — a parent bump alone never re-renders this autonomous
    /// component) is crossed and the displacement edge-trigger re-seeds. This is the WinUI on-timer reorder cadence, NOT
    /// per frame.</summary>
    public IReadSignal<int>? DisplacementVersion;
    /// <summary>OPTIONAL redundant hint: the resting index currently pointer-dragged. The displacement seed already
    /// skips the dragged node UNCONDITIONALLY via its <see cref="NodeFlags.DragGhost"/> scene flag (its translate is
    /// owned by the DragController and must never be animated), so this is needed only by callers whose drag does not
    /// flow through that flag. NOTE: returning (0,0) from <see cref="ItemDisplacement"/> for the dragged item does NOT
    /// by itself make the seed a no-op — the seed animates the row's LIVE translate back to that 0, which is exactly
    /// the ownership conflict the DragGhost-flag skip prevents.</summary>
    public IReadSignal<int>? DraggedSlot;
    /// <summary>OPTIONAL FLIP start override for the displacement seed: when non-null for a resting index, that row's
    /// translate animation starts from THIS value instead of its live translate — the "first" of first-invert-play, so a
    /// data reorder can glide surviving rows old-position → new-position in the SAME bump that lands the new order
    /// (return the old-minus-new residual; the target stays <see cref="ItemDisplacement"/>, normally (0,0)). Null (the
    /// delegate or its per-item result) ⇒ the live translate — the velocity-continuous drag-reorder retarget.</summary>
    public Func<int, (float dx, float dy)?>? ItemFlipFrom;
    /// <summary>OPTIONAL per-row opacity seed consumed by the SAME displacement bump: non-null ⇒ animate the row's
    /// Opacity from the value to 1 after the per-row delay (an added-row ease-in with a stagger, without a slot remount —
    /// bound slots recycle, so mount-keyed Enter can't express this). The delay also staggers the row's translate seed.</summary>
    public Func<int, (float from, float delayMs)?>? ItemFadeFrom;

    public int OverscanItems = 4;
    /// <summary>Flex participation of the view (host box + viewport). 1 (default) = FILL the parent-given size — the
    /// hard-viewport path every big list wants (a Grow viewport never measures its content extent, so 10k rows stay
    /// windowed). 0 = NATURAL size: an unconstrained ItemsView measures to its layout's ContentExtent — WinUI's
    /// unconstrained ScrollView-over-ItemsRepeater shape (ItemsView.xaml template) — the gallery card shape.</summary>
    public float Grow = 1f;

    /// <summary>Scroll-edge cues for the virtualized viewport (controls.md §8.3) — a surface-colour fade at an
    /// overflowing edge so a long list reads as scrollable. <see cref="ScrollEdgeCues.Auto"/> (default) → the app
    /// default (ON, fade-only); <see cref="ScrollEdgeCues.None"/> opts out. Forwarded onto the built VirtualListEl.</summary>
    public ScrollEdgeCues EdgeCues = ScrollEdgeCues.Auto;
    /// <summary>Premium alpha-mask edge fade: feather the content's OWN alpha at the overflowing edges. Unlike the
    /// surface-colour <see cref="EdgeCues"/> fade (which needs an opaque plate to dissolve into and self-skips over a
    /// gradient wash), this works over ANY background. One offscreen RT for the viewport. Forwarded onto the built
    /// VirtualListEl. Default false.</summary>
    public bool AutoEdgeFade;
    /// <summary>Never draw the conscious scrollbar for the virtualized viewport (a paged surface navigates by its
    /// pager, not a draggable bar). Forwarded onto the built VirtualListEl. Default false.</summary>
    public bool SuppressScrollBar;
    /// <summary>Scroll-position restoration key (see <see cref="VirtualListEl.ScrollKey"/>): a stable per-content identity
    /// so a revisit lands at the saved row on the first realized window. Forwarded onto the built VirtualListEl.</summary>
    public string? ScrollKey;
    public (Func<ScrollGeometry, long> Project, Action<ScrollGeometry> Action)? OnScrollGeometryChanged;

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
                                 ItemCollectionTransition? transition = null,
                                 SelectorVisual selector = SelectorVisual.Border,
                                 Func<int, (float, float)>? itemDisplacement = null,
                                 IReadSignal<int>? displacementVersion = null,
                                 IReadSignal<int>? draggedSlot = null,
                                 Func<int, ItemChromeState, PartDelta>? partDelta = null,
                                 bool suppressScrollBar = false,
                                 bool autoEdgeFade = false,
                                 string? scrollKey = null,
                                 (Func<ScrollGeometry, long> Project, Action<ScrollGeometry> Action)? onScrollGeometryChanged = null)
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
            SuppressScrollBar = suppressScrollBar,
            ScrollKey = scrollKey,
            AutoEdgeFade = autoEdgeFade,
            Transition = transition,
            Selector = selector,
            ItemDisplacement = itemDisplacement,
            DisplacementVersion = displacementVersion,
            DraggedSlot = draggedSlot,
            PartDelta = partDelta,
            OnScrollGeometryChanged = onScrollGeometryChanged,
        });

    /// <summary>The SIGNALS-FIRST bound factory: the same WinUI ItemsView substrate (selection model, keyboard nav,
    /// typeahead, invoke, controller, reorder) but rows are PERSISTENT bound slots instead of a rebuilt-per-index
    /// template. <paramref name="rowTemplate"/> is invoked ONCE per visible slot with a <see cref="RowScope"/> (the
    /// index SIGNAL + reactive IsSelected/IsCurrent/IsEnabled predicates + the interaction/focus callbacks) and must
    /// return the COMPLETE slot root — express everything that varies by index as a bind that reads the scope, and wrap
    /// content in <see cref="SelectorVisualsBound"/> chrome (or a custom skin). Scrolling/selection/now-playing then
    /// re-skin in place via signal writes — no list re-render, no row rebuild, no Enter-transition replay. Requires a
    /// VIRTUAL layout (Stack/Grid/Custom); the small-collection Wrap/Inline fallback has no bound path.</summary>
    public static Element CreateBound(int itemCount, Func<RowScope, Element> rowTemplate, RepeatLayout layout,
                                      ItemsSelectionMode selectionMode = ItemsSelectionMode.Single,
                                      SelectionModel? selection = null,
                                      bool isItemInvokedEnabled = false,
                                      Action<int>? itemInvoked = null,
                                      Action? selectionChanged = null,
                                      Func<int, string>? itemText = null,
                                      Func<int, bool>? isItemEnabled = null,
                                      ItemsViewController? controller = null,
                                      int overscan = 4,
                                      float grow = 1f,
                                      Func<int, (float, float)>? itemDisplacement = null,
                                      IReadSignal<int>? displacementVersion = null,
                                      IReadSignal<int>? draggedSlot = null,
                                      bool suppressScrollBar = false,
                                      bool autoEdgeFade = false,
                                      bool staggerColdRealize = false,
                                      string? scrollKey = null,
                                      Func<int, (float dx, float dy)?>? itemFlipFrom = null,
                                      Func<int, (float from, float delayMs)?>? itemFadeFrom = null,
                                      (Func<ScrollGeometry, long> Project, Action<ScrollGeometry> Action)? onScrollGeometryChanged = null,
                                      IReadSignal<int>? itemCountSignal = null)
        => Embed.Comp(() => new ItemsView
        {
            ItemCount = itemCount,
            ItemCountSignal = itemCountSignal,
            RowTemplate = rowTemplate,
            BoundMode = true,
            StaggerColdRealize = staggerColdRealize,
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
            Grow = grow,
            SuppressScrollBar = suppressScrollBar,
            ScrollKey = scrollKey,
            AutoEdgeFade = autoEdgeFade,
            ItemDisplacement = itemDisplacement,
            DisplacementVersion = displacementVersion,
            DraggedSlot = draggedSlot,
            ItemFlipFrom = itemFlipFrom,
            ItemFadeFrom = itemFadeFrom,
            OnScrollGeometryChanged = onScrollGeometryChanged,
        });

    // ── built-in presets (the former ListView/GridView controls, folded onto ItemsView) ──────────────
    // ItemsView.List(...) and ItemsView.Grid(...) are the built-in presets backed by the internal hook-bearing
    // components ItemsViewListPreset / ItemsViewGridPreset (the substrate needs hooks — UseMemo/UseSignal/UseRef/
    // conditional UseContext — which a plain static returning Element cannot host). The List preset uses AccentPill,
    // the Grid preset uses Check.

    /// <summary>The WinUI ListView simple surface: a vertical, single-selectable list over the labeled items, with the
    /// accent-bar selector. <paramref name="selectedIndex"/> is the controlled single-selection signal.</summary>
    public static Element List(IReadOnlyList<string> items,
                               Signal<int>? selectedIndex = null,
                               Action<int>? onSelectionChanged = null)
        => Embed.Comp(() => new ItemsViewListPreset { Items = items, SelectedIndex = selectedIndex ?? new Signal<int>(-1), OnSelectionChanged = onSelectionChanged });

    /// <summary>The full WinUI ListView-shaped preset: templated rows over the virtualized stack (the former
    /// <c>ListView.Create</c>).</summary>
    public static Element List(int itemCount, Func<int, Element> itemTemplate,
                               ItemsSelectionMode selectionMode = ItemsSelectionMode.Single,
                               SelectionModel? selection = null,
                               Action<int>? onItemClick = null,
                               Action<int>? onItemInvoked = null,
                               Action<int>? onSelectionIndexChanged = null,
                               bool canReorderItems = false,
                               Action<int, int>? onReorder = null,
                               Func<int, string>? itemText = null,
                               Func<int, bool>? isItemEnabled = null,
                               ItemsViewController? controller = null,
                               Func<int, string>? keyOf = null,
                               float itemExtent = ListItemExtent,
                               float width = float.NaN, float height = float.NaN, float grow = 0f)
        => Embed.Comp(() => new ItemsViewListPreset { ItemCount = itemCount, ItemTemplate = itemTemplate, SelectionMode = selectionMode, Selection = selection, OnItemClick = onItemClick, OnItemInvoked = onItemInvoked, OnSelectionChanged = onSelectionIndexChanged, CanReorderItems = canReorderItems, OnReorder = onReorder, ItemText = itemText, IsItemEnabled = isItemEnabled, Controller = controller, KeyOf = keyOf, ItemExtent = itemExtent, Width = width, Height = height, Grow = grow });

    /// <summary>The WinUI GridView simple surface: a grid of labeled tiles with the corner-check selector (the former
    /// <c>GridView.Create</c>).</summary>
    public static Element Grid(IReadOnlyList<string> items, int columns = 4, float tileSize = 96f)
        => Embed.Comp(() => new ItemsViewGridPreset { Items = items, Columns = columns, TileSize = tileSize });

    /// <summary>The full WinUI GridView-shaped preset: templated tiles over the virtualized grid (the former
    /// <c>GridView.Create</c>).</summary>
    public static Element Grid(int itemCount, Func<int, Element> itemTemplate, int columns, float tileHeight,
                               ItemsSelectionMode selectionMode = ItemsSelectionMode.Single,
                               SelectionModel? selection = null,
                               Action<int>? onItemClick = null,
                               Action<int>? onItemInvoked = null,
                               Action? onSelectionChanged = null,
                               bool canReorderItems = false,
                               Action<int, int>? onReorder = null,
                               Func<int, string>? itemText = null,
                               Func<int, bool>? isItemEnabled = null,
                               ItemsViewController? controller = null,
                               Func<int, string>? keyOf = null,
                               float width = float.NaN, float height = float.NaN, float grow = 0f)
        => Embed.Comp(() => new ItemsViewGridPreset { ItemCount = itemCount, ItemTemplate = itemTemplate, Columns = columns, TileSize = tileHeight, SelectionMode = selectionMode, Selection = selection, OnItemClick = onItemClick, OnItemInvoked = onItemInvoked, OnSelectionChanged = onSelectionChanged, CanReorderItems = canReorderItems, OnReorder = onReorder, ItemText = itemText, IsItemEnabled = isItemEnabled, Controller = controller, KeyOf = keyOf, Width = width, Height = height, Grow = grow });

    // DEBUG-only frozen-props tripwire (ReuseGuard): ItemCount/Items freeze at mount like any ComponentEl field. A
    // reused ItemsView whose EFFECTIVE item count changed means the caller grew/refiltered the set without a remount
    // Key or a reactive count — the DiagnosticsPanel bug class. Const-gated so it's compiled out of release entirely.
    public override bool ChecksReuse => ReuseGuard.CompiledIn;
    public override void DebugCheckReuse(Component next)
    {
        if (next is not ItemsView n) return;
        if (ItemCountSignal is not null && n.ItemCountSignal is not null) return;
        int a = ItemCount >= 0 ? ItemCount : Items.Count;
        int b = n.ItemCount >= 0 ? n.ItemCount : n.Items.Count;
        if (a != b)
            ReuseGuard.Violation(this, nameof(ItemCount),
                $"item count {a}→{b} on a reused list — re-key the list wrapper so a set change remounts it "
              + "(scrollKey preserves the offset; the DetailTracks idiom), or drive the count reactively");
    }

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
        var lastTabStop = UseRef(-1);                      // bound mode: the index currently holding the roving tab stop

        var model = Selection ?? ownModel;
        int count = ItemCountSignal is { } cs ? cs.Value : ItemCount >= 0 ? ItemCount : Items.Count;
        model.ItemCount = count;
        model.Mode = SelectionMode;
        // RenderItem mode re-skins selection by re-rendering this window (the container template reads IsSelected at
        // build time), so it subscribes to Version. BOUND mode does NOT: each persistent row owns a bind that reads the
        // model directly (RowScope.IsSelected), so a programmatic selection change re-skins those rows with no ItemsView
        // re-render at all (0-alloc) — subscribing here would force a wasteful whole-window re-render per selection.
        if (!BoundMode) _ = model.Version.Value;           // subscribe — a selection change re-skins just this window
        int cur = current.Value;                           // subscribe — current moves re-render (focus visuals)
        int dispVer = DisplacementVersion?.Value ?? 0;     // subscribe — reorder drag-delta/dwell re-seeds displacement
                                                           //   (crosses the frozen-ComponentEl boundary; the only re-render trigger here)

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
                RepeatKind.Grid => new GridVirtualLayout(spec.Columns, spec.Extent, spec.Gap, spec.MinCellWidth,
                    spec.Estimate > 0f ? spec.Estimate : 120f),
                RepeatKind.Custom => spec.CustomLayout,
                _ => null,   // Wrap/Inline — non-virtual fallback
            },
            spec.Kind, spec.Extent, spec.Gap, spec.Columns, spec.MinCellWidth, spec.Estimate, spec.Horizontal, spec.CustomLayout ?? (object)0);
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
        void BringIntoView(int index, float alignmentRatio, bool animate)
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

            // Animated (WinUI AnimationDesired): record the PendingTarget + Programmatic WheelAnimating and arm the
            // ScrollIntegrator — phase 7 chases the live offset toward it (+ re-realizes the window + fades the bar) with
            // the ProgrammaticSpringHalflifeMs crit-damped chase. Snap (default): write Offset==Target and apply the
            // -offset content transform now (the dispatcher's SetScrollOffset idiom, InputDispatcher.cs:388-433).
            if (animate)
            {
                sc.Phase = ScrollIntegrator.WheelAnimating;
                sc.PhaseFlags = ScrollState.PhaseProgrammatic;
                sc.FlingVelocity = 0f;
                sc.FlingRetargeted = false;
                sc.FlingSnapTarget = float.NaN;
                if (horizontal) sc.PendingTargetX = target; else sc.PendingTargetY = target;
                Context.ArmScroll?.Invoke(vp);
                Context.RequestRerender();
                return;
            }

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

        // The REALIZED container node for an index: ord = index − FirstRealized → the ord-th window child (Null when not
        // realized). Non-virtual hosts (Wrap/Inline fallback) have no scroll state: every container is a direct child of
        // the captured host box, so ord == index. Shared by FocusIndex and the bound-mode roving tab stop.
        NodeHandle SlotRootForIndex(int index)
        {
            if (sceneRef is null) return NodeHandle.Null;
            var vp = viewportNode.Value;
            if (vp.IsNull || !sceneRef.IsLive(vp)) return NodeHandle.Null;
            NodeHandle first;
            int ord;
            if (sceneRef.TryGetScroll(vp, out var sc))
            {
                ord = index - sc.FirstRealized;
                if (ord < 0 || index >= sc.LastRealized) return NodeHandle.Null;
                first = sceneRef.FirstChild(sc.ContentNode);
            }
            else
            {
                ord = index;
                first = sceneRef.FirstChild(vp);
            }
            var n = first;
            for (int k = 0; k < ord && !n.IsNull; k++) n = sceneRef.NextSibling(n);
            return !n.IsNull && sceneRef.IsLive(n) ? n : NodeHandle.Null;
        }

        // Keyboard focus the REALIZED container for an index (focus lands regardless of the Focusable flag — SetFocus
        // gates only on Disabled — so the bound roving tab stop's cleared Focusable on non-current rows is no obstacle).
        void FocusIndex(int index, bool visual)
        {
            var focusNode = hooks.FocusNode;
            if (focusNode is null) return;
            var n = SlotRootForIndex(index);
            if (!n.IsNull) focusNode(n, visual);
        }

        // Bound mode roving SINGLE tab stop (TabNavigation="Once"): the slot roots are built Focusable=false, so the tab
        // WALK skips them; this moves the one tab stop to the keyboard-current slot by toggling its scene focusability
        // flags IN PLACE — no re-render, mirroring the WriteColumns mirror (Reconciler: ii.Focusable ⇄ NodeFlags.Focusable).
        void SetSlotTabStop(int index, bool on)
        {
            if (sceneRef is null) return;
            var n = SlotRootForIndex(index);
            if (n.IsNull) return;
            sceneRef.Interaction(n).Focusable = on;
            if (on) sceneRef.Mark(n, NodeFlags.Focusable); else sceneRef.Unmark(n, NodeFlags.Focusable);
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
            BringIntoView(next, alignmentRatio, animate: false);
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
                {
                    // Left/Right = index ±1; Up/Down = ±columns (column-railed). Responsive grids read live column count.
                    int cols = spec.Columns > 0 ? spec.Columns
                        : layout is GridVirtualLayout gv ? gv.EffectiveColumns(CrossExtent()) : 1;
                    return StepEnabled(from, dx != 0 ? dx : dy * cols);
                }
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
                    // Ctrl+A — Multiple/Extended only (ItemsViewInteractions.cpp:35-50). Extends WinUI: a repeat Ctrl+A
                    // when everything is already selected CLEARS it (toggle), giving a keyboard path back to no-selection.
                    if (SelectionMode is ItemsSelectionMode.Multiple or ItemsSelectionMode.Extended)
                    {
                        if (model.SelectedCount >= count) model.DeselectAll();
                        else model.SelectAll();
                        e.Handled = true;
                    }
                    return;
                case Keys.Escape:
                    // Escape clears the selection (a deliberate addition — the multi-select dismiss gesture).
                    if (SelectionMode != ItemsSelectionMode.None && model.SelectedCount > 0)
                    {
                        model.DeselectAll();
                        e.Handled = true;
                    }
                    return;
                case Keys.Home or Keys.End:
                {
                    // Scroll the list end into view corner-aligned (item 0 / count−1, alignment ratios 0/1,
                    // cpp:1009-1016), then make the first/last FOCUSABLE element current — WinUI focuses
                    // FindFirst/LastFocusableElement, not blindly index 0/count−1 (cpp:1028-1040).
                    bool home = e.KeyCode == Keys.Home;
                    BringIntoView(home ? 0 : count - 1, home ? 0f : 1f, animate: false);
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
            if (pointer) BringIntoView(i, float.NaN, animate: false);
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
            ctl.GetOffsetImpl = () =>
            {
                if (sceneRef is null) return 0f;
                var vp = viewportNode.Value;
                if (vp.IsNull || !sceneRef.IsLive(vp) || !sceneRef.TryGetScroll(vp, out var sc)) return 0f;
                return horizontal ? sc.OffsetX : sc.OffsetY;
            };
        }

        // Post-layout: focus the (now realized) keyboard-current container so the engine ring lands on it.
        UseLayoutEffect(() =>
        {
            int target = pendingFocus.Value;
            if (target >= 0) { pendingFocus.Value = -1; FocusIndex(target, visual: true); }
        }, cur);

        // Bound mode: move the single roving tab stop to the keyboard-current slot IN PLACE (no re-render). RenderItem
        // mode bakes the tab stop into each container via isTabStop at build time; bound slots are built once, so the
        // stop is moved imperatively by toggling the old/new current slot's focusability flags post-layout.
        UseLayoutEffect(() =>
        {
            if (!BoundMode) return;
            // The roving stop follows the keyboard-current item; with none yet, fall back so Tab can still ENTER the list
            // (the selected item in Single mode — the GettingFocus redirect — else the first item, realized at the top on
            // a fresh mount). An off-screen fallback simply finds no realized node and no-ops.
            int stop = cur >= 0 ? cur
                     : count == 0 ? -1
                     : SelectionMode == ItemsSelectionMode.Single && model.FirstSelectedIndex >= 0 ? model.FirstSelectedIndex : 0;
            int old = lastTabStop.Value;
            if (old == stop) return;
            if (old >= 0) SetSlotTabStop(old, false);
            if (stop >= 0) SetSlotTabStop(stop, true);
            lastTabStop.Value = stop;
        }, cur);

        // ── reorder displacement seed (the WinUI "siblings part to make room" over the positional recycler) ──────────
        // Edge-triggered on DisplacementVersion (NOT per frame): the owner bumps it on each drag-delta/dwell-commit — the
        // WinUI MoveItemsForLiveReorder-on-timer cadence. The effect walks the REALIZED window and seeds each row's
        // AnimEngine TranslateX/Y track to its target displacement (in DIP), reading the row's CURRENT translate as the
        // animation start so a retarget is velocity-continuous. The track (not BoxEl.OffsetX/Y) owns the channel, so the
        // displacement survives every reconcile (ApplyBox only writes LocalTransform from a NON-ZERO static offset,
        // Reconciler.cs:935-947 — the rows carry none, so the AnimEngine track is never clobbered) and is re-seeded on
        // each realize from ItemDisplacement (recycling-safe). Animate allocates a Keyframe[] per call — fine here because
        // this body is cold/edge-triggered, never a frame phase.
        UseLayoutEffect(() =>
        {
            var disp = ItemDisplacement;
            var anim = Context.Anim;
            if (disp is null || anim is null || sceneRef is null) return;
            var flip = ItemFlipFrom;                   // optional FLIP start override (data-reorder glide)
            var fade = ItemFadeFrom;                   // optional opacity seed + stagger delay (added-row ease-in)
            var vp = viewportNode.Value;
            if (vp.IsNull || !sceneRef.IsLive(vp)) return;
            int dragged = DraggedSlot?.Peek() ?? -1;   // resting index whose translate DragController owns (skip the seed)

            NodeHandle first; int restingBase;
            if (sceneRef.TryGetScroll(vp, out var sc))
            {
                restingBase = sc.FirstRealized;        // realized window: ord-th child ⇒ resting index FirstRealized+ord
                first = sceneRef.FirstChild(sc.ContentNode);
            }
            else
            {
                restingBase = 0;                       // non-virtual fallback (Wrap/Inline): ord == index
                first = sceneRef.FirstChild(vp);
            }

            var n = first;
            for (int ord = 0; !n.IsNull && sceneRef.IsLive(n); ord++, n = sceneRef.NextSibling(n))
            {
                int item = restingBase + ord;
                // Skip the pointer-dragged ghost UNCONDITIONALLY. Its translate is owned by DragController, which
                // re-asserts it every move; OffsetFor(dragged)==0 does NOT make the seed a no-op here, because `fromY`
                // below is the LIVE drag translate, so |0 − fromY| > eps fires a Replace TranslateY track that fights
                // DragController for the node: AnimEngine.Tick folds it absolutely and overwrites the drag translate,
                // then DragController.RetargetFromRest double-counts the stomped origin per frame into an unbounded
                // runaway (the ghost flies off the page). The scene's DragGhost flag is the ground truth (set by
                // DragController.Promote/ApplyPresented), so this holds even when DraggedSlot is unwired (every preset
                // currently leaves it null) or its index doesn't align with the realized window.
                if ((sceneRef.Flags(n) & NodeFlags.DragGhost) != 0 || item == dragged) continue;
                var (dx, dy) = disp(item);             // (0,0) for non-displaced (non-dragged) items
                var fd = fade?.Invoke(item);           // opacity seed (from→1) + the row's stagger delay
                float delay = fd?.delayMs ?? 0f;
                ref NodePaint p = ref sceneRef.Paint(n);
                var f = flip?.Invoke(item);            // FLIP "first": start from the OLD visual position, not the live translate
                float fromX = f?.dx ?? p.LocalTransform.Dx, fromY = f?.dy ?? p.LocalTransform.Dy;
                if (MathF.Abs(dx - fromX) > DisplacementEpsilon)
                    anim.Animate(n, AnimChannel.TranslateX, fromX, dx, DisplacementAnimMs, Easing.FluentDecelerate, delayMs: delay);
                if (MathF.Abs(dy - fromY) > DisplacementEpsilon)
                    anim.Animate(n, AnimChannel.TranslateY, fromY, dy, DisplacementAnimMs, Easing.FluentDecelerate, delayMs: delay);
                if (fd is { } o)
                    anim.Animate(n, AnimChannel.Opacity, o.from, 1f, DisplacementAnimMs, Easing.FluentDecelerate, delayMs: o.delayMs);
            }
        }, dispVer);

        // ── item template: content wrapped in the WinUI ItemContainer chrome (or the L4 skin's chrome) ──
        bool multi = SelectionMode == ItemsSelectionMode.Multiple;
        Func<int, Element> content = ItemTemplate ?? DefaultTile;
        // Selector-preset chrome: a custom ContainerFactory wins; else pick a built-in SelectorVisuals builder by the
        // Selector field. Border ⇒ null ⇒ the existing ItemContainer.Build branch below (the default; keeps cp1.b +
        // the e11virt.11-18 ItemContainer pins untouched). The SelectorVisuals builders take `in ItemChromeState` (an
        // additive, readonly-passed shape, SelectorVisuals.cs), so each preset is bridged through a capture-free lambda
        // to the by-value ItemContainerFactory delegate — the compiler caches these as static singletons (zero per-render
        // alloc; the closures capture nothing).
        // NOTE: the public ItemContainerFactory delegate has a FIXED signature with NO PartDelta param, so it can't
        // carry a per-item delta. To keep that delegate untouched while still routing PartDelta to the BUILT-IN
        // presets, containerTemplate (below) calls the SelectorVisuals builder DIRECTLY with `in delta` whenever a
        // built-in Selector is active (ContainerFactory is null && Selector != Border); the `skin` indirection is used
        // ONLY for a custom ContainerFactory (whose author reads ItemChromeState itself — no delta routing). The Border
        // default flows the delta through ItemContainer.Build's partDelta: param.
        ItemContainerFactory? skin = ContainerFactory ?? Selector switch
        {
            SelectorVisual.AccentPill => (i, c, st, oi, of) => SelectorVisuals.AccentPill(i, c, in st, oi, of),
            SelectorVisual.Check      => (i, c, st, oi, of) => SelectorVisuals.Check(i, c, in st, oi, of),
            SelectorVisual.FullRow    => (i, c, st, oi, of) => SelectorVisuals.FullRow(i, c, in st, oi, of),
            SelectorVisual.None       => (i, c, st, oi, of) => SelectorVisuals.None(i, c, in st, oi, of),
            _                         => (ItemContainerFactory?)null,   // Border ⇒ keep the ItemContainer.Build path below
        };
        // True ⇒ a built-in SelectorVisuals preset is active (NOT a custom ContainerFactory, NOT Border) — the delta is
        // routed by a direct builder call in containerTemplate so it lands on the preset chrome.
        bool builtInSelector = ContainerFactory is null && Selector != SelectorVisual.Border;

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

        Func<int, ItemChromeState, PartDelta>? partDelta = PartDelta;
        Func<int, Element> containerTemplate = i =>
        {
            bool selected = model.IsSelected(i);
            bool enabled = IsItemEnabled?.Invoke(i) ?? true;
            var state = new ItemChromeState(selected, enabled, multi, multi && selected, i == cur);
            // Per-item VARIATION resolved ONCE per realized item (cold realize edge, never a frame phase) and passed BY
            // VALUE into the selector builder / ItemContainer. None ⇒ every `?? default` fallback preserves the preset
            // EXACTLY (so a null PartDelta is byte-for-byte the prior behavior). The Func must be pure-value.
            var delta = partDelta?.Invoke(i, state) ?? FluentGpu.Controls.PartDelta.None;
            // RESIDUAL (documented per S1b orders): these two closures allocate per realized item. The mechanically-
            // correct per-SLOT pool (grow-only, indexed by realize ORD so the SAME callback objects survive recycling)
            // is NOT installable in this pass — VirtualListEl.RenderItem is called with the ABSOLUTE item index and the
            // viewport ScrollState.FirstRealized is still the PREVIOUS window's value at call time (Reconciler
            // RealizeWindow writes FirstRealized AFTER the RenderItem build loop), and overlap-reuse skips RenderItem
            // entirely (Reconciler.cs:555) — so no reliable realize-ord is available to key a bounded pool without an
            // engine change. The C6 recycle shape-hash guard + the S3 steady-scroll HotPhaseAllocBytes==0 check reveal
            // whether closing this residual is required; pool here once an ord seam exists.
            Action<ItemContainerTrigger, KeyModifiers> interact = (t, m) => OnItemInteraction(i, t, m);
            Action<bool> focusChanged = got => { if (got && current.Peek() != i) current.Value = i; };
            // Built-in preset: call the SelectorVisuals builder DIRECTLY with `in delta` (the public ItemContainerFactory
            // delegate carries no delta — see the `skin` note above). Custom ContainerFactory: route through `skin`
            // (its author reads ItemChromeState itself; no delta). Border default: ItemContainer.Build with partDelta:.
            if (builtInSelector)
                return Selector switch
                {
                    SelectorVisual.AccentPill => SelectorVisuals.AccentPill(i, content(i), in state, interact, focusChanged, in delta),
                    SelectorVisual.Check      => SelectorVisuals.Check(i, content(i), in state, interact, focusChanged, in delta),
                    SelectorVisual.FullRow    => SelectorVisuals.FullRow(i, content(i), in state, interact, focusChanged, in delta),
                    SelectorVisual.None       => SelectorVisuals.None(i, content(i), in state, interact, focusChanged, in delta),
                    _                         => SelectorVisuals.None(i, content(i), in state, interact, focusChanged, in delta),
                };
            return skin is not null
                ? skin(i, content(i), state, interact, focusChanged)
                : ItemContainer.Build(
                    content(i),
                    isSelected: selected,
                    onInteraction: interact,
                    isEnabled: enabled,
                    showSelectionCheckbox: multi,
                    isChecked: multi && selected,
                    onFocusChanged: focusChanged,
                    isTabStop: i == tabStop,
                    partDelta: delta);
        };

        // ItemTransitionProvider (ItemsView.idl:45 → the inner repeater, ItemsView.xaml:30): stamp the collection
        // transition onto each realized container root. The non-virtual fallback passes it to ItemsRepeater instead.
        Func<int, Element> realizeTemplate = Transition is { } tr
            ? Repeater.WrapTransition(containerTemplate, tr.ToSpec())
            : containerTemplate;

        // Bound (signals-first) realize: build the row ONCE per slot from a RowScope of per-row read-signals (the index
        // SIGNAL + IsSelected/IsCurrent/IsEnabled predicates + the interaction/focus callbacks). A recycle/selection is
        // then a signal write into existing slots — no row rebuild, no remount, no Enter replay (the flash fix).
        Func<IReadSignal<int>, Element>? rowBind = null;
        if (BoundMode && RowTemplate is { } rowTpl)
        {
            rowBind = index =>
            {
                // Created ONCE per slot (RealizeBoundWindow invokes rowBind only while growing slots), retained by the
                // slot's bind effects, disposed with the slot. The predicates read model.Version/current + the index
                // signal, so a selection/current change OR a recycle re-fires exactly the binds that read them — with
                // NO Memo (Memo.OnStale propagates eagerly, so it adds no dedup over a thunk, only lifetime coupling).
                Func<bool> isSelected = () => { _ = model.Version.Value; return model.IsSelected(index.Value); };
                Func<bool> isCurrent = () => current.Value == index.Value;
                Func<bool> isEnabled = IsItemEnabled is null ? static () => true : () => IsItemEnabled(index.Value);
                Action<ItemContainerTrigger, KeyModifiers> interact = (t, m) => OnItemInteraction(index.Value, t, m);
                Action<bool> focusChanged = got => { if (got && current.Peek() != index.Value) current.Value = index.Value; };
                return rowTpl(new RowScope(index, isSelected, isCurrent, isEnabled, interact, focusChanged));
            };
        }

        Element itemsHost = rowBind is not null && layout is not null
            // Bound slots: the RowBind path (RealizeBoundWindow) — persistent rows, recycle by index-signal write.
            ? new VirtualListEl
            {
                ItemCount = count,
                Layout = layout,
                RowBind = rowBind,
                StaggerColdRealize = StaggerColdRealize,
                Overscan = OverscanItems,
                Horizontal = horizontal,
                EdgeCues = EdgeCues,
                AutoEdgeFade = AutoEdgeFade,
                SuppressScrollBar = SuppressScrollBar,
                ScrollKey = ScrollKey,
                OnScrollGeometryChanged = OnScrollGeometryChanged,
                Grow = Grow,
                OnRealized = h => viewportNode.Value = h,
            }
            : layout is not null
            ? new VirtualListEl
            {
                ItemCount = count,
                Layout = layout,
                RenderItem = realizeTemplate,
                KeyOf = KeyOf,
                Overscan = OverscanItems,
                Horizontal = horizontal,
                EdgeCues = EdgeCues,
                AutoEdgeFade = AutoEdgeFade,
                SuppressScrollBar = SuppressScrollBar,
                ScrollKey = ScrollKey,
                OnScrollGeometryChanged = OnScrollGeometryChanged,
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
