using FluentGpu.Foundation;

namespace FluentGpu.Scroll.Effects;

/// <summary>
/// Pure evaluator for <see cref="ScrollEffect"/> — one arithmetic expression per <see cref="EffectKind"/> (design doc
/// §B.6), run identically from the UI thread (hit-test/a11y/edge-flag signals) and the render thread (pixels) so the
/// two never disagree. Zero-alloc; every method is a value-in/value-out function of its parameters only — no shared
/// mutable state, so <see cref="Evaluate"/> is safe to call concurrently from multiple threads.
/// <para>
/// Determinism: every formula uses plain double arithmetic (no <see cref="System.Math.FusedMultiplyAdd"/>, no
/// hardware-contingent fast-math) so two calls with identical inputs — from the same thread or two different ones —
/// produce the bit-identical <see cref="float"/> result.
/// </para>
/// </summary>
public static class ScrollEffectEval
{
    /// <summary>Evaluate <paramref name="e"/> at scroll <paramref name="offset"/> (content coordinates) against
    /// <paramref name="g"/>. Returns the value to write into <see cref="ScrollEffect.Channel"/>.</summary>
    public static float Evaluate(in ScrollEffect e, double offset, in EffectGeometry g)
    {
        switch (e.Kind)
        {
            case EffectKind.Sticky:
            {
                // TransY = clamp(offset + Inset − NodeY, 0, max(0, ScopeEnd − NodeH − NodeY))
                double shift = offset + e.Inset - g.NodeY;
                double limit = g.ScopeEnd - g.NodeH - g.NodeY;
                if (limit < 0.0) limit = 0.0;
                if (shift < 0.0) shift = 0.0;
                else if (shift > limit) shift = limit;
                return (float)shift;
            }
            case EffectKind.StickyClip:
            {
                // ClipTop = clamp(offset + Inset − NodeY, 0, NodeH): a fully hidden node freezes at its own height, so
                // further offset never rewrites (or dirties) it.
                double top = offset + e.Inset - g.NodeY;
                if (top < 0.0) top = 0.0;
                else if (top > g.NodeH) top = g.NodeH;
                return (float)top;
            }
            case EffectKind.Thumb:
            {
                // ThumbPos = offset/(Extent − Viewport) · (Track − ThumbLen), clamped; 0 when Extent <= Viewport.
                double range = g.Extent - g.Viewport;
                if (range <= 0.0) return 0f;
                double track = g.Track - g.ThumbLen;
                if (track < 0.0) track = 0.0;
                double pos = offset / range * track;
                if (pos < 0.0) pos = 0.0;
                else if (pos > track) pos = track;
                return (float)pos;
            }
            case EffectKind.Collapse:
            {
                // PresentedH = lerp(full, Out1, clamp01((offset − In0)/(In1 − In0))), full = Out0 (NaN ⇒ NodeH);
                // ChildShiftY = PresentedH − full (≤ 0: the children's bottom rides the presented edge); ClipBottom =
                // PresentedH (the leading anchor's cut sits exactly on the presented edge).
                double full = double.IsNaN(e.Out0) ? g.NodeH : e.Out0;
                double h = full;
                double span = e.In1 - e.In0;
                if (span > 1e-9)
                {
                    double t = (offset - e.In0) / span;
                    if (t < 0.0) t = 0.0; else if (t > 1.0) t = 1.0;
                    h = full + (e.Out1 - full) * t;
                }
                return e.Channel == EffectChannel.ChildShiftY ? (float)(h - full) : (float)h;
            }
            case EffectKind.Stretch:
                return StretchScale(offset, g.NodeH);
            case EffectKind.MapFromNode:
            {
                double x = offset + e.Inset - g.NodeY;
                double span = e.In1 - e.In0;
                double t = span < 1e-9 ? 0.0 : (x - e.In0) / span;
                if (t < 0.0) t = 0.0; else if (t > 1.0) t = 1.0;
                double v = e.Out0 + (e.Out1 - e.Out0) * t;
                if (e.Channel == EffectChannel.TransY)
                {
                    double limit = g.ScopeEnd - g.NodeH - g.NodeY;   // never leave the section it heads
                    if (limit < 0.0) limit = 0.0;
                    if (v > limit) v = limit;
                }
                return (float)v;
            }
            default: // EffectKind.Map
            {
                double a = e.In0, b = e.In1;
                double t;
                if (System.Math.Abs(b - a) < 1e-9) t = 0.0;   // degenerate range ⇒ inactive (writes Out0)
                else
                {
                    t = (offset - a) / (b - a);
                    if (t < 0.0) t = 0.0; else if (t > 1.0) t = 1.0;
                }
                float ft = (float)t;
                float eased = e.Ease == Easing.Linear ? ft : Easings.Ease(e.Ease, ft);
                return e.Out0 + (e.Out1 - e.Out0) * eased;
            }
        }
    }

