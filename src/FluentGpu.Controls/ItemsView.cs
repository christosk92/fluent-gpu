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
    internal TryGetItemIndexDelegate? TryGetItemIndexImpl;
    internal Action<float>? ScrollByImpl;
    internal CorrectMeasuredExtentDelegate? CorrectMeasuredExtentImpl;
    internal Func<float>? GetOffsetImpl;
    internal Func<NodeHandle>? GetViewportImpl;
    internal Action<IReadOnlyList<int>, Action>? BeginRemovalImpl;
    internal Action<object>? ObserveInsertionMembershipImpl;
    internal Action<bool>? CompleteDisclosureImpl;
    internal readonly Signal<int> DisclosureVersion = new(0);
    internal ItemDisclosureRequest? PendingDisclosure;
    internal ItemDisclosureRequest? ActiveDisclosure;
    internal ItemDisclosureRequest? CompletedDisclosure;
    internal bool DisclosureStarted;
    internal bool DisclosurePresentationArmed;
    internal bool DisclosureTrackObserved;
    internal bool DisclosureNeedsClear;
    internal int DisclosureStartedItemCount;
    internal int DisclosureStartedSourceVersion;
    internal bool DisclosureTracksSourceVersion;
    internal int DisclosureClearAtCount = int.MaxValue;
    internal int DisclosureClearAtSourceVersion = int.MaxValue;
    internal int DisclosureProgressBucket = -1;
    internal Action<ItemDisclosureDiagnostic>? DisclosureDiagnostic;
    private long _nextDisclosureOperationId;

    internal delegate bool TryGetItemIndexDelegate(float horizontalViewportRatio, float verticalViewportRatio,
                                                   out int index);
    internal delegate bool CorrectMeasuredExtentDelegate(IMeasuredVirtualLayout layout, int index, float mainExtent);

    /// <summary>The REALIZED virtualized viewport node (<c>Null</c> before mount and for a non-virtual host). The seam a
    /// composing control needs to write per-viewport <c>ScrollState</c> knobs that a frozen-at-mount options record cannot
    /// express reactively — chiefly a snap interval derived from a live layout fit (a size-reactive pager's page stride).
    /// Read it inside a layout effect; the node is generation-checked by every <c>SceneStore</c> accessor.</summary>
    public NodeHandle Viewport => GetViewportImpl?.Invoke() ?? NodeHandle.Null;

    /// <summary>The live scroll offset along the view's scroll axis (DIP; 0 before the viewport realizes / for a
    /// non-virtual host). For a measured-layout correction, use <see cref="CorrectMeasuredExtent"/> so active scroll
    /// intents are rebased together with the visible anchor.</summary>
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

    /// <summary>Resolve the item under a normalized viewport point without realizing or enumerating the collection.
    /// Returns false before the virtual viewport has live layout geometry or when the point falls outside an item.</summary>
    public bool TryGetItemIndex(float horizontalViewportRatio, float verticalViewportRatio, out int index)
    {
        var resolve = TryGetItemIndexImpl;
        if (resolve is not null) return resolve(horizontalViewportRatio, verticalViewportRatio, out index);
        index = -1;
        return false;
    }

    /// <summary>Nudge the virtualized viewport by <paramref name="delta"/> DIP along its scroll axis (clamped).
    /// The drag-reorder EDGE AUTO-SCROLL seam: a composing list (ListView) calls this while the pointer drags near
    /// the viewport edge (the plan's E5-L3 edge auto-scroll in virtualized lists). No-op for non-virtual hosts.</summary>
    public void ScrollBy(float delta) => ScrollByImpl?.Invoke(delta);

    /// <summary>Correct a cached measured extent, preserving the current visible anchor and every active scroll intent.
    /// This is the unrealized-row counterpart to the layout engine's normal measure feedback: use it when transient UI
    /// (for example, an expanded drawer) disappears while its virtual slot is off-screen and cannot remeasure itself.
    /// Returns false when this controller is not mounted on <paramref name="layout"/> or the request is invalid.</summary>
    public bool CorrectMeasuredExtent(IMeasuredVirtualLayout layout, int index, float mainExtent)
    {
        ArgumentNullException.ThrowIfNull(layout);
        return CorrectMeasuredExtentImpl?.Invoke(layout, index, mainExtent) ?? false;
    }

    /// <summary>Animate currently-realized rows at the supplied logical indices out, then invoke
    /// <paramref name="commit"/> exactly once to mutate the backing collection. Indices are sorted, de-duplicated and
    /// negative values discarded; unseen items are never realized merely to animate. Without removal choreography the
    /// operation degrades to an immediate commit.</summary>
    public void BeginRemoval(IReadOnlyList<int> indices, Action commit)
    {
        ArgumentNullException.ThrowIfNull(indices);
        ArgumentNullException.ThrowIfNull(commit);
        if (indices.Count == 0) { commit(); return; }

        var normalized = new int[indices.Count];
        int count = 0;
        for (int i = 0; i < indices.Count; i++)
            if (indices[i] >= 0) normalized[count++] = indices[i];
        if (count == 0) { commit(); return; }
        Array.Sort(normalized, 0, count);
        int unique = 1;
        for (int i = 1; i < count; i++)
            if (normalized[i] != normalized[unique - 1]) normalized[unique++] = normalized[i];
        if (unique != normalized.Length) Array.Resize(ref normalized, unique);

        var begin = BeginRemovalImpl;
        if (begin is null) commit();
        else begin(normalized, commit);
    }

    /// <summary>The optimistic-membership HANDOFF edge for a configured <see cref="InsertionOptions"/>: call it from a
    /// layout effect keyed on the backing collection's identity. A NEW token after a deposit means the real list has
    /// accepted the mutation, so the temporary gap closes into its FLIP instead of holding a blank intermediate frame.
    /// No-op without an insertion destination.</summary>
    public void ObserveInsertionMembership(object token)
    {
        ArgumentNullException.ThrowIfNull(token);
        ObserveInsertionMembershipImpl?.Invoke(token);
    }

    /// <summary>Begin or retarget one contiguous disclosure band. Expand callers insert the range first; collapse callers
    /// supply the mutation that removes it, which runs exactly once after the close reaches zero.</summary>
    public void BeginDisclosure(ItemDisclosureRange range, ItemDisclosureDirection direction,
                                Action? collapseCommit = null, Action? settled = null)
    {
        if (range.FirstIndex < 0) throw new ArgumentOutOfRangeException(nameof(range));
        if (range.Count <= 0) throw new ArgumentOutOfRangeException(nameof(range));
        if (string.IsNullOrEmpty(range.Key)) throw new ArgumentException("A stable logical key is required.", nameof(range));
        if (direction == ItemDisclosureDirection.Collapse && collapseCommit is null)
            throw new ArgumentNullException(nameof(collapseCommit));

        var next = new ItemDisclosureRequest(++_nextDisclosureOperationId, range, direction, collapseCommit, settled);
        var active = ActiveDisclosure;
        if (active is not null && !string.Equals(active.Range.Key, range.Key, StringComparison.Ordinal))
            CompleteDisclosure();
        PendingDisclosure = next;
        Trace(ItemDisclosureDiagnosticKind.Queued, next, -1, -1, -1f);
        DisclosureVersion.Value = DisclosureVersion.Peek() + 1;
    }

    /// <summary>Force the active transaction to its requested endpoint. A pending collapse still commits exactly once.</summary>
    public void CompleteDisclosure()
    {
        var active = ActiveDisclosure;
        if (active is null)
        {
            if (PendingDisclosure is { } pending)
            {
                PendingDisclosure = null;
                if (pending.Direction == ItemDisclosureDirection.Collapse) pending.CollapseCommit?.Invoke();
                pending.Settled?.Invoke();
                DisclosureVersion.Value = DisclosureVersion.Peek() + 1;
            }
            return;
        }
        CompleteDisclosureImpl?.Invoke(active.Direction == ItemDisclosureDirection.Expand);
        FinishDisclosure(active);
    }

    internal void StartDisclosure(ItemDisclosureRequest request, int itemCount, int sourceVersion, bool tracksSourceVersion)
    {
        CompletedDisclosure = null;
        ActiveDisclosure = request;
        PendingDisclosure = null;
        DisclosureStarted = true;
        DisclosurePresentationArmed = false;
        DisclosureTrackObserved = false;
        DisclosureStartedItemCount = itemCount;
        DisclosureStartedSourceVersion = sourceVersion;
        DisclosureTracksSourceVersion = tracksSourceVersion;
        DisclosureProgressBucket = -1;
        Trace(ItemDisclosureDiagnosticKind.Starting, request, itemCount, sourceVersion, -1f);
    }

    internal void ArmDisclosure()
    {
        if (ActiveDisclosure is not { } request) return;
        DisclosurePresentationArmed = true;
        Trace(ItemDisclosureDiagnosticKind.Armed, request, DisclosureStartedItemCount,
            DisclosureStartedSourceVersion, request.Direction == ItemDisclosureDirection.Expand ? 0f : 1f);
    }

    internal void ObserveDisclosure(float progress)
    {
        if (ActiveDisclosure is not { } request) return;
        DisclosureTrackObserved = true;
        int bucket = Math.Clamp((int)MathF.Floor(Math.Clamp(progress, 0f, 1f) * 4f), 0, 4);
        if (bucket == DisclosureProgressBucket) return;
        DisclosureProgressBucket = bucket;
        Trace(ItemDisclosureDiagnosticKind.Progress, request, DisclosureStartedItemCount,
            DisclosureStartedSourceVersion, progress);
    }

    internal void PrepareExpand(ItemDisclosureRange range, Action? settled)
    {
        if (ActiveDisclosure is { } active && string.Equals(active.Range.Key, range.Key, StringComparison.Ordinal)) return;
        if (PendingDisclosure is { } pending && string.Equals(pending.Range.Key, range.Key, StringComparison.Ordinal)) return;
        var request = new ItemDisclosureRequest(++_nextDisclosureOperationId, range,
            ItemDisclosureDirection.Expand, null, settled, true);
        PendingDisclosure = request;
        Trace(ItemDisclosureDiagnosticKind.Queued, request, -1, -1, -1f);
    }

    internal void SettleDisclosure()
    {
        if (ActiveDisclosure is not { } active) return;
        FinishDisclosure(active);
    }

    private void FinishDisclosure(ItemDisclosureRequest request)
    {
        CompletedDisclosure = request;
        ActiveDisclosure = null;
        PendingDisclosure = null;
        DisclosureStarted = false;
        DisclosurePresentationArmed = false;
        DisclosureTrackObserved = false;
        DisclosureNeedsClear = true;
        DisclosureClearAtCount = request.Direction == ItemDisclosureDirection.Collapse
            ? Math.Max(0, DisclosureStartedItemCount - request.Range.Count)
            : int.MaxValue;
        DisclosureClearAtSourceVersion = request.Direction == ItemDisclosureDirection.Collapse
            ? DisclosureStartedSourceVersion + 1
            : DisclosureStartedSourceVersion;
        if (request.Direction == ItemDisclosureDirection.Collapse)
        {
            Trace(ItemDisclosureDiagnosticKind.Committing, request, DisclosureStartedItemCount,
                DisclosureStartedSourceVersion, 0f);
            request.CollapseCommit?.Invoke();
        }
        Trace(ItemDisclosureDiagnosticKind.Settled, request, DisclosureStartedItemCount,
            DisclosureStartedSourceVersion, request.Direction == ItemDisclosureDirection.Expand ? 1f : 0f);
        request.Settled?.Invoke();
        DisclosureVersion.Value = DisclosureVersion.Peek() + 1;
    }

    internal void DisclosureCleared(bool recovery, int itemCount, int sourceVersion)
    {
        var request = CompletedDisclosure ?? ActiveDisclosure ?? PendingDisclosure;
        DisclosureNeedsClear = false;
        DisclosureClearAtCount = int.MaxValue;
        DisclosureClearAtSourceVersion = int.MaxValue;
        if (request is not null)
            Trace(recovery ? ItemDisclosureDiagnosticKind.Recovered : ItemDisclosureDiagnosticKind.Cleared,
                request, itemCount, sourceVersion, -1f);
        else
            DisclosureDiagnostic?.Invoke(new ItemDisclosureDiagnostic(
                recovery ? ItemDisclosureDiagnosticKind.Recovered : ItemDisclosureDiagnosticKind.Cleared,
                0, default, ItemDisclosureDirection.Expand, itemCount, sourceVersion, -1f));
        CompletedDisclosure = null;
    }

    internal void TraceFailure()
    {
        if (ActiveDisclosure is { } request)
            Trace(ItemDisclosureDiagnosticKind.FailedToArm, request, DisclosureStartedItemCount,
                DisclosureStartedSourceVersion, -1f);
    }

    private void Trace(ItemDisclosureDiagnosticKind kind, ItemDisclosureRequest request,
                       int itemCount, int sourceVersion, float progress)
        => DisclosureDiagnostic?.Invoke(new ItemDisclosureDiagnostic(kind, request.OperationId, request.Range,
            request.Direction, itemCount, sourceVersion, progress));
}

