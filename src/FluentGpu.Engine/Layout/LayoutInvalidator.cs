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
    private readonly List<NodeHandle> _escaped = new();   // this frame's boundary-less marks, pending the size-stable probe

    /// <summary>Relayout escapes counted since the last <see cref="BeginFrame"/> — surfaced as
    /// <c>FrameStats.RootRelayoutEscapes</c>. Always-on (no DEBUG gate); the throttled human message is the DEBUG part.
    /// Counts the boundary-search outcome (a mark that found no firewall), NOT the cost that followed: an escape that the
    /// size-stable early-out resolved locally still counts here and is also counted in <see cref="LocalResolvesThisFrame"/>.</summary>
    public int EscapesThisFrame { get; private set; }

    /// <summary>Of <see cref="EscapesThisFrame"/>: marks proven size-stable and re-solved in place, i.e. full-window
    /// solves NOT paid. escapes==localResolves on a frame means every escape was absorbed (no root solve at all).</summary>
    public int LocalResolvesThisFrame { get; private set; }

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
        LocalResolvesThisFrame = 0;
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

    /// <summary>A scroll viewport is a relayout firewall only when its main-axis size is parent-owned. Auto-main,
    /// non-growing ItemsView presenters intentionally report their content extent to an outer page scroller; stopping
    /// there would leave that parent on the previous extent while a measured row reflows (album drawer snap/overlap).</summary>
    private bool IsHardScrollBoundary(NodeHandle node, in LayoutInput s)
    {
        if (!_scene.TryGetScroll(node, out var sc)) return false;
        if (sc.ContentSized) return true;                                  // popup presenters own their clamped extent
        bool explicitMain = sc.Orientation == 1 ? !float.IsNaN(s.Width) : !float.IsNaN(s.Height);
        return s.FlexGrow != 0f || explicitMain;
    }

    private NodeHandle FindRelayoutRoot(NodeHandle node, out int depth)
    {
        var cur = node;
        depth = 0;                                                          // steps walked = the dirty node's tree-depth when the walk reaches the root
        while (true)
        {
            if (cur == _scene.Root) return cur;
            ref LayoutInput input = ref _scene.Layout(cur);
            if (IsLayoutBoundary(input, _scene.Flags(cur))) return cur;
            if (IsHardScrollBoundary(cur, input)) return cur;               // fixed/filling viewport owns overflow (§4.3, §6)
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
        _escaped.Clear();
        for (int i = 0; i < dirty.Count; i++)
        {
            var n = dirty[i];
            if (!_scene.IsLive(n)) continue;
            var root = FindRelayoutRoot(n, out int depth);
            if (root == _scene.Root && depth > 1)
            {
                NoteEscape(n);                                    // escaped past every boundary to the scene root
                // …but an escape is only a boundary-search VERDICT, not proof that the WINDOW must be re-solved. Defer:
                // the size-stable early-out below can re-solve such a mark in place. Deferred (not decided here) so that a
                // frame which ends up needing the root solve anyway never pays for a probe the full solve would redo.
                _escaped.Add(n); continue;
            }
            if (_scene.IsLive(root) && !_roots.Contains(root)) _roots.Add(root);
        }

        // Escaped marks: re-measure ONLY that subtree at the width its parent offered last pass. If its outer size and its
        // parent-facing layout inputs are unchanged, no ancestor's numbers can move, so the subtree is re-solved in place
        // and the whole-window solve is skipped (FlexLayout.TryResolveSizeStable). The first mark that cannot be proven
        // local schedules the root solve — which subsumes every remaining escape, so we stop probing at that point.
        if (_escaped.Count > 0)
        {
            bool rootScheduled = _roots.Contains(_scene.Root);
            for (int i = 0; i < _escaped.Count && !rootScheduled; i++)
            {
                var n = _escaped[i];
                if (!_scene.IsLive(n)) continue;
                if (_layout.TryResolveSizeStable(n)) LocalResolvesThisFrame++;
                else rootScheduled = true;
            }
            if (rootScheduled && !_roots.Contains(_scene.Root)) _roots.Add(_scene.Root);
            _escaped.Clear();
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
