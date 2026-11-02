using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace FluentGpu.Controls;

/// <summary>
/// The in-house CommonMark + GFM parser behind <see cref="MarkdownTextBlock"/> (docs/design/subsystems/markdown.md
/// §3: why in-house — <c>FluentGpu.Controls</c> carries no NuGet reference and must stay NativeAOT-clean; Markdig's
/// pipeline/AST model and pulldown-cmark's two-pass (block tree, then inline) structure are the precedents). A COLD
/// path: it runs once per document text (memoised by the block on the text), never per frame, so it allocates freely.
///
/// <para><b>Blocks</b> (CommonMark §4-5): ATX + setext headings, paragraphs, fenced + indented code, block quotes
/// (with lazy continuation), bullet + ordered lists (nested, tight/loose), thematic breaks, HTML blocks (kept as raw
/// text), link reference definitions; GFM pipe tables and task-list items.
/// <b>Inlines</b> (§6): backslash escapes, entity references, code spans, emphasis via the delimiter-run algorithm
/// (<c>*</c>/<c>_</c>, flanking rules), links (inline + reference), images, autolinks (<c>&lt;…&gt;</c> and GFM
/// bare <c>http(s)://</c>/<c>www.</c>), hard + soft breaks; GFM <c>~~strikethrough~~</c>; consumer inline syntaxes.
/// Not implemented (documented, markdown.md §3.5): HTML inlines (literal text), footnotes, definition lists, math.</para>
/// </summary>
public static class MarkdownParser
{
    public static MarkdownDocument Parse(string? text, MarkdownOptions? options = null) => Parse(text, options, null, null);

    /// <summary>Parse with the previous result at hand. When <see cref="MarkdownOptions.IsStreaming"/> is set and
    /// <paramref name="text"/> extends <paramref name="previousText"/>, every top-level block of <paramref name="previous"/>
    /// but the last is reused as is and only the tail (from the last block's start) is re-parsed — with its unterminated
    /// inline delimiters closed (<see cref="CloseTail"/>). Otherwise a full parse.</summary>
    public static MarkdownDocument Parse(string? text, MarkdownOptions? options, MarkdownDocument? previous, string? previousText)
    {
        if (string.IsNullOrEmpty(text)) return MarkdownDocument.Empty;
        options ??= MarkdownOptions.Default;
        var p = new State(options);
        var lines = SplitLines(text, out var offsets);
        if ((options.Extensions & MarkdownExtensions.FrontMatter) != 0) BlankFrontMatter(text, lines, offsets);
        int refs = p.CollectReferences(lines);
        int from = 0;
        MarkdownBlock[] reused = Array.Empty<MarkdownBlock>();
        if (options.IsStreaming && previous is { Blocks.Length: > 1 } && previousText is not null
            && refs == previous.ReferenceCount && text.Length >= previousText.Length
            && text.AsSpan(0, previousText.Length).SequenceEqual(previousText))
        {
            int keep = previous.Blocks.Length - 1;
            int tailStart = previous.Blocks[keep].SourceStart;
            if (tailStart > 0)
            {
                reused = new MarkdownBlock[keep];
                Array.Copy(previous.Blocks, reused, keep);
                p.SeedTaskCount(CountTasks(reused));
                from = LineAt(offsets, tailStart);
            }
        }
        if (options.IsStreaming)
        {
            // Close the growing tail's delimiters on the LINES, so offsets of the reused prefix stay those of the source.
            int last = lines.Count - 1;
            while (last > from && IsBlank(lines[last])) last--;
            if (last >= from && !InOpenFence(lines, from, last)) lines[last] = CloseTail(lines, from, last);
        }
        var tail = p.ParseBlocks(lines, from, lines.Count, offsets);
        MarkdownBlock[] blocks;
        if (reused.Length == 0) blocks = tail;
        else { blocks = new MarkdownBlock[reused.Length + tail.Length]; reused.CopyTo(blocks, 0); tail.CopyTo(blocks, reused.Length); }
        return new MarkdownDocument(blocks) { ReferenceCount = refs };
    }

    /// <summary>The editor's parse (markdown-editor.md §4): the same grammar, with an inline source map on every node, and
    /// three readings that keep a half-typed construct from flipping the note: an unclosed fence is not a fence, and a bare
    /// <c>#</c>, <c>-</c>, <c>1.</c> or <c>&gt;</c> with nothing after it is text until the space arrives. Offsets are into
    /// <paramref name="text"/>, which must hold no tabs (the editor expands them first and maps back).</summary>
    internal static MarkdownDocument ParseForEditor(string text, MarkdownOptions options)
    {
        if (text.Length == 0) return MarkdownDocument.Empty;
        var p = new State(options) { Mapped = true };
        var lines = SplitLines(text, out var offsets);
        if ((options.Extensions & MarkdownExtensions.FrontMatter) != 0) BlankFrontMatter(text, lines, offsets);
        int refs = p.CollectReferences(lines);
        return new MarkdownDocument(p.ParseBlocks(lines, 0, lines.Count, offsets)) { ReferenceCount = refs };
    }

    /// <summary>Front matter is not content: its lines read as blank.</summary>
    private static void BlankFrontMatter(string text, List<string> lines, List<int> offsets)
    {
        if (!MarkdownFrontMatter.TryFind(text, out _, out int end)) return;
        for (int i = 0; i < lines.Count && offsets[i] < end; i++) lines[i] = "";
    }

    private static int LineAt(List<int> offsets, int charOffset)
    {
        int lo = 0, hi = offsets.Count - 1;
        while (lo < hi) { int mid = (lo + hi + 1) / 2; if (offsets[mid] <= charOffset) lo = mid; else hi = mid - 1; }
        return lo;
    }

    private static int CountTasks(MarkdownBlock[] blocks)
    {
        int n = 0;
        foreach (var b in blocks)
        {
            if (b is MarkdownList l) foreach (var it in l.Items) { if (it.Checked is not null) n++; n += CountTasks(it.Children); }
            else if (b is MarkdownQuote q) n += CountTasks(q.Children);
        }
        return n;
    }

    /// <summary>True when a fence opened at or after <paramref name="from"/> is still open at <paramref name="last"/>:
    /// the tail is code and must not be touched.</summary>
    private static bool InOpenFence(List<string> lines, int from, int last)
    {
        bool open = false; char fc = '\0'; int flen = 0;
        for (int i = from; i <= last; i++)
        {
            var b = lines[i].AsSpan(Math.Min(Indent(lines[i]), 3));
            if (open) { if (State.IsClosingFenceLine(b, fc, flen)) open = false; }
            else if (State.IsFenceLine(b, out fc, out flen)) open = true;
        }
        return open;
    }

