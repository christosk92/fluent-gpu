namespace FluentGpu.Scene;

/// <summary>
/// Per-item extent table for variable-height virtualization: a Fenwick tree / Binary Indexed Tree giving O(log n)
/// <see cref="OffsetOf"/> (prefix-sum → position a row) and <see cref="IndexAt"/> (binary-lift → which item is at a
/// scroll offset), with estimate-then-correct extents (an unmeasured item uses the estimate; on realize+measure
/// <see cref="SetExtent"/> corrects it, O(log n)). It persists across frames (only deltas update) and survives a
/// fling because corrections are O(measured-rows). 100k items × 8B ≈ 800KB — within budget.
///
/// (virtualization.md §6.2 / layout.md §8.4 — the table MATH. It lives here in <c>FluentGpu.Scene</c> because both
/// Layout and the Reconciler depend on Scene; double-precision prefix sums keep it exact at 100k×100px.)
/// </summary>
public sealed class ExtentTable
{
    private double[] _bit = System.Array.Empty<double>();  // 1-based Fenwick partial sums
    private float[] _extent = System.Array.Empty<float>(); // current per-item extent (for delta updates)
    private int _n;
    private float _estimate;

    public ExtentTable(int n, float estimate) => Reset(n, estimate);

    public int Count => _n;
    /// <summary>Total content extent (sum of all item extents) — the published ContentSize.</summary>
    public double Total { get; private set; }
    public float Estimate => _estimate;

    /// <summary>(Re)build the table for <paramref name="n"/> items, all seeded to <paramref name="estimate"/> (regroup / count change).</summary>
    public void Reset(int n, float estimate)
    {
        if (n < 0) n = 0;
        _n = n; _estimate = estimate;
        // Reuse the backing arrays when they already fit (finding #11) instead of reallocating two arrays on every
        // item-count change: _bit must be zeroed (it accumulates), _extent is fully overwritten below. Mirrors the
        // capacity-guarded reuse in LinedFlowLayout / SpanningGridVirtualLayout.
        if (_bit.Length >= n + 1) System.Array.Clear(_bit, 0, n + 1); else _bit = new double[n + 1];
        if (_extent.Length < n) _extent = new float[n];
        for (int i = 0; i < n; i++) _extent[i] = estimate;
        // O(n) Fenwick build: each node accumulates its range, then folds into its parent.
        for (int i = 1; i <= n; i++)
        {
            _bit[i] += estimate;
            int j = i + (i & -i);
            if (j <= n) _bit[j] += _bit[i];
        }
        Total = (double)n * estimate;
    }

    /// <summary>Prefix sum of extents for items [0, index) — the content-space offset of item <paramref name="index"/>.</summary>
    public float OffsetOf(int index)
    {
        if (index <= 0) return 0f;
        if (index > _n) index = _n;
        double s = 0; for (int i = index; i > 0; i -= i & -i) s += _bit[i];
        return (float)s;
    }

    /// <summary>The item index whose extent band contains <paramref name="offset"/> (count of items fully above it).</summary>
    public int IndexAt(float offset)
    {
        if (offset <= 0f || _n == 0) return 0;
        int pos = 0; double remaining = offset;
        int hb = 1; while ((hb << 1) <= _n) hb <<= 1;
        for (int pw = hb; pw > 0; pw >>= 1)
        {
            int next = pos + pw;
            if (next <= _n && _bit[next] <= remaining) { remaining -= _bit[next]; pos = next; }
        }
        return pos >= _n ? _n - 1 : pos;   // clamp to a valid item index
    }

    /// <summary>Correct item <paramref name="index"/>'s extent to its measured value (O(log n)); updates <see cref="Total"/>.</summary>
    public void SetExtent(int index, float extent)
    {
        if ((uint)index >= (uint)_n) return;
        double delta = extent - _extent[index];
        if (delta == 0.0) return;
        _extent[index] = extent;
        for (int i = index + 1; i <= _n; i += i & -i) _bit[i] += delta;
        Total += delta;
    }

    public float ExtentAt(int index) => (uint)index < (uint)_n ? _extent[index] : _estimate;
}
