using System;
using System.Collections.Generic;
using FluentGpu.Foundation;
using FluentGpu.Scene;
using FluentGpu.Scroll.Diag;
using FluentGpu.Scroll.Runtime;

namespace FluentGpu.Animation;

// ─────────────────────────────────────────────────────────────────────────────────────────────────────────────
//  SizeMode.FlowReveal — layout ONCE, motion at paint time (docs/plans/smooth-reveal-implementation.md).
//
//  A FlowReveal node's size change (a mount, an unmount, a resize) lands in LAYOUT in one pass; one row
//  (AnimChannel.RevealExtent) springs the node's PRESENTED vertical extent old → new under MotionTok.Reveal. The host
//  seeds the rows right after layout (6.3) and PropagateFlowReveals folds every live row into NodePaint.FlowDelta on the
//  node and its ancestors (up to the nearest flow boundary or containing reveal). The recorder / hit-test walk
//  (FlowCursor) shifts every following sibling by it. Siblings, parents, the scroll extent and the scrollbar move in
//  lockstep with no per-frame layout, no component render and no allocation. Interruptible by construction: the row is a
//  spring on an ABSOLUTE extent, so a reverse or a content-growth retarget departs from the live value with its velocity.
//  Nested reveals combine, never sum: a reveal presents its own spring, bounded — only while an inner reveal runs inside
//  it — by its content's presented bottom (ContentExtent + inner deltas), never by its laid-out height (a close lays out
//  at its FINAL height, which would snap it shut).
//
//  Settle callbacks (WhenSettled) replace the FrameClock poller components that used to watch HasTracks: the tick (or a
//  snap, or the node's death) queues them, and the host drains them at the START of the next frame (outside the
//  zero-alloc phases).
// ─────────────────────────────────────────────────────────────────────────────────────────────────────────────

public sealed partial class AnimEngine
{
    /// <summary>Nodes mounted this commit with a FlowReveal Enter. The host seeds their reveal right after layout (6.3);
    /// its projection pass then reads them as shove carriers and clears the list.</summary>
    public List<NodeHandle> PendingEnterReveal { get; } = new(8);
    /// <summary>FlowReveal exits orphaned this commit, with their former visual parent. The projection pass treats them
    /// as shove carriers (a sibling they moved must not position-FLIP on top of the presented flow).</summary>
    public List<(NodeHandle Node, NodeHandle VisualParent)> RevealExitCarriers { get; } = new(8);

    private readonly record struct SettleKey(uint Index, uint Gen, AnimChannel Channel);
    private readonly record struct RevealEntry(NodeHandle Node, int Depth, float Presented, float Target);

    private int _revealRows;                                    // live RevealExtent rows (the idle fast path's census)
    private readonly List<NodeHandle> _flowTouched = new(64);   // nodes whose flow columns the last pass wrote
    private readonly List<RevealEntry> _revealOrder = new(16);  // this pass's live reveals, deepest first
    private List<NodeHandle> _ovViewports = new(8), _ovPrevViewports = new(8);   // viewports given RevealOverscan
    private List<float> _ovAmount = new(8), _ovPrevAmount = new(8);              // (this pass / last pass)
    private bool _overscanGrew;
    private readonly Dictionary<SettleKey, Action> _settleCallbacks = new();
    private readonly Dictionary<uint, int> _settleIndexRefs = new();   // node index → callbacks registered on it
    private readonly List<Action> _settledQueue = new(32);
    private Action?[] _settledDrain = new Action?[32];

    /// <summary>True when this frame's pass raised a viewport's realize overscan past its realized window (the host
    /// re-realizes + re-lays out once, then re-runs the pass).</summary>
    public bool FlowOverscanGrew => _overscanGrew;

    /// <summary>True while any FlowReveal work is pending or running: a live reveal row, an entrant to seed, an exit
    /// orphaned this commit, a live reveal band. The host defers layout's scroll-offset clamp on these frames (6.3 clamps
    /// against the PRESENTED extent instead).</summary>
    public bool HasFlowRevealWork
        => _revealRows > 0 || PendingEnterReveal.Count > 0 || RevealExitCarriers.Count > 0 || _scene.HasActiveRevealBands;

    /// <summary>True while <paramref name="node"/> has a live reveal row (the Expander's always-fire check, gates).</summary>
    public bool IsRevealing(NodeHandle node) => _slab.NodeHasRows((int)node.Raw.Index) && Find(node, AnimChannel.RevealExtent) >= 0;

    /// <summary>Registered settle callbacks (diagnostics + gates: a dead node must leave none behind).</summary>
    public int SettleCallbackCount => _settleCallbacks.Count;

    // ── seeding ─────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>A FlowReveal node mounted this commit (host 6.3, post-layout, before the tick): present it from 0. When
    /// its visual parent still paints a revealing EXIT orphan of the same slot (a reopen mid-close), present it from that
    /// orphan's live extent and velocity instead, and reclaim the orphan. A reverse never shows two copies or restarts
    /// from zero.</summary>
    public void SeedFlowRevealEnter(NodeHandle node)
    {
        if (!_scene.IsLive(node) || !TryGetTransition(node, out var spec)) return;
        float to = _scene.Bounds(node).H;
        float from = 0f, v0 = 0f;
        if (TakeRevealOrphan(node, out float orphanP, out float orphanV)) { from = orphanP; v0 = orphanV; }
        SeedFlowReveal(node, from, to, v0, spec.Dynamics, enter: true);
    }

