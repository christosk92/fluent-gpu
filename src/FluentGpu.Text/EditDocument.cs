using System.Buffers;
using System.Globalization;
using System.Text;

namespace FluentGpu.Text;

/// <summary>
/// The text-edit document: a gap buffer of UTF-16 code units (grow by doubling, minimum capacity 64). Every public
/// index is a UTF-16 code-unit offset — the unit WinUI exposes through <c>SelectionStart</c>/<c>MaxLength</c> — so the
/// control layer never converts. Grapheme safety is layered ON TOP via the boundary scanners
/// (<see cref="SnapToGrapheme"/>/<see cref="NextGrapheme"/>/<see cref="PrevGrapheme"/>, BCL UAX#29 segmentation —
/// covers surrogate pairs, combining marks, ZWJ emoji, regional indicators): callers that move a caret must land on
/// those boundaries, while the raw <see cref="Insert"/>/<see cref="Remove"/> primitives deliberately accept any
/// code-unit range so undo replay can restore exact historic states.
///
/// Hard line breaks are a single '\r' (WinUI normalizes CRLF/LF on the way in — see
/// <see cref="NormalizeNewlines(string)"/>); '\n' must never be stored. The boundary scanners exploit that invariant:
/// a position right after '\r' is always a grapheme boundary (UAX#29 GB4: break after a control), so they anchor
/// their forward walk at the surrounding line start instead of offset 0 — O(line length), and text fields are short.
/// </summary>
public sealed class EditDocument
{
    private const int MinCapacity = 64;

    private char[] _buf;
    private int _gapStart;   // first code unit of the gap
    private int _gapEnd;     // first code unit past the gap

    public EditDocument()
    {
        _buf = new char[MinCapacity];
        _gapStart = 0;
        _gapEnd = MinCapacity;
    }

    /// <summary>Document length in UTF-16 code units.</summary>
    public int Length => _buf.Length - (_gapEnd - _gapStart);

    /// <summary>Bumps on every content mutation (insert/remove/reset). Render/measure caches key off this so a stale
    /// shaped layout can never be replayed against edited text.</summary>
    public int Version { get; private set; }

    /// <summary>Random access to one code unit (logical index — the gap is invisible to callers).</summary>
    public char this[int index]
    {
        get
        {
            if ((uint)index >= (uint)Length) throw new ArgumentOutOfRangeException(nameof(index));
            return index < _gapStart ? _buf[index] : _buf[index + (_gapEnd - _gapStart)];
        }
    }

    /// <summary>Insert <paramref name="text"/> at <paramref name="pos"/>. An empty insert is not a mutation (no
    /// <see cref="Version"/> bump). The caller owns newline normalization — '\n' must never reach the buffer.</summary>
    public void Insert(int pos, ReadOnlySpan<char> text)
    {
        if ((uint)pos > (uint)Length) throw new ArgumentOutOfRangeException(nameof(pos));
        if (text.IsEmpty) return;
        EnsureGap(text.Length);
        MoveGapTo(pos);
        text.CopyTo(_buf.AsSpan(_gapStart));
        _gapStart += text.Length;
        Version++;
    }

    /// <summary>Remove <paramref name="length"/> code units at <paramref name="start"/> by absorbing them into the gap
    /// (no copying of the removed range). A zero-length remove is not a mutation.</summary>
    public void Remove(int start, int length)
    {
        ValidateRange(start, length);
        if (length == 0) return;
        MoveGapTo(start);
        _gapEnd += length;
        Version++;
    }

    /// <summary>Copy a range into <paramref name="dst"/> without disturbing the gap (alloc-free read path).</summary>
    public void CopyTo(int start, int length, Span<char> dst)
    {
        ValidateRange(start, length);
        if (dst.Length < length) throw new ArgumentException("Destination too small.", nameof(dst));
        int gap = _gapEnd - _gapStart;
        if (start + length <= _gapStart)
        {
            _buf.AsSpan(start, length).CopyTo(dst);
        }
        else if (start >= _gapStart)
        {
            _buf.AsSpan(start + gap, length).CopyTo(dst);
        }
        else
        {
            int front = _gapStart - start;
            _buf.AsSpan(start, front).CopyTo(dst);
            _buf.AsSpan(_gapEnd, length - front).CopyTo(dst.Slice(front));
        }
    }

