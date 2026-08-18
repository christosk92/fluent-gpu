using System;
using FluentGpu.Foundation;
using FluentGpu.Scene;
using FluentGpu.Signals;

namespace FluentGpu.Scroll;

/// <summary>How a <see cref="ScrollController"/>-issued motion command reaches its target (scroll-v3-plan §7.2):
/// <see cref="Immediate"/> posts the kernel's <c>Immediate</c> flag (a snap, resolved on the next Reclamp — no
/// spring); <see cref="Glide"/> posts a Driven chase with a distance-derived half-life (the kernel's own
/// <c>ScrollFeel.ProgrammaticHalflifeS</c>), or the caller's own constants via the <see cref="ScrollGlide"/>
/// overload.</summary>
public enum ScrollAnimate : byte { Immediate, Glide }

/// <summary>Pinned Driven-chase constants for <see cref="ScrollController.ScrollTo(float, ScrollGlide)"/> — the
/// escape hatch a caller with an already-tuned spring uses instead of the kernel's distance-derived default (the
/// LyricsView follow-scroll case: a re-target mid-chase must keep the carried velocity with THESE ζ/ω, not
/// whatever the kernel would otherwise pick for the travel distance).</summary>
public readonly record struct ScrollGlide(float HalflifeMs, float Zeta, float Omega, float SettleVel);

/// <summary>The ONE authoring handle over a live scroll viewport (scroll-v3-plan §7.2) — a thin, allocation-light
/// façade over kernel commands (<c>ScrollPort.Post</c>) and the existing geometry-observer mechanism
/// (<see cref="FluentGpu.Scene.SceneStore.SetScrollObserver"/>). Never writes scene state directly (single-writer:
/// the kernel + <see cref="SceneScrollSink"/> own every scroll write) — every mutating call here is a POST, not a
/// write.
///
/// <para>Portable (Engine-only, no TerraFX). A controller is bound to AT MOST ONE live viewport node at a time via
/// <see cref="Attach"/> — the reconciler attaches an internal or author-supplied instance when a <c>ScrollEl</c>/
/// <c>VirtualListEl</c> realizes (see <see cref="FluentGpu.Hooks.Hooks.UseScroll"/>) and detaches it on unmount /
/// re-bake. Calling a mutator while detached is a silent no-op — a controller a caller is still holding after its
/// viewport unmounted must not throw or resurrect a dead node.</para></summary>
public sealed class ScrollController
{
    private SceneStore? _scene;
    private NodeHandle _node = NodeHandle.Null;
    private Action? _wake;
    private readonly FloatSignal _offsetSignal = new(0f);

    /// <summary>Main-axis offset (DIP), published — i.e. the backing signal is written — only when
    /// <see cref="Refresh"/> observes it actually changed (edge-driven from <see cref="SceneScrollSink.Apply"/> via
    /// <see cref="ScrollControllerRegistry.NotifyMoved"/>, not a per-frame poll).</summary>
    public IReadSignal<float> Offset => _offsetSignal;
    /// <summary>Zoom-scaled content extent on the main axis (DIP). 0 while detached / before the first geometry.</summary>
    public float Extent { get; private set; }
    /// <summary>Viewport size on the main axis (DIP). 0 while detached / before the first geometry.</summary>
    public float Viewport { get; private set; }
    /// <summary>True only for real user-driven motion (mirrors <c>ScrollState.UserScrollActive</c>) — false for a
    /// programmatic <see cref="ScrollTo(float, ScrollAnimate)"/>/<see cref="BringIntoView"/> chase and at rest.</summary>
    public bool UserScrolling { get; private set; }

    /// <summary>True once bound to a live viewport node (<see cref="Attach"/> called, not yet <see cref="Detach"/>'d).</summary>
    public bool IsAttached => _scene is not null && !_node.IsNull;

    /// <summary>Bind this controller to <paramref name="node"/>'s live <c>ScrollState</c> (a controller may be
    /// attached to only one viewport at a time — re-attaching detaches the previous one first). <paramref name="wake"/>
    /// is invoked after every posted command so the host schedules a frame without a full re-render (the same
    /// contract <c>ScrollIntoView</c> uses via <c>RequestFrame</c>) — pass the reconciler's own <c>RequestFrame</c>.</summary>
    public void Attach(SceneStore scene, NodeHandle node, Action? wake = null)
    {
        ArgumentNullException.ThrowIfNull(scene);
        Detach();
        _scene = scene;
        _node = node;
        _wake = wake;
        if (!node.IsNull) scene.ScrollControllers.Register((int)node.Raw.Index, this);
        Refresh();
    }