/// <summary>A stable contiguous logical range over an ItemsView's expanded item model.</summary>
public readonly record struct ItemDisclosureRange(string Key, int FirstIndex, int Count);

public enum ItemDisclosureDirection : byte { Expand, Collapse }

public enum ItemDisclosureDiagnosticKind : byte
{
    Queued, Starting, Armed, Progress, Committing, Settled, Cleared, Recovered, FailedToArm,
}

public readonly record struct ItemDisclosureDiagnostic(ItemDisclosureDiagnosticKind Kind, long OperationId,
    ItemDisclosureRange Range, ItemDisclosureDirection Direction, int ItemCount, int SourceVersion, float Progress);

internal sealed record ItemDisclosureRequest(long OperationId, ItemDisclosureRange Range, ItemDisclosureDirection Direction,
                                             Action? CollapseCommit, Action? Settled, bool PreparedExpansion = false);

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
    // Reorder/placement displacement motion (WinUI's MoveItemsForLiveReorder "siblings part to make room"). The timing
    // is deliberately NOT a literal here: the control names the MOTION — MotionTok.ItemPlacement — and AnimEngine.SeedValue
    // resolves its dynamics + reduced-motion policy centrally, so a token retune and reduced motion (SnapEnd ⇒ the
    // displacement lands instantly instead of gliding) apply engine-wide without touching this control.
    // (Superseded a local Motion.ControlNormal/Easing.FluentDecelerate pair.)
    private static MotionTokenDef DisplacementMotion => MotionTok.ItemPlacement;
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
    /// <summary>Identity-stable two-way controller for this view's vertical viewport.</summary>
    public IScrollController? VerticalScrollController;
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
    /// <summary>Optional bound-slot removal choreography, invoked through <see cref="ItemsViewController"/>.</summary>
    public RemovalOptions? Removal;
    public DisclosureOptions? Disclosure;
    /// <summary>Declarative insertion destination (<see cref="InsertionOptions"/>): the view mounts its own drop
    /// target and owns ALL insertion geometry — slot, exact live gap with virtual-removal accounting, the 2px accent
    /// line + terminal dot, the in-gap preview, the source-row hide and the commit/teardown lifecycle.</summary>
    public InsertionOptions? Insertion;

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
    /// <summary>Feather WIDTH in DIP for <see cref="AutoEdgeFade"/>; 0 (default) = the engine's standard band. Forwarded
    /// onto the built VirtualListEl — see <c>ScrollEl.AutoEdgeFadeBand</c>.</summary>
    public float AutoEdgeFadeBand;
    /// <summary>Never draw the conscious scrollbar for the virtualized viewport (a paged surface navigates by its
    /// pager, not a draggable bar). Forwarded onto the built VirtualListEl. Default false.</summary>
    public bool SuppressScrollBar;
    /// <summary>Scroll-position restoration key (see <see cref="VirtualListEl.ScrollKey"/>): a stable per-content identity
    /// so a revisit lands at the saved row on the first realized window. Forwarded onto the built VirtualListEl.</summary>
    public string? ScrollKey;
    /// <summary>CSS <c>scroll-timeline-name</c> (see <see cref="VirtualListEl.ScrollTimeline"/>): publish this viewport's
    /// offset under a NAME so a node OUTSIDE it can drive a <c>ScrollBindDsl.Timeline</c> bind from it. Forwarded onto the
    /// built VirtualListEl.</summary>
    public string? ScrollTimeline;
    /// <summary>Viewport-space top clip applied as one shared band to recyclable items after
    /// <see cref="PersistentPrefixCount"/>. NaN disables it.</summary>
    public float ItemClipTopInset = float.NaN;
    /// <summary>Top alpha-feather for the recyclable item band. Zero disables it.</summary>
    public float ItemClipTopFadeBand;
    public (Func<ScrollGeometry, long> Project, Action<ScrollGeometry> Action)? OnScrollGeometryChanged;
    /// <summary>Viewport-hydration hook forwarded onto the built VirtualListEl (see <c>VirtualListEl.OnVisibleRange</c>):
    /// the realized window moved → (first, last) exclusive. Fires only on a window CHANGE (never on a steady
    /// transform-only scroll frame) and reports the realized window INCLUDING the overscan halo, not the strictly-visible
    /// rows. Forwarded on BOTH virtual paths (RowBind and RenderItem).</summary>
    public Action<int, int>? OnVisibleRange;
    /// <summary>Declarative scroll-snap points forwarded onto the built VirtualListEl (see <c>ScrollEl.Snap</c>). Frozen at
    /// mount like every other unpacked option — a width-reactive interval must be written through
    /// <see cref="ItemsViewController.Viewport"/> instead. Null ⇒ the reconciler never touches the snap fields.</summary>
    public FluentGpu.Scene.SnapSpec? Snap;

    // ── research adjustment #16 — virtualization knobs (forwarded to the built VirtualListEl / applied per-container) ──
    /// <summary>Recycle-pool discriminator (bound path): heterogeneous rows only rebind within their content-type pool.</summary>
    public Func<int, int>? ContentType;
    /// <summary>Pre-realize cache extent in PIXELS beyond the viewport (overrides row-based <see cref="OverscanItems"/> when set).</summary>
    public float CacheExtentPx = float.NaN;
    /// <summary>Bound-path leading items kept mounted for native sticky/scroll-linked composition.</summary>
    public int PersistentPrefixCount;
    /// <summary>Per-item paint isolation: wrap each realized item container as a layout/paint boundary (IsolateLayout + clip).</summary>
    public bool RepaintBoundary;
    // ── research adjustment #5 — keep-alive-but-hidden slot (bound path) ──
    /// <summary>Keep-alive predicate (bound path): an item whose slot must park hidden instead of index-rebinding off-window.</summary>
    public Func<int, bool>? KeepAlive;
    /// <summary>Bounded keep-alive bucket cap (default 8; LRU-evicted beyond it).</summary>
    public int KeepAliveCap = 8;

    /// <summary>Legacy demo factory (compat): a single-selectable grid of labeled tiles, now riding the full
    /// L0–L3 substrate (virtualized grid + ItemContainer chrome + keyboard nav). Natural-sized (Grow 0): the demo
    /// grid sits in an auto-height gallery card, so the view measures to its grid's ContentExtent.</summary>
    public static Element Create(IReadOnlyList<string> items, int columns = 4)
        => Embed.Comp(() => new ItemsView { Items = items, Columns = columns, Grow = 0f });

    /// <summary>The canonical WinUI-shaped factory: templated items over any <see cref="RepeatLayout"/>. The ~20 former
    /// named arguments collapse into <paramref name="options"/> (a <see cref="ListOptions"/> record + the grouped
    /// <see cref="ScrollOptions"/>/<see cref="ReorderOptions"/> sub-records); the options are UNPACKED to the component's
    /// fields at factory time — the recycling hot path never reads the record.</summary>
    public static Element Create(int itemCount, Func<int, Element> itemTemplate, RepeatLayout layout,
                                 ListOptions? options = null)
    {
        var o = options ?? ListOptions.Default;
        return Embed.Comp(() => new ItemsView
        {
            ItemCount = itemCount,
            ItemTemplate = itemTemplate,
            Layout = layout,
            HasExplicitLayout = true,
            SelectionMode = o.SelectionMode,
            Selection = o.Selection,
            IsItemInvokedEnabled = o.IsItemInvokedEnabled,
            ItemInvoked = o.OnInvoked,
            SelectionChanged = o.OnChange,
            ItemText = o.ItemText,
            IsItemEnabled = o.IsItemEnabled,
            Controller = o.Controller,
            VerticalScrollController = o.Scroll?.VerticalScrollController,
            OverscanItems = o.Overscan,
            ContainerFactory = o.ContainerFactory,
            KeyOf = o.KeyOf,
            Grow = o.Grow,
            SuppressScrollBar = o.Scroll?.SuppressScrollBar ?? false,
            ScrollKey = o.Scroll?.ScrollKey,
            ScrollTimeline = o.Scroll?.ScrollTimeline,
            ItemClipTopInset = o.Scroll?.ItemClipTopInset ?? float.NaN,
            ItemClipTopFadeBand = o.Scroll?.ItemClipTopFadeBand ?? 0f,
            EdgeCues = o.Scroll?.EdgeCues ?? ScrollEdgeCues.Auto,
            AutoEdgeFade = o.Scroll?.AutoEdgeFade ?? false,
            AutoEdgeFadeBand = o.Scroll?.AutoEdgeFadeBand ?? 0f,
            OnScrollGeometryChanged = o.Scroll?.OnScrollGeometryChanged,
            OnVisibleRange = o.OnVisibleRange,
            Snap = o.Scroll?.Snap,
            Transition = o.Transition,
            Selector = o.Selector,
            ItemDisplacement = o.Reorder?.ItemDisplacement,
            DisplacementVersion = o.Reorder?.DisplacementVersion,
            DraggedSlot = o.Reorder?.DraggedSlot,
            PartDelta = o.PartDelta,
            ContentType = o.ContentType,
            CacheExtentPx = o.CacheExtentPx,
            PersistentPrefixCount = o.PersistentPrefixCount,
            RepaintBoundary = o.RepaintBoundary,
            ItemCountSignal = o.CountSignal,
            Removal = o.Removal,
            Disclosure = o.Disclosure,
            Insertion = o.Insertion,
        });
    }

    /// <summary>The SIGNALS-FIRST bound factory: the same WinUI ItemsView substrate (selection model, keyboard nav,
    /// typeahead, invoke, controller, reorder) but rows are PERSISTENT bound slots instead of a rebuilt-per-index
    /// template. <paramref name="rowTemplate"/> is invoked ONCE per visible slot with a <see cref="RowScope"/> (the
    /// index SIGNAL + reactive IsSelected/IsCurrent/IsEnabled predicates + the interaction/focus callbacks) and must
    /// return the COMPLETE slot root — express everything that varies by index as a bind that reads the scope, and wrap
    /// content in <see cref="SelectorVisualsBound"/> chrome (or a custom skin). Scrolling/selection/now-playing then
    /// re-skin in place via signal writes — no list re-render, no row rebuild, no Enter-transition replay. Requires a
    /// VIRTUAL layout (Stack/Grid/Custom); the small-collection Wrap/Inline fallback has no bound path.</summary>
    public static Element CreateBound(int itemCount, Func<RowScope, Element> rowTemplate, RepeatLayout layout,
                                      ListOptions? options = null)
    {
        var o = options ?? ListOptions.Default;
        return Embed.Comp(() => new ItemsView
        {
            ItemCount = itemCount,
            ItemCountSignal = o.CountSignal,
            RowTemplate = rowTemplate,
            BoundMode = true,
            StaggerColdRealize = o.Entrance?.StaggerColdRealize ?? false,
            Layout = layout,
            HasExplicitLayout = true,
            SelectionMode = o.SelectionMode,
            Selection = o.Selection,
            IsItemInvokedEnabled = o.IsItemInvokedEnabled,
            ItemInvoked = o.OnInvoked,
            SelectionChanged = o.OnChange,
            ItemText = o.ItemText,
            IsItemEnabled = o.IsItemEnabled,
            Controller = o.Controller,
            VerticalScrollController = o.Scroll?.VerticalScrollController,
            OverscanItems = o.Overscan,
            Grow = o.Grow,
            SuppressScrollBar = o.Scroll?.SuppressScrollBar ?? false,
            ScrollKey = o.Scroll?.ScrollKey,
            ScrollTimeline = o.Scroll?.ScrollTimeline,
            ItemClipTopInset = o.Scroll?.ItemClipTopInset ?? float.NaN,
            ItemClipTopFadeBand = o.Scroll?.ItemClipTopFadeBand ?? 0f,
            EdgeCues = o.Scroll?.EdgeCues ?? ScrollEdgeCues.Auto,
            AutoEdgeFade = o.Scroll?.AutoEdgeFade ?? false,
            AutoEdgeFadeBand = o.Scroll?.AutoEdgeFadeBand ?? 0f,
            OnScrollGeometryChanged = o.Scroll?.OnScrollGeometryChanged,
            OnVisibleRange = o.OnVisibleRange,
            Snap = o.Scroll?.Snap,
            ItemDisplacement = o.Reorder?.ItemDisplacement,
            DisplacementVersion = o.Reorder?.DisplacementVersion,
            DraggedSlot = o.Reorder?.DraggedSlot,
            ItemFlipFrom = o.Entrance?.ItemFlipFrom,
            ItemFadeFrom = o.Entrance?.ItemFadeFrom,
            Removal = o.Removal,
            Disclosure = o.Disclosure,
            Insertion = o.Insertion,
            ContentType = o.ContentType,
            CacheExtentPx = o.CacheExtentPx,
            PersistentPrefixCount = o.PersistentPrefixCount,
            RepaintBoundary = o.RepaintBoundary,
            KeepAlive = o.KeepAlive,
            KeepAliveCap = o.KeepAliveCap,
        });
    }

    /// <summary>The typed bound factory. The collection snapshot and recycled slot index are resolved together through
    /// <paramref name="items"/>, so every row property can read <see cref="BoundItemScope{T}.Item"/> without capturing
    /// a mount-time list instance. The row template still runs once per slot; source changes re-run only computations
    /// that read the item signal. Typed callbacks resolve the current item at invocation time.</summary>
    public static Element CreateBound<T>(BoundItemsSource<T> items, Func<BoundItemScope<T>, Element> rowTemplate,
                                         RepeatLayout layout, ListOptions<T>? options = null)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(rowTemplate);
        var o = options ?? new ListOptions<T>();

        // Typed callbacks WIN over the untyped ones (both resolve the current item at invocation time — no capture).
        Action<int, T>? typedInvoke = o.OnInvokedTyped;
        Action<int>? untypedInvoke = o.OnInvoked;
        Action<int>? invoke = typedInvoke is not null
            ? i => { if (items.TryPeek(i, out var item)) typedInvoke(i, item); }
            : untypedInvoke;

        Func<int, T, string>? typedText = o.ItemTextTyped;
        Func<int, string>? text = typedText is not null
            ? i => items.TryPeek(i, out var item) ? typedText(i, item) : string.Empty
            : o.ItemText;

        Func<int, T, bool>? typedEnabled = o.IsItemEnabledTyped;
        Func<int, bool>? enabled = typedEnabled is not null
            ? i => items.TryPeek(i, out var item) && typedEnabled(i, item)
            : o.IsItemEnabled;

        // Rebuild a base ListOptions with the bridged callbacks + the typed source's reactive count.
        var baseOptions = new ListOptions
        {
            SelectionMode = o.SelectionMode,
            Selection = o.Selection,
            IsItemInvokedEnabled = o.IsItemInvokedEnabled,
            OnInvoked = invoke,
            OnChange = o.OnChange,
            ItemText = text,
            IsItemEnabled = enabled,
            Controller = o.Controller,
            Overscan = o.Overscan,
            Grow = o.Grow,
            Selector = o.Selector,
            ContainerFactory = o.ContainerFactory,
            KeyOf = o.KeyOf,
            Transition = o.Transition,
            PartDelta = o.PartDelta,
            CountSignal = items.Count,
            Scroll = o.Scroll,
            Reorder = o.Reorder,
            Insertion = o.Insertion,
            Entrance = o.Entrance,
            Removal = o.Removal,
            Disclosure = o.Disclosure,
            ContentType = o.ContentType,
            CacheExtentPx = o.CacheExtentPx,
            PersistentPrefixCount = o.PersistentPrefixCount,
            RepaintBoundary = o.RepaintBoundary,
            KeepAlive = o.KeepAlive,
            KeepAliveCap = o.KeepAliveCap,
            OnVisibleRange = o.OnVisibleRange,
        };

        return CreateBound(
            items.Count.Peek(),
            scope => rowTemplate(new BoundItemScope<T>(scope, items.BindItem(scope.Index))),
            layout,
            baseOptions);
    }

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
        var ownModel = UseMemo(static () => new SelectionModel(), DepKey.Empty);
        var current = UseSignal(-1);                       // CurrentItemIndex (idl:46-47, default −1)
        var viewportNode = UseRef(NodeHandle.Null);        // the VirtualListEl scene node (OnRealized capture)
        var subscribed = UseRef<SelectionModel?>(null);
        var typeBuffer = UseRef(new System.Text.StringBuilder());
        var typeLastMs = UseRef(0L);
        var pendingFocus = UseRef(-1);
        var lastTabStop = UseRef(-1);                      // bound mode: the index currently holding the roving tab stop
        var insertionRef = UseRef<ItemsViewInsertion?>(null);
        var lastEntranceVer = UseRef(int.MinValue);        // last DisplacementVersion the entrance seeds were applied for
        var post = UsePost();                              // consumes no hook cell (safe to call unconditionally)

        // The framework-owned sortable core. Created once per mount (the options record is frozen at mount like every
        // other unpacked option), then fed the view's LIVE geometry below — the app never supplies a coordinate.
        ItemsViewInsertion? insertion = null;
        if (Insertion is { } insertionOptions)
            insertion = insertionRef.Value ??= new ItemsViewInsertion(insertionOptions);
        int insertionVer = insertion?.Version.Value ?? 0;   // subscribe — a gap open/retarget/clear re-seeds + re-previews

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
        int disclosureVer = Controller?.DisclosureVersion.Value ?? 0;
        int disclosureSourceVer = Disclosure?.Version?.Value ?? 0;
        if (Controller is { } disclosureOwner && Disclosure?.PendingExpand?.Invoke() is { } preparedRange
            && preparedRange.FirstIndex >= 0 && preparedRange.Count > 0
            && preparedRange.FirstIndex + preparedRange.Count <= count)
            disclosureOwner.PrepareExpand(preparedRange,
                Disclosure.OnExpandSettled is { } onSettled ? () => onSettled(preparedRange) : null);
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
            DepKey.From(HashCode.Combine((int)spec.Kind, spec.Extent, spec.Gap, spec.Columns, spec.MinCellWidth, spec.Estimate, spec.Horizontal, spec.CustomLayout)));
        bool horizontal = spec.Horizontal;

        var sceneRef = Context.Scene;
        ScrollGeometryObserverMux? geometryMux = UseMemo(
            () => VerticalScrollController is null || horizontal
                ? null
                : new ScrollGeometryObserverMux(VerticalScrollController, OnScrollGeometryChanged),
            DepKey.Combine(DepKey.FromRef(VerticalScrollController),
                OnScrollGeometryChanged is { } observer
                    ? DepKey.FromRef(observer.Project, observer.Action)
                    : DepKey.Empty));
        var geometryObserver = geometryMux is null
            ? OnScrollGeometryChanged
            : ((Func<ScrollGeometry, long>)geometryMux.Project,
               (Action<ScrollGeometry>)geometryMux.OnGeometryChanged);

        if (insertion is { } ins)
        {
            // Everything the insertion needs is ALREADY here: the scene, the memoized virtual layout (measured bands
            // included), the axis, the live item count and the persistent prefix. This is the whole point of ruling (f):
            // the values apps used to shovel into a hand-wired lane are the view's own state.
            ins.Scene = sceneRef;
            ins.Layout = layout;
            ins.Horizontal = horizontal;
            ins.ItemCount = count;
            ins.Prefix = Math.Clamp(PersistentPrefixCount, 0, count);
            ins.FallbackExtent = spec.Extent > 0f ? spec.Extent
                               : spec.Estimate > 0f ? spec.Estimate : ListItemExtent;
            ins.ViewportOf = () => viewportNode.Value;
            ins.Post = post;
        }

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

        // Targeting is OURS (an index resolves through the virtual layout MODEL, so this works for an item that is not
        // realized yet and therefore has no node); the offset WRITE is the shared ScrollIntoView.ScrollTo seam.
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
                // A halo-bleed FillRowVirtualLayout positions item i at LeadInset+i·stride inside a viewport widened by
                // the same gutter; an aligned (paged) bring-into-view must land the item at its REST screen position
                // (the gutter), not flush to the widened edge — subtract the lead gutter so a page offset cancels to
                // i·stride and the card stays pixel-identical to the pre-inset shelf. Minimal-scroll (NaN, keyboard nav)
                // is left alone — keep-visible needs no gutter correction.
                if (layout is FillRowVirtualLayout frl && frl.LeadInset != 0f)
                    target -= frl.LeadInset;
            }

            // Animated (WinUI AnimationDesired) arms the phase-7 ScrollIntegrator for the crit-damped programmatic
            // chase; snap (default) writes Offset==Target and applies the -offset content transform now. Clamping to
            // [0, content − viewport] happens inside the seam.
            ScrollIntoView.ScrollTo(Context, vp, target, animate);
        }

        bool TryGetItemAtViewport(float horizontalRatio, float verticalRatio, out int index)
        {
            index = -1;
            if (sceneRef is null || count <= 0
                || !float.IsFinite(horizontalRatio) || !float.IsFinite(verticalRatio)) return false;
            var vp = viewportNode.Value;
            if (vp.IsNull || !sceneRef.IsLive(vp) || !sceneRef.TryGetScroll(vp, out var sc)) return false;
            var liveLayout = sc.Layout;
            if (liveLayout is null) return false;

            float viewportW = MathF.Max(0f, sc.ViewportW), viewportH = MathF.Max(0f, sc.ViewportH);
            float cross = horizontal ? sc.ContentH : sc.ContentW;
            if (cross <= 0f) cross = horizontal ? viewportH : viewportW;
            float main = horizontal
                ? sc.OffsetX + Math.Clamp(horizontalRatio, 0f, 1f) * viewportW
                : sc.OffsetY + Math.Clamp(verticalRatio, 0f, 1f) * viewportH;

            int candidate;
            if (liveLayout is IMeasuredVirtualLayout measured) candidate = measured.IndexAt(main, cross);
            else liveLayout.Window(count, cross, 1f, main, 0, out candidate, out _);
            candidate = Math.Clamp(candidate, 0, count - 1);

            var rect = liveLayout.ItemRect(candidate, cross);
            float contentX = sc.OffsetX + Math.Clamp(horizontalRatio, 0f, 1f) * viewportW;
            float contentY = sc.OffsetY + Math.Clamp(verticalRatio, 0f, 1f) * viewportH;
            if (!rect.Contains(new Point2(contentX, contentY))) return false;
            index = candidate;
            return true;
        }

        // The REALIZED container node for an index: persistent-prefix indices map 1:1 to the leading children; a normal
        // index maps to prefix + index − FirstRealized (Null when outside the window). Non-virtual hosts (Wrap/Inline
        // fallback) have no scroll state: every container is a direct child of the captured host box, so ord == index.
        // Shared by FocusIndex and the bound-mode roving tab stop.
        NodeHandle SlotRootForIndex(int index)
        {
            if (sceneRef is null) return NodeHandle.Null;
            var vp = viewportNode.Value;
            if (vp.IsNull || !sceneRef.IsLive(vp)) return NodeHandle.Null;
            NodeHandle first;
            int ord;
            if (sceneRef.TryGetScroll(vp, out var sc))
            {
                int prefix = Math.Clamp(sc.PersistentPrefixCount, 0, sc.ItemCount);
                if (index < prefix) ord = index;
                else
                {
                    ord = prefix + index - sc.FirstRealized;
                    if (index < sc.FirstRealized || index >= sc.LastRealized) return NodeHandle.Null;
                }
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
            // Zoom-scaled max — the dispatcher's clamp contract (identical on the ZoomFactor==1 path).
            float zr = sc.ZoomFactor > 0f ? sc.ZoomFactor : 1f;
            float maxOffset = MathF.Max(0f, content * zr - viewport);
            float target = Math.Clamp(offsetNow + delta, 0f, maxOffset);
            if (target == offsetNow) return;
            if (horizontal) { sc.OffsetX = target; sc.TargetX = target; }
            else { sc.OffsetY = target; sc.TargetY = target; }
            var contentNode = sc.ContentNode;
            if (!contentNode.IsNull && sceneRef.IsLive(contentNode))
            {
                // Device-pixel-snapped, zoom-aware, band-composed — the shared writer (a raw Translation here painted
                // an unsnapped/unzoomed transform until the next ArrangeViewport healed it).
                ref NodePaint paint = ref sceneRef.Paint(contentNode);
                float band = OverscrollPhysics.GuardBandSign(sc.OverscrollPx, target, maxOffset);
                if (sc.Overscrolling && band != sc.OverscrollPx) sc.OverscrollPx = band;
                OverscrollPhysics.WriteContentTransform(ref paint, in sceneRef.Bounds(contentNode), horizontal, target,
                    band, sc.ZoomFactor, sceneRef.DeviceScale);
                sceneRef.Mark(contentNode, NodeFlags.TransformDirty | NodeFlags.PaintDirty);
            }
            sceneRef.Mark(vp, NodeFlags.VirtualRangeDirty);
            // WAKE, don't re-render. Everything this scroll needs is already written above: the offset + target on the
            // ScrollState POD, the content transform, and VirtualRangeDirty on the viewport — which the reconciler's
            // ReRealizeVirtuals drains granularly each frame ("no component re-render", Reconciler.ReRealizeVirtuals).
            // Re-rendering the whole ItemsView produced a byte-identical element tree (same count, memoized layout,
            // same VirtualListEl props) and was the entire measured UI-thread allocation of a programmatic scroll —
            // the list element, every template/rowBind closure and the keyed diff of the result, rebuilt per ScrollBy.
            // The frame WAKE is the only load-bearing part, so take that alone. (The engine's own wheel/fling path
            // never re-rendered here — its RequestRerender is already wired to WakeFrame — so this makes the
            // controller-driven scroll behave like the input-driven one.)
            (Context.RequestFrame ?? Context.RequestRerender)();
        }

        // An off-screen variable row cannot feed its collapsed size through ArrangeVirtualMeasured. Apply that one
        // explicit correction atomically with the SAME anchor-intent rebase as FlexLayout.RecordAnchorShift, so a live
        // wheel/programmatic/touch phase continues in the corrected coordinate space instead of undoing the pin.
        bool CorrectMeasuredExtent(IMeasuredVirtualLayout expectedLayout, int index, float mainExtent)
        {
            if (sceneRef is null || !float.IsFinite(mainExtent) || mainExtent < 0f) return false;
            var vp = viewportNode.Value;
            if (vp.IsNull || !sceneRef.IsLive(vp) || !sceneRef.HasScroll(vp)) return false;
            ref ScrollState sc = ref sceneRef.ScrollRef(vp);
            if (!ReferenceEquals(sc.Layout, expectedLayout) || (uint)index >= (uint)sc.ItemCount) return false;

            bool horizontal = sc.Orientation == 1;
            float viewport = horizontal ? sc.ViewportW : sc.ViewportH;
            // The INNER cross the arrange paths use (the content's published cross extent, viewport-fallback) — see
            // ApplyScrollPosition's viewport-check comment: a viewport-vs-inner mismatch makes a width-keyed measured
            // layout reseed its extent table on every alternation between this seam and arrange (the felt jitter).
            float cross = horizontal ? (sc.ContentH > 0f ? sc.ContentH : sc.ViewportH)
                                     : (sc.ContentW > 0f ? sc.ContentW : sc.ViewportW);
            if (!(viewport > 0f) || !(cross > 0f)) return false;
            if (expectedLayout is IViewportVirtualLayout viewportLayout)
                viewportLayout.SetViewport(viewport, cross);
            _ = expectedLayout.ContentExtent(sc.ItemCount, cross); // initialize stateful measured layouts before SetMeasured

            float oldOffset = horizontal ? sc.OffsetX : sc.OffsetY;
            int anchorIndex = Math.Clamp(expectedLayout.IndexAt(oldOffset, cross), 0, sc.ItemCount - 1);
            float anchorWithin = oldOffset - expectedLayout.OffsetOf(anchorIndex, cross);
            float oldMain = horizontal ? expectedLayout.ItemRect(index, cross).W : expectedLayout.ItemRect(index, cross).H;
            if (oldMain == mainExtent) return true;

            expectedLayout.SetMeasured(index, mainExtent, cross);
            float mainContent = expectedLayout.ContentExtent(sc.ItemCount, cross);
            // Zoom-scaled max, the dispatcher's clamp contract (identical on the ZoomFactor==1 path).
            float zc = sc.ZoomFactor > 0f ? sc.ZoomFactor : 1f;
            float maxOffset = MathF.Max(0f, mainContent * zc - viewport);
            float pinned = Math.Clamp(expectedLayout.OffsetOf(anchorIndex, cross) + anchorWithin, 0f, maxOffset);
            float delta = pinned - oldOffset;

            if (horizontal) { sc.OffsetX = pinned; sc.ContentW = mainContent; }
            else            { sc.OffsetY = pinned; sc.ContentH = mainContent; }
            sc.AnchorIndex = anchorIndex;
            sc.RebaseAnchorIntents(delta, horizontal);
            // This seam can run after phase 6, so clamp chase destinations against the corrected extent immediately;
            // otherwise phase 7 gets one tick against an out-of-range target before ArrangeViewport can reclamp it.
            if (horizontal)
            {
                sc.TargetX = Math.Clamp(sc.TargetX, 0f, maxOffset);
                if (!float.IsNaN(sc.PendingTargetX)) sc.PendingTargetX = Math.Clamp(sc.PendingTargetX, 0f, maxOffset);
            }
            else
            {
                sc.TargetY = Math.Clamp(sc.TargetY, 0f, maxOffset);
                if (!float.IsNaN(sc.PendingTargetY)) sc.PendingTargetY = Math.Clamp(sc.PendingTargetY, 0f, maxOffset);
            }

            float band = OverscrollPhysics.GuardBandSign(sc.OverscrollPx, pinned, maxOffset);
            if (sc.Overscrolling && band != sc.OverscrollPx) sc.OverscrollPx = band;

            var contentNode = sc.ContentNode;
            if (!contentNode.IsNull && sceneRef.IsLive(contentNode))
            {
                ref NodePaint paint = ref sceneRef.Paint(contentNode);
                OverscrollPhysics.WriteContentTransform(ref paint, in sceneRef.Bounds(contentNode), horizontal, pinned,
                    band, sc.ZoomFactor, sceneRef.DeviceScale);
                sceneRef.Mark(contentNode, NodeFlags.TransformDirty | NodeFlags.PaintDirty);
            }
            sceneRef.Mark(vp, NodeFlags.LayoutDirty | NodeFlags.VirtualRangeDirty | NodeFlags.PaintDirty);
            (Context.RequestFrame ?? Context.RequestRerender)();
            return true;
        }

        void ControllerScrollTo(ScrollToRequest request)
        {
            if (horizontal || sceneRef is null) return;
            var vp = viewportNode.Value;
            if (vp.IsNull || !sceneRef.IsLive(vp) || !sceneRef.HasScroll(vp)) return;
            ScrollIntoView.ScrollTo(Context, vp, request.Offset, request.Animate);
        }

        void ControllerScrollBy(ScrollByRequest request)
        {
            if (horizontal || sceneRef is null) return;
            var vp = viewportNode.Value;
            if (vp.IsNull || !sceneRef.IsLive(vp) || !sceneRef.TryGetScroll(vp, out var sc)) return;
            // Accumulate on the live chase target when one is armed — the dispatcher's own wheel idiom
            // (SetPendingWheelTarget bases the next notch on PendingTarget, not the animating offset). Re-basing a
            // second notch on the mid-chase offset silently ate most of its travel.
            float from = request.Animate && sc.Phase == ScrollIntegrator.WheelAnimating && !float.IsNaN(sc.PendingTargetY)
                ? sc.PendingTargetY
                : sc.OffsetY;
            ScrollIntoView.ScrollTo(Context, vp, from + request.Delta, request.Animate);
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
            ctl.TryGetItemIndexImpl = TryGetItemAtViewport;
            ctl.GetCurrent = current.Peek;
            ctl.Selection = model;
            ctl.ScrollByImpl = ScrollByDelta;
            ctl.CorrectMeasuredExtentImpl = CorrectMeasuredExtent;
            ctl.GetViewportImpl = () => viewportNode.Value;
            ctl.GetOffsetImpl = () =>
            {
                if (sceneRef is null) return 0f;
                var vp = viewportNode.Value;
                if (vp.IsNull || !sceneRef.IsLive(vp) || !sceneRef.TryGetScroll(vp, out var sc)) return 0f;
                return horizontal ? sc.OffsetX : sc.OffsetY;
            };
            var removal = Removal;
            var beginRemoval = Context.BeginVirtualRemoval;
            ctl.BeginRemovalImpl = removal is null || beginRemoval is null
                ? null
                : (indices, commit) => beginRemoval(
                    viewportNode.Value, indices, removal.Exit, removal.Motion, removal.StaggerMs, commit);
            ctl.CompleteDisclosureImpl = expanded
                => Context.CompleteVirtualDisclosure?.Invoke(viewportNode.Value, expanded);
            ctl.DisclosureDiagnostic = Disclosure?.Diagnostic;
            ctl.ObserveInsertionMembershipImpl = insertion is null ? null : insertion.ObserveMembership;
        }

        UseEffect(() =>
        {
            var controller = VerticalScrollController;
            if (controller is null || horizontal) return null;
            controller.ScrollToRequested += ControllerScrollTo;
            controller.ScrollByRequested += ControllerScrollBy;
            return () =>
            {
                controller.ScrollToRequested -= ControllerScrollTo;
                controller.ScrollByRequested -= ControllerScrollBy;
                controller.SetIsScrollable(false);
            };
        }, DepKey.FromRef(VerticalScrollController));

        UseEffect(() =>
        {
            var ctl = Controller;
            if (ctl is null) return null;
            return () =>
            {
                ctl.BringIntoViewImpl = null;
                ctl.TryGetItemIndexImpl = null;
                ctl.GetCurrent = null;
                ctl.ScrollByImpl = null;
                ctl.CorrectMeasuredExtentImpl = null;
                ctl.GetViewportImpl = null;
                ctl.GetOffsetImpl = null;
                ctl.BeginRemovalImpl = null;
                ctl.ObserveInsertionMembershipImpl = null;
                ctl.CompleteDisclosureImpl = null;
                ctl.Selection = null;
            };
        }, DepKey.FromRef(Controller));

        // Seed after the changed item model has reconciled and laid out, but before paint: an expanding range therefore
        // starts clipped at zero instead of flashing once at full height.
        UseLayoutEffect(() =>
        {
            if (Controller is not { } ctl) return;
            var viewport = viewportNode.Value;
            bool countReady = count <= ctl.DisclosureClearAtCount;
            bool sourceReady = !ctl.DisclosureTracksSourceVersion
                || disclosureSourceVer >= ctl.DisclosureClearAtSourceVersion;
            if (ctl.DisclosureNeedsClear && countReady && sourceReady
                && Context.ClearVirtualDisclosure is { } clear)
            {
                clear(viewport);
                ctl.DisclosureCleared(recovery: false, count, disclosureSourceVer);
            }
            else if (!ctl.DisclosureNeedsClear && ctl.PendingDisclosure is null && ctl.ActiveDisclosure is null
                     && Context.Scene is { } scene && scene.TryGetScroll(viewport, out var stale)
                     && float.IsFinite(stale.DisclosureT) && Context.ClearVirtualDisclosure is { } recover)
            {
                // A remount/interrupted owner must never inherit a finite disclosure clip forever.
                recover(viewport);
                ctl.DisclosureCleared(recovery: true, count, disclosureSourceVer);
            }
            if (ctl.PendingDisclosure is not { } request) return;
            ctl.StartDisclosure(request, count, disclosureSourceVer, Disclosure?.Version is not null);
            bool started = Context.BeginVirtualDisclosure?.Invoke(
                viewport, request.Range.FirstIndex, request.Range.Count,
                request.Direction == ItemDisclosureDirection.Expand) == true;
            if (started)
            {
                ctl.ArmDisclosure();
                if (request.PreparedExpansion) Disclosure?.OnExpandStarted?.Invoke(request.Range);
            }
            else
            {
                ctl.TraceFailure();
                ctl.SettleDisclosure();
            }
        }, DepKey.From(HashCode.Combine(disclosureVer, disclosureSourceVer, count)));

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
        // each realize from ItemDisplacement (recycling-safe). The seed goes through AnimEngine.SeedValue under the
        // host-owned MotionTok.ItemPlacement token, so the ENGINE owns the dynamics and the reduced-motion policy; this
        // body is cold/edge-triggered, never a frame phase.
        UseLayoutEffect(() =>
        {
            var ins = insertion;
            // A configured insertion OWNS the displacement while its gap is open; the external ReorderOptions provider
            // (the non-insertion consumers) keeps working untouched otherwise. Virtual removal also drives a per-row
            // OPACITY seed — a same-list source is "in the chip", so it must HIDE, not merely dim (design ruling (a)).
            var disp = ins is { Active: true } ? ins.Displacement : ItemDisplacement;
            bool insHides = ins is not null && (ins.HidesSources || ins.OpacityEngaged);
            var anim = Context.Anim;
            if (anim is null || sceneRef is null) return;
            var motion = DisplacementMotion;           // one token read per bump (readonly POD — no per-row resolve, no alloc)
            // ENTRANCE seeds (the owner's FLIP/fade choreography) belong to exactly ONE DisplacementVersion bump: the
            // write that filled them. An insertion gap open/retarget/clear re-runs this effect on its OWN channel, and
            // replaying a spent choreography there is a phantom half-fade on rows that never moved (A8). Gate them on
            // the entrance channel's own edge — the app no longer has to hand-guard a shared bus.
            bool entranceEdge = lastEntranceVer.Value != dispVer;
            lastEntranceVer.Value = dispVer;
            var flip = entranceEdge ? ItemFlipFrom : null;   // optional FLIP start override (data-reorder glide)
            var fade = entranceEdge ? ItemFadeFrom : null;   // optional opacity seed + stagger delay (added-row ease-in)
            if (disp is null && !insHides && flip is null && fade is null) return;
            var vp = viewportNode.Value;
            if (vp.IsNull || !sceneRef.IsLive(vp)) return;
            int dragged = DraggedSlot?.Peek() ?? -1;   // resting index whose translate DragController owns (skip the seed)

            NodeHandle first; int restingBase, prefix;
            if (sceneRef.TryGetScroll(vp, out var sc))
            {
                // Canonical realized-ordinal → item mapping (FlexLayout.VirtualIndex): the sticky persistent prefix
                // occupies the LEADING ordinals 1:1 and the recyclable window starts after it. A flat
                // FirstRealized + ord displaces the wrong rows entirely once a prefix exists (the sticky hero moves).
                prefix = Math.Clamp(sc.PersistentPrefixCount, 0, sc.ItemCount);
                restingBase = sc.FirstRealized;
                first = sceneRef.FirstChild(sc.ContentNode);
            }
            else
            {
                prefix = 0; restingBase = 0;           // non-virtual fallback (Wrap/Inline): ord == index
                first = sceneRef.FirstChild(vp);
            }

            var n = first;
            bool anyHidden = false;
            for (int ord = 0; !n.IsNull && sceneRef.IsLive(n); ord++, n = sceneRef.NextSibling(n))
            {
                int item = ord < prefix ? ord : restingBase + ord - prefix;
                // Skip the pointer-dragged ghost UNCONDITIONALLY. Its translate is owned by DragController, which
                // re-asserts it every move; OffsetFor(dragged)==0 does NOT make the seed a no-op here, because `fromY`
                // below is the LIVE drag translate, so |0 − fromY| > eps fires a Replace TranslateY track that fights
                // DragController for the node: AnimEngine.Tick folds it absolutely and overwrites the drag translate,
                // then DragController.RetargetFromRest double-counts the stomped origin per frame into an unbounded
                // runaway (the ghost flies off the page). The scene's DragGhost flag is the ground truth (set by
                // DragController.Promote/ApplyPresented), so this holds even when DraggedSlot is unwired (every preset
                // currently leaves it null) or its index doesn't align with the realized window.
                if ((sceneRef.Flags(n) & NodeFlags.DragGhost) != 0 || item == dragged) continue;
                (float dx, float dy) = disp is null ? (0f, 0f) : disp(item);   // (0,0) for non-displaced items
                var fd = fade?.Invoke(item);           // opacity seed (from→1) + the row's stagger delay
                float delay = fd?.delayMs ?? 0f;
                ref NodePaint p = ref sceneRef.Paint(n);
                var f = flip?.Invoke(item);            // FLIP "first": start from the OLD visual position, not the live translate
                float fromX = f?.dx ?? p.LocalTransform.Dx, fromY = f?.dy ?? p.LocalTransform.Dy;   // deadband reference
                // `from:` is supplied ONLY for the FLIP override; null lets SeedValue read the row's LIVE value, which on
                // an in-flight placement row is a velocity-continuous retarget instead of a restart (the drag cadence
                // wants exactly that, and it equals `fromX/fromY` when the row is at rest).
                if (MathF.Abs(dx - fromX) > DisplacementEpsilon)
                    anim.SeedValue(n, AnimChannel.TranslateX, dx, in motion, from: f?.dx, delayMs: delay);
                if (MathF.Abs(dy - fromY) > DisplacementEpsilon)
                    anim.SeedValue(n, AnimChannel.TranslateY, dy, in motion, from: f?.dy, delayMs: delay);
                if (fd is { } o)
                    anim.SeedValue(n, AnimChannel.Opacity, 1f, in motion, from: o.from, delayMs: o.delayMs);
                if (!insHides) continue;
                // Virtual removal: hide the dragged rows, restore everyone else. Written through the SAME seed so it
                // survives recycling and so the restore is a glide, not a pop, when the gesture ends.
                float opacity = ins!.IsDraggedSource(item) ? 0f : 1f;
                if (opacity <= 0f) anyHidden = true;
                if (MathF.Abs(opacity - p.Opacity) > 0.01f)
                    anim.SeedValue(n, AnimChannel.Opacity, opacity, in motion, delayMs: delay);
            }
            if (ins is not null) ins.OpacityEngaged = anyHidden;
        }, HashCode.Combine(dispVer, insertionVer));

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

        // #16 RepaintBoundary: isolate each realized item container's layout/paint (IsolateLayout + ClipToBounds) so an
        // item's internal invalidation can't escape to relayout the whole list. Applied to the virtual realize paths.
        if (RepaintBoundary)
        {
            if (rowBind is { } rb)
                rowBind = i => { var e = rb(i); return e is BoxEl b ? b with { IsolateLayout = true, ClipToBounds = true } : e; };
            var rt = realizeTemplate;
            realizeTemplate = i => { var e = rt(i); return e is BoxEl b ? b with { IsolateLayout = true, ClipToBounds = true } : e; };
        }

        Element itemsHost = rowBind is not null && layout is not null
            // Bound slots: the RowBind path (RealizeBoundWindow) — persistent rows, recycle by index-signal write.
            ? new VirtualListEl
            {
                ItemCount = count,
                ItemLayout = layout,
                RowBind = rowBind,
                StaggerColdRealize = StaggerColdRealize,
                Overscan = OverscanItems,
                CacheExtentPx = CacheExtentPx,
                PersistentPrefixCount = PersistentPrefixCount,
                ContentType = ContentType,       // #16 recycle-pool discriminator (bound path)
                KeepAlive = KeepAlive,           // #5 keep-alive-but-hidden bucket (bound path)
                KeepAliveCap = KeepAliveCap,
                Horizontal = horizontal,
                EdgeCues = EdgeCues,
                AutoEdgeFade = AutoEdgeFade,
                AutoEdgeFadeBand = AutoEdgeFadeBand,
                SuppressScrollBar = SuppressScrollBar,
                ScrollKey = ScrollKey,
                ScrollTimeline = ScrollTimeline,
                ItemClipTopInset = ItemClipTopInset,
                ItemClipTopFadeBand = ItemClipTopFadeBand,
                OnScrollGeometryChanged = geometryObserver,
                OnVisibleRange = OnVisibleRange,   // viewport-driven hydration (realized-window change)
                Snap = Snap,
                Grow = Grow,
                OnRealized = h => viewportNode.Value = h,
            }
            : layout is not null
            ? new VirtualListEl
            {
                ItemCount = count,
                ItemLayout = layout,
                RenderItem = realizeTemplate,
                KeyOf = KeyOf,
                Overscan = OverscanItems,
                CacheExtentPx = CacheExtentPx,   // #16 pixel cache extent (both paths)
                Horizontal = horizontal,
                EdgeCues = EdgeCues,
                AutoEdgeFade = AutoEdgeFade,
                AutoEdgeFadeBand = AutoEdgeFadeBand,
                SuppressScrollBar = SuppressScrollBar,
                ScrollKey = ScrollKey,
                ScrollTimeline = ScrollTimeline,
                ItemClipTopInset = ItemClipTopInset,
                ItemClipTopFadeBand = ItemClipTopFadeBand,
                OnScrollGeometryChanged = geometryObserver,
                OnVisibleRange = OnVisibleRange,   // viewport-driven hydration (realized-window change)
                Snap = Snap,
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

        // Declarative insertion: the view hosts its OWN drop surface — the list body, the in-gap preview and the
        // accent line stacked over one accepting target. Every consumer that sets Insertion gets the premiere
        // destination by declaration; a view without it is byte-identical to before (no wrapper node at all).
        if (insertion is { } insHost)
            itemsHost = new BoxEl
            {
                Key = "fg-insertion-host",
                ZStack = true, Grow = Grow, Shrink = 1f, MinHeight = 0f, ClipToBounds = true,
                DropTarget = insHost.Spec,
                Children =
                [
                    itemsHost,
                    Embed.Comp(() => new ItemsViewInsertionPreview(insHost)),
                    insHost.Line(),
                ],
            };

        ItemsViewController? disclosureController = Controller;
        bool watchesDisclosure = disclosureController is not null
            && (disclosureController.PendingDisclosure is not null || disclosureController.ActiveDisclosure is not null);
        Element[] rootChildren = watchesDisclosure
            ? [itemsHost, Embed.Comp(() => new ItemsViewDisclosureWatcher(disclosureController!))]
            : [itemsHost];

        return new BoxEl
        {
            // The root stacks along the LIST axis (D1 hygiene): a vertical view is a column so Grow distributes the
            // missing axis to the viewport; a horizontal shelf stays a row. Cross axis fills via the default stretch.
            Direction = horizontal ? (byte)0 : (byte)1,
            Grow = Grow,
            OnKeyDown = OnRootKey,      // bubbles up from the focused ItemContainer (dispatcher key routing)
            OnCharInput = OnRootChar,   // typeahead
            Children = rootChildren,
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

/// <summary>
/// THE framework-owned sortable core behind <see cref="InsertionOptions"/> — everything a live insertion needs, driven
/// entirely from the view's OWN geometry. It owns the <see cref="DropTargetSpec"/>, resolves the slot through
/// <see cref="SortableMath"/> against the virtual layout's MEASURED bands, publishes the reflow plan (one
/// <see cref="InsertionPlan"/>, so gap and preview can never disagree), and runs the drop/teardown lifecycle
/// (epoch-gated commit, optimistic-membership handoff, unconditional clear on a refusal or a dead viewport).
/// <para>Steady-state cost per pointer move: no allocation — the source-index buffer is grow-only and every geometry
/// query is arithmetic over the layout seam.</para>
/// </summary>
internal sealed class ItemsViewInsertion
{
    internal const float LineThickness = 2f;      // researched insertion cue: a 2px accent line…
    internal const float TerminalDot = 8f;        // …with an 8px terminal dot at its leading end (colourblind-legible)

    /// <summary>Bumped whenever the published plan changes — the ItemsView subscribes, so the displacement seed and the
    /// in-gap preview both re-run on exactly the same edge (the two can never show different slots).</summary>
    internal readonly Signal<int> Version = new(0);
    /// <summary>Viewport-space main-axis position of the gap's leading edge (a bound transform, not a re-render).</summary>
    internal readonly Signal<float> Offset = new(0f);
    internal readonly Signal<int> SlotSignal = new(-1);

    /// <summary>The ItemsView displacement provider while a gap is open (one delegate, bound once — the seed loop
    /// calls it per realized row and must not allocate).</summary>
    internal readonly Func<int, (float dx, float dy)> Displacement;

    internal readonly InsertionOptions Options;
    internal readonly DropTargetSpec Spec;

    internal SceneStore? Scene;
    internal IVirtualLayout? Layout;
    internal Func<NodeHandle>? ViewportOf;
    internal Action<Action>? Post;
    internal bool Horizontal;
    internal int ItemCount;
    internal int Prefix;
    internal float FallbackExtent = ItemsView.ListItemExtent;
    /// <summary>Latched by the displacement seed: source rows are currently hidden, so the next pass must run even
    /// after the gesture ends (to animate them back to full opacity).</summary>
    internal bool OpacityEngaged;

    private int[] _sources = [];
    private int _sourceCount;
    private object? _payload;
    private InsertionPlan _plan;
    private NodeHandle _dragSource;

    // Gesture-local policy snapshot. A re-render mid-drag (a live membership push, a breakpoint cross, a sort change)
    // must not move the range the user is aiming at out from under the pointer. The GEOMETRY is read live instead —
    // it comes from the layout seam, which is self-consistent by construction.
    private bool _gesture;
    private int _gFirst, _gCount;
    private bool _gSameList;

    // Commit / optimistic-membership handoff.
    private object? _membershipToken, _commitBaseline;
    private int _commitEpoch;
    private bool _awaiting, _landedPending;
    private int _landedSlot, _landedCount;

    internal ItemsViewInsertion(InsertionOptions options)
    {
        Options = options;
        Displacement = DisplacementForItem;
        Spec = new DropTargetSpec(options.AcceptKinds, Enter, Over, Leave, Drop)
        {
            CanAccept = s => Accepts(s.Payload),
            VisualPolicy = options.VisualPolicy,
            SpotlightWhen = options.SpotlightWhen,
            Transparent = options.Transparent is { } skip ? s => skip(s.Payload) : null,
            RefusalCaption = options.RefusalCaption is { } why ? s => why(s.Payload) : null,
        };
    }

    internal bool Active => _plan.IsActive;
    internal bool HidesSources => _plan.IsActive && _plan.SameList && _sourceCount > 0;
    internal ReadOnlySpan<int> Sources => _sources.AsSpan(0, _sourceCount);
    internal bool IsDraggedSource(int item) => _plan.IsHiddenSource(item, Sources);

    /// <summary>The owner's optimistic-membership edge (see <see cref="ItemsViewController.ObserveInsertionMembership"/>):
    /// a NEW snapshot means the real list accepted the mutation, so the temporary gap can close into its FLIP without a
    /// blank intermediate frame.</summary>
    internal void ObserveMembership(object token)
    {
        _membershipToken = token;
        if (!_awaiting || ReferenceEquals(_commitBaseline, token)) return;
        _awaiting = false;
        Clear(releaseOverride: false);   // deferred: the gesture already ended, and with it the controller's release
        FireLanded();
    }

    // ── drop-target lifecycle ───────────────────────────────────────────────────────────────────────

    private void Enter(DragSession s)
    {
        (_gFirst, _gCount) = ResolveRange();
        _gSameList = Options.IsSameList?.Invoke(s.Payload) ?? false;
        _gesture = true;
        Over(s);
    }

    private void Over(DragSession s)
    {
        if (!Accepts(s.Payload)) { Clear(); return; }
        var scene = Scene;
        var getVp = ViewportOf;
        if (scene is null || getVp is null) { Clear(); return; }
        var vp = getVp();
        // A dead / not-yet-realized viewport cannot place a slot. Tear the projection DOWN instead of bailing bare: a
        // plain return strands the last gap and its preview at a stale offset for the rest of the gesture (A6).
        if (vp.IsNull || !scene.IsLive(vp)) { Clear(); return; }

        float offset = 0f, cross = 0f;
        if (scene.TryGetScroll(vp, out var sc))
        {
            offset = Horizontal ? sc.OffsetX : sc.OffsetY;
            cross = Horizontal ? sc.ViewportH : sc.ViewportW;
        }
        (int first, int count) = _gesture ? (_gFirst, _gCount) : ResolveRange();
        float leading = LeadingOf(first, cross);
        float extent = RepresentativeExtent(first, count, cross);
        if (!(extent > 0f)) { Clear(); return; }

        var rect = scene.AbsoluteRect(vp);
        float pointer = Horizontal ? s.Position.X - rect.X : s.Position.Y - rect.Y;
        float contentOffset = pointer + offset;
        int slot = SlotAt(contentOffset, first, count, leading, extent, cross);

        bool same = _gesture ? _gSameList : Options.IsSameList?.Invoke(s.Payload) ?? false;
        bool payloadChanged = !ReferenceEquals(_payload, s.Payload);
        if (payloadChanged)
        {
            _payload = s.Payload;
            CaptureSources(s.Payload, same, first, count);
        }
        int dragged = same && _sourceCount > 0 ? _sourceCount
                    : Math.Max(1, Options.DraggedCount?.Invoke(s.Payload) ?? 1);

        var plan = SortableMath.Plan(first, count, slot, dragged, extent, same, Options.PreviewCap);
        var sources = Sources;
        Offset.Value = plan.PreviewY(leading, offset, sources);
        // The press-source row is the ONE node DragController also writes opacity on (Stationary dim). L1 Move runs
        // before this L2 Over in every dispatch path, so the hide lands last and wins for the frame; the anim seed in
        // the displacement pass covers the post-reconcile re-assert. Only a source INSIDE this viewport is ours —
        // a foreign session's source (or an external drag rooted at the scene root) must never be touched.
        _dragSource = Contains(scene, vp, s.Source) ? s.Source : NodeHandle.Null;
        HideDragSource(scene, plan.SameList && _sourceCount > 0);

        s.Effect = same ? DropEffect.Move : DropEffect.Copy;
        if (Options.Caption is { } caption) s.Caption = caption(s.Payload, slot);

        if (plan == _plan && !payloadChanged) return;
        _plan = plan;
        SlotSignal.Value = plan.IsActive ? slot : -1;
        Bump();
    }

    private void Leave(DragSession _)
    {
        _gesture = false;
        if (!_awaiting) Clear();
    }

    private void Drop(DragSession s)
    {
        var plan = _plan;
        object? payload = s.Payload;
        // Accept is answered against the gesture snapshot; the scope closes only afterwards.
        bool accepted = plan.IsActive && Accepts(payload) && Options.OnDeposit is not null;
        _gesture = false;
        if (!accepted) { Clear(); return; }

        _commitBaseline = _membershipToken;
        int epoch = ++_commitEpoch;
        _landedSlot = plan.Slot;
        _landedCount = plan.DraggedCount;
        var commit = Options.OnDeposit!(payload, plan.Slot);
        // Hold the gap open only while a commit that can still publish a membership snapshot is in flight — that
        // snapshot is the handoff edge ObserveMembership waits for. A commit that already resolved WITHOUT issuing a
        // mutation owns no handoff, so latching on it would strand the gap forever.
        _awaiting = !commit.IsCompletedSuccessfully || commit.Result;
        _landedPending = _awaiting;
        if (!_awaiting) Clear();
        _ = CompleteAsync(commit, epoch);
    }

    /// <summary>The teardown fallback. The fast path is <see cref="ObserveMembership"/> handing the gap to the real
    /// list; a commit that faults, or that the backend collapses without publishing, still ends the wait here.</summary>
    private async Task CompleteAsync(Task<bool> commit, int epoch)
    {
        bool ok = false;
        try { ok = await commit.ConfigureAwait(false); }
        catch { ok = false; }
        finally
        {
            void Finish()
            {
                if (epoch != _commitEpoch) return;
                if (!ok) _landedPending = false;
                if (_awaiting) { _awaiting = false; Clear(releaseOverride: false); }   // deferred — see ObserveMembership
                FireLanded();
            }
            var post = Post;
            if (post is null) Finish(); else post(Finish);
        }
    }

    private void FireLanded()
    {
        if (!_landedPending) return;
        _landedPending = false;
        Options.OnLanded?.Invoke(_landedSlot, _landedCount);
    }

    /// <param name="releaseOverride">Also release <c>SceneStore.DragSourceOpacityOverride</c>. False on the DEFERRED
    /// teardowns (a membership hand-off / a commit that completes after the gesture): by then the drag is over and the
    /// controller has already released the override, so a late write would clear the dim a NEWER drag is holding —
    /// strobing that gesture's press-source row back to 0.4 while its siblings stay hidden.</param>
    private void Clear(bool releaseOverride = true)
    {
        bool changed = _plan.IsActive || _payload is not null || _sourceCount != 0;
        if (HidesSources && Scene is { } scene) HideDragSource(scene, false, releaseOverride);
        _plan = default;
        _payload = null;
        _sourceCount = 0;
        _dragSource = NodeHandle.Null;
        if (SlotSignal.Peek() >= 0) SlotSignal.Value = -1;
        // OpacityEngaged forces one more seed pass so hidden rows animate back to full opacity.
        if (changed || OpacityEngaged) Bump();
    }

    private void Bump() => Version.Value = Version.Peek() + 1;

    // ── policy ──────────────────────────────────────────────────────────────────────────────────────

    private bool Accepts(object? payload) => Options.CanAccept?.Invoke(payload) ?? true;

    private (int First, int Count) ResolveRange()
    {
        int total = Math.Max(0, ItemCount);
        if (Options.Range?.Invoke() is { } r)
        {
            int f = Math.Clamp(r.First, 0, total);
            return (f, Math.Clamp(r.Count, 0, total - f));
        }
        int prefix = Math.Clamp(Prefix, 0, total);
        return (prefix, total - prefix);
    }

    private void CaptureSources(object? payload, bool sameList, int first, int count)
    {
        _sourceCount = 0;
        if (!sameList || Options.SourceIndices is not { } resolve) return;
        var list = resolve(payload);
        if (list is null || list.Count == 0) return;
        if (_sources.Length < list.Count)
        {
            int cap = _sources.Length > 0 ? _sources.Length : 8;
            while (cap < list.Count) cap *= 2;
            _sources = new int[cap];                       // grow-only: a gesture pays this at most once
        }
        int n = 0;
        for (int i = 0; i < list.Count; i++)
        {
            int display = list[i];
            if ((uint)display < (uint)count) _sources[n++] = first + display;   // → absolute item space
        }
        _sourceCount = SortableMath.Normalize(_sources.AsSpan(0, n), int.MaxValue);
    }

    // ── geometry (all read from the view's own layout seam — never an app estimate) ──────────────────

    /// <summary>MEASURED content-space offset of the first insertable item: with a hero + chrome persistent prefix
    /// that is the real height those two items measured, which is exactly what the app used to guess.</summary>
    private float LeadingOf(int first, float cross)
    {
        var layout = Layout;
        if (first <= 0) return 0f;
        if (layout is null) return first * FallbackExtent;
        if (first >= ItemCount) return layout.ContentExtent(Math.Max(0, ItemCount), cross);
        var r = layout.ItemRect(first, cross);
        return Horizontal ? r.X : r.Y;
    }

    private float RepresentativeExtent(int first, int count, float cross)
    {
        var layout = Layout;
        if (layout is null || count <= 0 || first >= ItemCount) return FallbackExtent;
        var r = layout.ItemRect(first, cross);
        float e = Horizontal ? r.W : r.H;
        return e > 0f ? e : FallbackExtent;
    }

    private int SlotAt(float contentOffset, int first, int count, float leading, float extent, float cross)
    {
        if (count <= 0) return 0;                          // empty list: slot 0 (the drop appends, never discards)
        // A MEASURED layout answers "which item's band holds this offset" in O(log n) — the exact fix for a variable
        // row (an open versions drawer) making a uniform-stride slot land rows off.
        if (Layout is IMeasuredVirtualLayout measured)
        {
            int index = Math.Clamp(measured.IndexAt(contentOffset, cross), first, first + count - 1);
            float start = measured.OffsetOf(index, cross);
            var band = Layout!.ItemRect(index, cross);
            return SortableMath.SlotFromBand(contentOffset, index - first, start,
                Horizontal ? band.W : band.H, count);
        }
        return SortableMath.SlotFromOffset(contentOffset, leading, extent, count);
    }

    private (float dx, float dy) DisplacementForItem(int item)
    {
        float d = _plan.DisplacementFor(item, Sources);
        return Horizontal ? (d, 0f) : (0f, d);
    }

    /// <summary>Is <paramref name="node"/> inside this view's viewport? (Bounded parent walk — the drag source of a
    /// FOREIGN session, or the scene root of an external drag, is not ours to dim.)</summary>
    private static bool Contains(SceneStore scene, NodeHandle viewport, NodeHandle node)
    {
        if (node.IsNull || viewport.IsNull || node == viewport || !scene.IsLive(node)) return false;
        var n = node;
        for (int depth = 0; depth < 64 && !n.IsNull; depth++)
        {
            n = scene.Parent(n);
            if (n == viewport) return true;
        }
        return false;
    }

    private void HideDragSource(SceneStore scene, bool hide, bool releaseOverride = true)
    {
        // The press-source row has TWO owners while a same-list insertion is open: this virtual removal (0 — the row is
        // "in the chip") and DragController's Stationary re-assert, which re-writes the source's authored dim on every
        // mid-drag reconcile, AFTER the frame's animation compose. Publish the hidden value as the drag's source-opacity
        // override so the re-assert agrees instead of strobing this one row back to 0.4 while its siblings stay hidden.
        // Written before the node guard: the teardown must release the override even when the row itself is gone.
        if (hide || releaseOverride) scene.DragSourceOpacityOverride = hide ? 0f : null;
        var node = _dragSource;
        if (node.IsNull || !scene.IsLive(node)) return;
        ref NodePaint p = ref scene.Paint(node);
        float target = hide ? 0f : 1f;
        if (!hide && p.Opacity >= 1f) return;
        if (hide && p.Opacity <= 0f) return;
        p.Opacity = target;
        scene.Mark(node, NodeFlags.PaintDirty);
    }

    // ── elements (the framework's own cue; the app supplies only the in-gap CONTENT) ─────────────────

    /// <summary>The 2px accent insertion line with its 8px terminal dot, positioned by a bound transform (no
    /// re-render per move) and faded out while no gap is open.</summary>
    internal Element Line()
    {
        var dot = new BoxEl
        {
            Key = "dot", Width = TerminalDot, Height = TerminalDot, Shrink = 0f,
            Corners = Radii.PillAll, Fill = Tok.AccentDefault,
        };
        var bar = new BoxEl
        {
            Key = "bar", Grow = 1f, Shrink = 1f,
            Width = Horizontal ? LineThickness : float.NaN,
            Height = Horizontal ? float.NaN : LineThickness,
            Fill = Tok.AccentDefault,
        };
        return new BoxEl
        {
            Key = "fg-insertion-line",
            Direction = Horizontal ? (byte)1 : (byte)0,
            AlignItems = FlexAlign.Center,
            Width = Horizontal ? TerminalDot : float.NaN,
            Height = Horizontal ? float.NaN : TerminalDot,
            Shrink = 0f, HitTestVisible = false,
            Opacity = Prop.Of(() => SlotSignal.Value >= 0 ? 1f : 0f),
            Transform = Prop.Of(LineTransform),
            Transition = MotionTok.ControlFaster,
            Children = [dot, bar],
        };
    }

    private Affine2D LineTransform()
    {
        float at = Offset.Value - TerminalDot * 0.5f;
        return Horizontal ? Affine2D.Translation(at, 0f) : Affine2D.Translation(0f, at);
    }

    internal InsertionPlan PlanPeek => _plan;
    internal object? PayloadPeek => _payload;
}

/// <summary>The in-gap preview host: the framework owns the gap's SIZE and POSITION; the app's
/// <see cref="InsertionOptions.GapPreview"/> owns the cards drawn in it. The idle branch keeps the SAME key as the
/// active one — an unkeyed idle box against a keyed active one remounts the subtree on every open and close (A13).
/// <para>BOTH branches declare the SAME BOUND <c>Transform</c>, and that is load-bearing, not tidiness: bind wiring is
/// MOUNT-ONLY (<c>Reconciler.BindNode</c>), and the shared key makes this node's reuse permanent — so an idle branch
/// that omitted the binding left it never wired, <c>LocalTransform</c> stuck at identity, and the gap-sized card
/// arranged at the ZStack's top-left (viewport y = 0) while the line and the gap sat at the real slot (B1; the
/// engine's own DEBUG <c>[bindcontract]</c> tripwire names this defect class). For the same reason the thunk is a
/// MOUNT-SURVIVING method group that reads <c>Horizontal</c> and the slot LIVE: a render-time local captured in a
/// lambda would freeze at first mount once the binding actually persists.</para></summary>
internal sealed class ItemsViewInsertionPreview : Component
{
    private readonly ItemsViewInsertion _owner;
    public ItemsViewInsertionPreview(ItemsViewInsertion owner) => _owner = owner;

    public override Element Render()
    {
        _ = _owner.Version.Value;                          // the ONE re-render edge (same edge the gap displacement uses)
        var plan = _owner.PlanPeek;
        var payload = _owner.PayloadPeek;
        var template = _owner.Options.GapPreview;
        if (!plan.IsActive || payload is null || template is null)
            return new BoxEl
            {
                Key = "fg-insertion-preview", Height = 0f, Shrink = 0f, HitTestVisible = false,
                Transform = Prop.Of(PreviewTransform),
            };

        bool horizontal = _owner.Horizontal;
        float gap = plan.GapExtent;
        return new BoxEl
        {
            Key = "fg-insertion-preview",
            Direction = horizontal ? (byte)0 : (byte)1,
            Width = horizontal ? gap : float.NaN,
            Height = horizontal ? float.NaN : gap,
            Shrink = 0f, HitTestVisible = false, ClipToBounds = true,
            Transform = Prop.Of(PreviewTransform),
            Children = [template(payload, plan.Slot)],
        };
    }

    /// <summary>The gap's leading edge in VIEWPORT space — the same <c>Offset</c> the accent line rides, so the card
    /// and the line can never disagree. Identity while no slot is open: a gesture torn down mid-flight must not leave
    /// the (zero-extent) idle box parked at a stale translation.</summary>
    private Affine2D PreviewTransform()
    {
        if (_owner.SlotSignal.Value < 0) return Affine2D.Identity;
        float at = _owner.Offset.Value;
        return _owner.Horizontal ? Affine2D.Translation(at, 0f) : Affine2D.Translation(0f, at);
    }
}

/// <summary>Frame-clock settle watcher mounted only while one disclosure track is active.</summary>
internal sealed class ItemsViewDisclosureWatcher : Component
{
    private readonly ItemsViewController _controller;
    public ItemsViewDisclosureWatcher(ItemsViewController controller) => _controller = controller;

    public override Element Render()
    {
        long tick = UseContext(FrameClock.Tick);
        UseEffect(() =>
        {
            if (!_controller.DisclosureStarted || _controller.ActiveDisclosure is null
                || !_controller.DisclosurePresentationArmed) return;
            var viewport = _controller.Viewport;
            var anim = Context.Anim;
            var scene = Context.Scene;
            if (anim is null || scene is null || viewport.IsNull || !scene.IsLive(viewport))
            {
                _controller.TraceFailure();
                _controller.SettleDisclosure();
                return;
            }

            bool presented = scene.TryGetScroll(viewport, out var scroll) && float.IsFinite(scroll.DisclosureT);
            bool tracked = anim.TryGetTrackValue(viewport, AnimChannel.DisclosureProgress, out float progress);
            if (tracked) _controller.ObserveDisclosure(progress);
            else if (presented) _controller.ObserveDisclosure(scroll.DisclosureT);
            if (!tracked && (presented || _controller.DisclosureTrackObserved))
                _controller.SettleDisclosure();
        }, tick);
        return new BoxEl { Height = 0f, Shrink = 0f, HitTestVisible = false };
    }
}
