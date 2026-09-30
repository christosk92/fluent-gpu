using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Hooks;
using FluentGpu.Scroll.Runtime;
using FluentGpu.Signals;

namespace FluentGpu.ScrollLab.Surfaces;

/// <summary>
/// The lab's reference surface (scroll-lab plan §4): a 100 000-row uniform track list at 56 DIP through
/// <c>ItemsView.CreateBound</c> — every row ONE <see cref="ListRowEl"/> (8 cells, a per-slot <see cref="RowCellBuffer"/>)
/// beside the position barcode. The Wavee track-list shape at its worst-case length.
/// <para><see cref="Handle"/> is mount-frozen on purpose: a different handle means a different surface, which the
/// lab remounts through its key.</para>
/// </summary>
public sealed class FixedList100k : Component
{
    public const int Count = 100_000;
    public const float RowHeight = 56f;

    public ScrollHandle Handle = null!;

    public static Element Create(ScrollHandle handle) => Embed.Comp(() => new FixedList100k { Handle = handle });

    public override Element Render()
    {
        int bits = PositionBarcode.BitsFor(Count);
        return ItemsView.CreateBound(Count, scope => SurfaceRows.TrackRow(scope.Index, bits, RowHeight),
            RepeatLayout.Stack(RowHeight),
            new ListOptions
            {
                SelectionMode = ItemsSelectionMode.None,
                Scroll = new ScrollOptions { Handle = Handle },
            });
    }
}
