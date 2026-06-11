using FluentGpu.Foundation;
using FluentGpu.Text;

namespace FluentGpu.Text.Headless;

/// <summary>
/// Deterministic stub font system for tests: uniform advances so layout is reproducible without DirectWrite.
/// THE ADVANCE MODEL (headless checks are written against these exact constants):
/// <list type="bullet">
/// <item>advance/char = SizeDip × 0.55 (weight &lt; 600) | SizeDip × 0.62 (weight ≥ 600) + SizeDip × CharSpacing/1000
/// (WinUI CharacterSpacing, 1/1000 em) — every UTF-16 unit one cell, spaces and control chars included (no hard-break
/// semantics; grapheme/surrogate snapping is the editor's job). The old bool-Bold model maps exactly: Bold ≡ 700 →
/// 0.62, default ≡ 400 → 0.55, so pre-weight measurements are unchanged;</item>
/// <item>line height = SizeDip × 1.4; baseline = SizeDip × 1.1 (down from the line top). TextLineBounds.Tight trims
/// the line box to cap-height..baseline: capHeight ≈ 0.7 × SizeDip ⇒ line height = baseline = SizeDip × 0.7.
/// An explicit LineHeight resolves per LineStacking (MaxHeight = max(natural, LineHeight); BlockLineHeight =
/// LineHeight exactly) and the baseline scales proportionally (resolved × naturalBaseline/naturalHeight);</item>
/// <item>UnderlineY = baseline + 1, UnderlineThickness = 1, StrikeY = SizeDip × 0.8 (≈ baseline − half an 0.6-em
/// x-height) — same top-down frame as the baseline;</item>
/// <item>wrap = greedy word-wrap on ' ' runs (a word's trailing spaces ride its line; an over-long unbreakable word
/// overflows rather than breaking mid-word), engaged only when the style wraps ∧ maxWidth is finite ∧ the single-line
/// width exceeds maxWidth; MaxLines caps the line count — the last line still wraps at maxWidth and the remainder
/// is dropped (WinUI clips whole lines past the cap; it never dumps the remainder unwrapped).</item>
/// <item>SPAN RUNS (rtb-01, <c>TextStyle.SpanRunId</c> ≠ 0): each char's advance resolves through the covering
/// <see cref="SpanStyle"/> (its weight/size deltas apply per the same constants — a 700-weight span's chars advance at
/// size × 0.62); the line model's SizeDip becomes the MAX size across base + spans. The whole paragraph still wraps
/// as ONE flow over the per-char advances. <c>Measure</c> additionally publishes the run's link/decoration rect
/// artifacts (link = full line band; underline bar at baseline + 1 × 1; strike bar at metricSize × 0.8).</item>
/// </list>
/// <see cref="Measure"/>, <see cref="HitTestText"/>, <see cref="GetCaret"/> and <see cref="GetRangeRects"/> all run the
/// SAME line walk (<see cref="LayoutLines"/>), so headless hit-tests agree with headless layout positions exactly.
/// Line ranges + the span-run prefix table live in reused grow-only tables — 0 steady-state allocation per query.
/// </summary>
public sealed class HeadlessFontSystem : IFontSystem
{
    private struct LineRange { public int Start, End; }

    private readonly StringTable _strings;
    private LineRange[] _lines = new LineRange[4];
    private int _lineCount;
    // Span-run state for the current call: the resolved spans + the per-char prefix-sum X table (xs[i] = advance sum
    // of [0, i)). Null spans ⇒ the uniform fast path (the exact pre-span constants — goldens unchanged).
    private SpanStyle[]? _spans;
    private float[] _xs = new float[64];

    public HeadlessFontSystem(StringTable strings)
    {
        _strings = strings;
        TextSeam.Default = this;   // last-constructed wins (the InputHooks.Current.Default convention) — see TextSeam
    }

    private static float AdvanceOf(in TextStyle style)
        => style.SizeDip * (style.Weight >= 600 ? 0.62f : 0.55f) + style.SizeDip * style.CharSpacing / 1000f;

    /// <summary>Resolve the span overlay for this call; build the per-char prefix table when present.</summary>
    private void ResolveSpans(ReadOnlySpan<char> text, in TextStyle style)
    {
        _spans = style.SpanRunId != 0 ? SpanRunTable.Shared.Resolve(style.SpanRunId)?.Spans : null;
        if (_spans is null) return;
        int n = text.Length;
        if (_xs.Length < n + 1) System.Array.Resize(ref _xs, System.Math.Max(n + 1, _xs.Length * 2));
        float x = 0f;
        int cursor = 0;
        for (int i = 0; i < n; i++)
        {
            _xs[i] = x;
            ushort w = style.Weight; float sz = style.SizeDip;
            while (cursor < _spans.Length && i >= _spans[cursor].End) cursor++;
            if (cursor < _spans.Length && i >= _spans[cursor].Start)
            {
                if (_spans[cursor].Weight != 0) w = _spans[cursor].Weight;
                if (_spans[cursor].SizeDip > 0f) sz = _spans[cursor].SizeDip;
            }
            x += sz * (w >= 600 ? 0.62f : 0.55f) + sz * style.CharSpacing / 1000f;
        }
        _xs[n] = x;
    }

