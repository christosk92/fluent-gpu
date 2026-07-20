using System.Collections.Immutable;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Scene;

namespace FluentGpu.Controls;

/// <summary>One node in a <see cref="TreeView"/>: a label plus zero or more child nodes. <see cref="IsEnabled"/>
/// gates interaction (disabled rows dim to 0.3); custom row content comes from the tree's ItemTemplate.</summary>
public sealed record TreeNode(string Label, params TreeNode[] Children)
{
    public bool IsEnabled { get; init; } = true;
}

/// <summary>
/// WinUI <c>TreeView</c> — rebuilt as a FLATTENED VISIBLE-NODE PROJECTION (how WinUI actually works: TreeViewList is a
/// ListView over the flattened ViewModel of visible nodes) with the WinUI TreeViewItem chrome and interaction model.
///
/// Behavior verified against microsoft-ui-xaml controls\dev\TreeView:
/// • Chevron-only expand vs row select split — the chevron is its own hit target whose press toggles IsExpanded and
///   nothing else (TreeViewItem.cpp:698-707 OnExpandCollapseChevronPointerPressed, args.Handled = true); a row click
///   selects and raises ItemInvoked (TreeView.cpp:159-165 OnItemClick). There is NO double-tap expand handler
///   anywhere in the WinUI TreeView sources (audited expectation corrected against the cpp).
/// • Arrow keys (TreeViewItem.cpp:638-696 HandleExpandCollapse): Left collapses an expanded node, else focuses the
///   parent; Right expands a collapsed parent, else focuses the first child. Up/Down move through the visible flat
///   order. Shift+Alt+(Up/Left = backward, Down/Right = forward) with Ctrl up reorders the focused node ±1 among its
///   SIBLINGS, collapsing it first (TreeViewItem.cpp:396 IsReorder + cpp:600-636 HandleReorder; the position guard is
///   applied to BOTH directions, fixing WinUI's Up||Left&&pos!=0 precedence bug — sweep:484). Plain (unmodified)
///   direction keys are navigation only (IsExpandCollapse, cpp:527-541).
/// • Multi-select (TreeViewItem.xaml:101-118 TreeViewMultiSelectStates): a 32px-wide checkbox lane (CheckBox
///   Width=32 Margin=10,0,0,0 — xaml:138) slides the row content; Space / row press toggles; toggling a parent
///   cascades through its subtree and a partially-selected parent shows the indeterminate dash
///   (TreeViewNode UpdateSelection cascade + PartialSelectionState).
/// • Drag reorder — composes the E5 drag engine + <see cref="ReorderList"/> over the SIBLING blocks (a sibling's
///   extent = its visible subtree height, so whole subtrees part to make room); 200ms list dwell; commit =
///   RemoveAt+Insert among the siblings. The DISPLACEMENT (keyed rows FLIP-sliding to part) is the only drop cue —
///   WinUI draws NO insertion line for same-list reorder (none in TreeViewItem.xaml). While dragging, a collapsed
///   row with children that the pointer dwells over for 1s auto-expands (c_dragOverInterval=1000ms, reset-on-leave —
///   TreeViewItem.h:15 + cpp:204-224/242-245/348-362), and an edge-near pointer nudges a scrollable ancestor.
///   • Reorder default — <c>CanReorderItems</c> defaults to <c>true</c>, matching WinUI's default true (TreeView.idl:141
///   <c>[MUX_DEFAULT_VALUE("true")]</c> + TreeView.xaml:12 <c>Setter … Value="True"</c>): a bare tree is reorderable.
///   • DOCUMENTED GAP (WinUI superset deferral) — reorder here is SIBLING-ONLY (RemoveAt+Insert within one parent's
///   children, the <c>CommitSiblingMove</c> local). WinUI additionally supports drag-REPARENT: dropping ONTO a row
///   (vs between rows) appends the dragged node as that row's last child, resolved through a cross-parent insertion
///   model with an ancestor walk-up cycle guard (TreeViewList.cpp MoveNodeInto + TreeViewItem.cpp:68-126; the
///   you-can't-drop-a-node-into-its-own-descendant check). That cross-parent gesture layer (drop-onto-row + a
///   DropTargetSpec the current single-axis ReorderList-over-siblings path does not use) is intentionally NOT
///   implemented in this pass to protect the keyed-projection invariant + the E5 substrate pins; it is a known gap.
///
/// Style verified against controls\dev\TreeView\TreeView_themeresources.xaml + TreeViewItem.xaml:
/// row plate rest=SubtleFillColorTransparent (:5), hover=Secondary (:6), pressed=Tertiary (:7), selected=Secondary
/// (:9), selectedHover=Tertiary (:10), selectedPressed=Secondary (:11); presenter margin 4,2 (:38), padding 0,3,0,5
/// (:39), MinHeight 28 (:41), ControlCornerRadius 4; SelectionIndicator 3×16 r2, left-aligned, vertically centred,
/// opacity 0 unless selected (TreeViewItem.xaml:130 + states :27/:38/:49), fill AccentFillColorDefault (:33),
/// disabled TextFillColorDisabled (:36); chevron glyphs E76C collapsed / E70D expanded at FontSize 8 in a 12×12 block
/// (TreeView.idl:201-205 defaults; TreeViewItem.xaml:144-145), cell padding 14,0 (xaml:143), foreground TextPrimary
/// with Pressed → TextFillColorSecondary (xaml:33-36); indentation = depth × 16 (TreeViewItem.cpp:515-519).
/// </summary>
public sealed class TreeView : Component
{
    internal const float MinRowHeight = 28f;      // TreeViewItemMinHeight (:41)
    internal const float RowStrideFallback = 32f; // MinHeight 28 + the 2+2 presenter margins
    internal const float IndentStep = 16f;        // TreeViewItem.cpp:518 depth × 16
    internal const float ChevronPad = 14f;        // ExpandCollapseChevron Padding 14,0 (xaml:143)
    internal const float GlyphSize = 8f;          // GlyphSize default (TreeView.idl:204)
    internal const float DisabledOpacity = 0.3f;

