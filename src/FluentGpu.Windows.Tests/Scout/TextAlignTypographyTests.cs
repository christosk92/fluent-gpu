using System;
using FluentGpu.Foundation;
using FluentGpu.Rhi.D3D12;
using FluentGpu.Text;
using FluentGpu.Text.DirectWrite;
using Xunit;

namespace FluentGpu.Windows.Tests;

/// <summary>
/// TextEl.TextAlign / TextEl.FontFeatures on the real DirectWrite engine (docs/plans/app-authoring-ergonomics-implementation.md
/// §4): alignment shifts the laid glyphs AND the cluster table by the same per-line offset, so the drawn glyph x, the
/// caret and the hit-test agree; trailing spaces hang; justify widens interior gaps except on the last line; the
/// measured box never changes; features are a shaping input (shape cache + run key). Real DirectWrite, no GPU.
/// </summary>
public sealed class TextAlignTypographyTests
{
    private const string Family = "Segoe UI";

    private static void Lay(TextLayoutEngine e, string s, float maxWidth, int wrap, TextAlign align, float alignWidth,
        FontFeatures features = FontFeatures.None)
        => e.Layout(s.AsSpan(), Family, 400, 14f, maxWidth, wrap, 0, 0,
            typography: TextTypography.Pack(align, features), alignWidth: alignWidth);

    [Fact]
    public void End_aligned_nowrap_glyph_x_matches_caret_and_hit_test()
    {
        using var e = new TextLayoutEngine();
        Lay(e, "42", float.PositiveInfinity, 0, TextAlign.Start, float.PositiveInfinity);
        float natural = e.Width;

        Lay(e, "42", 200f, 0, TextAlign.End, 200f);
        float expected = 200f - natural;
        Assert.Equal(expected, e.Glyphs[0].X, 3);
        Assert.Equal(e.Glyphs[0].X, e.Clusters[0].X, 3);
        e.CaretAt(0, out float caretX, out _, out _, out _);
        Assert.Equal(expected, caretX, 3);
        Assert.Equal(0, e.HitTest(new Point2(expected + 1f, 5f), out _));
        Assert.Equal(0, e.HitTest(new Point2(10f, 5f), out _));   // left of the aligned line → line start
        Span<RectF> r = stackalloc RectF[2];
        Assert.Equal(1, e.RangeRects(0, 2, r));
        Assert.Equal(expected, r[0].X, 3);
        Assert.Equal(natural, e.Width, 3);   // alignment never changes the measured box
    }

    [Fact]
    public void Trailing_spaces_do_not_shift_end_alignment()
    {
        using var e = new TextLayoutEngine();
        Lay(e, "42", 200f, 0, TextAlign.End, 200f);
        float plain = e.Glyphs[0].X;
        Lay(e, "42   ", 200f, 0, TextAlign.End, 200f);
        Assert.Equal(plain, e.Glyphs[0].X, 3);
    }

    [Fact]
    public void Center_wrapped_lines_center_on_their_own_visible_width()
    {
        const string s = "Hidden content, revealed when the Expander is expanded.";
        using var e = new TextLayoutEngine();
        Lay(e, s, 150f, 1, TextAlign.Center, 150f);
        Assert.True(e.LineCount >= 2);
        var lines = e.Lines;
        for (int li = 0; li < lines.Length; li++)
        {
            if (lines[li].GlyphCount == 0) continue;
            float w = e.VisibleLineWidth(li);
            Assert.Equal(MathF.Max(0f, (150f - w) * 0.5f), e.Clusters[lines[li].FirstGlyph].X, 2);
        }
    }

    [Fact]
    public void Justify_fills_every_line_but_the_last()
    {
        const string s = "Hidden content, revealed when the Expander is expanded.";
        using var start = new TextLayoutEngine();
        using var e = new TextLayoutEngine();
        Lay(start, s, 150f, 1, TextAlign.Start, 150f);
        Lay(e, s, 150f, 1, TextAlign.Justify, 150f);
        Assert.Equal(start.LineCount, e.LineCount);
        Assert.Equal(start.Width, e.Width, 3);
        Assert.Equal(start.Height, e.Height, 3);
        var lines = e.Lines;
        // Line 0: its last visible cluster's right edge reaches the box.
        Assert.Equal(150f, e.VisibleLineWidth(0), 1);
        // The last line is untouched (Start).
        var last = lines[^1];
        for (int k = last.FirstGlyph; k < last.FirstGlyph + last.GlyphCount; k++)
            Assert.Equal(start.Clusters[k].X, e.Clusters[k].X, 3);
    }

