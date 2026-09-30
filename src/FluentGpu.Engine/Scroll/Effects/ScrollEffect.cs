using FluentGpu.Foundation;

namespace FluentGpu.Scroll.Effects;

/// <summary>
/// The paint channel a <see cref="ScrollEffect"/> writes into. Kept separate from <see cref="EffectKind"/> (the
/// FORMULA) so the same channel (e.g. <see cref="TransY"/>) can be driven by a plain <see cref="EffectKind.Map"/> or
/// by a positional formula (<see cref="EffectKind.Sticky"/>) without the caller branching on shape.
/// </summary>
public enum EffectChannel : byte
{
    /// <summary>Horizontal translation (DIP, pre-device-snap).</summary>
    TransX,
    /// <summary>Vertical translation (DIP, pre-device-snap).</summary>
    TransY,
    /// <summary>Paint opacity, 0..1.</summary>
    Opacity,
    /// <summary>The node-local top edge of a clip rect (guillotine line), DIP.</summary>
    ClipTop,
    /// <summary>Uniform scale factor applied about the node's authored origin.</summary>
    ScaleXY,
    /// <summary>Scrollbar thumb offset along the track, DIP.</summary>
    ThumbPos,
    /// <summary>The node's PRESENTED height (<c>NodePaint.PresentedH</c>), DIP: the recorder draws the node's fill and
    /// clips a <c>ClipToBounds</c> node's children to it, and hit-testing / accessibility read it instead of the laid-out
    /// height — so a collapsing hero stops taking input below its presented edge without a relayout.</summary>
    PresentedH,
    /// <summary>The child-group vertical offset (<c>NodePaint.ChildShiftY</c>), DIP: every child's origin shifts by it
    /// while the node's own fill/clip stay put — the trailing anchor of a <see cref="EffectKind.Collapse"/>.</summary>
    ChildShiftY,
    /// <summary>The node-local BOTTOM edge of a clip whose top/left/right are open (<c>NodePaint.CollapseCut</c>), DIP —
    /// the leading anchor's cut of a <see cref="EffectKind.Collapse"/>: the recorder cuts the children (paint) and
    /// hit-testing cuts them (input) at the presented edge ONLY, so a child that draws above the node's top (an overscroll
    /// stretch) is not clipped the way a <c>ClipToBounds</c> box would clip it.</summary>
    ClipBottom,
}

/// <summary>Which edge of a collapsing node its CHILDREN stay attached to (<see cref="ScrollEffect.Collapse"/>).</summary>
public enum CollapseAnchor : byte
{
    /// <summary>Children stay put; only the presented extent shrinks from the bottom, and the children are cut — paint
    /// and input — at the presented edge by the node's own <see cref="EffectChannel.ClipBottom"/> row (no
    /// <c>ClipToBounds</c> needed: the cut is open above, left and right, so a <c>.StretchFromTop()</c> child still grows
    /// into a top overscroll; a <c>ClipToBounds</c> node keeps its full box clip, which cuts that stretch). The child
    /// presentation carries its own motion, e.g. a parallax that slides it away.</summary>
    Leading,
    /// <summary>Children ride the presented bottom edge (<see cref="EffectChannel.ChildShiftY"/> = presentedH − fullH):
    /// the content's END stays visible while the top is consumed.</summary>
    Trailing,
}

