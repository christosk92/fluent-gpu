using FluentGpu.Foundation;
using FluentGpu.Scene;

namespace FluentGpu.Layout;

/// <summary>
/// Scoped relayout (layout.md §4): consumes the SceneStore's LayoutDirty worklist and, for each dirty node, walks UP to
/// the nearest <b>layout boundary</b> — a fixed-size, non-flexing, clipped container whose own size cannot change due to
/// a descendant — then re-solves just that subtree (<see cref="FlexLayout.RunSubtree"/>). The boundary is the firewall:
/// a setState deep inside a fixed-size card relayouts only the card, never the page. Falls back to a full layout from the
/// root when a dirty node has no bounded ancestor.
/// </summary>
public sealed class LayoutInvalidator
{
    private readonly SceneStore _scene;
    private readonly FlexLayout _layout;
    private readonly List<NodeHandle> _roots = new();

    public LayoutInvalidator(SceneStore scene, FlexLayout layout)
    {
        _scene = scene;
        _layout = layout;
    }

    /// <summary>A fixed-size, non-flexing, overflow-clipping container — its size is independent of its children, so the
    /// up-rule stops here (layout.md §4.3). (AspectRatio is not modeled in the live LayoutInput; add when it lands.)</summary>
    private static bool IsLayoutBoundary(in LayoutInput s, NodeFlags f)
        => (f & NodeFlags.LayoutBoundary) != 0
        || (!float.IsNaN(s.Width) && !float.IsNaN(s.Height)
        && s.FlexGrow == 0f && s.FlexShrink == 0f
        && (f & NodeFlags.ClipsToBounds) != 0);

    private NodeHandle FindRelayoutRoot(NodeHandle node)
    {
        var cur = node;
        while (true)
        {
            if (cur == _scene.Root) return cur;
            if (_scene.HasScroll(cur)) return cur;                          // a scroll/virtual viewport is a boundary (§4.3, §6)
            if (IsLayoutBoundary(_scene.Layout(cur), _scene.Flags(cur))) return cur;
            var parent = _scene.Parent(cur);
            if (parent.IsNull) return cur;
            cur = parent;
        }
    }

    /// <summary>Re-solve only the subtrees affected by this frame's LayoutDirty marks. O(dirty), firewalled at boundaries.</summary>
    public void RunDirty(Size2 window)
    {
        var dirty = _scene.LayoutDirtyNodes;
        if (dirty.Count == 0) return;

        _roots.Clear();
        for (int i = 0; i < dirty.Count; i++)
        {
            var n = dirty[i];
            if (!_scene.IsLive(n)) continue;
            var root = FindRelayoutRoot(n);
            if (_scene.IsLive(root) && !_roots.Contains(root)) _roots.Add(root);
        }

        // Running an ancestor root and a descendant root is harmless (layout is idempotent); we keep it simple and
        // just dedupe exact roots. The root case re-solves against the window; others reflow against their own bounds.
        for (int i = 0; i < _roots.Count; i++)
        {
            var r = _roots[i];
            if (!_scene.IsLive(r)) continue;
            if (r == _scene.Root) _layout.Run(r, window);
            else _layout.RunSubtree(r);
        }
    }
}