    /// <summary>A FlowReveal node's laid-out height changed this commit (host 6.3, from the FLIP capture).</summary>
    public void SeedFlowRevealResize(NodeHandle node, float fromH, float toH, in TransitionDynamics dyn)
        => SeedFlowReveal(node, fromH, toH, 0f, in dyn, enter: false);

    /// <summary>A FlowReveal node was just orphaned (Reconciler.Remove): present it from <paramref name="fromP"/> (its live
    /// extent, read BEFORE the unmount cancelled its rows) down to 0 with <paramref name="v0"/>. The row keeps the orphan
    /// alive; the host reclaims it when the row settles.</summary>
    public void SeedFlowRevealExit(NodeHandle node, float fromP, float v0, in LayoutTransition spec)
    {
        TransitionDynamics dyn = spec.ExitDynamics ?? spec.Dynamics;
        SeedFlowReveal(node, fromP, 0f, v0, in dyn, enter: true);
    }

    /// <summary>Land a FlowReveal node on its laid-out height <paramref name="toH"/> NOW (host 6.3 under suppressed
    /// projections: a user scroll in flight, a keep-alive switch). The row (if any) is cancelled; a node wholly above the
    /// view anchors the scroll like any snap; and the settle callback still fires: a snapped reveal is a settled one.</summary>
    public void SnapFlowReveal(NodeHandle node, float fromH, float toH)
    {
        const AnimChannel ch = AnimChannel.RevealExtent;
        int ex = Find(node, ch);
        float cur = ex >= 0 ? _slab.At(ex).Position : fromH;
        if (ex >= 0) Cancel(node, ch);
        RevealView(node, out float top, out float viewTop, out _, out NodeHandle viewport);
        if (RevealPlan.IsAboveView(cur, toH, top, viewTop)) AnchorShift(node, viewport, toH - cur);
        QueueSettled(node, ch);
    }

    /// <summary>The live presented extent + velocity of a node's reveal; its laid-out height and 0 at rest.</summary>
    public void ReadFlowReveal(NodeHandle node, out float presented, out float velocity)
    {
        int s = Find(node, AnimChannel.RevealExtent);
        if (s >= 0) { presented = _slab.At(s).Position; velocity = _slab.At(s).Velocity; return; }
        presented = _scene.IsLive(node) ? _scene.Bounds(node).H : 0f;
        velocity = 0f;
    }

    private void SeedFlowReveal(NodeHandle node, float p0, float p1, float v0, in TransitionDynamics dynIn, bool enter)
    {
        const AnimChannel ch = AnimChannel.RevealExtent;
        TransitionDynamics dyn = Normalize(dynIn);
        int ex = Find(node, ch);
        float cur = ex >= 0 ? _slab.At(ex).Position : p0;
        float vel = ex >= 0 ? _slab.At(ex).Velocity : v0;
        // Reduced motion is a VALUE read here (never a branch in authoring code).
        bool snap = FluentGpu.Dsl.Motion.ReducedMotion || (dyn.Kind == DynamicsKind.Tween && dyn.DurationMs <= 1f);
        RevealView(node, out float top, out float viewTop, out float viewBottom, out NodeHandle viewport);
        float from = p1, to = p1;
        if (!snap) snap = !RevealPlan.TryClamp(cur, p1, top, viewTop, viewBottom, out from, out to);
        if (snap)
        {
            // Wholly ABOVE the view (whatever made it snap, reduced motion included): the snap must not move what the user
            // is reading. Shift the scroll frame by what the content below just moved (an instant anchor shift, never a motion).
            if (RevealPlan.IsAboveView(cur, p1, top, viewTop)) AnchorShift(node, viewport, p1 - cur);
            if (ex >= 0) Cancel(node, ch);
            QueueSettled(node, ch);
            return;
        }
        if (ex >= 0 && MathF.Abs(from - cur) < 0.5f && dyn.Kind == DynamicsKind.Spring && _slab.At(ex).Kind == GenKind.Spring)
        {
            Spring(node, ch, to, SpringParams.FromResponse(dyn.Response, dyn.DampingRatio));   // retarget: live value + velocity
            return;
        }
        // The visible window moved the departure point (or the dynamics changed): jump where nobody sees it, keep the speed.
        if (ex >= 0) Cancel(node, ch);
        if (dyn.Kind == DynamicsKind.Spring)
            Spring(node, ch, to, SpringParams.FromResponse(dyn.Response, dyn.DampingRatio), initial: from, initialVelocity: vel);
        else
            Animate(node, ch, from, to, dyn.DurationMs, dyn.Easing);
        ClaimRevealClip(node);
        if (enter) MarkStartPending(node);
    }

    // The node clips its content at the presented extent for the life of the row — the reflow ClipAdded contract: a node
    // that declared ClipToBounds itself is never un-clipped at teardown (FreeSlot releases only what the row added).
    private void ClaimRevealClip(NodeHandle node)
    {
        int s = Find(node, AnimChannel.RevealExtent);
        if (s < 0 || !_scene.IsLive(node)) return;
        ref AnimValue r = ref _slab.At(s);
        if (r.Has(AnimFlags.ClipAdded) || (_scene.Flags(node) & NodeFlags.ClipsToBounds) != 0) return;
        _scene.Mark(node, NodeFlags.ClipsToBounds | NodeFlags.PaintDirty);
        r.Flags |= AnimFlags.ClipAdded;
    }

