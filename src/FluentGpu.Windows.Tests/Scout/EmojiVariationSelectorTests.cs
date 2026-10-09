using System;
using FluentGpu.Text.DirectWrite;
using Xunit;

namespace FluentGpu.Windows.Tests;

/// <summary>
/// A variation selector (VS15/VS16, VS17+) after a char the base face covers shapes WITH that char. The selector has no
/// glyph in Segoe UI, and TextLayoutEngine's covered-prefix scan used to stop on it and hand it to the system fallback
/// alone. That maps it to no font, so it shaped by itself in Segoe UI as a drawn .notdef box (♥️ ©️ ™️ ‼️, and 1️⃣
/// became "1", a box and a stray keycap mark). Real DirectWrite, no GPU.
/// </summary>
public sealed class EmojiVariationSelectorTests
{
    [Theory]
    [InlineData("♥️")]          // ♥️
    [InlineData("©️")]          // ©️
    [InlineData("™️")]          // ™️
    [InlineData("‼️")]          // ‼️
    [InlineData("↔️")]          // ↔️
    [InlineData("♥︎")]          // ♥︎ (text presentation)
    [InlineData("♥\U000E0100")]      // VS17 (surrogate pair)
    [InlineData("1️⃣")]         // 1️⃣
    [InlineData("#️⃣")]         // #️⃣
    public void A_variation_selector_after_a_covered_char_draws_no_notdef_box(string text)
    {
        using var e = new TextLayoutEngine();
        e.Layout(text.AsSpan(), "Segoe UI", 400, 14f, float.PositiveInfinity, 1, 0, 0);
        Assert.NotEmpty(e.Glyphs.ToArray());
        foreach (var g in e.Glyphs)
            Assert.False(g.Gid == 0 && g.Face != 0, $"drawn .notdef glyph in \"{text}\"");
    }

    [Fact]
    public void The_selector_adds_no_advance_inside_a_line()
    {
        using var e = new TextLayoutEngine();
        e.Layout("Hi ♥ there".AsSpan(), "Segoe UI", 400, 14f, float.PositiveInfinity, 1, 0, 0);
        float plain = e.Width;
        e.Layout("Hi ♥️ there".AsSpan(), "Segoe UI", 400, 14f, float.PositiveInfinity, 1, 0, 0);
        Assert.Equal(plain, e.Width, 0.01f);
    }
}
