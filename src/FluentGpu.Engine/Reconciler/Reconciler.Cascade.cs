using FluentGpu.Animation;
using FluentGpu.Foundation;
using FluentGpu.Scene;

namespace FluentGpu.Reconciler;

// ENTRANCE CASCADES (EntranceCascade, Element.EntranceGroup): a navigation entrance that staggers a page's on-screen units
// instead of moving the page as one block. A keep-alive activation (KeepAliveOptions.CascadeFor) or a component
// (RenderContext.EntranceCascade) REQUESTS one during the commit; the host RESOLVES it after the frame's layout (the units are the
// group children the viewport shows, so bounds must be valid) and before the frame's animation tick, so the first presented frame
// already shows every unit at its terminal.
//
// A cascade stays LIVE for its own length (the last unit's start plus one unit's tween): a page rarely presents all of itself on
// its first frame (a body that mounts once its width is measured, a column that lands a frame later, a skeleton whose content lands
// mid-entrance). Every frame while it is live the walk runs again, and a unit it has not seen yet JOINS the cascade where its slot
// is: before its slot starts it waits for it, after it it starts at the slot's current progress for the slot's remaining time, so a
// late part rides with its neighbours instead of popping, and content that replaces a skeleton mid-entrance takes the skeleton's
// place in the motion rather than entering a second time. Once the cascade is over, what mounts later just appears.
//
// Units are composited while they enter (NodePaint.CompositeLayer, up to the cascade's CompositeUnits): their slide and fade are
// composite parameters, nothing under them re-records per frame, and the flag is dropped again once their tracks settle
// (SettleEntranceCascades), so a page at rest holds no effect slice for it.
public sealed partial class TreeReconciler
{
    /// <summary>Nodes whose element sets <see cref="FluentGpu.Dsl.Element.EntranceGroup"/>, by index (the handle carries the
    /// generation). Small: a page marks a handful of containers.</summary>
    private readonly Dictionary<int, NodeHandle> _entranceGroups = new();
    private readonly List<(NodeHandle Root, EntranceCascade Cascade)> _pendingCascades = new(2);
    private readonly List<LiveCascade> _liveCascades = new(2);
    private readonly HashSet<int> _cascadePath = new();
    private readonly List<NodeHandle> _cascadeUnits = new(32);
    private double[] _cascadeOrderKeys = new double[32];
    private NodeHandle[] _cascadeOrder = new NodeHandle[32];
    /// <summary>Units this reconciler composited for their entrance: the flag is cleared when their tracks settle.</summary>
    private readonly List<NodeHandle> _cascadeComposited = new(16);
    private Action<NodeHandle, EntranceCascade>? _cascadeSeam;

    private sealed class LiveCascade
    {
        public NodeHandle Root;
        public EntranceCascade Cascade;
        public double StartMs;
        public double EndMs;
        public int Composited;
        public readonly HashSet<ulong> Seen = new();
    }

    /// <summary>Entrance cascades started since the reconciler was created, and the units seeded so far (diagnostics).</summary>
    public int CascadesRun { get; private set; }
    public int CascadeUnitsSeeded { get; private set; }

    private void NoteEntranceGroup(NodeHandle node, bool group)
    {
        if (group) _entranceGroups[(int)node.Raw.Index] = node;
        else _entranceGroups.Remove((int)node.Raw.Index);
    }

    /// <summary>Stagger the on-screen units under <paramref name="root"/> in (<see cref="EntranceCascade"/>), resolved after this
    /// frame's layout. A new request for a root replaces the one live or pending for it.</summary>
    public void RequestEntranceCascade(NodeHandle root, EntranceCascade cascade)
    {
        if (root.IsNull) return;
        for (int i = 0; i < _pendingCascades.Count; i++)
            if (_pendingCascades[i].Root == root) { _pendingCascades[i] = (root, cascade); return; }
        _pendingCascades.Add((root, cascade));
    }

    /// <summary>Host phase 6.7 (after layout and the FLIP projections, before the animation tick): start every cascade requested this
    /// commit and let the live ones take in units that appeared since. Nothing to do on a frame without one.</summary>
    public void RunEntranceCascades()
    {
        if (_pendingCascades.Count == 0 && _liveCascades.Count == 0) return;
        double now = _scene.AnimClockMs;
        for (int i = 0; i < _pendingCascades.Count; i++)
        {
            var (root, cascade) = _pendingCascades[i];
            for (int k = _liveCascades.Count - 1; k >= 0; k--)
                if (_liveCascades[k].Root == root) _liveCascades.RemoveAt(k);
            if (Anim is null || SnapMotion || !_scene.IsLive(root)) continue;
            float last = cascade.DelayOf(cascade.MaxUnits);
            var live = new LiveCascade { Root = root, Cascade = cascade, StartMs = now, EndMs = now + last + Math.Max(0f, cascade.Dynamics.DurationMs) };
            _liveCascades.Add(live);
            CascadesRun++;
            Advance(live, now, first: true);
        }
        _pendingCascades.Clear();
        for (int k = _liveCascades.Count - 1; k >= 0; k--)
        {
            var live = _liveCascades[k];
            if (now > live.EndMs || Anim is null || SnapMotion || !_scene.IsLive(live.Root)) { _liveCascades.RemoveAt(k); continue; }
            if (live.StartMs < now) Advance(live, now, first: false);
        }
    }

