using System;
using System.Collections.Generic;
using FluentGpu.Foundation;
using FluentGpu.Scene;

namespace FluentGpu.Animation;

// ─────────────────────────────────────────────────────────────────────────────────────────────────────────────
//  ANIMATION REWORK — switch-over Step A (final): SizeMode.Reflow / Relayout parity + host worklists.
//
//  The intricate parity: a reflow track WRITES the layout size each tick, so the host's projection diff would see
//  "old ≠ new" again every frame — two guards (target/echo) kill that feedback loop. The host drains the worklists
//  after the tick (RunReflowLayout / incremental re-solve) and refreshes Trailing child-shifts. Ported from AnimEngine
//  (ReflowSize / SeedEnterReflow / SeedReflowResize / the settle-restore) onto the slab. This is the last piece that
//  makes AnimScheduler a full AnimEngine drop-in. (Behavioral fidelity here wants the gallery/gates to verify.)
//
//  A reflow row is NOT seed-once-and-forget. Two things about it go stale mid-flight, and both have a ground-truth
//  owner outside this file, so both get an explicit re-entry point rather than a guess:
//    • the TARGET — a node whose children mount asynchronously (a shelf that fills in, a lyrics pane that measures)
//      has a natural size that only becomes true several frames after the row was seeded. A row flagged
//      AnimFlags.NaturalTarget consents to being retargeted from the host's solved child extent every reflow tick
//      (CollectLiveReflowNodes → TryGetLiveReflow → RetargetReflow), so it flies to the size the content actually
//      wants instead of an early guess that SettleRestore then snaps away.
//    • the RESTORE value — RestoreTo is the author's DECLARED LayoutInput, replayed at settle. ReflowSize can only
//      snapshot it on the row's creation (re-reading _scene.Layout mid-flight reads the row's OWN interp), so a
//      re-declaration while the row is in flight has no way in. RecordDeclaredSize is that way in: the reconciler
//      hands the declared value over instead of writing LayoutInput behind the row's back. Without it an interrupted
//      open→close restored the OLD row's RestoreTo (NaN) and flashed a full-height frame before layout caught up.
//  Both paths reseed through Animate/Spring, which REWRITE AnimFlags on the slot — so every reflow-owned bit is
//  re-applied through the ONE shared tail (FinishReflowRow); seed and retarget cannot drift.
// ─────────────────────────────────────────────────────────────────────────────────────────────────────────────

public sealed partial class AnimEngine
{
    /// <summary>Nodes whose REFLOW (LayoutW/H) size advanced this tick — the host re-solves their boundary scope.</summary>
    public List<NodeHandle> ReflowRoots { get; } = new();
    /// <summary>Nodes whose presented SIZE changed under SizeMode.Relayout — the host re-solves those subtrees (live re-wrap).</summary>
    public List<NodeHandle> IncrementalRoots { get; } = new();
    /// <summary>Nodes that mounted this frame with a SizeMode.Reflow enter — the host seeds their reveal reflow after layout.</summary>
    public List<NodeHandle> PendingEnterReflow { get; } = new();
    /// <summary>Containers whose SizeMode.Reflow child orphaned this frame — the host eases them to the without-child size.</summary>
    public List<(NodeHandle node, float fromW, float fromH, LayoutTransition spec)> PendingExitReflow { get; } = new();

    /// <summary>Absolute (world/DIP) rects a structural-track CANCEL vacated this frame — the LAST-PRESENTED extent of a
    /// node whose in-flight FLIP/Reveal was snapped away (SnapStructuralToLayout / CancelStructuralAll). A cancel resets
    /// LocalTransform→identity and PresentedW/H→NaN, so the node next draws at its FINAL bounds only; the band it vacated
    /// (last-presented rect minus final bounds) is never re-touched by any node, so the region-aware acrylic/backdrop
    /// damage cache would keep last frame's pixels there — a persistent dark "ghost rail" beside the content card. The
    /// recorder unions these into the frame damage (SceneRecorder.Record) so the vacated band repaints, then the host
    /// drains this list. Pre-sized + drained per recorded frame ⇒ no steady-state alloc; a cancel is rare (drag/resize)
    /// so even the Add is off the zero-alloc hot phases (6–13).</summary>
    public List<RectF> PendingStructuralDamage { get; } = new(64);

    private bool _reflowWrote;
    /// <summary>True if any reflow track wrote LayoutInput this tick (advance or settle restore) — the host runs a
    /// boundary-scoped re-solve before record. Self-clearing.</summary>
    public bool ConsumeReflowWrites() { bool w = _reflowWrote; _reflowWrote = false; return w; }

