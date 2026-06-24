using FluentGpu.Animation;
using FluentGpu.Foundation;

namespace FluentGpu.Dsl;

/// <summary>A two-anchor active scroll interval — the authoring form of <see cref="FluentGpu.Animation.ScrollBind"/>'s
/// range. The reconciler bakes the anchors to two scroll-px bounds <c>(a,b)</c> (literal-px at reconcile; geometry
/// anchors at <c>ArrangeViewport</c>), from which the per-frame eval derives <c>t = clamp01((sample − a)/(b − a))</c>.
/// A <c>default</c> range (<see cref="HasValue"/> = false) means "the whole scroller" — <c>[0, maxOffset]</c>.</summary>
public readonly record struct ScrollRange
{
    public ScrollBindAnchor A { get; init; }
    public float Av { get; init; }
    public ScrollBindAnchor B { get; init; }
    public float Bv { get; init; }
    /// <summary>False for a <c>default</c> range (⇒ whole scroller); true once authored via a factory below.</summary>
    public bool HasValue { get; init; }

    /// <summary>Literal scroll-px window <c>[a, b]</c>.</summary>
    public static ScrollRange Px(float a, float b)
        => new() { A = ScrollBindAnchor.OffsetPx, Av = a, B = ScrollBindAnchor.OffsetPx, Bv = b, HasValue = true };
    /// <summary>Fraction of the scroller's max offset (0..1 ⇒ 0..maxOffset).</summary>
    public static ScrollRange Frac(float a, float b)
        => new() { A = ScrollBindAnchor.OffsetFrac, Av = a, B = ScrollBindAnchor.OffsetFrac, Bv = b, HasValue = true };
    /// <summary>The overscroll band itself is the sample; bakes to <c>[0, BandLimit]</c>.</summary>
    public static ScrollRange Overscroll
        => new() { A = ScrollBindAnchor.OverscrollBand, B = ScrollBindAnchor.OverscrollBand, HasValue = true };
    /// <summary>The target node entering→exiting the viewport (SwiftUI/CSS view-timeline cover range).</summary>
    public static ScrollRange Enter
        => new() { A = ScrollBindAnchor.NodeEnterViewport, B = ScrollBindAnchor.NodeExitViewport, HasValue = true };
}

/// <summary>One authored scroll-driven binding on an element (the declarative form of <see cref="FluentGpu.Animation.ScrollBind"/>).
/// The generic, hookable scroll surface: bind any compositor property to a normalized scroll progress, pin it (sticky),
/// or stretch it (overscroll hero). The reconciler compiles each entry to a POD <see cref="FluentGpu.Animation.ScrollBind"/>
/// row evaluated allocation-free in the frame loop.</summary>
public readonly record struct ScrollBindDsl
{
    /// <summary>Which scroller scalar drives this binding (offset / overscroll band / velocity / per-item signed phase).</summary>
    public ScrollChannel From { get; init; }
    /// <summary>Which compositor property this binding writes (transform / opacity / clip / presented size).</summary>
    public BindSink To { get; init; }
    /// <summary>The active scroll interval; omit for the whole scroller (<c>[0, maxOffset]</c>).</summary>
    public ScrollRange Range { get; init; }
    /// <summary>Output value at progress 0.</summary>
    public float OutStart { get; init; }
    /// <summary>Output value at progress 1.</summary>
    public float OutEnd { get; init; }
    /// <summary>Clamp progress to [0,1] (default). Clear for an extrapolating parallax that keeps translating past the range.</summary>
    public bool Clamp { get; init; }
    /// <summary>Shaping applied to progress before the output lerp (0 = linear).</summary>
    public Easing Ease { get; init; }

    // ── shorthands for the two re-expressed legacy behaviors ──
    /// <summary>Sticky: pin this node at the viewport top at this inset (replaces the old <c>StickyTop</c>).</summary>
    public float? PinTop { get; init; }
    /// <summary>Overscroll hero: scale uniformly from origin (0.5,0) by the top overscroll band, cancelling the band's
    /// content shift (replaces the old <c>ScrollStretchHeader</c>). The hero authors <c>TransformOriginX=0.5, Y=0</c>.</summary>
    public bool StretchFromTop { get; init; }

    // ── predicate channel hook (the CSS :stuck-style observable) ──
    /// <summary>Fires once per edge flip of the watched flag (UI-thread, never per-frame). For a <see cref="PinTop"/>
    /// bind it observes THIS node's pinned state; otherwise it observes <see cref="FlagBit"/> of the scroller's flags.</summary>
    public Action<bool>? OnFlag { get; init; }
    /// <summary>Which scroller flag bit <see cref="OnFlag"/> observes for a non-pin bind (e.g. <c>ScrollState.ScrolledFwdBit</c>,
    /// <c>MovingNowBit</c>). Ignored for a pin bind (it observes the node's own pinned transition).</summary>
    public byte FlagBit { get; init; }

    public ScrollBindDsl()
    {
        OutStart = 0f;
        OutEnd = 1f;
        Clamp = true;
    }
}