    public IReadOnlyList<TreeNode> Roots = [];
    /// <summary>Custom row content (icon + label + metadata) — WinUI ContentTemplate (TreeViewItem.xaml:147).</summary>
    // Per-item chrome customization goes through this ItemTemplate seam (the ContainerFactory analogue), NOT
    // TemplateParts — per-item part modifiers in recycled scroll paths are an allocation/recycling hazard
    // (docs/guide/control-fidelity.md §6).
    public Func<TreeNode, Element>? ItemTemplate;
    /// <summary>WinUI <c>TreeViewSelectionMode.Multiple</c>: checkbox lane + cascading subtree selection.</summary>
    public bool IsMultiSelectEnabled;
    /// <summary>WinUI <c>TreeView.ItemInvoked</c> — raised on row click/Enter (TreeView.cpp:159-165).</summary>
    public Action<TreeNode>? ItemInvoked;
    /// <summary>Selection changed (node, isSelected). Multi mode reports each toggled node once per gesture root.</summary>
    public Action<TreeNode, bool>? SelectionChanged;
    public Action<TreeNode, bool>? Expanding;     // (node, isExpanded) — WinUI Expanding/Collapsed pair, folded
    /// <summary>WinUI <c>CanReorderItems</c>: rows drag-reorder among their siblings (kind scoped to this tree).
    /// Defaults to <c>true</c>, matching WinUI (TreeView.idl:141 <c>[MUX_DEFAULT_VALUE("true")]</c> + TreeView.xaml:12
    /// <c>&lt;Setter Property="CanReorderItems" Value="True"/&gt;</c>).</summary>
    public bool CanReorderItems = true;
    /// <summary>Reorder commit: (parent — null at root level, fromChildIndex, toChildIndex). When null and the
    /// sibling array is mutable, the tree moves the node in place (WinUI mutates its own node tree).</summary>
    public Action<TreeNode?, int, int>? OnReorder;

    public static Element Create(IReadOnlyList<TreeNode> roots)
        => Embed.Comp(() => new TreeView { Roots = roots });

    public static Element Create(IReadOnlyList<TreeNode> roots,
                                 Func<TreeNode, Element>? itemTemplate,
                                 bool isMultiSelectEnabled = false,
                                 Action<TreeNode>? itemInvoked = null,
                                 Action<TreeNode, bool>? selectionChanged = null,
                                 bool canReorderItems = false,
                                 Action<TreeNode?, int, int>? onReorder = null)
        => Embed.Comp(() => new TreeView
        {
            Roots = roots,
            ItemTemplate = itemTemplate,
            IsMultiSelectEnabled = isMultiSelectEnabled,
            ItemInvoked = itemInvoked,
            SelectionChanged = selectionChanged,
            CanReorderItems = canReorderItems,
            OnReorder = onReorder,
        });

    /// <summary>A visible row of the flat projection.</summary>
    private readonly record struct FlatRow(TreeNode Node, TreeNode? Parent, int Depth, int ChildIndex, string Id);