    private void Advance(LiveCascade live, double now, bool first)
    {
        NodeHandle root = live.Root;
        EntranceCascade cascade = live.Cascade;
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

        // 2. The units, clipped to what the viewport shows.
        _cascadeUnits.Clear();
        if (_cascadePath.Count > 0) Visit(root, _scene.AbsoluteRect(root), cascade.MaxUnits);
        // A root that holds no group (yet) enters as the one unit: on the first frame only, and only when no recipe moves it already.
        if (_cascadeUnits.Count == 0)
        {
            if (first && !Anim!.DrivesPose(root)) _cascadeUnits.Add(root);
            else return;
        }

        // 2b. The stagger runs down the screen: top first, then left to right along a row (a grid's cards, two columns side by side).
        //     Document order put a long list's rows ahead of the pane beside it, which then waited out the whole list.
        SortUnitsOnScreen();

        // 3. Seed each unit not seen yet: from the terminal (or, joining late, from where its slot is now) to its authored pose.
        float elapsed = (float)(now - live.StartMs);
        float dur = Math.Max(1f, cascade.Dynamics.DurationMs);
        EasingSpec ease = cascade.Dynamics.Easing.IsDefault ? Easing.FluentDecelerate : cascade.Dynamics.Easing;
        for (int i = 0; i < _cascadeUnits.Count; i++)
        {
            NodeHandle unit = _cascadeUnits[i];
            if (!live.Seen.Add(((ulong)unit.Raw.Gen << 32) | unit.Raw.Index)) continue;
            if (unit != root && Anim!.DrivesPose(unit)) continue;   // its own enter (a keyed fade-in) owns it
            float slot = cascade.DelayOf(i) - elapsed;              // ms until its slot starts (negative: it started that long ago)
            float progress = slot >= 0f ? 0f : Math.Min(1f, -slot / dur);
            if (progress >= 1f) continue;                            // its slot is over: it is at rest, as it would be
            float k = 1f - Easings.Ease(ease, progress);             // how much of the terminal is still to travel
            EnterExit e = cascade.Enter;
            EnterExit terminal = new(e.Dx * k, e.Dy * k, 1f + (e.Sx - 1f) * k, 1f + (e.Sy - 1f) * k, 1f + (e.Opacity - 1f) * k, true,
                e.Blur * k, e.DelayMs + Math.Max(0f, slot));
            var spec = new LayoutTransition(TransitionChannels.Position | TransitionChannels.Opacity,
                progress > 0f ? cascade.Dynamics with { DurationMs = dur * (1f - progress) } : cascade.Dynamics);
            ref NodePaint p = ref _scene.Paint(unit);
            Affine2D m = p.LocalTransform;
            var rest = new EnterRest(m.Dx, m.Dy, MathF.Sqrt(m.M11 * m.M11 + m.M12 * m.M12), MathF.Sqrt(m.M21 * m.M21 + m.M22 * m.M22), p.Opacity, 0f);
            if (unit != root && live.Composited < cascade.CompositeUnits && !p.CompositeLayer)
            {
                p.CompositeLayer = true;
                _scene.Mark(unit, NodeFlags.PaintDirty);
                _cascadeComposited.Add(unit);
                live.Composited++;
            }
            Anim!.SeedEnterOver(unit, terminal, spec, rest);
            CascadeUnitsSeeded++;
        }
    }

    /// <summary>Order <see cref="_cascadeUnits"/> by their on-screen top, then left (whole DIP; ties keep reading order).</summary>
    private void SortUnitsOnScreen()
    {
        int n = _cascadeUnits.Count;
        if (n < 2) return;
        if (_cascadeOrderKeys.Length < n) { _cascadeOrderKeys = new double[n * 2]; _cascadeOrder = new NodeHandle[n * 2]; }
        for (int i = 0; i < n; i++)
        {
            RectF r = _scene.AbsoluteRect(_cascadeUnits[i]);
            _cascadeOrderKeys[i] = (MathF.Round(r.Y) * 65536.0 + MathF.Round(r.X) + 32768.0) * 256.0 + i;
            _cascadeOrder[i] = _cascadeUnits[i];
        }
        Array.Sort(_cascadeOrderKeys, _cascadeOrder, 0, n);
        for (int i = 0; i < n; i++) _cascadeUnits[i] = _cascadeOrder[i];
        Array.Clear(_cascadeOrder, 0, n);
    }

    /// <summary>A node on the path (it holds a group) or a group itself: its children are walked (a group's are offered as units).</summary>
    private void Visit(NodeHandle n, RectF clip, int maxUnits)
    {
        if (!Shows(n)) return;
        clip = ClipOf(n, clip);
        if (clip.IsEmpty) return;
        bool group = _entranceGroups.ContainsKey((int)n.Raw.Index);
        // A group that is a scroll viewport (a list, a shelf) is a frame, not a column: its items are its content's children.
        bool viewport = group && (_scene.Flags(n) & NodeFlags.Scrollable) != 0;
        for (var c = _scene.FirstChild(n); !c.IsNull && _cascadeUnits.Count < maxUnits; c = _scene.NextSibling(c))
        {
            if (viewport)
            {
                for (var item = _scene.FirstChild(c); !item.IsNull && _cascadeUnits.Count < maxUnits; item = _scene.NextSibling(item))
                    Offer(item, clip, maxUnits);
            }
            else if (group) Offer(c, clip, maxUnits);
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
