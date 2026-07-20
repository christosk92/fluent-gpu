using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Animation;
using FluentGpu.Signals;
using FluentGpu.Scene;

namespace FluentGpu.Controls;

/// <summary>
/// The List preset backing <see cref="ItemsView.List(System.Collections.Generic.IReadOnlyList{string}, Signal{int}, System.Action{int})"/>
/// — the WinUI <c>ListView</c> layout (<see cref="RepeatLayout.Stack"/>, 44dip stride) + the
/// <see cref="SelectorVisual.AccentPill"/> selector (the WinUI accent bar) + Single-selection + the 200ms live-reorder
/// dwell + the WinUI ListView defaults (CanReorderItems FALSE), hosted as an internal hook-bearing preset (the substrate
/// needs hooks; <c>ItemsView.List</c> is its public surface). ItemsView supplies the ONE selection model
/// (<see cref="SelectionModel"/> — None/Single/Multiple/Extended), keyboard navigation
/// (arrows/Home/End/PageUp/Down/Space/Enter/Ctrl+A/typeahead), focus rings and StartBringItemIntoView; the row chrome
/// lives in <see cref="SelectorVisuals.AccentPill"/> (the pixel-exact <c>ListViewItemPresenter</c> plate + selection
/// indicator + multi-select checkbox, lifted out so any selector × any layout can wear it). This preset adds only the
/// drag wiring + the <c>ItemClick</c> passthrough around that shared chrome.
///
/// Selector chrome (AccentPill) verified against microsoft-ui-xaml — see <see cref="SelectorVisuals"/> for the
/// per-value cites (ListViewItem_themeresources.xaml backplate ramp / 3×16 r1.5 indicator / 20×20 inline checkbox /
/// content padding 16,0,12,0 / MinHeight 40 / disabled 0.3). DEFERRED parity polish (low, non-reorder-blocking):
/// the 7px content prefix, the exact checkbox slide-timing token, Header/Footer, pill-height adapt to row height,
/// disabled+selected chrome nuances, check stroke ramps, and explicit min sizes (parity:421-429).
///
/// • Reorder — composes the E5 drag engine (BoxEl.CanDrag) + <see cref="ReorderList"/>: 4px drag box promotion,
///   0.80 drag opacity (DragController), the 200ms LISTVIEW_LIVEREORDER_TIMER dwell (ReorderList.ListDwellMs,
///   ListViewBase_Partial_Reorder.cpp:50). Rows render in RESTING order (the VirtualListEl host recycles positionally
///   and ignores KeyOf, so a projected order moves no node). DISPLACEMENT now flows through
///   <see cref="ItemsView.ItemDisplacement"/> (resting index → vertical offset from <see cref="ReorderList.OffsetFor"/>)
///   + <see cref="ItemsView.DisplacementVersion"/> — ItemsView host-seeds an ANIMATED translate FLIP on each realized
///   row (the WinUI "part to make room" glide), replacing the old STATIC <c>OffsetY</c> hint (the dragged slot gets 0
///   so it stays put — WinUI source targetIndex=-1, never repositioned, ListViewBase_Partial_Reorder.cpp:2194-2228).
///   Edge auto-scroll through <see cref="ItemsViewController.ScrollBy"/> (a hand-rolled ±8dip-within-24dip nudge; the
///   WinUI 100px/150-1500px/s edge gradient lives in DragDropContext, which this path doesn't route through), and the
///   WinUI RemoveAt+Insert commit (ReorderList.Move).
/// </summary>
internal sealed class ItemsViewListPreset : Component
{
    private static readonly ItemCollectionTransition ReorderTransition =
        new(AnimateAdds: false, AnimateRemoves: false, AnimateMoves: true, DurationMs: Motion.ControlNormal);

    // ── legacy simple surface (source-compatible: ItemsView.List(items, selected)) ──
    public IReadOnlyList<string> Items = [];
    /// <summary>Controlled single-selection index (kept two-way-synced with the <see cref="SelectionModel"/>).</summary>
    public Signal<int> SelectedIndex = new(-1);
    public Action<int>? OnSelectionChanged;