    public override Element Render()
    {
        var hooks = UseContext(InputHooks.Current);
        // Stable per-node identity (REFERENCE-keyed — TreeNode is a record, value equality would alias identical
        // subtrees): expansion/selection/keys survive sibling reorders, unlike positional paths.
        var ids = UseMemo(static () => new Dictionary<TreeNode, string>(ReferenceEqualityComparer.Instance), DepKey.Empty);
        var (expanded, setExpanded) = UseState(ImmutableHashSet<string>.Empty);
        var (selected, setSelected) = UseState<string?>(null);          // single-select
        var (multiSelected, setMultiSelected) = UseState(ImmutableHashSet<string>.Empty);
        var handles = UseMemo(static () => new Dictionary<string, NodeHandle>(), DepKey.Empty);
        var focusedId = UseRef<string?>(null);
        var reorder = UseMemo(static () => new ReorderList { DwellMs = ReorderList.ListDwellMs }, DepKey.Empty);
        var dragParentId = UseRef<string?>(null);
        var orderVersion = UseSignal(0);
        var structureVersion = UseSignal(0);     // bumped on in-place sibling mutation (OnReorder == null commits)
        var lastDwellTick = UseRef(0L);
        var autoExpandId = UseRef<string?>(null);     // collapsed-with-children row the pointer currently dwells over
        var autoExpandMs = UseRef(0f);                 // ms remaining until auto-expand (1000ms WinUI c_dragOverInterval)
        var dragPointerY = UseRef(0f);                 // last drag-delta absolute pointer Y (window space)
        var draggedId = UseRef<string?>(null);         // the row that started the drag (never auto-expand it)

        _ = orderVersion.Value;
        _ = structureVersion.Value;

        string IdOf(TreeNode n)
        {
            if (!ids.TryGetValue(n, out var id)) { id = "tn" + ids.Count; ids[n] = id; }
            return id;
        }

        // ── flatten the visible projection (projected sibling order while a drag is live) ───────────
        bool reordering = reorder.IsActive;
        var rows = new List<FlatRow>(32);
        int[] orderScratch = reordering ? new int[reorder.Count] : [];   // cold (per render during a drag only)

        void Walk(IReadOnlyList<TreeNode> siblings, TreeNode? parent, int depth)
        {
            string? parentId = parent is null ? null : IdOf(parent);
            bool projected = reordering && dragParentId.Value == (parentId ?? "") && siblings.Count == reorder.Count
                             && orderScratch.Length == reorder.Count;
            if (projected) reorder.ProjectOrder(orderScratch);
            for (int i = 0; i < siblings.Count; i++)
            {
                var n = siblings[projected ? orderScratch[i] : i];
                rows.Add(new FlatRow(n, parent, depth, projected ? orderScratch[i] : i, IdOf(n)));
                if (n.Children.Length > 0 && expanded.Contains(IdOf(n)))
                    Walk(n.Children, n, depth + 1);
            }
        }
        Walk(Roots, null, 0);

        // ── selection helpers ────────────────────────────────────────────────────────────────────────
        bool IsSelected(string id) => IsMultiSelectEnabled ? multiSelected.Contains(id) : selected == id;

        // Subtree state for the tri-state parent checkbox: 0 none, 1 partial, 2 all.
        int SubtreeState(TreeNode n)
        {
            bool any = false, all = true;
            void Visit(TreeNode x)
            {
                bool s = multiSelected.Contains(IdOf(x));
                any |= s; all &= s;
                foreach (var c in x.Children) Visit(c);
            }
            Visit(n);
            return all ? 2 : any ? 1 : 0;
        }

        void ToggleMulti(TreeNode n)
        {
            // WinUI cascades the toggle through the subtree (TreeViewNode selection propagation).
            bool select = SubtreeState(n) != 2;
            var set = multiSelected;
            void Visit(TreeNode x)
            {
                string id = IdOf(x);
                set = select ? set.Add(id) : set.Remove(id);
                foreach (var c in x.Children) Visit(c);
            }
            Visit(n);
            setMultiSelected(set);
            SelectionChanged?.Invoke(n, select);
        }

        void SelectSingle(TreeNode n)
        {
            string id = IdOf(n);
            if (selected != id) { setSelected(id); SelectionChanged?.Invoke(n, true); }
        }

        void ToggleExpand(TreeNode n)
        {
            string id = IdOf(n);
            bool open = expanded.Contains(id);
            setExpanded(open ? expanded.Remove(id) : expanded.Add(id));
            Expanding?.Invoke(n, !open);
        }

        void Invoke(TreeNode n)
        {
            if (IsMultiSelectEnabled) ToggleMulti(n);
            else SelectSingle(n);
            ItemInvoked?.Invoke(n);   // ItemClick → ItemInvoked (TreeView.cpp:159-165); selection is the list's
        }

        // ── keyboard (flat-order arrows + the cpp expand/collapse + Ctrl-reorder semantics) ─────────
        int RowIndexOf(string? id)
        {
            if (id is null) return -1;
            for (int i = 0; i < rows.Count; i++) if (rows[i].Id == id) return i;
            return -1;
        }

        void FocusRow(int i)
        {
            if ((uint)i >= (uint)rows.Count) return;
            focusedId.Value = rows[i].Id;
            if (handles.TryGetValue(rows[i].Id, out var h) && Context.Scene is { } s && s.IsLive(h))
                hooks.FocusNode?.Invoke(h, true);
        }

        void CommitSiblingMove(TreeNode? parent, int from, int to)
        {
            if (from == to) return;
            if (OnReorder is not null) { OnReorder(parent, from, to); return; }
            // In-place move (WinUI mutates its own node tree — ReorderItems, TreeViewItem.cpp:543-567).
            if (parent is not null) MoveInArray(parent.Children, from, to);
            else if (Roots is IList<TreeNode> mutableRoots) ReorderList.Move(mutableRoots, from, to);
            else if (Roots is TreeNode[] rootArr) MoveInArray(rootArr, from, to);
            structureVersion.Value = structureVersion.Peek() + 1;
        }

        void HandleKey(KeyEventArgs e)
        {
            int idx = RowIndexOf(focusedId.Value);
            if (idx < 0 && rows.Count > 0 && e.KeyCode is Keys.Up or Keys.Down or Keys.Home or Keys.End)
            {
                FocusRow(e.KeyCode == Keys.End ? rows.Count - 1 : 0);
                e.Handled = true;
                return;
            }
            if (idx < 0) return;
            var row = rows[idx];
            bool plain = !e.Ctrl && !e.Alt && !e.Shift;   // IsExpandCollapse: direction keys with NO modifiers (cpp:527-541)

            switch (e.KeyCode)
            {
                // WinUI keyboard reorder: Shift+Alt + Up/Left (backward) / Down/Right (forward), Ctrl up, CanReorderItems
                // (TreeViewItem.cpp:396). Move ±1 among SIBLINGS, collapse the node first (cpp:611-613). Position guard
                // applied to BOTH directions (fixes WinUI's Up||Left&&pos!=0 precedence bug — sweep:484).
                case Keys.Up when e.Shift && e.Alt && !e.Ctrl && CanReorderItems:
                case Keys.Left when e.Shift && e.Alt && !e.Ctrl && CanReorderItems:
                case Keys.Down when e.Shift && e.Alt && !e.Ctrl && CanReorderItems:
                case Keys.Right when e.Shift && e.Alt && !e.Ctrl && CanReorderItems:
                {
                    bool backward = e.KeyCode is Keys.Up or Keys.Left;
                    TreeNode[] siblings = row.Parent?.Children ?? (Roots as TreeNode[]) ?? [.. Roots];
                    int to = row.ChildIndex + (backward ? -1 : 1);
                    if (to >= 0 && to < siblings.Length)   // guard BOTH directions
                    {
                        if (expanded.Contains(row.Id)) ToggleExpand(row.Node);
                        CommitSiblingMove(row.Parent, row.ChildIndex, to);
                    }
                    e.Handled = true;
                    return;
                }
                case Keys.Up when plain:
                    FocusRow(idx - 1); e.Handled = true; return;
                case Keys.Down when plain:
                    FocusRow(idx + 1); e.Handled = true; return;
                case Keys.Home when plain:
                    FocusRow(0); e.Handled = true; return;
                case Keys.End when plain:
                    FocusRow(rows.Count - 1); e.Handled = true; return;
                case Keys.Left when plain:
                    // Collapse, else focus the parent (cpp:645-668).
                    if (expanded.Contains(row.Id)) { ToggleExpand(row.Node); e.Handled = true; }
                    else if (row.Parent is { } p)
                    {
                        int pi = RowIndexOf(IdOf(p));
                        if (pi >= 0) { FocusRow(pi); e.Handled = true; }
                    }
                    return;
                case Keys.Right when plain:
                    // Expand, else focus the first child (cpp:670-693).
                    if (row.Node.Children.Length > 0)
                    {
                        if (!expanded.Contains(row.Id)) ToggleExpand(row.Node);
                        else FocusRow(idx + 1);   // first child is the next visible row
                        e.Handled = true;
                    }
                    return;
            }
        }

        // ── drag reorder among siblings (sibling extent = its visible block height) ─────────────────
        // The collapsed-with-children row the dragged pointer currently sits over (window-space Y vs realized rects),
        // skipping the dragged row itself. null when the pointer is over an expanded row, a leaf, or empty space.
        string? HoveredCollapsedFolder()
        {
            if (Context.Scene is not { } s) return null;
            float py = dragPointerY.Value;
            for (int i = 0; i < rows.Count; i++)
            {
                var r = rows[i];
                if (r.Node.Children.Length == 0 || expanded.Contains(r.Id)) continue;   // only collapsed-with-children
                if (handles.TryGetValue(r.Id, out var h) && s.IsLive(h))
                {
                    var rc = s.AbsoluteRect(h);
                    if (py >= rc.Y && py <= rc.Y + rc.H && r.Id != draggedId.Value) return r.Id;   // not the dragged row itself
                }
            }
            return null;
        }

        // The dwell + 1s drag-over auto-expand tick on the frame clock while a drag is live. The tick subscription
        // drives the per-frame re-render; the ADVANCE + orderVersion bump run in the post-render effect below, NOT the
        // render body — writing orderVersion in render would be a backwards-write (the render subscribes it above to
        // bootstrap the reorder loop, so a same-run write re-marks this render stale = a convergence risk).
        long tick = reordering ? UseContext(FrameClock.Tick) : 0L;
        UseEffect(() =>
        {
            if (!reorder.IsActive) { lastDwellTick.Value = 0; autoExpandId.Value = null; return; }
            long now = Environment.TickCount64;
            float dt = lastDwellTick.Value == 0 ? 0f : Math.Clamp(now - lastDwellTick.Value, 0, 100);
            lastDwellTick.Value = now;
            if (reorder.Advance(dt)) orderVersion.Value = orderVersion.Peek() + 1;

            // 1s drag-over auto-expand (WinUI c_dragOverInterval=1000ms, reset-on-leave — TreeViewItem.h:14-15,
            // cpp:204-224/242-245/348-362): a collapsed row with children that the pointer dwells over for 1s expands.
            string? hoverId = HoveredCollapsedFolder();
            if (hoverId != autoExpandId.Value) { autoExpandId.Value = hoverId; autoExpandMs.Value = hoverId is null ? 0f : 1000f; }
            else if (hoverId is not null)
            {
                autoExpandMs.Value -= dt;
                if (autoExpandMs.Value <= 0f)
                {
                    autoExpandId.Value = null; autoExpandMs.Value = 0f;
                    var hn = rows.Find(r => r.Id == hoverId).Node;
                    if (hn is not null && !expanded.Contains(hoverId)) { ToggleExpand(hn); orderVersion.Value = orderVersion.Peek() + 1; }
                }
            }
        }, DepKey.From(reordering ? tick + 1 : 0L));

        // Edge auto-scroll during a drag (WinUI band 100px, 150-1500px/s, ListViewBase_Partial_Reorder.cpp:39-47).
        // The flat row column has no ScrollView of its own, so nudge the nearest SCROLLABLE ANCESTOR (the consumer's
        // ScrollView) when one exists; no-op otherwise (the gallery card has none → harmless). Cold drag path.
        void EdgeAutoScroll(float pointerY)
        {
            if (Context.Scene is not { } s) return;
            // Walk to the nearest scrollable ancestor of this tree (the consumer's ScrollView). None → no-op.
            var vp = NodeHandle.Null;
            for (var n = Context.HostNode; !n.IsNull; n = s.Parent(n))
                if ((s.Flags(n) & NodeFlags.Scrollable) != 0) { vp = n; break; }
            if (vp.IsNull || !s.IsLive(vp) || !s.TryGetScroll(vp, out var sc)) return;
            var rc = s.AbsoluteRect(vp);
            const float band = 24f, speed = 8f;   // engine nudge (the WinUI 100px/150-1500px/s gradient lives in DragDropContext; the tree path is hand-rolled to match ListView's nudge)
            float delta = 0f;
            if (pointerY < rc.Y + band) delta = -speed;
            else if (pointerY > rc.Y + rc.H - band) delta = speed;
            if (delta == 0f) return;
            float viewport = sc.ViewportH, content = sc.ContentH, now = sc.OffsetY;
            float target = Math.Clamp(now + delta, 0f, MathF.Max(0f, content - viewport));
            if (target == now) return;
            ref ScrollState scw = ref s.ScrollRef(vp);
            scw.OffsetY = target; scw.TargetY = target;
            var cn = sc.ContentNode;
            if (!cn.IsNull && s.IsLive(cn)) { s.Paint(cn).LocalTransform = Affine2D.Translation(0f, -target); s.Mark(cn, NodeFlags.TransformDirty | NodeFlags.PaintDirty); }
            s.Mark(vp, NodeFlags.VirtualRangeDirty);
            orderVersion.Value = orderVersion.Peek() + 1;
        }

        float BlockExtent(TreeNode n, string? collapsedId)
        {
            float h = handles.TryGetValue(IdOf(n), out var hd) && Context.Scene is { } s && s.IsLive(hd)
                ? s.AbsoluteRect(hd).H + 4f   // + the 2+2 presenter margins
                : RowStrideFallback;
            if (IdOf(n) != collapsedId && expanded.Contains(IdOf(n)))
                foreach (var c in n.Children) h += BlockExtent(c, collapsedId);
            return h;
        }

        void StartDrag(FlatRow row)
        {
            // WinUI collapses the dragged node before reordering it (the keyboard path does, cpp:611-613; a collapsed
            // block keeps the live-reorder math to one row + its hidden subtree).
            if (expanded.Contains(row.Id)) ToggleExpand(row.Node);
            var siblings = row.Parent?.Children ?? null;
            int count = siblings?.Length ?? Roots.Count;
            Span<float> extents = count <= 128 ? stackalloc float[count] : new float[count];
            for (int i = 0; i < count; i++)
                extents[i] = BlockExtent(siblings is not null ? siblings[i] : Roots[i], row.Id);
            reorder.Begin(row.ChildIndex, extents, spacing: 0f);
            var parent = row.Parent;
            reorder.OnCommit = (from, to) => CommitSiblingMove(parent, from, to);
            dragParentId.Value = parent is null ? "" : IdOf(parent);
            draggedId.Value = row.Id;
            autoExpandId.Value = null; autoExpandMs.Value = 0f;
            orderVersion.Value = orderVersion.Peek() + 1;
        }

        // ── rows ─────────────────────────────────────────────────────────────────────────────────────
        var children = new Element[rows.Count];
        for (int i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            var node = row.Node;
            string id = row.Id;
            bool hasKids = node.Children.Length > 0;
            bool open = expanded.Contains(id);
            bool isSelected = IsSelected(id);
            int triState = IsMultiSelectEnabled && hasKids ? SubtreeState(node) : (isSelected ? 2 : 0);

            var el = BuildRow(
                content: ItemTemplate?.Invoke(node) ?? new TextEl(node.Label) { Size = 14f, Color = Tok.TextPrimary, PressedColor = Tok.TextSecondary },
                depth: row.Depth,
                hasChildren: hasKids,
                isExpanded: open,
                isSelected: isSelected,
                isEnabled: node.IsEnabled,
                showCheckbox: IsMultiSelectEnabled,
                checkState: triState,
                onChevron: () => ToggleExpand(node),
                onInvoke: () => Invoke(node),
                onSpace: () => { if (IsMultiSelectEnabled) ToggleMulti(node); else SelectSingle(node); },
                onFocusChanged: got => { if (got) focusedId.Value = id; },
                onRealized: h => handles[id] = h);

            el = el with { Key = id };
            if (CanReorderItems && node.IsEnabled)
            {
                var rowCopy = row;
                el = el with
                {
                    CanDrag = true,
                    OnDragStarted = _ => StartDrag(rowCopy),
                    OnDragDelta = e =>
                    {
                        dragPointerY.Value = e.Absolute.Y;
                        if (reorder.Update(e.TotalDy)) orderVersion.Value = orderVersion.Peek() + 1;
                        EdgeAutoScroll(e.Absolute.Y);
                    },
                    OnDragCompleted = _ =>
                    {
                        reorder.Complete();
                        dragParentId.Value = null;
                        draggedId.Value = null;
                        autoExpandId.Value = null; autoExpandMs.Value = 0f;
                        orderVersion.Value = orderVersion.Peek() + 1;
                    },
                    OnDragCanceled = () =>
                    {
                        reorder.Cancel();
                        dragParentId.Value = null;
                        draggedId.Value = null;
                        autoExpandId.Value = null; autoExpandMs.Value = 0f;
                        orderVersion.Value = orderVersion.Peek() + 1;
                    },
                };
            }
            children[i] = el;
        }

        // ── prune the realized-handle map (parity sweep:472 — was unbounded per scroll) ───────────────
        // `handles` only ever ADDED (onRealized: handles[id] = h) and never pruned, so every row id that ever
        // realized — including the ones a Roots rebuild dropped and every off-screen row a long scroll touched —
        // leaked an entry forever. A stale handle for an off-screen / removed row is dead weight (its only readers
        // — FocusRow/HoveredCollapsedFolder/BlockExtent — already guard with s.IsLive). So at the end of Render
        // (cold, once per render) keep only the ids that are CURRENTLY realized this projection and drop the rest.
        // (rows.Count == 0 ⇒ live = empty ⇒ all handles cleared, correct: nothing is realized.)
        // `ids` (the REFERENCE-keyed TreeNode→id map, TreeView.cs:107) is deliberately NOT pruned here: those entries
        // are what keep expansion/selection/keys stable across sibling reorders, and a node can be momentarily
        // unrealized (collapsed/off-screen) yet still reachable from Roots and about to reappear with the SAME id —
        // pruning by realization would re-mint its id and break that stability. Its growth is bounded by distinct
        // TreeNode instances ever seen (acceptable); only `handles` was unbounded-per-scroll.
        // (No count fast-path: handles can hold a stale id AND miss a not-yet-realized one at equal Count, so a
        // count guard could skip a needed prune. Walking handles.Keys unconditionally is still cold — handles is
        // bounded by realized rows — and the drop list is lazily allocated only when something actually leaked, so
        // the steady state with no stale keys allocates nothing beyond the `live` set.)
        var live = new HashSet<string>(rows.Count);
        for (int i = 0; i < rows.Count; i++) live.Add(rows[i].Id);
        List<string>? drop = null;
        foreach (var key in handles.Keys)
            if (!live.Contains(key)) (drop ??= new List<string>()).Add(key);
        if (drop is not null) foreach (var key in drop) handles.Remove(key);

        // Same-list reorder shows DISPLACEMENT only (the keyed rows FLIP to part) — WinUI draws NO insertion line for
        // same-list reorder (no InsertionLine/DropLine element anywhere in TreeViewItem.xaml; the only indicator is the
        // 3x16 SelectionIndicator, which is for selection). The 2px accent line stays ONLY in Reorderable's cross-list case.
        return new BoxEl { Key = "treeview-rows", Direction = 1, OnKeyDown = HandleKey, Children = children };
    }

