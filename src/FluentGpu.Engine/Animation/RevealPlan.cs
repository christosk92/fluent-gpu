using System;
using FluentGpu.Foundation;

namespace FluentGpu.Animation;

/// <summary>The pure decisions behind <see cref="SizeMode.FlowReveal"/> and the virtual reveal bands: which part of a
/// presented-extent move a viewer can see, the Parallax content lead, and the one reveal spring's shape. Engine-free, so
/// Engine.Tests pins it directly (RevealPlanTests). docs/plans/smooth-reveal-implementation.md §1/§3.</summary>
public static class RevealPlan
{
    /// <summary>Slack below the view's bottom edge (DIP) the visible span still animates through.</summary>
    public const float VisibleSlackDip = 8f;
    /// <summary>Parallax anchor: the content trails the moving edge by this share of the extent still hidden …</summary>
    public const float ParallaxShare = 0.35f;
    /// <summary>… capped at this many DIP.</summary>
    public const float ParallaxMaxDip = 24f;

    /// <summary>Clamp a presented-extent move <paramref name="p0"/> → <paramref name="p1"/> of a region whose top sits at
    /// <paramref name="regionTop"/> (window DIP) inside a view spanning [<paramref name="viewTop"/>,
    /// <paramref name="viewBottom"/>]. Only the part of the move above viewBottom + slack is visible: the spring drives
    /// [<paramref name="from"/>, <paramref name="to"/>] and the remainder is applied instantly where nobody can see it.
    /// False — the caller snaps — when the region lies wholly above the view (a reveal above the scroll anchor would slide
    /// the content the user is reading) or nothing of the move is visible at all.</summary>
    public static bool TryClamp(float p0, float p1, float regionTop, float viewTop, float viewBottom, out float from, out float to)
    {
        from = to = p1;
        if (IsAboveView(p0, p1, regionTop, viewTop)) return false;
        float limit = VisibleLimit(regionTop, viewBottom);
        from = MathF.Min(p0, MathF.Max(p1, limit));
        to = MathF.Min(p1, MathF.Max(p0, limit));
        if (MathF.Abs(from - to) >= 0.5f) return true;
        from = to = p1;
        return false;
    }

    /// <summary>How much of a region whose top sits at <paramref name="regionTop"/> (window DIP) can be seen above a view's lower
    /// edge <paramref name="viewBottom"/> (with the slack): the extent <see cref="TryClamp"/> drives a reveal through, and the
    /// extent a Parallax lead is measured against (<see cref="ParallaxShift"/>).</summary>
    public static float VisibleLimit(float regionTop, float viewBottom) => MathF.Max(0f, viewBottom + VisibleSlackDip - regionTop);

    /// <summary>The visible span (<see cref="Scene.RevealBand.Visible"/>) a band records when it is armed over a move whose clamped
    /// spring runs [<paramref name="from"/>, <paramref name="to"/>]: the larger end. A REVERSE of a live band (<paramref name="prior"/>
    /// = its recorded span, NaN / &lt;= 0 = the whole <paramref name="extent"/>) keeps at least that span: the reversed move's
    /// own larger end is only the live value, and shrinking the span to it would drop the rows by the whole difference in one
    /// frame (their slide is min(0, presented - visible)).</summary>
    public static float VisibleSpan(float prior, float from, float to, float extent)
    {
        float v = MathF.Max(from, to);
        if (!float.IsNaN(prior)) v = MathF.Max(v, prior <= 0f ? extent : MathF.Min(prior, extent));
        return MathF.Min(v, extent);
    }

    /// <summary>True when a region at <paramref name="regionTop"/> stays wholly above the view's top edge
    /// <paramref name="viewTop"/> through the whole move <paramref name="p0"/> → <paramref name="p1"/>. The reveal then
    /// snaps, and the caller shifts the scroll frame by the change so the content being read stays put (scroll anchoring).</summary>
    public static bool IsAboveView(float p0, float p1, float regionTop, float viewTop)
        => regionTop + MathF.Max(p0, p1) <= viewTop;

    /// <summary>The Parallax child shift of a node presenting <paramref name="presented"/> of a content extent
    /// <paramref name="contentExtent"/>: the content leads by a damped share of what is still hidden, never past
    /// <see cref="ParallaxMaxDip"/>; 0 once fully presented. <paramref name="contentExtent"/> is the content a viewer can SEE
    /// (the caller caps it at <see cref="VisibleLimit"/>): a reveal that runs past its view's lower edge is driven only to that
    /// edge, so the lead must reach 0 there, not at the content's far end.</summary>
    public static float ParallaxShift(float presented, float contentExtent)
        => -MathF.Min(ParallaxMaxDip, MathF.Max(0f, contentExtent - presented) * ParallaxShare);

    /// <summary>The Trailing child shift of a node presenting <paramref name="presented"/> of a content extent <paramref name="contentExtent"/>:
    /// the content's far edge rides the moving edge (it slides out from under the node's top, and back under it), so the clip never cuts
    /// through it: what shows is always its lower part, its foot last. 0 once fully presented. The WHOLE content's extent: a Trailing
    /// reveal springs its whole move (no visible-span clamp; a slide is on screen all the way), so the shift is continuous end to end.</summary>
    public static float TrailingShift(float presented, float contentExtent)
        => -MathF.Max(0f, contentExtent - presented);

    /// <summary>Normalized progress (0 → 1) of the <see cref="MotionTokenId.Reveal"/> spring <paramref name="tMs"/> after it
    /// leaves rest — the curve every reveal runs, sampled exactly as the scheduler samples it (gates + tests).</summary>
    public static float Progress(float tMs)
    {
        var g = Generators.BakeSpring(MotionTok.Reveal.Spring, x0: -1f, v0: 0f);
        return Generators.EvalSpring(in g, 1f, tMs, Generators.RestDelta, Generators.RestSpeed, out _).Value;
    }
}