    /// <summary>A node mounted with a SizeMode.Reflow enter eases its MAIN-axis LAYOUT size 0 → its solved size so
    /// neighbours reflow as it reveals. Host-called after layout (the natural size isn't known pre-layout).
    /// <para>The seeded row is flagged <see cref="AnimFlags.NaturalTarget"/>: an ENTER reflow targets the node's
    /// natural (auto) size BY DEFINITION, and at mount that size is whatever happened to be solved on the very first
    /// frame — before an async child (an image that decodes, a list that hydrates, a text run that measures) has
    /// contributed anything. The flag is the row's consent to be retargeted from the solved child extent each tick;
    /// without it the reveal eases to the empty-shell height and SettleRestore snaps the remainder in one frame.</para></summary>
    public void SeedEnterReflow(NodeHandle node, bool horizontal, float toW, float toH)
    {
        if (!TryGetTransition(node, out var spec)) return;
        AnimChannel ch;
        if (horizontal) { if (toW <= 0.5f) return; ch = AnimChannel.LayoutW; ReflowSize(node, ch, 0f, toW, spec); }
        else { if (toH <= 0.5f) return; ch = AnimChannel.LayoutH; ReflowSize(node, ch, 0f, toH, spec); }
        // NOT set by SeedReflowResize (a container's EXIT reflow targets a solved WITHOUT-child size) and never by a
        // declared-height reflow row: a still-painted exit orphan retargeted from its child extent would fly back OPEN.
        int s = Find(node, ch);
        if (s >= 0) _slab.At(s).Flags |= AnimFlags.NaturalTarget;
    }

    /// <summary>Ease a node's MAIN-axis LAYOUT size from → to so its parent re-solves and SIBLINGS reflow (the
    /// smooth-exit lever for an orphaning Reflow child's container). Shrink picks ExitDynamics.</summary>
    public void SeedReflowResize(NodeHandle node, bool horizontal, float from, float to, in LayoutTransition spec)
    {
        if (horizontal) ReflowSize(node, AnimChannel.LayoutW, from, to, spec);
        else ReflowSize(node, AnimChannel.LayoutH, from, to, spec);
    }

    // SizeMode.Reflow seed/retarget with the two feedback guards (the track writes the layout size, so each commit's
    // projection diff re-sees "old ≠ new" — only a genuinely new destination passes both guards). Ported from AnimEngine.
    internal void ReflowSize(NodeHandle node, AnimChannel ch, float from, float to, in LayoutTransition spec)
    {
        TransitionDynamics dyn = Normalize(to < from && spec.ExitDynamics is { } ed ? ed : spec.Dynamics);
        int ex = Find(node, ch);
        if (dyn.Kind == DynamicsKind.Tween && dyn.DurationMs <= 1f && spec.DelayMs <= 0f)
        {
            if (ex >= 0) Cancel(node, ch);
            if (_scene.IsLive(node))
            {
                ref NodePaint p = ref _scene.Paint(node);
                p.ChildShiftX = 0f; p.ChildShiftY = 0f;
                _scene.Mark(node, NodeFlags.PaintDirty);
            }
            return;
        }
        if (ex >= 0)
        {
            float exTo = _slab.At(ex).To;          // target guard: same destination → keep flying
            float exPos = _slab.At(ex).Position;   // echo guard: layout still holds our own interp → keep flying
            if (MathF.Abs(exTo - to) < 0.5f) return;
            if (MathF.Abs(exPos - to) < 0.5f) return;
            from = exPos;                          // genuine retarget — depart from the current interp
        }
        else if (MathF.Abs(from - to) < 0.5f) return;

        // The DECLARED value must be captured ONCE, on the row's creation. `_scene.Layout(node)` is the very column
        // this track writes each tick, so re-reading it on a genuine RETARGET (the two guards above protect the track,
        // not this snapshot) hands back a MID-ANIMATION number — which SettleRestore would then write back as the
        // node's permanent declared size. Carry the existing row's RestoreTo forward instead. (A re-DECLARATION while
        // the row flies does not come through here at all — it arrives via RecordDeclaredSize, whose whole job is to
        // replace this stashed value with ground truth rather than let it rot for the life of the row.)
        bool hadRow = ex >= 0;
        AnimFlags carried = hadRow ? _slab.At(ex).Flags & ReflowOwnedFlags : AnimFlags.None;
        float declared = hadRow
            ? _slab.At(ex).RestoreTo
            : (ch == AnimChannel.LayoutW ? _scene.Layout(node).Width : _scene.Layout(node).Height);
        if (dyn.Kind == DynamicsKind.Spring)
            Spring(node, ch, to, SpringParams.FromResponse(dyn.Response, dyn.DampingRatio), initial: from, delayMs: spec.DelayMs);
        else
            Animate(node, ch, from, to, dyn.DurationMs, dyn.Easing, delayMs: spec.DelayMs);

        FinishReflowRow(node, ch, declared, in spec, hadRow, carried);
    }

