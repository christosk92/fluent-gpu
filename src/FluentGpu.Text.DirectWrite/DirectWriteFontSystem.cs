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
    private readonly List<SpanRect> _artifactScratch = new();   // reused measure-miss buffer (cold path)

    public DirectWriteFontSystem(StringTable strings)
    {
        _strings = strings;
        TextSeam.Default = this;   // last-constructed wins (the InputHooks.Current.Default convention) — see TextSeam
    }

    public TextMetrics Measure(StringId text, in TextStyle style, float maxWidth = float.PositiveInfinity)
    {
        string s = _strings.Resolve(text);
        LayoutFor(s.AsSpan(), in style, maxWidth);
        // Span runs: publish the laid-out link/decoration rect artifacts on the run (read by the recorder for the
        // bars and by the input dispatcher for hyperlink cursor/click) — measure-miss only, steady frames are 0-touch.
        if (style.SpanRunId != 0 && SpanRunTable.Shared.Resolve(style.SpanRunId) is { } run)
            PublishSpanArtifacts(run, maxWidth);
        return new TextMetrics(new Size2(_engine.Width, _engine.Height), _engine.Baseline,
            _engine.UnderlineY, _engine.UnderlineThickness, _engine.StrikeY);
    }

    /// <summary>Bake the CURRENT layout's span artifacts: per decorated/link span, one entry per covered line fragment.
    /// Link entries are the full line band (hit-test); Underline/Strikethrough entries are the positioned bars (the
    /// paragraph's face-metric positions — TextLayoutEngine.UnderlineY/StrikeY frame, per line via the fragment top).</summary>
    private void PublishSpanArtifacts(SpanRun run, float maxWidth)
    {
        _artifactScratch.Clear();
        Span<RectF> frags = stackalloc RectF[8];
        var spans = run.Spans;
        for (int i = 0; i < spans.Length; i++)
        {
            byte flags = spans[i].Flags;
            if (flags == 0) continue;
            int n = _engine.RangeRects(spans[i].Start, spans[i].End, frags);
            for (int f = 0; f < n; f++)
            {
                if ((flags & SpanStyle.LinkBit) != 0)
                    _artifactScratch.Add(new SpanRect(frags[f], i, SpanStyle.LinkBit));
                if ((flags & SpanStyle.UnderlineBit) != 0)
                    _artifactScratch.Add(new SpanRect(
                        new RectF(frags[f].X, frags[f].Y + _engine.UnderlineY, frags[f].W, _engine.UnderlineThickness), i, SpanStyle.UnderlineBit));
                if ((flags & SpanStyle.StrikethroughBit) != 0)
                    _artifactScratch.Add(new SpanRect(
                        new RectF(frags[f].X, frags[f].Y + _engine.StrikeY, frags[f].W, _engine.UnderlineThickness), i, SpanStyle.StrikethroughBit));
            }
        }
        run.PublishRects(new SpanRunRects(maxWidth, _artifactScratch.ToArray()));
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
    /// hit-test geometry is computed from the exact wrapping and advances the renderer draws. A non-zero
    /// <c>style.SpanRunId</c> overlays the run's per-range SpanStyles (rtb-01) — still one flow, one wrap pass.</summary>
    private void LayoutFor(ReadOnlySpan<char> text, in TextStyle style, float maxWidth)
    {
        string family = _strings.Resolve(style.FontFamily);
        var spans = style.SpanRunId != 0 && SpanRunTable.Shared.Resolve(style.SpanRunId) is { } run
            ? run.Spans.AsSpan() : default(ReadOnlySpan<SpanStyle>);
        _engine.Layout(text, family, style.Weight, style.SizeDip, maxWidth, (int)style.Wrap, (int)style.Trim, style.MaxLines,
            style.CharSpacing, style.LineHeight, (int)style.Stacking, (int)style.LineBounds, spans, _strings);
    }

    public void Dispose() => _engine.Dispose();
}
