using FluentGpu.Foundation;

namespace FluentGpu.Text;

public readonly record struct TextStyle(StringId FontFamily, float SizeDip, bool Bold);

/// <summary>Result of measuring a run: the box the shaped glyphs occupy + the baseline offset from the top.</summary>
public readonly record struct TextMetrics(Size2 Size, float Baseline);

/// <summary>
/// Text seam. DirectWrite implements it on Windows; <c>Text.Headless</c> provides a deterministic stub for tests.
/// The shaper resolves BiDi/script/fallback and returns FINAL device-space glyph positions in visual order; the
/// renderer treats glyph runs as opaque positioned quads. The slice only needs measurement.
/// </summary>
public interface IFontSystem
{
    /// <summary>Measure a string under a style (intrinsic content size). Feeds the layout engine.</summary>
    TextMetrics Measure(StringId text, in TextStyle style);
}
