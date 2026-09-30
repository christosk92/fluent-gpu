using FluentGpu.Scroll.Runtime;
using FluentGpu.Signals;

namespace FluentGpu.Controls;

/// <summary>
/// The controller an <see cref="AnnotatedScrollBar"/> (or any other scroll affordance) drives a viewport through: a
/// thin adapter over the ONE app-facing scroll surface, <see cref="ScrollHandle"/> (scroll rework §9). Hand its
/// <see cref="Handle"/> to the list (<c>ScrollOptions.Handle</c> / <c>ScrollEl.Handle</c>); the host binds the handle
/// when the viewport mounts, and from then on the range signals below are the viewport's live truth (no geometry
/// observer, no event seam) and every request authors a plan on it directly.
/// </summary>
public sealed class AnnotatedScrollBarController
{
    private static readonly Signal<double> s_zero = new(0.0);

    /// <param name="handle">The viewport's handle to share, or null to mint one (pass <see cref="Handle"/> to the list).</param>
    public AnnotatedScrollBarController(ScrollHandle? handle = null) => Handle = handle ?? new ScrollHandle();

    /// <summary>The shared scroll handle: the list's <c>ScrollOptions.Handle</c>, a header's <c>Element.WheelTarget</c>.</summary>
    public ScrollHandle Handle { get; }

    public IReadSignal<double> MinimumOffset => s_zero;
    public IReadSignal<double> MaximumOffset => Handle.MaxOffsetSignal;
    public IReadSignal<double> Offset => Handle.Offset;
    public IReadSignal<double> ViewportLength => Handle.ViewportSignal;
    public IReadSignal<bool> IsScrollable => Handle.CanScroll;

    /// <summary>Request an absolute viewport offset (a glide when <paramref name="animate"/>, else an immediate jump).</summary>
    public void ScrollTo(float offset, bool animate = false)
        => Handle.ScrollTo(offset, animate ? ScrollMove.Glide : ScrollMove.Immediate);

    /// <summary>Request a delta from the viewport's live offset.</summary>
    public void ScrollBy(float delta, bool animate = false)
        => Handle.ScrollBy(delta, animate ? ScrollMove.Glide : ScrollMove.Immediate);

    /// <summary>Whole/fractional wheel notches as the same WinUI glide a device notch gets.</summary>
    public void WheelNotch(float notches) => Handle.WheelNow(notches);
}

/// <summary>Shared release-velocity projection: a flick's rest distance is <c>distance + velocity / FlickProjectK</c>
/// (the bounded coast a lifted contact travels), used by every control that commits a gesture on release
/// (FlipView, SwipeControl, PagedShelf).</summary>
internal static class FlickMath
{
    public const float FlickProjectK = 6f;
}
