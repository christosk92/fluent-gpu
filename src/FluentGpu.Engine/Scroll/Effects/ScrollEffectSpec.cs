using FluentGpu.Dsl;
using FluentGpu.Signals;

namespace FluentGpu.Scroll.Effects;

/// <summary>
/// The AUTHORING form of a scroll-linked effect (design §8): the runtime <see cref="ScrollEffect"/> plus the NAME of the
/// scope ancestor a sticky effect clamps against (<see cref="Element.ScrollScope"/> names a node; the reconciler
/// resolves the name to the nearest such ancestor's node index at bake). Null scope = the scroller's whole content.
/// <para><see cref="Engaged"/> (CSS <c>:stuck</c>, sticky and sticky-clip rows only) is the pinned/clipping EDGE as a
/// signal: the host writes it on the UI thread, before the frame publishes, exactly when the row engages or releases
/// (never per frame, never an unchanged value) — so UI DECISIONS (input hand-off, a feather's gate) follow the pin. It
/// is not a pixel channel: a "stuck" look belongs in a <see cref="ScrollEffect.Fade"/> row at the same offset.</para>
/// </summary>
public readonly record struct ScrollEffectSpec(ScrollEffect Effect, string? Scope = null, Signal<bool>? Engaged = null);

/// <summary>Element-level scroll-effect authoring (design §8): <c>el.OnScroll(ScrollEffect.Sticky(56), scope: "hero")</c>,
/// <c>.Parallax(...)</c>, <c>.Fade(...)</c>. Effects evaluate against the enclosing scroller's shown offset — the same
/// arithmetic on the UI thread (hit-test/publish) and the render thread (pixels).</summary>
public static class ScrollEffectDsl
{
    /// <summary>Appends <paramref name="effect"/> (optionally clamped to the ancestor named <paramref name="scope"/>).</summary>
    public static T OnScroll<T>(this T el, ScrollEffect effect, string? scope = null) where T : Element
        => AppendSpec(el, new ScrollEffectSpec(effect, scope));

    /// <summary>Appends a fully authored <see cref="ScrollEffectSpec"/> (effect + scope + engaged edge signal).</summary>
    public static T OnScroll<T>(this T el, ScrollEffectSpec spec) where T : Element => AppendSpec(el, spec);

    /// <summary>Appends several effects at once.</summary>
    public static T OnScroll<T>(this T el, params ScrollEffect[] effects) where T : Element
    {
        var existing = el.ScrollEffects;
        var next = new ScrollEffectSpec[existing.Length + effects.Length];
        existing.CopyTo(next, 0);
        for (int i = 0; i < effects.Length; i++) next[existing.Length + i] = new ScrollEffectSpec(effects[i], null);
        return el with { ScrollEffects = next };
    }

    /// <summary>CSS position:sticky — pin <paramref name="top"/> DIP from the viewport's leading edge, releasing at the
    /// end of the ancestor named <paramref name="scope"/> (null = the node's parent). <paramref name="engaged"/> receives
    /// the pinned edge (CSS <c>:stuck</c>): true exactly while the node is held at the line, written only on a flip.</summary>
    public static T Sticky<T>(this T el, float top = 0f, string? scope = null, Signal<bool>? engaged = null) where T : Element
        => AppendSpec(el, new ScrollEffectSpec(ScrollEffect.Sticky(top), scope, engaged));

    /// <summary>The sticky clip dual: guillotine this node's content at the viewport line <paramref name="inset"/> DIP
    /// down (a list clipped under a pinned header). Input over the clipped band is gated too. <paramref name="engaged"/>
    /// receives the clip's engage edge (true while the line cuts into the node), written only on a flip.</summary>
    public static T StickyClip<T>(this T el, float inset, Signal<bool>? engaged = null) where T : Element
        => AppendSpec(el, new ScrollEffectSpec(ScrollEffect.StickyClip(inset), null, engaged));

