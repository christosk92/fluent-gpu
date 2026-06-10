using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Scene;

namespace FluentGpu.Controls;

/// <summary>The ONE cross-list commit callback (plan E5-L3): the app removes the item from <paramref name="from"/>
/// at <paramref name="fromIndex"/> and inserts <paramref name="payload"/> into <paramref name="to"/> at
/// <paramref name="toIndex"/> — one atomic model mutation, both lists re-render from it (signals-first).
/// <paramref name="from"/> is null (and <paramref name="fromIndex"/> −1) when the drag came from a plain
/// <c>BoxEl.Draggable</c> of the shared kind rather than another <see cref="Reorderable"/> (a deposit-style insert).</summary>
public delegate void ReorderCrossCommit(object? payload, int fromIndex, Reorderable? from, Reorderable to, int toIndex);

/// <summary>The typed payload <see cref="Reorderable"/> items carry through the L2 <see cref="DragSession"/>:
/// the owning list, the item's ORIGINAL index there, and the app item (<see cref="Reorderable.ItemOf"/>). Any foreign
/// <c>DropTarget</c> accepting the kind reads <see cref="Item"/> (track row → sidebar playlist).</summary>
public sealed record ReorderPayload(Reorderable Owner, int Index, object? Item);

/// <summary>
/// E5-L3 — drag-reorder for ANY one-axis item list (incl. <c>Virtual.List</c>), composed from the engine layers
/// beneath it (plan Part B E5; user ruling: the Flutter Draggable/DragTarget + react-beautiful-dnd + SwiftUI model,
/// deliberately NOT WinUI's OLE loop): each item is an L2 <see cref="DragSource"/> of <see cref="Kind"/>, the list
/// body is ONE accepting <see cref="DropTargetSpec"/>, the slot math is <see cref="ReorderList"/> (WinUI's midpoint
/// rule + live-reorder dwell — ListViewBase_Partial_Reorder.cpp:984-1063, :50-51), sibling displacement is the
/// engine FLIP pipeline (each wrapped item carries a <c>LayoutTransition</c>; re-rendering the PROJECTED order moves
/// them through their springs), the ghost/lift visuals and the drop-glide are L1 <c>DragController</c>, and edge
/// auto-scroll is engine-level in <c>DragDropContext</c> (WinUI's 100px/150–1500px/s gradient) — this class adds none
/// of those mechanics, only the list semantics.
///
/// Usage (inside a component render):
/// <code>
///   var ro = UseMemo(() => new Reorderable("track") { OnReorder = (f, t) => Move(tracks, f, t) });
///   ro.Scene = Context.Scene; ro.RequestRender = Context.RequestRerender;
///   ro.ItemCount = tracks.Count; ro.ItemExtent = 48f;
///   return ro.List(Virtual.List(tracks.Count, 48f,
///       slot => ro.Item(ro.ItemAt(slot), RowFor(tracks[ro.ItemAt(slot)]), key: tracks[ro.ItemAt(slot)].Id)));
/// </code>
/// Render slots through <see cref="ItemAt"/> with STABLE per-item keys: mid-drag the projection moves the dragged
/// item to the dwell-committed slot, the keyed diff keeps node identity, and the FLIP pass animates the displaced
/// siblings (the part-to-make-room motion) while <c>DragController.Move</c> re-anchors the pointer-held visual.
///
/// CROSS-LIST: two Reorderables sharing a Kind. The target list accepts the foreign session, shows the insertion
/// line while hovered, and on drop routes the ONE <see cref="OnCrossCommit"/> (target's, else the source's) —
/// the source's gesture completion then cancels its local reorder instead of committing (no double mutation).
///
/// KEYBOARD lift mode (the react-beautiful-dnd a11y pattern): Space on a focused item lifts it (a pointer-free
/// reorder; the item dims to the L1 drag opacity with the lifted shadow), Up/Down (Left/Right when
/// <see cref="Horizontal"/>) move the insertion slot with NO dwell, Space drops (commits), Escape cancels, and
/// focus leaving the lifted item cancels. All slot moves re-render the projection → FLIP animates them.
/// </summary>
public sealed class Reorderable
{
    /// <summary>Keyboard-lift visual opacity — the SAME lift visual the pointer path gets from L1
    /// (WinUI <c>ListViewItemDragThemeOpacity</c> = 0.80 — microsoft-ui-xaml
    /// controls\dev\CommonStyles\ListViewItem_themeresources.xaml:7, identical in all ThemeDictionaries).</summary>
    public const float LiftOpacity = 0.80f;