    /// <summary>The streaming tail-closer (remend's idea, <c>streamdown/packages/remend</c>): append the closers an
    /// unterminated inline construct in the tail paragraph still needs — code span ticks, then <c>)</c> for an open
    /// <c>](</c>, then <c>==</c>, <c>~~</c>, <c>**</c>, <c>*</c>, <c>_</c> — so a half-arrived <c>**bold</c> renders bold
    /// at once and never flickers through literal asterisks. Only the LAST line changes (the appended closers); a tail
    /// that starts a code span it has not closed gets its ticks and nothing else (its content is code).</summary>
    internal static string CloseTail(List<string> lines, int from, int last)
    {
        // The tail paragraph: lines after the last blank line in [from, last].
        int start = last;
        while (start > from && !IsBlank(lines[start - 1])) start--;
        var sb = new StringBuilder();
        for (int li = start; li <= last; li++) { if (li > start) sb.Append('\n'); sb.Append(lines[li]); }
        string tail = sb.ToString();
        int n = tail.Length;
        bool strong = false, em = false, strongU = false, emU = false, strike = false, mark = false;
        int openParenLink = 0, codeRun = 0;
        int i = 0;
        bool atLineStart = true;
        while (i < n)
        {
            char c = tail[i];
            if (c == '\\') { i += 2; atLineStart = false; continue; }
            if (c == '\n') { i++; atLineStart = true; continue; }
            if (c == '`')
            {
                int run = 0; while (i + run < n && tail[i + run] == '`') run++;
                int close = -1, j = i + run;
                while (j < n) { if (tail[j] != '`') { j++; continue; } int r = 0; while (j + r < n && tail[j + r] == '`') r++; if (r == run) { close = j; break; } j += r; }
                if (close < 0) { codeRun = run; break; }   // unterminated: everything after is code
                i = close + run; atLineStart = false; continue;
            }
            if (atLineStart && (c == '*' || c == '-' || c == '+' || c == '>' || c == '#') && i + 1 < n && tail[i + 1] == ' ') { i += 2; atLineStart = false; continue; }
            atLineStart = atLineStart && c == ' ';
            if (c == '*' || c == '_' || c == '~' || c == '=')
            {
                int run = 0; while (i + run < n && tail[i + run] == c) run++;
                bool wsBefore = i == 0 || char.IsWhiteSpace(tail[i - 1]);
                bool wsAfter = i + run >= n || char.IsWhiteSpace(tail[i + run]);
                if (!(wsBefore && wsAfter))
                {
                    if (c == '~') { if (run == 2) strike = !strike; }
                    else if (c == '=') { if (run == 2) mark = !mark; }
                    else if (c == '*') { if (run >= 2) strong = !strong; if (run % 2 == 1) em = !em; }
                    else { if (run >= 2) strongU = !strongU; if (run % 2 == 1) emU = !emU; }
                }
                i += run; continue;
            }
            if (c == ']' && i + 1 < n && tail[i + 1] == '(') { openParenLink++; i += 2; continue; }
            if (c == ')' && openParenLink > 0) { openParenLink--; i++; continue; }
            i++;
        }
        var closers = new StringBuilder();
        if (codeRun > 0) closers.Append('`', codeRun);
        else
        {
            if (openParenLink > 0) closers.Append(')', openParenLink);
            if (mark) closers.Append("==");
            if (strike) closers.Append("~~");
            if (em) closers.Append('*');
            if (strong) closers.Append("**");
            if (emU) closers.Append('_');
            if (strongU) closers.Append("__");
        }
        return closers.Length == 0 ? lines[last] : lines[last] + closers;
    }

    // ── line splitting ──────────────────────────────────────────────────────────────────────────────────────────────