    /// <summary>Unbind from the live viewport (unmount / re-bake to a different node). Idempotent.</summary>
    public void Detach()
    {
        if (_scene is { } s && !_node.IsNull) s.ScrollControllers.Unregister((int)_node.Raw.Index, this);
        _scene = null;
        _node = NodeHandle.Null;
        _wake = null;
    }

    /// <summary>Re-read <c>Offset</c>/<c>Extent</c>/<c>Viewport</c>/<c>UserScrolling</c> from the live
    /// <c>ScrollState</c>. Called by <see cref="Attach"/> and by <see cref="ScrollControllerRegistry.NotifyMoved"/>
    /// (edge-driven off <see cref="SceneScrollSink.Apply"/> — never a per-frame poll). A no-op while detached or the
    /// bound node is no longer live (e.g. the unmount race between the sink's last Apply and this controller's own
    /// Detach).</summary>
    internal void Refresh()
    {
        if (_scene is not { } scene || _node.IsNull || !scene.IsLive(_node) || !scene.HasScroll(_node)) return;
        ref ScrollState sc = ref scene.ScrollRef(_node);
        bool horizontal = sc.Orientation == 1;
        float zoom = sc.ZoomFactor > 0f ? sc.ZoomFactor : 1f;
        _offsetSignal.SetIfChanged(horizontal ? sc.OffsetX : sc.OffsetY);
        Extent = (horizontal ? sc.ContentW : sc.ContentH) * zoom;
        Viewport = horizontal ? sc.ViewportW : sc.ViewportH;
        UserScrolling = sc.UserScrollActive;
    }

    /// <summary>Move to an absolute main-axis offset (content-space DIP). The kernel clamps to
    /// <c>[0, content·zoom − viewport]</c> — no pre-clamp needed here. A no-op while detached.</summary>
    public void ScrollTo(float offset, ScrollAnimate animate = ScrollAnimate.Glide)
    {
        if (_scene is not { } scene || _node.IsNull) return;
        scene.ScrollPort?.Post(ScrollInput.ScrollTo((int)_node.Raw.Index, offset, immediate: animate == ScrollAnimate.Immediate));
        _wake?.Invoke();
    }

    /// <summary>Move to an absolute offset with EXPLICIT Driven-chase constants (the LyricsView follow-scroll case —
    /// see <see cref="ScrollGlide"/>). Always a chase (never immediate); re-targeting an already-running chase for
    /// this node keeps its carried velocity (kernel Driven retarget-in-place).</summary>
    public void ScrollTo(float offset, ScrollGlide glide)
    {
        if (_scene is not { } scene || _node.IsNull) return;
        scene.ScrollPort?.Post(ScrollInput.ScrollTo((int)_node.Raw.Index, offset, immediate: false,
            halflifeMs: glide.HalflifeMs, zeta: glide.Zeta, omega: glide.Omega, settleVel: glide.SettleVel));
        _wake?.Invoke();
    }

    /// <summary>Move by a relative delta from the LIVE offset (the kernel resolves <c>current + delta</c>, then
    /// clamps) — PageUp/PageDown/Home/End and wheel-alike chrome. A no-op while detached.</summary>
    public void ScrollBy(float delta, ScrollAnimate animate = ScrollAnimate.Glide)
    {
        if (_scene is not { } scene || _node.IsNull) return;
        scene.ScrollPort?.Post(ScrollInput.ScrollBy((int)_node.Raw.Index, delta, immediate: animate == ScrollAnimate.Immediate));
        _wake?.Invoke();
    }

    /// <summary>Bring <paramref name="node"/> (a descendant of the attached viewport) into view — the
    /// <c>ScrollIntoView.Bring/BringInto</c> replacement (rect→offset math factored into <see cref="ScrollTargets"/>).
    /// <paramref name="alignmentRatio"/> NaN (default) = MINIMAL scroll: a no-op when already visible; 0/1 park the
    /// leading/trailing edge at the matching viewport edge. A no-op while detached, when <paramref name="node"/> is
    /// null/not live, or when the node is already visible under the minimal-scroll rule.</summary>
    public void BringIntoView(NodeHandle node, float alignmentRatio = float.NaN, ScrollAnimate animate = ScrollAnimate.Glide)
    {
        if (_scene is not { } scene || _node.IsNull || node.IsNull) return;
        if (!ScrollTargets.TryResolve(scene, _node, node, 0f, alignmentRatio, out float target)) return;
        ScrollTo(target, animate);
    }

