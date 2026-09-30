using FluentGpu.Signals;

namespace FluentGpu.Scroll.Runtime;

/// <summary>
/// A read-only view of one scroller's observable state (<c>Component.UseScroll</c>): the SAME signals the viewport's
/// <see cref="ScrollHandle"/> publishes every UI frame it moves (offset, motion, the start/end edges, extent and
/// viewport). Read <c>.Value</c> in a bind/effect/memo to subscribe; a component render that reads them re-renders on
/// every moved frame, so prefer a bind or a coarse memo (<c>UseScrollProgress</c>) for anything per-frame.
/// </summary>
public readonly record struct ScrollObservation(
    IReadSignal<double> Offset,
    IReadSignal<ScrollMotionState> Motion,
    IReadSignal<bool> AtStart,
    IReadSignal<bool> AtEnd,
    IReadSignal<double> Extent,
    IReadSignal<double> Viewport)
{
    private static readonly Signal<double> s_zero = new(0.0);
    private static readonly Signal<ScrollMotionState> s_idle = new(ScrollMotionState.Idle);
    private static readonly Signal<bool> s_true = new(true);

    /// <summary>No scroller in scope (a component outside every viewport, or a headless tree with no host): constant
    /// signals at rest — offset 0, idle, at both edges, empty extent/viewport. Never written, so never re-renders.</summary>
    public static ScrollObservation None { get; } = new(s_zero, s_idle, s_true, s_true, s_zero, s_zero);

    /// <summary>The observation over <paramref name="handle"/>'s published signals (no allocation).</summary>
    public static ScrollObservation Of(ScrollHandle handle)
        => new(handle.Offset, handle.Motion, handle.AtStart, handle.AtEnd, handle.ExtentSignal, handle.ViewportSignal);

    /// <summary>Normalized scroll progress: <c>clamp01((offset − in0)/(in1 − in0))</c>; a degenerate range is a step at
    /// <paramref name="in1"/>. Pure — the <c>UseScrollProgress</c> memo's arithmetic.</summary>
    public static float Progress(double offset, double in0, double in1)
    {
        double span = in1 - in0;
        if (System.Math.Abs(span) < 1e-9) return offset >= in1 ? 1f : 0f;
        double t = (offset - in0) / span;
        return t <= 0.0 ? 0f : t >= 1.0 ? 1f : (float)t;
    }
}