    // The node's top and the view it is seen through, in window DIP: the nearest vertical scroll viewport (returned in
    // `viewport`), else the root (viewport = Null). An exit orphan is detached, so its top is read through its former
    // visual parent.
    private void RevealView(NodeHandle node, out float top, out float viewTop, out float viewBottom, out NodeHandle viewport)
    {
        NodeHandle anchor = node;
        float localTop = 0f;
        if ((_scene.Flags(node) & NodeFlags.Exiting) != 0 && _scene.TryGetOrphanVisualParent(node, out var vp) && !vp.IsNull)
        {
            anchor = vp;
            localTop = _scene.Bounds(node).Y;
        }
        top = _scene.AbsoluteRect(anchor).Y + localTop;
        for (var p = anchor == node ? _scene.Parent(node) : anchor; !p.IsNull; p = _scene.Parent(p))
        {
            if ((_scene.Flags(p) & NodeFlags.Scrollable) == 0 || !_scene.TryGetScroll(p, out var sc) || sc.Orientation != 0) continue;
            RectF r = _scene.AbsoluteRect(p);
            viewTop = r.Y;
            viewBottom = r.Y + sc.ViewportH;
            viewport = p;
            return;
        }
        RectF root = _scene.Root.IsNull ? default : _scene.Bounds(_scene.Root);
        viewTop = root.Y;
        viewBottom = root.Bottom;
        viewport = NodeHandle.Null;
    }

    // Scroll anchoring for a reveal that snapped wholly above the view. Shift the viewport's plan frame by how far the
    // change moves the content that follows the node: a column hop carries all of it, a row / z-stack hop only what
    // reaches past its tallest other child, and a flow boundary absorbs it. Uses ScrollHandle.ShiftFrame, the same instant
    // rebase a measured correction above the anchor takes (it moves with a live plan, never starts a motion).
    private void AnchorShift(NodeHandle node, NodeHandle viewport, float d)
    {
        if (viewport.IsNull || MathF.Abs(d) < 0.5f || _scene.ScrollHandleFor(viewport) is not { } handle) return;
        NodeHandle child = node, parent = _scene.Parent(node);
        bool viaOrphan = (_scene.Flags(node) & NodeFlags.Exiting) != 0;
        if (viaOrphan && (!_scene.TryGetOrphanVisualParent(node, out parent) || parent.IsNull)) return;
        while (!parent.IsNull && _scene.IsLive(parent) && parent != viewport)
        {
            d = FlowContribution(parent, child, d, viaOrphan);
            if (MathF.Abs(d) < 0.5f) return;
            if (IsVerticalScrollContent(parent, out _)) { handle.ShiftFrame(d); return; }
            if (IsFlowBoundary(parent)) return;
            child = parent;
            parent = _scene.Parent(parent);
            viaOrphan = false;
        }
    }

    // A reopen mid-close: the entrant's visual parent still paints a revealing exit orphan of the SAME slot (same element
    // type, same laid-out top). Hand its extent + velocity to the entrant and reclaim it now (the reclaim frees the
    // orphan's slot, which fires and drops any settle callback registered on it — ClearForIndex).
    private bool TakeRevealOrphan(NodeHandle node, out float presented, out float velocity)
    {
        presented = velocity = 0f;
        if (_scene.OrphanCount == 0) return false;
        NodeHandle parent = _scene.Parent(node);
        if (parent.IsNull || _scene.OrphanChildrenOf(parent) is not { } orphans) return false;
        float top = _scene.Bounds(node).Y;
        ushort type = _scene.ElementTypeId(node);
        for (int i = orphans.Count - 1; i >= 0; i--)
        {
            NodeHandle o = orphans[i];
            if (!_scene.IsLive(o) || _scene.ElementTypeId(o) != type || MathF.Abs(_scene.Bounds(o).Y - top) > 1f) continue;
            int s = Find(o, AnimChannel.RevealExtent);
            if (s < 0) continue;
            presented = _slab.At(s).Position;
            velocity = _slab.At(s).Velocity;
            CancelAll(o);
            _scene.ReclaimOrphan(o);
            return true;
        }
        return false;
    }

    // ── settle callbacks ────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Run <paramref name="callback"/> on the UI thread at the START of the next frame (before the reactive flush —
    /// never inside the zero-alloc phases) each time the row on <paramref name="node"/>/<paramref name="channel"/> comes to
    /// rest. That includes a reveal that snaps (reduced motion, off-screen, suppressed). When the node itself is freed
    /// (recycled, reclaimed, unmounted), the callback runs once more and is dropped. Null unregisters. One callback per
    /// (node, channel). Replaces the FrameClock poller components that polled <see cref="HasTracks"/>.</summary>
    public void WhenSettled(NodeHandle node, AnimChannel channel, Action? callback)
    {
        var key = new SettleKey(node.Raw.Index, node.Raw.Gen, channel);
        if (callback is null)
        {
            if (!_settleCallbacks.Remove(key)) return;
            if (_settleIndexRefs.TryGetValue(key.Index, out int refs) && refs > 1) _settleIndexRefs[key.Index] = refs - 1;
            else _settleIndexRefs.Remove(key.Index);
            return;
        }
        if (_settleCallbacks.TryAdd(key, callback))
            _settleIndexRefs[key.Index] = _settleIndexRefs.TryGetValue(key.Index, out int n) ? n + 1 : 1;
        else _settleCallbacks[key] = callback;
    }

    /// <summary>True while settle callbacks wait for the next frame's drain (the host keeps one more frame coming).</summary>
    public bool HasSettledCallbacks => _settledQueue.Count != 0;