    /// <summary>Materialize the whole document as a string (user-gesture edge only — copy, undo capture, GetText).</summary>
    public string GetText() => GetText(0, Length);

    /// <summary>Materialize a range as a string (user-gesture edge only).</summary>
    public string GetText(int start, int length)
    {
        ValidateRange(start, length);
        if (length == 0) return string.Empty;
        return string.Create(length, (self: this, start),
            static (dst, s) => s.self.CopyTo(s.start, dst.Length, dst));
    }

    /// <summary>Replace the entire content (programmatic <c>Text</c> set). Keeps the existing buffer when it fits;
    /// grows by doubling otherwise. Bumps <see cref="Version"/>. Caller owns newline normalization.</summary>
    public void Reset(ReadOnlySpan<char> text)
    {
        if (text.Length > _buf.Length)
        {
            int cap = Math.Max(_buf.Length, MinCapacity);
            while (cap < text.Length) cap *= 2;
            _buf = new char[cap];
        }
        text.CopyTo(_buf);
        _gapStart = text.Length;
        _gapEnd = _buf.Length;
        Version++;
    }

    /// <summary>
    /// Contiguous view of the whole document, obtained by MOVING THE GAP TO THE END first (one memmove of the
    /// post-gap tail). The BCL segmentation APIs need one contiguous span, and a two-segment scanner would have to
    /// re-implement UAX#29 stitching across the seam — moving the gap is simpler and provably correct. Repeated
    /// scans after the move are free; the next interior <see cref="Insert"/>/<see cref="Remove"/> pays the move
    /// back. That trade is deliberate: every caller is a cold user-gesture path (caret nav, word jump, grapheme
    /// snap), never the frame loop, and edit fields are short.
    /// </summary>
    public ReadOnlySpan<char> AsSpan()
    {
        MoveGapTo(Length);
        return _buf.AsSpan(0, _gapStart);
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Grapheme boundaries (UAX#29 extended grapheme clusters via StringInfo — span-based, alloc-free, AOT-safe).
    // ---------------------------------------------------------------------------------------------------------------

    /// <summary>Snap <paramref name="pos"/> DOWN to the nearest grapheme boundary at or before it (clamped to
    /// [0, <see cref="Length"/>]). A position already on a boundary is returned unchanged.</summary>
    public int SnapToGrapheme(int pos)
    {
        int len = Length;
        if (pos <= 0) return 0;
        if (pos >= len) return len;
        return SnapDown(AsSpan(), pos);
    }

    /// <summary>The grapheme boundary after <paramref name="pos"/> — the caret target for a Right arrow. From the
    /// middle of a cluster this lands at the END of that cluster (never inside one).</summary>
    public int NextGrapheme(int pos)
    {
        int len = Length;
        if (pos >= len) return len;
        if (pos < 0) pos = 0;
        ReadOnlySpan<char> s = AsSpan();
        int b = SnapDown(s, pos);
        int next = b + GraphemeLength(s, b);
        return next > len ? len : next;
    }

    /// <summary>The grapheme boundary before <paramref name="pos"/> — the caret target for a Left arrow (and the
    /// range start for a Backspace). From the middle of a cluster this lands at the START of that cluster.</summary>
    public int PrevGrapheme(int pos)
    {
        if (pos <= 0) return 0;
        int len = Length;
        if (pos > len) pos = len;
        ReadOnlySpan<char> s = AsSpan();
        if (s[pos - 1] == '\r') return pos - 1;   // a lone CR is its own grapheme ('\n' never stored)
        int i = LineAnchor(s, pos);
        while (true)
        {
            int step = GraphemeLength(s, i);
            if (i + step >= pos) return i;
            i += step;
        }
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Word boundaries — RichEdit/WinUI Ctrl+arrow semantics, deliberately NOT full UAX#29 word segmentation:
    // word chars are letters/digits/underscore, punctuation runs are their own "words", whitespace separates.
    // Combining marks and format chars (ZWJ) extend whatever run they follow so clusters are never split mid-word.
    // ---------------------------------------------------------------------------------------------------------------

    private const int ClsSpace = 0, ClsWord = 1, ClsPunct = 2, ClsExtend = 3;

    /// <summary>Ctrl+Right target: skip the run under the caret (word chars, or a punctuation run), THEN trailing
    /// whitespace — landing at the START of the next word, exactly like the Windows TextBox. From inside whitespace
    /// only the whitespace is skipped.</summary>
    public int NextWord(int pos)
    {
        int len = Length;
        if (pos >= len) return len;
        if (pos < 0) pos = 0;
        ReadOnlySpan<char> s = AsSpan();
        int i = pos;

        int cls = ClsExtend;   // class of the run under the caret: first non-extender at/after pos
        for (int j = i; j < len;)
        {
            int c = ClassAt(s, j, out int rl);
            if (c != ClsExtend) { cls = c; break; }
            j += rl;
        }
        if (cls == ClsExtend) return len;   // nothing but extenders remain

        if (cls != ClsSpace)
        {
            while (i < len)
            {
                int c = ClassAt(s, i, out int rl);
                if (c != cls && c != ClsExtend) break;
                i += rl;
            }
        }
        while (i < len)
        {
            int c = ClassAt(s, i, out int rl);
            if (c != ClsSpace) break;
            i += rl;
        }
        return i;
    }

    /// <summary>Ctrl+Left target: skip whitespace backward, then the word (or punctuation) run — landing at the
    /// start of the current word when mid-word, or the start of the previous word when at a word start.</summary>
    public int PrevWord(int pos)
    {
        if (pos <= 0) return 0;
        int len = Length;
        if (pos > len) pos = len;
        ReadOnlySpan<char> s = AsSpan();
        int i = pos;

        while (i > 0)
        {
            int c = ClassBefore(s, i, out int rl);
            if (c != ClsSpace) break;
            i -= rl;
        }
        if (i == 0) return 0;

        int cls = ClsExtend;   // class of the run ending at i: first non-extender scanning backward
        for (int j = i; j > 0;)
        {
            int c = ClassBefore(s, j, out int rl);
            if (c != ClsExtend) { cls = c; break; }
            j -= rl;
        }
        if (cls == ClsExtend) return 0;   // nothing but extenders precede

        while (i > 0)
        {
            int c = ClassBefore(s, i, out int rl);
            if (c != cls && c != ClsExtend) break;
            i -= rl;
        }
        return i;
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Hard-break line boundaries ('\r' only — see the class contract). Scanned through the indexer so Home/End
    // do not churn the gap position.
    // ---------------------------------------------------------------------------------------------------------------

    /// <summary>The start of the line containing <paramref name="pos"/>: the index after the preceding '\r', or 0.</summary>
    public int LineStart(int pos)
    {
        int len = Length;
        if (pos > len) pos = len;
        for (int i = pos - 1; i >= 0; i--)
            if (this[i] == '\r')
                return i + 1;
        return 0;
    }

    /// <summary>The end of the line containing <paramref name="pos"/>: the index of the following '\r' (the caret
    /// sits BEFORE the break, like WinUI End), or <see cref="Length"/>.</summary>
    public int LineEnd(int pos)
    {
        int len = Length;
        if (pos < 0) pos = 0;
        if (pos > len) pos = len;
        for (int i = pos; i < len; i++)
            if (this[i] == '\r')
                return i;
        return len;
    }

    /// <summary>Map CRLF and lone LF to the document's '\r' form (WinUI's TextBox normalization). Returns the input
    /// instance unchanged when it contains no '\n' — the overwhelmingly common case costs one IndexOf.</summary>
    public static string NormalizeNewlines(string text)
    {
        if (string.IsNullOrEmpty(text)) return text ?? string.Empty;
        return text.IndexOf('\n') < 0 ? text : NormalizeNewlines(text.AsSpan());
    }

    /// <summary>Span form of <see cref="NormalizeNewlines(string)"/> — always materializes a string (cold paste edge).</summary>
    public static string NormalizeNewlines(ReadOnlySpan<char> text)
    {
        if (text.IsEmpty) return string.Empty;
        if (text.IndexOf('\n') < 0) return new string(text);

        char[]? rented = null;
        Span<char> dst = text.Length <= 512 ? stackalloc char[512] : (rented = ArrayPool<char>.Shared.Rent(text.Length));
        int w = 0;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '\n')
            {
                if (i > 0 && text[i - 1] == '\r') continue;   // CRLF: the '\r' is already written
                c = '\r';                                      // lone LF → CR
            }
            dst[w++] = c;
        }
        string result = new string(dst.Slice(0, w));
        if (rented is not null) ArrayPool<char>.Shared.Return(rented);
        return result;
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Internals.
    // ---------------------------------------------------------------------------------------------------------------

    private void ValidateRange(int start, int length)
    {
        if ((uint)start > (uint)Length) throw new ArgumentOutOfRangeException(nameof(start));
        if (length < 0 || start + length > Length) throw new ArgumentOutOfRangeException(nameof(length));
    }

    /// <summary>Grow (by doubling, never below <see cref="MinCapacity"/>) until the gap can take <paramref name="needed"/>
    /// more code units. Relocation keeps the front block in place and re-homes the tail at the new buffer's end.</summary>
    private void EnsureGap(int needed)
    {
        if (_gapEnd - _gapStart >= needed) return;
        int len = Length;
        int cap = Math.Max(_buf.Length, MinCapacity);
        while (cap - len < needed) cap *= 2;
        var nb = new char[cap];
        Array.Copy(_buf, 0, nb, 0, _gapStart);
        int back = _buf.Length - _gapEnd;
        Array.Copy(_buf, _gapEnd, nb, cap - back, back);
        _buf = nb;
        _gapEnd = cap - back;
    }

    /// <summary>Slide the gap so it starts at <paramref name="pos"/> — one memmove of the span between the old and
    /// new positions. Adjacent edits (the typing case) move nothing.</summary>
    private void MoveGapTo(int pos)
    {
        if (pos < _gapStart)
        {
            int count = _gapStart - pos;
            Array.Copy(_buf, pos, _buf, _gapEnd - count, count);
            _gapStart = pos;
            _gapEnd -= count;
        }
        else if (pos > _gapStart)
        {
            int count = pos - _gapStart;
            Array.Copy(_buf, _gapEnd, _buf, _gapStart, count);
            _gapStart += count;
            _gapEnd += count;
        }
    }

    /// <summary>Length of the grapheme cluster starting at <paramref name="i"/> (defensively never 0, so boundary
    /// walks always terminate).</summary>
    private static int GraphemeLength(ReadOnlySpan<char> s, int i)
    {
        int n = StringInfo.GetNextTextElementLength(s.Slice(i));
        return n > 0 ? n : 1;
    }

    /// <summary>Walk boundaries forward from the surrounding line start (after-'\r' is always a boundary — GB4)
    /// and return the last boundary ≤ <paramref name="pos"/>. Caller guarantees 0 &lt; pos &lt; s.Length.</summary>
    private static int SnapDown(ReadOnlySpan<char> s, int pos)
    {
        int i = LineAnchor(s, pos);
        while (i < pos)
        {
            int step = GraphemeLength(s, i);
            if (i + step > pos) return i;
            i += step;
        }
        return pos;
    }

    /// <summary>The index after the last '\r' strictly before <paramref name="pos"/>, or 0 — the safe anchor a
    /// grapheme walk may start from.</summary>
    private static int LineAnchor(ReadOnlySpan<char> s, int pos)
    {
        for (int j = pos - 1; j >= 0; j--)
            if (s[j] == '\r')
                return j + 1;
        return 0;
    }

    private static int ClassOf(Rune r)
    {
        if (Rune.IsWhiteSpace(r)) return ClsSpace;                  // includes '\r' — breaks separate words
        if (Rune.IsLetterOrDigit(r) || r.Value == '_') return ClsWord;
        UnicodeCategory cat = Rune.GetUnicodeCategory(r);
        return cat is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark
            or UnicodeCategory.EnclosingMark or UnicodeCategory.Format
            ? ClsExtend                                             // combining marks / ZWJ ride the run they follow
            : ClsPunct;
    }

    private static int ClassAt(ReadOnlySpan<char> s, int i, out int runeLen)
    {
        Rune.DecodeFromUtf16(s.Slice(i), out Rune r, out runeLen);  // lone surrogate → U+FFFD, len 1 (punct)
        return ClassOf(r);
    }

    private static int ClassBefore(ReadOnlySpan<char> s, int i, out int runeLen)
    {
        runeLen = i >= 2 && char.IsLowSurrogate(s[i - 1]) && char.IsHighSurrogate(s[i - 2]) ? 2 : 1;
        Rune.DecodeFromUtf16(s.Slice(i - runeLen), out Rune r, out _);
        return ClassOf(r);
    }
}