    private readonly string[] _kinds;
    private readonly DropTargetSpec _dropSpec;

    private int[] _order = [];           // cached ProjectOrder (grow-only; identity when idle)
    private bool _orderValid;
    private float[] _extents = [];       // pooled variable-extent scratch (ExtentOf path; grow-only)
    private long _lastDeltaWallMs;       // wall-clock dwell advance between OnDragDelta events
    private bool _crossConsumed;         // this gesture's item was taken by ANOTHER list's drop → don't commit locally
    private int _kbLifted = -1;          // keyboard lift mode item (ORIGINAL index; −1 = none)
    private NodeHandle _listNode;        // List() wrapper node (cross-list insertion math + scroll offset)
    private bool _crossOver;             // a foreign session is hovering this list
    private int _crossInsert = -1;       // its current insertion slot (0..ItemCount)

    public Reorderable(string kind)
    {
        Kind = kind;
        _kinds = [kind];
        _listNode = NodeHandle.Null;
        Core.OnCommit = (from, to) => OnReorder?.Invoke(from, to);
        // SettleOnDrop: a same-list release keeps the L1 drop-glide — the commit's FLIP retarget turns it into the
        // glide-into-the-new-slot motion (foreign deposit targets default to snap-home instead).
        _dropSpec = new DropTargetSpec(_kinds, OnTargetEnter, OnTargetOver, OnTargetLeave, OnTargetDrop)
        {
            SettleOnDrop = true,
        };
    }

    /// <summary>The drag kind this list emits AND accepts (the cast-free L2 discriminator).</summary>
    public string Kind { get; }

    /// <summary>The slot-math core (midpoint rule + dwell + displacement hints + projection). Exposed for advanced
    /// composition; normal consumers only read the surface properties here.</summary>
    public ReorderList Core { get; } = new();

    // ── consumer-supplied state (set every render — plain fields, signals-friendly) ───────────────
    /// <summary>Item count this render (drives <see cref="ReorderList.Begin"/> at lift).</summary>
    public int ItemCount;
    /// <summary>Uniform resting main-axis extent per item (ignored when <see cref="ExtentOf"/> is set).</summary>
    public float ItemExtent = 48f;
    /// <summary>Variable extents: resting main-axis extent per ORIGINAL index (sampled once at lift into pooled
    /// storage). Cross-list insertion math still assumes the uniform <see cref="ItemExtent"/> pitch.</summary>
    public Func<int, float>? ExtentOf;
    /// <summary>The container's main-axis Gap between items.</summary>
    public float Spacing;
    /// <summary>Horizontal strip (TotalDx drives the slot math; Left/Right are the keyboard-lift arrows).</summary>
    public bool Horizontal;
    /// <summary>The scene (wire <c>Context.Scene</c>) — needed only for CROSS-LIST insertion math (pointer →
    /// slot) and the insertion-line offset; same-list reorder works without it.</summary>
    public SceneStore? Scene;
    /// <summary>Re-render request (wire <c>Context.RequestRerender</c> or a signal bump): fired whenever the
    /// projected order / lift / insertion-line state changed and the consumer must re-render the list.</summary>
    public Action? RequestRender;

    /// <summary>SAME-LIST commit: (fromIndex, toIndex) in the ORIGINAL order — remove at from, insert at to
    /// (<see cref="ReorderList.Move{T}"/> applies it to an <c>IList&lt;T&gt;</c>).</summary>
    public Action<int, int>? OnReorder;
    /// <summary>CROSS-LIST commit (see <see cref="ReorderCrossCommit"/>). The drop TARGET's delegate runs; when it
    /// has none, the SOURCE's is used — set the one shared callback on both lists.</summary>
    public ReorderCrossCommit? OnCrossCommit;
    /// <summary>The app item carried as <see cref="ReorderPayload.Item"/> (what a foreign target receives).</summary>
    public Func<int, object?>? ItemOf;