    /// <summary>Prefix X of char <paramref name="i"/> (span path) or i × uniform advance.</summary>
    private float XAt(in TextStyle style, int i) => _spans is not null ? _xs[i] : i * AdvanceOf(in style);

    /// <summary>The size driving the line model: the base SizeDip, lifted to the MAX span size for span runs (a larger
    /// span lifts the whole paragraph's lines — the same merge the DirectWrite engine does).</summary>
    private float MetricSize(in TextStyle style)
    {
        float sz = style.SizeDip;
        if (_spans is not null)
            for (int i = 0; i < _spans.Length; i++)
                if (_spans[i].SizeDip > sz) sz = _spans[i].SizeDip;
        return sz;
    }

    // Font-natural metrics of the advance model (Full: 1.4/1.1 of the em; Tight: cap ≈ 0.7 em, box = cap..baseline).
    private static float NaturalLineHeight(in TextStyle style, float effSize)
        => effSize * (style.LineBounds == TextLineBounds.Tight ? 0.7f : 1.4f);
    private static float NaturalBaseline(in TextStyle style, float effSize)
        => effSize * (style.LineBounds == TextLineBounds.Tight ? 0.7f : 1.1f);
    private static bool HasLineHeight(in TextStyle style) => !float.IsNaN(style.LineHeight) && style.LineHeight > 0f;

    private float LineHeightOf(in TextStyle style)
    {
        float natural = NaturalLineHeight(in style, MetricSize(in style));
        if (!HasLineHeight(in style)) return natural;
        return style.Stacking == LineStacking.BlockLineHeight ? style.LineHeight : MathF.Max(natural, style.LineHeight);
    }

    private float BaselineOf(in TextStyle style)
    {
        float effSize = MetricSize(in style);
        if (!HasLineHeight(in style)) return NaturalBaseline(in style, effSize);   // exact pre-LineHeight constants (goldens)
        return LineHeightOf(in style) * (NaturalBaseline(in style, effSize) / NaturalLineHeight(in style, effSize));
    }

    public TextMetrics Measure(StringId text, in TextStyle style, float maxWidth = float.PositiveInfinity)
    {
        string s = _strings.Resolve(text);
        ResolveSpans(s.AsSpan(), in style);
        float lineH = LineHeightOf(in style);
        float baseline = BaselineOf(in style);
        float total = _spans is not null ? _xs[s.Length] : s.Length * AdvanceOf(in style);
        bool wrapped = style.Wrap != TextWrap.NoWrap && !float.IsInfinity(maxWidth) && total > maxWidth;
        LayoutLines(s.AsSpan(), in style, maxWidth);
        // An unbreakable over-long word keeps the wrapped box width clamped to maxWidth (1 line, width = maxWidth).
        var size = wrapped ? new Size2(maxWidth, _lineCount * lineH) : new Size2(total, lineH);
        // Span runs: publish the link/decoration rect artifacts on the run (recorder bars + dispatcher hyperlinks).
        if (style.SpanRunId != 0 && SpanRunTable.Shared.Resolve(style.SpanRunId) is { } run && ReferenceEquals(run.Spans, _spans))
            PublishSpanArtifacts(run, in style, maxWidth, lineH, baseline);
        float metricSize = MetricSize(in style);
        return new TextMetrics(size, baseline, baseline + 1f, 1f, metricSize * 0.8f);
    }

    private void PublishSpanArtifacts(SpanRun run, in TextStyle style, float maxWidth, float lineH, float baseline)
    {
        var rects = new List<SpanRect>();   // measure-miss only (content/width change) — cold-path alloc, like TextMeasureCache fills
        float strikeY = MetricSize(in style) * 0.8f;
        var spans = run.Spans;
        for (int i = 0; i < spans.Length; i++)
        {
            byte flags = spans[i].Flags;
            if (flags == 0) continue;
            for (int li = 0; li < _lineCount; li++)
            {
                int s = System.Math.Max(spans[i].Start, _lines[li].Start);
                int e = System.Math.Min(spans[i].End, _lines[li].End);
                if (s >= e) continue;
                float x0 = _xs[s] - _xs[_lines[li].Start];
                float w = _xs[e] - _xs[s];
                float top = li * lineH;
                if ((flags & SpanStyle.LinkBit) != 0)
                    rects.Add(new SpanRect(new RectF(x0, top, w, lineH), i, SpanStyle.LinkBit));
                if ((flags & SpanStyle.UnderlineBit) != 0)
                    rects.Add(new SpanRect(new RectF(x0, top + baseline + 1f, w, 1f), i, SpanStyle.UnderlineBit));
                if ((flags & SpanStyle.StrikethroughBit) != 0)
                    rects.Add(new SpanRect(new RectF(x0, top + strikeY, w, 1f), i, SpanStyle.StrikethroughBit));
            }
        }
        run.PublishRects(new SpanRunRects(maxWidth, rects.ToArray()));
    }

