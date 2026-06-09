using FluentGpu.Foundation;

namespace FluentGpu.Text;

public readonly record struct TextStyle(StringId FontFamily, float SizeDip, bool Bold,
    TextWrap Wrap = TextWrap.NoWrap, TextTrim Trim = TextTrim.None, int MaxLines = 0);

/// <summary>Result of measuring a run: the box the shaped glyphs occupy + the baseline offset from the top, plus the
/// face's decoration metrics — all vertical values measured DOWN from the line top, the same frame as
/// <see cref="Baseline"/>. <see cref="UnderlineY"/>/<see cref="UnderlineThickness"/> place the underline bar and
/// <see cref="StrikeY"/> the strikethrough bar (it reuses the underline thickness). These feed text decorations;
/// backends without face metrics (the experimental GDI path) leave them 0.</summary>
public readonly record struct TextMetrics(Size2 Size, float Baseline,
    float UnderlineY = 0f, float UnderlineThickness = 0f, float StrikeY = 0f);

/// <summary>
/// Text seam. DirectWrite implements it on Windows; <c>Text.Headless</c> provides a deterministic stub for tests.
/// The shaper resolves BiDi/script/fallback and returns FINAL device-space glyph positions in visual order; the
/// renderer treats glyph runs as opaque positioned quads. Beyond measurement, the seam answers the editor queries —
/// hit-test, caret geometry, selection rects — from the SAME layout pipeline (identical wrapping, identical advances),
/// so hit-testing matches rendering exactly. For all members <c>maxWidth</c> is the wrap width
/// (<see cref="float.PositiveInfinity"/> = no wrap), interpreted exactly as <see cref="Measure"/> interprets it.
/// </summary>
public interface IFontSystem
{
    /// <summary>Measure a string under a style (intrinsic content size). Feeds the layout engine. When
    /// <paramref name="maxWidth"/> is finite and the style wraps, the result is the word-wrapped multi-line box.</summary>
    TextMetrics Measure(StringId text, in TextStyle style, float maxWidth = float.PositiveInfinity);

    /// <summary>Point → UTF-16 index. Returns the SNAPPED insertion index — the leading or trailing edge of the hit
    /// glyph cluster — and <paramref name="trailing"/> reports which half of the cluster was hit. Points outside the
    /// laid-out bounds clamp to the nearest valid index (above → first line, below → last line, left → line start,
    /// right → line end), so drag-selection keeps tracking past the box. Editor-hosting backends must implement this;
    /// the default throws (the experimental GDI path cannot host editable text).</summary>
    int HitTestText(ReadOnlySpan<char> text, in TextStyle style, float maxWidth, Point2 point, out bool trailing)
        => throw new NotSupportedException($"{GetType().Name} does not implement text hit-testing.");

    /// <summary>Caret geometry for a UTF-16 index: x + the line's top/height (single line: top = 0, height = the line
    /// height) + line index. Returns the LEADING position of <paramref name="charIndex"/>; at a soft-wrap boundary the
    /// leading position is the START of the continuation line — trailing affinity (pinning the caret to the END of the
    /// wrapped line) is the CALLER's job. <paramref name="charIndex"/> == text length → the trailing edge of the last
    /// line. An index inside a multi-char cluster snaps to the cluster's edges. Editor-hosting backends must implement
    /// this; the default throws.</summary>
    void GetCaret(ReadOnlySpan<char> text, in TextStyle style, float maxWidth, int charIndex, out float x, out float lineTop, out float lineHeight, out int lineIndex)
        => throw new NotSupportedException($"{GetType().Name} does not implement caret metrics.");

    /// <summary>One rect per line fragment covered by [<paramref name="start"/>, <paramref name="end"/>), x-from/x-to
    /// via cluster edges. Returns the count written (≤ <paramref name="rects"/>.Length; excess fragments are dropped —
    /// callers size generously). Zero-width ranges produce nothing. Editor-hosting backends must implement this; the
    /// default throws.</summary>
    int GetRangeRects(ReadOnlySpan<char> text, in TextStyle style, float maxWidth, int start, int end, Span<RectF> rects)
        => throw new NotSupportedException($"{GetType().Name} does not implement selection geometry.");
}