    /// <summary>The reflow-OWNED flag bits — the ones <see cref="Animate"/>/<see cref="Spring"/> would silently drop
    /// when they rewrite <see cref="AnimValue.Flags"/> on a retargeted slot, so every reseed must carry them. All three
    /// describe the ROW's relationship to the node (who owns the clip, which edge the content rides, whether the target
    /// is the natural size), none of them the trajectory — a new destination never changes any of those facts, so
    /// carrying them is the rule and dropping one is always a bug.</summary>
    private const AnimFlags ReflowOwnedFlags = AnimFlags.ClipAdded | AnimFlags.TrailingAnchor | AnimFlags.NaturalTarget;

    /// <summary>The post-seed tail shared by <see cref="ReflowSize"/> and <see cref="RetargetReflow"/>: re-stash the
    /// declared value, re-apply the carried reflow-owned bits, and take clip ownership on a FRESH row. Both seed paths
    /// route through here so they cannot drift — the bug this closes is a retarget that keeps flying but silently
    /// loses its <see cref="AnimFlags.ClipAdded"/> ownership (the clip then leaks onto the node forever) or its
    /// <see cref="AnimFlags.TrailingAnchor"/> (the Expander content stops riding the animated edge mid-motion).
    /// Returns the row's slot (or -1), so a caller with an extra bit to restore needn't re-<c>Find</c> it.</summary>
    private int FinishReflowRow(NodeHandle node, AnimChannel ch, float declared, in LayoutTransition spec,
                                bool hadRow, AnimFlags carried)
    {
        int s = Find(node, ch);
        if (s < 0) return -1;
        ref AnimValue r = ref _slab.At(s);
        r.RestoreTo = declared;
        r.Flags |= carried & ReflowOwnedFlags;   // survives a reseed that rewrote Flags
        if (spec.Anchor == SizeAnchor.Trailing) r.Flags |= AnimFlags.TrailingAnchor;
        // The code that writes LayoutInput owns the clip. A reflow drives the node's LAYOUT size while its CONTENT
        // is still arranged at the natural height, so an unclipped node paints over whatever follows it (a Skel
        // region easing 0→H is the pathological case: it starts at ZERO and covers the whole sibling below). Add
        // the clip for the life of the track and remember that WE added it — a node that declared ClipToBounds
        // itself, or a ScrollEl / VirtualListEl viewport, must never be un-clipped when the row is freed.
        if (!r.Has(AnimFlags.ClipAdded) && !hadRow && _scene.IsLive(node) && (_scene.Flags(node) & NodeFlags.ClipsToBounds) == 0)
        {
            _scene.Mark(node, NodeFlags.ClipsToBounds);
            _scene.Mark(node, NodeFlags.PaintDirty);
            r.Flags |= AnimFlags.ClipAdded;
        }
        return s;
    }

    /// <summary>Re-aim a LIVE reflow row at a NEW ground-truth target, departing from where the interp currently is
    /// (velocity-continuous on the spring path). No-op when no row is in flight, or when the target hasn't actually
    /// moved.
    /// <para>Deliberately carries the TARGET guard but NOT <see cref="ReflowSize"/>'s echo guard. The echo guard exists
    /// to kill the projection feedback loop — a reflow row writes LayoutInput, so the host's next bounds diff re-sees
    /// "old ≠ new" and would re-seed the row against its own interp forever. Callers of THIS method do not close that
    /// loop: they pass ground truth that is independent of the row's output (the host's solved child extent measured
    /// WITHOUT the animated constraint, or the author's freshly declared value). Applying the echo guard here would
    /// swallow exactly the legitimate retargets this exists for — every one of them arrives while <c>Position</c> is
    /// somewhere in the neighbourhood of the new target, which is precisely what that guard rejects.</para>
    /// <para>The dynamics come from the node's stashed <see cref="LayoutTransition"/> (the same side-table
    /// <see cref="SeedEnterReflow"/> reads), so a retarget obeys the author's spring/tween exactly as the seed did —
    /// including the shrink→<c>ExitDynamics</c> pick. A node with no stashed spec falls back to the engine defaults via
    /// <see cref="Normalize"/> rather than snapping. The reseed rewrites <c>Flags</c>, so the reflow-owned bits are
    /// restored through <see cref="FinishReflowRow"/> plus an explicit <see cref="AnimFlags.NaturalTarget"/> carry —
    /// a retarget must never demote a natural-target row into a fixed one (the very next tick would stop tracking).</para></summary>
    internal void RetargetReflow(NodeHandle node, AnimChannel ch, float to)
    {
        int ex = Find(node, ch);
        if (ex < 0) return;
        AnimValue cur = _slab.At(ex);          // copy: Animate/Spring may grow the slab's backing array
        if (MathF.Abs(cur.To - to) < 0.5f) return;   // target guard ONLY — see the WHY above

        float from = cur.Position;             // depart from the current interp, not from a recomputed endpoint
        float declared = cur.RestoreTo;
        AnimFlags carried = cur.Flags & ReflowOwnedFlags;
        bool natural = (cur.Flags & AnimFlags.NaturalTarget) != 0;

        if (!TryGetTransition(node, out LayoutTransition spec)) spec = default;
        TransitionDynamics dyn = Normalize(to < from && spec.ExitDynamics is { } ed ? ed : spec.Dynamics);
        // No delay on a retarget: the spec's DelayMs is the ENTRY stagger and has already been served by the seed.
        if (dyn.Kind == DynamicsKind.Spring)
            Spring(node, ch, to, SpringParams.FromResponse(dyn.Response, dyn.DampingRatio), initial: from);
        else
            Animate(node, ch, from, to, dyn.DurationMs, dyn.Easing);

        int s = FinishReflowRow(node, ch, declared, in spec, hadRow: true, carried);
        if (s >= 0 && natural) _slab.At(s).Flags |= AnimFlags.NaturalTarget;
    }

