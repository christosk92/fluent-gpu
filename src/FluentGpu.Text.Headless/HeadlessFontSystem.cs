using FluentGpu.Foundation;
using FluentGpu.Text;

namespace FluentGpu.Text.Headless;

/// <summary>Deterministic stub font system for tests: monospace-ish advances so layout is reproducible without DirectWrite.</summary>
public sealed class HeadlessFontSystem : IFontSystem
{
    private readonly StringTable _strings;
    public HeadlessFontSystem(StringTable strings) => _strings = strings;

    public TextMetrics Measure(StringId text, in TextStyle style)
    {
        string s = _strings.Resolve(text);
        float advance = style.SizeDip * (style.Bold ? 0.62f : 0.55f);
        float width = s.Length * advance;
        float height = style.SizeDip * 1.4f;
        float baseline = style.SizeDip * 1.1f;
        return new TextMetrics(new Size2(width, height), baseline);
    }
}