    /// <summary>Host, frame start (before the reactive flush): invoke the callbacks the last frame queued, in order.</summary>
    public void DrainSettledCallbacks()
    {
        int n = _settledQueue.Count;
        if (n == 0) return;
        if (_settledDrain.Length < n) _settledDrain = new Action?[Math.Max(n, _settledDrain.Length * 2)];
        for (int i = 0; i < n; i++) _settledDrain[i] = _settledQueue[i];
        _settledQueue.Clear();
        for (int i = 0; i < n; i++)
        {
            Action cb = _settledDrain[i]!;
            _settledDrain[i] = null;
            cb();
        }
    }

    private void QueueSettled(NodeHandle node, AnimChannel channel)
    {
        if (_settleCallbacks.Count == 0) return;
        if (_settleCallbacks.TryGetValue(new SettleKey(node.Raw.Index, node.Raw.Gen, channel), out Action? cb))
            Enqueue(cb);
    }

    // A callback already queued this frame (a row that settled, then its node freed in the same frame) runs once.
    private void Enqueue(Action cb)
    {
        if (!_settledQueue.Contains(cb)) _settledQueue.Add(cb);
    }

    /// <summary>The slot at <paramref name="index"/> is being freed (ClearForIndex): whatever waited on one of its rows
    /// coming to rest is over. Queue each callback registered on it once and forget it, so nothing waits forever and nothing
    /// leaks. Covers a drawer recycled out from under its owner, an exit orphan reclaimed by a reopen, a viewport
    /// unmounted with bands in flight. (Removing during the enumeration is legal on .NET Core 3+.)</summary>
    private void FireSettleCallbacksForIndex(int index)
    {
        if (_settleCallbacks.Count == 0 || !_settleIndexRefs.Remove((uint)index)) return;
        foreach (var kv in _settleCallbacks)
        {
            if (kv.Key.Index != (uint)index) continue;
            Enqueue(kv.Value);
            _settleCallbacks.Remove(kv.Key);
        }
    }

    // ── the flow pass (host 6.3 and phase 7.05) ─────────────────────────────────────────────────────────────

    /// <summary>Rebuild the presented-flow columns (<see cref="NodePaint.FlowDelta"/>/<c>FlowBits</c>/<c>FlowOrphan*</c>,
    /// the owned PresentedH/ChildShiftY, and <see cref="ScrollState.RevealOverscan"/>) from the live reveal rows. The host
    /// runs it at 6.3 (right after a commit's layout, on the rows it just seeded) and at 7.05 (after the tick and the
    /// reflow re-solve, before record). False with zero work when nothing reveals and nothing did last pass; true
    /// otherwise (the host then re-clamps scroll extents against the PRESENTED content). Allocation-free: pre-sized
    /// lists, and the walk visits only the reveal nodes' ancestor chains.</summary>
    public bool PropagateFlowReveals()
    {
        _overscanGrew = false;
        if (_revealRows == 0 && _flowTouched.Count == 0 && _ovPrevViewports.Count == 0 && !_scene.HasActiveRevealBands) return false;
        ResetFlow();
        if (_revealRows > 0)
        {
            CollectReveals();
            for (int i = 0; i < _revealOrder.Count; i++)
            {
                RevealEntry e = _revealOrder[i];
                AddReveal(e.Node, e.Presented, e.Target);
            }
            _revealOrder.Clear();
        }
        var bandViewports = _scene.RevealBandViewports;
        for (int i = 0; i < bandViewports.Count; i++) AddBands(bandViewports[i]);
        FinishFlow();
        return true;
    }

    // ── virtual reveal bands ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>The row channel of band <paramref name="slot"/> on its viewport node.</summary>
    public static AnimChannel RevealBandChannel(int slot) => (AnimChannel)((int)AnimChannel.RevealBand0 + slot);

    private static bool IsRevealBand(AnimChannel ch) => ch >= AnimChannel.RevealBand0 && ch <= AnimChannel.RevealBand3;

