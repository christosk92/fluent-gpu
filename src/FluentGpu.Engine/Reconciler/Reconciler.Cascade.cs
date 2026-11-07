using FluentGpu.Animation;
using FluentGpu.Foundation;
using FluentGpu.Scene;

namespace FluentGpu.Reconciler;

// ENTRANCE CASCADES (EntranceCascade, Element.EntranceGroup): a navigation entrance that staggers a page's on-screen units
// instead of moving the page as one block. A keep-alive activation (KeepAliveOptions.CascadeFor) or a component
// (RenderContext.EntranceCascade) REQUESTS one during the commit; the host RESOLVES it after the frame's layout (the units are the
// group children the viewport shows, so bounds must be valid) and before the frame's animation tick, so the first presented frame
// already shows every unit at its terminal. Units are composited while they enter (NodePaint.CompositeLayer, up to the cascade's
// CompositeUnits): their slide and fade are composite parameters, nothing under them re-records per frame, and the flag is
// dropped again once their tracks settle (SettleEntranceCascades), so a page holds no effect slice at rest.
public sealed partial class TreeReconciler
{
    /// <summary>Nodes whose element sets <see cref="FluentGpu.Dsl.Element.EntranceGroup"/>, by index (the handle carries the
    /// generation). Small: a page marks a handful of containers.</summary>
    private readonly Dictionary<int, NodeHandle> _entranceGroups = new();
    private readonly List<(NodeHandle Root, EntranceCascade Cascade)> _pendingCascades = new(2);
    private readonly HashSet<int> _cascadePath = new();
    private readonly List<NodeHandle> _cascadeUnits = new(32);
    /// <summary>Units this reconciler composited for their entrance: the flag is cleared when their tracks settle.</summary>
    private readonly List<NodeHandle> _cascadeComposited = new(16);
    private Action<NodeHandle, EntranceCascade>? _cascadeSeam;

    /// <summary>Entrance cascades resolved since the reconciler was created (diagnostics), and the units the last one seeded.</summary>
    public int CascadesRun { get; private set; }
    public int LastCascadeUnits { get; private set; }

    private void NoteEntranceGroup(NodeHandle node, bool group)
    {
        if (group) _entranceGroups[(int)node.Raw.Index] = node;
        else _entranceGroups.Remove((int)node.Raw.Index);
    }

    /// <summary>Stagger the on-screen units under <paramref name="root"/> in (<see cref="EntranceCascade"/>), resolved after this
    /// frame's layout. A second request for the same root this frame replaces the first.</summary>
    public void RequestEntranceCascade(NodeHandle root, EntranceCascade cascade)
    {
        if (root.IsNull) return;
        for (int i = 0; i < _pendingCascades.Count; i++)
            if (_pendingCascades[i].Root == root) { _pendingCascades[i] = (root, cascade); return; }
        _pendingCascades.Add((root, cascade));
    }

    /// <summary>Host phase 6.7 (after layout and the FLIP projections, before the animation tick): resolve and seed every cascade
    /// requested this frame. Nothing to do on a frame without one.</summary>
    public void RunEntranceCascades()
    {
        if (_pendingCascades.Count == 0) return;
        for (int i = 0; i < _pendingCascades.Count; i++)
        {
            var (root, cascade) = _pendingCascades[i];
            if (Anim is null || SnapMotion || !_scene.IsLive(root)) continue;
            RunCascade(root, in cascade);
        }
        _pendingCascades.Clear();
    }

    private void RunCascade(NodeHandle root, in EntranceCascade cascade)
    {
        // 1. The path: every ancestor (up to the root) of a group under the root. The walk below descends only along it.
        _cascadePath.Clear();
        int rootIdx = (int)root.Raw.Index;
        foreach (var g in _entranceGroups.Values)
        {
            if (!_scene.IsLive(g)) continue;
            bool under = false;
            for (var n = g; !n.IsNull; n = _scene.Parent(n))
                if (n == root) { under = true; break; }
            if (!under) continue;
            for (var n = g; !n.IsNull && n != root; n = _scene.Parent(n))
                if (!_cascadePath.Add((int)n.Raw.Index)) break;
            _cascadePath.Add(rootIdx);
        }

        // 2. The units, in reading (document) order, clipped to what the viewport shows.
        _cascadeUnits.Clear();
        RectF clip = _scene.AbsoluteRect(root);
        if (_cascadePath.Count > 0) Visit(root, clip, cascade.MaxUnits);
        // A root that holds no group enters as the one unit.
        if (_cascadeUnits.Count == 0 && !Anim!.DrivesPose(root)) _cascadeUnits.Add(root);
        else if (_cascadeUnits.Count == 0) return;

        // 3. Seed: each unit from the terminal to its authored pose, its start staggered.
        var spec = new LayoutTransition(TransitionChannels.Position | TransitionChannels.Opacity, cascade.Dynamics);
        EnterExit terminal = cascade.Enter with { Active = true };
        for (int i = 0; i < _cascadeUnits.Count; i++)
        {
            NodeHandle unit = _cascadeUnits[i];
            ref NodePaint p = ref _scene.Paint(unit);
            Affine2D m = p.LocalTransform;
            var rest = new EnterRest(m.Dx, m.Dy, MathF.Sqrt(m.M11 * m.M11 + m.M12 * m.M12), MathF.Sqrt(m.M21 * m.M21 + m.M22 * m.M22), p.Opacity, 0f);
            if (unit != root && i < cascade.CompositeUnits && !p.CompositeLayer)
            {
                p.CompositeLayer = true;
                _scene.Mark(unit, NodeFlags.PaintDirty);
                _cascadeComposited.Add(unit);
            }
            Anim!.SeedEnterOver(unit, terminal with { DelayMs = cascade.Enter.DelayMs + cascade.DelayOf(i) }, spec, rest);
        }
        CascadesRun++;
        LastCascadeUnits = _cascadeUnits.Count;
    }

