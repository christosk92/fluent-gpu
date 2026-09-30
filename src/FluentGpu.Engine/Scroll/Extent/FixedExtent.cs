namespace FluentGpu.Scroll.Extent;

/// <summary>
/// A stride-based <see cref="IExtentSource"/> for a uniform-height list: no per-item table, O(1)
/// <see cref="OffsetOf"/>/<see cref="IndexAt"/>. Every row is exact by construction, so
/// <see cref="SetMeasured"/> is a no-op that always returns 0 — a fixed list never needs an estimate-then-correct
/// pass. Optional <see cref="LeadingPad"/>/<see cref="TrailingPad"/> let a section header (or footer) of known,
/// exact extent sit directly above (below) the fixed rows without needing its own extent source.
/// </summary>
public sealed class FixedExtent : IExtentSource
{
    private readonly double _stride;
    private int _count;

    /// <summary>Fixed extent BEFORE row 0 (e.g. an exact-height section header) — added to every offset, and to
    /// <see cref="Total"/>. Never negative.</summary>
    public double LeadingPad { get; }

    /// <summary>Fixed extent AFTER the last row (e.g. an exact-height footer) — added to <see cref="Total"/> only.
    /// Never negative.</summary>
    public double TrailingPad { get; }

    /// <summary>The per-row extent every item shares.</summary>
    public double Stride => _stride;

    public FixedExtent(int count, double stride, double leadingPad = 0.0, double trailingPad = 0.0)
    {
        _count = count < 0 ? 0 : count;
        _stride = stride < 0.0 ? 0.0 : stride;
        LeadingPad = leadingPad < 0.0 ? 0.0 : leadingPad;
        TrailingPad = trailingPad < 0.0 ? 0.0 : trailingPad;
    }

    public int Count => _count;

    public double Total => LeadingPad + (double)_count * _stride + TrailingPad;

    public double OffsetOf(int i)
    {
        if (i <= 0) return LeadingPad;
        if (i > _count) i = _count;
        return LeadingPad + (double)i * _stride;
    }

    public int IndexAt(double off)
    {
        if (_count == 0) return 0;
        double rel = off - LeadingPad;
        if (rel <= 0.0 || _stride <= 0.0) return 0;
        int idx = (int)(rel / _stride);
        if (idx >= _count) idx = _count - 1;
        else if (idx < 0) idx = 0;
        return idx;
    }

    public double ExtentOf(int i) => _stride;

    /// <summary>Always true — every row's extent is exact by construction (the stride).</summary>
    public bool IsMeasured(int i) => true;

    /// <summary>No-op: a fixed-stride row is exact by construction, so there is nothing to correct. Always returns 0.</summary>
    public double SetMeasured(int i, double extent, int anchorIndex) => 0.0;

    public void Resize(int newCount) => _count = newCount < 0 ? 0 : newCount;
}
