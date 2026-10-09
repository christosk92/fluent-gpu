using System;
using System.IO;
using FluentGpu.Text.DirectWrite;
using Xunit;

namespace FluentGpu.Windows.Tests;

/// <summary>
/// A run whose FontFamily is a custom font FILE ("path.ttf#Family") still falls back to a system face for the characters
/// the file lacks. ResolveRunFace used to return before MapCharacters for any file family, so CJK and emoji shaped in the
/// file face and drew as .notdef boxes, while the same text in a system family fell back to a CJK / emoji face. The
/// characters the file covers must stay in the file face. Real DirectWrite, no GPU.
/// </summary>
public sealed class CustomFontFileFallbackTests
{
    private static string FontFile(string name) => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), name);

    [Theory]
    [InlineData("segoeui.ttf", "#Segoe UI")]
    [InlineData("arial.ttf", "")]
    public void Uncovered_characters_in_a_file_family_draw_no_notdef_box(string file, string suffix)
    {
        string path = FontFile(file);
        if (!File.Exists(path)) Assert.Skip($"{path} is not installed");
        string family = path + suffix;
        using var e = new TextLayoutEngine();
        e.Layout("Hi 你好 😀".AsSpan(), family, 400, 14f, float.PositiveInfinity, 1, 0, 0);
        Assert.NotEmpty(e.Glyphs.ToArray());
        foreach (var g in e.Glyphs)
            Assert.False(g.Gid == 0 && g.Face != 0, $"drawn .notdef glyph in a {file} run");
    }

    [Fact]
    public void Covered_characters_stay_in_the_file_face()
    {
        string path = FontFile("arial.ttf");
        if (!File.Exists(path)) Assert.Skip($"{path} is not installed");
        using var e = new TextLayoutEngine();
        e.Layout("H".AsSpan(), path, 400, 14f, float.PositiveInfinity, 1, 0, 0);
        nint fileFace = e.Glyphs[0].Face;
        e.Layout("H 日本".AsSpan(), path, 400, 14f, float.PositiveInfinity, 1, 0, 0);
        Assert.Equal(fileFace, e.Glyphs[0].Face);                    // 'H' keeps the custom file face
        Assert.NotEqual(fileFace, e.Glyphs[e.Glyphs.Length - 1].Face); // the CJK falls back to a system face
    }
}