    /// <summary>A node on the path (it holds a group) or a group itself: its children are walked (a group's are offered as units).</summary>
    private void Visit(NodeHandle n, RectF clip, int maxUnits)
    {
        if (!Shows(n)) return;
        clip = ClipOf(n, clip);
        if (clip.IsEmpty) return;
        bool group = _entranceGroups.ContainsKey((int)n.Raw.Index);
        for (var c = _scene.FirstChild(n); !c.IsNull && _cascadeUnits.Count < maxUnits; c = _scene.NextSibling(c))
        {
            if (group) Offer(c, clip, maxUnits);
            else if (_cascadePath.Contains((int)c.Raw.Index)) Visit(c, clip, maxUnits);
        }
    }

    /// <summary>A group's child: a unit, unless it is a layout-transparent anchor (its children are offered instead) or it is, or
    /// holds, a group (it dissolves into its own children).</summary>
    private void Offer(NodeHandle c, RectF clip, int maxUnits)
    {
        if (!Shows(c)) return;
        int idx = (int)c.Raw.Index;
        if (_entranceGroups.ContainsKey(idx) || _cascadePath.Contains(idx)) { Visit(c, clip, maxUnits); return; }
        if (IsTransparentAnchor(c))
        {
            for (var g = _scene.FirstChild(c); !g.IsNull && _cascadeUnits.Count < maxUnits; g = _scene.NextSibling(g)) Offer(g, clip, maxUnits);
            return;
        }
        RectF r = _scene.AbsoluteRect(c);
        if (r.IsEmpty || r.Intersect(clip).IsEmpty) return;   // off screen: never animated
        if (Anim!.DrivesPose(c)) return;                       // its own track owns its pose
        if (_scene.TryGetScrollEffects(idx, out var fx) && fx is { Length: > 0 }) return;   // a sticky / parallax pose is the scroll's
        _cascadeUnits.Add(c);
    }

    private bool Shows(NodeHandle n)
        => _scene.IsLive(n) && (_scene.Flags(n) & NodeFlags.Visible) != 0 && _scene.Paint(n).Opacity > 0f;

    private RectF ClipOf(NodeHandle n, RectF clip)
        => (_scene.Flags(n) & (NodeFlags.ClipsToBounds | NodeFlags.Scrollable)) != 0 ? clip.Intersect(_scene.AbsoluteRect(n)) : clip;

    /// <summary>Components, providers, Show, For, skeleton regions and keep-alive boundaries: anchors laid out as if their children
    /// were the parent's own.</summary>
    private bool IsTransparentAnchor(NodeHandle n) => _scene.ElementTypeId(n) is 3 or 4 or 7 or 10 or 13 or 14;

    /// <summary>Host phase 7 (after the animation tick): a composited unit whose entrance settled goes back to recording inline, so a
    /// page at rest holds no effect slice for it. Nothing to do while no unit is composited.</summary>
    public void SettleEntranceCascades()
    {
        if (_cascadeComposited.Count == 0 || Anim is null) return;
        for (int i = _cascadeComposited.Count - 1; i >= 0; i--)
        {
            NodeHandle u = _cascadeComposited[i];
            if (_scene.IsLive(u) && Anim.DrivesPose(u)) continue;
            if (_scene.IsLive(u))
            {
                _scene.Paint(u).CompositeLayer = false;
                _scene.Mark(u, NodeFlags.PaintDirty);
            }
            _cascadeComposited.RemoveAt(i);
        }
    }
}