    /// <summary>Arm (or REVERSE) band <paramref name="slot"/> of a vertical virtual viewport over the logical rows
    /// [<paramref name="first"/>, +<paramref name="count"/>) — the model must already hold the rows laid out (an expand
    /// inserts them first; a collapse keeps them until its commit). Springs the presented height toward the rows' extent
    /// (<paramref name="opening"/>) or 0 under MotionTok.Reveal, from the live value with its velocity, clamped to the
    /// visible span; snaps (and reports settled) under reduced motion or when nothing of it is visible. The band records the
    /// visible span its rows slide by (<see cref="RevealBand.Visible"/>). False when the
    /// viewport cannot carry a band (not a vertical virtual list, or the range is out of its model).</summary>
    public bool BeginRevealBand(NodeHandle viewport, int slot, int first, int count, bool opening)
    {
        if ((uint)slot >= RevealBands.Capacity || viewport.IsNull || !_scene.IsLive(viewport)
            || !_scene.TryGetScroll(viewport, out var sc) || sc.Orientation != 0 || sc.Layout is null
            || sc.ContentNode.IsNull || !_scene.IsLive(sc.ContentNode) || first < 0 || count <= 0 || first + count > sc.ItemCount)
            return false;
        float cross = MathF.Max(1f, _scene.Bounds(viewport).W);
        RectF a = sc.Layout.ItemRect(first, cross), z = sc.Layout.ItemRect(first + count - 1, cross);
        float top = a.Y, extent = z.Bottom - a.Y;
        if (!float.IsFinite(top) || !float.IsFinite(extent) || extent <= 0f) return false;

        AnimChannel ch = RevealBandChannel(slot);
        int ex = Find(viewport, ch);
        float cur = ex >= 0 ? _slab.At(ex).Position
            : _scene.TryGetRevealBand(viewport, slot, out var old) && float.IsFinite(old.Presented) ? old.Presented
            : opening ? 0f : extent;
        float vel = ex >= 0 ? _slab.At(ex).Velocity : 0f;
        float p1 = opening ? extent : 0f;
        RectF view = _scene.AbsoluteRect(viewport);
        float regionTop = _scene.AbsoluteRect(sc.ContentNode).Y + top;
        float from = p1, to = p1;
        bool animate = !FluentGpu.Dsl.Motion.ReducedMotion
            && RevealPlan.TryClamp(cur, p1, regionTop, view.Y, view.Y + sc.ViewportH, out from, out to);
        if (!animate)
        {
            if (ex >= 0) Cancel(viewport, ch);
            if (opening) _scene.ClearRevealBand(viewport, slot);
            else _scene.SetRevealBand(viewport, slot, first, count, top, extent, opening: false, presented: 0f);
            // Wholly ABOVE the view: the snap moves every row below by (p1 − cur). Shift the scroll frame by the same
            // amount so the rows the user reads stay put. A collapse's later commit removes rows already presented at 0
            // (a zero-delta handoff), so it adds no second shift.
            if (RevealPlan.IsAboveView(cur, p1, regionTop, view.Y) && MathF.Abs(p1 - cur) >= 0.5f)
                _scene.ScrollHandleFor(viewport)?.ShiftFrame(p1 - cur);
            QueueSettled(viewport, ch);
            return true;
        }
        // The span the rows slide by: a fresh arm's larger clamp end; a reverse keeps the live band's span (see VisibleSpan).
        float prior = ex >= 0 && _scene.TryGetRevealBand(viewport, slot, out var live) ? live.Visible : float.NaN;
        float visible = RevealPlan.VisibleSpan(prior, from, to, extent);
        if (!_scene.SetRevealBand(viewport, slot, first, count, top, extent, opening, ex >= 0 ? cur : from, visible)) return false;
        var spring = MotionTok.Reveal.Spring;
        if (ex >= 0 && MathF.Abs(from - cur) < 0.5f) Spring(viewport, ch, to, spring);   // reverse: live value + velocity
        else
        {
            if (ex >= 0) Cancel(viewport, ch);
            Spring(viewport, ch, to, spring, initial: from, initialVelocity: vel);
        }
        return true;
    }

    /// <summary>Move a live band to the rows it covers now (the owner's model moved under it).</summary>
    public void SetRevealBandRange(NodeHandle viewport, int slot, int first, int count)
        => _scene.SetRevealBandRange(viewport, slot, first, count);

    /// <summary>Release a band (its row, if any, too). Repeated clears are harmless.</summary>
    public void ClearRevealBand(NodeHandle viewport, int slot)
    {
        if ((uint)slot >= RevealBands.Capacity || viewport.IsNull) return;
        Cancel(viewport, RevealBandChannel(slot));
        _scene.ClearRevealBand(viewport, slot);
    }

    /// <summary>A closing band is at rest and its owner is about to remove its rows (ItemsViewController.BandSettled, frame
    /// start, right BEFORE the collapse commit). The band stops presenting the flush its rows leave the model
    /// (<see cref="RevealBand.Presents"/>), so the commit frame's 6.3 pass and scroll sync never subtract a phantom band.
    /// Its row (if a stray one is left) goes too.</summary>
    public void CommitRevealBand(NodeHandle viewport, int slot)
    {
        if ((uint)slot >= RevealBands.Capacity || viewport.IsNull) return;
        Cancel(viewport, RevealBandChannel(slot));
        _scene.CommitRevealBand(viewport, slot);
    }

    /// <summary>A committed band's rows are gone (the owner's render saw it): it stops presenting before the layout pass, even
    /// at an unchanged item count (<see cref="SceneStore.RetireRevealBand"/>). Its row (if a stray one is left) goes too.</summary>
    public void RetireRevealBand(NodeHandle viewport, int slot)
    {
        if ((uint)slot >= RevealBands.Capacity || viewport.IsNull) return;
        Cancel(viewport, RevealBandChannel(slot));
        _scene.RetireRevealBand(viewport, slot);
    }

