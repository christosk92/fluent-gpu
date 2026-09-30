namespace FluentGpu.Scroll.Extent;

/// <summary>
/// Per-item extent source for variable- or fixed-height virtualization (design doc §B.4): the one seam the
/// virtualizer queries for "where is item i" (<see cref="OffsetOf"/>) and "which item is at offset x"
/// (<see cref="IndexAt"/>), plus estimate-then-correct measurement (<see cref="SetMeasured"/>) so a row realized this
/// frame can report its true extent without shifting rows the viewport has already anchored against.
/// <para>
/// All positions/extents are content-space DOUBLES (matching <c>ScrollPlan</c>'s double precision — design doc §B.1
/// — so a 100k-row list at 100 px/row stays exact well past single-precision's ~16M mantissa ceiling). Implementations
/// are portable: no <c>SceneStore</c>/TerraFX dependency, so they can be unit-tested headlessly and reused by any host.
/// </para>
/// </summary>
public interface IExtentSource
{
    /// <summary>Number of items.</summary>
    int Count { get; }

    /// <summary>Total content extent — the sum of every item's extent (the published ContentSize along the scroll axis).</summary>
    double Total { get; }

    /// <summary>The content-space offset (start) of item <paramref name="i"/>. <c>OffsetOf(0)</c> is the first item's start: 0, or a layout's leading pad (the content start is always 0);
    /// <c>OffsetOf(Count) == Total</c>. Clamped to <c>[0, Count]</c>.</summary>
    double OffsetOf(int i);

    /// <summary>The last item index whose <see cref="OffsetOf"/> is <c>&lt;= off</c> — the item occupying content
    /// position <paramref name="off"/>. Clamped to <c>[0, Count-1]</c>; returns 0 when <see cref="Count"/> is 0.</summary>
    int IndexAt(double off);

    /// <summary>Item <paramref name="i"/>'s current extent — its measured value once
    /// <see cref="IsMeasured"/> is true, else the source's running estimate.</summary>
    double ExtentOf(int i);

    /// <summary>True once item <paramref name="i"/> has been corrected by <see cref="SetMeasured"/> (realized and
    /// laid out at least once) rather than still carrying an estimate.</summary>
    bool IsMeasured(int i);

    /// <summary>
    /// Correct item <paramref name="i"/>'s extent to its measured <paramref name="extent"/> (O(log n) for a Fenwick
    /// source, O(1) for a fixed-stride one). <paramref name="anchorIndex"/> is the item the caller's viewport window
    /// is currently anchored to (its <c>WindowOrigin</c>, design doc §B.4).
    /// <para>
    /// Returns the delta this correction applies ABOVE <paramref name="anchorIndex"/> — i.e. the amount
    /// <c>OffsetOf(anchorIndex)</c> itself moved (nonzero only when <c>i &lt; anchorIndex</c>; 0 when
    /// <c>i &gt;= anchorIndex</c>, since a correction at or below the anchor cannot move the anchor's own offset). The
    /// caller applies this delta to its own frame (<c>WindowOrigin += delta</c>, shift already-arranged rows the same
    /// frame) so a measurement never visibly shifts content the user is looking at.
    /// </para>
    /// </summary>
    double SetMeasured(int i, double extent, int anchorIndex);

    /// <summary>Grow or shrink to <paramref name="newCount"/> items. Growth appends estimate rows at the END only
    /// (every existing index's offset and measured/estimate state is unchanged); shrink truncates the tail.</summary>
    void Resize(int newCount);
}