    /// <summary>The reconciler's hand-off for a node whose main-axis size is owned by a LIVE reflow row: record what
    /// the author DECLARED this reconcile instead of writing <c>LayoutInput</c> behind the row's back.
    /// <para>Returns <c>true</c> when a live reflow row owns <paramref name="ch"/> — meaning "declared value recorded,
    /// do NOT write LayoutInput" (writing it would be immediately overwritten by the next tick's compose anyway, and
    /// in between it publishes a one-frame jump). <c>false</c> means the channel is unowned and the caller writes
    /// normally; the probe is O(1) for the overwhelmingly common no-rows node.</para>
    /// <para>Two distinct fixes ride on this. (1) <c>RestoreTo</c> stops rotting: <see cref="ReflowSize"/> can only
    /// snapshot the declared value on the row's CREATION and then carries it verbatim across every retarget, so an
    /// interrupted open→close restored the OPEN row's stash — <c>NaN</c> (auto) — and the node flashed one full-height
    /// frame before layout re-solved. (2) A declared value is GROUND TRUTH, so it also re-aims the row: the author
    /// saying "this is 240 tall now" mid-flight must bend the animation, not queue a snap at settle. And because a
    /// declared value is by definition not the natural one, it clears <see cref="AnimFlags.NaturalTarget"/> — the host
    /// must stop overwriting the target from the solved child extent from here on.</para>
    /// <para>A declared <c>NaN</c> ("auto") therefore only ever PRESERVES <see cref="AnimFlags.NaturalTarget"/>; it may
    /// never GRANT it. Whether the host is allowed to re-aim a row from the solved child extent is a property of how the
    /// row was SEEDED, not of the author's declared value: <see cref="SeedEnterReflow"/> targets the node's natural size
    /// and sets the bit itself, while <see cref="SeedReflowResize"/> deliberately does NOT — a container's EXIT reflow
    /// targets its solved WITHOUT-child size, and its child extent still includes the STILL-PAINTED exit orphan (see
    /// <c>SceneStore.Orphan</c>: a Reflow exit keeps contributing its animating main-axis size to the visual parent's
    /// Measure). Granting the bit here handed exactly that row permission to chase the orphan, i.e. the "flies back
    /// OPEN" failure <see cref="SeedEnterReflow"/>'s own note forbids — and every author node re-renders during a close,
    /// so the grant fired on essentially every one. In a MEASURED virtual list the consequence is visible: the closing
    /// slot's pinned height climbs back toward the with-drawer extent while the drawer's content is already faded out,
    /// so the row reserves a band nothing paints in (and, mid-flight, paints content past a band that stopped
    /// growing).</para></summary>
    public bool RecordDeclaredSize(NodeHandle node, AnimChannel ch, float declared)
    {
        if (!_slab.NodeHasRows((int)node.Raw.Index)) return false;   // O(1) — the common case, per reconciled node
        int s = Find(node, ch);
        if (s < 0) return false;

        _slab.At(s).RestoreTo = declared;
        // NaN ("auto"): nothing to re-aim and nothing to clear — the seed owns NaturalTarget (see the note above).
        if (!float.IsNaN(declared) && MathF.Abs(declared - _slab.At(s).To) >= 0.5f)
        {
            RetargetReflow(node, ch, declared);
            int ns = Find(node, ch);                        // the reseed may have landed on a different slot
            if (ns >= 0) _slab.At(ns).Flags &= ~AnimFlags.NaturalTarget;
        }
        return true;
    }

