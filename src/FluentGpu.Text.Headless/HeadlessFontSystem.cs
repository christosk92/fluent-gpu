using FluentGpu.Foundation;
using FluentGpu.Text;

namespace FluentGpu.Text.Headless;

/// <summary>
/// Deterministic stub font system for tests: uniform advances so layout is reproducible without DirectWrite.
/// THE ADVANCE MODEL (headless checks are written against these exact constants):
/// <list type="bullet">
/// <item>advance/char = SizeDip × 0.55 (regular) | SizeDip × 0.62 (bold) — every UTF-16 unit one cell, spaces and
/// control chars included (no hard-break semantics; grapheme/surrogate snapping is the editor's job);</item>
/// <item>line height = SizeDip × 1.4; baseline = SizeDip × 1.1 (down from the line top);</item>
/// <item>UnderlineY = baseline + 1, UnderlineThickness = 1, StrikeY = SizeDip × 0.8 (≈ baseline − half an 0.6-em
/// x-height) — same top-down frame as the baseline;</item>
/// <item>wrap = greedy word-wrap on ' ' runs (a word's trailing spaces ride its line; an over-long unbreakable word
/// overflows rather than breaking mid-word), engaged only when the style wraps ∧ maxWidth is finite ∧ the single-line
/// width exceeds maxWidth; MaxLines stops further wrapping (the remainder accumulates on the last line).</item>
/// </list>
/// <see cref="Measure"/>, <see cref="HitTestText"/>, <see cref="GetCaret"/> and <see cref="GetRangeRects"/> all run the
/// SAME line walk (<see cref="LayoutLines"/>), so headless hit-tests agree with headless layout positions exactly.
/// Line ranges live in a reused grow-only table — 0 steady-state allocation per query.
/// </summary>
public sealed class HeadlessFontSystem : IFontSystem
{
    private struct LineRange { public int Start, End; }

    private readonly StringTable _strings;
    private LineRange[] _lines = new LineRange[4];
    private int _lineCount;

    public HeadlessFontSystem(StringTable strings) => _strings = strings;

    private static float AdvanceOf(in TextStyle style) => style.SizeDip * (style.Bold ? 0.62f : 0.55f);
    private static float LineHeightOf(in TextStyle style) => style.SizeDip * 1.4f;
    private static float BaselineOf(in TextStyle style) => style.SizeDip * 1.1f;

    public TextMetrics Measure(StringId text, in TextStyle style, float maxWidth = float.PositiveInfinity)
    {
        string s = _strings.Resolve(text);
        float advance = AdvanceOf(in style);
        float lineH = LineHeightOf(in style);
        float baseline = BaselineOf(in style);
        bool wrapped = style.Wrap != TextWrap.NoWrap && !float.IsInfinity(maxWidth) && s.Length * advance > maxWidth;
        LayoutLines(s.AsSpan(), in style, maxWidth);
        // An unbreakable over-long word keeps the wrapped box width clamped to maxWidth (1 line, width = maxWidth).
        var size = wrapped ? new Size2(maxWidth, _lineCount * lineH) : new Size2(s.Length * advance, lineH);
        return new TextMetrics(size, baseline, baseline + 1f, 1f, style.SizeDip * 0.8f);
    }

    public int HitTestText(ReadOnlySpan<char> text, in TextStyle style, float maxWidth, Point2 point, out bool trailing)
    {
        trailing = false;
        if (text.Length == 0) return 0;
        LayoutLines(text, in style, maxWidth);
        float lineH = LineHeightOf(in style);
        int li = point.Y < 0f ? 0 : (int)(point.Y / lineH);
        if (li >= _lineCount) li = _lineCount - 1;
        int start = _lines[li].Start, len = _lines[li].End - _lines[li].Start;
        if (len <= 0 || point.X <= 0f) return start;                  // empty line / left of the line → line start
        float cells = point.X / AdvanceOf(in style);
        int cell = (int)cells;
        if (cell >= len) { trailing = true; return _lines[li].End; }  // right of the line → line end (affinity = caller)
        trailing = cells - cell >= 0.5f;
        return start + cell + (trailing ? 1 : 0);
    }

    public void GetCaret(ReadOnlySpan<char> text, in TextStyle style, float maxWidth, int charIndex, out float x, out float lineTop, out float lineHeight, out int lineIndex)
    {
        if (charIndex < 0) charIndex = 0; else if (charIndex > text.Length) charIndex = text.Length;
        LayoutLines(text, in style, maxWidth);
        lineHeight = LineHeightOf(in style);
        int li = _lineCount - 1;
        for (int i = 0; i < _lineCount; i++)
            if (charIndex < _lines[i].End) { li = i; break; }         // boundary index belongs to the NEXT line (leading)
        x = (charIndex - _lines[li].Start) * AdvanceOf(in style);
        lineTop = li * lineHeight;
        lineIndex = li;
    }

    public int GetRangeRects(ReadOnlySpan<char> text, in TextStyle style, float maxWidth, int start, int end, Span<RectF> rects)
    {
        if (start < 0) start = 0;
        if (end > text.Length) end = text.Length;
        if (end <= start) return 0;
        LayoutLines(text, in style, maxWidth);
        float advance = AdvanceOf(in style), lineH = LineHeightOf(in style);
        int written = 0;
        for (int li = 0; li < _lineCount && written < rects.Length; li++)
        {
            int s = start > _lines[li].Start ? start : _lines[li].Start;
            int e = end < _lines[li].End ? end : _lines[li].End;
            if (s >= e) continue;
            rects[written++] = new RectF((s - _lines[li].Start) * advance, li * lineH, (e - s) * advance, lineH);
        }
        return written;
    }

    /// <summary>The single line walk every member runs — the deterministic greedy word-wrap of the advance model above.
    /// Partitions [0, text.Length] into line ranges in the reused grow-only table (no per-call allocation). The line
    /// COUNT matches what counting all natural wraps then capping at MaxLines yields (stopping early ≡ capping), so
    /// Measure's box is unchanged from the pre-seam model.</summary>
    private void LayoutLines(ReadOnlySpan<char> s, in TextStyle style, float maxWidth)
    {
        _lineCount = 0;
        int n = s.Length;
        float advance = AdvanceOf(in style);
        if (style.Wrap == TextWrap.NoWrap || float.IsInfinity(maxWidth) || n * advance <= maxWidth)
        {
            AddLine(0, n);
            return;
        }
        int maxL = style.MaxLines > 0 ? style.MaxLines : int.MaxValue;
        int lineStart = 0; float pen = 0f; int i = 0;
        while (i < n)
        {
            int ws = i; while (i < n && s[i] != ' ') i++;
            float wordW = (i - ws) * advance;
            int spaces = 0; while (i < n && s[i] == ' ') { i++; spaces++; }
            if (pen > 0f && pen + wordW > maxWidth && _lineCount + 1 < maxL)
            {
                AddLine(lineStart, ws);
                lineStart = ws; pen = 0f;
            }
            pen += wordW + spaces * advance;
        }
        AddLine(lineStart, n);
    }

    private void AddLine(int start, int end)
    {
        if (_lines.Length == _lineCount) System.Array.Resize(ref _lines, _lines.Length * 2);
        _lines[_lineCount++] = new LineRange { Start = start, End = end };
    }
}
