using FluentGpu.Animation;
using FluentGpu.Dsl;
using FluentGpu.Foundation;

namespace FluentGpu.Hooks;

/// <summary>
/// Reusable Fluent motion patterns built on the component's animation hooks. The entrance mirrors WinUI's implicit
/// show transition (Offset From 0,24 + Opacity 0→1, 400/200ms, the real Fluent decelerate curve). Honors
/// <see cref="Motion.ReducedMotion"/>.
/// </summary>
public static class MotionHooks
{
    /// <summary>The Fluent entrance: TranslateY 24→0 (400ms) + Opacity 0→1 (200ms), FluentDecelerate. Call inside the
    /// component whose rendered root is the node to animate. <paramref name="key"/> distinguishes deps if re-armed.</summary>
    public static void UseEntrance(this Component c, float offsetPx = Motion.EntranceOffsetPx, object? key = null)
    {
        // Reduced-motion as a VALUE, never an early-return — Motion.ReducedMotion is a mutable global (a resize grip flips
        // it), and skipping these two hooks mid-life would shift every later hook slot in the caller → a cell-cast crash.
        bool reduce = Motion.ReducedMotion;
        object dep = key ?? "enter";
        c.Context.UseTransition(AnimChannel.Opacity, reduce ? 1f : 0f, 1f, Motion.Fade, Easing.FluentDecelerate, dep);
        c.Context.UseTransition(AnimChannel.TranslateY, reduce ? 0f : offsetPx, 0f, Motion.OffsetEntrance, Easing.FluentDecelerate, dep);
    }

    /// <summary>A subtle pointer-over scale lift (spring), the WinUI card/button micro-interaction.</summary>
    public static void UseHoverScale(this Component c, bool hovered, float to = 1.02f)
    {
        var spring = SpringParams.FromResponse(0.25f, 0.85f);
        c.Context.UseSpring(AnimChannel.ScaleX, hovered ? to : 1f, spring, hovered);
        c.Context.UseSpring(AnimChannel.ScaleY, hovered ? to : 1f, spring, hovered);
    }
}
