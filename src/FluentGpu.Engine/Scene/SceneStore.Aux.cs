using FluentGpu.Foundation;

namespace FluentGpu.Scene;

// P1 (Operation ultra-fast GPU engine): the `_aux` byte column — one bit-name enum shared by every phase that needs a
// cheap per-node sideband bit (P1 = Collapsed only; P4's incremental layout reuses SubtreeLayoutDirty/ArrangedValid
// without a second column). Mechanical split out of SceneStore.cs per the plan's file-ownership rule.
public sealed partial class SceneStore
{
    /// <summary>Per-node sideband bits, one byte, SoA column (parallel to <c>_flags</c> etc.). Only
    /// <see cref="AuxFlags.Collapsed"/> is read/written before P4; the other two bits are reserved so P4's
    /// incremental-layout machinery (<c>FlexLayout.Incremental.cs</c>) can land without a second column.</summary>
    [System.Flags]
    public enum AuxFlags : byte
    {
        None = 0,
        /// <summary>Element.Visible resolved to false (P1 presence channel): the node is out of layout flow AND
        /// paint AND hit-test — a collapsed box, not merely an invisible one. Mirrored onto
        /// <see cref="NodeFlags.Visible"/>/<see cref="NodeFlags.HitTestVisible"/> by <see cref="SetCollapsed"/> so
        /// every existing Visible-gated reader (paint reachability, hit-test, the layout-signature hash) sees it for
        /// free with no new column read.</summary>
        Collapsed = 1 << 0,
        /// <summary>Reserved for P4 (incremental layout): a subtree-scoped layout-dirty bit cheaper to probe than
        /// walking <c>_layoutDirty</c>. Unused before P4.</summary>
        SubtreeLayoutDirty = 1 << 1,
        /// <summary>Reserved for P4: sticky "this node's last Arrange is still valid" bit for the incremental solver's
        /// reuse check. Unused before P4.</summary>
        ArrangedValid = 1 << 2,
        /// <summary>P4: this node has a scroll/virtual viewport SOMEWHERE in its subtree (set permanently — never
        /// cleared — the first time <see cref="ScrollRef"/> creates a viewport row under it; see
        /// <see cref="MarkScrollDescendantChain"/>). A viewport's <c>ArrangeViewport</c> has continuous per-frame
        /// obligations (posting <c>SetFrame</c>, checking <c>VirtualWindowing.NeedsRealize</c>) that are NOT gated by
        /// <see cref="NodeFlags.LayoutDirty"/> at all — scrolling is deliberately layout-free (transform-only,
        /// layout.md §6) — so an ancestor whose OWN rect is clean-and-unchanged must still be walked into on every
        /// pass if a viewport lives anywhere below it, or that viewport silently stops being serviced. The Arrange
        /// early-out excludes any such ancestor.</summary>
        HasScrollDescendant = 1 << 3,
    }

    private byte[] _aux = System.Array.Empty<byte>();

    /// <summary>True when the node is collapsed by the presence channel (<see cref="Dsl.Element.Visible"/> resolved
    /// false) — out of layout flow, unpainted, not hit-testable. Default false (every node starts visible).</summary>
    public bool IsCollapsed(NodeHandle h) => ((AuxFlags)_aux[h.Raw.Index] & AuxFlags.Collapsed) != 0;

