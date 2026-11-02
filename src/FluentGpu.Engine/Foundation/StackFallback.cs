using System;

namespace FluentGpu.Foundation;

/// <summary>The stacked form of a row that may not fit, for <c>BoxEl.StackFallback</c>: the box lays its children in a row
/// (its own <c>Padding</c>, each child's own <c>Margin</c>) while their natural widths fit, and otherwise stacks them with these
/// spacings. The choice is made during layout from what the children really measure, on every pass and at every width, so the
/// first frame is already right and no render is involved (WinUI's InfoBarPanel, its VerticalOrientationPadding and
/// VerticalOrientationMargin).</summary>
/// <param name="Padding">The box's padding while stacked.</param>
/// <param name="Margins">Each child's margin while stacked, by child position. A child past the end has none.</param>
/// <param name="MaxRowHeight">A child taller than this in the row (its margins included) stacks the box even when the widths fit:
/// the row is only for lines that keep to the box's minimum height. NaN = no limit.</param>
public sealed record StackFallback(Edges4 Padding, Edges4[] Margins, float MaxRowHeight = float.NaN)
{
    public Edges4 MarginAt(int child) => child < Margins.Length ? Margins[child] : default;

    public bool Equals(StackFallback? other)
        => other is not null && Padding == other.Padding && MaxRowHeight.Equals(other.MaxRowHeight)
           && Margins.AsSpan().SequenceEqual(other.Margins);

    public override int GetHashCode() => HashCode.Combine(Padding, MaxRowHeight, Margins.Length);
}