    // ── full surface ──
    public int ItemCount = -1;                         // −1 ⇒ Items.Count
    public Func<int, Element>? ItemTemplate;
    public Func<int, string>? ItemText;                // typeahead text
    public Func<int, bool>? IsItemEnabled;
    public ItemsSelectionMode SelectionMode = ItemsSelectionMode.Single;
    public SelectionModel? Selection;
    /// <summary>WinUI <c>IsItemClickEnabled</c>+<c>ItemClick</c>: fires on every pointer tap regardless of the
    /// invoke matrix (ListViewBase raises ItemClick on the click edge before selection).</summary>
    public Action<int>? OnItemClick;
    /// <summary>WinUI <c>ItemsView.ItemInvoked</c> gating (Enter/DoubleTap with a selection mode; see ItemsView).</summary>
    public Action<int>? OnItemInvoked;
    /// <summary>WinUI <c>CanReorderItems</c>: rows become drag sources; reorder commits via <see cref="OnReorder"/>.</summary>
    public bool CanReorderItems;
    /// <summary>Reorder commit (fromIndex, toIndex) in the pre-drop order — apply <c>ReorderList.Move</c> to your list.</summary>
    public Action<int, int>? OnReorder;
    /// <summary>Main-axis slot extent per row (uniform virtualization stride).</summary>
    public float ItemExtent = ItemsView.ListItemExtent;
    public ItemsViewController? Controller;
    public Func<int, string>? KeyOf;
    public float Width = float.NaN;
    public float Height = float.NaN;
    public float Grow;

