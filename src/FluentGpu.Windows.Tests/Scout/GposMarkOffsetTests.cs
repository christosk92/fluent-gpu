using FluentGpu.Text.DirectWrite;
using Xunit;

namespace FluentGpu.Windows.Tests;

/// <summary>
/// The DirectWrite layout engine carries the shaper's GPOS glyph offsets (DWRITE_GLYPH_OFFSET) into the laid glyphs: a
/// zero-advance combining mark is drawn where mark-to-base attachment puts it, not at the bare pen position. Segoe UI has
/// no precomposed q-acute, so "q́" shapes to q + a combining acute whose GPOS offset pulls it back over the q
/// (about -8.7 DIP across, +1 DIP up at 20 DIP). Hit-test clusters stay on the pen. Real DirectWrite, no GPU.
/// </summary>
public sealed class GposMarkOffsetTests
{
    [Fact]
    public void Combining_mark_is_drawn_at_its_gpos_attachment_not_the_pen()
    {
        using var e = new TextLayoutEngine();
        e.Layout("q́", "Segoe UI", 400, 20f, 1000f, 0, 0, 0);

        var glyphs = e.Glyphs;
        var clusters = e.Clusters;
        Assert.Equal(2, glyphs.Length);
        Assert.Equal(2, clusters.Length);

        // The pen (hit-test) still sits after the q's full advance; the mark adds no advance.
        float pen = clusters[1].X;
        Assert.True(pen > 5f, $"pen after q = {pen}");
        Assert.Equal(0f, clusters[1].Advance);

        // The base is unshifted.
        Assert.Equal(0f, glyphs[0].X);
        // The mark is pulled back over the base (GPOS advanceOffset ≈ -8.7 DIP), not left at the pen...
        Assert.True(glyphs[1].X < pen - 2f, $"mark x {glyphs[1].X} vs pen {pen}");
        // ...and raised off the baseline (ascenderOffset > 0 ⇒ smaller top-down Y).
        Assert.True(glyphs[1].Y < glyphs[0].Y, $"mark y {glyphs[1].Y} vs base y {glyphs[0].Y}");
    }

    [Fact]
    public void Plain_text_glyphs_stay_on_the_pen_and_baseline()
    {
        using var e = new TextLayoutEngine();
        e.Layout("Hello", "Segoe UI", 400, 20f, 1000f, 0, 0, 0);
        var glyphs = e.Glyphs;
        var clusters = e.Clusters;
        for (int i = 0; i < glyphs.Length; i++)
        {
            Assert.Equal(clusters[i].X, glyphs[i].X);
            Assert.Equal(glyphs[0].Y, glyphs[i].Y);
        }
    }
}
