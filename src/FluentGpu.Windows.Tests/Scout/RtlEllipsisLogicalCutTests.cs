using System;
using System.Collections.Generic;
using System.Linq;
using FluentGpu.Foundation;
using FluentGpu.Text.DirectWrite;
using Xunit;

namespace FluentGpu.Windows.Tests;

/// <summary>
/// A CharacterEllipsis trim keeps the LOGICAL start of a line and drops its logical end (DirectWrite/WinUI), whatever the
/// line's BiDi levels. EmitLine used to fit the VISUAL prefix after the L2 reorder, and an RTL run's visual prefix is its
/// logical tail: a trimmed Hebrew title showed its closing words and lost its opening ones. Box widths derive from each
/// prefix's own natural width, so the facts hold for any Segoe UI metrics. Real DirectWrite, no GPU.
/// </summary>
public sealed class RtlEllipsisLogicalCutTests
{
    private const float Size = 14f;
    private const string Hebrew = "אבגדהוזחטיכלמנ";   // 14 distinct letters: one level-1 run in the LTR paragraph

    private static float Natural(TextLayoutEngine e, string s)
    {
        e.Layout(s.AsSpan(), "Segoe UI", 400, Size, float.PositiveInfinity, 0, 0, 0);
        return e.Width;
    }

    // The distinct source clusters left on the trimmed line, ascending.
    private static int[] Kept(TextLayoutEngine e)
    {
        var set = new SortedSet<int>();
        foreach (var c in e.Clusters) set.Add(c.Cluster);
        return [.. set];
    }

    private static void AssertLogicalPrefix(int[] kept, int textLength)
    {
        Assert.NotEmpty(kept);
        Assert.True(kept.Length < textLength);
        Assert.Equal(Enumerable.Range(0, kept.Length), kept);
    }

    [Fact]
    public void Trimmed_rtl_title_keeps_its_logical_start()
    {
        using var e = new TextLayoutEngine();
        float w = Natural(e, Hebrew) * 0.6f;
        e.Layout(Hebrew.AsSpan(), "Segoe UI", 400, Size, w, 0, 1, 0);
        int[] kept = Kept(e);
        AssertLogicalPrefix(kept, Hebrew.Length);
        // The kept run still reads right-to-left: its logically last kept letter is leftmost.
        Assert.Equal(kept[^1], e.Clusters[0].Cluster);
        // The "…" is the last laid glyph and sits right of every kept letter.
        float ellX = e.Glyphs[^1].X;
        foreach (var c in e.Clusters) Assert.True(c.X + c.Advance <= ellX + 0.01f);
    }

    [Fact]
    public void Rtl_part_of_a_latin_title_keeps_its_first_word()
    {
        const string s = "Song (feat. עומר אדם)";
        const string prefix = "Song (feat. עומר";
        using var e = new TextLayoutEngine();
        float w = Natural(e, prefix) + Natural(e, "…") + 0.5f;
        e.Layout(s.AsSpan(), "Segoe UI", 400, Size, w, 0, 1, 0);
        Assert.Equal(Enumerable.Range(0, prefix.Length), Kept(e));
    }

    [Fact]
    public void Spanned_rtl_title_keeps_its_logical_start()
    {
        using var e = new TextLayoutEngine();
        float w = Natural(e, Hebrew) * 0.6f;
        var spans = new[] { new SpanStyle(0, Hebrew.Length, 700, 0f, default, default, 0) };
        e.Layout(Hebrew.AsSpan(), "Segoe UI", 400, Size, w, 0, 1, 0, spans: spans);
        AssertLogicalPrefix(Kept(e), Hebrew.Length);
    }
}
