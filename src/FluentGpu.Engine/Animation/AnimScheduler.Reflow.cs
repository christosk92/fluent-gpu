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

    private bool _reflowWrote;
    /// <summary>True if any reflow track wrote LayoutInput this tick (advance or settle restore) — the host runs a
    /// boundary-scoped re-solve before record. Self-clearing.</summary>
    public bool ConsumeReflowWrites() { bool w = _reflowWrote; _reflowWrote = false; return w; }

    /// <summary>A node mounted with a SizeMode.Reflow enter eases its MAIN-axis LAYOUT size 0 → its solved size so
    /// neighbours reflow as it reveals. Host-called after layout (the natural size isn't known pre-layout).</summary>
    public void SeedEnterReflow(NodeHandle node, bool horizontal, float toW, float toH)
    {
        if (!TryGetTransition(node, out var spec)) return;
        if (horizontal) { if (toW > 0.5f) ReflowSize(node, AnimChannel.LayoutW, 0f, toW, spec); }
        else { if (toH > 0.5f) ReflowSize(node, AnimChannel.LayoutH, 0f, toH, spec); }
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

        float declared = ch == AnimChannel.LayoutW ? _scene.Layout(node).Width : _scene.Layout(node).Height;
        if (dyn.Kind == DynamicsKind.Spring)
            Spring(node, ch, to, SpringParams.FromResponse(dyn.Response, dyn.DampingRatio), initial: from, delayMs: spec.DelayMs);
        else
            Animate(node, ch, from, to, dyn.DurationMs, dyn.Easing, delayMs: spec.DelayMs);

        int s = Find(node, ch);
        if (s >= 0)
        {
            ref AnimValue r = ref _slab.At(s);
            r.RestoreTo = declared;
            if (spec.Anchor == SizeAnchor.Trailing) r.Flags |= AnimFlags.TrailingAnchor;
        }
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
            var rpar = _scene.Parent(node);
            _scene.Mark(rpar.IsNull ? node : rpar, NodeFlags.LayoutDirty);
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
    /// translate/scale survives to draw the node at slot+staleOffset. Interaction/brush/opacity/blur rows are left
    /// running. Zero-alloc POD-slab walk (no LINQ/enumerator); the caller runs it BEFORE layout so bounds land clean.</summary>
    public void SnapStructuralToLayout(NodeHandle node)
    {
        int idx = (int)node.Raw.Index;
        bool live = _scene.IsLive(node);
        bool resetTransform = false;
        int s = _slab.HeadOnNode(idx);
        while (s >= 0)
        {
            int next = _slab.At(s).NextOnNode;   // read the link BEFORE FreeSlot unlinks the row
            AnimChannel ch = _slab.At(s).Channel;
            if (IsStructuralChannel(ch))
            {
                if (ch is AnimChannel.TranslateX or AnimChannel.TranslateY or AnimChannel.ScaleX or AnimChannel.ScaleY)
                    resetTransform = true;
                SettleRestore(s);   // size/reflow: restore declared LayoutInput + sentinels; transform channels: no-op
                FreeSlot(s);
            }
            s = next;
        }
        // The freed FLIP rows no longer re-compose, so the last-written translate/scale would persist in NodePaint —
        // reset to identity (rotation, if any live row still drives it, re-folds from FromPaint next tick).
        if (resetTransform && live)
        {
            ref NodePaint p = ref _scene.Paint(node);
            p.LocalTransform = Affine2D.Identity;
            _scene.Mark(node, NodeFlags.TransformDirty | NodeFlags.PaintDirty);
        }
    }

    /// <summary>Resize-frame bulk snap: cancel every FLIP node's in-flight structural rows and land it on the geometry
    /// the imminent (re)layout solves (<see cref="SnapStructuralToLayout"/> per node). Zero-alloc — walks the caller's
    /// FLIP-node registry (SceneStore.BoundsAnimatedNodes) and, per node, the POD row chain. Freeing anim rows never
    /// mutates the passed list (it indexes SceneStore nodes, not slab slots), so the walk is stable across the frees.</summary>
    public void CancelStructuralAll(List<NodeHandle> flipNodes)
    {
        for (int i = 0; i < flipNodes.Count; i++)
            SnapStructuralToLayout(flipNodes[i]);
    }
}