    /// <summary>Set the node's collapsed state (P1 presence). Composes <see cref="NodeFlags.Visible"/> and
    /// <see cref="NodeFlags.HitTestVisible"/> directly (collapsed ⇒ both cleared; else both set) so the recorder's
    /// paint-reachability skip (<c>SceneRecorder</c>, gated on <c>NodeFlags.Visible</c>), the hit-test walk
    /// (<c>InputDispatcher</c>) and the layout-signature hash (<c>FlexLayout.LayoutSig</c>, which already mixes
    /// <c>Flags(node)</c>) all see the flip with NO new read. Marks the node LayoutDirty+PaintDirty and the PARENT
    /// LayoutDirty (a collapse/reveal changes the parent's flow, not just this node's own box). Unconditional — call
    /// <see cref="SetCollapsedIfChanged"/> instead when you need the equality gate (the common WriteColumns/bind path,
    /// so an identical re-render or an unchanged bound value marks nothing — <c>gate.hooks.layout-dirty-identical-tree</c>).</summary>
    public void SetCollapsed(NodeHandle h, bool collapsed)
    {
        int idx = (int)h.Raw.Index;
        var cur = (AuxFlags)_aux[idx];
        _aux[idx] = (byte)(collapsed ? (cur | AuxFlags.Collapsed) : (cur & ~AuxFlags.Collapsed));
        if (collapsed)
        {
            Unmark(h, NodeFlags.Visible | NodeFlags.HitTestVisible);
            // Zero immediately rather than waiting for a layout pass to reach it: a Flex/Wrap/ZStack parent's
            // FirstVisibleChild/NextVisibleSibling walk (FlexLayout.cs) SKIPS a collapsed child entirely — true flow
            // removal, not merely an invisible box — so it never calls Measure/Arrange on this node again while
            // collapsed, and its Bounds would otherwise sit stale at whatever size it last held (a runtime true→false
            // flip on an already-laid-out node, unlike a fresh mount whose Bounds start at the SceneStore default).
            ref RectF b = ref _bounds[idx];
            b = new RectF(b.X, b.Y, 0f, 0f);
        }
        else Mark(h, NodeFlags.Visible | NodeFlags.HitTestVisible);
        Mark(h, NodeFlags.LayoutDirty | NodeFlags.PaintDirty);
        var parent = Parent(h);
        if (!parent.IsNull && IsLive(parent)) Mark(parent, NodeFlags.LayoutDirty);
    }

    /// <summary>Equality-gated <see cref="SetCollapsed"/>: no-op (marks nothing, touches no flag) when the node is
    /// already in the requested state. Returns true iff it actually flipped (the reconciler uses this to decide
    /// whether to also walk the subtree's component entries — <c>Reconciler.Presence.cs</c>'s SetSubtreeHidden).</summary>
    public bool SetCollapsedIfChanged(NodeHandle h, bool collapsed)
    {
        if (IsCollapsed(h) == collapsed) return false;
        SetCollapsed(h, collapsed);
        return true;
    }

    // ── P4 (Operation ultra-fast GPU engine, incremental layout by default) ────────────────────────────────────

    /// <summary>True when some node STRICTLY BELOW <paramref name="h"/> (a descendant, not <paramref name="h"/>
    /// itself) is layout-dirty this frame. Set by <see cref="Mark"/>'s <see cref="NodeFlags.LayoutDirty"/> 0→1 edge,
    /// which walks PARENTS setting this bit until it finds an already-set ancestor (mirrors the <c>_recordDirty</c>
    /// aggregate walk); cleared by <see cref="ClearLayoutDirty"/> walking the same chain once the frame's worklist
    /// is fully processed. A node's OWN dirtiness is deliberately NOT folded into its own bit — only into its
    /// ancestors' — so the combined "nothing to do here" test is <see cref="IsLayoutClean"/>, not this alone.</summary>
    public bool IsSubtreeLayoutDirty(NodeHandle h) => ((AuxFlags)_aux[h.Raw.Index] & AuxFlags.SubtreeLayoutDirty) != 0;

    /// <summary>The P4 Measure/Arrange early-out precondition: neither <paramref name="h"/> itself nor anything in
    /// its subtree was marked layout-dirty this frame. Two flag reads, no traversal.</summary>
    public bool IsLayoutClean(NodeHandle h)
    {
        int idx = (int)h.Raw.Index;
        if ((_flags[idx] & NodeFlags.LayoutDirty) != 0) return false;
        return ((AuxFlags)_aux[idx] & AuxFlags.SubtreeLayoutDirty) == 0;
    }

    /// <summary>True once <paramref name="h"/> has a real, fully-formed arranged rect on record (P4's <c>_arranged</c>
    /// column in <c>FlexLayout</c>) — set by <c>FlexLayout.SetArrangedBounds</c>, cleared on <see cref="CreateNode"/>
    /// (the whole <c>_aux</c> byte is zeroed there). A false reading means "never arranged, or the node index was
    /// recycled since" — the Arrange early-out must not trust <c>_arranged</c> without this.</summary>
    public bool IsArrangedValid(NodeHandle h) => ((AuxFlags)_aux[h.Raw.Index] & AuxFlags.ArrangedValid) != 0;

