using FluentGpu.Animation;
using FluentGpu.Signals;

namespace FluentGpu.Controls;

/// <summary>A controller-originated absolute scroll request.</summary>
public readonly record struct ScrollToRequest(float Offset, bool Animate);

/// <summary>A controller-originated relative scroll request.</summary>
public readonly record struct ScrollByRequest(float Delta, bool Animate);

/// <summary>
/// Two-way scroll-controller seam: a viewport pushes its live range through <see cref="SetValues"/> and a composing
/// control requests motion through the two events. The controller is an identity and is therefore frozen at mount when
/// supplied through <see cref="ScrollOptions.VerticalScrollController"/>.
/// </summary>
public interface IScrollController
{
    void SetValues(float minOffset, float maxOffset, float offset, float viewportLength);
    void SetIsScrollable(bool isScrollable);
    event Action<ScrollToRequest>? ScrollToRequested;
    event Action<ScrollByRequest>? ScrollByRequested;
}

/// <summary>
/// Stock controller for <see cref="AnnotatedScrollBar"/> and other scroll affordances. Its range is exposed as
/// read-only signals so compositor bindings and derived text can react without polling the viewport.
/// </summary>
public sealed class AnnotatedScrollBarController : IScrollController
{
    private readonly FloatSignal _minimumOffset = new();
    private readonly FloatSignal _maximumOffset = new();
    private readonly FloatSignal _offset = new();
    private readonly FloatSignal _viewportLength = new();
    private readonly Signal<bool> _isScrollable = new(false);

    public IReadSignal<float> MinimumOffset => _minimumOffset;
    public IReadSignal<float> MaximumOffset => _maximumOffset;
    public IReadSignal<float> Offset => _offset;
    public IReadSignal<float> ViewportLength => _viewportLength;
    public IReadSignal<bool> IsScrollable => _isScrollable;

    public event Action<ScrollToRequest>? ScrollToRequested;
    public event Action<ScrollByRequest>? ScrollByRequested;

    public void SetValues(float minOffset, float maxOffset, float offset, float viewportLength)
    {
        minOffset = float.IsFinite(minOffset) ? minOffset : 0f;
        maxOffset = float.IsFinite(maxOffset) ? MathF.Max(minOffset, maxOffset) : minOffset;
        viewportLength = float.IsFinite(viewportLength) ? MathF.Max(0f, viewportLength) : 0f;
        offset = float.IsFinite(offset) ? Math.Clamp(offset, minOffset, maxOffset) : minOffset;

        _minimumOffset.Value = minOffset;
        _maximumOffset.Value = maxOffset;
        _offset.Value = offset;
        _viewportLength.Value = viewportLength;
    }

    public void SetIsScrollable(bool isScrollable) => _isScrollable.Value = isScrollable;

    /// <summary>Request an absolute viewport offset.</summary>
    public void ScrollTo(float offset, bool animate = false)
        => ScrollToRequested?.Invoke(new ScrollToRequest(offset, animate));

    /// <summary>Request a delta from the viewport's live offset.</summary>
    public void ScrollBy(float delta, bool animate = false)
        => ScrollByRequested?.Invoke(new ScrollByRequest(delta, animate));
}

/// <summary>
/// Allocation-free composition of the engine's single change-gated geometry observer. Controller geometry and the
/// caller's projection keep independent last-value gates: a controller-only change never replays the app callback and
/// an app-only projected change never redundantly pushes the controller.
/// </summary>
internal sealed class ScrollGeometryObserverMux
{
    private readonly IScrollController _controller;
    private readonly (Func<ScrollGeometry, long> Project, Action<ScrollGeometry> Action)? _other;

    private long _epoch;
    private bool _hasProjectedController;
    private int _projectedMin, _projectedMax, _projectedOffset, _projectedViewport;
    private bool _hasDeliveredController;
    private int _deliveredMin, _deliveredMax, _deliveredOffset, _deliveredViewport;
    private bool _deliveredScrollable;
    private bool _hasDeliveredScrollable;
    private bool _hasProjectedOther;
    private long _projectedOther;
    private bool _hasDeliveredOther;
    private long _deliveredOther;

    public ScrollGeometryObserverMux(
        IScrollController controller,
        (Func<ScrollGeometry, long> Project, Action<ScrollGeometry> Action)? other)
    {
        _controller = controller;
        _other = other;
    }

    public long Project(ScrollGeometry geometry)
    {
        Values(in geometry, out float min, out float max, out float offset, out float viewport);
        int minBits = Bits(min), maxBits = Bits(max), offsetBits = Bits(offset), viewportBits = Bits(viewport);
        bool controllerChanged = !_hasProjectedController
            || minBits != _projectedMin || maxBits != _projectedMax
            || offsetBits != _projectedOffset || viewportBits != _projectedViewport;
        if (controllerChanged)
        {
            _hasProjectedController = true;
            _projectedMin = minBits; _projectedMax = maxBits;
            _projectedOffset = offsetBits; _projectedViewport = viewportBits;
        }

        bool otherChanged = false;
        if (_other is { } other)
        {
            long key = other.Project(geometry);
            otherChanged = !_hasProjectedOther || key != _projectedOther;
            _hasProjectedOther = true;
            _projectedOther = key;
        }

        if (controllerChanged || otherChanged) unchecked { _epoch++; }
        return _epoch;
    }

    public void OnGeometryChanged(ScrollGeometry geometry)
    {
        Values(in geometry, out float min, out float max, out float offset, out float viewport);
        int minBits = Bits(min), maxBits = Bits(max), offsetBits = Bits(offset), viewportBits = Bits(viewport);
        if (!_hasDeliveredController
            || minBits != _deliveredMin || maxBits != _deliveredMax
            || offsetBits != _deliveredOffset || viewportBits != _deliveredViewport)
        {
            _hasDeliveredController = true;
            _deliveredMin = minBits; _deliveredMax = maxBits;
            _deliveredOffset = offsetBits; _deliveredViewport = viewportBits;
            _controller.SetValues(min, max, offset, viewport);
        }

        bool scrollable = max > min;
        if (!_hasDeliveredScrollable || scrollable != _deliveredScrollable)
        {
            _hasDeliveredScrollable = true;
            _deliveredScrollable = scrollable;
            _controller.SetIsScrollable(scrollable);
        }

        if (_other is { } other && (!_hasDeliveredOther || _projectedOther != _deliveredOther))
        {
            _hasDeliveredOther = true;
            _deliveredOther = _projectedOther;
            other.Action(geometry);
        }
    }

    private static void Values(in ScrollGeometry geometry,
                               out float min, out float max, out float offset, out float viewport)
    {
        min = 0f;
        viewport = MathF.Max(0f, geometry.ViewportH);
        max = MathF.Max(min, geometry.ContentH - viewport);
        offset = Math.Clamp(geometry.OffsetY, min, max);
    }

    private static int Bits(float value) => BitConverter.SingleToInt32Bits(value);
}