    public override Element Render()
    {
        int count = ItemCount >= 0 ? ItemCount : Items.Count;
        var controller = UseMemo(() => Controller ?? new ItemsViewController(), DepKey.Empty);
        var model = UseMemo<SelectionModel>(() => Selection ?? new SelectionModel(), DepKey.Empty);
        var reorder = UseMemo(static () => new ReorderList { DwellMs = ReorderList.ListDwellMs }, DepKey.Empty);
        var orderVersion = UseSignal(0);
        var order = UseRef<int[]>([]);
        var lastDwellTick = UseRef(0L);
        var syncedIndex = UseRef(-2);

        // Two-way SelectedIndex ⇄ SelectionModel sync (the legacy controlled-signal surface).
        int wantIndex = SelectedIndex.Value;
        if (wantIndex != syncedIndex.Value)
        {
            syncedIndex.Value = wantIndex;
            if (wantIndex >= 0) model.Select(wantIndex);
            else if (model.SelectedCount > 0 && wantIndex == -1) model.DeselectAll();
        }

        void SelectionToSignal()
        {
            int first = model.FirstSelectedIndex;
            syncedIndex.Value = first;                       // suppress the echo re-sync
            if (SelectedIndex.Peek() != first) SelectedIndex.Value = first;
            OnSelectionChanged?.Invoke(first);
        }

        // ── live-reorder: RESTING order; displacement flows through ItemsView.ItemDisplacement (host-seeded FLIP) ──
        _ = orderVersion.Value;   // subscribe locally — keeps THIS preset's render in step with the dwell ticker
        bool reordering = reorder.IsActive;
        // Same-list reorder renders the RESTING order (slot s shows item s). The VirtualListEl host recycles
        // positionally and ignores KeyOf, so a projected order can't move nodes; displaced siblings now part via an
        // ANIMATED translate FLIP seeded BY ItemsView from the ItemDisplacement channel (below) — the WinUI "source
        // stays put at 0.80 opacity, the block between source and drag-over slot parts to make room" glide
        // (ListViewBase_Partial_Reorder.cpp:2194-2228). orderVersion is also passed to ItemsView as displacementVersion
        // so each drag-delta/dwell-commit re-seeds the channel across the autonomous-component boundary.
        if (order.Value.Length < count) order.Value = new int[count];
        var slots = order.Value;
        for (int i = 0; i < count; i++) slots[i] = i;

        // The 200ms live-reorder dwell ticks on the frame clock ONLY while a drag is active (the conditional
        // FrameClock read is safe — UseContext keeps no hook cell). The tick subscription drives the per-frame
        // re-render; the dwell ADVANCE + orderVersion bump run in the post-render effect below, NOT here — writing
        // orderVersion in the render body would be a backwards-write (the render subscribes it above to bootstrap
        // the reorder loop on drag-start, so a same-run write re-marks this render stale = a convergence risk).
        long tick = reordering ? UseContext(FrameClock.Tick) : 0L;

        // Advance the dwell OUTSIDE the tracked render scope: this effect (a SEPARATE computation) bumps orderVersion,
        // which re-renders this preset (the subscribe above) AND re-seeds ItemsView's displacement channel — so the
        // read and the write never share one computation. Keyed on the frame tick so it advances once per frame while
        // a drag is live; the transition to idle runs it once more to reset the dwell clock.
        UseEffect(() =>
        {
            if (!reorder.IsActive) { lastDwellTick.Value = 0; return; }
            long now = Environment.TickCount64;
            float dt = lastDwellTick.Value == 0 ? 0f : Math.Clamp(now - lastDwellTick.Value, 0, 100);
            lastDwellTick.Value = now;
            if (reorder.Advance(dt)) orderVersion.Value = orderVersion.Peek() + 1;
        }, DepKey.From(reordering ? tick + 1 : 0L));

        var ctx = Context;   // edge auto-scroll host-node rect + dwell re-render context

        void OnDragStarted(int slot, DragEventArgs e)
        {
            reorder.Begin(slot, count, ItemExtent, spacing: 0f);
            reorder.OnCommit = (from, to) =>
            {
                if (Items is IList<string> mutable && OnReorder is null) ReorderList.Move(mutable, from, to);
                OnReorder?.Invoke(from, to);
                model.RemapMove(from, to);
            };
            orderVersion.Value = orderVersion.Peek() + 1;    // re-render → arms the dwell frame ticks
        }

        void OnDragDelta(DragEventArgs e)
        {
            if (reorder.Update(e.TotalDy))
                orderVersion.Value = orderVersion.Peek() + 1;
            // Edge auto-scroll (E5-L3): pointer within 24dip of the viewport edge nudges the scroll.
            var scene = ctx.Scene;
            if (scene is not null && !ctx.HostNode.IsNull && scene.IsLive(ctx.HostNode))
            {
                var r = scene.AbsoluteRect(ctx.HostNode);
                const float edge = 24f, speed = 8f;
                if (e.Absolute.Y < r.Y + edge) controller.ScrollBy(-speed);
                else if (e.Absolute.Y > r.Y + r.H - edge) controller.ScrollBy(speed);
            }
        }

        void OnDragCompleted(DragEventArgs e)
        {
            reorder.Complete();                              // fires OnCommit when the slot changed
            orderVersion.Value = orderVersion.Peek() + 1;
        }

        void OnDragCanceled()
        {
            reorder.Cancel();
            orderVersion.Value = orderVersion.Peek() + 1;
        }

        Func<int, Element> contentOf = ItemTemplate ?? DefaultRowContent;
        Func<int, string>? keyFn = KeyOf;
        Func<int, string>? textFn = ItemText;
        if (ItemTemplate is null && Items.Count == count)
        {
            var items = Items;
            keyFn ??= i => items[i];
            textFn ??= i => items[i];
        }
        var slotsLocal = slots;
        var onItemClick = OnItemClick;
        bool canReorder = CanReorderItems;

        // The List preset wears the AccentPill selector (the shared ListViewItemPresenter chrome, now in
        // SelectorVisuals) and adds ONLY the drag wiring + the ItemClick passthrough. Rows render in RESTING order
        // (slot == item); displaced siblings part via an ANIMATED translate FLIP host-seeded by ItemsView from the
        // ItemDisplacement channel (passed below) — NOT a static OffsetY hint and NOT a projected node move (the
        // VirtualListEl host recycles positionally and ignores KeyOf). DragController re-anchors the pointer-held visual
        // from rest (the dragged slot is fed (0,0), so it never moves).
        // Per-item chrome SKIN goes through the ContainerFactory/SelectorVisual seam; per-item VARIATION goes through the
        // PartDelta value seam (fill/fg/opacity/corner/padding/glyph as values, applied during construction — shape-stable,
        // 0-alloc, CI-enforced; docs/guide/control-fidelity.md §6).
        BoxEl Chrome(int slot, Element content, ItemChromeState state,
                     Action<ItemContainerTrigger, KeyModifiers> onInteraction, Action<bool> onFocusChanged)
        {
            int item = slot < count ? slotsLocal[slot] : slot;
            Action<ItemContainerTrigger, KeyModifiers> interact = onItemClick is null
                ? onInteraction
                : (t, m) => { if (t == ItemContainerTrigger.Tap) onItemClick(item); onInteraction(t, m); };
            var baseChrome = SelectorVisuals.AccentPill(slot, content, in state, interact, onFocusChanged);
            return canReorder && state.IsEnabled
                ? baseChrome with
                {
                    CanDrag = true,
                    OnDragStarted = e => OnDragStarted(slot, e),
                    OnDragDelta = OnDragDelta,
                    OnDragCompleted = OnDragCompleted,
                    OnDragCanceled = OnDragCanceled,
                }
                : baseChrome;
        }

        float height = Height;
        // Legacy content-sizing: an unconstrained list realizes all rows at its natural height (the WinUI gallery
        // card shape); apps with big data set Height/Grow and get true windowed virtualization.
        bool natural = float.IsNaN(height) && Grow == 0f;
        if (natural) height = count * ItemExtent;

        var list = ItemsView.Create(count,
            itemTemplate: i => contentOf(slotsLocal[i]),
            layout: RepeatLayout.Stack(ItemExtent),
            selectionMode: SelectionMode,
            selection: model,
            isItemInvokedEnabled: OnItemInvoked is not null,
            itemInvoked: OnItemInvoked is null ? null : i => OnItemInvoked(slotsLocal[i]),
            selectionChanged: SelectionToSignal,
            itemText: textFn is null ? null : i => textFn(slotsLocal[i]),
            isItemEnabled: IsItemEnabled is null ? null : i => IsItemEnabled(slotsLocal[i]),
            controller: controller,
            // The List preset's selector: the WinUI accent bar (the row plate + 3×16 indicator + multi-select
            // checkbox now live in SelectorVisuals.AccentPill). Chrome adds only the drag wiring + ItemClick.
            selector: SelectorVisual.AccentPill,
            containerFactory: Chrome,
            // Displacement via the channel: resting index i → vertical offset (OffsetFor returns 0 for the dragged
            // item, and 0 for everything while idle — _dragged < 0). ItemsView skips the dragged ghost itself via its
            // DragGhost scene flag (NOT via OffsetFor==0, which would still animate the ghost's live translate back to
            // 0 and fight DragController). orderVersion drives the host-seeded translate FLIP re-seed. STABLE captures
            // only: this closure freezes at the inner
            // ItemsView's mount (autonomous-component boundary — constructor args freeze, engine Rule #2), so it must
            // read the memoized `reorder` live; a per-render local (`reordering`) freezes to its mount-time false and
            // kills the live displacement.
            itemDisplacement: i => (0f, reorder.OffsetFor(i)),
            displacementVersion: orderVersion,
            keyOf: keyFn is null ? null : i => keyFn(slotsLocal[i]),
            transition: ReorderTransition,
            // Natural (unconstrained) lists size the viewport to its content extent — the auto-WIDTH chain
            // also needs the non-flexing viewport's availW fallback (the 280-card gallery shape, D1).
            // Constrained lists (explicit Height or Grow) keep the hard-viewport fill path.
            grow: natural ? 0f : 1f);

        return new BoxEl
        {
            Width = Width,
            Height = height,
            Grow = Grow,
            Direction = 1,   // the list axis is vertical (D1 hygiene): the inner ItemsView grows the HEIGHT axis
            Children = [list],
        };
    }