    /// <summary>Live-reorder dwell before displaced items shift (WinUI LISTVIEW_LIVEREORDER_TIMER 200ms /
    /// GRIDVIEW 300ms — ListViewBase_Partial_Reorder.cpp:50-51). 0 ⇒ displacement follows the pointer immediately.</summary>
    public float DwellMs { get => Core.DwellMs; set => Core.DwellMs = value; }
    /// <summary>Advance the dwell from the wall clock on every drag delta (default). Set false for deterministic
    /// tests and drive <see cref="Advance"/> yourself (or set <see cref="DwellMs"/> = 0).</summary>
    public bool AutoDwell = true;
    /// <summary>Show the built-in 2px accent insertion line while a FOREIGN session hovers this list, and for
    /// same-list feedback when <see cref="LiveProject"/> is off. Default true.</summary>
    public bool ShowInsertionLine = true;
    /// <summary>Live mid-drag projection (default): <see cref="ItemAt"/> returns the PROJECTED order while lifted, so
    /// displaced siblings FLIP to make room (the rbd motion). REQUIRES key-preserving children (plain keyed children /
    /// <c>ForEl</c> / <c>Repeater</c>): the keyed diff moves nodes with their items. Set FALSE over
    /// <c>Virtual.List</c>/<c>VirtualListEl</c> — its window diff recycles nodes POSITIONALLY (no keys), so a mid-drag
    /// projection would swap content under the lifted node; instead the resting order holds during the drag and the
    /// insertion LINE shows the pending slot (commit still lands exactly where shown).</summary>
    public bool LiveProject = true;

    // ── read surface (consumers render from these) ────────────────────────────────────────────────
    /// <summary>A lift is in flight on THIS list (pointer drag or keyboard lift).</summary>
    public bool IsLifted => Core.IsActive;
    /// <summary>The lifted item's ORIGINAL index (−1 when idle).</summary>
    public int LiftedIndex => Core.DraggedIndex;
    /// <summary>True while the lift is the pointer-free keyboard mode.</summary>
    public bool IsKeyboardLifted => _kbLifted >= 0;
    /// <summary>The dwell-committed slot the projection currently shows (−1 when idle).</summary>
    public int TargetIndex => Core.TargetIndex;
    /// <summary>The insertion line is showing: a foreign session hovers the list, OR a same-list lift with
    /// <see cref="LiveProject"/> off has a pending slot away from home (the virtual-list feedback mode).</summary>
    public bool InsertionVisible
        => (_crossOver && _crossInsert >= 0)
           || (!LiveProject && Core.IsActive && Core.PendingIndex != Core.DraggedIndex);

    /// <summary>The slot the line marks: the foreign session's insertion slot (0..<see cref="ItemCount"/>), else the
    /// same-list pending slot (−1 when nothing shows).</summary>
    public int InsertionIndex
        => _crossOver && _crossInsert >= 0 ? _crossInsert
            : !LiveProject && Core.IsActive ? Core.PendingIndex
            : -1;

    /// <summary>Main-axis offset (list-wrapper space, scroll-corrected) of the insertion boundary — where the
    /// built-in <see cref="InsertionLine"/> sits. Foreign sessions mark the slot boundary; a same-list
    /// (<see cref="LiveProject"/>-off) lift marks the resting start the dragged item will land at
    /// (<see cref="ReorderList.DraggedTargetStart"/>... computed at the PENDING slot for immediacy).</summary>
    public float InsertionLineOffset
    {
        get
        {
            float pitch = ItemExtent + Spacing;
            float pos;
            if (_crossOver && _crossInsert >= 0)
            {
                pos = _crossInsert * pitch - Spacing * 0.5f;
            }
            else
            {
                // Same-list pending slot p: the line sits at the boundary the item will insert at — above item p
                // when moving up, below item p when moving down (the midpoint-rule claim direction).
                int p = Core.PendingIndex;
                pos = p > Core.DraggedIndex ? (p + 1) * pitch - Spacing * 0.5f : p * pitch - Spacing * 0.5f;
            }
            if (pos < 0f) pos = 0f;
            return pos - ViewportScrollOffset();
        }
    }

    /// <summary>The ORIGINAL item index shown at <paramref name="slot"/> under the current projection (identity when
    /// idle): render slot s with the item at <c>ItemAt(s)</c> and the item's STABLE key — mid-drag the dragged item
    /// occupies the dwell-committed slot and the keyed diff + FLIP animate the displaced siblings. With
    /// <see cref="LiveProject"/> off this stays identity while lifted (virtual-list mode — the insertion line is the
    /// feedback; the commit reorders the model itself).</summary>
    public int ItemAt(int slot)
    {
        if (!Core.IsActive || !LiveProject) return slot;
        EnsureOrder();
        return (uint)slot < (uint)Core.Count ? _order[slot] : slot;
    }

