using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace FluentGpu.Controls;

// The projection (markdown-editor.md §4): the source text as what the editor shows. A pure function of the source, the raw
// flag and the open island; the caret is not an input, so moving it costs nothing and moves nothing.

/// <summary>What one run of a flow is. Colours and faces resolve at render, never here.</summary>
[Flags]
internal enum MdRunStyle : byte
{
    None = 0, Strong = 1, Em = 2, Strike = 4, Code = 8, Mark = 16, Link = 32, Dim = 64,
}

internal readonly record struct MdRun(int Start, int End, MdRunStyle Style);

internal enum MdFlowKind : byte { Paragraph, Heading, CodeLine, RawLine, Object }

internal enum MdObjectKind : byte { None, Rule, Table, Html, Image, Code }

/// <summary>A delimiter pair in the source (a mark, a code span, a link), for the cuts that must not orphan one (R7).</summary>
internal readonly record struct MdPair(int OpenStart, int OpenEnd, int CloseStart, int CloseEnd, MarkdownMarkKind Kind);

/// <summary>A link on a flow, as display and source ranges.</summary>
internal sealed record MdLink(int DisplayStart, int DisplayEnd, string Url, string Title, string? Syntax, int SourceStart, int SourceEnd, int UrlStart, int UrlEnd);

/// <summary>The list item a flow sits in.</summary>
internal sealed record MdItem(int MarkerStart, int ContentStart, int LineEnd, bool Ordered, int Ordinal, bool? Checked, int TaskCharOffset, bool Tight);

/// <summary>One caret-holding run of text (one <c>SpanTextEl</c>), or one object the caret selects whole.</summary>
internal sealed class MdFlow
{
    public long Id;
    public MdFlowKind Kind;
    public MdObjectKind Object;
    public int HeadingLevel;
    public string Display = "";
    /// <summary>Source range of each display unit: [S[i], E[i]). Two units with the same range are one stop (an atom).</summary>
    public int[] S = [], E = [];
    /// <summary>Where the caret may sit: <see cref="Start"/> before the first unit, <see cref="End"/> after the last, past any closers.</summary>
    public int Start, End;
    /// <summary>Where the flow's block ends in the source: its closing <c>#</c>s or a setext underline are past <see cref="End"/>.</summary>
    public int BlockEnd;
    public int BlockStart;
    public MdRun[] Runs = [];
    public MdPair[] Pairs = [];
    public MdLink[] Links = [];
    public MdItem? Item;
    public bool FirstInItem;
    public bool InQuote;
    /// <summary>A hard break ends this flow and the next flow continues the same paragraph.</summary>
    public bool ContinuesBelow;
    /// <summary>Written before the first typed character: the space a bare marker line needs, or the newline after front matter.</summary>
    public string InsertPrefix = "";
    public bool ReadOnly;
    public string Language = "";
    public MarkdownImage? Image;
    public MarkdownBlock? Block;

    public bool IsObject => Kind == MdFlowKind.Object;
    public int Length => Display.Length;

    /// <summary>Display index of a source offset: how many units end at or before it.</summary>
    public int ToDisplay(int p)
    {
        int lo = 0, hi = E.Length;
        while (lo < hi)
        {
            int mid = (lo + hi) >>> 1;
            if (E[mid] <= p) lo = mid + 1; else hi = mid;
        }
        return lo;
    }

    /// <summary>The source offset left of display index <paramref name="d"/>, hugging the character before it: inside a
    /// closer, before an opener. Where a click lands (the RichEdit habit) and where a move to the right stops.</summary>
    public int LeftOf(int d) => d <= 0 ? Start : E[Math.Min(d, E.Length) - 1];

    /// <summary>The source offset right of display index <paramref name="d"/>, hugging the character after it: past a closer.
    /// Where a move to the left stops.</summary>
    public int RightOf(int d) => d >= S.Length ? End : S[Math.Max(d, 0)];

    private int[]? _graphemes;
    private int[] Graphemes => _graphemes ??= StringInfo.ParseCombiningCharacters(Display);

    public int NextStop(int d)
    {
        var g = Graphemes;
        int i = Array.BinarySearch(g, d);
        i = i >= 0 ? i + 1 : ~i;
        int next = i < g.Length ? g[i] : Display.Length;
        // An atom is one stop: walk past every unit with the same source range.
        while (next > 0 && next < S.Length && S[next] == S[next - 1] && E[next] == E[next - 1]) next++;
        return next;
    }

