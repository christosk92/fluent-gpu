using System;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Scroll.Runtime;
using FluentGpu.Signals;

namespace FluentGpu.ScrollLab.Surfaces;

/// <summary>
/// A 2 000-row VARIABLE-extent list (scroll-lab plan §4): each row is 40..140 DIP (a deterministic per-index height),
/// realized through the measured stack layout — rows seed at a 90 DIP estimate and correct on arrange, so the probe's
/// Extent records and the anchoring path are exercised under real scrolling. Each row shows its own height as a bar and
/// a number, beside the position barcode (whose ramp spans the row's real height).
/// </summary>
public sealed class MeasuredList : Component
{
    public const int Count = 2000;
    public const float MinRow = 40f;
    public const float MaxRow = 140f;
    public const float Estimate = 90f;

    public ScrollHandle Handle = null!;

    public static Element Create(ScrollHandle handle) => Embed.Comp(() => new MeasuredList { Handle = handle });

    /// <summary>Row <paramref name="i"/>'s real height (DIP), 40..140.</summary>
    public static float HeightOf(int i) => MinRow + SurfaceRows.Hash(i + 101) % (int)(MaxRow - MinRow + 1f);

    public override Element Render()
    {
        // The measured layout is stateful (its realize table): hoisted once for the component's lifetime.
        var layout = UseMemo(() => RepeatLayout.VariableList(Estimate), 0);
        int bits = PositionBarcode.BitsFor(Count);
        return ItemsView.CreateBound(Count, scope => Row(scope.Index, bits), layout,
            new ListOptions
            {
                SelectionMode = ItemsSelectionMode.None,
                Scroll = new ScrollOptions { Handle = Handle },
            });
    }

    static Element Row(IReadSignal<int> index, int bits)
    {
        var cells = new RowCellBuffer();
        ColorF primary = Tok.TextPrimary;
        ColorF secondary = Tok.TextSecondary;
        ColorF accent = Tok.AccentDefault;
        ColorF divider = Tok.StrokeDividerDefault;
        return new BoxEl
        {
            Direction = 0,
            Height = Prop.Of(() => HeightOf(index.Value)),
            Grow = 1f,
            Basis = 0f,
            MinWidth = 0f,
            Children =
            [
                new ListRowEl(Prop.Of<RowCells>(() =>
                {
                    int i = index.Value;
                    float h = HeightOf(i);
                    cells.Clear();
                    cells.Add(new RowCell { Kind = RowCellKind.Rect, Rect = new RectF(8f, 4f, 4f, MathF.Max(1f, h - 8f)), Color = accent, Corners = CornerRadius4.All(2f) });
                    cells.Add(new RowCell { Kind = RowCellKind.Text, Rect = new RectF(20f, h * 0.5f - 10f, 52f, 20f), Text = FormatCache.Int(i + 1), Color = secondary, FontSize = 13f, Trim = TextTrim.Clip });
                    cells.Add(new RowCell { Kind = RowCellKind.Text, Rect = new RectF(80f, h * 0.5f - 10f, 320f, 20f), Text = SurfaceRows.Title(i), Color = primary, FontSize = 14f, Trim = TextTrim.CharacterEllipsis });
                    cells.Add(new RowCell { Kind = RowCellKind.Text, Rect = new RectF(420f, h * 0.5f - 10f, 60f, 20f), Text = FormatCache.Int((int)h), Color = secondary, FontSize = 13f, Trim = TextTrim.Clip });
                    cells.Add(new RowCell { Kind = RowCellKind.Rect, Rect = new RectF(8f, h - 1f, 764f, 1f), Color = divider });
                    return cells.Current;
                }))
                {
                    Grow = 1f,
                    Shrink = 1f,
                    MinWidth = 0f,
                    HoverFill = Tok.FillSubtleSecondary,
                },
                PositionBarcode.Column(index, bits, static i => HeightOf(i)),
            ],
        };
    }
}