    /// <summary>True while any live row on <paramref name="node"/> put <see cref="NodeFlags.ClipsToBounds"/> there
    /// itself (see <see cref="AnimFlags.ClipAdded"/>). The reconciler consults this before honouring an author's
    /// clip declaration diff: the node's ClipsToBounds bit is ENGINE state for the life of the row, so a reconcile
    /// that "restores" it from the element would hand the flag to the author and the row's teardown would then strip
    /// a clip the author actually wanted (or vice-versa). O(1) for a node with no rows.</summary>
    public bool HasEngineOwnedClip(NodeHandle node)
    {
        int idx = (int)node.Raw.Index;
        if (!_slab.NodeHasRows(idx)) return false;
        for (int s = _slab.HeadOnNode(idx); s >= 0; s = _slab.At(s).NextOnNode)
            if (_slab.At(s).Has(AnimFlags.ClipAdded)) return true;
        return false;
    }

    /// <summary>The author declared <c>ClipToBounds = true</c> on a node whose clip the engine currently owns
    /// (<see cref="AnimFlags.ClipAdded"/>): hand the flag over. Without this, the row's teardown sink would unmark
    /// a clip the author now wants — the mark itself is already there (the reconciler just wrote it), so ownership
    /// transfer is nothing but dropping the ClipAdded bit. O(1) for a node with no rows.</summary>
    public void AdoptEngineClip(NodeHandle node)
    {
        int idx = (int)node.Raw.Index;
        if (!_slab.NodeHasRows(idx)) return;
        for (int s = _slab.HeadOnNode(idx); s >= 0; s = _slab.At(s).NextOnNode)
        {
            ref AnimValue r = ref _slab.At(s);
            if (r.Has(AnimFlags.ClipAdded)) r.Flags &= ~AnimFlags.ClipAdded;
        }
    }

    /// <summary>Fill <paramref name="dst"/> with the nodes that currently own an advancing reflow (LayoutW/LayoutH)
    /// row — the host's per-tick worklist for re-measuring natural targets. Clears <paramref name="dst"/> first and
    /// adds each node ONCE even when both axes reflow. Walks the slab's active-node chain (O(active nodes), the same
    /// structure the tick passes use) and allocates nothing: the caller owns the list, so a pre-sized one keeps the
    /// whole path inside the phase 6–13 zero-alloc budget. Settled (Done, awaiting PASS3) and Parked rows are skipped
    /// — retargeting either would resurrect an animation the engine has already decided is over or quiesced.</summary>
    public void CollectLiveReflowNodes(List<NodeHandle> dst)
    {
        dst.Clear();
        for (int n = _slab.FirstActiveNode; n >= 0; n = _slab.NextActiveNode(n))
            for (int s = _slab.HeadOnNode(n); s >= 0; s = _slab.At(s).NextOnNode)
            {
                ref readonly AnimValue r = ref _slab.At(s);
                if (r.Channel is not (AnimChannel.LayoutW or AnimChannel.LayoutH)) continue;
                if (r.Has(AnimFlags.Done) || r.Has(AnimFlags.Parked)) continue;
                dst.Add(r.Node);
                break;   // one entry per node — the caller re-solves the node, not the axis
            }
    }

    /// <summary>Read a live reflow row's state without touching it: its current <paramref name="to"/> target, whether
    /// that target is the node's natural size (<see cref="AnimFlags.NaturalTarget"/> — i.e. whether the host is allowed
    /// to re-aim it), and the declared value <paramref name="restoreTo"/> queued for the settle restore. False (with
    /// NaN/false outs) when no row owns the channel. The read half of the
    /// <see cref="CollectLiveReflowNodes"/> → <see cref="RetargetReflow"/> loop.</summary>
    public bool TryGetLiveReflow(NodeHandle node, AnimChannel ch, out float to, out bool naturalTarget, out float restoreTo)
    {
        int idx = (int)node.Raw.Index;
        if (_slab.NodeHasRows(idx))
        {
            int s = Find(node, ch);
            if (s >= 0)
            {
                ref readonly AnimValue r = ref _slab.At(s);
                to = r.To;
                naturalTarget = r.Has(AnimFlags.NaturalTarget);
                restoreTo = r.RestoreTo;
                return true;
            }
        }
        to = float.NaN; naturalTarget = false; restoreTo = float.NaN;
        return false;
    }