    public int HitTestText(ReadOnlySpan<char> text, in TextStyle style, float maxWidth, Point2 point, out bool trailing)
    {
        trailing = false;
        if (text.Length == 0) return 0;
        ResolveSpans(text, in style);
        LayoutLines(text, in style, maxWidth);
        float lineH = LineHeightOf(in style);
        int li = point.Y < 0f ? 0 : (int)(point.Y / lineH);
        if (li >= _lineCount) li = _lineCount - 1;
        int start = _lines[li].Start, len = _lines[li].End - _lines[li].Start;
        if (len <= 0 || point.X <= 0f) return start;                  // empty line / left of the line → line start
        if (_spans is null)
        {
            float cells = point.X / AdvanceOf(in style);
            int cell = (int)cells;
            if (cell >= len) { trailing = true; return _lines[li].End; }  // right of the line → line end (affinity = caller)
            trailing = cells - cell >= 0.5f;
            return start + cell + (trailing ? 1 : 0);
        }
        // Span path: non-uniform cells — walk the prefix table within the line.
        float lineX0 = _xs[start];
        for (int k = start; k < _lines[li].End; k++)
        {
            float cx = _xs[k] - lineX0, cw = _xs[k + 1] - _xs[k];
            if (point.X < cx + cw)
            {
                trailing = point.X >= cx + cw * 0.5f;
                return k + (trailing ? 1 : 0);
            }
        }
        trailing = true;
        return _lines[li].End;
    }

    public void GetCaret(ReadOnlySpan<char> text, in TextStyle style, float maxWidth, int charIndex, out float x, out float lineTop, out float lineHeight, out int lineIndex)
    {
        if (charIndex < 0) charIndex = 0; else if (charIndex > text.Length) charIndex = text.Length;
        ResolveSpans(text, in style);
        LayoutLines(text, in style, maxWidth);
        lineHeight = LineHeightOf(in style);
        int li = _lineCount - 1;
        for (int i = 0; i < _lineCount; i++)
            if (charIndex < _lines[i].End) { li = i; break; }         // boundary index belongs to the NEXT line (leading)
        x = XAt(in style, charIndex) - XAt(in style, _lines[li].Start);
        lineTop = li * lineHeight;
        lineIndex = li;
    }

    public int GetRangeRects(ReadOnlySpan<char> text, in TextStyle style, float maxWidth, int start, int end, Span<RectF> rects)
    {
        if (start < 0) start = 0;
        if (end > text.Length) end = text.Length;
        if (end <= start) return 0;
        ResolveSpans(text, in style);
        LayoutLines(text, in style, maxWidth);
        float lineH = LineHeightOf(in style);
        int written = 0;
        for (int li = 0; li < _lineCount && written < rects.Length; li++)
        {
            int s = start > _lines[li].Start ? start : _lines[li].Start;
            int e = end < _lines[li].End ? end : _lines[li].End;
            if (s >= e) continue;
            float x0 = XAt(in style, s) - XAt(in style, _lines[li].Start);
            float w = XAt(in style, e) - XAt(in style, s);
            rects[written++] = new RectF(x0, li * lineH, w, lineH);
        }
        return written;
    }

    /// <summary>The single line walk every member runs — the deterministic greedy word-wrap of the advance model above.
    /// Partitions [0, text.Length] into line ranges in the reused grow-only table (no per-call allocation). The line
    /// COUNT matches what counting all natural wraps then capping at MaxLines yields (the over-cap remainder is
    /// dropped, so the box is unchanged from capping), so
    /// Measure's box is unchanged from the pre-seam model. Span runs walk the per-char prefix table instead of the
    /// uniform advance — same greedy rule over non-uniform cells.</summary>
    private void LayoutLines(ReadOnlySpan<char> s, in TextStyle style, float maxWidth)
    {
        _lineCount = 0;
        int n = s.Length;
        float total = _spans is not null ? _xs[n] : n * AdvanceOf(in style);
        if (style.Wrap == TextWrap.NoWrap || float.IsInfinity(maxWidth) || total <= maxWidth)
        {
            AddLine(0, n);
            return;
        }
        int maxL = style.MaxLines > 0 ? style.MaxLines : int.MaxValue;
        int lineStart = 0; float pen = 0f; int i = 0;
        while (i < n)
        {
            int ws = i; while (i < n && s[i] != ' ') i++;
            float wordW = XAt(in style, i) - XAt(in style, ws);
            int spaceStart = i; while (i < n && s[i] == ' ') i++;
            float spacesW = XAt(in style, i) - XAt(in style, spaceStart);
            if (pen > 0f && pen + wordW > maxWidth)
            {
                // Line budget reached: the last line still wraps at the word boundary and the remainder is
                // DROPPED (WinUI MaxLines clips whole lines past the cap — never the remainder run-on).
                if (_lineCount + 1 >= maxL) { AddLine(lineStart, ws); return; }
                AddLine(lineStart, ws);
                lineStart = ws; pen = 0f;
            }
            pen += wordW + spacesW;
        }
        AddLine(lineStart, n);
    }

    private void AddLine(int start, int end)
    {
        if (_lines.Length == _lineCount) System.Array.Resize(ref _lines, _lines.Length * 2);
        _lines[_lineCount++] = new LineRange { Start = start, End = end };
    }
}