    private Element DefaultRowContent(int i)
        => new TextEl(i < Items.Count ? Items[i] : string.Empty)
        {
            Size = 14f,
            Color = Tok.TextPrimary,        // ListViewItemForeground = TextFillColorPrimary, every state (:23-28)
            Grow = 1f,
        };
}

/// <summary>
/// The Grid preset backing <see cref="ItemsView.Grid(System.Collections.Generic.IReadOnlyList{string}, int, float)"/>
/// — the WinUI <c>GridView</c> layout (<see cref="RepeatLayout.Grid"/>) + the <see cref="SelectorVisual.Check"/> selector
/// (the GridView dual-border + top-right corner check) + Single-selection + the 300ms grid live-reorder dwell + the
/// WinUI GridView defaults (CanReorderItems FALSE), hosted as an internal hook-bearing preset (the substrate needs
/// hooks; <c>ItemsView.Grid</c> is its public surface). ItemsView + <see cref="SelectionModel"/> supply selection
/// (None/Single/Multiple/Extended), grid keyboard navigation (Left/Right ±1, Up/Down ±columns —
/// ItemsViewInteractions.cpp:1051-1067), Home/End/PageUp/Down, Ctrl+A, typeahead and focus rings; the tile chrome
/// lives in <see cref="SelectorVisuals.Check"/> (the pixel-exact <c>GridViewItem</c> plate + inner ring + corner
/// check, lifted out so any selector × any layout can wear it). This preset adds only the drag wiring +
/// the <c>ItemClick</c> passthrough around that shared chrome.
///
/// Selector chrome (Check) verified against microsoft-ui-xaml — see <see cref="SelectorVisuals"/> for the per-value
/// cites (GridViewItem_themeresources.xaml plate/border ramps / 2px accent border + 1px inner ControlSolid ring /
/// 20×20 top-right corner check @ margin 0,2,2,0 / item margin 0,0,4,4 / disabled 0.3). DEFERRED parity polish (low,
/// non-reorder-blocking) noted on the List preset applies here too.
///
/// • Reorder: 2-D live reorder over <see cref="ReorderList"/>'s 2-D mode (<see cref="ReorderList.Begin2D"/> /
///   <see cref="ReorderList.Update2D"/> / <see cref="ReorderList.OffsetFor2D"/>) — pending slot from the dragged tile's
///   accumulated (dx,dy) against the grid geometry (row from the vertical stride, column from the realized column
///   width), the 300ms GRIDVIEW_LIVEREORDER_TIMER dwell (<see cref="ReorderList.GridDwellMs"/>,
///   ListViewBase_Partial_Reorder.cpp:51). Tiles render in RESTING order (the VirtualListEl host recycles positionally
///   and ignores KeyOf, so a projected order moves no tile). DISPLACEMENT now flows through
///   <see cref="ItemsView.ItemDisplacement"/> (resting index → 2-D offset from <see cref="ReorderList.OffsetFor2D"/> —
///   a one-slot shift can wrap a row) + <see cref="ItemsView.DisplacementVersion"/> — ItemsView host-seeds an ANIMATED
///   translate FLIP on each realized tile (the WinUI "part to make room" glide), replacing the old STATIC OffsetX/Y
///   hint (the dragged tile gets (0,0) so it stays put — WinUI source targetIndex=-1). RemoveAt+Insert commit
///   (ReorderList.Move semantics).
/// </summary>
internal sealed class ItemsViewGridPreset : Component
{
    private const float ItemGap = 4f;            // GridViewItem Margin 0,0,4,4 (:144) — used for the layout stride + demo sizing
    private static readonly ItemCollectionTransition ReorderTransition =
        new(AnimateAdds: false, AnimateRemoves: false, AnimateMoves: true, DurationMs: Motion.ControlNormal);

