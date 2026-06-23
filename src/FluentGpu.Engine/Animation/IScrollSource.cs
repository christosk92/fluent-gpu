using FluentGpu.Foundation;
using FluentGpu.Scene;

namespace FluentGpu.Animation;

/// <summary>
/// The engine services an OS-backed scroll source needs from the host: the single clamp chokepoint writers
/// (<see cref="WriteScrollOffset"/> / <see cref="WriteOverscroll"/> — the SAME <c>SetScrollOffset</c> path the
/// integrator uses, so the clamp/translation-only/virtual-re-realize contract is never bypassed), the engaged-viewport
/// resolver at a press point, and a frame request to keep frames flowing during an OS glide. <see cref="AppHost"/>
/// implements this; the integrator path does not use it. POD/NodeHandle-only — no TerraFX/COM.
/// </summary>
public interface IScrollHost
{
    /// <summary>The retained scene the source reads viewport geometry from.</summary>
    SceneStore Scene { get; }

    /// <summary>Write an ABSOLUTE scroll offset through the Input chokepoint (clamp + content <c>-offset</c> transform +
    /// virtual re-realize), returning whether the viewport actually moved. The OS source re-applies its polled position
    /// through here so <c>SetScrollOffset</c> stays the SOLE clamp authority (the source never writes the transform itself).</summary>
    bool WriteScrollOffset(NodeHandle viewport, float absoluteOffset);

    /// <summary>Write the rubber-band overscroll DISPLACEMENT (visual only; the offset is untouched) through the chokepoint.</summary>
    void WriteOverscroll(NodeHandle viewport, float overscrollBandPx);

    /// <summary>Nearest scrollable viewport under a window-space point (for binding an OS manipulation to a viewport at
    /// contact-down).</summary>
    NodeHandle ScrollableUnder(Point2 windowPt);

    /// <summary>The viewport an OS manipulation should bind for a pan/wheel on a KNOWN axis — the nearest scrollable
    /// ancestor whose orientation matches <paramref name="horizontal"/> AND has room to move, climbing PAST a cross-axis
    /// inner scroller (so a vertical pan over a horizontal shelf reaches the vertical page behind it instead of being
    /// eaten by the shelf). Mirrors the wheel notch path's <c>ScrollAxis</c> routing so DM and the fallback agree on the
    /// target. Null ⇒ nothing on this axis is scrollable under the point (the caller lets the wheel fall through).</summary>
    NodeHandle ScrollableUnderForAxis(Point2 windowPt, bool horizontal);

    /// <summary>Request another frame (keep the loop alive while an OS manipulation/inertia is running).</summary>
    void RequestFrame();
}

/// <summary>
/// A per-frame scroll position source behind the engine's scroll seam. The DEFAULT is the deterministic
/// <see cref="IntegratorScrollSource"/> (the <see cref="ScrollAnimator"/>); a platform backend MAY supply an
/// OS-manipulation source (Windows DirectManipulation, manual-update mode) that consumes OS position/velocity/inertia
/// and re-applies it through the SAME Input chokepoint via <see cref="IScrollHost"/>. POD/NodeHandle-only — no
/// TerraFX/COM, so the concrete OS type stays confined to the platform backend and the portable engine /
/// <c>FluentGpu.Controls</c> / <c>FluentGpu.VerticalSlice</c> closure stays TerraFX-free.
/// </summary>
public interface IScrollSource
{
    /// <summary>Advance this source's owned viewports for the frame, writing any offset/band through the chokepoint.</summary>
    void Tick(float dtMs);

    /// <summary>True while this source needs more frames (an active manipulation, inertia glide, or settling spring).</summary>
    bool HasActive { get; }

    /// <summary>KeepAlive park edge: freeze/resume an owned viewport (a backgrounded tab must not keep the loop awake).
    /// No-op for a viewport this source does not own.</summary>
    void SetNodeParked(NodeHandle viewport, bool parked);

    /// <summary>A scene slot was freed — drop any per-index state this source kept for it.</summary>
    void ClearForIndex(int index);
}

/// <summary>The default scroll source: a thin adapter over the deterministic <see cref="ScrollAnimator"/> (the wheel
/// TargetChase, the touch fling, the overscroll spring, and the conscious scrollbar). This is the ONLY source headless
/// and on non-Windows; its behavior is byte-identical to calling the animator directly.</summary>
public sealed class IntegratorScrollSource : IScrollSource
{
    private readonly ScrollAnimator _anim;
    public IntegratorScrollSource(ScrollAnimator anim) => _anim = anim;

    public void Tick(float dtMs) => _anim.Tick(dtMs);
    public bool HasActive => _anim.HasActive;
    public void SetNodeParked(NodeHandle viewport, bool parked) => _anim.SetNodeParked(viewport, parked);
    public void ClearForIndex(int index) => _anim.ClearForIndex(index);
}

/// <summary>
/// Composes the always-present deterministic integrator with an OPTIONAL OS-manipulation source. With no OS source
/// (headless / non-Windows / DM not engaged) this is byte-identical to ticking the integrator alone — the OS branch is
/// a constant-null test, so the single Mutate chokepoint, translation-only scroll, and zero-alloc properties are
/// preserved by construction. When present, the OS source advances FIRST (it polled its manipulation in the input pump)
/// and writes its owned viewports through the chokepoint; the integrator then advances its own (wheel/fling/spring)
/// viewports. The two sets are disjoint by construction — a touch contact is owned by exactly one (the integrator is
/// never armed for a DM-owned contact; <c>SeedScrollFling</c> hands the touch-up velocity to whichever owns it).
/// </summary>
public sealed class ScrollSourceMux
{
    private readonly IScrollSource _integrator;
    private readonly IScrollSource? _os;

    public ScrollSourceMux(IScrollSource integrator, IScrollSource? os)
    {
        _integrator = integrator;
        _os = os;
    }

    /// <summary>True when an OS-manipulation source is wired (Windows DM); false = integrator only (the headless guarantee).</summary>
    public bool HasOsSource => _os is not null;

    public void Pump(float dtMs)
    {
        _os?.Tick(dtMs);          // poll + write OS-owned viewports (Phase 3); null on headless → no-op, byte-identical
        _integrator.Tick(dtMs);   // advance integrator-owned (wheel/fling/spring/scrollbar) viewports
    }

    public bool HasActive => _integrator.HasActive || (_os?.HasActive ?? false);

    public void SetNodeParked(NodeHandle viewport, bool parked)
    {
        _integrator.SetNodeParked(viewport, parked);
        _os?.SetNodeParked(viewport, parked);
    }

    public void ClearForIndex(int index)
    {
        _integrator.ClearForIndex(index);
        _os?.ClearForIndex(index);
    }
}