/// <summary>
/// The formula a <see cref="ScrollEffect"/> evaluates. See <see cref="ScrollEffectEval.Evaluate"/> for the one
/// arithmetic expression per kind (design doc §B.6): <see cref="Map"/> is a plain eased-and-clamped range map;
/// <see cref="Sticky"/> and <see cref="StickyClip"/> are the CSS position:sticky pin/clip pair, clamped to a scope
/// ancestor's end; <see cref="Thumb"/> is the scrollbar-thumb position formula.
/// </summary>
public enum EffectKind : byte
{
    /// <summary>Eased, clamped linear map of <c>offset ∈ [In0,In1]</c> to <c>[Out0,Out1]</c>.</summary>
    Map,
    /// <summary>CSS position:sticky translation, clamped so the node never scrolls past its scope's end.</summary>
    Sticky,
    /// <summary>The clip-rect dual of <see cref="Sticky"/>: guillotines content at the sticky line instead of
    /// translating the node to hold it.</summary>
    StickyClip,
    /// <summary>Scrollbar thumb position along its track.</summary>
    Thumb,
    /// <summary>Hero collapse: over <c>offset ∈ [In0,In1]</c> the presented height runs from the full height
    /// (<see cref="ScrollEffect.Out0"/>, NaN = the node's laid-out height) to <see cref="ScrollEffect.Out1"/>. On the
    /// <see cref="EffectChannel.PresentedH"/> channel it yields that height; on <see cref="EffectChannel.ChildShiftY"/>
    /// it yields <c>presentedH − fullH</c> (≤ 0 — the trailing anchor).</summary>
    Collapse,
    /// <summary>Top overscroll stretch: for a rubber-banded <c>offset &lt; 0</c> the node scales by
    /// <c>1 + (−offset)/NodeH</c> about its top-centre (0.5, 0) and translates by <c>offset</c>, so it fills the pulled
    /// gap from the viewport top down; identity at and past the content start.</summary>
    Stretch,
    /// <summary>A <see cref="Map"/> whose input is measured from the NODE, not the scroller: <c>x = offset + Inset −
    /// NodeY</c> (0 when the node's top meets the line <see cref="ScrollEffect.Inset"/> below the viewport edge). On
    /// <see cref="EffectChannel.TransY"/> the result is clamped to the scope's end (like <see cref="Sticky"/>), so a
    /// lagging header never drifts out of its own section. Used for node-relative parallax/fade anywhere in a long
    /// (or virtualized) list, where an absolute offset range can't be known at build time.</summary>
    MapFromNode,
}