    /// <summary>Drop a node's in-flight FLIP TRANSLATE rows and land it on the geometry layout just solved — the
    /// position-only sibling of <see cref="SnapStructuralToLayout"/>. Size/scale/opacity/blur rows keep running: a
    /// node that is itself reflowing, revealing or fading has not become stale merely because a neighbour shoved it.
    /// <para>This is the escape hatch for the node that keeps getting shoved. A reflowing drawer moves its siblings a
    /// few pixels EVERY tick, and each shove seeds another position FLIP; the sibling therefore chases a target that
    /// has already moved again and never converges, painting offset over the rows below it for the whole animation.
    /// When the host decides a node's displacement is host-driven rather than a discrete move worth animating, it
    /// snaps here instead of re-seeding.</para>
    /// <para>Gesture-owned Translate rows (a <c>WhileHover</c> Offset) are skipped for the same reason
    /// <see cref="SnapStructuralToLayout"/> skips them: those are the authored REST pose, not a FLIP leftover, and
    /// wiping them parks the node at the origin until the next hover edge. Only the TRANSLATION component of
    /// <c>LocalTransform</c> is zeroed — an authored/animated scale or rotation on the same node survives.</para></summary>
    public void SnapPositionToLayout(NodeHandle node)
    {
        int idx = (int)node.Raw.Index;
        if (!_slab.NodeHasRows(idx)) return;
        bool live = _scene.IsLive(node);
        bool freedAny = false;
        int s = _slab.HeadOnNode(idx);
        while (s >= 0)
        {
            int next = _slab.At(s).NextOnNode;   // read the link BEFORE FreeSlot unlinks the row
            AnimChannel ch = _slab.At(s).Channel;
            if ((ch is AnimChannel.TranslateX or AnimChannel.TranslateY) && !IsGestureOwnedTransform(idx, ch))
            {
                // Ghost-band damage, same contract as SnapStructuralToLayout: snapshot the last-PRESENTED rect while
                // the paint still holds the translated origin, so the band the node vacates repaints instead of
                // keeping last frame's pixels in the damage-driven acrylic/backdrop cache.
                if (live && !freedAny) CaptureLastPresentedDamage(node);
                FreeSlot(s);
                freedAny = true;
            }
            s = next;
        }
        if (!freedAny || !live) return;
        ref NodePaint p = ref _scene.Paint(node);
        Affine2D tf = p.LocalTransform;
        if (tf.Dx == 0f && tf.Dy == 0f) return;   // already at rest — don't dirty a node for nothing
        p.LocalTransform = tf with { Dx = 0f, Dy = 0f };
        _scene.Mark(node, NodeFlags.TransformDirty | NodeFlags.PaintDirty);
    }

    /// <summary>Take back the <see cref="NodeFlags.ClipsToBounds"/> a reflow row added (see
    /// <see cref="AnimFlags.ClipAdded"/>). Called from the ONE row-teardown sink (<c>FreeSlot</c>) plus
    /// <c>CancelAll</c>'s bulk clear, so no path — settle, Cancel, or a structural snap — can leak the clip.</summary>
    private void ReleaseReflowClip(NodeHandle node)
    {
        if (!_scene.IsLive(node)) return;
        _scene.Unmark(node, NodeFlags.ClipsToBounds);
        _scene.Mark(node, NodeFlags.PaintDirty);
    }

    /// <summary>Dirty the node whose Measure must see this reflow write. A live node's topological parent is that
    /// scope; an exit orphan's Parent is null (detached), so the former visual parent is the one that still measures
    /// it — marking the orphan itself would RunSubtree a rootless node and never update the virtual row's extent.</summary>
    private void MarkReflowLayoutDirty(NodeHandle node)
    {
        var rp = _scene.Parent(node);
        if (rp.IsNull && (_scene.Flags(node) & NodeFlags.Exiting) != 0
            && _scene.TryGetOrphanVisualParent(node, out var vp) && !vp.IsNull)
            rp = vp;
        _scene.Mark(rp.IsNull ? node : rp, NodeFlags.LayoutDirty);
    }

    /// <summary>The DECLARED (author-written) LayoutInput value a live SizeMode.Reflow row on
    /// <paramref name="node"/>/<paramref name="ch"/> will restore at settle — normally NaN (auto). False when no reflow
    /// row is in flight. The reconciler's transparent-boundary mirror consults this so a component anchor never
    /// snapshots the EASED size as a hard declared size (nothing would ever restore the anchor: SettleRestore only
    /// touches the animated node itself, and MountComponent never writes an anchor's LayoutInput).</summary>
    public bool TryGetReflowDeclared(NodeHandle node, AnimChannel ch, out float declared)
    {
        if (ch is AnimChannel.LayoutW or AnimChannel.LayoutH)
        {
            int s = Find(node, ch);
            if (s >= 0) { declared = _slab.At(s).RestoreTo; return true; }
        }
        declared = float.NaN;
        return false;
    }