    private static void MoveInArray(TreeNode[] arr, int from, int to)
    {
        if (from == to || (uint)from >= (uint)arr.Length || (uint)to >= (uint)arr.Length) return;
        var item = arr[from];
        if (to > from) Array.Copy(arr, from + 1, arr, from, to - from);
        else Array.Copy(arr, to, arr, to + 1, from - to);
        arr[to] = item;
    }

    /// <summary>The TreeViewItem row chrome (per-value cites in the class doc). The chevron is a SEPARATE hit target
    /// (expand-only); the row body selects/invokes.</summary>
    private static BoxEl BuildRow(Element content, int depth, bool hasChildren, bool isExpanded, bool isSelected,
                                  bool isEnabled, bool showCheckbox, int checkState,
                                  Action onChevron, Action onInvoke, Action onSpace,
                                  Action<bool> onFocusChanged, Action<NodeHandle> onRealized)
    {
        // SelectionIndicator: 3×16 r2, left-aligned, vertically centred, visible ONLY while selected
        // (TreeViewItem.xaml:130 + the Selected states' Opacity=1 vs unselected 0).
        var indicator = new BoxEl
        {
            Width = 3f,
            Height = 16f,
            Corners = CornerRadius4.All(2f),
            Fill = isEnabled ? Tok.AccentDefault : Tok.TextDisabled,   // (:33 / :36)
            AlignSelf = FlexAlign.Center,
            HitTestVisible = false,
        };

        // The chevron cell: its OWN clickable box (nearest-clickable wins the hit-test, so a chevron press never
        // reaches the row body — the engine analogue of args.Handled in OnExpandCollapseChevronPointerPressed).
        Element chevronCell = hasChildren
            ? new BoxEl
            {
                Padding = new Edges4(ChevronPad, 0, ChevronPad, 0),   // ExpandCollapseChevron Padding 14,0 (xaml:143)
                AlignItems = FlexAlign.Center,
                Justify = FlexJustify.Center,
                AlignSelf = FlexAlign.Stretch,
                OnClick = onChevron,
                Children =
                [
                    new TextEl(isExpanded ? "" : "")      // ExpandedGlyph/CollapsedGlyph (TreeView.idl:201-203)
                    {
                        Size = GlyphSize,                              // GlyphSize default 8.0 (idl:204)
                        FontFamily = Theme.IconFont,
                        Color = Tok.TextPrimary,
                        PressedColor = Tok.TextSecondary,              // Pressed → TreeViewItemForegroundPressed (xaml:33-36)
                        DisabledColor = Tok.TextDisabled,
                    },
                ],
            }
            : new BoxEl { Width = ChevronPad * 2f + GlyphSize + 4f };  // leaves reserve the chevron cell width

        var lane = new List<Element>(4);
        if (showCheckbox)
        {
            // MultiSelectCheckBox: 32px lane, Margin 10,0,0,0 (TreeViewItem.xaml:138); 20px plate; the tri-state
            // parent shows the indeterminate dash (TreeViewNode PartialSelectionState).
            lane.Add(new BoxEl
            {
                Width = 32f,
                Margin = new Edges4(10f, 0f, 0f, 0f),
                AlignItems = FlexAlign.Center,
                Justify = FlexJustify.Center,
                AlignSelf = FlexAlign.Stretch,
                HitTestVisible = false,
                Animate = new LayoutTransition(
                    TransitionChannels.Opacity,
                    TransitionDynamics.Tween(167f, Easing.FluentDecelerate),
                    Enter: new EnterExit(Dx: -32f, Opacity: 0f, Active: true),
                    Exit: new EnterExit(Dx: -32f, Opacity: 0f, Active: true)),
                Children = [BuildTriCheckPlate(checkState, isEnabled)],
            });
        }
        lane.Add(chevronCell);
        lane.Add(new BoxEl
        {
            Direction = 0,
            AlignItems = FlexAlign.Center,
            Grow = 1f,
            MinHeight = MinRowHeight - 8f,        // TreeViewItemContentHeight band inside the 0,3,0,5 padding
            Children = [content],
        });

        return new BoxEl
        {
            ZStack = true,
            MinHeight = MinRowHeight,                              // TreeViewItemMinHeight 28 (:41)
            Margin = new Edges4(4f, 2f, 4f, 2f),                   // TreeViewItemPresenterMargin 4,2 (:38)
            Corners = Radii.ControlAll,
            // Plate ramp (:5-11): selected rest/pressed = Secondary, selected hover = Tertiary;
            // unselected hover = Secondary, pressed = Tertiary.
            Fill = isSelected ? Tok.FillSubtleSecondary : ColorF.Transparent,
            HoverFill = isSelected ? Tok.FillSubtleTertiary : Tok.FillSubtleSecondary,
            PressedFill = isSelected ? Tok.FillSubtleSecondary : Tok.FillSubtleTertiary,
            Opacity = isEnabled ? 1f : DisabledOpacity,
            IsEnabled = isEnabled,
            Focusable = true,
            FocusVisualMargin = new Edges4(0f, -1f, 0f, -1f),      // FocusVisualMargin 0,-1,0,-1 (TreeViewItem.xaml:11)
            Role = AutomationRole.Button,
            OnClick = onInvoke,
            OnKeyDown = args =>
            {
                if (args.KeyCode == Keys.Enter) { onInvoke(); args.Handled = true; }
                else if (args.KeyCode == Keys.Space && !args.IsRepeat) { onSpace(); args.Handled = true; }
            },
            OnFocusChanged = onFocusChanged,
            OnRealized = onRealized,
            Animate = LayoutTransition.Slide with { Dynamics = TransitionDynamics.Spring(0.18f, 1f) },
            Children =
            [
                new BoxEl
                {
                    Direction = 0,
                    AlignItems = FlexAlign.Center,
                    Grow = 1f,
                    Padding = new Edges4(depth * IndentStep, 3f, 8f, 5f),   // Indentation depth×16 + presenter 0,3,0,5
                    Children = lane.ToArray(),
                },
                isSelected && isEnabled
                    ? indicator with
                    {
                        Animate = new LayoutTransition(
                            TransitionChannels.Opacity,
                            new TransitionDynamics(DynamicsKind.Spring, 0.30f, 0.85f),   // MotionSprings.NavPill values
                            Enter: new EnterExit(Sy: 0f, Opacity: 0f, Active: true)),
                    }
                    : new BoxEl { Width = 0f, Height = 0f, HitTestVisible = false },
            ],
        };
    }