    /// <summary>Displacement hint passthrough (<see cref="ReorderList.OffsetFor"/>) for offset-hint consumers that
    /// render the RESTING order with translation hints instead of the projected order. Do not combine an authored
    /// offset hint with a FLIP <c>Animate</c> on the same node (see the ReorderList class remarks).</summary>
    public float OffsetFor(int originalIndex) => Core.OffsetFor(originalIndex);

    /// <summary>Deterministic dwell driver (tests / a FrameClock subscriber when <see cref="AutoDwell"/> is off).
    /// Returns true when the shown target changed (a re-render was requested).</summary>
    public bool Advance(float dtMs)
    {
        if (!Core.Advance(dtMs)) return false;
        Changed();
        return true;
    }

    // ── element wrappers ──────────────────────────────────────────────────────────────────────────

    /// <summary>Wrap one item's content: an L2 <see cref="DragSource"/> of <see cref="Kind"/> (payload =
    /// <see cref="ReorderPayload"/>, resolved once at promotion), the L1 lifecycle wired into the slot math, a FLIP
    /// <c>LayoutTransition</c> (default <see cref="LayoutTransition.Slide"/>) for the displacement motion, focusable
    /// with the keyboard lift-mode keys, and the keyboard-lift visuals when lifted that way (the pointer path gets
    /// identical visuals from L1). <paramref name="index"/> is the ORIGINAL item index (pass <c>ItemAt(slot)</c>).</summary>
    public Element Item(int index, Element content, string? key = null, LayoutTransition? transition = null)
    {
        bool kbLifted = _kbLifted == index;
        return new BoxEl
        {
            Key = key ?? "reorder#" + index,
            Draggable = new DragSource(Kind, () => new ReorderPayload(this, index, ItemOf?.Invoke(index))),
            OnDragStarted = _ => BeginGesture(index),
            OnDragDelta = OnDelta,
            OnDragCompleted = _ => CompleteGesture(),
            OnDragCanceled = CancelGesture,
            OnKeyDown = e => OnItemKey(index, e),
            OnFocusChanged = got => { if (!got && _kbLifted == index) KbCancel(); },   // rbd: losing focus drops the lift
            Focusable = true,
            Animate = transition ?? LayoutTransition.Slide,
            // Keyboard-lift visuals (the pointer path gets these from DragController.Promote):
            Opacity = kbLifted ? LiftOpacity : 1f,                        // ListViewItemDragThemeOpacity 0.80
            Shadow = kbLifted ? Elevation.Flyout : (ShadowSpec?)null,     // the lifted depth class (matches DragShadow)
            Children = [content],
        };
    }

    /// <summary>Wrap the list body as the ONE accepting drop target of <see cref="Kind"/> (a ZStack so the
    /// insertion-line overlay can ride above the body): same-list releases commit through the gesture completion,
    /// foreign sessions get the insertion line + <see cref="OnCrossCommit"/>. Captures the wrapper node for the
    /// pointer→slot math (set <see cref="Scene"/>).</summary>
    public Element List(Element body)
    {
        Element[] children = InsertionVisible && ShowInsertionLine
            ? new[] { body, InsertionLine() }
            : new[] { body };
        return new BoxEl
        {
            ZStack = true,
            Grow = 1f,
            DropTarget = _dropSpec,
            OnRealized = h => _listNode = h,
            Children = children,
        };
    }

    /// <summary>The built-in cross-list insertion indicator: a 2px accent bar at the insertion boundary, stretched
    /// across the list (rbd's insertion cue; WinUI's native cue is displacement, which same-list reorder gets via
    /// FLIP). Non-hit-testable. Consumers composing their own <see cref="List"/> wrapper can place it manually from
    /// <see cref="InsertionVisible"/>/<see cref="InsertionLineOffset"/>.</summary>
    public Element InsertionLine()
        => new BoxEl
        {
            Key = "reorder-insertion-line",
            Width = Horizontal ? 2f : float.NaN,
            Height = Horizontal ? float.NaN : 2f,
            Fill = Tok.AccentDefault,
            OffsetX = Horizontal ? InsertionLineOffset - 1f : 0f,
            OffsetY = Horizontal ? 0f : InsertionLineOffset - 1f,
            HitTestVisible = false,
        };