    /// <summary>The <see cref="EffectKind.Stretch"/> scale at <paramref name="offset"/>: <c>1 + (−offset)/nodeH</c> while
    /// the viewport is rubber-banded past its start (<c>offset &lt; 0</c>), else exactly 1. The matching translation is
    /// derived from it (<c>offset = −(scale − 1)·nodeH</c>), so one row carries both halves.</summary>
    public static float StretchScale(double offset, double nodeH)
    {
        if (offset >= 0.0 || nodeH <= 1.0) return 1f;
        return (float)(1.0 + (-offset) / nodeH);
    }

    /// <summary>True for the channels that compose into the node's <c>LocalTransform</c> (translation / scale / stretch)
    /// — the poser folds a node's rows on these channels into ONE <see cref="EffectTransform"/>.</summary>
    public static bool IsTransformChannel(EffectChannel c) => c is EffectChannel.TransX or EffectChannel.TransY or EffectChannel.ScaleXY;

    /// <summary>True when a <see cref="EffectKind.Sticky"/>/<see cref="EffectKind.StickyClip"/> effect is currently
    /// engaged (pinned/clipping) at <paramref name="offset"/> — the edge-flag predicate a bound
    /// <c>Signal&lt;bool&gt;</c> (design doc §B.6, "Edge flags → Signal&lt;bool&gt;") observes. Always false for
    /// <see cref="EffectKind.Map"/>/<see cref="EffectKind.Thumb"/> (they have no pinned/released state).</summary>
    public static bool StickyEngaged(in ScrollEffect e, double offset, in EffectGeometry g)
    {
        if (e.Kind != EffectKind.Sticky && e.Kind != EffectKind.StickyClip) return false;
        return offset + e.Inset - g.NodeY > 0.0;
    }

    /// <summary>True when an ENGAGED effect of <paramref name="kind"/> lifts its node above its later siblings in paint
    /// (<c>NodeFlags.StickyPinned</c>) — only <see cref="EffectKind.Sticky"/>: a pinned header must paint over the rows
    /// that scroll under it. A <see cref="EffectKind.StickyClip"/> is a guillotine on a node that stays where it is in
    /// paint order (a hero's backdrop wash clipped at the pinned band must stay BEHIND the hero it backs); its engaged
    /// edge is still reported through its <c>engaged:</c> signal, it just never re-orders paint.</summary>
    public static bool PinsAboveSiblings(EffectKind kind) => kind == EffectKind.Sticky;

    /// <summary>Snap a DIP-space value to the nearest whole device pixel, expressed back in DIP — the ONE snap function
    /// of the scroll system: the poser snaps the content translation through it and every effect derives from that same
    /// snapped value, so an effect's pixels never disagree with the content they ride on. Banker's rounding
    /// (deterministic, symmetric about zero). Identity for a non-finite or non-positive <paramref name="scale"/> or a
    /// non-finite <paramref name="dip"/>.</summary>
    public static float SnapToDevicePixel(float dip, float scale)
    {
        if (!float.IsFinite(scale) || scale <= 0f || !float.IsFinite(dip)) return dip;
        return MathF.Round(dip * scale) / scale;
    }
}
