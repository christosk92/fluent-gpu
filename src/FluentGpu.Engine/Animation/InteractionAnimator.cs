using FluentGpu.Foundation;
using FluentGpu.Scene;

namespace FluentGpu.Animation;

/// <summary>
/// Eases per-node hover/press progress (phase 7), so the recorder can cross-fade Fill/Border/Opacity and scale template
/// parts from WinUI-style visual-state targets instead of instant flag switches. State lives in the sparse
/// <see cref="InteractionAnim"/> side-table; only nodes currently transitioning are ticked. Zero work / zero alloc on a
/// steady frame (<see cref="HasActive"/> == false).
/// </summary>
public sealed class InteractionAnimator
{
    private readonly SceneStore _scene;
    private readonly List<NodeHandle> _active = new();
    private readonly HashSet<int> _member = new();

    public InteractionAnimator(SceneStore scene) => _scene = scene;
    public bool HasActive => _active.Count > 0;
    /// <summary>Nodes currently easing a hover/press transition — O(1) census.</summary>
    public int ActiveCount => _active.Count;

    public void SetHover(NodeHandle node, bool on)
    {
        if (node.IsNull || !_scene.IsLive(node)) return;
        // A container reads as hovered while the pointer is anywhere in its subtree — Hovered (directly) OR HoverWithin
        // (over an interactive child). So its reveal-on-hover descendants (a list row's #-cell play glyph) must STAY
        // driven as the pointer crosses onto the row's buttons/links: turning the leaf hover off the row would otherwise
        // decay this subtree and collapse the reveal. The dispatcher's UpdateHoverWithin drives the on/off edges by
        // HoverWithin transitions; this guard makes a stray leaf-hover-off of a still-within container a no-op.
        bool effective = on || (_scene.Flags(node) & (NodeFlags.Hovered | NodeFlags.HoverWithin)) != 0;
        SetHoverCore(node, effective, force: true);
        SetHoverDescendants(node, effective);
    }

    public void SetPress(NodeHandle node, bool on)
    {
        if (node.IsNull || !_scene.IsLive(node)) return;
        SetPressCore(node, on, force: true);
        SetPressDescendants(node, on);
    }

    private void SetHoverDescendants(NodeHandle node, bool on)
    {
        for (var c = _scene.FirstChild(node); !c.IsNull; c = _scene.NextSibling(c))
        {
            // Only a REVEAL/scale affordance follows its CONTAINER's hover (a list-row #-cell play glyph that fades in
            // on row hover via HoverOpacity, a part that scales). An independent interactive sibling whose only hover
            // visual is a FILL (a ♥/like button: HoverFill, no HoverOpacity/HoverScale) must track the ACTUAL pointer,
            // so the container hover must not light it up. A pure-fill control is not seeded an anim row by the
            // reconciler — it only gets one after a DIRECT hover, and from then on the unconditional drive lit it up on
            // every row hover (the reported "hover the play button → the like button highlights too"). Gate the drive
            // on the reveal/scale intent; recurse unconditionally so a reveal nested deeper still gets reached.
            if (FollowsContainerHover(c)) SetHoverCore(c, on, force: false);
            SetHoverDescendants(c, on);
        }
    }

    /// <summary>A descendant follows its container's hover only for a reveal (HoverOpacity/PressedOpacity) or a
    /// hover/press scale — not for a fill-only control (which tracks the real pointer). Mirrors the reconciler's
    /// anim-row seeding predicate minus the fill case.</summary>
    private bool FollowsContainerHover(NodeHandle node)
    {
        ref NodePaint p = ref _scene.Paint(node);
        if (!float.IsNaN(p.HoverOpacity) || !float.IsNaN(p.PressedOpacity)) return true;
        return _scene.TryGetInteract(node, out var ia) && (ia.HoverScale != 1f || ia.PressScale != 1f);
    }

    private void SetPressDescendants(NodeHandle node, bool on)
    {
        for (var c = _scene.FirstChild(node); !c.IsNull; c = _scene.NextSibling(c))
        {
            SetPressCore(c, on, force: false);
            SetPressDescendants(c, on);
        }
    }

    private void SetHoverCore(NodeHandle node, bool on, bool force)
    {
        if (!force && !_scene.TryGetInteract(node, out _)) return;
        ref InteractionAnim ia = ref _scene.InteractRef(node);
        ia.HoverStart = ia.HoverT;
        ia.HoverTarget = on ? 1f : 0f;
        ia.HoverElapsedMs = 0f;
        Arm(node);
    }

    private void SetPressCore(NodeHandle node, bool on, bool force)
    {
        if (!force && !_scene.TryGetInteract(node, out _)) return;
        ref InteractionAnim ia = ref _scene.InteractRef(node);
        ia.PressStart = ia.PressT;
        ia.PressTarget = on ? 1f : 0f;
        ia.PressElapsedMs = 0f;
        Arm(node);
    }

    private void Arm(NodeHandle node)
    {
        if (_member.Add((int)node.Raw.Index)) _active.Add(node);
    }

    public void Tick(float dtMs)
    {
        if (_active.Count == 0) return;
        for (int i = _active.Count - 1; i >= 0; i--)
        {
            NodeHandle h = _active[i];
            if (!_scene.IsLive(h)) { Drop(i, h); continue; }
            ref InteractionAnim ia = ref _scene.InteractRef(h);
            ia.HoverT = Step(ia.HoverStart, ia.HoverTarget, ref ia.HoverElapsedMs, ia.HoverDurationMs, ia.HoverEasing, dtMs);
            ia.PressT = Step(ia.PressStart, ia.PressTarget, ref ia.PressElapsedMs, ia.PressDurationMs, ia.PressEasing, dtMs);
            _scene.Mark(h, NodeFlags.PaintDirty);
            if (MathF.Abs(ia.HoverTarget - ia.HoverT) < 0.004f && MathF.Abs(ia.PressTarget - ia.PressT) < 0.004f)
            {
                ia.HoverT = ia.HoverTarget; ia.PressT = ia.PressTarget;
                Drop(i, h);
            }
        }
    }

    private static float Step(float start, float target, ref float elapsedMs, float durationMs, EasingSpec easing, float dtMs)
    {
        if (MathF.Abs(target - start) < 0.0001f) return target;
        if (durationMs <= 0f) { elapsedMs = 0f; return target; }

        elapsedMs += MathF.Max(0f, dtMs);
        float t = Math.Clamp(elapsedMs / durationMs, 0f, 1f);
        float e = Easings.Ease(easing, t);
        float v = start + (target - start) * e;
        return t >= 1f ? target : v;
    }

    private void Drop(int i, NodeHandle h)
    {
        _member.Remove((int)h.Raw.Index);
        _active.RemoveAt(i);
    }
}