    // ── pointer gesture (L1 lifecycle → slot math) ───────────────────────────────────────────────

    private void BeginGesture(int index)
    {
        _kbLifted = -1;
        _crossConsumed = false;
        _lastDeltaWallMs = Environment.TickCount64;
        BeginCore(index);
        InvalidateOrder();   // projection starts as identity (target == dragged) — no render needed yet
    }

    private void OnDelta(DragEventArgs e)
    {
        bool pendingChanged = Core.Update(Horizontal ? e.TotalDx : e.TotalDy);
        if (pendingChanged && !LiveProject) RequestRender?.Invoke();   // the insertion line tracks the pending slot
        if (!AutoDwell) return;
        long now = Environment.TickCount64;
        float dt = _lastDeltaWallMs == 0 ? 0f : Math.Clamp(now - _lastDeltaWallMs, 0, 1000);
        _lastDeltaWallMs = now;
        Advance(dt);
    }

    private void CompleteGesture()
    {
        if (_crossConsumed)
        {
            // The item was deposited into ANOTHER Reorderable — its OnCrossCommit mutated both models; committing
            // the local move too would double-apply. Drop the hints only.
            _crossConsumed = false;
            Core.Cancel();
        }
        else
        {
            Core.Complete();   // fires OnReorder(from, to) iff the slot actually changed
        }
        Changed();
    }

    private void CancelGesture()
    {
        _crossConsumed = false;
        Core.Cancel();
        Changed();
    }

    private void BeginCore(int index)
    {
        int count = Math.Max(0, ItemCount);
        if (ExtentOf is { } extentOf && count > 0)
        {
            if (_extents.Length < count)
            {
                int cap = _extents.Length > 0 ? _extents.Length : 8;
                while (cap < count) cap *= 2;
                _extents = new float[cap];
            }
            for (int i = 0; i < count; i++) _extents[i] = extentOf(i);
            Core.Begin(index, _extents.AsSpan(0, count), Spacing);
        }
        else
        {
            Core.Begin(index, count, ItemExtent, Spacing);
        }
    }

    // ── keyboard lift mode (rbd a11y: Space lifts, arrows move, Space drops, Escape cancels) ──────