    /// <summary>Sets <see cref="AuxFlags.ArrangedValid"/>. The ONLY writer is <c>FlexLayout.SetArrangedBounds</c> —
    /// every other Arrange exit (the P1 collapsed short-circuit included) also goes through that one method.</summary>
    public void SetArrangedValid(NodeHandle h) => _aux[h.Raw.Index] |= (byte)AuxFlags.ArrangedValid;

    /// <summary>Walk from <paramref name="idx"/>'s PARENT upward, setting <see cref="AuxFlags.SubtreeLayoutDirty"/>,
    /// stopping at the first ancestor that already has it set (everything above that point is therefore already
    /// known-dirty — the propagation from an earlier mark this frame already reached it and beyond). Called once
    /// per <see cref="NodeFlags.LayoutDirty"/> 0→1 edge, from <see cref="Mark"/>.</summary>
    private void MarkSubtreeLayoutDirtyChain(int idx)
    {
        for (int n = _parent[idx]; n != 0; n = _parent[n])
        {
            var bits = (AuxFlags)_aux[n];
            if ((bits & AuxFlags.SubtreeLayoutDirty) != 0) return;
            _aux[n] = (byte)(bits | AuxFlags.SubtreeLayoutDirty);
        }
    }

    /// <summary>The mirror clear, run once per <c>ClearLayoutDirty</c> worklist entry: walk from
    /// <paramref name="idx"/>'s PARENT upward clearing <see cref="AuxFlags.SubtreeLayoutDirty"/>, stopping at the
    /// first ancestor that is already clear. Safe to stop early: <c>ClearLayoutDirty</c> processes EVERY node that
    /// was LayoutDirty this frame (the worklist is exactly that set, by construction — see <see cref="Mark"/>), so
    /// by the time this whole loop finishes, no ancestor chain can still have a live reason to stay set; an
    /// already-clear ancestor found mid-walk means either it was never set, or an earlier iteration of this same
    /// loop already cleared it (and, by the same stopping rule, everything above it too).</summary>
    private void ClearSubtreeLayoutDirtyChain(int idx)
    {
        for (int n = _parent[idx]; n != 0; n = _parent[n])
        {
            var bits = (AuxFlags)_aux[n];
            if ((bits & AuxFlags.SubtreeLayoutDirty) == 0) return;
            _aux[n] = (byte)(bits & ~AuxFlags.SubtreeLayoutDirty);
        }
    }

    /// <summary>True when <paramref name="h"/> itself is a scroll viewport OR has one anywhere below it. The Arrange
    /// early-out's SECOND exclusion (alongside the direct <c>HasScroll(node)</c> check on the node itself): a clean,
    /// geometrically-unchanged ANCESTOR must still be walked into so a viewport further down keeps getting serviced
    /// every pass — see <see cref="AuxFlags.HasScrollDescendant"/>.</summary>
    public bool HasScrollInSubtree(NodeHandle h) => HasScroll(h) || ((AuxFlags)_aux[h.Raw.Index] & AuxFlags.HasScrollDescendant) != 0;

    /// <summary>Walk from <paramref name="idx"/>'s PARENT upward setting <see cref="AuxFlags.HasScrollDescendant"/>,
    /// stopping at the first ancestor that already has it (a permanent, monotonic mark — mirrors
    /// <see cref="MarkSubtreeLayoutDirtyChain"/>'s shape but is NEVER cleared: a viewport occasionally being removed
    /// later is not worth tracking precisely — the cost is a few skipped early-outs on an ancestor that no longer
    /// needs the caution, never a correctness gap). Called once, the first time <c>ScrollRef</c> creates a viewport
    /// row for a given node.</summary>
    private void MarkScrollDescendantChain(int idx)
    {
        for (int n = _parent[idx]; n != 0; n = _parent[n])
        {
            var bits = (AuxFlags)_aux[n];
            if ((bits & AuxFlags.HasScrollDescendant) != 0) return;
            _aux[n] = (byte)(bits | AuxFlags.HasScrollDescendant);
        }
    }
}
