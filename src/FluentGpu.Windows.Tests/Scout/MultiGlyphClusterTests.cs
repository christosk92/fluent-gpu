using System;
using FluentGpu.Text.DirectWrite;
using Xunit;

namespace FluentGpu.Windows.Tests;

/// <summary>
/// Every glyph of a multi-glyph cluster carries that cluster's own start char. DirectWrite's clusterMap points only at a
/// cluster's first glyph; the rest used to fall back to the shaped sub-run's start, so a combining mark or a reordered
/// Devanagari matra right after a newline read the newline's MustBreak and got a line of its own. Segoe UI has no
/// precomposed q-acute (GposMarkOffsetTests), and Devanagari falls back to Nirmala UI. Real DirectWrite, no GPU.
/// </summary>
public sealed class MultiGlyphClusterTests
{
    private static void Layout(TextLayoutEngine e, string s) => e.Layout(s.AsSpan(), "Segoe UI", 400, 14f, 1000f, 1, 0, 0);

    [Fact]
    public void Combining_mark_carries_its_base_cluster()
    {
        using var e = new TextLayoutEngine();
        Layout(e, "aq́");
        var clusters = e.Clusters;
        Assert.Equal(3, clusters.Length);
        Assert.Equal(0, clusters[0].Cluster);
        Assert.Equal(1, clusters[1].Cluster);
        Assert.Equal(1, clusters[2].Cluster);   // the mark belongs to the q, not to char 0
    }

    [Fact]
    public void Combining_mark_after_a_hard_break_stays_on_its_line()
    {
        using var e = new TextLayoutEngine();
        Layout(e, "a\nq́");
        Assert.Equal(2, e.LineCount);
        var lines = e.Lines;
        Assert.Equal(2, lines[1].StartChar);
        Assert.Equal(4, lines[1].EndChar);
    }

    [Fact]
    public void Devanagari_word_after_a_hard_break_is_one_line()
    {
        const string s = "Intro\nहिंदी";
        using var e = new TextLayoutEngine();
        Layout(e, s);
        Assert.Equal(2, e.LineCount);
        Assert.Equal(6, e.Lines[1].StartChar);
        foreach (var c in e.Clusters)
            if (c.Line == 1) Assert.InRange(c.Cluster, 6, s.Length - 1);
    }
}
