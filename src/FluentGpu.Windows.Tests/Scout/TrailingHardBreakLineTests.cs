using System;
using FluentGpu.Foundation;
using FluentGpu.Text.DirectWrite;
using Xunit;

namespace FluentGpu.Windows.Tests;

/// <summary>
/// A paragraph that ends in a hard break ('\r', the TextBox Enter char) owns an empty last line after it, the way
/// IDWriteTextLayout lays it out: Enter at the end of a multi-line TextBox moves the caret onto a new line and grows the
/// box. A click past the end of the old line still lands before its terminator. Real DirectWrite, no GPU.
/// </summary>
public sealed class TrailingHardBreakLineTests
{
    private const float Size = 14f, Box = 300f;

    private static void Lay(TextLayoutEngine e, string s, int maxLines = 0)
        => e.Layout(s.AsSpan(), "Segoe UI", 400, Size, Box, 1, 0, maxLines);

    private static float OneLine(TextLayoutEngine e) { Lay(e, "abc"); return e.Height; }

    [Fact]
    public void Enter_at_the_end_opens_an_empty_line_holding_the_caret()
    {
        using var e = new TextLayoutEngine();
        float lh = OneLine(e);
        Lay(e, "abc\r");
        Assert.Equal(2, e.LineCount);
        Assert.Equal(2 * lh, e.Height, 3);
        Assert.Equal(2, e.Lines.Length);
        Assert.Equal(4, e.Lines[1].StartChar);
        Assert.Equal(4, e.Lines[1].EndChar);
        Assert.Equal(4, e.Lines[0].EndChar);

        e.CaretAt(4, out float x, out float top, out _, out int li);
        Assert.Equal(1, li);
        Assert.Equal(0f, x);
        Assert.Equal(e.Lines[1].Top, top);
    }

    [Fact]
    public void Second_enter_puts_the_caret_on_the_third_line()
    {
        using var e = new TextLayoutEngine();
        Lay(e, "abc\r\r");
        Assert.Equal(3, e.LineCount);
        e.CaretAt(5, out _, out _, out _, out int li);
        Assert.Equal(2, li);
        e.CaretAt(4, out _, out _, out _, out int li4);
        Assert.Equal(1, li4);
    }

    [Fact]
    public void Clicks_resolve_to_the_line_they_hit()
    {
        using var e = new TextLayoutEngine();
        Lay(e, "abc\r");
        float lh = e.Lines[0].Height;
        // Below the text: the empty last line, after the terminator.
        Assert.Equal(4, e.HitTest(new Point2(500f, 10 * lh), out _));
        // Past the right end of "abc": before the terminator, the caret stays on line 1.
        Assert.Equal(3, e.HitTest(new Point2(500f, lh * 0.5f), out _));
    }

    [Fact]
    public void Crlf_and_the_rewrap_cache_keep_the_empty_line()
    {
        using var e = new TextLayoutEngine();
        Lay(e, "abc\r\n");
        Assert.Equal(2, e.LineCount);
        Lay(e, "abc\r");
        e.Layout("abc\r".AsSpan(), "Segoe UI", 400, Size, 200f, 1, 0, 0);   // width-only change: shape-cache re-wrap
        Assert.Equal(2, e.LineCount);
    }

    [Fact]
    public void Text_without_a_trailing_break_and_capped_paragraphs_are_unchanged()
    {
        using var e = new TextLayoutEngine();
        Lay(e, "abc");
        Assert.Equal(1, e.LineCount);
        Lay(e, "abc\rd");
        Assert.Equal(2, e.LineCount);
        Lay(e, "abc\r", maxLines: 1);
        Assert.Equal(1, e.LineCount);
    }
}
