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

    /// <inheritdoc/>
    public long ShapeCount => _engine.ShapeCount;

    public TextMetrics Measure(StringId text, in TextStyle style, float maxWidth = float.PositiveInfinity)
        => Measure(text, in style, maxWidth, publishArtifacts: true);

    /// <inheritdoc/>
    public TextMetrics Measure(StringId text, in TextStyle style, float maxWidth, bool publishArtifacts)
    {
        string s = _strings.Resolve(text);
        // Measure lays out START-aligned — Justify too (alignment never changes the box, and the arranged box width is
        // not known yet); span artifacts carry per-line justify gap counts so consumers justify at the drawn Bounds.W.
        LayoutFor(s.AsSpan(), in style, maxWidth, float.PositiveInfinity, measure: true);
        // Span runs: publish the laid-out link/decoration rect artifacts on the run (read by the recorder for the
        // bars and by the input dispatcher for hyperlink cursor/click) — measure-miss only, steady frames are 0-touch.
        // A pure measure (publishArtifacts false: the SizeToContent probe / auto-fit search) leaves them alone.
        if (publishArtifacts && style.SpanRunId != 0 && SpanRunTable.Shared.Resolve(style.SpanRunId) is { } run)
            PublishSpanArtifacts(run, maxWidth, style.Align);
        return new TextMetrics(new Size2(_engine.Width, _engine.Height), _engine.Baseline,
            _engine.UnderlineY, _engine.UnderlineThickness, _engine.StrikeY, MeasureFirstLineWidth(in style));
    }

    /// <summary>TextMetrics.FirstLineWidth of the (Start-laid) measure layout: the first line's visible width — or +Inf
    /// under Justify when that line is justified (it fills whatever box it is drawn in; the recorder's whole-node
    /// decoration bar then spans the box).</summary>
    private float MeasureFirstLineWidth(in TextStyle style)
        => style.Align == TextAlign.Justify && _engine.JustifyGapCount(0) > 0
            ? float.PositiveInfinity : _engine.VisibleLineWidth(0);

    /// <summary>Bake the CURRENT (Start-laid) layout's span artifacts: per decorated/link span, one entry per covered line
    /// fragment. Link entries are the full line band (hit-test); Underline/Strikethrough entries are the positioned bars
    /// (the paragraph's face-metric positions — TextLayoutEngine.UnderlineY/StrikeY frame, per line via the fragment
    /// top). Each entry carries its line's visible width so a Center/End consumer shifts it by the renderer's line offset
    /// (SpanRect.LineWidth); Justify entries also carry the line's justify gap counts (JustifyGaps/GapsBefore/GapsIn) so
    /// the consumer re-justifies at the drawn box (TextTypography.PlaceSpanRect) — the renderer justifies at Bounds.W,
    /// which is not the measure wrap width when the box shrinks to the widest line.</summary>
    private void PublishSpanArtifacts(SpanRun run, float maxWidth, TextAlign align)
    {
        _artifactScratch.Clear();
        Span<RectF> frags = stackalloc RectF[8];
        var spans = run.Spans;
        var lines = _engine.Lines;
        for (int i = 0; i < spans.Length; i++)
        {
            byte flags = spans[i].Flags;
            if (flags == 0) continue;
            int n = _engine.RangeRects(spans[i].Start, spans[i].End, frags);
            for (int f = 0; f < n; f++)
            {
                float lineW = float.NaN;
                int gaps = 0, before = 0, inside = 0;
                if (align != TextAlign.Start)
                {
                    int li = _engine.LineIndexAtTop(frags[f].Y);
                    lineW = _engine.VisibleLineWidth(li);
                    if (align == TextAlign.Justify && (gaps = _engine.JustifyGapCount(li)) > 0)
                    {
                        // Justifiable ⇒ an ordinary contiguous line: the fragment covers [s, e) of it (RangeRects' clip).
                        int s = Math.Max(spans[i].Start, lines[li].StartChar), e = Math.Min(spans[i].End, lines[li].EndChar);
                        before = _engine.JustifyGapsBefore(li, s);
                        inside = _engine.JustifyGapsBefore(li, e) - before;
                    }
                }
                if ((flags & SpanStyle.LinkBit) != 0)
                    _artifactScratch.Add(new SpanRect(frags[f], i, SpanStyle.LinkBit, lineW, gaps, before, inside));
                if ((flags & SpanStyle.UnderlineBit) != 0)
                    _artifactScratch.Add(new SpanRect(
                        new RectF(frags[f].X, frags[f].Y + _engine.UnderlineY, frags[f].W, _engine.UnderlineThickness), i, SpanStyle.UnderlineBit, lineW, gaps, before, inside));
                if ((flags & SpanStyle.StrikethroughBit) != 0)
                    _artifactScratch.Add(new SpanRect(
                        new RectF(frags[f].X, frags[f].Y + _engine.StrikeY, frags[f].W, _engine.UnderlineThickness), i, SpanStyle.StrikethroughBit, lineW, gaps, before, inside));
            }
        }
        run.PublishRects(new SpanRunRects(maxWidth, _artifactScratch.ToArray()));
    }

    /// <inheritdoc/>
    public bool TryMeasureInk(StringId text, in TextStyle style, float maxWidth, out RectF ink)
    {
        string s = _strings.Resolve(text);
        LayoutFor(s.AsSpan(), in style, maxWidth, float.PositiveInfinity, measure: true);
        return _engine.TryInkBounds(out ink);
    }

    public int HitTestText(ReadOnlySpan<char> text, in TextStyle style, float maxWidth, Point2 point, out bool trailing,
        float alignWidth = float.PositiveInfinity)
    {
        LayoutFor(text, in style, maxWidth, alignWidth);
        return _engine.HitTest(point, out trailing);
    }

    public void GetCaret(ReadOnlySpan<char> text, in TextStyle style, float maxWidth, int charIndex, out float x, out float lineTop, out float lineHeight, out int lineIndex,
        float alignWidth = float.PositiveInfinity)
    {
        LayoutFor(text, in style, maxWidth, alignWidth);
        _engine.CaretAt(charIndex, out x, out lineTop, out lineHeight, out lineIndex);
    }

    public int GetRangeRects(ReadOnlySpan<char> text, in TextStyle style, float maxWidth, int start, int end, Span<RectF> rects,
        float alignWidth = float.PositiveInfinity)
    {
        LayoutFor(text, in style, maxWidth, alignWidth);
        return _engine.RangeRects(start, end, rects);
    }

    /// <summary>The single layout call all members funnel through — identical arguments for Measure and the queries, so
    /// hit-test geometry is computed from the exact wrapping and advances the renderer draws. A non-zero
    /// <c>style.SpanRunId</c> overlays the run's per-range SpanStyles (rtb-01) — still one flow, one wrap pass.
    /// <paramref name="alignWidth"/> is the drawn box width (the same value the glyph op's Bounds.W hands the render
    /// engine) so Center/End/Justify lines sit where they are drawn; <paramref name="measure"/> lays every alignment
    /// Start-aligned, Justify included (span artifacts are published unshifted/unjustified — consumers place them at
    /// the box via TextTypography.PlaceSpanRect).</summary>
    private void LayoutFor(ReadOnlySpan<char> text, in TextStyle style, float maxWidth, float alignWidth, bool measure = false)
    {
        string family = _strings.Resolve(style.FontFamily);
        var run = style.SpanRunId != 0 ? SpanRunTable.Shared.Resolve(style.SpanRunId) : null;
        var spans = run is not null ? run.Spans.AsSpan() : default(ReadOnlySpan<SpanStyle>);
        _engine.Layout(text, family, style.Weight, style.SizeDip, maxWidth, (int)style.Wrap, (int)style.Trim, style.MaxLines,
            style.CharSpacing, style.LineHeight, (int)style.Stacking, (int)style.LineBounds, spans, _strings,
            run?.OverflowSuffixStart ?? -1,
            TextTypography.Pack(measure ? TextAlign.Start : style.Align, style.Features, style.Case),
            alignWidth);
    }

    /// <summary>Releases the DirectWrite engine. A disposed font system stops being the process default
    /// (<see cref="TextSeam.Default"/>), so a later fallback lookup never reaches a released factory.</summary>
    public void Dispose()
    {
        if (ReferenceEquals(TextSeam.Default, this)) TextSeam.Default = null;
        _engine.Dispose();
    }
}
