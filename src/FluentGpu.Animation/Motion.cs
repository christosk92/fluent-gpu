using FluentGpu.Foundation;

namespace FluentGpu.Animation;

/// <summary>
/// The WinUI/Fluent-2 motion vocabulary as named presets, so controls share ONE set of spring/duration constants
/// instead of inlining response/damping pairs. Durations: ControlFaster = 83ms (brush transitions, hover/press),
/// ControlFast = 167ms (knob travel, settle animations), ControlNormal = 250ms (reposition, pane motion).
/// The composition-spring presets match the WinUI indicator/selection-pill feel (slight overshoot, fast settle).
/// </summary>
public static class MotionSprings
{
    /// <summary>The NavigationView selection-pill vertical slide (WinUI composition spring: snappy, faint overshoot).</summary>
    public static SpringParams NavPill => SpringParams.FromResponse(0.30f, 0.85f);

    /// <summary>The SelectorBar underline/pill slide between items.</summary>
    public static SpringParams SelectorPill => SpringParams.FromResponse(0.25f, 0.80f);

    /// <summary>A snappy critically-damped settle for small template parts (knobs, chevrons).</summary>
    public static SpringParams PartSettle => SpringParams.FromResponse(0.20f, 1.0f);
}
