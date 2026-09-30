using System;
using System.Numerics;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Signals;

namespace FluentGpu.ScrollLab.Surfaces;

/// <summary>
/// The per-row POSITION BARCODE every lab surface draws in a fixed column at the row's right edge (scroll-lab plan
/// phase 2b, optical measurement): a screen capture decodes the displayed offset from it, so the lab, the WinUI
/// reference and Edge are measured with one ruler on the composed output.
/// <code>
///  ┌─┐  ┌───┬───┬───┬ … ┬───┐  ┌──┐
///  │S│  │b16│b15│b14│   │ b0│  │R │   S = sentinel (always ink, full height) — finds the column even for row 0
///  │ │  │   │   │   │   │   │  │RR│   b = the row index, MSB first, one BitWidth column per bit; runs of 1-bits are
///  │ │  │   │   │   │   │   │  │RRR   ONE rect (no AA seams between adjacent ones); a code change = a row boundary
///  └─┘  └───┴───┴───┴ … ┴───┘  └────┘ R = the sub-row phase ramp: RampSteps stacked bars, step s (from the top)
///                                        RampWidth·(s+1)/RampSteps wide — a scanline's ramp width = its row phase
/// </code>
/// Ink on black, theme-independent. At most <see cref="MaxCells"/> cells — two <see cref="ListRowEl"/>s (8 cells each)
/// stacked in one ZStack carry it.
/// </summary>
public static class PositionBarcode
{
    public const float SentinelWidth = 2f;
    public const float Gap = 2f;
    public const float BitWidth = 3f;
    public const float RampWidth = 12f;
    public const int RampSteps = 6;
    /// <summary>1 sentinel + ⌈17/2⌉ = 9 runs (a 100k list) + 6 ramp steps.</summary>
    public const int MaxCells = 16;

    public static readonly ColorF Ink = ColorF.FromRgba(255, 255, 255);
    public static readonly ColorF Background = ColorF.FromRgba(0, 0, 0);

    /// <summary>Bits needed to code every index of a <paramref name="count"/>-row list (≥ 1).</summary>
    public static int BitsFor(int count)
    {
        uint max = (uint)Math.Max(count - 1, 1);
        return Math.Max(1, 32 - BitOperations.LeadingZeroCount(max));
    }

    /// <summary>The column's width (DIP) for a <paramref name="bits"/>-bit code.</summary>
    public static float WidthFor(int bits) => SentinelWidth + Gap + bits * BitWidth + Gap + RampWidth;

    /// <summary>X (DIP, column-local) where the ramp starts.</summary>
    public static float RampX(int bits) => SentinelWidth + Gap + bits * BitWidth + Gap;

    /// <summary>Emits the barcode cells of row <paramref name="index"/> (height <paramref name="rowHeight"/>), in the fixed
    /// order sentinel → 1-bit runs (MSB first) → ramp steps, skipping the first <paramref name="skip"/> cells and adding
    /// at most <paramref name="take"/> to <paramref name="into"/>. Returns the TOTAL cell count of the row. Zero
    /// allocation (the buffer only grows).</summary>
    public static int Emit(int index, int bits, float rowHeight, int skip, int take, RowCellBuffer into)
    {
        int ordinal = 0;
        static void Add(RowCellBuffer buf, ref int ord, int skipN, int takeN, float x, float y, float w, float h)
        {
            if (ord >= skipN && ord < skipN + takeN)
                buf.Add(new RowCell { Kind = RowCellKind.Rect, Rect = new RectF(x, y, w, h), Color = Ink });
            ord++;
        }

        Add(into, ref ordinal, skip, take, 0f, 0f, SentinelWidth, rowHeight);

        float bitsX = SentinelWidth + Gap;
        int runStart = -1;
        for (int i = 0; i <= bits; i++)
        {
            bool one = i < bits && ((index >> (bits - 1 - i)) & 1) != 0;
            if (one && runStart < 0) runStart = i;
            else if (!one && runStart >= 0)
            {
                Add(into, ref ordinal, skip, take, bitsX + runStart * BitWidth, 0f, (i - runStart) * BitWidth, rowHeight);
                runStart = -1;
            }
        }

        float rampX = RampX(bits);
        float stepH = rowHeight / RampSteps;
        for (int s = 0; s < RampSteps; s++)
            Add(into, ref ordinal, skip, take, rampX, s * stepH, RampWidth * (s + 1) / RampSteps, stepH);
        return ordinal;
    }

    /// <summary>The barcode column for a bound row: two stacked <see cref="ListRowEl"/>s (cells 0–7 and 8–15), each
    /// refilling its own per-slot <see cref="RowCellBuffer"/> from <paramref name="index"/> — ONE closure per slot, zero
    /// allocation on a rebind. <paramref name="rowHeightOf"/> maps an index to its row height (constant for a uniform
    /// list).</summary>
    public static Element Column(IReadSignal<int> index, int bits, Func<int, float> rowHeightOf)
    {
        var first = new RowCellBuffer();
        var second = new RowCellBuffer();
        return new BoxEl
        {
            ZStack = true,
            Width = WidthFor(bits),
            Shrink = 0f,
            Fill = Background,
            HitTestVisible = false,
            Children =
            [
                new ListRowEl(Prop.Of<RowCells>(() =>
                {
                    int i = index.Value;
                    first.Clear();
                    Emit(i, bits, rowHeightOf(i), 0, 8, first);
                    return first.Current;
                })),
                new ListRowEl(Prop.Of<RowCells>(() =>
                {
                    int i = index.Value;
                    second.Clear();
                    Emit(i, bits, rowHeightOf(i), 8, 8, second);
                    return second.Current;
                })),
            ],
        };
    }
}
