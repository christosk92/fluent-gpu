using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using FluentGpu.Text;

namespace FluentGpu.Controls;

/// <summary>The inline marks the editor's commands write.</summary>
public enum MarkdownMarkKind : byte { Bold, Italic, Strikethrough, Code, Highlight, Link }

/// <summary>
/// The editor's document: the file's exact characters, one anchor and one caret as source offsets, and one history of
/// replaces (markdown-editor.md §4). Every byte change goes through <see cref="Apply"/>: nothing is ever serialized, so
/// what no command touched cannot change (R1, R2). It knows nothing of the scene: the control asks it where things are and
/// tells it what was pressed, which is also what lets a command be checked without a window.
/// </summary>
public sealed class MarkdownEditModel
{
    private readonly MarkdownOptions _options;
    private readonly TextUndoStack _undo = new();
    private string _text;
    private MdProjection? _projection;
    private int _nextId;
    private int _openIslandAt = -1;
    private bool _lastTypedWasDelimiter;
    private readonly List<MarkdownMarkKind> _pending = new();

    public MarkdownEditModel(string text, MarkdownOptions? options = null)
    {
        _text = text ?? "";
        _options = options ?? MarkdownOptions.Default;
        int start = P.SnapOffset(P.BodyStart);
        Anchor = start;
        Caret = start;
    }

    /// <summary>The file, byte for byte.</summary>
    public string Text => _text;
    public int Anchor { get; private set; }
    public int Caret { get; private set; }
    public int SelectionStart => Math.Min(Anchor, Caret);
    public int SelectionEnd => Math.Max(Anchor, Caret);
    public bool HasSelection => Anchor != Caret;
    /// <summary>True while the note shows its source (Ctrl+/). Caret and selection stay put: they are source offsets either way.</summary>
    public bool IsRaw { get; private set; }
    public bool CanUndo => _undo.CanUndo;
    public bool CanRedo => _undo.CanRedo;
    public int UndoDepth => _undo.UndoDepth;
    /// <summary>Bumped by every change to the text or the shown form.</summary>
    public int Version { get; private set; }
    /// <summary>Fired on every replace, inserted text after removed: (start, removed, inserted).</summary>
    public Action<int, string, string>? OnEdit;

    internal MdProjection P => _projection ??= new MdProjection(_text, IsRaw, _options, _openIslandAt, _lastProjection, ref _nextId);
    private MdProjection? _lastProjection;
    internal MdFlow? CurrentFlow { get { int i = P.IndexAt(Caret); return i < 0 ? null : P.Flows[i]; } }
    internal int OpenIslandAt => _openIslandAt;
    internal IReadOnlyList<MarkdownMarkKind> PendingMarks => _pending;

    /// <summary>What the note shows, one line per flow: the source with its markers concealed.</summary>
    public string VisibleText
    {
        get
        {
            var sb = new StringBuilder();
            foreach (var f in P.Flows) sb.Append(f.IsObject ? "[" + f.Object + "]" : f.Display).Append('\n');
            return sb.ToString();
        }
    }

    /// <summary>The text shown in the block under the caret, for a command bar's style label.</summary>
    public int CurrentHeadingLevel => CurrentFlow is { Kind: MdFlowKind.Heading } f ? f.HeadingLevel : 0;

    // ── the one way a byte changes ─────────────────────────────────────────────────────────────────────────────────

    private void Apply(int start, int removeLength, string inserted, int anchorAfter, int caretAfter, byte runKind = TextUndoOp.KindOther, bool seal = true)
    {
        var p = P;
        if (start < p.ProtectedEnd)   // R5: no edit reaches the file's head
        {
            Debug.Fail("An edit reached the protected range.");
            return;
        }
        string before = _text;
        string removed = before.Substring(start, removeLength);
        if (removed.Length == 0 && inserted.Length == 0) { SetSelectionRaw(anchorAfter, caretAfter); return; }
        _text = string.Concat(before.AsSpan(0, start), inserted, before.AsSpan(start + removeLength));
        Debug.Assert(_text.AsSpan(0, start).SequenceEqual(before.AsSpan(0, start)), "Bytes before a replace changed.");
        Debug.Assert(_text.AsSpan(start + inserted.Length).SequenceEqual(before.AsSpan(start + removeLength)), "Bytes after a replace changed.");
        var op = new TextUndoOp(start, removed, inserted, Anchor, Caret, anchorAfter, caretAfter, runKind);
        _undo.Push(in op, seal);
        ShiftOpenIsland(start, removed.Length, inserted.Length);
        Invalidate();
        SetSelectionRaw(anchorAfter, caretAfter);
        OnEdit?.Invoke(start, removed, inserted);
    }

    private void ShiftOpenIsland(int start, int removed, int inserted)
    {
        if (_openIslandAt < 0) return;
        if (start + removed <= _openIslandAt) _openIslandAt += inserted - removed;
        else if (start < _openIslandAt) _openIslandAt = -1;
    }

    private void Invalidate()
    {
        _lastProjection = _projection ?? _lastProjection;
        _projection = null;
        Version++;
    }

    private void SetSelectionRaw(int anchor, int caret)
    {
        Anchor = Math.Clamp(anchor, 0, _text.Length);
        Caret = Math.Clamp(caret, 0, _text.Length);
    }

    /// <summary>A write from outside (another app saved the file): one minimal replace by common prefix and suffix, caret mapped
    /// through it. History ends here: its offsets describe a text that no longer exists.</summary>
    public void SetExternalText(string text)
    {
        text ??= "";
        if (string.Equals(text, _text, StringComparison.Ordinal)) return;
        int pre = 0, max = Math.Min(text.Length, _text.Length);
        while (pre < max && text[pre] == _text[pre]) pre++;
        int suf = 0;
        while (suf < max - pre && text[text.Length - 1 - suf] == _text[_text.Length - 1 - suf]) suf++;
        int removed = _text.Length - pre - suf, inserted = text.Length - pre - suf;
        int Map(int o) => o <= pre ? o : o >= pre + removed ? o + inserted - removed : pre + inserted;
        int a = Map(Anchor), c = Map(Caret);
        _text = text;
        _undo.Clear();
        _openIslandAt = -1;
        Invalidate();
        SetSelectionRaw(P.SnapOffset(a), P.SnapOffset(c));
    }

    /// <summary>Show the source, or the note. No byte moves.</summary>
    public void ToggleRaw()
    {
        IsRaw = !IsRaw;
        _openIslandAt = -1;
        _undo.SealRun();
        Invalidate();
        SetSelectionRaw(P.SnapOffset(Anchor), P.SnapOffset(Caret));
    }

    // ── selection ──────────────────────────────────────────────────────────────────────────────────────────────────

    public void Select(int anchor, int caret)
    {
        _undo.SealRun();
        _lastTypedWasDelimiter = false;
        _pending.Clear();
        SetSelectionRaw(P.SnapOffset(anchor), P.SnapOffset(caret));
        CloseIslandIfLeft();
    }