/// <summary>
/// One scroll-linked effect binding: a pure function of the scroller's offset (and static <see cref="EffectGeometry"/>)
/// into one paint <see cref="EffectChannel"/>. Immutable POD — safe to evaluate from the UI thread (hit-test/a11y) and
/// the render thread (pixels) with byte-identical results (design doc §B.6: "same arithmetic on both threads").
/// <para>
/// <see cref="In0"/>/<see cref="In1"/> are the input domain for <see cref="EffectKind.Map"/> (offset range, content
/// coordinates, double precision); <see cref="Out0"/>/<see cref="Out1"/> are the output range for <see cref="Map"/>
/// (float — paint-space units); <see cref="Ease"/> only applies to <see cref="Map"/>; <see cref="Inset"/> is the
/// sticky/stickyClip pin offset from the viewport edge; <see cref="ScopeNode"/> is the containing-block ancestor a
/// sticky effect clamps against (opaque handle-shaped int — Wave 1's node-handle indirection; 0 = the effect's own
/// content root).
/// </para>
/// </summary>
public readonly record struct ScrollEffect(
    EffectChannel Channel,
    EffectKind Kind,
    double In0,
    double In1,
    float Out0,
    float Out1,
    Easing Ease,
    float Inset,
    int ScopeNode)
{
    /// <summary>CSS position:sticky: pins the node <paramref name="inset"/> DIP from the viewport's leading edge,
    /// releasing (clamping) once the node reaches the end of <paramref name="scopeNode"/>'s containing block.</summary>
    public static ScrollEffect Sticky(float inset, int scopeNode = 0)
        => new(EffectChannel.TransY, EffectKind.Sticky, 0.0, 0.0, 0f, 0f, Easing.Linear, inset, scopeNode);

    /// <summary>The clip-rect dual of <see cref="Sticky"/>: guillotines content at the sticky line instead of
    /// translating the node.</summary>
    public static ScrollEffect StickyClip(float inset)
        => new(EffectChannel.ClipTop, EffectKind.StickyClip, 0.0, 0.0, 0f, 0f, Easing.Linear, inset, 0);

    /// <summary>A vertical-translation parallax map: as <c>offset</c> runs <paramref name="in0"/>..<paramref name="in1"/>,
    /// <see cref="EffectChannel.TransY"/> runs <paramref name="out0"/>..<paramref name="out1"/>.</summary>
    public static ScrollEffect Parallax(double in0, double in1, float out0, float out1)
        => new(EffectChannel.TransY, EffectKind.Map, in0, in1, out0, out1, Easing.Linear, 0f, 0);

    /// <summary>Node-relative parallax (<see cref="EffectKind.MapFromNode"/>): once the node's top reaches
    /// <paramref name="inset"/> DIP below the viewport edge, it translates down by <paramref name="fraction"/> of the
    /// further scroll over the next <paramref name="over"/> DIP (0.5 = moves at half the content's speed), clamped to
    /// its scope's end.</summary>
    public static ScrollEffect ParallaxFromNode(float fraction, float over, float inset = 0f, int scopeNode = 0)
        => new(EffectChannel.TransY, EffectKind.MapFromNode, 0.0, over, 0f, over * fraction, Easing.Linear, inset, scopeNode);

    /// <summary>Node-relative fade (<see cref="EffectKind.MapFromNode"/>): opacity runs <paramref name="from"/> →
    /// <paramref name="to"/> as the node travels <paramref name="in0"/>..<paramref name="in1"/> DIP past the line
    /// <paramref name="inset"/> below the viewport edge.</summary>
    public static ScrollEffect FadeFromNode(double in0, double in1, float from, float to, float inset = 0f)
        => new(EffectChannel.Opacity, EffectKind.MapFromNode, in0, in1, from, to, Easing.Linear, inset, 0);

    /// <summary>An opacity fade map: as <c>offset</c> runs <paramref name="in0"/>..<paramref name="in1"/>,
    /// <see cref="EffectChannel.Opacity"/> runs <paramref name="from"/>..<paramref name="to"/>.</summary>
    public static ScrollEffect Fade(double in0, double in1, float from, float to)
        => new(EffectChannel.Opacity, EffectKind.Map, in0, in1, from, to, Easing.Linear, 0f, 0);

    /// <summary>A uniform-scale map: as <c>offset</c> runs <paramref name="in0"/>..<paramref name="in1"/>,
    /// <see cref="EffectChannel.ScaleXY"/> runs <paramref name="from"/>..<paramref name="to"/>.</summary>
    public static ScrollEffect Scale(double in0, double in1, float from, float to, Easing ease = Easing.Linear)
        => new(EffectChannel.ScaleXY, EffectKind.Map, in0, in1, from, to, ease, 0f, 0);

    /// <summary>Scrollbar thumb position along its track (design doc §B.8): posed render-side from the same
    /// arithmetic as everything else here.</summary>
    public static ScrollEffect Thumb()
        => new(EffectChannel.ThumbPos, EffectKind.Thumb, 0.0, 0.0, 0f, 0f, Easing.Linear, 0f, 0);

    /// <summary>Hero collapse, the PRESENTED-HEIGHT row: as the offset runs 0..<paramref name="over"/> the node's
    /// presented height runs from its laid-out height to <paramref name="minH"/> (clamped both ends; the full height at
    /// any overpan). Hit-testing and accessibility read the presented height, so the band below it reaches whatever
    /// lies beneath. Pair with <see cref="ScrollEffectDsl.Sticky{T}"/>(0) to hold the collapsing node at the top; the
    /// DSL <see cref="ScrollEffectDsl.Collapse{T}"/> adds the <see cref="CollapseChildShift"/> row for a trailing anchor
    /// and the <see cref="CollapseClip"/> row for a leading one.</summary>
    public static ScrollEffect Collapse(double over, float minH)
        => new(EffectChannel.PresentedH, EffectKind.Collapse, 0.0, over, float.NaN, minH, Easing.Linear, 0f, 0);

    /// <summary>Hero collapse, the TRAILING-ANCHOR row: <see cref="EffectChannel.ChildShiftY"/> = presentedH − fullH over
    /// the same 0..<paramref name="over"/> range as <see cref="Collapse"/>, so the children's bottom rides the presented
    /// edge.</summary>
    public static ScrollEffect CollapseChildShift(double over, float minH)
        => new(EffectChannel.ChildShiftY, EffectKind.Collapse, 0.0, over, float.NaN, minH, Easing.Linear, 0f, 0);

    /// <summary>Hero collapse, the LEADING-ANCHOR cut row: <see cref="EffectChannel.ClipBottom"/> = the presented height
    /// over the same 0..<paramref name="over"/> range as <see cref="Collapse"/>, so the children are cut (paint and input)
    /// at the presented edge and nowhere else — open above, so a stretching child still fills a top overscroll.</summary>
    public static ScrollEffect CollapseClip(double over, float minH)
        => new(EffectChannel.ClipBottom, EffectKind.Collapse, 0.0, over, float.NaN, minH, Easing.Linear, 0f, 0);

    /// <summary>Top overscroll stretch (the iOS/Spotify stretchy hero): while the viewport is rubber-banded past its
    /// start the node scales about its top-centre by <c>1 + pull/NodeH</c> and cancels the pull's content shift, so its
    /// top edge stays on the viewport top and it grows into the gap. Composes with the node's other transform effects
    /// (a parallax on the same node keeps its translation).</summary>
    public static ScrollEffect StretchFromTop()
        => new(EffectChannel.ScaleXY, EffectKind.Stretch, 0.0, 0.0, 0f, 0f, Easing.Linear, 0f, 0);
}