    /// <summary>Mark a live Reveal-size track as Relayout-mode (the host re-solves the subtree each tick; the declared
    /// LayoutInput is stashed for the settle-restore).</summary>
    private void MarkRestoreLayout(NodeHandle node, AnimChannel ch, float declared)
    {
        int s = Find(node, ch);
        if (s < 0) return;
        ref AnimValue r = ref _slab.At(s);
        r.RestoreTo = declared;
        r.Flags |= AnimFlags.RestoreLayout;
    }

    /// <summary>Settle-restore for a completing Reveal/Reflow row (called before the row is freed): restore the
    /// element-DECLARED LayoutInput, reset the presented-size/trim/clip sentinels to NaN/Infinite, queue the final
    /// boundary re-solve. Ported from AnimEngine.Tick's free loop.</summary>
    private void SettleRestore(int slot)
    {
        AnimValue r = _slab.At(slot);   // copy: we only read its fields (no ref held across _scene mutations)
        NodeHandle node = r.Node;
        if (!_scene.IsLive(node)) return;
        AnimChannel ch = r.Channel;

        if (ch is AnimChannel.LayoutW or AnimChannel.LayoutH)
        {
            ref LayoutInput rli = ref _scene.Layout(node);
            if (ch == AnimChannel.LayoutW) rli.Width = r.RestoreTo; else rli.Height = r.RestoreTo;
            ref NodePaint rp = ref _scene.Paint(node);
            rp.ChildShiftX = 0f; rp.ChildShiftY = 0f;
            MarkReflowLayoutDirty(node);
            _scene.Mark(node, NodeFlags.PaintDirty);
            _reflowWrote = true;
            return;
        }

        bool isReveal = ch is AnimChannel.SizeW or AnimChannel.SizeH or AnimChannel.StrokeTrimStart or AnimChannel.StrokeTrimEnd
            or AnimChannel.ClipL or AnimChannel.ClipT or AnimChannel.ClipR or AnimChannel.ClipB;
        if (!isReveal) return;

        if (r.Has(AnimFlags.RestoreLayout) && ch is AnimChannel.SizeW or AnimChannel.SizeH)
        {
            ref LayoutInput rli = ref _scene.Layout(node);
            if (ch == AnimChannel.SizeW) rli.Width = r.RestoreTo; else rli.Height = r.RestoreTo;
            _scene.Mark(node, NodeFlags.LayoutDirty);
            _reflowWrote = true;
        }
        ref NodePaint p = ref _scene.Paint(node);
        if (ch == AnimChannel.SizeW) { p.PresentedW = float.NaN; _scene.Unmark(node, NodeFlags.Relayouting); }
        else if (ch == AnimChannel.SizeH) p.PresentedH = float.NaN;
        else if (ch == AnimChannel.StrokeTrimStart) p.StrokeTrimStart = float.NaN;
        else if (ch == AnimChannel.StrokeTrimEnd) p.StrokeTrimEnd = float.NaN;
        else p.ClipRect = RectF.Infinite;
        _scene.Mark(node, NodeFlags.PaintDirty);
    }

    /// <summary>The STRUCTURAL / bounds channels a resize or a suppressed projection must snap: the FLIP position +
    /// ScaleCorrect scale axes and the Reveal/Relayout/Reflow size axes. Excludes the gesture/brush side-table fades
    /// (Hover/Press/Brush), opacity, blur, rotation, stroke-trim, and clip — those keep running through a resize (a
    /// card's hover/press/enter fade is not stale just because the window changed size).</summary>
    private static bool IsStructuralChannel(AnimChannel ch)
        => ch is AnimChannel.TranslateX or AnimChannel.TranslateY
              or AnimChannel.ScaleX or AnimChannel.ScaleY
              or AnimChannel.SizeW or AnimChannel.SizeH
              or AnimChannel.LayoutW or AnimChannel.LayoutH;

