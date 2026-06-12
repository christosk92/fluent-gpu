using FluentGpu.Foundation;

namespace FluentGpu.Hooks;

/// <summary>
/// The <c>UseGesture</c> hook (input-a11y.md §13) — a CONFIG-ONLY declaration that enrolls a gesture-arena member on
/// this component's node (<see cref="RenderContext.HostNode"/>) and routes the arena WINNER's event to the handler. It
/// owns no render output and triggers no re-render: the declaration is written into the
/// <c>SceneStore</c> gesture column (the Hooks⇄Input seam — both reference Scene, neither references the other), and the
/// dispatcher fires the handler when the gesture arena (§7A) resolves this node's matching member.
///
/// <para><b>Pattern (matches the engine's other node-targeting hooks — <c>UseSpring</c>/<c>UseTransition</c>):</b> a
/// mount-once <c>UseLayoutEffect</c> keyed by the gesture KIND writes a STABLE forwarder onto the node (re-run only if
/// the kind changes); the forwarder dispatches to the LATEST handler held in a persistent <c>UseRef</c> cell, so passing
/// a fresh lambda each render needs no re-registration and allocates nothing per render. The layout-effect runs at
/// phase 6.5, when <see cref="RenderContext.HostNode"/> is a valid, mounted node (Bounds resolved). On unmount the node
/// is freed and <c>SceneStore</c> drops the gesture column with it; a kind-change re-target clears the prior install.</para>
///
/// <para><b>Phase-3 usable kinds:</b> <see cref="GestureType.Tap"/>, <see cref="GestureType.Hold"/>,
/// <see cref="GestureType.Pan"/> (the args carry the gesture <c>Position</c>, and the Pan end carries <c>Velocity</c>).
/// The reserved kinds (DoubleTap / RightTap / Drag / Pinch) are accepted for forward-compatibility but not yet routed —
/// their wiring is Phase-4 surface (pinch-zoom, the double/right-tap routing).</para>
/// </summary>
public static class GestureHooks
{
    /// <summary>Declare a gesture handler on this component's node (§13). Stable call order (hook); config-only — no
    /// render output, no re-render, no per-render allocation. The latest <paramref name="handler"/> is always the one
    /// invoked (held in a ref cell), so the call site may pass a fresh lambda each render.</summary>
    public static void UseGesture(this Component c, GestureType kind, Action<GestureEventArgs> handler)
        => c.Context.UseGesture(kind, handler);
}
