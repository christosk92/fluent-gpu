using FluentGpu.Foundation;

namespace FluentGpu.Animation;

/// <summary>
/// The Microsoft WinUI NavigationView selection-indicator choreography, expressed in painted-top coordinates because
/// FluentGpu has no animated CenterPoint channel. Source: NavigationView.cpp PlayIndicatorAnimations /
/// PlayIndicatorNonSameLevelAnimations. The conversion is algebraically identical: the one-frame Offset+CenterPoint
/// swap at 200 ms becomes a continuous top track while Scale.Y keeps the original 600 ms keyframes.
/// </summary>
public static class NavigationSelectionMotion
{
    public const float DurationMs = 600f;
    public const float StretchPhase = 0.333f;

    /// <summary>Cancel any flight and restore one item-owned indicator to its resting transform and visibility.</summary>
    public static void SnapVertical(AnimEngine anim, NodeHandle indicator, bool visible)
    {
        anim.CancelAll(indicator);
        PlaceImmediate(anim, indicator, AnimChannel.TranslateY, 0f);
        PlaceImmediate(anim, indicator, AnimChannel.ScaleY, 1f);
        PlaceImmediate(anim, indicator, AnimChannel.Opacity, visible ? 1f : 0f);
    }

    /// <summary>
    /// Animate one half of a realized previous/next indicator pair. For the outgoing item pass local coordinates
    /// <c>0 → delta</c>; for the incoming item pass <c>-delta → 0</c>. WinUI runs both simultaneously, fades only the
    /// outgoing copy after the stretch phase, and force-completes an interrupted pair before retargeting.
    /// </summary>
    public static void StartVertical(AnimEngine anim, NodeHandle indicator, float from, float to,
                                     float indicatorHeight, bool outgoing, bool sameDepth)
    {
        anim.CancelAll(indicator);
        if (!float.IsFinite(from) || !float.IsFinite(to)
            || !float.IsFinite(indicatorHeight) || indicatorHeight <= 0f)
        {
            SnapVertical(anim, indicator, visible: !outgoing);
            return;
        }

        PlaceImmediate(anim, indicator, AnimChannel.Opacity, 1f);
        if (!sameDepth)
        {
            bool towardBelow = from < to;
            // WinUI anchors a cross-depth indicator at the edge facing its peer. FluentGpu standardizes this primitive
            // on a top-edge transform origin, so a bottom anchor is the equivalent translate H*(1-scale).
            float translateFrom = outgoing
                ? 0f
                : towardBelow ? 0f : indicatorHeight;
            float translateTo = outgoing
                ? towardBelow ? indicatorHeight : 0f
                : 0f;
            float scaleFrom = outgoing ? 1f : 0f;
            float scaleTo = outgoing ? 0f : 1f;
            PlaceImmediate(anim, indicator, AnimChannel.TranslateY, translateFrom);
            PlaceImmediate(anim, indicator, AnimChannel.ScaleY, scaleFrom);
            anim.KeyframesMotion(indicator, AnimChannel.TranslateY,
            [
                new(0f, translateFrom),
                new(1f, translateTo, Easing.Linear),
            ], DurationMs, ReducedMotionPolicy.KeepFade);
            anim.KeyframesMotion(indicator, AnimChannel.ScaleY,
            [
                new(0f, scaleFrom),
                new(1f, scaleTo, Easing.Linear),
            ], DurationMs, ReducedMotionPolicy.KeepFade);
            return;
        }

        float peak = MathF.Abs(to - from) / indicatorHeight + 1f;
        bool forward = from < to;

        // Exact painted bounds of WinUI's Offset.Y + Scale.Y + CenterPoint.Y tracks. During the first third the leading
        // edge stretches toward the target; at 200 ms WinUI atomically swaps Offset/origin; the trailing edge then catches
        // up under its second cubic. FluentAccelerate/Decelerate are the source cubics (0.9,.1,1,.2)/(0.1,.9,.2,1).
        Keyframe[] top = forward
            ? [new(0f, from), new(StretchPhase, from, Easing.Linear),
               new(1f, to, Easing.FluentDecelerate)]
            : [new(0f, from), new(StretchPhase, to, Easing.FluentAccelerate),
               new(1f, to, Easing.Linear)];
        Keyframe[] scale =
        [
            new(0f, 1f),
            new(StretchPhase, peak, Easing.FluentAccelerate),
            new(1f, 1f, Easing.FluentDecelerate),
        ];

        PlaceImmediate(anim, indicator, AnimChannel.TranslateY, top[0].Value);
        PlaceImmediate(anim, indicator, AnimChannel.ScaleY, scale[0].Value);
        anim.KeyframesMotion(indicator, AnimChannel.TranslateY, top, DurationMs, ReducedMotionPolicy.KeepFade);
        anim.KeyframesMotion(indicator, AnimChannel.ScaleY, scale, DurationMs, ReducedMotionPolicy.KeepFade);
        if (outgoing)
            anim.KeyframesMotion(indicator, AnimChannel.Opacity,
            [
                new(0f, 1f),
                new(StretchPhase, 1f, Easing.Linear),
                new(1f, 0f, Easing.FluentDecelerate),
            ], DurationMs, ReducedMotionPolicy.KeepFade);
    }

    static void PlaceImmediate(AnimEngine anim, NodeHandle node, AnimChannel channel, float value)
        => anim.ApplyImmediate(node, channel, value);
}