    // A viewport's bands, folded into its content's flow: the laid-out geometry refreshed from the layout (rows above may
    // have opened or closed, realized rows report measured extents), Σ(presented − extent) onto the content, the overscan.
    // A committed band whose rows already left the model contributes nothing: this is the zero-delta handoff, made at 6.3
    // of the commit frame (before the scroll sync), not at ItemsView's 6.5 clear.
    private void AddBands(NodeHandle viewport)
    {
        if (!_scene.IsLive(viewport) || !_scene.TryGetScroll(viewport, out var sc) || sc.BandMask == 0
            || sc.ContentNode.IsNull || !_scene.IsLive(sc.ContentNode)) return;
        float cross = MathF.Max(1f, _scene.Bounds(viewport).W);
        float delta = 0f, overscan = 0f;
        for (int slot = 0; slot < RevealBands.Capacity; slot++)
        {
            if ((sc.BandMask & (1 << slot)) == 0) continue;
            RevealBand b = sc.Bands.Get(slot);
            if (!b.Presents(sc.ItemCount)) continue;
            if (sc.Layout is { } layout && b.Count > 0 && b.First + b.Count <= sc.ItemCount)
            {
                RectF a = layout.ItemRect(b.First, cross), z = layout.ItemRect(b.First + b.Count - 1, cross);
                float top = a.Y, extent = z.Bottom - a.Y;
                if (float.IsFinite(top) && float.IsFinite(extent) && extent > 0f)
                {
                    _scene.SetRevealBandGeometry(viewport, slot, top, extent);
                    b.Top = top;
                    b.Extent = extent;
                }
            }
            float p = float.IsNaN(b.Presented) ? b.Extent : Math.Clamp(b.Presented, 0f, b.Extent);
            delta += p - b.Extent;
            int s = Find(viewport, RevealBandChannel(slot));
            float target = s >= 0 ? _slab.At(s).To : p;
            overscan += MathF.Max(0f, b.Extent - MathF.Min(p, target));
        }
        ref NodePaint cp = ref TouchFlow(sc.ContentNode);
        cp.FlowDelta += delta;
        cp.FlowBits |= NodePaint.FlowBoundaryBit;
        AddOverscan(viewport, overscan);
    }

    // Undo the last pass: every column it wrote goes back to rest (the new pass rewrites what still reveals).
    private void ResetFlow()
    {
        for (int i = 0; i < _flowTouched.Count; i++)
        {
            NodeHandle n = _flowTouched[i];
            if (!_scene.IsLive(n)) continue;
            ref NodePaint p = ref _scene.Paint(n);
            if ((p.FlowBits & NodePaint.FlowOwnsHBit) != 0) p.PresentedH = float.NaN;
            if ((p.FlowBits & NodePaint.FlowOwnsShiftBit) != 0) p.ChildShiftY = 0f;
            p.FlowDelta = 0f;
            p.FlowOrphanTop = 0f;
            p.FlowOrphanDelta = 0f;
            p.FlowBits = 0;
            _scene.Mark(n, NodeFlags.PaintDirty);
        }
        _flowTouched.Clear();
    }

    // Every live, unparked RevealExtent row, ordered DEEPEST first: an inner reveal's delta must reach the reveal that
    // contains it before that one combines it with its own presented extent (AddReveal). Insertion sort into a pre-sized
    // list — a handful of rows, no allocation.
    private void CollectReveals()
    {
        _revealOrder.Clear();
        for (int nodeIndex = _slab.FirstActiveNode; nodeIndex >= 0; nodeIndex = _slab.NextActiveNode(nodeIndex))
            for (int s = _slab.HeadOnNode(nodeIndex); s >= 0; s = _slab.At(s).NextOnNode)
            {
                ref AnimValue r = ref _slab.At(s);
                if (r.Channel != AnimChannel.RevealExtent || r.Has(AnimFlags.Parked) || !_scene.IsLive(r.Node)) continue;
                var e = new RevealEntry(r.Node, DepthOf(r.Node), r.Position, r.To);
                int j = _revealOrder.Count;
                _revealOrder.Add(e);
                while (j > 0 && _revealOrder[j - 1].Depth < e.Depth) { _revealOrder[j] = _revealOrder[j - 1]; j--; }
                _revealOrder[j] = e;
            }
    }

    // Tree depth (an exit orphan counts through its former visual parent).
    private int DepthOf(NodeHandle n)
    {
        NodeHandle p = _scene.Parent(n);
        if ((_scene.Flags(n) & NodeFlags.Exiting) != 0 && _scene.TryGetOrphanVisualParent(n, out var vp) && !vp.IsNull) p = vp;
        int d = 1;
        for (; !p.IsNull; p = _scene.Parent(p)) d++;
        return d;
    }