    /// <summary>Cancel a node's in-flight STRUCTURAL rows and land its presented state on the just-solved geometry —
    /// the shared snap for (a) a suppressed projection (an interactive/edge/maximize resize owns geometry, so a bounds
    /// change must NOT start a projection) and (b) a real-resize frame (an in-flight track is guaranteed-stale). Each
    /// size/reflow row settle-restores FIRST (declared LayoutInput back — usually NaN — plus PresentedW/H → NaN, the
    /// Relayouting flag cleared, child-shift zeroed) so SizeMode.Relayout leaves no li.Width/Height poisoned with a
    /// stale PresentedW; the FLIP position/scale rows drop and the composited transform resets to identity so no stale
    /// translate/scale survives to draw the node at slot+staleOffset. Gesture-owned Translate/Scale (WhileHover Offset
    /// on a Fold cover) are skipped — those are the authored rest pose, not a FLIP leftover; wiping them to identity
    /// stacked covers at the origin until the next hover. Interaction/brush/opacity/blur rows are left running.
    /// Zero-alloc POD-slab walk (no LINQ/enumerator); the caller runs it BEFORE layout so bounds land clean.</summary>
    public void SnapStructuralToLayout(NodeHandle node)
    {
        int idx = (int)node.Raw.Index;
        bool live = _scene.IsLive(node);
        bool resetTransform = false;
        bool damaged = false;
        int freed = 0;
        int s = _slab.HeadOnNode(idx);
        while (s >= 0)
        {
            int next = _slab.At(s).NextOnNode;   // read the link BEFORE FreeSlot unlinks the row
            AnimChannel ch = _slab.At(s).Channel;
            if (IsStructuralChannel(ch) && !IsGestureOwnedTransform(idx, ch))
            {
                // Ghost-band damage (Fix 1): the FIRST structural row we're about to cancel means this node was drawing
                // at a translated/reveal-inflated extent LAST frame. Snapshot that last-presented rect NOW — before the
                // SettleRestore below wipes PresentedW/H→NaN and before the LocalTransform→identity reset at the tail —
                // so the next record damages (and repaints) the band the node vacates. Without this the damage-driven
                // acrylic/backdrop cache keeps last frame's pixels in the vacated band → a persistent dark ghost rail.
                if (live && !damaged) { CaptureLastPresentedDamage(node); damaged = true; }
                if (ch is AnimChannel.TranslateX or AnimChannel.TranslateY or AnimChannel.ScaleX or AnimChannel.ScaleY)
                    resetTransform = true;
                SettleRestore(s);   // size/reflow: restore declared LayoutInput + sentinels; transform channels: no-op
                FreeSlot(s);
                freed++;
            }
            s = next;
        }
        if (s_motionDiag) Console.Error.WriteLine($"[motion-diag]   SnapStructuralToLayout node={idx} freedStructuralRows={freed} live={live}");
        // The freed FLIP rows no longer re-compose, so the last-written translate/scale would persist in NodePaint —
        // reset to identity (rotation, if any live row still drives it, re-folds from FromPaint next tick).
        if (resetTransform && live)
        {
            ref NodePaint p = ref _scene.Paint(node);
            p.LocalTransform = Affine2D.Identity;
            _scene.Mark(node, NodeFlags.TransformDirty | NodeFlags.PaintDirty);
        }
    }

    /// <summary>Queue the node's LAST-PRESENTED absolute rect as pending frame damage (see
    /// <see cref="PendingStructuralDamage"/>). Must be called BEFORE the row's SettleRestore/transform-reset, while the
    /// paint still holds the mid-anim state. <see cref="SceneStore.AbsoluteRect"/> already sums the node's OWN
    /// LocalTransform.Dx/Dy up the chain (it is the same origin SceneRecorder draws the node at — the ghost-band walk
    /// subtracts it back out), so we take it directly and do NOT re-add the translate; only the presented EXTENT
    /// (PresentedW/H, which may exceed model bounds mid-shrink) is substituted, matching the recorder's presented rect.
    /// Space matches the recorder's world-space float damage (walk starts at identity; DPI is applied at the RHI leaf),
    /// so no DIP→device conversion is needed here.</summary>
    private void CaptureLastPresentedDamage(NodeHandle node)
    {
        RectF abs = _scene.AbsoluteRect(node);   // presented absolute top-left (own LocalTransform already folded in)
        ref readonly NodePaint p = ref _scene.Paint(node);
        float pw = float.IsNaN(p.PresentedW) ? abs.W : p.PresentedW;
        float ph = float.IsNaN(p.PresentedH) ? abs.H : p.PresentedH;
        const float pad = 1.5f;   // AA/subpixel halo (the shrinking edge is not pixel-snapped mid-anim)
        PendingStructuralDamage.Add(new RectF(abs.X - pad, abs.Y - pad, pw + 2f * pad, ph + 2f * pad));
    }

    /// <summary>Resize-frame bulk snap: cancel every FLIP node's in-flight structural rows and land it on the geometry
    /// the imminent (re)layout solves (<see cref="SnapStructuralToLayout"/> per node). Zero-alloc — walks the caller's
    /// FLIP-node registry (SceneStore.BoundsAnimatedNodes) and, per node, the POD row chain. Freeing anim rows never
    /// mutates the passed list (it indexes SceneStore nodes, not slab slots), so the walk is stable across the frees.</summary>
    public void CancelStructuralAll(List<NodeHandle> flipNodes)
    {
        if (s_motionDiag) Console.Error.WriteLine($"[motion-diag] CancelStructuralAll flipNodes={flipNodes.Count}");
        for (int i = 0; i < flipNodes.Count; i++)
            SnapStructuralToLayout(flipNodes[i]);
    }
}