    private void OnItemKey(int index, KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Space && !e.IsRepeat)
        {
            if (_kbLifted < 0 && !Core.IsActive) KbLift(index);
            else if (_kbLifted == index) KbDrop();
            else return;   // Space on another item while one is lifted: ignore (focus change cancels anyway)
            e.Handled = true;
            return;
        }
        if (_kbLifted != index) return;
        if (e.KeyCode == Keys.Escape)
        {
            KbCancel();
            e.Handled = true;
            return;
        }
        int back = Horizontal ? Keys.Left : Keys.Up;
        int fwd = Horizontal ? Keys.Right : Keys.Down;
        if (e.KeyCode == back || e.KeyCode == fwd)
        {
            if (Core.MoveTarget(e.KeyCode == back ? -1 : 1)) Changed();
            e.Handled = true;   // a lifted item's arrows never fall through to list nav / scrolling
        }
    }

    private void KbLift(int index)
    {
        _kbLifted = index;
        _crossConsumed = false;
        BeginCore(index);
        Changed();   // re-render: the item takes the lift visuals
    }

    private void KbDrop()
    {
        _kbLifted = -1;
        Core.Complete();   // commits at the shown slot (pending == target in keyboard mode)
        Changed();
    }

    private void KbCancel()
    {
        _kbLifted = -1;
        Core.Cancel();
        Changed();
    }

    // ── drop-target handlers (the list body's DropTargetSpec) ─────────────────────────────────────

    private void OnTargetEnter(DragSession s) => OnTargetOver(s);

    private void OnTargetOver(DragSession s)
    {
        if (s.Payload is ReorderPayload rp && ReferenceEquals(rp.Owner, this)) return;   // same-list: displacement, no line
        int slot = SlotFromPosition(s.Position);
        if (!_crossOver || slot != _crossInsert)
        {
            _crossOver = true;
            _crossInsert = slot;
            RequestRender?.Invoke();
        }
    }

    private void OnTargetLeave(DragSession s)
    {
        if (!_crossOver) return;
        _crossOver = false;
        _crossInsert = -1;
        RequestRender?.Invoke();
    }

    private void OnTargetDrop(DragSession s)
    {
        int to = SlotFromPosition(s.Position);
        bool hadLine = _crossOver;
        _crossOver = false;
        _crossInsert = -1;

        if (s.Payload is ReorderPayload rp)
        {
            if (ReferenceEquals(rp.Owner, this)) return;   // same-list: the gesture completion commits (SettleOnDrop glide)
            rp.Owner._crossConsumed = true;                // the source's completion cancels its local reorder
            var commit = OnCrossCommit ?? rp.Owner.OnCrossCommit;
            commit?.Invoke(rp.Item, rp.Index, rp.Owner, this, to);
        }
        else
        {
            // A plain BoxEl.Draggable of the shared kind dropped here: deposit-style insert (no source list).
            OnCrossCommit?.Invoke(s.Payload, -1, null, this, to);
        }
        if (hadLine) RequestRender?.Invoke();
    }

    // ── geometry (cross-list pointer → slot; cold per-move path) ─────────────────────────────────

    /// <summary>Insertion slot (0..<see cref="ItemCount"/>) for a window-space position: midpoint rule over the
    /// uniform pitch in CONTENT space (wrapper origin + inner scroll offset). Without <see cref="Scene"/> the wrapper
    /// origin/scroll are unknown and 0 is assumed (lists at the window origin only) — wire Scene for cross-list.</summary>
    private int SlotFromPosition(Point2 abs)
    {
        int count = Math.Max(0, ItemCount);
        if (count == 0) return 0;
        float main = Horizontal ? abs.X : abs.Y;
        if (Scene is { } scene && !_listNode.IsNull && scene.IsLive(_listNode))
        {
            var r = scene.AbsoluteRect(_listNode);
            main -= Horizontal ? r.X : r.Y;
            main += ViewportScrollOffset();
        }
        float pitch = ItemExtent + Spacing;
        if (pitch <= 0f) return 0;
        int item = (int)MathF.Floor(main / pitch);
        float within = main - item * pitch;
        int slot = within > ItemExtent * 0.5f ? item + 1 : item;   // past the midpoint ⇒ insert AFTER the item
        return Math.Clamp(slot, 0, count);
    }

    private float ViewportScrollOffset()
    {
        if (Scene is not { } scene || _listNode.IsNull || !scene.IsLive(_listNode)) return 0f;
        var vp = FindScrollableWithin(scene, _listNode);
        if (vp.IsNull || !scene.TryGetScroll(vp, out var sc)) return 0f;
        return Horizontal ? sc.OffsetX : sc.OffsetY;
    }

    /// <summary>The scroll viewport carrying the list rows: the wrapper itself or a descendant within two levels
    /// (the wrapped body is typically the <c>Virtual.List</c>/<c>ScrollEl</c> viewport directly). Cold path.</summary>
    private static NodeHandle FindScrollableWithin(SceneStore scene, NodeHandle root)
    {
        if ((scene.Flags(root) & NodeFlags.Scrollable) != 0) return root;
        for (var c = scene.FirstChild(root); !c.IsNull; c = scene.NextSibling(c))
        {
            if ((scene.Flags(c) & NodeFlags.Scrollable) != 0) return c;
            for (var g = scene.FirstChild(c); !g.IsNull; g = scene.NextSibling(g))
                if ((scene.Flags(g) & NodeFlags.Scrollable) != 0) return g;
        }
        return NodeHandle.Null;
    }

    // ── projection cache ──────────────────────────────────────────────────────────────────────────

    private void Changed()
    {
        InvalidateOrder();
        RequestRender?.Invoke();
    }

    private void InvalidateOrder() => _orderValid = false;

    private void EnsureOrder()
    {
        if (_orderValid) return;
        int n = Core.Count;
        if (n > 0)
        {
            if (_order.Length < n)
            {
                int cap = _order.Length > 0 ? _order.Length : 8;
                while (cap < n) cap *= 2;
                _order = new int[cap];
            }
            Core.ProjectOrder(_order.AsSpan(0, n));
        }
        _orderValid = true;
    }
}
