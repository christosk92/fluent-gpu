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

    /// <summary>Relayout escapes counted since the last <see cref="BeginFrame"/> — surfaced as
    /// <c>FrameStats.RootRelayoutEscapes</c>. Always-on (no DEBUG gate); the throttled human message is the DEBUG part.</summary>
    public int EscapesThisFrame { get; private set; }

    /// <summary>DEBUG-only best-effort node→key resolver for the escape message (wired by the host to the reconciler).
    /// Invoked only inside the throttled, FG_DIAG-gated message path, so it costs nothing on Release / when quiet.</summary>
    public Func<NodeHandle, string?>? DebugKeyResolver;

    private double _frameNowMs;                       // the host frame clock at frame start (message throttle uses it, not wall time)
    private Dictionary<int, double>? _escapeReportedAtMs;   // DEBUG throttle: node idx → last-reported frame-ms (lazy; DEBUG only)

    public LayoutInvalidator(SceneStore scene, FlexLayout layout)
    {
        _scene = scene;
        _layout = layout;
    }

    /// <summary>Frame start: reset the per-frame escape counter and stamp the frame clock (for the throttled diagnostic).
    /// The host calls this once per frame before any scoped relayout, alongside <c>FlexLayout.ResetFrameDiagCounters</c>.</summary>
    public void BeginFrame(double frameNowMs)
    {
        EscapesThisFrame = 0;
        _frameNowMs = frameNowMs;
    }

    /// <summary>A fixed-size, non-flexing, overflow-clipping container — its size is independent of its children, so the
    /// up-rule stops here (layout.md §4.3). An aspect-sized box (LayoutInput.AspectRatio set) is intentionally NOT a
    /// boundary: it leaves one of Width/Height NaN (the derived extent), so it never satisfies the both-explicit test.</summary>
    private static bool IsLayoutBoundary(in LayoutInput s, NodeFlags f)
        => (f & NodeFlags.LayoutBoundary) != 0
        || (!float.IsNaN(s.Width) && !float.IsNaN(s.Height)
        && s.FlexGrow == 0f && s.FlexShrink == 0f
        && (f & NodeFlags.ClipsToBounds) != 0);

    private NodeHandle FindRelayoutRoot(NodeHandle node, out int depth)
    {
        var cur = node;
        depth = 0;                                                          // steps walked = the dirty node's tree-depth when the walk reaches the root
        while (true)
        {
            if (cur == _scene.Root) return cur;
            if (_scene.HasScroll(cur)) return cur;                          // a scroll/virtual viewport is a boundary (§4.3, §6)
            if (IsLayoutBoundary(_scene.Layout(cur), _scene.Flags(cur))) return cur;
            var parent = _scene.Parent(cur);
            if (parent.IsNull) return cur;
            cur = parent;
            depth++;
        }
    }

    // A dirty node deeper than a direct child (depth > 1) whose relayout search found no boundary and fell back to the
    // scene root: a full-subtree relayout that a fixed-size ClipToBounds boundary (or `.Boundary()`) would have firewalled.
    // Always counts; the human message is throttled (once per offending node per ~1s of frame time) and FG_DIAG-gated.
    private void NoteEscape(NodeHandle n)
    {
        EscapesThisFrame++;
        if (!Diag.CompiledIn || !Diag.Enabled) return;
        int idx = (int)n.Raw.Index;
        _escapeReportedAtMs ??= new Dictionary<int, double>();
        if (_escapeReportedAtMs.TryGetValue(idx, out double last) && _frameNowMs - last < 1000.0) return;
        _escapeReportedAtMs[idx] = _frameNowMs;
        ushort typeId = _scene.ElementTypeId(n);
        string key = DebugKeyResolver?.Invoke(n) ?? "(none)";
        Diag.Event("layout", $"relayout escaped to root from node #{idx} (type {typeId}, key {key}) — add a fixed-size ClipToBounds boundary or .Boundary()");
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
            var root = FindRelayoutRoot(n, out int depth);
            if (_scene.IsLive(root) && !_roots.Contains(root)) _roots.Add(root);
            if (root == _scene.Root && depth > 1) NoteEscape(n);   // escaped past every boundary to the scene root

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