    private static List<string> SplitLines(string text, out List<int> offsets)
    {
        var lines = new List<string>();
        offsets = new List<int>();
        int start = 0;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '\n' || c == '\r')
            {
                offsets.Add(start);
                lines.Add(ExpandTabs(text.AsSpan(start, i - start)));
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                start = i + 1;
            }
        }
        if (start < text.Length) { offsets.Add(start); lines.Add(ExpandTabs(text.AsSpan(start))); }
        offsets.Add(text.Length);   // sentinel: the end of the last line
        return lines;
    }

    /// <summary>Tabs expand to the next 4-column stop (CommonMark §2.2), only in leading position matters for
    /// indentation but expanding everywhere keeps code blocks aligned too.</summary>
    private static string ExpandTabs(ReadOnlySpan<char> line)
    {
        if (line.IndexOf('\t') < 0) return line.ToString();
        var sb = new StringBuilder(line.Length + 8);
        foreach (char c in line)
        {
            if (c == '\t') { int n = 4 - sb.Length % 4; sb.Append(' ', n); }
            else sb.Append(c);
        }
        return sb.ToString();
    }

    private static int Indent(string s)
    {
        int i = 0;
        while (i < s.Length && s[i] == ' ') i++;
        return i;
    }

    private static bool IsBlank(string s) => s.AsSpan().Trim().Length == 0;

    // ── the parser state ────────────────────────────────────────────────────────────────────────────────────────────

    private sealed class State(MarkdownOptions options)
    {
        private readonly MarkdownOptions _o = options;
        private readonly Dictionary<string, (string Url, string Title)> _refs = new(StringComparer.OrdinalIgnoreCase);
        private int _taskCount;
        /// <summary>The editor's parse: inline nodes carry source offsets (<see cref="ParseForEditor"/>).</summary>
        public bool Mapped { get; init; }

        private bool Has(MarkdownExtensions e) => (_o.Extensions & e) != 0;

        // ── link reference definitions: `[label]: url "title"` on its own line(s) ──
        public void SeedTaskCount(int n) => _taskCount = n;

        public int CollectReferences(List<string> lines)
        {
            for (int i = 0; i < lines.Count; i++)
            {
                var s = lines[i];
                if (Indent(s) > 3) continue;
                var t = s.AsSpan().Trim();
                if (t.Length < 4 || t[0] != '[') continue;
                int close = t.IndexOf("]:");
                if (close <= 1) continue;
                string label = NormalizeLabel(t.Slice(1, close - 1));
                var rest = t.Slice(close + 2).Trim();
                if (rest.Length == 0) continue;
                if (!TryParseDestinationAndTitle(rest, out string url, out string title, out int used)) continue;
                if (rest.Slice(used).Trim().Length != 0) continue;
                if (!_refs.ContainsKey(label)) _refs[label] = (url, title);
                lines[i] = "";   // the definition is not content
            }
            return _refs.Count;
        }

        private static string NormalizeLabel(ReadOnlySpan<char> s)
        {
            var sb = new StringBuilder(s.Length);
            bool ws = false;
            foreach (char c in s)
            {
                if (char.IsWhiteSpace(c)) { ws = true; continue; }
                if (ws && sb.Length > 0) sb.Append(' ');
                ws = false;
                sb.Append(char.ToLowerInvariant(c));
            }
            return sb.ToString();
        }

        // ── block parsing ───────────────────────────────────────────────────────────────────────────────────────────

        public MarkdownBlock[] ParseBlocks(List<string> lines, int from, int to, List<int> offsets)
        {
            var blocks = new List<MarkdownBlock>();
            int i = from;
            while (i < to)
            {
                string line = lines[i];
                if (IsBlank(line)) { i++; continue; }
                int ind = Indent(line);
                int blockStartLine = i;
                int before = blocks.Count;
                ParseOne(lines, ref i, to, line, ind, blocks, offsets);
                if (blocks.Count > before)
                {
                    int endLine = Math.Max(blockStartLine, i - 1);
                    blocks[^1] = blocks[^1] with { SourceStart = offsets[blockStartLine], SourceEnd = offsets[endLine] + lines[endLine].Length };
                }
            }
            return blocks.ToArray();
        }

        private void ParseOne(List<string> lines, ref int i, int to, string line, int ind, List<MarkdownBlock> blocks, List<int> offsets)
        {
            {

                if (ind >= 4)
                {
                    // Indented code block: consecutive lines indented 4+ (blank lines inside keep it going).
                    var sb = new StringBuilder();
                    int j = i;
                    int lastNonBlank = i;
                    while (j < to && (IsBlank(lines[j]) || Indent(lines[j]) >= 4))
                    {
                        if (!IsBlank(lines[j])) lastNonBlank = j;
                        j++;
                    }
                    for (int k = i; k <= lastNonBlank; k++)
                    {
                        var l = lines[k];
                        sb.Append(l.Length >= 4 ? l.AsSpan(4) : ReadOnlySpan<char>.Empty).Append('\n');
                    }
                    blocks.Add(new MarkdownCodeBlock("", sb.ToString().TrimEnd('\n')));
                    i = lastNonBlank + 1;
                    return;
                }

                var body = line.AsSpan(ind);
                bool bare = Mapped && IsBareMarkerLine(body);

                if (!bare && TryFence(body, out char fc, out int flen, out string info) && (!Mapped || HasClosingFence(lines, i + 1, to, fc, flen)))
                {
                    var sb = new StringBuilder();
                    int j = i + 1;
                    while (j < to)
                    {
                        var l = lines[j];
                        var lb = l.AsSpan(Math.Min(Indent(l), 3));
                        if (IsClosingFence(lb, fc, flen)) { j++; goto fenced_done; }
                        // Content lines lose up to the fence's own indentation (CommonMark §4.5).
                        int strip = Math.Min(Indent(l), ind);
                        sb.Append(l.AsSpan(strip)).Append('\n');
                        j++;
                    }
                fenced_done:
                    blocks.Add(new MarkdownCodeBlock(info, sb.ToString().TrimEnd('\n')) { Fenced = true });
                    i = j;
                    return;
                }

                if (!bare && TryAtxHeading(body, out int level, out string headText))
                {
                    if (Mapped)
                    {
                        int textAt = offsets[i] + ind + level;
                        var afterHashes = body.Slice(level);
                        while (textAt < offsets[i] + line.Length && afterHashes.Length > 0 && afterHashes[0] == ' ') { textAt++; afterHashes = afterHashes.Slice(1); }
                        int[] hm = IdentityMap(textAt, headText.Length);
                        blocks.Add(new MarkdownHeading(level, ParseInlines(headText, hm)) { ContentStart = hm[0], ContentEnd = hm[^1] });
                    }
                    else blocks.Add(new MarkdownHeading(level, ParseInlines(headText)));
                    i++;
                    return;
                }

                if (IsThematicBreak(body))
                {
                    blocks.Add(MarkdownThematicBreak.Instance);
                    i++;
                    return;
                }

                if (!bare && body.Length > 0 && body[0] == '>')
                {
                    // Block quote: collect `>` lines + lazy paragraph continuations, strip the marker, recurse.
                    var inner = new List<string>();
                    var innerOff = new List<int>();
                    int j = i;
                    while (j < to)
                    {
                        var l = lines[j];
                        int li = Indent(l);
                        if (li <= 3 && li < l.Length && l[li] == '>')
                        {
                            int k = li + 1;
                            if (k < l.Length && l[k] == ' ') k++;
                            inner.Add(l.Substring(k)); innerOff.Add(offsets[j] + k);
                            j++;
                        }
                        else if (!IsBlank(l) && inner.Count > 0 && !IsBlank(inner[^1]) && !StartsBlock(l))
                        {
                            inner.Add(l); innerOff.Add(offsets[j]);   // lazy continuation (CommonMark §5.1)
                            j++;
                        }
                        else break;
                    }
                    innerOff.Add(innerOff.Count > 0 ? offsets[j] : offsets[i]);   // sentinel
                    blocks.Add(new MarkdownQuote(ParseBlocks(inner, 0, inner.Count, innerOff)));
                    i = j;
                    return;
                }

                if (!bare && TryListMarker(body, out bool ordered, out int start, out int markerWidth, out _))
                {
                    i = ParseList(lines, i, to, ordered, start, blocks, offsets);
                    return;
                }

                if (!bare && Has(MarkdownExtensions.Tables) && i + 1 < to && TryTable(lines, i, to, out var table, out int consumed))
                {
                    blocks.Add(table);
                    i += consumed;
                    return;
                }

                if (!bare && IsHtmlBlockStart(body))
                {
                    var sb = new StringBuilder();
                    int j = i;
                    while (j < to && !IsBlank(lines[j])) { sb.Append(lines[j]).Append('\n'); j++; }
                    blocks.Add(new MarkdownHtmlBlock(sb.ToString().TrimEnd('\n')));
                    i = j;
                    return;
                }

                // Paragraph: until a blank line or an interrupting block start; a setext underline closes it as a heading.
                {
                    var para = new List<string> { body.ToString() };
                    var paraOff = Mapped ? new List<int> { offsets[i] + ind } : null;
                    int j = i + 1;
                    while (j < to)
                    {
                        var l = lines[j];
                        if (IsBlank(l)) break;
                        var lb = l.AsSpan(Math.Min(Indent(l), 3));
                        if (Indent(l) <= 3 && TrySetext(lb, out int slevel))
                        {
                            blocks.Add(LeafOf(slevel, para, paraOff));
                            i = j + 1;
                            return;
                        }
                        if (Indent(l) <= 3 && InterruptsParagraph(lb)) break;
                        para.Add(l.TrimStart(' '));
                        paraOff?.Add(offsets[j] + (l.Length - para[^1].Length));
                        j++;
                    }
                    blocks.Add(LeafOf(0, para, paraOff));
                    i = j;
                }
            }
        }

        /// <summary>A paragraph (level 0) or setext heading from its lines, with the map from the joined inline text back
        /// to the source when the parse is mapped.</summary>
        private MarkdownBlock LeafOf(int level, List<string> para, List<int>? paraOff)
        {
            string joined = string.Join("\n", para);
            if (paraOff is null) return level == 0 ? new MarkdownParagraph(ParseInlines(joined)) : new MarkdownHeading(level, ParseInlines(joined));
            int[] m = LinesMap(para, paraOff, joined.Length);
            MarkdownInline[] inl = ParseInlines(joined, m);
            return level == 0
                ? new MarkdownParagraph(inl) { ContentStart = m[0], ContentEnd = m[^1] }
                : new MarkdownHeading(level, inl) { ContentStart = m[0], ContentEnd = m[^1], Setext = true };
        }

        private static int[] IdentityMap(int start, int length)
        {
            var m = new int[length + 1];
            for (int k = 0; k <= length; k++) m[k] = start + k;
            return m;
        }

        /// <summary>Position in the joined text -> source offset. A joining newline maps to the end of its line.</summary>
        private static int[] LinesMap(List<string> lines, List<int> origins, int joinedLength)
        {
            var m = new int[joinedLength + 1];
            int at = 0;
            for (int li = 0; li < lines.Count; li++)
            {
                for (int k = 0; k < lines[li].Length; k++) m[at++] = origins[li] + k;
                m[at] = origins[li] + lines[li].Length;   // the newline, or the end sentinel on the last line
                at++;
            }
            return m;
        }

        /// <summary>A line that holds only a block marker with no space after it: text until the space arrives, in the editor.</summary>
        private static bool IsBareMarkerLine(ReadOnlySpan<char> b)
        {
            if (b.Length == 0) return false;
            if (b[0] == '>') return b.Length == 1;
            if (b[0] == '#')
            {
                int n = 0;
                while (n < b.Length && b[n] == '#') n++;
                return n == b.Length && n <= 6;
            }
            if (b[0] == '-' || b[0] == '+' || b[0] == '*') return b.Length == 1;
            int d = 0;
            while (d < b.Length && d < 9 && char.IsAsciiDigit(b[d])) d++;
            return d > 0 && d == b.Length - 1 && (b[d] == '.' || b[d] == ')');
        }

        private static bool HasClosingFence(List<string> lines, int from, int to, char fc, int flen)
        {
            for (int j = from; j < to; j++)
            {
                var l = lines[j];
                if (IsClosingFence(l.AsSpan(Math.Min(Indent(l), 3)), fc, flen)) return true;
            }
            return false;
        }

        internal static bool IsFenceLine(ReadOnlySpan<char> b, out char fc, out int len) => TryFence(b, out fc, out len, out _);
        internal static bool IsClosingFenceLine(ReadOnlySpan<char> b, char fc, int openLen) => IsClosingFence(b, fc, openLen);

        /// <summary>A line that starts a non-paragraph block (used for lazy-continuation decisions).</summary>
        private bool StartsBlock(string l)
        {
            int ind = Indent(l);
            if (ind >= 4) return false;
            var b = l.AsSpan(ind);
            return InterruptsParagraph(b) || (b.Length > 0 && b[0] == '>') || TryFence(b, out _, out _, out _);
        }

        /// <summary>CommonMark §4.8: what may interrupt a paragraph — ATX heading, fence, thematic break, quote, a
        /// bullet list item, or an ordered item starting at 1 (not an indented code block, not an arbitrary number).</summary>
        private bool InterruptsParagraph(ReadOnlySpan<char> b)
        {
            if (b.Length == 0) return false;
            if (Mapped && IsBareMarkerLine(b)) return false;
            if (b[0] == '>') return true;
            if (TryAtxHeading(b, out _, out _)) return true;
            if (TryFence(b, out _, out _, out _)) return true;
            if (IsThematicBreak(b)) return true;
            if (TryListMarker(b, out bool ordered, out int start, out _, out bool emptyItem) && !emptyItem && (!ordered || start == 1)) return true;
            if (IsHtmlBlockStart(b)) return true;
            return false;
        }

        private int ParseList(List<string> lines, int i, int to, bool ordered, int start, List<MarkdownBlock> blocks, List<int> offsets)
        {
            var items = new List<MarkdownListItem>();
            bool loose = false;
            bool sawBlankBetween = false;
            char bulletChar = '\0';
            char delim = '\0';
            while (i < to)
            {
                string line = lines[i];
                int ind = Indent(line);
                if (Mapped && ind <= 3 && IsBareMarkerLine(line.AsSpan(ind))) break;
                if (ind > 3 || !TryListMarker(line.AsSpan(ind), out bool o, out _, out int mw, out bool empty) || o != ordered) break;
                char mc = line[ind];
                char dl = ordered ? line[ind + mw - 2] : '\0';
                if (items.Count == 0) { bulletChar = mc; delim = dl; }
                else if (ordered ? dl != delim : mc != bulletChar) break;   // a different marker starts a new list
                if (sawBlankBetween) loose = true;

                // Content column: marker + 1-4 spaces (CommonMark §5.2); an empty item has column marker+1.
                int contentCol = ind + mw;
                if (empty) contentCol = ind + mw;
                var inner = new List<string>();
                var innerOff = new List<int>();
                string first = line.Length > contentCol ? line.Substring(contentCol) : "";
                bool? isChecked = null;
                int markerOffset = -1;
                if (Has(MarkdownExtensions.TaskLists) && TryTaskMarker(first, out bool chk, out int tw))
                {
                    isChecked = chk;
                    markerOffset = offsets[i] + contentCol + 1;   // the char inside `[ ]` (tabs: MarkdownTasks re-finds the bracket on the source line)
                    first = first.Substring(tw);
                    inner.Add(first); innerOff.Add(offsets[i] + contentCol + tw);
                }
                else { inner.Add(first); innerOff.Add(offsets[i] + contentCol); }
                int j = i + 1;
                bool blankInside = false;
                bool pendingBlank = false;
                while (j < to)
                {
                    var l = lines[j];
                    if (IsBlank(l)) { pendingBlank = true; inner.Add(""); innerOff.Add(offsets[j]); j++; continue; }
                    int li = Indent(l);
                    if (li >= contentCol)
                    {
                        if (pendingBlank) blankInside = true;
                        pendingBlank = false;
                        inner.Add(l.Substring(contentCol)); innerOff.Add(offsets[j] + contentCol);
                        j++;
                        continue;
                    }
                    if (pendingBlank) break;   // a blank then an outdented line ends the item
                    // Lazy continuation of a paragraph inside the item.
                    if (!StartsBlock(l) && inner.Count > 0 && !IsBlank(inner[^1]) && !TryListMarker(l.AsSpan(li), out _, out _, out _, out _))
                    {
                        inner.Add(l.TrimStart(' ')); innerOff.Add(offsets[j] + li);
                        j++;
                        continue;
                    }
                    break;
                }
                // Trailing blanks belong between items, not inside this one.
                int endTrim = inner.Count;
                while (endTrim > 0 && IsBlank(inner[endTrim - 1])) endTrim--;
                sawBlankBetween = endTrim < inner.Count && j < to;
                if (blankInside && HasBlankBetweenBlocks(inner, endTrim)) loose = true;
                // Document order: this item's ordinal is taken BEFORE its children parse (a nested task counts after
                // its parent — the order MarkdownTasks.Toggle walks the source in).
                int taskIndex = isChecked is null ? -1 : _taskCount++;
                innerOff.Add(offsets[j]);   // sentinel: the end of the item's last line region
                var children = ParseBlocks(inner, 0, endTrim, innerOff);
                items.Add(new MarkdownListItem(children, isChecked, taskIndex, markerOffset)
                {
                    MarkerStart = offsets[i] + ind,
                    ContentStart = innerOff[0],
                    LineEnd = offsets[i] + line.Length,
                    Ordinal = ordered ? ParseOrdinal(line.AsSpan(ind)) : 0,
                });
                i = j;
            }
            blocks.Add(new MarkdownList(ordered, start, !loose, items.ToArray()));
            return i;
        }

        /// <summary>True when a blank line sits between two non-blank content lines of the item (a loose item).
        /// Blank lines inside a fenced code block do not count.</summary>
        private static bool HasBlankBetweenBlocks(List<string> inner, int end)
        {
            bool inFence = false;
            char fc = '\0'; int flen = 0;
            bool seenBlank = false;
            for (int k = 0; k < end; k++)
            {
                var l = inner[k];
                var b = l.AsSpan(Math.Min(Indent(l), 3));
                if (inFence) { if (IsClosingFence(b, fc, flen)) inFence = false; continue; }
                if (TryFence(b, out fc, out flen, out _)) { inFence = true; seenBlank = false; continue; }
                if (IsBlank(l)) { if (k > 0) seenBlank = true; continue; }
                if (seenBlank && Indent(l) < 4) return true;
                seenBlank = false;
            }
            return false;
        }

        // ── block-start recognisers ─────────────────────────────────────────────────────────────────────────────────

        private static bool TryFence(ReadOnlySpan<char> b, out char fc, out int len, out string info)
        {
            fc = '\0'; len = 0; info = "";
            if (b.Length < 3 || (b[0] != '`' && b[0] != '~')) return false;
            fc = b[0];
            while (len < b.Length && b[len] == fc) len++;
            if (len < 3) return false;
            var rest = b.Slice(len).Trim();
            if (fc == '`' && rest.IndexOf('`') >= 0) return false;
            int sp = rest.IndexOfAny(" \t");
            info = (sp < 0 ? rest : rest.Slice(0, sp)).ToString();
            return true;
        }

        private static bool IsClosingFence(ReadOnlySpan<char> b, char fc, int openLen)
        {
            int n = 0;
            while (n < b.Length && b[n] == fc) n++;
            return n >= openLen && b.Slice(n).Trim().Length == 0;
        }

        private static bool TryAtxHeading(ReadOnlySpan<char> b, out int level, out string text)
        {
            level = 0; text = "";
            while (level < b.Length && b[level] == '#') level++;
            if (level == 0 || level > 6) return false;
            if (level < b.Length && b[level] != ' ') return false;
            var t = b.Slice(level).Trim();
            // Optional closing sequence: spaces then #s at the end.
            int e = t.Length;
            while (e > 0 && t[e - 1] == '#') e--;
            if (e < t.Length && (e == 0 || t[e - 1] == ' ')) t = t.Slice(0, e).TrimEnd();
            text = t.ToString();
            return true;
        }

        private static bool TrySetext(ReadOnlySpan<char> b, out int level)
        {
            level = 0;
            var t = b.TrimEnd();
            if (t.Length == 0) return false;
            char c = t[0];
            if (c != '=' && c != '-') return false;
            foreach (char x in t) if (x != c) return false;
            level = c == '=' ? 1 : 2;
            return true;
        }

        private static bool IsThematicBreak(ReadOnlySpan<char> b)
        {
            char c = '\0'; int n = 0;
            foreach (char x in b)
            {
                if (x == ' ') continue;
                if (c == '\0') { if (x != '-' && x != '*' && x != '_') return false; c = x; }
                else if (x != c) return false;
                n++;
            }
            return n >= 3;
        }

        /// <summary>A list marker at the start of <paramref name="b"/>: `-`, `+`, `*` or 1-9 digits + `.`/`)`,
        /// followed by 1-4 spaces (or end of line = an empty item). <paramref name="width"/> is the content column
        /// relative to the marker (marker + the spaces that follow; 5+ spaces count as one, the rest is content indent).</summary>
        private static bool TryListMarker(ReadOnlySpan<char> b, out bool ordered, out int start, out int width, out bool empty)
        {
            ordered = false; start = 1; width = 0; empty = false;
            if (b.Length == 0) return false;
            int m;
            if (b[0] == '-' || b[0] == '+' || b[0] == '*')
            {
                m = 1;
            }
            else
            {
                int d = 0;
                while (d < b.Length && d < 9 && char.IsAsciiDigit(b[d])) d++;
                if (d == 0 || d >= b.Length || (b[d] != '.' && b[d] != ')')) return false;
                ordered = true;
                start = int.Parse(b.Slice(0, d), CultureInfo.InvariantCulture);
                m = d + 1;
            }
            if (m >= b.Length || b.Slice(m).Trim().Length == 0) { width = m + 1; empty = true; return true; }
            if (b[m] != ' ') return false;
            int sp = 0;
            while (m + sp < b.Length && b[m + sp] == ' ') sp++;
            width = m + (sp > 4 ? 1 : sp);
            return true;
        }

        private static int ParseOrdinal(ReadOnlySpan<char> b)
        {
            int d = 0;
            while (d < b.Length && d < 9 && char.IsAsciiDigit(b[d])) d++;
            return d == 0 ? 0 : int.Parse(b.Slice(0, d), CultureInfo.InvariantCulture);
        }

        private static bool TryTaskMarker(string s, out bool isChecked, out int width)
        {
            isChecked = false; width = 0;
            if (s.Length < 3 || s[0] != '[' || s[2] != ']') return false;
            char c = s[1];
            if (c != ' ' && c != 'x' && c != 'X') return false;
            if (s.Length > 3 && s[3] != ' ') return false;
            isChecked = c != ' ';
            width = s.Length > 3 ? 4 : 3;
            return true;
        }

        private static bool IsHtmlBlockStart(ReadOnlySpan<char> b)
        {
            if (b.Length < 2 || b[0] != '<') return false;
            if (b.StartsWith("<!--")) return true;
            int i = 1;
            if (b[i] == '/') i++;
            if (i >= b.Length || !char.IsAsciiLetter(b[i])) return false;
            // Only a BLOCK-level tag line qualifies (CommonMark §4.6 type 6/7): `<div>`, `<p class=…>`, `</section>`.
            int e = i;
            while (e < b.Length && (char.IsAsciiLetterOrDigit(b[e]) || b[e] == '-')) e++;
            if (e >= b.Length) return true;
            return b[e] == '>' || b[e] == ' ' || b[e] == '/';
        }

        // ── GFM tables ──────────────────────────────────────────────────────────────────────────────────────────────

        private bool TryTable(List<string> lines, int i, int to, out MarkdownTable table, out int consumed)
        {
            table = null!; consumed = 0;
            var header = lines[i];
            if (header.IndexOf('|') < 0) return false;
            if (!TryDelimiterRow(lines[i + 1], out var aligns)) return false;
            var head = SplitCells(header);
            if (head.Count != aligns.Length) return false;
            var headInl = new MarkdownInline[head.Count][];
            for (int c = 0; c < head.Count; c++) headInl[c] = ParseInlines(head[c]);
            var rows = new List<MarkdownInline[][]>();
            int j = i + 2;
            while (j < to && !IsBlank(lines[j]) && !StartsBlock(lines[j]))
            {
                var cells = SplitCells(lines[j]);
                var row = new MarkdownInline[head.Count][];
                for (int c = 0; c < head.Count; c++) row[c] = c < cells.Count ? ParseInlines(cells[c]) : Array.Empty<MarkdownInline>();
                rows.Add(row);
                j++;
            }
            table = new MarkdownTable(headInl, aligns, rows.ToArray());
            consumed = j - i;
            return true;
        }

        private static bool TryDelimiterRow(string line, out MarkdownTableAlign[] aligns)
        {
            aligns = Array.Empty<MarkdownTableAlign>();
            if (line.IndexOf('-') < 0) return false;
            var cells = SplitCells(line);
            if (cells.Count == 0) return false;
            var a = new MarkdownTableAlign[cells.Count];
            for (int c = 0; c < cells.Count; c++)
            {
                var t = cells[c].AsSpan().Trim();
                if (t.Length == 0) return false;
                bool l = t[0] == ':', r = t[^1] == ':';
                var dashes = t.Slice(l ? 1 : 0, t.Length - (l ? 1 : 0) - (r ? 1 : 0));
                if (dashes.Length == 0) return false;
                foreach (char x in dashes) if (x != '-') return false;
                a[c] = l && r ? MarkdownTableAlign.Center : l ? MarkdownTableAlign.Left : r ? MarkdownTableAlign.Right : MarkdownTableAlign.None;
            }
            aligns = a;
            return true;
        }

        /// <summary>Split a table row on unescaped pipes (a `\|` stays literal, a pipe inside a code span splits —
        /// the GFM rule, spec §4.10 example 200), dropping the optional leading/trailing pipe.</summary>
        private static List<string> SplitCells(string line)
        {
            var cells = new List<string>();
            var sb = new StringBuilder();
            var t = line.Trim();
            int i = 0;
            if (t.Length > 0 && t[0] == '|') i = 1;
            for (; i < t.Length; i++)
            {
                char c = t[i];
                if (c == '\\' && i + 1 < t.Length && t[i + 1] == '|') { sb.Append('|'); i++; continue; }
                if (c == '|') { cells.Add(sb.ToString().Trim()); sb.Clear(); continue; }
                sb.Append(c);
            }
            if (sb.Length > 0 || (t.Length > 0 && t[^1] != '|')) cells.Add(sb.ToString().Trim());
            return cells;
        }

        // ── inline parsing (CommonMark §6: a token pass, then the delimiter-run emphasis algorithm) ─────────────────

        private sealed class Tok
        {
            public MarkdownInline? Node;        // a finished node (text, code span, link…)
            public char Delim;                  // '*', '_', '~' for a delimiter run; '\0' for a node
            public int Count;                   // remaining length of the run
            public bool CanOpen, CanClose;
            public string Text = "";            // the run's literal text (what remains if unmatched)
            public int Pos;                     // mapped parse: the run's first unconsumed position in the inline text
            public int[]? M;                    // mapped parse: inline text position -> source offset
        }

        public MarkdownInline[] ParseInlines(string text, int[]? m = null)
        {
            var toks = Tokenize(text, m);
            ProcessEmphasis(toks);
            return Flatten(toks);
        }

        private List<Tok> Tokenize(string s, int[]? m)
        {
            var toks = new List<Tok>();
            var sb = new StringBuilder();
            int n = s.Length;
            // Mapped parse: per text character, the source range it came from (an escape or entity is one range).
            List<int>? starts = m is null ? null : new List<int>();
            List<int>? ends = m is null ? null : new List<int>();
            int Mp(int p) => m is null ? -1 : m[p];
            void A(char ch, int from, int to)
            {
                sb.Append(ch);
                if (m is not null) { starts!.Add(m[from]); ends!.Add(m[to]); }
            }
            void FlushText()
            {
                if (sb.Length == 0) return;
                int[]? map = null;
                if (m is not null)
                {
                    map = new int[starts!.Count * 2];
                    for (int k = 0; k < starts.Count; k++) { map[2 * k] = starts[k]; map[2 * k + 1] = ends![k]; }
                    starts.Clear(); ends!.Clear();
                }
                toks.Add(new Tok { Node = new MarkdownText(sb.ToString()) { Map = map } });
                sb.Clear();
            }

            int i = 0;
            while (i < n)
            {
                char c = s[i];

                // Consumer syntaxes first, in registration order (an extension may claim `[[` ahead of `[`).
                if (_o.InlineSyntaxes is { Count: > 0 } exts)
                {
                    bool matched = false;
                    for (int x = 0; x < exts.Count; x++)
                    {
                        var ext = exts[x];
                        if (ext.Open.Length == 0 || !s.AsSpan(i).StartsWith(ext.Open, StringComparison.Ordinal)) continue;
                        int close = s.IndexOf(ext.Close, i + ext.Open.Length, StringComparison.Ordinal);
                        if (close < 0) continue;
                        var node = ext.Build(s.Substring(i + ext.Open.Length, close - i - ext.Open.Length));
                        if (node is null) continue;
                        FlushText();
                        toks.Add(new Tok { Node = m is null ? node : node with { SourceStart = m[i], SourceEnd = m[close + ext.Close.Length] } });
                        i = close + ext.Close.Length;
                        matched = true;
                        break;
                    }
                    if (matched) continue;
                }

                switch (c)
                {
                    case '\\':
                        if (i + 1 < n && IsAsciiPunct(s[i + 1])) { A(s[i + 1], i, i + 2); i += 2; continue; }
                        if (i + 1 < n && s[i + 1] == '\n')
                        {
                            FlushText();
                            int brStart = Mp(i);
                            i += 2;
                            toks.Add(new Tok { Node = new MarkdownLineBreak(true) { SourceStart = brStart, SourceEnd = Mp(i) } });
                            continue;
                        }
                        A('\\', i, i + 1); i++; continue;

                    case '\n':
                    {
                        // Hard break on 2+ trailing spaces, else a soft break.
                        int trailing = 0;
                        while (sb.Length - trailing > 0 && sb[sb.Length - 1 - trailing] == ' ') trailing++;
                        if (trailing > 0)
                        {
                            sb.Length -= trailing;
                            if (m is not null) { starts!.RemoveRange(starts.Count - trailing, trailing); ends!.RemoveRange(ends.Count - trailing, trailing); }
                        }
                        FlushText();
                        int lbStart = Mp(i - trailing);
                        i++;
                        while (i < n && s[i] == ' ') i++;   // leading spaces of the next line are stripped
                        toks.Add(new Tok { Node = new MarkdownLineBreak(trailing >= 2) { SourceStart = lbStart, SourceEnd = Mp(i) } });
                        continue;
                    }

                    case '`':
                    {
                        int run = 0;
                        while (i + run < n && s[i + run] == '`') run++;
                        int close = FindBacktickClose(s, i + run, run);
                        if (close < 0) { for (int k = 0; k < run; k++) A('`', i + k, i + k + 1); i += run; continue; }
                        FlushText();
                        var code = s.AsSpan(i + run, close - i - run);
                        // CommonMark §6.1: line endings become spaces; one leading+trailing space pair is stripped.
                        string codeText = code.ToString().Replace('\n', ' ');
                        int stripped = 0;
                        if (codeText.Length >= 2 && codeText[0] == ' ' && codeText[^1] == ' ' && codeText.AsSpan().Trim().Length > 0)
                        {
                            codeText = codeText.Substring(1, codeText.Length - 2);
                            stripped = 1;
                        }
                        int[]? codeMap = null;
                        if (m is not null)
                        {
                            codeMap = new int[codeText.Length * 2];
                            for (int k = 0; k < codeText.Length; k++) { codeMap[2 * k] = m[i + run + stripped + k]; codeMap[2 * k + 1] = m[i + run + stripped + k + 1]; }
                        }
                        toks.Add(new Tok
                        {
                            Node = new MarkdownCodeSpan(codeText)
                            {
                                Map = codeMap, SourceStart = Mp(i), SourceEnd = Mp(close + run),
                                OpenStart = Mp(i), OpenEnd = Mp(i + run), CloseStart = Mp(close), CloseEnd = Mp(close + run),
                            },
                        });
                        i = close + run;
                        continue;
                    }

                    case '&':
                    {
                        if (TryEntity(s, i, out string decoded, out int used)) { foreach (char dc in decoded) A(dc, i, i + used); i += used; continue; }
                        A('&', i, i + 1); i++; continue;
                    }

                    case '<':
                    {
                        if (TryAngleAutolink(s, i, out string url, out string label, out int used))
                        {
                            FlushText();
                            toks.Add(new Tok
                            {
                                Node = new MarkdownLink([new MarkdownText(label) { Map = SpanMap(m, i + 1, label.Length) }], url, "")
                                {
                                    SourceStart = Mp(i), SourceEnd = Mp(i + used),
                                    OpenStart = Mp(i), OpenEnd = Mp(i + 1), CloseStart = Mp(i + used - 1), CloseEnd = Mp(i + used),
                                },
                            });
                            i += used;
                            continue;
                        }
                        A('<', i, i + 1); i++; continue;
                    }

                    case '!':
                        if (i + 1 < n && s[i + 1] == '[' && TryLink(s, i + 1, m, out var alt, out string iurl, out string ititle, out int iused, out _, out _, out _))
                        {
                            FlushText();
                            toks.Add(new Tok { Node = new MarkdownImage(PlainText(alt), iurl, ititle) { SourceStart = Mp(i), SourceEnd = Mp(i + 1 + iused) } });
                            i += 1 + iused;
                            continue;
                        }
                        A('!', i, i + 1); i++; continue;

                    case '[':
                        if (TryLink(s, i, m, out var kids, out string lurl, out string ltitle, out int lused, out int labelClose, out int urlAt, out int urlEnd))
                        {
                            FlushText();
                            toks.Add(new Tok
                            {
                                Node = new MarkdownLink(kids, lurl, ltitle)
                                {
                                    SourceStart = Mp(i), SourceEnd = Mp(i + lused),
                                    OpenStart = Mp(i), OpenEnd = Mp(i + 1), CloseStart = Mp(labelClose), CloseEnd = Mp(i + lused),
                                    UrlStart = urlAt < 0 ? -1 : Mp(urlAt), UrlEnd = urlAt < 0 ? -1 : Mp(urlEnd),
                                },
                            });
                            i += lused;
                            continue;
                        }
                        A('[', i, i + 1); i++; continue;

                    case '*':
                    case '_':
                    case '~':
                    case '=':
                    {
                        if (c == '~' && !Has(MarkdownExtensions.Strikethrough)) { A(c, i, i + 1); i++; continue; }
                        if (c == '=' && !Has(MarkdownExtensions.Mark)) { A(c, i, i + 1); i++; continue; }
                        int run = 0;
                        while (i + run < n && s[i + run] == c) run++;
                        if ((c == '~' || c == '=') && run != 2) { for (int k = 0; k < run; k++) A(c, i + k, i + k + 1); i += run; continue; }   // GFM: exactly two tildes; mark: exactly two =
                        char before = i > 0 ? s[i - 1] : ' ';
                        char after = i + run < n ? s[i + run] : ' ';
                        bool leftFlank = !IsWs(after) && (!IsPunct(after) || IsWs(before) || IsPunct(before));
                        bool rightFlank = !IsWs(before) && (!IsPunct(before) || IsWs(after) || IsPunct(after));
                        bool canOpen, canClose;
                        if (c == '_')
                        {
                            canOpen = leftFlank && (!rightFlank || IsPunct(before));
                            canClose = rightFlank && (!leftFlank || IsPunct(after));
                        }
                        else { canOpen = leftFlank; canClose = rightFlank; }
                        FlushText();
                        toks.Add(new Tok { Delim = c, Count = run, CanOpen = canOpen, CanClose = canClose, Text = new string(c, run), Pos = i, M = m });
                        i += run;
                        continue;
                    }

                    default:
                        if (Has(MarkdownExtensions.Autolinks) && (c == 'h' || c == 'w') && TryBareAutolink(s, i, out string burl, out string blabel, out int bused)
                            && (i == 0 || IsWs(s[i - 1]) || s[i - 1] == '(' || s[i - 1] == '*' || s[i - 1] == '_' || s[i - 1] == '~'))
                        {
                            FlushText();
                            toks.Add(new Tok
                            {
                                Node = new MarkdownLink([new MarkdownText(blabel) { Map = SpanMap(m, i, blabel.Length) }], burl, "")
                                { SourceStart = Mp(i), SourceEnd = Mp(i + bused) },
                            });
                            i += bused;
                            continue;
                        }
                        A(c, i, i + 1); i++; continue;
                }
            }
            FlushText();
            return toks;
        }

        private static int[]? SpanMap(int[]? m, int from, int length)
        {
            if (m is null) return null;
            var map = new int[length * 2];
            for (int k = 0; k < length; k++) { map[2 * k] = m[from + k]; map[2 * k + 1] = m[from + k + 1]; }
            return map;
        }

        /// <summary>CommonMark §6.2 "process emphasis": walk closers, find the nearest matching opener, wrap the
        /// tokens between them. Strong (2) is taken before em (1); `~` only ever pairs 2-2.</summary>
        private static void ProcessEmphasis(List<Tok> toks)
        {
            int ci = 0;
            while (ci < toks.Count)
            {
                var closer = toks[ci];
                if (closer.Delim == '\0' || !closer.CanClose || closer.Count == 0) { ci++; continue; }
                bool matched = false;
                for (int oi = ci - 1; oi >= 0; oi--)
                {
                    var opener = toks[oi];
                    if (opener.Delim != closer.Delim || !opener.CanOpen || opener.Count == 0) continue;
                    // The "multiple of 3" rule (§6.2 rule 9/10) for runs that both open and close.
                    if (closer.Delim != '~' && closer.Delim != '=' && (opener.CanClose || closer.CanOpen) && (opener.Count + closer.Count) % 3 == 0
                        && !(opener.Count % 3 == 0 && closer.Count % 3 == 0)) continue;

                    int use = closer.Delim is '~' or '=' ? 2 : (opener.Count >= 2 && closer.Count >= 2 ? 2 : 1);
                    // Collect the inner tokens (oi+1 .. ci-1) into one node; an unmatched run inside stays literal.
                    var inner = new List<Tok>();
                    for (int k = oi + 1; k < ci; k++) inner.Add(toks[k]);
                    MarkdownInline[] kids = Flatten(inner);
                    MarkdownInline node = closer.Delim == '~' ? new MarkdownStrikethrough(kids) : closer.Delim == '=' ? new MarkdownMark(kids) : new MarkdownEmphasis(kids, use == 2);
                    if (closer.M is { } dm)
                    {
                        // The inner end of each run is what pairs: an opener spends from its right, a closer from its left.
                        int openAt = opener.Pos + opener.Count - use;
                        node = node with
                        {
                            OpenStart = dm[openAt], OpenEnd = dm[openAt + use],
                            CloseStart = dm[closer.Pos], CloseEnd = dm[closer.Pos + use],
                            SourceStart = dm[openAt], SourceEnd = dm[closer.Pos + use],
                        };
                        closer.Pos += use;
                    }
                    opener.Count -= use;
                    closer.Count -= use;
                    opener.Text = opener.Text.Substring(0, opener.Count);
                    closer.Text = closer.Text.Substring(0, closer.Count);
                    toks.RemoveRange(oi + 1, ci - oi - 1);
                    toks.Insert(oi + 1, new Tok { Node = node });
                    ci = oi + 2;   // the closer now sits right after the new node
                    matched = true;
                    break;
                }
                // A closer with delimiters left is retried against earlier openers (`***a** b*`); else move on.
                if (!matched || closer.Count == 0) ci++;
            }
        }

        private static MarkdownInline[] Flatten(List<Tok> toks)
        {
            var list = new List<MarkdownInline>(toks.Count);
            StringBuilder? pending = null;
            foreach (var t in toks)
            {
                if (t.M is { } dm && t.Delim != '\0')
                {
                    // Mapped: an unmatched delimiter stays its own node, so the editor can dim an opener still waiting for its closer.
                    if (t.Count > 0) list.Add(new MarkdownText(t.Text) { Map = SpanMap(dm, t.Pos, t.Count), UnmatchedOpener = t.CanOpen });
                    continue;
                }
                if (t.Node is MarkdownText { Map: not null }) { list.Add(t.Node); continue; }   // mapped text keeps its map: never merged
                string? lit = t.Node is MarkdownText mt ? mt.Text : t.Delim != '\0' ? (t.Count > 0 ? t.Text : null) : null;
                if (lit is not null)
                {
                    (pending ??= new StringBuilder()).Append(lit);
                    continue;
                }
                if (t.Node is null) continue;
                if (pending is { Length: > 0 }) { list.Add(new MarkdownText(pending.ToString())); pending.Clear(); }
                list.Add(t.Node);
            }
            if (pending is { Length: > 0 }) list.Add(new MarkdownText(pending.ToString()));
            return list.ToArray();
        }

        private static string PlainText(MarkdownInline[] nodes)
        {
            var sb = new StringBuilder();
            AppendPlain(nodes, sb);
            return sb.ToString();
        }

        private static void AppendPlain(MarkdownInline[] nodes, StringBuilder sb)
        {
            foreach (var n in nodes)
            {
                switch (n)
                {
                    case MarkdownText t: sb.Append(t.Text); break;
                    case MarkdownCodeSpan c: sb.Append(c.Code); break;
                    case MarkdownEmphasis e: AppendPlain(e.Children, sb); break;
                    case MarkdownStrikethrough st: AppendPlain(st.Children, sb); break;
                    case MarkdownMark mk: AppendPlain(mk.Children, sb); break;
                    case MarkdownLink l: AppendPlain(l.Children, sb); break;
                    case MarkdownImage im: sb.Append(im.Alt); break;
                    case MarkdownLineBreak: sb.Append(' '); break;
                }
            }
        }

        // ── inline recognisers ──────────────────────────────────────────────────────────────────────────────────────

        private static int FindBacktickClose(string s, int from, int run)
        {
            int i = from;
            while (i < s.Length)
            {
                if (s[i] != '`') { i++; continue; }
                int r = 0;
                while (i + r < s.Length && s[i + r] == '`') r++;
                if (r == run) return i;
                i += r;
            }
            return -1;
        }

        /// <summary>`[text](dest "title")`, `[text][ref]`, `[text][]`, `[ref]` — with balanced nested brackets in the
        /// text. Link text is parsed recursively (links may not nest: an inner link stays literal).</summary>
        private bool TryLink(string s, int i, int[]? m, out MarkdownInline[] children, out string url, out string title, out int used,
            out int labelClose, out int urlAt, out int urlEnd)
        {
            children = Array.Empty<MarkdownInline>(); url = ""; title = ""; used = 0; labelClose = -1; urlAt = -1; urlEnd = -1;
            int depth = 0, j = i;
            for (; j < s.Length; j++)
            {
                char c = s[j];
                if (c == '\\') { j++; continue; }
                if (c == '`') { int r = 0; while (j + r < s.Length && s[j + r] == '`') r++; int cl = FindBacktickClose(s, j + r, r); if (cl >= 0) { j = cl + r - 1; continue; } }
                if (c == '[') depth++;
                else if (c == ']' && --depth == 0) break;
            }
            if (j >= s.Length) return false;
            string inner = s.Substring(i + 1, j - i - 1);
            int k = j + 1;
            if (k < s.Length && s[k] == '(')
            {
                var rest = s.AsSpan(k + 1);
                int close = FindParenClose(rest);
                if (close < 0) return false;
                var body = rest.Slice(0, close).Trim();
                if (body.Length == 0) { url = ""; }
                else if (!TryParseDestinationAndTitle(body, out url, out title, out int bu) || body.Slice(bu).Trim().Length != 0) return false;
                used = k + 1 + close + 1 - i;
                // The written destination, for the link card's one replace.
                int lead = 0;
                while (lead < rest.Length && char.IsWhiteSpace(rest[lead])) lead++;
                urlAt = k + 1 + lead;
                int dl = 0;
                if (body.Length > 0 && body[0] == '<') dl = body.IndexOf('>') + 1;
                else while (dl < body.Length && !char.IsWhiteSpace(body[dl])) dl++;
                urlEnd = urlAt + dl;
            }
            else
            {
                string label;
                if (k < s.Length && s[k] == '[')
                {
                    int e = s.IndexOf(']', k + 1);
                    if (e < 0) return false;
                    label = e == k + 1 ? inner : s.Substring(k + 1, e - k - 1);
                    used = e + 1 - i;
                }
                else { label = inner; used = j + 1 - i; }
                if (!_refs.TryGetValue(NormalizeLabel(label), out var def)) return false;
                url = def.Url; title = def.Title;
            }
            if (inner.Contains('[') && inner.Contains("](")) return false;   // no nested links
            labelClose = j;
            children = ParseInlines(inner, m is null ? null : m.AsSpan(i + 1, inner.Length + 1).ToArray());
            return true;
        }

        private static int FindParenClose(ReadOnlySpan<char> s)
        {
            int depth = 0;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '\\') { i++; continue; }
                if (c == '(') depth++;
                else if (c == ')') { if (depth == 0) return i; depth--; }
            }
            return -1;
        }

        /// <summary>Destination (`&lt;…&gt;` or bare, no spaces) + optional title in `"…"`, `'…'` or `(…)`.</summary>
        private static bool TryParseDestinationAndTitle(ReadOnlySpan<char> s, out string url, out string title, out int used)
        {
            url = ""; title = ""; used = 0;
            s = s.TrimStart();
            int i = 0;
            if (s.Length > 0 && s[0] == '<')
            {
                int e = s.IndexOf('>');
                if (e < 0) return false;
                url = Unescape(s.Slice(1, e - 1));
                i = e + 1;
            }
            else
            {
                while (i < s.Length && !char.IsWhiteSpace(s[i])) i++;
                url = Unescape(s.Slice(0, i));
            }
            var rest = s.Slice(i).TrimStart();
            if (rest.Length > 0 && (rest[0] == '"' || rest[0] == '\'' || rest[0] == '('))
            {
                char close = rest[0] == '(' ? ')' : rest[0];
                int e = rest.Slice(1).IndexOf(close);
                if (e < 0) return false;
                e += 1;
                title = Unescape(rest.Slice(1, e - 1));
                rest = rest.Slice(e + 1);
            }
            used = s.Length - rest.Length;
            return true;
        }

        private static string Unescape(ReadOnlySpan<char> s)
        {
            if (s.IndexOf('\\') < 0 && s.IndexOf('&') < 0) return s.ToString();
            var sb = new StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                if (s[i] == '\\' && i + 1 < s.Length && IsAsciiPunct(s[i + 1])) { sb.Append(s[i + 1]); i++; continue; }
                if (s[i] == '&' && TryEntity(s, i, out string d, out int u)) { sb.Append(d); i += u - 1; continue; }
                sb.Append(s[i]);
            }
            return sb.ToString();
        }

        private static bool TryAngleAutolink(string s, int i, out string url, out string label, out int used)
        {
            url = label = ""; used = 0;
            int e = s.IndexOf('>', i + 1);
            if (e < 0) return false;
            var body = s.AsSpan(i + 1, e - i - 1);
            if (body.Length == 0 || body.IndexOfAny(" <\n") >= 0) return false;
            int colon = body.IndexOf(':');
            if (colon >= 2 && colon <= 32 && char.IsAsciiLetter(body[0]))
            {
                for (int k = 0; k < colon; k++) if (!(char.IsAsciiLetterOrDigit(body[k]) || body[k] == '+' || body[k] == '.' || body[k] == '-')) return false;
                url = label = body.ToString();
                used = e + 1 - i;
                return true;
            }
            int at = body.IndexOf('@');
            if (at > 0 && body.Slice(at + 1).IndexOf('.') > 0)
            {
                label = body.ToString();
                url = "mailto:" + label;
                used = e + 1 - i;
                return true;
            }
            return false;
        }

        /// <summary>GFM §6.9 extended autolinks: `http://`, `https://`, `www.` up to whitespace or `&lt;`; trailing
        /// punctuation and an unbalanced `)` are excluded.</summary>
        private static bool TryBareAutolink(string s, int i, out string url, out string label, out int used)
        {
            url = label = ""; used = 0;
            var r = s.AsSpan(i);
            bool www = r.StartsWith("www.", StringComparison.OrdinalIgnoreCase);
            if (!www && !r.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !r.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return false;
            int e = 0;
            while (e < r.Length && !char.IsWhiteSpace(r[e]) && r[e] != '<') e++;
            int start = www ? 4 : r.IndexOf("://") + 3;
            if (e <= start || r.Slice(start, e - start).IndexOf('.') < 0) return false;
            // Trailing punctuation + unbalanced ')' trimming.
            while (e > start && ("?!.,:*_~'\"".IndexOf(r[e - 1]) >= 0 || (r[e - 1] == ')' && Count(r.Slice(0, e), '(') < Count(r.Slice(0, e), ')')))) e--;
            label = r.Slice(0, e).ToString();
            url = www ? "http://" + label : label;
            used = e;
            return true;
        }

        private static int Count(ReadOnlySpan<char> s, char c) { int n = 0; foreach (char x in s) if (x == c) n++; return n; }

        private static bool TryEntity(ReadOnlySpan<char> s, int i, out string decoded, out int used)
        {
            decoded = ""; used = 0;
            int e = s.Slice(i).IndexOf(';');
            if (e < 2 || e > 32) return false;
            var name = s.Slice(i + 1, e - 1);
            if (name[0] == '#')
            {
                bool hex = name.Length > 1 && (name[1] == 'x' || name[1] == 'X');
                var digits = name.Slice(hex ? 2 : 1);
                if (digits.Length == 0 || digits.Length > 7) return false;
                if (!int.TryParse(digits, hex ? NumberStyles.HexNumber : NumberStyles.None, CultureInfo.InvariantCulture, out int cp)) return false;
                if (cp <= 0 || cp > 0x10FFFF || (cp >= 0xD800 && cp <= 0xDFFF)) cp = 0xFFFD;
                decoded = char.ConvertFromUtf32(cp);
                used = e + 1;
                return true;
            }
            string? d = name switch
            {
                "amp" => "&", "lt" => "<", "gt" => ">", "quot" => "\"", "apos" => "'", "nbsp" => " ",
                "copy" => "©", "reg" => "®", "trade" => "™", "hellip" => "…", "mdash" => "—",
                "ndash" => "–", "laquo" => "«", "raquo" => "»", "ldquo" => "“", "rdquo" => "”",
                "lsquo" => "‘", "rsquo" => "’", "bull" => "•", "middot" => "·", "times" => "×",
                "rarr" => "→", "larr" => "←", "deg" => "°", "euro" => "€", "pound" => "£",
                _ => null,
            };
            if (d is null) return false;
            decoded = d;
            used = e + 1;
            return true;
        }

        private static bool IsWs(char c) => char.IsWhiteSpace(c);
        private static bool IsPunct(char c) => IsAsciiPunct(c) || (c > 127 && (char.IsPunctuation(c) || char.IsSymbol(c)));
        private static bool IsAsciiPunct(char c) => c < 128 && (char.IsPunctuation(c) || char.IsSymbol(c));
    }
}
