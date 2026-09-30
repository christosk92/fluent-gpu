using System;

namespace FluentGpu.Scroll.Extent;

/// <summary>
/// Variable-height <see cref="IExtentSource"/> backed by a Fenwick tree (Binary Indexed Tree) of DOUBLE partial sums —
/// a new, double-precision implementation of the estimate-then-correct algorithm <c>FluentGpu.Scene.ExtentTable</c>
/// pioneers (that type is float-precision and scene-coupled; this one is portable and exact past ExtentTable's
/// single-precision ceiling for a 100k-row list). O(log n) <see cref="OffsetOf"/> (prefix sum) and
/// <see cref="IndexAt"/> (binary lifting); O(log n) <see cref="SetMeasured"/>. An unmeasured row's extent is the
/// source's running <see cref="Estimate"/>; <see cref="SetEstimate"/> retargets every still-unmeasured row's extent
/// without shifting the ones already anchored above (same anchor-relative-delta contract as <see cref="SetMeasured"/>).
/// <para>
/// Backing arrays (<c>double[]</c> partial sums, <c>double[]</c> per-row extents, a packed <c>ulong[]</c>
/// measured-bit array) are preallocated to a capacity that only grows by doubling — allocation happens on growth
/// only, never per frame.
/// </para>
/// </summary>
public sealed class MeasuredExtent : IExtentSource
{
    private double[] _bit = Array.Empty<double>();      // 1-based Fenwick partial sums, length >= _n+1
    private double[] _extent = Array.Empty<double>();   // current per-item extent (measured value, or last-applied estimate)
    private ulong[] _measured = Array.Empty<ulong>();    // packed measured-flag bit array, 64 rows/word
    private int _cap;                                    // allocated capacity of _extent/_measured (in rows)
    private int _n;
    private double _estimate;

    public MeasuredExtent(int n, double estimate)
    {
        if (n < 0) n = 0;
        _estimate = estimate;
        EnsureCapacity(n);
        for (int i = 0; i < n; i++) _extent[i] = estimate;
        _n = n;
        Rebuild();
    }

    public int Count => _n;
    public double Total { get; private set; }

    /// <summary>The current estimate seeded into every still-unmeasured row (see <see cref="SetEstimate"/>).</summary>
    public double Estimate => _estimate;

    public double OffsetOf(int index)
    {
        if (index <= 0) return 0.0;
        if (index > _n) index = _n;
        double s = 0.0;
        for (int i = index; i > 0; i -= i & -i) s += _bit[i];
        return s;
    }

    public int IndexAt(double off)
    {
        if (_n == 0 || off <= 0.0) return 0;
        int pos = 0;
        double remaining = off;
        int hb = 1; while ((hb << 1) <= _n) hb <<= 1;
        for (int pw = hb; pw > 0; pw >>= 1)
        {
            int next = pos + pw;
            if (next <= _n && _bit[next] <= remaining) { remaining -= _bit[next]; pos = next; }
        }
        return pos >= _n ? _n - 1 : pos;
    }

    public double ExtentOf(int i) => (uint)i < (uint)_n ? _extent[i] : _estimate;

    public bool IsMeasured(int i) => (uint)i < (uint)_n && GetBit(i);

    public double SetMeasured(int i, double extent, int anchorIndex)
    {
        if ((uint)i >= (uint)_n) return 0.0;
        double delta = extent - _extent[i];
        if (delta != 0.0)
        {
            _extent[i] = extent;
            AddDelta(i, delta);
            Total += delta;
        }
        SetBit(i);
        return i < anchorIndex ? delta : 0.0;
    }

    /// <summary>
    /// Retarget the running estimate to <paramref name="estimate"/>, updating every currently-UNMEASURED row's extent
    /// to match (a measured row is untouched — it already has a real value). Same anchor-relative contract as
    /// <see cref="SetMeasured"/>: returns the total delta this applied to indices <c>&lt; anchorIndex</c> (the amount
    /// <c>OffsetOf(anchorIndex)</c> moved), which the caller folds into its own frame so the change never visibly
    /// shifts content the user is looking at (design doc §B.4).
    /// </summary>
    public double SetEstimate(double estimate, int anchorIndex)
    {
        double deltaAbove = 0.0;
        for (int i = 0; i < _n; i++)
        {
            if (GetBit(i)) continue;
            double d = estimate - _extent[i];
            if (d == 0.0) continue;
            _extent[i] = estimate;
            AddDelta(i, d);
            Total += d;
            if (i < anchorIndex) deltaAbove += d;
        }
        _estimate = estimate;
        return deltaAbove;
    }

    public void Resize(int newCount)
    {
        if (newCount < 0) newCount = 0;
        if (newCount == _n) return;
        EnsureCapacity(newCount);
        if (newCount > _n)
        {
            for (int i = _n; i < newCount; i++)
            {
                _extent[i] = _estimate;
                ClearBit(i);   // guard against a stale bit from a prior shrink into this slot
            }
        }
        _n = newCount;
        Rebuild();
    }

    private void AddDelta(int index0, double delta)
    {
        for (int i = index0 + 1; i <= _n; i += i & -i) _bit[i] += delta;
    }

    private void Rebuild()
    {
        int n = _n;
        Array.Clear(_bit, 0, n + 1);
        double total = 0.0;
        for (int i = 1; i <= n; i++)
        {
            _bit[i] += _extent[i - 1];
            total += _extent[i - 1];
            int j = i + (i & -i);
            if (j <= n) _bit[j] += _bit[i];
        }
        Total = total;
    }

    private void EnsureCapacity(int n)
    {
        if (n > _cap)
        {
            int newCap = _cap == 0 ? Math.Max(n, 16) : Math.Max(n, _cap * 2);
            var grownExtent = new double[newCap];
            Array.Copy(_extent, grownExtent, _n);
            _extent = grownExtent;
            var grownBits = new ulong[(newCap + 63) / 64];
            Array.Copy(_measured, grownBits, _measured.Length);
            _measured = grownBits;
            _cap = newCap;
        }
        if (_bit.Length < n + 1) _bit = new double[n + 1];
    }

    private bool GetBit(int i) => (_measured[i >> 6] & (1UL << (i & 63))) != 0;
    private void SetBit(int i) => _measured[i >> 6] |= 1UL << (i & 63);
    private void ClearBit(int i) => _measured[i >> 6] &= ~(1UL << (i & 63));
}