    // One live reveal (deepest first). It presents its own spring. Only when an inner reveal's delta stopped here this pass
    // (FlowInnerBit; those deltas already sit in its FlowDelta) is that bounded by what its CONTENT presents: the children's
    // laid-out bottom (ContentExtent) plus the inner deltas. An outer clip then never shows more than its inner flow, and an
    // inner reveal never pushes past the outer clip: nested reveals COMBINE, never sum.
    // The bound is deliberately NOT the laid-out height: a close lays out at its FINAL height (a clip at Height 0, a Resize
    // that shrank), so min(P, H + inner) would present 0 from the seed frame on and snap every close shut. And with no
    // inner reveal there is no bound at all: a Resize whose content shrank outright (the Logs line unwrapping, a slot
    // shrinking) closes over the space it still presents, exactly as it opened. Its delta (presented − laid-out; an exit
    // orphan holds no layout space) then climbs the ancestor chain to the nearest flow boundary, or to a containing reveal,
    // which combines it in its own turn.
    private void AddReveal(NodeHandle node, float presented, float target)
    {
        bool orphan = (_scene.Flags(node) & NodeFlags.Exiting) != 0;
        float layoutExtent = orphan ? 0f : _scene.Bounds(node).H;
        ref NodePaint np = ref TouchFlow(node);
        bool bounded = !orphan && (np.FlowBits & NodePaint.FlowInnerBit) != 0;
        float shown = bounded ? MathF.Min(presented, ContentExtent(node) + np.FlowDelta) : presented;
        float d = shown - layoutExtent;
        np.FlowDelta = d;
        np.FlowBits |= NodePaint.FlowRevealBit;
        // The rows a reveal pulls up into view must stay realized for its whole flight. min(shown, To) is monotone over a
        // flight, so this only shrinks after the seed frame: one realize, never a per-frame one.
        float overscan = MathF.Max(0f, layoutExtent - MathF.Min(shown, target));
        NodeHandle child = node, parent;
        if (orphan)
        {
            if (!_scene.TryGetOrphanVisualParent(node, out parent) || parent.IsNull || !_scene.IsLive(parent)) return;
            if (IsColumnFlow(parent))
            {
                ref NodePaint vp = ref TouchFlow(parent);
                float top = _scene.Bounds(node).Y;
                if ((vp.FlowBits & NodePaint.FlowOrphanBit) == 0 || top < vp.FlowOrphanTop) vp.FlowOrphanTop = top;
                vp.FlowOrphanDelta += d;
                vp.FlowBits |= NodePaint.FlowOrphanBit | NodePaint.FlowShiftsBit;
            }
        }
        else parent = _scene.Parent(node);
        bool viaOrphan = orphan;
        while (!parent.IsNull && _scene.IsLive(parent))
        {
            float c = FlowContribution(parent, child, d, viaOrphan);
            ref NodePaint pp = ref TouchFlow(parent);
            pp.FlowDelta += c;
            if (!viaOrphan && IsColumnFlow(parent)) pp.FlowBits |= NodePaint.FlowShiftsBit;
            if (IsLiveReveal(parent))
            {
                pp.FlowBits |= NodePaint.FlowInnerBit;   // a containing reveal: shallower, so it runs later and combines this delta
                return;
            }
            if (IsVerticalScrollContent(parent, out NodeHandle viewport))
            {
                pp.FlowBits |= NodePaint.FlowBoundaryBit;
                AddOverscan(viewport, overscan);
                return;
            }
            if (IsFlowBoundary(parent)) { pp.FlowBits |= NodePaint.FlowBoundaryBit; return; }
            if (MathF.Abs(c) < 1e-3f) return;
            child = parent;
            d = c;
            viaOrphan = false;
            parent = _scene.Parent(parent);
        }
    }

    // A live, unparked reveal row on the node.
    private bool IsLiveReveal(NodeHandle n)
    {
        if (!_slab.NodeHasRows((int)n.Raw.Index)) return false;
        int s = Find(n, AnimChannel.RevealExtent);
        return s >= 0 && !_slab.At(s).Has(AnimFlags.Parked);
    }

    // How far a child's presented-extent change moves its PARENT's presented bottom: all of it in a column (children
    // stack); in a row / z-stack / grid only the part that reaches past the tallest other child.
    private float FlowContribution(NodeHandle parent, NodeHandle child, float d, bool childIsOrphan)
    {
        if (IsColumnFlow(parent)) return d;
        ref readonly RectF cb = ref _scene.Bounds(child);
        float childBottom = cb.Y + (childIsOrphan ? 0f : cb.H);
        float others = 0f;
        for (var s = _scene.FirstChild(parent); !s.IsNull; s = _scene.NextSibling(s))
        {
            if (s == child) continue;
            ref readonly RectF sb = ref _scene.Bounds(s);
            others = MathF.Max(others, sb.Y + sb.H);
        }
        return MathF.Max(childBottom + d, others) - MathF.Max(childBottom, others);
    }

    private bool IsColumnFlow(NodeHandle n)
        => (_scene.Flags(n) & NodeFlags.ZStack) == 0 && !_scene.HasGrid(n)
           && (_scene.Layout(n).Direction == 1 || IsVerticalScrollContent(n, out _));

    private bool IsVerticalScrollContent(NodeHandle n, out NodeHandle viewport)
    {
        viewport = _scene.Parent(n);
        return !viewport.IsNull && (_scene.Flags(viewport) & NodeFlags.Scrollable) != 0
            && _scene.TryGetScroll(viewport, out var sc) && sc.ContentNode == n && sc.Orientation == 0;
    }

    // Where a presented-extent change stops climbing (the shift lives INSIDE these, their own size does not change): the
    // root, a scroll viewport, a box with a DECLARED height, a Grow-filled child of a column whose height is DEFINITE, and
    // a node whose height another size row owns (SizeMode.Reveal / Relayout = SizeH, Reflow = LayoutH). A Grow child of a
    // content-sized column (a scroll content, an auto column) is NOT a boundary: there the free space is 0 and the child's
    // height follows its content, so a reveal inside it must reach the scroll content (presented extent, scrollbar,
    // overscan).
    private bool IsFlowBoundary(NodeHandle n)
    {
        if (n == _scene.Root || (_scene.Flags(n) & NodeFlags.Scrollable) != 0) return true;
        ref readonly LayoutInput li = ref _scene.Layout(n);
        if (!float.IsNaN(li.Height)) return true;
        if (li.FlexGrow > 0f)
        {
            NodeHandle p = _scene.Parent(n);
            if (!p.IsNull && _scene.Layout(p).Direction == 1 && IsDefiniteHeight(p)) return true;
        }
        if (!_slab.NodeHasRows((int)n.Raw.Index)) return false;
        return Find(n, AnimChannel.SizeH) >= 0 || Find(n, AnimChannel.LayoutH) >= 0;
    }

