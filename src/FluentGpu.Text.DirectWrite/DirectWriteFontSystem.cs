using FluentGpu.Foundation;
using FluentGpu.Text;

namespace FluentGpu.Text.DirectWrite;

/// <summary>
/// The DirectWrite text backend (<see cref="IFontSystem"/>) — a thin wrapper over <see cref="TextLayoutEngine"/>.
/// Every member (measurement AND the editor queries) runs the full itemize → shape → wrap/trim → position pipeline
/// (kerning, ligatures, complex script, UAX #14 line breaking, CJK/emoji font fallback), so the measured box, the
/// hit-tested caret and the selection rects all come from the SAME layout the renderer — which uses the same engine —
/// actually draws. measure ≡ hit-test ≡ render: this parity is what keeps shaped glyphs from overlapping (the
/// divergence that read as bold when the renderer was shaped but measurement was per-char). Touched on the UI thread
/// during phase-6 layout and at edit/drag time; the render thread owns a separate engine instance, so queries here can
/// never corrupt render-side buffers. Query results live in the engine's reused grow-only tables — 0 steady-state
/// allocation per keystroke/pointer-move.
/// </summary>
public sealed class DirectWriteFontSystem : IFontSystem, IDisposable
{
    private readonly StringTable _strings;
    private readonly TextLayoutEngine _engine = new();

    public DirectWriteFontSystem(StringTable strings) => _strings = strings;

    public TextMetrics Measure(StringId text, in TextStyle style, float maxWidth = float.PositiveInfinity)
    {
        string s = _strings.Resolve(text);
        LayoutFor(s.AsSpan(), in style, maxWidth);
        return new TextMetrics(new Size2(_engine.Width, _engine.Height), _engine.Baseline,
            _engine.UnderlineY, _engine.UnderlineThickness, _engine.StrikeY);
    }

    public int HitTestText(ReadOnlySpan<char> text, in TextStyle style, float maxWidth, Point2 point, out bool trailing)
    {
        LayoutFor(text, in style, maxWidth);
        return _engine.HitTest(point, out trailing);
    }

    public void GetCaret(ReadOnlySpan<char> text, in TextStyle style, float maxWidth, int charIndex, out float x, out float lineTop, out float lineHeight, out int lineIndex)
    {
        LayoutFor(text, in style, maxWidth);
        _engine.CaretAt(charIndex, out x, out lineTop, out lineHeight, out lineIndex);
    }

    public int GetRangeRects(ReadOnlySpan<char> text, in TextStyle style, float maxWidth, int start, int end, Span<RectF> rects)
    {
        LayoutFor(text, in style, maxWidth);
        return _engine.RangeRects(start, end, rects);
    }

    /// <summary>The single layout call all members funnel through — identical arguments for Measure and the queries, so
    /// hit-test geometry is computed from the exact wrapping and advances the renderer draws.</summary>
    private void LayoutFor(ReadOnlySpan<char> text, in TextStyle style, float maxWidth)
    {
        string family = _strings.Resolve(style.FontFamily);
        _engine.Layout(text, family, style.Weight, style.SizeDip, maxWidth, (int)style.Wrap, (int)style.Trim, style.MaxLines,
            style.CharSpacing, style.LineHeight, (int)style.Stacking, (int)style.LineBounds);
    }

    public void Dispose() => _engine.Dispose();
}