    internal void SetCaret(int offset, bool extend)
    {
        int p = P.SnapOffset(offset);
        Select(extend ? Anchor : p, p);
    }

    /// <summary>A flow's whole text, for a triple click.</summary>
    internal void SelectFlow(MdFlow f) => Select(f.Start, f.End);

    /// <summary>Everything after the protected head (R5).</summary>
    public void SelectAll()
    {
        var p = P;
        if (p.Flows.Count == 0) return;
        Select(p.Flows[0].Start, p.Flows[^1].End);
    }

    private void CloseIslandIfLeft()
    {
        if (_openIslandAt < 0) return;
        var f = CurrentFlow;
        if (f is null || (f.Kind == MdFlowKind.CodeLine && f.BlockStart == _openIslandAt)) return;
        _openIslandAt = -1;
        Invalidate();
    }

    /// <summary>Move one stop. An object is selected whole on arrival and left on the next press.</summary>
    public void MoveHorizontal(int direction, bool extend, bool word = false)
    {
        var p = P;
        if (!extend && HasSelection)
        {
            // A plain arrow collapses a selection toward its travel, unless the selection is one object: then it leaves it.
            int edge = direction > 0 ? SelectionEnd : SelectionStart;
            Select(edge, edge);
            if (word || !(CurrentFlow?.IsObject ?? false)) { if (!word) return; }
        }
        int i = p.IndexAt(Caret);
        if (i < 0) return;
        var f = p.Flows[i];
        int target;
        if (!f.IsObject)
        {
            int d = f.ToDisplay(Caret);
            bool atEdge = direction > 0 ? d >= f.Length : d <= 0;
            if (!atEdge)
            {
                int nd = word ? (direction > 0 ? f.WordRight(d) : f.WordLeft(d)) : direction > 0 ? f.NextStop(d) : f.PrevStop(d);
                target = direction > 0 ? f.LeftOf(nd) : f.RightOf(nd);
                SetCaret(target, extend);
                return;
            }
        }
        int ni = i + direction;
        if (ni < 0 || ni >= p.Flows.Count)
        {
            // Past the last unit, still before a closer: the file's end of this flow is one quiet step further.
            target = direction > 0 ? f.End : f.Start;
            SetCaret(target, extend);
            return;
        }
        var g = p.Flows[ni];
        target = direction > 0 ? g.Start : g.End;
        if (g.IsObject && !extend)
        {
            Select(direction > 0 ? g.Start : g.End, direction > 0 ? g.End : g.Start);
            return;
        }
        SetCaret(target, extend);
    }

    public void MoveToDocEdge(int direction, bool extend)
    {
        var p = P;
        if (p.Flows.Count == 0) return;
        SetCaret(direction < 0 ? p.Flows[0].Start : p.Flows[^1].End, extend);
    }

    // ── typing ─────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Text typed or committed by an IME. The file holds what was typed; it converts when the parse says it is complete.
    /// A delimiter run is its own undo step, so Ctrl+Z takes back only the closer.</summary>
    public void InsertTyped(string s)
    {
        if (s.Length == 0) return;
        var p = P;
        if (HasSelection)
        {
            ReplaceRange(SelectionStart, SelectionEnd, s, TextUndoOp.KindOther, seal: true);
            _lastTypedWasDelimiter = false;
            return;
        }
        if (Caret < p.ProtectedEnd) return;
        var f = CurrentFlow;
        string text = s;
        int caretInText = s.Length;
        int at = Caret;
        if (f is { Length: 0, InsertPrefix.Length: > 0 } && at == f.Start && !(s[0] == ' ' && f.InsertPrefix == " "))
        {
            text = f.InsertPrefix + s;
            caretInText = text.Length;
        }
        if (_pending.Count > 0 && !char.IsWhiteSpace(s[0]))
        {
            // A mark asked for with no selection: written around the first character, so it is never a lone `****` (R7).
            var open = new StringBuilder();
            var close = new StringBuilder();
            foreach (var k in _pending) { string m = MarkerFor(k, at, at); open.Append(m); close.Insert(0, m); }
            text = open + text;
            caretInText = text.Length;
            text += close;
            _pending.Clear();
        }
        char c = s[0];
        bool delimiter = s.Length == 1 && c is '*' or '_' or '~' or '=' or '`';
        if (delimiter != _lastTypedWasDelimiter) _undo.SealRun();
        _lastTypedWasDelimiter = delimiter;
        Apply(at, 0, text, at + caretInText, at + caretInText, TextUndoOp.KindTyping, seal: false);
    }

    /// <summary>Pasted text: one undo step, in the note's newlines (R4). A web address over a selection becomes a link.</summary>
    public void Paste(string s)
    {
        if (s.Length == 0) return;
        if (HasSelection && !IsRaw && LooksLikeUrl(s))
        {
            int a = SelectionStart, b = SelectionEnd;
            if (P.IndexAt(a) == P.IndexAt(b) && !(CurrentFlow?.IsObject ?? true))
            {
                WrapAsLink(a, b, s.Trim());
                return;
            }
        }
        string normal = _text.IndexOfAny(['\r', '\n']) >= 0 ? NormalizeEols(s, P.Eol) : s;
        _undo.SealRun();
        _lastTypedWasDelimiter = false;
        ReplaceRange(SelectionStart, SelectionEnd, normal, TextUndoOp.KindOther, seal: true);
    }