    /// <summary>Change-only scroll-geometry observer on the attached viewport (the escape hatch;
    /// <see cref="FluentGpu.Scene.SceneStore.SetScrollObserver"/>). Replaces any observer this controller's viewport
    /// already had. A no-op while detached.</summary>
    public void OnGeometry(Func<FluentGpu.Animation.ScrollGeometry, long> project, Action<FluentGpu.Animation.ScrollGeometry> action)
    {
        if (_scene is not { } scene || _node.IsNull) return;
        scene.SetScrollObserver(_node, project, action);
    }
}

/// <summary>Pure rect→offset math for "bring this node into view" (scroll-v3-plan §7.2 — factored out of the pre-v3
/// <c>ScrollIntoView.BringInto</c> so <see cref="ScrollController.BringIntoView"/> needs no <c>RenderContext</c>).
/// VIEWPORT-RELATIVE bounds (not absolute), so this resolves correctly nested inside another scroller or under a
/// sticky/morph ancestor — the live -offset transform is already folded into both rects.</summary>
public static class ScrollTargets
{
    /// <summary>Resolve the content-space offset that brings <paramref name="node"/> into <paramref name="viewport"/>'s
    /// view. <paramref name="alignmentRatio"/> NaN = MINIMAL scroll (false when already visible); 0..1 parks the
    /// leading..trailing edge at the matching viewport edge (clamped). Returns false (no <paramref name="target"/>)
    /// when either node is not live, the viewport has no scroll state, its geometry has not published yet, or the
    /// minimal-scroll rule finds nothing to do. The RAW target is NOT clamped to the scroller's max offset — the
    /// kernel clamps on receipt of the resulting <c>ScrollTo</c>.</summary>
    public static bool TryResolve(SceneStore scene, NodeHandle viewport, NodeHandle node, float margin, float alignmentRatio, out float target)
    {
        target = 0f;
        if (scene is null || viewport.IsNull || node.IsNull) return false;
        if (!scene.IsLive(viewport) || !scene.IsLive(node) || !scene.HasScroll(viewport)) return false;

        ref ScrollState sc = ref scene.ScrollRef(viewport);
        bool horizontal = sc.Orientation != 0;
        float viewportExtent = horizontal ? sc.ViewportW : sc.ViewportH;
        if (viewportExtent <= 1f) return false;                 // geometry not published yet — the caller should re-post

        RectF nodeAbs = scene.AbsoluteRect(node);
        RectF vpAbs = scene.AbsoluteRect(viewport);
        float lead = horizontal ? nodeAbs.X - vpAbs.X : nodeAbs.Y - vpAbs.Y;
        float extent = horizontal ? nodeAbs.W : nodeAbs.H;
        float offset = horizontal ? sc.OffsetX : sc.OffsetY;

        if (float.IsNaN(alignmentRatio))
        {
            float trail = lead + extent;
            if (lead < margin) target = offset + lead - margin;
            else if (trail > viewportExtent - margin) target = offset + trail - viewportExtent + margin;
            else return false;                                  // already fully visible — minimal scroll does nothing
        }
        else
        {
            float ratio = Math.Clamp(alignmentRatio, 0f, 1f);
            float slack = MathF.Max(0f, viewportExtent - extent - margin * 2f);
            target = offset + lead - margin - ratio * slack;
        }
        return true;
    }
}

/// <summary>Node-index → live <see cref="ScrollController"/> side-table (scroll-v3-plan §7.2), one member on
/// <see cref="FluentGpu.Scene.SceneStore"/> (<c>SceneStore.ScrollControllers</c>). <see cref="SceneScrollSink.Apply"/>
/// calls <see cref="NotifyMoved"/> once per body that moved this pass so the attached controller's
/// <see cref="ScrollController.Offset"/> signal (and Extent/Viewport/UserScrolling) republish EDGE-DRIVEN — no
/// per-frame poll, no allocation on the hot path (a plain dictionary lookup).</summary>
public sealed class ScrollControllerRegistry
{
    private readonly System.Collections.Generic.Dictionary<int, ScrollController> _byNode = new();

    internal void Register(int nodeIndex, ScrollController controller) => _byNode[nodeIndex] = controller;

    internal void Unregister(int nodeIndex, ScrollController controller)
    {
        if (_byNode.TryGetValue(nodeIndex, out var live) && ReferenceEquals(live, controller))
            _byNode.Remove(nodeIndex);
    }

    /// <summary>Refresh the controller attached to <paramref name="nodeIndex"/>, if any. Called from
    /// <see cref="SceneScrollSink.Apply"/> — the ONE place a kernel write becomes scene state.</summary>
    public void NotifyMoved(int nodeIndex)
    {
        if (_byNode.TryGetValue(nodeIndex, out var controller)) controller.Refresh();
    }
}
