namespace FluentGpu.Text;

/// <summary>
/// A per-glyph advance source for the shared wrap/measure math. Implemented as a <c>struct</c> so the generic wrap
/// routines below monomorphize and inline the advance lookup (zero allocation, no virtual dispatch). The advance is the
/// DIP advance for one UTF-16 code unit (design advance × size/em). The render side (D3D12 <c>GlyphRenderer</c>) and the
/// measure side (<c>DirectWriteFontSystem</c>) each supply their own struct over the SAME DirectWrite design advances, so
/// measurement and rendering break lines identically.
/// </summary>
public interface IAdvanceSource
{
    float Advance(char ch);
    float EllipsisAdvance { get; }
}

/// <summary>
/// THE single source of truth for line breaking, ellipsis fitting, and wrapped-box measurement — extracted verbatim from
/// the D3D12 <c>GlyphRenderer</c> shaping so the layout MEASURE path and the glyph RENDER path can never disagree on where
/// lines break or how tall a wrapped block is (the GDI-measure/DWrite-render mismatch that overlapped wrapped content).
/// Pure, COM-free, allocation-free over a <see cref="ReadOnlySpan{Char}"/> + a struct <see cref="IAdvanceSource"/>.
/// <c>wrap</c> uses the <c>TextWrap</c> enum value (0 = NoWrap, 1 = Wrap, 2 = WrapWholeWords); <c>trim</c> uses the
/// <c>TextTrim</c> enum value (0 = None). LTR design-advance positioning (matches the v1 renderer).
/// </summary>
public static class LineBreaker
{
    /// <summary>Measure a (possibly wrapping/trimming) run: line count, widest line width, total box height. Mirrors the
    /// <c>GlyphRenderer.ShapeInto</c> line loop exactly. Width is clamped to <paramref name="maxWidth"/> when finite.</summary>
    public static (int lines, float width, float height) MeasureWrapped<TAdv>(
        ReadOnlySpan<char> text, float maxWidth, int wrap, int trim, int maxLines, float lineHeight, in TAdv adv)
        where TAdv : struct, IAdvanceSource
    {
        int n = text.Length;
        bool doWrap = wrap != 0 && maxWidth > 1f && !float.IsInfinity(maxWidth);
        bool doTrim = trim != 0 && maxWidth > 1f && !float.IsInfinity(maxWidth);
        int maxL = maxLines > 0 ? maxLines : int.MaxValue;

        if (!doWrap && !doTrim && maxLines <= 0)   // fast path: single line (matches the renderer's fast path; ignores '\n')
            return (1, MeasureRange(text, 0, n, in adv), lineHeight);

        float ellipsisW = doTrim ? adv.EllipsisAdvance : 0f;
        int i = 0, line = 0;
        float maxLineW = 0f;
        while (i < n && line < maxL)
        {
            bool lastLine = line == maxL - 1;
            int lineStart = i, end;
            bool ellipsize = false;

            if (!doWrap)
            {
                end = n; for (int k = i; k < n; k++) if (IsHardBreak(text[k])) { end = k; break; }
                if (doTrim && MeasureRange(text, lineStart, end, in adv) > maxWidth)
                { end = FitEllipsis(text, lineStart, end, maxWidth, ellipsisW, in adv); ellipsize = true; }
            }
            else
            {
                end = WrapEnd(text, lineStart, n, maxWidth, wrap, in adv);
                if (lastLine && SkipSpaces(text, end) < n && doTrim)
                { end = FitEllipsis(text, lineStart, end, maxWidth, ellipsisW, in adv); ellipsize = true; }
            }

            float lw = MeasureRange(text, lineStart, end, in adv) + (ellipsize ? ellipsisW : 0f);
            if (lw > maxLineW) maxLineW = lw;

            i = end;
            int afterBreak = SkipHardBreak(text, i);   // consume the LF / CR / CRLF terminator (zero-width)
            if (afterBreak > i) i = afterBreak;
            else if (doWrap) i = SkipSpaces(text, i);
            line++;
            if (ellipsize) break;
        }
        if (line == 0) line = 1;   // empty text → one (empty) line of lineHeight
        float width = float.IsInfinity(maxWidth) ? maxLineW : MathF.Min(maxLineW, maxWidth);
        return (line, width, line * lineHeight);
    }

    /// <summary>Greedy line break: the exclusive end index of the line starting at <paramref name="start"/> that fits within
    /// <paramref name="maxWidth"/> (breaking at word boundaries; inside an over-long word only when wrap == Wrap (1)).</summary>
    public static int WrapEnd<TAdv>(ReadOnlySpan<char> text, int start, int n, float maxWidth, int wrap, in TAdv adv)
        where TAdv : struct, IAdvanceSource
    {
        float pen = 0f; int i = start;
        while (i < n)
        {
            if (IsHardBreak(text[i])) return i;
            int ws = i; float wordW = 0f;
            while (i < n && text[i] != ' ' && !IsHardBreak(text[i])) { wordW += adv.Advance(text[i]); i++; }
            if (pen > 0f && pen + wordW > maxWidth) return ws;          // break before this word
            if (wordW > maxWidth && pen == 0f && wrap == 1)             // a single word longer than the line → break inside (Wrap only)
            {
                float p2 = 0f;
                for (int k = ws; k < i; k++) { float a = adv.Advance(text[k]); if (p2 + a > maxWidth && k > ws) return k; p2 += a; }
                return i;
            }
            pen += wordW;
            while (i < n && text[i] == ' ') { pen += adv.Advance(text[i]); i++; }
        }
        return n;
    }

    /// <summary>The exclusive end index of the longest prefix of [s,e) that fits in <paramref name="maxWidth"/> with room for the ellipsis.</summary>
    public static int FitEllipsis<TAdv>(ReadOnlySpan<char> text, int s, int e, float maxWidth, float ellipsisW, in TAdv adv)
        where TAdv : struct, IAdvanceSource
    {
        float p = 0f; int k = s;
        while (k < e) { float a = adv.Advance(text[k]); if (p + a + ellipsisW > maxWidth && k > s) break; p += a; k++; }
        return k;
    }

    /// <summary>Sum of advances over [s,e). Hard-break characters contribute nothing (zero-width) — a stray '\r' inside a
    /// CRLF line would otherwise add a .notdef advance and over-measure the line.</summary>
    public static float MeasureRange<TAdv>(ReadOnlySpan<char> text, int s, int e, in TAdv adv)
        where TAdv : struct, IAdvanceSource
    {
        float p = 0f; for (int k = s; k < e; k++) { char c = text[k]; if (!IsHardBreak(c)) p += adv.Advance(c); } return p;
    }

    public static int SkipSpaces(ReadOnlySpan<char> text, int i) { while (i < text.Length && text[i] == ' ') i++; return i; }

    /// <summary>A mandatory line-break character (LF or CR — incl. the CR of a CRLF pair): a hard break that carries NO ink.
    /// The shaper still returns a .notdef glyph for it, so it must be a ZERO-WIDTH break, never measured or drawn (else a
    /// [] tofu box paints at every line break).</summary>
    public static bool IsHardBreak(char c) => c is '\n' or '\r';

    /// <summary>If <paramref name="i"/> sits on a hard break, the index just past it (CRLF counts as one); else <paramref name="i"/>.</summary>
    public static int SkipHardBreak(ReadOnlySpan<char> text, int i)
    {
        if (i >= text.Length) return i;
        if (text[i] == '\r') { i++; return i < text.Length && text[i] == '\n' ? i + 1 : i; }   // CRLF or lone CR
        return IsHardBreak(text[i]) ? i + 1 : i;
    }
}