    private static bool LooksLikeUrl(string s)
    {
        var t = s.AsSpan().Trim();
        if (t.IndexOfAny(" \r\n\t") >= 0) return false;
        return t.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || t.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            || t.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeEols(string s, string eol)
    {
        var sb = new StringBuilder(s.Length + 8);
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c == '\r') { if (i + 1 < s.Length && s[i + 1] == '\n') i++; sb.Append(eol); }
            else if (c == '\n') sb.Append(eol);
            else sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>The source of the selection, with any marker it cut through closed, so what lands elsewhere still reads as written here.</summary>
    public string CopySelection()
    {
        if (!HasSelection) return "";
        int a = SelectionStart, b = SelectionEnd;
        if (IsRaw) return _text.Substring(a, b - a);
        var pre = new StringBuilder();
        var post = new StringBuilder();
        foreach (var pair in PairsIn(a, b))
        {
            bool openIn = a <= pair.OpenStart && pair.OpenEnd <= b;
            bool closeIn = a <= pair.CloseStart && pair.CloseEnd <= b;
            bool wrapsCut = pair.OpenEnd <= a && b <= pair.CloseStart;
            if (wrapsCut || (closeIn && !openIn)) pre.Insert(0, _text.AsSpan(pair.OpenStart, pair.OpenEnd - pair.OpenStart));
            if (wrapsCut || (openIn && !closeIn)) post.Append(_text.AsSpan(pair.CloseStart, pair.CloseEnd - pair.CloseStart));
        }
        return pre + _text.Substring(a, b - a) + post;
    }

    private IEnumerable<MdPair> PairsIn(int a, int b)
    {
        var p = P;
        if (p.Flows.Count == 0) yield break;
        int i = p.IndexAt(a), j = p.IndexAt(b);
        for (int k = Math.Max(0, i); k <= j && k < p.Flows.Count; k++)
            foreach (var pair in p.Flows[k].Pairs)
                if (pair.OpenStart < b && pair.CloseEnd > a) yield return pair;
    }

    /// <summary>Cut [a, b) and write <paramref name="inserted"/> there as one replace. A marker whose partner survives the cut
    /// is written back (R7), and a pair left holding nothing goes with its text.</summary>
    private void ReplaceRange(int a, int b, string inserted, byte kind, bool seal)
    {
        int ra = a, rb = b;
        string pre = "", post = "";
        if (!IsRaw)
        {
            foreach (var pair in PairsIn(a, b))
            {
                bool openIn = a <= pair.OpenStart && pair.OpenEnd <= b;
                bool closeIn = a <= pair.CloseStart && pair.CloseEnd <= b;
                if (openIn == closeIn)
                {
                    // Both delimiters go, or neither. Neither, with all the inner text gone and nothing written back: the pair goes too.
                    if (!openIn && inserted.Length == 0 && a <= pair.OpenEnd && pair.CloseStart <= b)
                    {
                        ra = Math.Min(ra, pair.OpenStart);
                        rb = Math.Max(rb, pair.CloseEnd);
                    }
                    continue;
                }
                if (openIn)
                {
                    if (b >= pair.CloseStart) rb = Math.Max(rb, pair.CloseEnd);   // nothing left inside
                    else post = _text.Substring(pair.OpenStart, pair.OpenEnd - pair.OpenStart) + post;
                }
                else
                {
                    if (a <= pair.OpenEnd) ra = Math.Min(ra, pair.OpenStart);
                    else pre += _text.Substring(pair.CloseStart, pair.CloseEnd - pair.CloseStart);
                }
            }
        }
        string full = pre + inserted + post;
        int caret = ra + pre.Length + inserted.Length;
        Apply(ra, rb - ra, full, caret, caret, kind, seal);
        if (seal) _undo.SealRun();
    }

    // ── keys with block meaning ────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Enter. One replace, one undo step: the new line copies its neighbours (R3) in the note's newline (R4).</summary>
    public void Enter()
    {
        _lastTypedWasDelimiter = false;
        var p = P;
        string eol = p.Eol;
        if (IsRaw)
        {
            ReplaceRange(SelectionStart, SelectionEnd, eol, TextUndoOp.KindOther, seal: true);
            return;
        }
        var f = CurrentFlow;
        if (f is null) return;

        // A selected object: a table opens its source, a rule gets a paragraph after it.
        if (f.IsObject && (HasSelection || Caret == f.End || Caret == f.Start))
        {
            if (HasSelection && f.Object is MdObjectKind.Table or MdObjectKind.Html or MdObjectKind.Code && SelectionStart == f.Start && SelectionEnd == f.End)
            {
                OpenIsland(f);
                return;
            }
            InsertParagraphAfter(f);
            return;
        }

        if (HasSelection)
        {
            ReplaceRange(SelectionStart, SelectionEnd, "", TextUndoOp.KindOther, seal: false);
            p = P;
            f = CurrentFlow;
            if (f is null) return;
        }
        int at = Caret;
        int lineStart = p.LineStartOf(at), lineEnd = p.LineEndOf(at);

        if (f.Kind == MdFlowKind.CodeLine)
        {
            // Code keeps the line's indent. Inside the block nothing else happens: its fences stay where they are.
            int ind = lineStart;
            while (ind < at && _text[ind] is ' ' or '\t') ind++;
            string nl = eol + _text.Substring(lineStart, ind - lineStart);
            Apply(at, 0, nl, at + nl.Length, at + nl.Length);
            return;
        }

        // ``` plus Enter also writes the closing fence, so the rest of the note never flips to code.
        if (f.Kind == MdFlowKind.Paragraph && at == f.End && IsOpeningFence(f.Display))
        {
            string fence = FenceRun(f.Display);
            string body = eol + eol + fence;
            Apply(at, 0, body, at + eol.Length, at + eol.Length);
            return;
        }

        string prefix = _text.Substring(lineStart, Math.Max(0, Math.Min(f.Start, at) - lineStart));
        if (at < f.Start) prefix = "";
        if (f.Kind == MdFlowKind.Heading && prefix.IndexOf('#') is >= 0 and var hashAt) prefix = prefix.Substring(0, hashAt);
        string cont = ContinuationOf(prefix);

        // An empty item or quote line: Enter leaves the container.
        if (f.Length == 0 && (f.Item is not null || f.InQuote))
        {
            int from = f.Item is { } emptyItem ? emptyItem.MarkerStart : lineStart;
            int le = p.LineEndOf(f.Start);
            if (f.Item is not null && from > lineStart && IsOnlySpaces(lineStart, from)) from = lineStart;
            if (f.Item is null) from = lineStart;
            bool prevHasText = lineStart > 0;
            string rep = prevHasText ? eol : "";
            Apply(from, le - from, rep, from + rep.Length, from + rep.Length);
            return;
        }

        // A list item splits into two items.
        if (f.Item is { } item && f.FirstInItem)
        {
            string marker = NextMarker(item);
            string inner = lineStart < item.MarkerStart ? ContinuationOf(_text.Substring(lineStart, item.MarkerStart - lineStart)) : "";
            string sep = item.Tight ? eol : eol + inner.TrimEnd(' ') + eol;
            string add = sep + inner + marker;
            int insertAt = at;
            int skip = 0;
            if (at == f.End) { insertAt = LeafEnd(f); }
            else
            {
                // A soft break the caret stands at: the break becomes the item boundary.
                while (insertAt + skip < _text.Length && _text[insertAt + skip] == ' ') skip++;
            }
            string repl = add;
            Apply(insertAt, skip, repl, insertAt + repl.Length, insertAt + repl.Length);
            return;
        }

        // A paragraph or heading splits, or gets a new empty paragraph after it. Blank lines carry the container's prefix.
        string blank = cont.TrimEnd(' ');
        if (f.Length == 0)
        {
            // An empty paragraph line: another below it.
            int le = p.LineEndOf(at);
            string nl = eol;
            Apply(le, 0, nl, le + nl.Length, le + nl.Length);
            return;
        }
        if (at == f.End || IsOnlySpaces(at, lineEnd) && f.End <= lineEnd)
        {
            int end = LeafEnd(f);
            int afterLine = p.AfterEol(end);
            int nextEnd = p.LineEndOf(afterLine);
            bool endsNote = end >= _text.Length;
            bool nextBlank = !endsNote && afterLine < _text.Length && IsOnlySpaces(afterLine, nextEnd);
            if (f.ContinuesBelow)
            {
                // The end of a hard-broken line: the paragraph splits at the break.
                int brk = p.LineEndOf(at);
                Apply(brk, 0, eol, p.AfterEol(brk) + eol.Length, p.AfterEol(brk) + eol.Length);
                return;
            }
            if (nextBlank)
            {
                // One blank line is already there and shows nothing: one more makes the empty paragraph.
                string one = eol + blank;
                int caret = end + one.Length + (p.AfterEol(end) - end) + 0;
                Apply(end, 0, one, caret, caret);
                return;
            }
            string two = eol + blank + eol + cont;
            Apply(end, 0, two, end + two.Length, end + two.Length);
            return;
        }
        if (at == f.Start)
        {
            // At the very start: an empty paragraph above, the caret stays with the text.
            string add = eol;
            int ls = lineStart;
            bool hasBlankAbove = ls > p.BodyStart && LineAboveIsBlank(ls);
            if (hasBlankAbove || ls == p.BodyStart)
            {
                Apply(ls, 0, add, at + add.Length, at + add.Length);
                return;
            }
        }
        {
            // In the middle of the text. Where the caret stands at a soft break, that newline is half the paragraph break.
            int d = f.ToDisplay(at);
            bool atSoft = d < f.Length && f.Display[d] == ' ' && f.S[d] == at && f.E[d] > at && _text.AsSpan(at, f.E[d] - at).IndexOfAny('\r', '\n') >= 0;
            if (atSoft)
            {
                int unit = f.E[d];
                Apply(p.LineEndOf(at), 0, eol + blank, unit + (eol + blank).Length, unit + (eol + blank).Length);
                return;
            }
            bool afterSoft = at == lineStart + prefix.Length && lineStart > f.Start;
            if (afterSoft)
            {
                Apply(lineStart, 0, blank + eol, at + (blank + eol).Length, at + (blank + eol).Length);
                return;
            }
            string mid = eol + blank + eol + cont;
            Apply(at, 0, mid, at + mid.Length, at + mid.Length);
        }
    }

    private bool LineAboveIsBlank(int lineStart)
    {
        int prevEnd = lineStart;
        if (prevEnd > 0 && _text[prevEnd - 1] == '\n') prevEnd--;
        if (prevEnd > 0 && _text[prevEnd - 1] == '\r') prevEnd--;
        int ps = P.LineStartOf(prevEnd);
        return IsOnlySpaces(ps, prevEnd);
    }

    private int LeafEnd(MdFlow f) => f.ContinuesBelow ? f.End : Math.Max(f.BlockEnd, f.End);

    private bool IsOnlySpaces(int a, int b)
    {
        for (int i = a; i < b; i++) if (_text[i] != ' ') return false;
        return true;
    }

    private static bool IsOpeningFence(string line)
    {
        if (line.Length < 3 || line[0] != '`') return false;
        int n = 0;
        while (n < line.Length && line[n] == '`') n++;
        return n >= 3 && line.IndexOf('`', n) < 0;
    }

    private static string FenceRun(string line)
    {
        int n = 0;
        while (n < line.Length && line[n] == '`') n++;
        return line.Substring(0, n);
    }

    /// <summary>A line's container prefix as a continuation: quote markers stay, list markers become their width in spaces.</summary>
    private static string ContinuationOf(string prefix)
    {
        if (prefix.Length == 0) return "";
        var sb = new StringBuilder(prefix.Length);
        foreach (char c in prefix) sb.Append(c is '>' or ' ' or '\t' ? c : ' ');
        return sb.ToString();
    }

    /// <summary>The marker the next sibling gets: the same bullet or delimiter, the next number at its width, an empty box on a task (R3).</summary>
    private string NextMarker(MdItem item)
    {
        int m = item.MarkerStart;
        int e = m;
        string marker;
        if (item.Ordered)
        {
            while (e < _text.Length && char.IsAsciiDigit(_text[e])) e++;
            char delim = e < _text.Length ? _text[e] : '.';
            string written = _text.Substring(m, e - m);
            string next = (item.Ordinal + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (written.Length > 1 && written[0] == '0') next = next.PadLeft(written.Length, '0');
            int gap = e + 1;
            int spaces = 0;
            while (gap + spaces < _text.Length && _text[gap + spaces] == ' ') spaces++;
            marker = next + delim + new string(' ', Math.Max(1, Math.Min(spaces, 4)));
        }
        else
        {
            int gap = m + 1;
            int spaces = 0;
            while (gap + spaces < _text.Length && _text[gap + spaces] == ' ') spaces++;
            marker = _text[m] + new string(' ', Math.Max(1, Math.Min(spaces, 4)));
        }
        return item.Checked is null ? marker : marker + "[ ] ";
    }

    /// <summary>Backspace. At the start of a block it takes the block's marker first, then merges with the block before it.</summary>
    public void Backspace(bool word = false)
    {
        _lastTypedWasDelimiter = false;
        _pending.Clear();
        if (HasSelection) { DeleteSelection(); return; }
        var p = P;
        var f = CurrentFlow;
        if (f is null) return;
        if (f.IsObject)
        {
            if (Caret == f.End) { Select(f.End, f.Start); }
            else MergeBack(p, f);
            return;
        }
        int d = f.ToDisplay(Caret);
        if (d > 0)
        {
            int nd = word ? f.WordLeft(d) : f.PrevStop(d);
            // An atom selects first, so one key cannot silently take a whole link.
            if (!word && nd < d - 1 && f.S[nd] == f.S[d - 1] && f.E[nd] == f.E[d - 1] && f.E[nd] - f.S[nd] > 1)
            {
                Select(f.E[nd], f.S[nd]);
                return;
            }
            int a = f.S[nd], b = f.E[d - 1];
            int behind = Caret - b;   // hidden markers between the character and the caret stay on the caret's side
            CutChars(a, b, behind, TextUndoOp.KindBackspace, !word);
            return;
        }
        if (IsRaw)
        {
            if (Caret <= p.BodyStart) return;
            int i = p.IndexAt(Caret);
            if (i <= 0) return;
            var g = p.Flows[i - 1];
            Apply(g.End, Caret - g.End, "", g.End, g.End);
            return;
        }
        if (RemoveBlockMarker(p, f)) return;
        MergeBack(p, f);
    }

    /// <summary>Delete (forward). At a flow's end it merges the next block up.</summary>
    public void DeleteForward(bool word = false)
    {
        _lastTypedWasDelimiter = false;
        _pending.Clear();
        if (HasSelection) { DeleteSelection(); return; }
        var p = P;
        var f = CurrentFlow;
        if (f is null) return;
        int i = p.IndexAt(Caret);
        if (!f.IsObject)
        {
            int d = f.ToDisplay(Caret);
            if (d < f.Length)
            {
                int nd = word ? f.WordRight(d) : f.NextStop(d);
                if (!word && nd > d + 1 && f.S[d] == f.S[nd - 1] && f.E[d] == f.E[nd - 1] && f.E[d] - f.S[d] > 1)
                {
                    Select(f.S[d], f.E[d]);
                    return;
                }
                CutChars(f.S[d], f.E[nd - 1], Caret - f.S[d], TextUndoOp.KindDelete, !word);
                return;
            }
        }
        if (i + 1 >= p.Flows.Count) return;
        var g = p.Flows[i + 1];
        if (g.IsObject) { Select(g.Start, g.End); return; }
        int from = f.IsObject ? f.Start : (IsRaw ? f.End : LeafEnd(f));
        // Past the block's own closing marks. A list item's marker on the next line goes with the merge.
        Apply(from, g.Start - from, "", from, from);
    }

    private void CutChars(int a, int b, int caretOffsetFromB, byte kind, bool coalesce)
    {
        // The cut's text may be all a pair holds: then the pair goes too (R7), and the caret lands where the pair was.
        int ra = a, rb = b;
        bool pairGone = false;
        if (!IsRaw)
            foreach (var pair in PairsIn(a, b))
                if (pair.OpenEnd >= a && pair.CloseStart <= b && pair.OpenEnd <= pair.CloseStart)
                {
                    ra = Math.Min(ra, pair.OpenStart);
                    rb = Math.Max(rb, pair.CloseEnd);
                    pairGone = true;
                }
        if (pairGone)
        {
            Apply(ra, rb - ra, "", ra, ra, TextUndoOp.KindOther, seal: true);
            return;
        }
        int caret = kind == TextUndoOp.KindDelete ? a + Math.Max(0, Math.Min(caretOffsetFromB, 0)) + (Caret - a) : Caret - (b - a);
        caret = Math.Clamp(caret, a, _text.Length - (b - a));
        Apply(a, b - a, "", caret, caret, kind, seal: !coalesce);
    }

    private void DeleteSelection()
    {
        var p = P;
        int a = SelectionStart, b = SelectionEnd;
        int i = p.IndexAt(a);
        if (i >= 0 && p.Flows[i] is { IsObject: true } o && a == o.Start && b == o.End)
        {
            RemoveObject(p, o, i);
            return;
        }
        _undo.SealRun();
        ReplaceRange(a, b, "", TextUndoOp.KindOther, seal: true);
        // A cut can leave the caret off the new text's stops; it rests on the nearest one.
        SetSelectionRaw(P.SnapOffset(Caret), P.SnapOffset(Caret));
    }

    /// <summary>An object goes with the blank line that separated it, so the gap it leaves is the gap that was there.</summary>
    private void RemoveObject(MdProjection p, MdFlow o, int index)
    {
        int end = p.AfterEol(o.End);
        // The blank lines after it, up to the next block.
        while (end < _text.Length && IsOnlySpaces(end, p.LineEndOf(end)) && p.LineEndOf(end) < _text.Length) end = p.AfterEol(p.LineEndOf(end));
        int start = o.Start;
        if (end >= _text.Length && index > 0)
        {
            // The last block: take the gap before it instead, and keep the final newline state (R4).
            bool hadFinalEol = o.End < _text.Length;
            int prevEnd = Math.Max(p.Flows[index - 1].BlockEnd, p.Flows[index - 1].End);
            start = prevEnd;
            end = hadFinalEol ? o.End : _text.Length;
        }
        Apply(start, end - start, "", start, start);
        SetSelectionRaw(P.SnapOffset(start), P.SnapOffset(start));
    }

    private bool RemoveBlockMarker(MdProjection p, MdFlow f)
    {
        int lineStart = p.LineStartOf(f.Start);
        if (f.Item is { } item && f.FirstInItem)
        {
            int from = item.MarkerStart;
            if (IsOnlySpaces(lineStart, from)) from = lineStart;
            bool lineAbove = lineStart > p.BodyStart && !LineAboveIsBlank(lineStart);
            string ins = lineAbove && from == lineStart ? p.Eol : "";
            int caret = from + ins.Length;
            Apply(from, f.Start - from, ins, caret, caret);
            return true;
        }
        if (f.Kind == MdFlowKind.Heading)
        {
            if (f.Block is MarkdownHeading { Setext: true })
            {
                // The underline goes; the text stays where it is.
                int le = p.LineEndOf(f.End);
                Apply(le, f.BlockEnd - le, "", f.Start, f.Start);
                return true;
            }
            int hash = lineStart;
            while (hash < f.Start && _text[hash] != '#') hash++;
            int tail = p.LineEndOf(f.End);
            Edits(new List<Edit> { new(hash, f.Start - hash, ""), new(f.End, tail - f.End, "") }, hash, hash);
            return true;
        }
        if (f.InQuote && lineStart < f.Start)
        {
            int q = f.Start - 1;
            while (q >= lineStart && _text[q] != '>') q--;
            if (q >= lineStart)
            {
                int qe = q + 1;
                if (qe < f.Start && _text[qe] == ' ') qe++;
                Apply(q, qe - q, "", q, q);
                return true;
            }
        }
        return false;
    }

    private void MergeBack(MdProjection p, MdFlow f)
    {
        int i = p.IndexAt(Caret);
        if (i <= 0) return;
        var g = p.Flows[i - 1];
        if (g.IsObject) { Select(g.End, g.Start); return; }
        if (f.Length == 0 && f.Item is null && !f.InQuote)
        {
            // An empty paragraph goes: its own line, the caret to the end of what is above.
            int ls = p.LineStartOf(f.Start);
            int from = ls > 0 ? ls - (ls >= 2 && _text[ls - 2] == '\r' && _text[ls - 1] == '\n' ? 2 : 1) : 0;
            int to = ls > 0 ? ls : p.AfterEol(p.LineEndOf(ls));
            int target = g.End;
            if (from < p.ProtectedEnd) return;
            Apply(from, to - from, "", Math.Min(target, from), Math.Min(target, from));
            return;
        }
        int end = g.ContinuesBelow ? g.End : LeafEnd(g);
        Apply(end, f.Start - end, "", end, end);
    }

    private void InsertParagraphAfter(MdFlow f)
    {
        var p = P;
        string eol = p.Eol;
        int end = f.End;
        int afterLine = p.AfterEol(end);
        bool nextBlank = end < _text.Length && afterLine < _text.Length && IsOnlySpaces(afterLine, p.LineEndOf(afterLine));
        if (nextBlank)
        {
            int caret = end + eol.Length + (afterLine - end);
            Apply(end, 0, eol, caret, caret);
            return;
        }
        string two = eol + eol;
        Apply(end, 0, two, end + two.Length, end + two.Length);
    }

    /// <summary>A table or other island shows its source in place.</summary>
    internal void OpenIsland(MdFlow f)
    {
        _openIslandAt = f.Start;
        Invalidate();
        var g = CurrentFlowAt(f.Start);
        int at = g?.Start ?? f.Start;
        SetSelectionRaw(at, at);
    }

    private MdFlow? CurrentFlowAt(int p)
    {
        int i = P.IndexAt(p);
        return i < 0 ? null : P.Flows[i];
    }

    // ── undo ───────────────────────────────────────────────────────────────────────────────────────────────────────

    public void Undo()
    {
        if (!_undo.TryUndo(out var op)) return;
        string removedNow = op.Inserted;
        _text = string.Concat(_text.AsSpan(0, op.Start), op.Removed, _text.AsSpan(op.Start + removedNow.Length));
        ShiftOpenIsland(op.Start, removedNow.Length, op.Removed.Length);
        Invalidate();
        SetSelectionRaw(op.AnchorBefore, op.ActiveBefore);
        _lastTypedWasDelimiter = false;
        OnEdit?.Invoke(op.Start, removedNow, op.Removed);
    }

    public void Redo()
    {
        if (!_undo.TryRedo(out var op)) return;
        _text = string.Concat(_text.AsSpan(0, op.Start), op.Inserted, _text.AsSpan(op.Start + op.Removed.Length));
        ShiftOpenIsland(op.Start, op.Removed.Length, op.Inserted.Length);
        Invalidate();
        SetSelectionRaw(op.AnchorAfter, op.ActiveAfter);
        _lastTypedWasDelimiter = false;
        OnEdit?.Invoke(op.Start, op.Removed, op.Inserted);
    }

    // ── many small edits, one replace ──────────────────────────────────────────────────────────────────────────────

    private readonly record struct Edit(int At, int Remove, string Insert);

    /// <summary>Several edits as one replace over their hull: one undo step, and the bytes between them copied verbatim.</summary>
    private void Edits(List<Edit> edits, int anchorAfterOld, int caretAfterOld, bool anchorRight = false, bool caretRight = false)
    {
        edits.RemoveAll(e => e.Remove == 0 && e.Insert.Length == 0);
        if (edits.Count == 0) return;
        edits.Sort((x, y) => x.At != y.At ? x.At.CompareTo(y.At) : 0);
        int hullStart = edits[0].At;
        int hullEnd = hullStart;
        var sb = new StringBuilder();
        foreach (var e in edits)
        {
            if (e.At > hullEnd) sb.Append(_text, hullEnd, e.At - hullEnd);
            sb.Append(e.Insert);
            hullEnd = Math.Max(hullEnd, e.At + e.Remove);
        }
        int Map(int o, bool right)
        {
            int delta = 0;
            foreach (var e in edits)
            {
                if (o > e.At + e.Remove || (o == e.At + e.Remove && e.Remove > 0)) delta += e.Insert.Length - e.Remove;
                else if (o == e.At && e.Remove == 0) { if (right) delta += e.Insert.Length; break; }
                else if (o >= e.At && o < e.At + e.Remove) { delta += e.At - o + (right ? e.Insert.Length : 0); break; }
                else break;
            }
            return o + delta;
        }
        _undo.SealRun();
        Apply(hullStart, hullEnd - hullStart, sb.ToString(), Map(anchorAfterOld, anchorRight), Map(caretAfterOld, caretRight));
    }

    // ── marks ──────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The marker a mark writes: the note's own, so a <c>__strong__</c> note stays one (R3), unless it would not parse here (R7).</summary>
    private string MarkerFor(MarkdownMarkKind kind, int openAt, int closeAt)
    {
        string fallback = kind switch
        {
            MarkdownMarkKind.Bold => "**", MarkdownMarkKind.Italic => "*", MarkdownMarkKind.Strikethrough => "~~",
            MarkdownMarkKind.Code => "`", MarkdownMarkKind.Highlight => "==", _ => "",
        };
        if (kind is not (MarkdownMarkKind.Bold or MarkdownMarkKind.Italic)) return fallback;
        foreach (var f in P.Flows)
            foreach (var pair in f.Pairs)
                if (pair.Kind == kind)
                {
                    string used = _text.Substring(pair.OpenStart, pair.OpenEnd - pair.OpenStart);
                    // `_` cannot open or close inside a word.
                    if (used[0] == '_' && ((openAt > 0 && char.IsLetterOrDigit(_text[openAt - 1])) || (closeAt < _text.Length && char.IsLetterOrDigit(_text[closeAt])))) return fallback;
                    return used;
                }
        return fallback;
    }

    /// <summary>Is the whole selection (trimmed) in this mark, for a command's checked state?</summary>
    public bool IsMarkActive(MarkdownMarkKind kind)
    {
        if (_pending.Contains(kind)) return true;
        var p = P;
        if (IsRaw || p.Flows.Count == 0) return false;
        var style = StyleOf(kind);
        if (!HasSelection)
        {
            var f = CurrentFlow;
            if (f is null || f.IsObject || f.Length == 0) return false;
            foreach (var pair in f.Pairs) if (pair.Kind == kind && pair.OpenEnd <= Caret && Caret <= pair.CloseStart) return true;
            return false;
        }
        bool any = false;
        foreach (var (f, da, db) in SelectedChars())
        {
            any = true;
            if (!AllStyled(f, da, db, style)) return false;
        }
        return any;
    }

    private static MdRunStyle StyleOf(MarkdownMarkKind k) => k switch
    {
        MarkdownMarkKind.Bold => MdRunStyle.Strong, MarkdownMarkKind.Italic => MdRunStyle.Em,
        MarkdownMarkKind.Strikethrough => MdRunStyle.Strike, MarkdownMarkKind.Code => MdRunStyle.Code,
        MarkdownMarkKind.Highlight => MdRunStyle.Mark, _ => MdRunStyle.Link,
    };

    private static bool AllStyled(MdFlow f, int da, int db, MdRunStyle style)
    {
        for (int i = da; i < db; i++) if (!StyledAt(f, i, style)) return false;
        return true;
    }

    private static bool StyledAt(MdFlow f, int d, MdRunStyle style)
    {
        foreach (var r in f.Runs) if (d >= r.Start && d < r.End) return (r.Style & style) != 0;
        return false;
    }

    /// <summary>The selection per text flow, with markers-hug-text whitespace trimmed off each end.</summary>
    private IEnumerable<(MdFlow Flow, int From, int To)> SelectedChars()
    {
        var p = P;
        int a = SelectionStart, b = SelectionEnd;
        int i = p.IndexAt(a), j = p.IndexAt(b);
        for (int k = Math.Max(0, i); k <= j && k < p.Flows.Count; k++)
        {
            var f = p.Flows[k];
            if (f.IsObject || f.Kind is MdFlowKind.CodeLine or MdFlowKind.RawLine || f.Length == 0) continue;
            int da = f.ToDisplay(Math.Max(a, f.Start)), db = f.ToDisplay(Math.Min(b, f.End));
            while (da < db && char.IsWhiteSpace(f.Display[da])) da++;
            while (db > da && char.IsWhiteSpace(f.Display[db - 1])) db--;
            if (da < db) yield return (f, da, db);
        }
    }

    /// <summary>Bold, Italic, Strikethrough, Code or Highlight on the selection. Off when it is all on. With no selection: the word under
    /// the caret, else the mark waits for the next character typed.</summary>
    public void ToggleMark(MarkdownMarkKind kind)
    {
        if (IsRaw) return;
        var p = P;
        if (!HasSelection)
        {
            var f = CurrentFlow;
            if (f is null || f.IsObject || f.Kind == MdFlowKind.CodeLine) return;
            int d = f.ToDisplay(Caret);
            bool inWord = (d < f.Length && char.IsLetterOrDigit(f.Display[d])) && (d > 0 && char.IsLetterOrDigit(f.Display[d - 1]));
            if (!inWord)
            {
                // In a pair with nothing to wrap, the caret is what is asking: nothing typed yet, nothing written.
                if (!_pending.Remove(kind)) _pending.Add(kind);
                return;
            }
            int ws = d, we = d;
            while (ws > 0 && char.IsLetterOrDigit(f.Display[ws - 1])) ws--;
            while (we < f.Length && char.IsLetterOrDigit(f.Display[we])) we++;
            int keep = Caret;
            ApplyMark(kind, f.S[ws], f.E[we - 1], keep, keep);
            return;
        }
        ApplyMark(kind, SelectionStart, SelectionEnd, Anchor, Caret);
    }

    private void ApplyMark(MarkdownMarkKind kind, int a, int b, int keepAnchor, int keepCaret)
    {
        int oa = Anchor, oc = Caret;
        SetSelectionRaw(a, b);
        var targets = new List<(MdFlow Flow, int From, int To)>(SelectedChars());
        var style = StyleOf(kind);
        bool allOn = targets.Count > 0;
        foreach (var (f, da, db) in targets) if (!AllStyled(f, da, db, style)) { allOn = false; break; }
        var edits = new List<Edit>();
        if (targets.Count == 0) { SetSelectionRaw(oa, oc); return; }
        foreach (var (f, da, db) in targets)
        {
            int s = f.S[da], e = f.E[db - 1];
            if (allOn) RemoveMark(f, kind, s, e, edits);
            else AddMark(f, kind, da, db, s, e, edits);
        }
        SetSelectionRaw(oa, oc);
        if (edits.Count == 0) return;
        if (!allOn && !WouldParse(edits, kind))
        {
            // R7: never write a marker that would not parse as that mark.
            return;
        }
        bool caretForward = oc >= oa;
        Edits(edits, keepAnchor, keepCaret, anchorRight: caretForward, caretRight: !caretForward);
    }

    private void AddMark(MdFlow f, MarkdownMarkKind kind, int da, int db, int s, int e, List<Edit> edits)
    {
        var style = StyleOf(kind);
        string marker = MarkerFor(kind, s, e);
        bool startOn = StyledAt(f, da, style), endOn = StyledAt(f, db - 1, style);
        // Same-mark pairs the selection runs into merge: a delimiter inside the new range goes, and an end already in the mark needs none.
        int from = startOn ? int.MinValue : s, to = endOn ? int.MaxValue : e;
        foreach (var pair in f.Pairs)
        {
            if (pair.Kind != kind) continue;
            if (pair.OpenStart >= s && pair.OpenEnd <= e) edits.Add(new Edit(pair.OpenStart, pair.OpenEnd - pair.OpenStart, ""));
            if (pair.CloseStart >= s && pair.CloseEnd <= e) edits.Add(new Edit(pair.CloseStart, pair.CloseEnd - pair.CloseStart, ""));
        }
        if (from != int.MinValue) edits.Add(new Edit(s, 0, marker));
        if (to != int.MaxValue) edits.Add(new Edit(e, 0, marker));
    }

    private void RemoveMark(MdFlow f, MarkdownMarkKind kind, int s, int e, List<Edit> edits)
    {
        foreach (var pair in f.Pairs)
        {
            if (pair.Kind != kind || pair.OpenEnd > e || pair.CloseStart < s) continue;
            string open = _text.Substring(pair.OpenStart, pair.OpenEnd - pair.OpenStart);
            string close = _text.Substring(pair.CloseStart, pair.CloseEnd - pair.CloseStart);
            if (s <= pair.OpenEnd) edits.Add(new Edit(pair.OpenStart, pair.OpenEnd - pair.OpenStart, ""));
            else edits.Add(new Edit(s, 0, close));
            if (e >= pair.CloseStart) edits.Add(new Edit(pair.CloseStart, pair.CloseEnd - pair.CloseStart, ""));
            else edits.Add(new Edit(e, 0, open));
        }
    }

    /// <summary>R7: the text the edits would make shows no stray markers: its visible length is the visible length now.</summary>
    private bool WouldParse(List<Edit> edits, MarkdownMarkKind kind)
    {
        var sorted = new List<Edit>(edits);
        sorted.Sort((x, y) => x.At.CompareTo(y.At));
        var sb = new StringBuilder(_text.Length + 16);
        int at = 0;
        foreach (var e in sorted)
        {
            sb.Append(_text, at, e.At - at);
            sb.Append(e.Insert);
            at = e.At + e.Remove;
        }
        sb.Append(_text, at, _text.Length - at);
        int id = 0;
        var trial = new MdProjection(sb.ToString(), false, _options, -1, null, ref id);
        return VisibleLength(trial) == VisibleLength(P);
    }

    private static int VisibleLength(MdProjection p)
    {
        int n = 0;
        foreach (var f in p.Flows) n += f.Length;
        return n;
    }

    // ── blocks ─────────────────────────────────────────────────────────────────────────────────────────────────────

    private IEnumerable<MdFlow> SelectedLeaves()
    {
        var p = P;
        int i = p.IndexAt(SelectionStart), j = p.IndexAt(SelectionEnd);
        int lastBlock = -1;
        for (int k = Math.Max(0, i); k <= j && k < p.Flows.Count; k++)
        {
            var f = p.Flows[k];
            if (f.BlockStart == lastBlock) continue;
            lastBlock = f.BlockStart;
            if (!f.IsObject && f.Kind is MdFlowKind.Paragraph or MdFlowKind.Heading) yield return f;
        }
    }

    /// <summary>Paragraph (0) or heading level 1 to 6, on the blocks under the selection. Only their own markers change.</summary>
    public void SetBlockLevel(int level)
    {
        if (IsRaw) return;
        var p = P;
        var edits = new List<Edit>();
        foreach (var f in SelectedLeaves())
        {
            int lineStart = p.LineStartOf(f.Start);
            if (f.Kind == MdFlowKind.Heading && f.HeadingLevel == level) continue;
            if (f.Kind == MdFlowKind.Paragraph && level == 0) continue;
            string hashes = level > 0 ? new string('#', level) + " " : "";
            if (f.Kind == MdFlowKind.Heading && f.Block is MarkdownHeading { Setext: true })
            {
                int le = p.LineEndOf(f.End);
                int underEnd = f.BlockEnd;
                int underStart = p.AfterEol(le);
                if (level is 1 or 2)
                {
                    // A setext heading stays setext where it can: the underline's character changes, its length stays.
                    int ul = underStart;
                    while (ul < underEnd && _text[ul] == ' ') ul++;
                    int ulEnd = ul;
                    while (ulEnd < underEnd && _text[ulEnd] is '=' or '-') ulEnd++;
                    edits.Add(new Edit(ul, ulEnd - ul, new string(level == 1 ? '=' : '-', ulEnd - ul)));
                }
                else
                {
                    edits.Add(new Edit(le, underEnd - le, ""));
                    if (level > 0) edits.Add(new Edit(f.Start, 0, hashes));
                }
                continue;
            }
            if (f.Kind == MdFlowKind.Heading)
            {
                int hash = lineStart;
                while (hash < f.Start && _text[hash] != '#') hash++;
                edits.Add(new Edit(hash, f.Start - hash, hashes));
                if (level == 0)
                {
                    int tail = p.LineEndOf(f.End);
                    if (tail > f.End) edits.Add(new Edit(f.End, tail - f.End, ""));
                }
                continue;
            }
            edits.Add(new Edit(f.Start, 0, hashes));
        }
        Edits(edits, Anchor, Caret, anchorRight: true, caretRight: true);
    }

    /// <summary>Flip one task box: one character (R2). The caret stays where it was.</summary>
    public void ToggleTaskAt(int boxCharOffset)
    {
        if (boxCharOffset < 0 || boxCharOffset >= _text.Length) return;
        if (boxCharOffset < 1 || _text[boxCharOffset - 1] != '[') return;
        char now = _text[boxCharOffset];
        if (now is not (' ' or 'x' or 'X')) return;
        string next = now == ' ' ? "x" : " ";
        _undo.SealRun();
        Apply(boxCharOffset, 1, next, Anchor, Caret);
    }

    /// <summary>Ctrl+L: a paragraph becomes a task with the note's bullet; a plain item gains a box; a task loses it.</summary>
    public void ToggleTaskLine()
    {
        if (IsRaw) return;
        var edits = new List<Edit>();
        foreach (var f in SelectedLeaves())
        {
            if (f.Item is { } item && f.FirstInItem)
            {
                if (item.Checked is not null)
                {
                    int open = item.TaskCharOffset - 1;
                    int end = open + 3;
                    if (end < _text.Length && _text[end] == ' ') end++;
                    edits.Add(new Edit(open, end - open, ""));
                }
                else edits.Add(new Edit(f.Start, 0, "[ ] "));
                continue;
            }
            if (f.Kind == MdFlowKind.Heading) continue;
            edits.Add(new Edit(f.Start, 0, NoteBullet() + " [ ] "));
        }
        Edits(edits, Anchor, Caret, anchorRight: true, caretRight: true);
    }

    /// <summary>A plain paragraph becomes a list item with the note's own bullet or number style; an item loses its marker.</summary>
    public void ToggleList(bool ordered)
    {
        if (IsRaw) return;
        var p = P;
        var edits = new List<Edit>();
        int number = 1;
        foreach (var f in SelectedLeaves())
        {
            if (f.Item is { } item && f.FirstInItem)
            {
                edits.Add(new Edit(item.MarkerStart, f.Start - item.MarkerStart, ""));
                continue;
            }
            if (f.Kind == MdFlowKind.Heading) continue;
            edits.Add(new Edit(f.Start, 0, ordered ? number++.ToString(System.Globalization.CultureInfo.InvariantCulture) + ". " : NoteBullet() + " "));
        }
        Edits(edits, Anchor, Caret, anchorRight: true, caretRight: true);
    }

    public void ToggleQuote()
    {
        if (IsRaw) return;
        var p = P;
        var edits = new List<Edit>();
        foreach (var f in SelectedLeaves())
        {
            if (f.InQuote)
            {
                int ls = p.LineStartOf(f.Start);
                int q = ls;
                while (q < f.Start && _text[q] != '>') q++;
                if (q < f.Start)
                {
                    int qe = q + 1;
                    if (qe < f.Start && _text[qe] == ' ') qe++;
                    edits.Add(new Edit(q, qe - q, ""));
                }
                continue;
            }
            edits.Add(new Edit(p.LineStartOf(f.Start), 0, "> "));
        }
        Edits(edits, Anchor, Caret, anchorRight: true, caretRight: true);
    }

    private char NoteBullet()
    {
        foreach (var f in P.Flows)
            if (f.Item is { Ordered: false } it && it.MarkerStart < _text.Length) return _text[it.MarkerStart];
        return '-';
    }

    // ── inserting ──────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The insert menu's Enter: <c>/query</c> (the source range) becomes the block's syntax in one undo step, with
    /// <c>\n</c> in the syntax written as the note's newline and the caret at <paramref name="caretInSyntax"/>.</summary>
    public void ReplaceWithSyntax(int start, int end, string syntax, int caretInSyntax)
    {
        string eol = P.Eol;
        string before = caretInSyntax >= 0 ? syntax.Substring(0, Math.Min(caretInSyntax, syntax.Length)) : syntax;
        string text = syntax.Replace("\n", eol);
        int caret = start + before.Replace("\n", eol).Length;
        _undo.SealRun();
        _lastTypedWasDelimiter = false;
        Apply(start, end - start, text, caret, caret);
    }

    /// <summary>A completion's text over its trigger and query: written whole, the caret after it.</summary>
    public void ReplaceRangePlain(int start, int end, string text)
    {
        _undo.SealRun();
        _lastTypedWasDelimiter = false;
        int caret = start + text.Length;
        Apply(start, end - start, text, caret, caret);
    }

    internal void WrapAsLink(int a, int b, string url)
    {
        _undo.SealRun();
        string text = "[" + _text.Substring(a, b - a) + "](" + url + ")";
        int caret = a + text.Length;
        Apply(a, b - a, text, caret, caret);
    }

    internal void SetLinkAddress(MdLink link, string url)
    {
        if (link.UrlStart < 0) return;
        _undo.SealRun();
        int delta = url.Length - (link.UrlEnd - link.UrlStart);
        int Map(int o) => o >= link.UrlEnd ? o + delta : o;
        Apply(link.UrlStart, link.UrlEnd - link.UrlStart, url, Map(Anchor), Map(Caret));
    }

    /// <summary>Remove a link's markers and keep its text.</summary>
    internal void RemoveLink(MdFlow f, MdLink link)
    {
        foreach (var pair in f.Pairs)
        {
            if (pair.Kind != MarkdownMarkKind.Link || pair.OpenStart != link.SourceStart) continue;
            var edits = new List<Edit>
            {
                new(pair.OpenStart, pair.OpenEnd - pair.OpenStart, ""),
                new(pair.CloseStart, pair.CloseEnd - pair.CloseStart, ""),
            };
            Edits(edits, Anchor, Caret);
            return;
        }
    }
}