    /// <summary>The 20px multi-select plate: 0 = unchecked (alt plate + strong stroke), 1 = indeterminate dash
    /// (accent plate + 8×2 bar — the WinUI partial state), 2 = checked (accent + drawn checkmark).</summary>
    private static BoxEl BuildTriCheckPlate(int state, bool enabled)
        => new()
        {
            Width = 20f,
            Height = 20f,
            Corners = CornerRadius4.All(3f),
            BorderWidth = 1f,
            Fill = state > 0 ? (enabled ? Tok.AccentDefault : Tok.AccentDisabled) : Tok.FillControlAltSecondary,
            BorderColor = state > 0
                ? (enabled ? Tok.AccentDefault : Tok.AccentDisabled)
                : (enabled ? Tok.StrokeControlStrongDefault : Tok.StrokeControlStrongDisabled),
            AlignItems = FlexAlign.Center,
            Justify = FlexJustify.Center,
            HitTestVisible = false,
            Children = state switch
            {
                2 =>
                [
                    new PolylineStrokeEl
                    {
                        Width = 14f,
                        Height = 14f,
                        P0 = new Point2(0.18f * 14f, 0.50f * 14f),
                        P1 = new Point2(0.42f * 14f, 0.72f * 14f),
                        P2 = new Point2(0.80f * 14f, 0.26f * 14f),
                        PointCount = 3,
                        Color = enabled ? Tok.TextOnAccentPrimary : Tok.TextOnAccentDisabled,
                        Thickness = 1.8f,
                        RoundCaps = true,
                    },
                ],
                1 => [new BoxEl { Width = 8f, Height = 2f, Corners = CornerRadius4.All(1f), Fill = Tok.TextOnAccentPrimary }],
                _ => [],
            },
        };
}