    // Does this node's height NOT follow its content: the root, a scroll viewport, a declared height, or a Grow child of a
    // definite column (recursively). A vertical scroll content (unbounded main axis) and an auto column are not definite.
    private bool IsDefiniteHeight(NodeHandle n)
    {
        for (int guard = 0; guard < 64 && !n.IsNull; guard++)
        {
            if (n == _scene.Root || (_scene.Flags(n) & NodeFlags.Scrollable) != 0) return true;
            if (IsVerticalScrollContent(n, out _)) return false;
            ref readonly LayoutInput li = ref _scene.Layout(n);
            if (!float.IsNaN(li.Height)) return true;
            NodeHandle p = _scene.Parent(n);
            if (li.FlexGrow <= 0f || p.IsNull || _scene.Layout(p).Direction != 1) return false;
            n = p;
        }
        return false;
    }

    private ref NodePaint TouchFlow(NodeHandle n)
    {
        bool seen = false;
        for (int i = 0; i < _flowTouched.Count; i++)
            if (_flowTouched[i] == n) { seen = true; break; }
        if (!seen) _flowTouched.Add(n);
        return ref _scene.Paint(n);
    }

    private void AddOverscan(NodeHandle viewport, float amount)
    {
        for (int i = 0; i < _ovViewports.Count; i++)
            if (_ovViewports[i] == viewport) { _ovAmount[i] += amount; return; }
        _ovViewports.Add(viewport);
        _ovAmount.Add(amount);
    }

    // Turn the accumulated deltas into what the recorder draws: the presented height of every revealing node and every
    // non-boundary ancestor, the Parallax child shift, the viewports' realize overscan. A boundary keeps its laid-out size
    // and reports FlowDelta 0 to its own parent's cursor. The exception is a scroll content: it keeps the sum, which is
    // the presented content extent the scroll plan and the scrollbar measure against.
    private void FinishFlow()
    {
        for (int i = 0; i < _flowTouched.Count; i++)
        {
            NodeHandle n = _flowTouched[i];
            ref NodePaint p = ref _scene.Paint(n);
            bool self = (p.FlowBits & NodePaint.FlowRevealBit) != 0;
            if (!self && (p.FlowBits & NodePaint.FlowBoundaryBit) != 0)
            {
                if (!IsVerticalScrollContent(n, out _)) p.FlowDelta = 0f;
                _scene.Mark(n, NodeFlags.PaintDirty);
                continue;
            }
            bool orphan = (_scene.Flags(n) & NodeFlags.Exiting) != 0;
            float h = orphan ? 0f : _scene.Bounds(n).H;
            float delta = p.FlowDelta;
            // Never steal a PresentedH another writer owns (a SizeMode.Reveal row, a scroll-effect collapse).
            if (MathF.Abs(delta) >= 1e-3f && float.IsNaN(p.PresentedH))
            {
                p.PresentedH = MathF.Max(0f, h + delta);
                p.FlowBits |= NodePaint.FlowOwnsHBit;
            }
            if (self && TryGetTransition(n, out var spec) && spec.Anchor == SizeAnchor.Parallax)
            {
                p.ChildShiftY = RevealPlan.ParallaxShift(h + delta, ContentExtent(n));
                p.FlowBits |= NodePaint.FlowOwnsShiftBit;
            }
            _scene.Mark(n, NodeFlags.PaintDirty);
        }
        FinishOverscan();
    }

    private float ContentExtent(NodeHandle n)
    {
        float e = 0f;
        for (var c = _scene.FirstChild(n); !c.IsNull; c = _scene.NextSibling(c))
        {
            ref readonly RectF cb = ref _scene.Bounds(c);
            e = MathF.Max(e, cb.Y + cb.H);
        }
        return e > 0f ? e + _scene.Layout(n).Padding.Bottom : _scene.Bounds(n).H;
    }

    // Publish each viewport's realize overscan; a GROWN one that no longer covers the window marks the viewport for a
    // re-realize (the host runs it right after this pass, once). Viewports no reveal reached this pass go back to 0.
    private void FinishOverscan()
    {
        for (int i = 0; i < _ovViewports.Count; i++)
        {
            NodeHandle v = _ovViewports[i];
            if (!_scene.IsLive(v) || !_scene.TryGetScroll(v, out ScrollState sc)) continue;
            float amount = _ovAmount[i];
            float prev = 0f;
            for (int j = 0; j < _ovPrevViewports.Count; j++)
                if (_ovPrevViewports[j] == v) { prev = _ovPrevAmount[j]; break; }
            if (sc.RevealOverscan != amount) _scene.ScrollRef(v).RevealOverscan = amount;
            if (amount > prev + 0.5f && sc.ItemCount > 0 && sc.Extent is { } ext)
            {
                var feel = ScrollTunables.Current;
                var rw = Virtualizer.Plan(ext, sc.Offset, sc.Velocity, sc.ViewportMain + amount, in feel, sc.AnchorIndex);
                if (ScrollContentPose.NeedsRealize(in sc, in rw))
                {
                    _scene.Mark(v, NodeFlags.VirtualRangeDirty);
                    _overscanGrew = true;
                }
            }
        }
        for (int j = 0; j < _ovPrevViewports.Count; j++)
        {
            NodeHandle v = _ovPrevViewports[j];
            if (_ovViewports.Contains(v) || !_scene.IsLive(v) || !_scene.TryGetScroll(v, out ScrollState old)) continue;
            if (old.RevealOverscan != 0f) _scene.ScrollRef(v).RevealOverscan = 0f;
        }
        (_ovPrevViewports, _ovViewports) = (_ovViewports, _ovPrevViewports);
        (_ovPrevAmount, _ovAmount) = (_ovAmount, _ovPrevAmount);
        _ovViewports.Clear();
        _ovAmount.Clear();
    }
}