    /// <summary>Span artifacts are laid Start (unjustified) at the measure wrap width; the renderer justifies at the drawn
    /// box. In a non-stretching column the box is the widest natural line, narrower than the wrap width — the published
    /// gap counts (JustifyGapCount / JustifyGapsBefore → SpanRect) re-derive, through TextTypography.PlaceSpanRect, the
    /// exact rects of a layout justified at that box.</summary>
    [Fact]
    public void Justify_gap_counts_rederive_span_rects_at_the_drawn_box()
    {
        const string s = "The quick brown fox jumps over the lazy dog while five boxing wizards jump quickly past the gate.";
        using var measured = new TextLayoutEngine();
        using var drawn = new TextLayoutEngine();
        Lay(measured, s, 200f, 1, TextAlign.Start, float.PositiveInfinity);   // the measure (artifact) layout
        float box = measured.Width;                                           // box shrinks to the widest line
        Lay(drawn, s, box, 1, TextAlign.Justify, box);                        // the renderer: wrap + justify at Bounds.W
        Assert.True(measured.LineCount >= 3);
        Assert.Equal(measured.LineCount, drawn.LineCount);
        Span<RectF> a = stackalloc RectF[4];
        Span<RectF> b = stackalloc RectF[4];
        int widened = 0;
        for (int st = 0; st + 6 <= s.Length; st += 4)
        {
            int en = st + 6;
            int na = measured.RangeRects(st, en, a), nb = drawn.RangeRects(st, en, b);
            Assert.Equal(na, nb);
            for (int f = 0; f < na; f++)
            {
                int li = measured.LineIndexAtTop(a[f].Y);
                var line = measured.Lines[li];
                int gaps = measured.JustifyGapCount(li), before = 0, inside = 0;
                if (gaps > 0)
                {
                    before = measured.JustifyGapsBefore(li, Math.Max(st, line.StartChar));
                    inside = measured.JustifyGapsBefore(li, Math.Min(en, line.EndChar)) - before;
                }
                var art = new SpanRect(a[f], 0, SpanStyle.LinkBit, measured.VisibleLineWidth(li), gaps, before, inside);
                var placed = TextTypography.PlaceSpanRect(in art, TextAlign.Justify, box);
                Assert.Equal(b[f].X, placed.X, 2);
                Assert.Equal(b[f].W, placed.W, 2);
                if (gaps > 0 && before > 0 && box - art.LineWidth > 1f) widened++;
            }
        }
        Assert.True(widened > 0);   // the gate actually exercised a justified, shifted fragment
        Assert.Equal(0, measured.JustifyGapCount(measured.LineCount - 1));   // the last line is never justified
    }

    [Fact]
    public void Measure_is_alignment_independent()
    {
        const string s = "Hidden content, revealed when the Expander is expanded.";
        using var e = new TextLayoutEngine();
        Lay(e, s, 180f, 1, TextAlign.Start, float.PositiveInfinity);
        float w = e.Width, h = e.Height;
        foreach (var a in new[] { TextAlign.Center, TextAlign.End, TextAlign.Justify })
        {
            Lay(e, s, 180f, 1, a, float.PositiveInfinity);
            Assert.Equal(w, e.Width, 3);
            Assert.Equal(h, e.Height, 3);
        }
    }

    [Fact]
    public void Font_features_are_a_shaping_input()
    {
        using var e = new TextLayoutEngine();
        Lay(e, "1234567890", float.PositiveInfinity, 0, TextAlign.Start, float.PositiveInfinity);
        long afterPlain = e.ShapeCount;
        Lay(e, "1234567890", float.PositiveInfinity, 0, TextAlign.Start, float.PositiveInfinity, FontFeatures.TabularFigures);
        Assert.Equal(afterPlain + 1, e.ShapeCount);   // a different feature set never replays the plain shape
        Lay(e, "1234567890", float.PositiveInfinity, 0, TextAlign.End, 300f, FontFeatures.TabularFigures);
        Assert.Equal(afterPlain + 1, e.ShapeCount);   // alignment is positioning only — the tnum shape is reused
    }

    [Fact]
    public void Run_key_separates_align_and_features()
    {
        static RunKey Key(int typo) => GlyphRenderer.MakeRunKey(new StringId(7), new StringId(3), 14f, 400, 200f, 0, 0, 0, 0f, 0f, 1.25f,
            0f, float.NaN, 0, 0, 0, typo);
        Assert.Equal(Key(0), Key(TextTypography.Pack(TextAlign.Start, FontFeatures.None)));
        Assert.NotEqual(Key(0), Key(TextTypography.Pack(TextAlign.End, FontFeatures.None)));
        Assert.NotEqual(Key(0), Key(TextTypography.Pack(TextAlign.Start, FontFeatures.TabularFigures)));
    }
}