    // ── legacy simple surface (ItemsView.Grid(Items, columns: 4)) ──
    public IReadOnlyList<string> Items = [];
    public int Columns = 4;
    public float TileSize = 96f;

    // ── full surface ──
    public int ItemCount = -1;
    public Func<int, Element>? ItemTemplate;
    public Func<int, string>? ItemText;
    public Func<int, bool>? IsItemEnabled;
    public ItemsSelectionMode SelectionMode = ItemsSelectionMode.Single;
    public SelectionModel? Selection;
    public Action<int>? OnItemClick;
    public Action<int>? OnItemInvoked;
    public Action? OnSelectionChanged;
    public bool CanReorderItems;
    public Action<int, int>? OnReorder;
    public ItemsViewController? Controller;
    public Func<int, string>? KeyOf;
    public float Width = float.NaN;
    public float Height = float.NaN;
    public float Grow;

    public override Element Render()
    {
        int count = ItemCount >= 0 ? ItemCount : Items.Count;
        int columns = Math.Max(1, Columns);
        var controller = UseMemo(() => Controller ?? new ItemsViewController(), DepKey.Empty);
        var model = UseMemo<SelectionModel>(() => Selection ?? new SelectionModel(), DepKey.Empty);
        var orderVersion = UseSignal(0);
        var order = UseRef<int[]>([]);
        var rl = UseMemo(static () => new ReorderList { DwellMs = ReorderList.GridDwellMs }, DepKey.Empty);   // 300ms grid dwell
        var lastDwellTick = UseRef(0L);

        // ── 2-D live-reorder: RESTING order; displacement flows through ItemsView.ItemDisplacement (host-seeded FLIP) ──
        _ = orderVersion.Value;   // subscribe locally — keeps THIS preset's render in step with the dwell ticker
        bool reordering = rl.IsActive;
        // Resting order (slot s shows item s) — same rationale as the List preset: the VirtualListEl host recycles
        // positionally and ignores KeyOf, so a projection moves no tile. Displaced tiles now part via an ANIMATED
        // translate FLIP seeded BY ItemsView from the ItemDisplacement channel (OffsetFor2D over resting indices, below);
        // orderVersion is passed as displacementVersion so each drag-delta/dwell-commit re-seeds across the autonomous boundary.
        if (order.Value.Length < count) order.Value = new int[count];
        var slots = order.Value;
        for (int i = 0; i < count; i++) slots[i] = i;

        // The 300ms grid dwell subscribes the frame tick to re-render each frame; the dwell ADVANCE + orderVersion bump
        // run in the post-render effect below, NOT the render body — writing orderVersion here would be a backwards-write
        // (the render subscribes it above to bootstrap the reorder loop, so a same-run write re-marks this render stale).
        long tick = reordering ? UseContext(FrameClock.Tick) : 0L;
        UseEffect(() =>
        {
            if (!rl.IsActive) { lastDwellTick.Value = 0; return; }
            long now = Environment.TickCount64;
            float dt = lastDwellTick.Value == 0 ? 0f : Math.Clamp(now - lastDwellTick.Value, 0, 100);
            lastDwellTick.Value = now;
            if (rl.Advance(dt)) orderVersion.Value = orderVersion.Peek() + 1;
        }, DepKey.From(reordering ? tick + 1 : 0L));

        float stride = TileSize + ItemGap;
        void Bump() => orderVersion.Value = orderVersion.Peek() + 1;

        void OnDragStarted(int slot)
        {
            rl.Begin2D(slot, count, columns);
            Bump();
        }
        void OnDragDelta(DragEventArgs e)
        {
            // Pending slot from the dragged tile's centre: row from the vertical stride, column from the realized
            // column width (cross/columns — GridVirtualLayout geometry).
            var scene = Context.Scene;
            float colW = stride;
            if (scene is not null && !Context.HostNode.IsNull && scene.IsLive(Context.HostNode))
                colW = MathF.Max(1f, scene.AbsoluteRect(Context.HostNode).W / columns);
            if (rl.Update2D(e.TotalDx, e.TotalDy, colW, stride)) Bump();
        }
        void OnDragCompleted()
        {
            int from = rl.DraggedIndex, to = rl.PendingIndex >= 0 ? rl.PendingIndex : rl.DraggedIndex;
            rl.Cancel();
            if (from >= 0 && to != from)
            {
                if (Items is IList<string> mutable && OnReorder is null) ReorderList.Move(mutable, from, to);
                OnReorder?.Invoke(from, to);
                model.RemapMove(from, to);
            }
            Bump();
        }
        void OnDragCanceled() { rl.Cancel(); Bump(); }

        Func<int, Element> contentOf = ItemTemplate ?? DefaultTile;
        Func<int, string>? keyFn = KeyOf;
        Func<int, string>? textFn = ItemText;
        if (ItemTemplate is null && Items.Count == count)
        {
            var items = Items;
            keyFn ??= i => items[i];
            textFn ??= i => items[i];
        }
        var slotsLocal = slots;
        var onItemClick = OnItemClick;
        bool canReorder = CanReorderItems;

        // The Grid preset wears the Check selector (the shared GridViewItem chrome, now in SelectorVisuals) and
        // adds ONLY the drag wiring + the ItemClick passthrough. Tiles render in RESTING order (slot == item);
        // displaced tiles part via an ANIMATED translate FLIP host-seeded by ItemsView from the ItemDisplacement
        // channel (OffsetFor2D, passed below) — NOT a static OffsetX/Y hint and NOT a projected node move (the
        // VirtualListEl host recycles positionally and ignores KeyOf). DragController re-anchors the pointer-held tile
        // from rest (the dragged slot is fed (0,0), so it never moves).
        // Per-item chrome SKIN goes through the ContainerFactory/SelectorVisual seam; per-item VARIATION goes through the
        // PartDelta value seam (fill/fg/opacity/corner/padding/glyph as values, applied during construction — shape-stable,
        // 0-alloc, CI-enforced; docs/guide/control-fidelity.md §6).
        BoxEl Chrome(int slot, Element content, ItemChromeState state,
                     Action<ItemContainerTrigger, KeyModifiers> onInteraction, Action<bool> onFocusChanged)
        {
            int item = slot < count ? slotsLocal[slot] : slot;
            Action<ItemContainerTrigger, KeyModifiers> interact = onItemClick is null
                ? onInteraction
                : (t, m) => { if (t == ItemContainerTrigger.Tap) onItemClick(item); onInteraction(t, m); };
            var baseChrome = SelectorVisuals.Check(slot, content, in state, interact, onFocusChanged);
            return canReorder && state.IsEnabled
                ? baseChrome with
                {
                    CanDrag = true,
                    OnDragStarted = _ => OnDragStarted(slot),
                    OnDragDelta = OnDragDelta,
                    OnDragCompleted = _ => OnDragCompleted(),
                    OnDragCanceled = OnDragCanceled,
                }
                : baseChrome;
        }

        float width = Width, height = Height;
        if (float.IsNaN(width) && Grow == 0f) width = columns * stride - ItemGap;   // legacy fixed-tile demo shape
        if (float.IsNaN(height) && Grow == 0f)
        {
            int rows = (count + columns - 1) / columns;
            height = rows <= 0 ? 0f : rows * TileSize + (rows - 1) * ItemGap;
        }

        return new BoxEl
        {
            Width = width,
            Height = height,
            Grow = Grow,
            Direction = 1,   // the grid scrolls vertically (D1 hygiene): the inner ItemsView grows the HEIGHT axis
            Children =
            [
                ItemsView.Create(count,
                    itemTemplate: i => contentOf(slotsLocal[i]),
                    // gap: 0 — SelectorVisuals.Check carries the WinUI GridViewItem Margin 0,0,4,4, so the LAYOUT must
                    // not also space by ItemGap (that double-counted to ~8px net; parity:424). The tile margin is the
                    // parity value; the layout pitch is TileSize + the tile's own margin.
                    layout: RepeatLayout.Grid(columns, TileSize, gap: 0f),
                    selectionMode: SelectionMode,
                    selection: model,
                    isItemInvokedEnabled: OnItemInvoked is not null,
                    // slot→item mapping (consistent with the List preset; slotsLocal[i]==i in resting order, so identical
                    // behavior today, but future-proof if the host ever projects).
                    itemInvoked: OnItemInvoked is null ? null : i => OnItemInvoked(slotsLocal[i]),
                    selectionChanged: OnSelectionChanged,
                    itemText: textFn is null ? null : i => textFn(slotsLocal[i]),
                    isItemEnabled: IsItemEnabled is null ? null : i => IsItemEnabled(slotsLocal[i]),
                    controller: controller,
                    // The Grid preset's selector: the WinUI corner check + dual border (now in SelectorVisuals.Check).
                    selector: SelectorVisual.Check,
                    containerFactory: Chrome,
                    // 2-D displacement via the channel: resting index i → (dx,dy) from OffsetFor2D (a one-slot shift can
                    // wrap a row; returns (0,0) while idle and for the dragged tile — but ItemsView skips the dragged
                    // ghost itself via its DragGhost scene flag, since (0,0) alone would still animate the tile's live
                    // translate back to 0 and fight DragController on both axes). colW is the realized column width
                    // (host rect / columns), same as the delta handler. STABLE captures only: this closure freezes at the
                    // inner ItemsView's mount (constructor args freeze, engine Rule #2) — `rl`/`Context` are stable
                    // memoized state; a per-render local (`reordering`) froze to false here and killed the displacement.
                    itemDisplacement: i =>
                    {
                        var sc = Context.Scene;
                        float colW = stride;
                        if (sc is not null && !Context.HostNode.IsNull && sc.IsLive(Context.HostNode))
                            colW = MathF.Max(1f, sc.AbsoluteRect(Context.HostNode).W / columns);
                        rl.OffsetFor2D(i, colW, stride, out float dx, out float dy);
                        return (dx, dy);
                    },
                    displacementVersion: orderVersion,
                    keyOf: keyFn is null ? null : i => keyFn(slotsLocal[i]),
                    transition: ReorderTransition),
            ],
        };
    }

    private Element DefaultTile(int i)
        => new BoxEl
        {
            Grow = 1f,
            AlignItems = FlexAlign.Center,
            Justify = FlexJustify.Center,
            Children =
            [
                new TextEl(i < Items.Count ? Items[i] : string.Empty)
                {
                    Size = 13f,
                    Color = Tok.TextPrimary,            // GridViewItemForeground (:11)
                    HoverColor = Tok.TextSecondary,     // GridViewItemForegroundPointerOver = TextFillColorSecondary (:12)
                },
            ],
        };
}
