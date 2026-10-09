using System;
using FluentGpu.Text.DirectWrite;
using Xunit;

namespace FluentGpu.Windows.Tests;

/// <summary>
/// Wrapped text lets trailing whitespace HANG past the box edge (DirectWrite/WinUI): a word that fits stays on its line
/// even when the space after it does not, and a continuation line never starts with that space. Box widths are each
/// prefix's own natural width, so the facts hold for any Segoe UI metrics. Real DirectWrite, no GPU.
/// </summary>
public sealed class TrailingSpaceHangTests
{
    private const float Size = 14f;

    private static float Natural(TextLayoutEngine e, string s)
    {
        e.Layout(s.AsSpan(), "Segoe UI", 400, Size, float.PositiveInfinity, 1, 0, 0);
        return e.Width;
    }

    private static string[] Wrap(TextLayoutEngine e, string s, float maxWidth)
    {
        e.Layout(s.AsSpan(), "Segoe UI", 400, Size, maxWidth, 1, 0, 0);
        var lines = e.Lines;
        var r = new string[lines.Length];
        for (int i = 0; i < lines.Length; i++) r[i] = s.Substring(lines[i].StartChar, lines[i].EndChar - lines[i].StartChar);
        return r;
    }

    [Fact]
    public void WordThatFitsStaysWhenOnlyItsTrailingSpaceOverflows()
    {
        using var e = new TextLayoutEngine();
        string[] lines = Wrap(e, "aa hello world", Natural(e, "aa hello"));
        Assert.Equal(new[] { "aa hello ", "world" }, lines);
        Assert.Equal(2, e.LineCount);
    }

    [Fact]
    public void ContinuationLineDoesNotStartWithTheSpace()
    {
        using var e = new TextLayoutEngine();
        string[] lines = Wrap(e, "hello abc", Natural(e, "hello"));
        Assert.Equal(new[] { "hello ", "abc" }, lines);
    }

    [Fact]
    public void RunOfSpacesNeverBecomesItsOwnLine()
    {
        using var e = new TextLayoutEngine();
        string[] lines = Wrap(e, "a    b", Natural(e, "a"));
        Assert.Equal(new[] { "a    ", "b" }, lines);
        foreach (string l in lines) Assert.NotEqual(' ', l[0]);
    }
}