    /// <summary>Hero collapse: over the first <paramref name="over"/> DIP of scroll the node's PRESENTED height shrinks
    /// from its laid-out height to <paramref name="minH"/> (hit-testing and accessibility follow it, so the rows below the
    /// compact band take the input there). <paramref name="anchor"/> picks what the children ride: the node's top
    /// (<see cref="CollapseAnchor.Leading"/> — the content is cut at the presented edge by the node's own bottom-only
    /// clip, so the node needs no <c>ClipToBounds</c> and a <c>.StretchFromTop()</c> child still fills a top overscroll)
    /// or its presented bottom (<see cref="CollapseAnchor.Trailing"/>). Pair with <c>.Sticky(0)</c> to hold the
    /// collapsing node at the top.</summary>
    public static T Collapse<T>(this T el, double over, float minH, CollapseAnchor anchor = CollapseAnchor.Trailing) where T : Element
        => anchor == CollapseAnchor.Trailing
            ? el.OnScroll(ScrollEffect.Collapse(over, minH), ScrollEffect.CollapseChildShift(over, minH))
            : el.OnScroll(ScrollEffect.Collapse(over, minH), ScrollEffect.CollapseClip(over, minH));

    /// <summary>Top overscroll stretch: while the viewport is rubber-banded past its start the node scales about its
    /// top-centre to fill the pulled gap (its top edge stays on the viewport top). Composes with a parallax on the same
    /// node.</summary>
    public static T StretchFromTop<T>(this T el) where T : Element
        => el.OnScroll(ScrollEffect.StretchFromTop());

    /// <summary>Parallax: as the offset runs <paramref name="in0"/>..<paramref name="in1"/>, translate the node
    /// <paramref name="out0"/>..<paramref name="out1"/> DIP along the scroll axis.</summary>
    public static T Parallax<T>(this T el, double in0, double in1, float out0, float out1) where T : Element
        => el.OnScroll(ScrollEffect.Parallax(in0, in1, out0, out1));

    /// <summary>Parallax by fraction: over the first <paramref name="overPx"/> DIP of scroll the node moves
    /// <paramref name="fraction"/> of the distance (0.5 = half speed).</summary>
    public static T ParallaxY<T>(this T el, float fraction, float overPx) where T : Element
        => el.OnScroll(ScrollEffect.Parallax(0.0, overPx, 0f, overPx * fraction));

    /// <summary>Node-relative parallax + fade-out (a "lagging header"): once the node reaches <paramref name="inset"/>
    /// below the viewport edge it moves at <paramref name="fraction"/> of the scroll speed for <paramref name="over"/>
    /// DIP while fading to <paramref name="fadeTo"/>, and never leaves <paramref name="scope"/> (its section).</summary>
    public static T ParallaxFromNode<T>(this T el, float fraction, float over, float inset = 0f, string? scope = null,
        float fadeTo = 1f) where T : Element
    {
        var r = el.OnScroll(ScrollEffect.ParallaxFromNode(fraction, over, inset), scope);
        return fadeTo < 1f ? r.OnScroll(ScrollEffect.FadeFromNode(0.0, over, 1f, fadeTo, inset), scope) : r;
    }

    /// <summary>Opacity fade: offset <paramref name="in0"/>..<paramref name="in1"/> → opacity <paramref name="from"/>..<paramref name="to"/>.</summary>
    public static T Fade<T>(this T el, double in0, double in1, float from, float to) where T : Element
        => el.OnScroll(ScrollEffect.Fade(in0, in1, from, to));

    /// <summary>Reveal: as the offset runs <paramref name="revealStart"/>..<paramref name="revealStart"/>+<paramref name="overPx"/>
    /// the node fades in from 0 and slides from <paramref name="dy"/> to 0.</summary>
    public static T Reveal<T>(this T el, float revealStart, float overPx, float dy) where T : Element
        => el.OnScroll(ScrollEffect.Fade(revealStart, revealStart + overPx, 0f, 1f))
             .OnScroll(ScrollEffect.Parallax(revealStart, revealStart + overPx, dy, 0f));

    private static T AppendSpec<T>(T el, ScrollEffectSpec spec) where T : Element
    {
        var existing = el.ScrollEffects;
        var next = new ScrollEffectSpec[existing.Length + 1];
        existing.CopyTo(next, 0);
        next[existing.Length] = spec;
        return el with { ScrollEffects = next };
    }
}