    public int PrevStop(int d)
    {
        var g = Graphemes;
        int i = Array.BinarySearch(g, d);
        i = i >= 0 ? i - 1 : ~i - 1;
        int prev = i >= 0 ? g[i] : 0;
        while (prev > 0 && prev < S.Length && S[prev] == S[prev - 1] && E[prev] == E[prev - 1]) prev--;
        return prev;
    }

    /// <summary>A display index a hit test returned, moved off the inside of an atom.</summary>
    public int Snap(int d)
    {
        d = Math.Clamp(d, 0, Display.Length);
        if (d == 0 || d >= S.Length || S[d] != S[d - 1] || E[d] != E[d - 1]) return d;
        int a = d, b = d;
        while (a > 0 && S[a] == S[a - 1] && E[a] == E[a - 1]) a--;
        while (b < S.Length && S[b] == S[d] && E[b] == E[d]) b++;
        return d - a <= b - d ? a : b;
    }

    public int WordRight(int d)
    {
        var s = Display;
        int i = d;
        while (i < s.Length && !IsWord(s[i]) && !char.IsWhiteSpace(s[i])) i++;
        if (i == d) while (i < s.Length && IsWord(s[i])) i++;
        while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
        return Snap(i);
    }

    public int WordLeft(int d)
    {
        var s = Display;
        int i = d;
        while (i > 0 && char.IsWhiteSpace(s[i - 1])) i--;
        if (i > 0 && IsWord(s[i - 1])) while (i > 0 && IsWord(s[i - 1])) i--;
        else while (i > 0 && !IsWord(s[i - 1]) && !char.IsWhiteSpace(s[i - 1])) i--;
        return Snap(i);
    }

    private static bool IsWord(char c) => char.IsLetterOrDigit(c) || c == '_' || char.IsSurrogate(c);
}

/// <summary>The block tree the renderer walks. Leaves hold flows; the document order of flows is <see cref="MdProjection.Flows"/>.</summary>
internal abstract class MdView { public long Id; public int SourceStart, SourceEnd; public string Signature = ""; }
internal sealed class MdLeafView : MdView { public MdFlow[] Flows = []; }
internal sealed class MdCodeView : MdView { public MdFlow[] Lines = []; public string Language = ""; }
internal sealed class MdRawBlockView : MdView { public MdFlow[] Lines = []; public MdObjectKind Kind; }
internal sealed class MdObjectView : MdView { public MdFlow Object = null!; }
internal sealed class MdQuoteView : MdView { public MdView[] Children = []; }
internal sealed class MdListView : MdView { public bool Ordered; public int Start; public bool Tight; public MdItemView[] Items = []; }
internal sealed class MdItemView { public MdItem Item = null!; public MdView[] Children = []; }
internal sealed class MdFrontMatterView : MdView { public string Text = ""; }

/// <summary>One full projection of a source text.</summary>
internal sealed class MdProjection
{
    public readonly string Text;
    public readonly bool Raw;
    /// <summary>Every flow and object in document order, read-only flows excluded.</summary>
    public readonly List<MdFlow> Flows = new();
    public readonly List<MdView> Views = new();
    public int BodyStart;
    public int ProtectedEnd;
    public string Eol = "\n";
    private readonly MarkdownOptions _o;
    private int[]? _t2s;      // expanded text offset -> source offset; null when the source has no tab
    private int _base;        // BOM length
    private string _t = "";
    private readonly List<int> _lineStart = new(), _lineEnd = new();
    private bool _endsOpenLine;
    public MarkdownDocument Doc = MarkdownDocument.Empty;
    private int _nextId;
    private readonly int _openIslandAt;

    public MdProjection(string text, bool raw, MarkdownOptions options, int openIslandAt, MdProjection? previous, ref int nextId)
    {
        Text = text;
        Raw = raw;
        _o = options with { Extensions = options.Extensions | MarkdownExtensions.FrontMatter };
        _openIslandAt = openIslandAt;
        _nextId = nextId;
        Eol = DominantEol(text);
        if (MarkdownFrontMatter.TryFind(text, out int fmStart, out int fmEnd))
        {
            ProtectedEnd = fmEnd;
            BodyStart = fmEnd;
        }
        else
        {
            fmStart = -1;
            ProtectedEnd = text.Length > 0 && text[0] == '﻿' ? 1 : 0;
            BodyStart = ProtectedEnd;
        }
        if (raw) BuildRaw();
        else BuildRendered(fmStart, fmEnd);
        AssignIds(previous);
        nextId = _nextId;
    }

