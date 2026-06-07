using FluentGpu.Foundation;
using FluentGpu.Text;

namespace FluentGpu.Text.Headless;

/// <summary>Deterministic stub font system for tests: monospace-ish advances so layout is reproducible without DirectWrite.</summary>
public sealed class HeadlessFontSystem : IFontSystem
{
    private readonly StringTable _strings;
    public HeadlessFontSystem(StringTable strings) => _strings = strings;

    public TextMetrics Measure(StringId text, in TextStyle style, float maxWidth = float.PositiveInfinity)
    {
        string s = _strings.Resolve(text);
        float advance = style.SizeDip * (style.Bold ? 0.62f : 0.55f);
        float lineH = style.SizeDip * 1.4f;
        float width = s.Length * advance;
        float baseline = style.SizeDip * 1.1f;

        if (style.Wrap == TextWrap.NoWrap || float.IsInfinity(maxWidth) || width <= maxWidth)
            return new TextMetrics(new Size2(width, lineH), baseline);

        // deterministic greedy word-wrap: count lines by word advances.
        int lines = 1; float pen = 0f; int i = 0, n = s.Length;
        while (i < n)
        {
            int ws = i; while (i < n && s[i] != ' ') i++;
            float wordW = (i - ws) * advance;
            int spaces = 0; while (i < n && s[i] == ' ') { i++; spaces++; }
            if (pen > 0f && pen + wordW > maxWidth) { lines++; pen = 0f; }
            pen += wordW + spaces * advance;
        }
        if (style.MaxLines > 0) lines = System.Math.Min(lines, style.MaxLines);
        return new TextMetrics(new Size2(maxWidth, lines * lineH), baseline);
    }
}
