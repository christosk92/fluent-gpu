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
        float limit = MathF.Max(0f, viewBottom + VisibleSlackDip - regionTop);
        from = MathF.Min(p0, MathF.Max(p1, limit));
        to = MathF.Min(p1, MathF.Max(p0, limit));
        if (MathF.Abs(from - to) >= 0.5f) return true;
        from = to = p1;
        return false;
    }

    /// <summary>True when a region at <paramref name="regionTop"/> stays wholly above the view's top edge
    /// <paramref name="viewTop"/> through the whole move <paramref name="p0"/> → <paramref name="p1"/>. The reveal then
    /// snaps, and the caller shifts the scroll frame by the change so the content being read stays put (scroll anchoring).</summary>
    public static bool IsAboveView(float p0, float p1, float regionTop, float viewTop)
        => regionTop + MathF.Max(p0, p1) <= viewTop;

    /// <summary>The Parallax child shift of a node presenting <paramref name="presented"/> of a content extent
    /// <paramref name="contentExtent"/>: the content leads by a damped share of what is still hidden, never past
    /// <see cref="ParallaxMaxDip"/>; 0 once fully presented.</summary>
    public static float ParallaxShift(float presented, float contentExtent)
        => -MathF.Min(ParallaxMaxDip, MathF.Max(0f, contentExtent - presented) * ParallaxShare);

    /// <summary>Normalized progress (0 → 1) of the <see cref="MotionTokenId.Reveal"/> spring <paramref name="tMs"/> after it
    /// leaves rest — the curve every reveal runs, sampled exactly as the scheduler samples it (gates + tests).</summary>
    public static float Progress(float tMs)
    {
        var g = Generators.BakeSpring(MotionTok.Reveal.Spring, x0: -1f, v0: 0f);
        return Generators.EvalSpring(in g, 1f, tMs, Generators.RestDelta, Generators.RestSpeed, out _).Value;
    }
}