    // ── source helpers ─────────────────────────────────────────────────────────────────────────────────────────────

    public static string DominantEol(string text)
    {
        int crlf = 0, lf = 0, cr = 0;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '\n') lf++;
            else if (c == '\r')
            {
                if (i + 1 < text.Length && text[i + 1] == '\n') { crlf++; i++; }
                else cr++;
            }
        }
        if (crlf == 0 && lf == 0 && cr == 0) return "\n";
        if (crlf >= lf && crlf >= cr) return "\r\n";
        return lf >= cr ? "\n" : "\r";
    }

    public int LineStartOf(int p)
    {
        int i = Math.Min(p, Text.Length);
        while (i > 0 && Text[i - 1] != '\n' && Text[i - 1] != '\r') i--;
        return i;
    }

    public int LineEndOf(int p)
    {
        int i = Math.Min(p, Text.Length);
        while (i < Text.Length && Text[i] != '\n' && Text[i] != '\r') i++;
        return i;
    }

    public int AfterEol(int lineEnd)
    {
        if (lineEnd >= Text.Length) return Text.Length;
        return Text[lineEnd] == '\r' && lineEnd + 1 < Text.Length && Text[lineEnd + 1] == '\n' ? lineEnd + 2 : lineEnd + 1;
    }

    private int Src(int t) => _t2s is { } m ? m[Math.Clamp(t, 0, m.Length - 1)] : t + _base;

    /// <summary>The flow holding a source offset: the last flow starting at or before it, or the nearest when it falls in a gap.</summary>
    public int IndexAt(int p)
    {
        if (Flows.Count == 0) return -1;
        int lo = 0, hi = Flows.Count - 1;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) >>> 1;
            if (Flows[mid].Start <= p) lo = mid; else hi = mid - 1;
        }
        var f = Flows[lo];
        if (p > f.End && lo + 1 < Flows.Count && Flows[lo + 1].Start - p < p - f.End) return lo + 1;
        return lo;
    }

    /// <summary>A source offset moved to the nearest place the caret may sit.</summary>
    public int SnapOffset(int p)
    {
        int i = IndexAt(p);
        if (i < 0) return Math.Clamp(p, BodyStart, Text.Length);
        var f = Flows[i];
        if (p <= f.Start) return f.Start;
        if (p >= f.End) return f.End;
        if (f.IsObject) return p - f.Start <= f.End - p ? f.Start : f.End;
        // Inside a unit (an escape, an entity, an atom): its nearer edge.
        int d = f.ToDisplay(p);
        if (d < f.S.Length && p > f.S[d] && p < f.E[d]) return p - f.S[d] <= f.E[d] - p ? f.S[d] : f.E[d];
        return p;
    }

    // ── raw view ───────────────────────────────────────────────────────────────────────────────────────────────────

    private void BuildRaw()
    {
        var rows = new List<MdFlow>();
        int at = _baseOfRaw();
        var all = new List<MdFlow>();
        while (true)
        {
            int end = LineEndOf(at);
            var f = RawLine(at, end, MdFlowKind.RawLine);
            f.ReadOnly = at < ProtectedEnd;
            all.Add(f);
            if (!f.ReadOnly) Flows.Add(f);
            if (end >= Text.Length) break;
            at = AfterEol(end);
            if (at >= Text.Length)
            {
                var last = RawLine(at, at, MdFlowKind.RawLine);
                all.Add(last);
                Flows.Add(last);
                break;
            }
        }
        if (Flows.Count == 0)
        {
            // Front matter with no newline after it: the one stop writes that newline first (R5).
            var tail = RawLine(Text.Length, Text.Length, MdFlowKind.RawLine);
            tail.InsertPrefix = Eol;
            all.Add(tail);
            Flows.Add(tail);
        }
        Views.Add(new MdRawBlockView { Lines = all.ToArray(), Kind = MdObjectKind.None, SourceStart = 0, SourceEnd = Text.Length });
    }

    private int _baseOfRaw() => Text.Length > 0 && Text[0] == '﻿' ? 1 : 0;

    /// <summary>A flow showing one source line as written: every unit maps one to one, a tab shows as four spaces.</summary>
    private MdFlow RawLine(int start, int end, MdFlowKind kind)
    {
        var sb = new StringBuilder(end - start);
        var s = new List<int>(end - start);
        var e = new List<int>(end - start);
        for (int i = start; i < end; i++)
        {
            char c = Text[i];
            if (c == '\t') for (int k = 0; k < 4; k++) { sb.Append(' '); s.Add(i); e.Add(i + 1); }
            else { sb.Append(c); s.Add(i); e.Add(i + 1); }
        }
        return new MdFlow
        {
            Kind = kind, Start = start, End = end, BlockStart = start, BlockEnd = end,
            Display = sb.ToString(), S = s.ToArray(), E = e.ToArray(),
            Runs = sb.Length == 0 ? [] : [new MdRun(0, sb.Length, MdRunStyle.None)],
        };
    }

    // ── rendered view ──────────────────────────────────────────────────────────────────────────────────────────────

    private void BuildRendered(int fmStart, int fmEnd)
    {
        ExpandTabs();
        SplitTLines();
        Doc = MarkdownParser.ParseForEditor(_t, _o);

        bool sepPending = false;
        bool lastWasFlow = false;
        if (fmStart >= 0)
        {
            Views.Add(new MdFrontMatterView { Text = Text.Substring(fmStart, fmEnd - fmStart), SourceStart = fmStart, SourceEnd = fmEnd, Signature = "fm" });
            sepPending = true;
        }
        int line = 0;
        foreach (var block in Doc.Blocks)
        {
            int first = TLineAt(block.SourceStart), last = TLineAt(Math.Max(block.SourceStart, block.SourceEnd));
            Gap(line, first, ref sepPending, ref lastWasFlow, fmEnd);
            Views.Add(TopLevel(block));
            sepPending = true;
            lastWasFlow = false;
            line = last + 1;
        }
        Gap(line, _lineStart.Count, ref sepPending, ref lastWasFlow, fmEnd);

        // A note that ends in front matter still needs one place to write (R5).
        bool anyStop = false;
        foreach (var f in Flows) if (!f.ReadOnly) { anyStop = true; break; }
        if (!anyStop)
        {
            bool atLineStart = Text.Length == BodyStart && (BodyStart == 0 || Text[BodyStart - 1] is '\n' or '\r' or '﻿');
            var tail = EmptyFlow(Text.Length);
            if (!atLineStart) tail.InsertPrefix = Eol;
            Flows.Add(tail);
            Views.Add(new MdLeafView { Flows = [tail], SourceStart = tail.Start, SourceEnd = tail.End, Signature = "empty" });
        }
    }

    /// <summary>Blank source lines between blocks: the first is the separator and shows nothing, each further one is an empty
    /// paragraph with a caret stop, so the file's two blank lines stay visible and reachable. The final newline is not a line.</summary>
    private void Gap(int from, int to, ref bool sepPending, ref bool lastWasFlow, int fmEnd)
    {
        for (int i = from; i < to; i++)
        {
            int ts = _lineStart[i], te = _lineEnd[i];
            int srcStart = Src(ts);
            if (srcStart < fmEnd) continue;
            bool isVirtual = i == _lineStart.Count - 1 && _endsOpenLine;
            bool blank = true;
            for (int k = ts; k < te && blank; k++) if (_t[k] != ' ') blank = false;
            if (!blank) { sepPending = true; lastWasFlow = false; continue; }   // a hidden reference definition
            if (sepPending) { sepPending = false; continue; }
            if (isVirtual && lastWasFlow) continue;
            var f = EmptyFlow(srcStart);
            f.End = Src(te);
            f.BlockEnd = f.End;
            Flows.Add(f);
            Views.Add(new MdLeafView { Flows = [f], SourceStart = f.Start, SourceEnd = f.End, Signature = "empty" });
            lastWasFlow = true;
        }
    }

    private MdFlow EmptyFlow(int at) => new() { Kind = MdFlowKind.Paragraph, Start = at, End = at, BlockStart = at, BlockEnd = at };

    private MdView TopLevel(MarkdownBlock b) => BlockView(b, null, false, false, topLevel: true);

    private MdView BlockView(MarkdownBlock b, MdItem? item, bool firstInItem, bool inQuote, bool topLevel)
    {
        int ss = Src(b.SourceStart), se = Src(b.SourceEnd);
        MdView v;
        switch (b)
        {
            case MarkdownParagraph p when SoleImage(p, out var img):
            {
                var o = ObjectFlow(MdObjectKind.Image, ss, se, b);
                o.Image = img;
                Flows.Add(o);
                v = new MdObjectView { Object = o };
                break;
            }
            case MarkdownParagraph p:
                v = new MdLeafView { Flows = Inline(p.Inlines, b, MdFlowKind.Paragraph, 0, item, firstInItem, inQuote) };
                break;
            case MarkdownHeading h:
                v = new MdLeafView { Flows = Inline(h.Inlines, b, MdFlowKind.Heading, h.Level, item, firstInItem, inQuote) };
                break;
            case MarkdownCodeBlock c when topLevel && c.Fenced && CodeLines(c, ss, se) is { Length: > 0 } lines:
                v = new MdCodeView { Lines = lines, Language = c.Language };
                foreach (var l in lines) Flows.Add(l);
                break;
            case MarkdownCodeBlock c when topLevel && !c.Fenced && IndentedCodeLines(ss, se) is { Length: > 0 } lines:
                v = new MdCodeView { Lines = lines, Language = c.Language };
                foreach (var l in lines) Flows.Add(l);
                break;
            case MarkdownCodeBlock:
                v = Island(MdObjectKind.Code, ss, se, b);
                break;
            case MarkdownHtmlBlock:
                v = Island(MdObjectKind.Html, ss, se, b);
                break;
            case MarkdownTable:
                v = Island(MdObjectKind.Table, ss, se, b);
                break;
            case MarkdownThematicBreak:
            {
                var o = ObjectFlow(MdObjectKind.Rule, ss, se, b);
                Flows.Add(o);
                v = new MdObjectView { Object = o };
                break;
            }
            case MarkdownQuote q:
            {
                if (q.Children.Length == 0)
                {
                    // `> ` with nothing yet: still a place to type.
                    var ef = EmptyFlow(se);
                    ef.InQuote = true;
                    ef.Item = item;
                    if (se > 0 && Text[se - 1] != ' ') ef.InsertPrefix = " ";
                    Flows.Add(ef);
                    v = new MdQuoteView { Children = [new MdLeafView { Flows = [ef], SourceStart = se, SourceEnd = se, Signature = "quote" }] };
                    break;
                }
                var kids = new MdView[q.Children.Length];
                for (int i = 0; i < kids.Length; i++) kids[i] = BlockView(q.Children[i], item, firstInItem && i == 0, true, false);
                v = new MdQuoteView { Children = kids };
                break;
            }
            case MarkdownList l:
            {
                var items = new MdItemView[l.Items.Length];
                for (int i = 0; i < items.Length; i++)
                {
                    var it = l.Items[i];
                    var info = new MdItem(Src(it.MarkerStart), Src(it.ContentStart), Src(it.LineEnd), l.Ordered, it.Ordinal, it.Checked,
                        it.MarkerOffset >= 0 ? Src(it.MarkerOffset) : -1, l.Tight);
                    MdView[] kids;
                    if (it.Children.Length == 0)
                    {
                        // An empty item is still a place to type. `-` with no space past it needs the space first.
                        int at = Math.Min(info.ContentStart, info.LineEnd);
                        var f = EmptyFlow(at);
                        f.Item = info;
                        f.FirstInItem = true;
                        f.InQuote = inQuote;
                        f.BlockEnd = info.LineEnd;
                        f.End = at;
                        if (at == info.LineEnd && at > 0 && Text[at - 1] != ' ') f.InsertPrefix = " ";
                        Flows.Add(f);
                        kids = [new MdLeafView { Flows = [f], SourceStart = at, SourceEnd = at, Signature = "item" }];
                    }
                    else
                    {
                        kids = new MdView[it.Children.Length];
                        for (int k = 0; k < kids.Length; k++) kids[k] = BlockView(it.Children[k], info, k == 0, inQuote, false);
                    }
                    items[i] = new MdItemView { Item = info, Children = kids };
                }
                v = new MdListView { Ordered = l.Ordered, Start = l.Start, Tight = l.Tight, Items = items };
                break;
            }
            default:
                v = Island(MdObjectKind.Html, ss, se, b);
                break;
        }
        v.SourceStart = ss;
        v.SourceEnd = se;
        return v;
    }

    private static bool SoleImage(MarkdownParagraph p, out MarkdownImage img)
    {
        img = null!;
        if (p.Inlines.Length != 1 || p.Inlines[0] is not MarkdownImage m) return false;
        img = m;
        return true;
    }

    private MdFlow ObjectFlow(MdObjectKind kind, int start, int end, MarkdownBlock b) => new()
    {
        Kind = MdFlowKind.Object, Object = kind, Start = start, End = end, BlockStart = start, BlockEnd = end, Block = b,
    };

    /// <summary>A block the editor does not edit in place: one object, whose source lines open as raw flows.</summary>
    private MdView Island(MdObjectKind kind, int start, int end, MarkdownBlock b)
    {
        if (start == _openIslandAt)
        {
            var rows = new List<MdFlow>();
            int at = start;
            while (true)
            {
                int le = Math.Min(LineEndOf(at), end);
                var f = RawLine(at, le, MdFlowKind.CodeLine);
                f.BlockStart = start;
                f.BlockEnd = end;
                rows.Add(f);
                Flows.Add(f);
                if (le >= end) break;
                at = AfterEol(le);
            }
            return new MdRawBlockView { Lines = rows.ToArray(), Kind = kind };
        }
        var o = ObjectFlow(kind, start, end, b);
        Flows.Add(o);
        return new MdObjectView { Object = o };
    }

    private MdFlow[] CodeLines(MarkdownCodeBlock c, int start, int end)
    {
        // Fence line, content lines, closing line. A body the fence opens and closes at once has no line to hold a caret: an island.
        int firstEnd = LineEndOf(start);
        int fenceIndent = 0;
        while (start + fenceIndent < firstEnd && Text[start + fenceIndent] == ' ') fenceIndent++;
        int closeStart = LineStartOf(end);
        int at = AfterEol(firstEnd);
        if (at >= closeStart || firstEnd >= end) return [];
        var rows = new List<MdFlow>();
        while (at < closeStart)
        {
            int le = LineEndOf(at);
            int strip = 0;
            while (strip < fenceIndent && at + strip < le && Text[at + strip] == ' ') strip++;
            var f = RawLine(at + strip, le, MdFlowKind.CodeLine);
            f.BlockStart = start;
            f.BlockEnd = end;
            f.Language = c.Language;
            rows.Add(f);
            at = AfterEol(le);
        }
        return rows.ToArray();
    }

    private MdFlow[] IndentedCodeLines(int start, int end)
    {
        var rows = new List<MdFlow>();
        int at = LineStartOf(start);
        while (at <= end)
        {
            int le = LineEndOf(at);
            int strip = 0;
            while (strip < 4 && at + strip < le && Text[at + strip] == ' ') strip++;
            if (strip < 4 && at + strip < le && Text[at + strip] == '\t') strip++;   // the tab that made it code
            var f = RawLine(at + strip, le, MdFlowKind.CodeLine);
            f.BlockStart = start;
            f.BlockEnd = end;
            rows.Add(f);
            if (le >= end || le >= Text.Length) break;
            at = AfterEol(le);
        }
        return rows.ToArray();
    }

    // ── inlines → one or more flows ────────────────────────────────────────────────────────────────────────────────

    private sealed class FlowBuilder
    {
        public readonly StringBuilder Text = new();
        public readonly List<int> S = new(), E = new();
        public readonly List<MdRun> Runs = new();
        public readonly List<MdPair> Pairs = new();
        public readonly List<MdLink> Links = new();
        public int Start;

        public void Add(string text, int[]? map, int atomStart, int atomEnd, MdRunStyle style, Func<int, int> src)
        {
            if (text.Length == 0) return;
            int at = Text.Length;
            Text.Append(text);
            for (int i = 0; i < text.Length; i++)
            {
                if (map is not null) { S.Add(src(map[2 * i])); E.Add(src(map[2 * i + 1])); }
                else { S.Add(atomStart); E.Add(atomEnd); }
            }
            if (Runs.Count > 0 && Runs[^1].End == at && Runs[^1].Style == style) Runs[^1] = Runs[^1] with { End = at + text.Length };
            else Runs.Add(new MdRun(at, at + text.Length, style));
        }
    }

    private MdFlow[] Inline(MarkdownInline[] inlines, MarkdownBlock block, MdFlowKind kind, int level, MdItem? item, bool firstInItem, bool inQuote)
    {
        int contentStart = Src(block.ContentStart), contentEnd = Src(block.ContentEnd);
        int blockStart = Src(block.SourceStart), blockEnd = Src(block.SourceEnd);
        var flows = new List<MdFlow>(1);
        var fb = new FlowBuilder { Start = contentStart };
        void Finish(int end, bool continues)
        {
            flows.Add(new MdFlow
            {
                Kind = kind, HeadingLevel = level, Start = fb.Start, End = end, BlockStart = blockStart, BlockEnd = blockEnd,
                Display = fb.Text.ToString(), S = fb.S.ToArray(), E = fb.E.ToArray(), Runs = fb.Runs.ToArray(),
                Pairs = fb.Pairs.ToArray(), Links = fb.Links.ToArray(), Item = item,
                FirstInItem = firstInItem && flows.Count == 0, InQuote = inQuote, ContinuesBelow = continues, Block = block,
            });
        }
        void Walk(MarkdownInline node, MdRunStyle style, int atomStart, int atomEnd)
        {
            switch (node)
            {
                case MarkdownText t:
                    if (t.UnmatchedOpener) style |= MdRunStyle.Dim;
                    fb.Add(t.Text, t.Map, atomStart, atomEnd, style, Src);
                    break;
                case MarkdownLineBreak { Hard: true } br:
                    Finish(Src(br.SourceStart), continues: true);
                    fb = new FlowBuilder { Start = Src(br.SourceEnd) };
                    break;
                case MarkdownLineBreak br:
                    fb.Add(" ", null, Src(br.SourceStart), Src(br.SourceEnd), style, Src);
                    break;
                case MarkdownCodeSpan c:
                    AddPair(c, MarkdownMarkKind.Code);
                    fb.Add(c.Code, c.Map, Src(c.SourceStart), Src(c.SourceEnd), style | MdRunStyle.Code, Src);
                    break;
                case MarkdownMark m:
                    AddPair(m, MarkdownMarkKind.Highlight);
                    foreach (var k in m.Children) Walk(k, style | MdRunStyle.Mark, atomStart, atomEnd);
                    break;
                case MarkdownEmphasis e:
                    AddPair(e, e.Strong ? MarkdownMarkKind.Bold : MarkdownMarkKind.Italic);
                    foreach (var k in e.Children) Walk(k, style | (e.Strong ? MdRunStyle.Strong : MdRunStyle.Em), atomStart, atomEnd);
                    break;
                case MarkdownStrikethrough s:
                    AddPair(s, MarkdownMarkKind.Strikethrough);
                    foreach (var k in s.Children) Walk(k, style | MdRunStyle.Strike, atomStart, atomEnd);
                    break;
                case MarkdownLink l:
                {
                    // A consumer syntax is written whole by the consumer: its text has no map and is one atom.
                    bool atom = l.Syntax is not null;
                    if (!atom && l.OpenStart >= 0) AddPair(l, MarkdownMarkKind.Link);
                    int ds = fb.Text.Length;
                    int aS = l.SourceStart >= 0 ? Src(l.SourceStart) : atomStart, aE = l.SourceEnd >= 0 ? Src(l.SourceEnd) : atomEnd;
                    foreach (var k in l.Children) Walk(k, style | MdRunStyle.Link, aS, aE);
                    fb.Links.Add(new MdLink(ds, fb.Text.Length, l.Url, l.Title, l.Syntax, aS, aE,
                        l.UrlStart >= 0 ? Src(l.UrlStart) : -1, l.UrlEnd >= 0 ? Src(l.UrlEnd) : -1));
                    break;
                }
                case MarkdownImage img:
                    fb.Add(img.Alt.Length > 0 ? img.Alt : img.Url, null, Src(img.SourceStart), Src(img.SourceEnd), style, Src);
                    break;
            }
        }
        void AddPair(MarkdownInline n, MarkdownMarkKind kind2)
        {
            if (n.OpenStart < 0) return;
            fb.Pairs.Add(new MdPair(Src(n.OpenStart), Src(n.OpenEnd), Src(n.CloseStart), Src(n.CloseEnd), kind2));
        }
        foreach (var n in inlines) Walk(n, MdRunStyle.None, contentStart, contentStart);
        Finish(contentEnd, continues: false);
        foreach (var f in flows) Flows.Add(f);
        return flows.ToArray();
    }

    // ── tabs and lines ─────────────────────────────────────────────────────────────────────────────────────────────

    private void ExpandTabs()
    {
        _base = Text.Length > 0 && Text[0] == '﻿' ? 1 : 0;
        if (Text.IndexOf('\t') < 0)
        {
            _t = _base == 0 ? Text : Text.Substring(_base);
            return;
        }
        var sb = new StringBuilder(Text.Length + 16);
        var map = new List<int>(Text.Length + 16);
        int col = 0;
        for (int i = _base; i < Text.Length; i++)
        {
            char c = Text[i];
            if (c == '\t')
            {
                int n = 4 - col % 4;
                for (int k = 0; k < n; k++) { sb.Append(' '); map.Add(i); }
                col += n;
                continue;
            }
            sb.Append(c);
            map.Add(i);
            col = c is '\n' or '\r' ? 0 : col + 1;
        }
        map.Add(Text.Length);
        _t = sb.ToString();
        _t2s = map.ToArray();
    }

    private void SplitTLines()
    {
        int start = 0;
        for (int i = 0; i < _t.Length; i++)
        {
            char c = _t[i];
            if (c != '\n' && c != '\r') continue;
            _lineStart.Add(start);
            _lineEnd.Add(i);
            if (c == '\r' && i + 1 < _t.Length && _t[i + 1] == '\n') i++;
            start = i + 1;
        }
        _lineStart.Add(start);
        _lineEnd.Add(_t.Length);
        _endsOpenLine = start == _t.Length;   // the line after the final newline, or an empty note
    }

    private int TLineAt(int t)
    {
        int lo = 0, hi = _lineStart.Count - 1;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) >>> 1;
            if (_lineStart[mid] <= t) lo = mid; else hi = mid - 1;
        }
        return lo;
    }

    // ── stable identity across edits ───────────────────────────────────────────────────────────────────────────────

    /// <summary>Blocks keep their ids across an edit: the same blocks from each end, then the changed middle in order. A
    /// node is reused where its block survived, so a height change eases and a caret stays on its text node.</summary>
    private void AssignIds(MdProjection? prev)
    {
        foreach (var v in Views) if (v.Signature.Length == 0 || v is MdLeafView) v.Signature = Text.Substring(v.SourceStart, Math.Max(0, v.SourceEnd - v.SourceStart));
        var old = prev?.Views;
        int oldN = old?.Count ?? 0, n = Views.Count;
        int head = 0;
        while (head < n && head < oldN && SameShape(old![head], Views[head]) && old![head].Signature == Views[head].Signature) { Views[head].Id = old[head].Id; head++; }
        int tail = 0;
        while (tail < n - head && tail < oldN - head && SameShape(old![oldN - 1 - tail], Views[n - 1 - tail])
               && old![oldN - 1 - tail].Signature == Views[n - 1 - tail].Signature)
        {
            Views[n - 1 - tail].Id = old[oldN - 1 - tail].Id;
            tail++;
        }
        for (int i = head; i < n - tail; i++)
        {
            int oi = i;
            if (oi < oldN - tail && SameShape(old![oi], Views[i])) Views[i].Id = old[oi].Id;
            else Views[i].Id = ++_nextId;
        }
        // Flow ids follow their block, by ordinal: a flow's scene node survives the edit that keeps its block.
        foreach (var v in Views)
        {
            int ordinal = 0;
            NumberFlows(v, v.Id, ref ordinal);
        }
    }

    private static bool SameShape(MdView a, MdView b) => a.GetType() == b.GetType();

    private static void NumberFlows(MdView v, long block, ref int ordinal)
    {
        switch (v)
        {
            case MdLeafView l: foreach (var f in l.Flows) f.Id = block << 20 | (uint)ordinal++; break;
            case MdCodeView c: foreach (var f in c.Lines) f.Id = block << 20 | (uint)ordinal++; break;
            case MdRawBlockView r: foreach (var f in r.Lines) f.Id = block << 20 | (uint)ordinal++; break;
            case MdObjectView o: o.Object.Id = block << 20 | (uint)ordinal++; break;
            case MdQuoteView q: foreach (var k in q.Children) NumberFlows(k, block, ref ordinal); break;
            case MdListView li: foreach (var it in li.Items) foreach (var k in it.Children) NumberFlows(k, block, ref ordinal); break;
        }
    }
}
