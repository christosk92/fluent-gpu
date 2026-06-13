using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace FluentGpu.Localization;

/// <summary>
/// The message-template engine: resolves named <c>{placeholder}</c> interpolation and an ICU MessageFormat
/// <b>subset</b> (<c>plural</c> + <c>select</c>) into a surface string. This is the modern feature RESW lacks — RESW
/// strings are static with positional <c>{0}</c> holes and no grammatical number/gender. Grammar (a small
/// recursive-descent parser):
///
/// <code>
/// message   := ( text | '{' arg '}' )*
/// arg       := name                                   // simple interpolation: {name}
///            | name ',' 'plural' ',' pluralBody       // {count, plural, =0 {…} one {# item} other {# items}}
///            | name ',' 'select' ',' selectBody       // {gender, select, male {…} female {…} other {…}}
/// pluralBody:= ( selector message )+                  // selector = '=' digits  |  one|two|few|many|zero|other
/// selectBody:= ( keyword message )+                   // keyword  = an identifier; 'other' is the required fallback
/// text      := any chars except '{' '}' (with '#' meaningful only inside a plural body, where it = the count)
/// </code>
///
/// <para><b>Named, not positional.</b> Arguments are bound by NAME (<c>{name}</c>), so a translator sees meaning and may
/// reorder freely across languages (the i18next / ICU convention; WinUI's <c>{0}</c> forces source word order). A
/// missing argument renders VISIBLY (<c>{name}</c> is kept as-is) and never throws — a missing binding is a loud
/// editorial bug, not a crash.</para>
///
/// <para><b>Nested.</b> A plural/select branch body is itself a full message, so it may contain further <c>{name}</c>
/// placeholders and even nested plural/select blocks — the parser recurses. Inside a <c>plural</c> body the literal
/// <c>#</c> is replaced by the (invariantly-formatted) count.</para>
///
/// <para><b>Plural categories</b> come from <see cref="PluralRules"/> (hand-rolled CLDR-lite, mandatory under
/// <c>InvariantGlobalization</c>); an <c>=N</c> exact-match selector wins over the category selector (ICU semantics).
/// Numbers are formatted with <see cref="CultureInfo.InvariantCulture"/> (the engine formats invariantly under
/// invariant globalization; culture-specific digit grouping is out of scope and would belong to a number-format seam).</para>
///
/// <para><b>AOT-clean.</b> Pure span/char scanning + a <see cref="StringBuilder"/>; no reflection, no regex, no
/// <c>CultureInfo</c> lookups beyond the invariant singleton.</para>
/// </summary>
public static class MessageFormatter
{
    /// <summary>An argument bag: name → value. Lookup is ordinal/case-sensitive (placeholder names are identifiers the
    /// author controls). Backed by the caller's dictionary or built from a <c>(string, object)</c> tuple array.</summary>
    public readonly struct Args
    {
        private readonly IReadOnlyDictionary<string, object>? _map;
        private readonly (string Name, object Value)[]? _pairs;

        public Args(IReadOnlyDictionary<string, object> map) { _map = map; _pairs = null; }
        public Args((string Name, object Value)[] pairs) { _pairs = pairs; _map = null; }

        public static readonly Args Empty = new(System.Array.Empty<(string, object)>());

        /// <summary>Resolve <paramref name="name"/> to its bound value; false ⇒ no such argument (render visibly).</summary>
        public bool TryGet(string name, out object value)
        {
            if (_map is not null) return _map.TryGetValue(name, out value!);
            if (_pairs is not null)
                foreach (var (n, v) in _pairs)
                    if (string.Equals(n, name, System.StringComparison.Ordinal)) { value = v; return true; }
            value = null!;
            return false;
        }
    }

    /// <summary>Format <paramref name="template"/> against <paramref name="args"/> for plural selection in
    /// <paramref name="culture"/> (a BCP-47 name; drives <see cref="PluralRules"/>). Never throws on bad input: a
    /// missing argument keeps its <c>{name}</c> literal, a malformed block degrades to its raw text.</summary>
    public static string Format(string template, in Args args, string? culture)
    {
        if (string.IsNullOrEmpty(template)) return template;
        // Fast path: no '{' ⇒ no placeholders, return as-is (the overwhelming majority of UI strings).
        if (template.IndexOf('{') < 0) return template;

        var sb = new StringBuilder(template.Length + 16);
        int pos = 0;
        // Top-level has no active plural count, so '#' is a literal there (passing long.MinValue as "no count").
        AppendMessage(sb, template, ref pos, args, culture, hasCount: false, count: 0);
        return sb.ToString();
    }

    // ── Recursive-descent core ───────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Append the message starting at <paramref name="pos"/> until end-of-string or an unmatched <c>'}'</c>
    /// (which closes the enclosing block). <paramref name="hasCount"/>/<paramref name="count"/> carry the active plural
    /// count so a literal <c>#</c> in a plural branch substitutes it.</summary>
    private static void AppendMessage(StringBuilder sb, string s, ref int pos, in Args args, string? culture,
                                      bool hasCount, long count)
    {
        while (pos < s.Length)
        {
            char c = s[pos];
            if (c == '}')           // end of the enclosing branch body — let the caller consume the brace
                return;
            if (c == '{')
            {
                AppendArgument(sb, s, ref pos, args, culture);
                continue;
            }
            if (c == '#' && hasCount)   // the ICU count token, valid only inside a plural body
            {
                sb.Append(count.ToString(CultureInfo.InvariantCulture));
                pos++;
                continue;
            }
            if (c == '\'' && pos + 1 < s.Length)
            {
                // ICU apostrophe escaping (subset): '{' / '}' / '#' quoted as '{', '}', '#'; '' = a literal apostrophe.
                char n = s[pos + 1];
                if (n is '{' or '}' or '#' or '\'') { sb.Append(n); pos += 2; continue; }
            }
            sb.Append(c);
            pos++;
        }
    }

    /// <summary>Parse one <c>{ … }</c> argument at <paramref name="pos"/> (which is ON the <c>'{'</c>): simple
    /// interpolation, <c>plural</c>, or <c>select</c>. Advances <paramref name="pos"/> past the closing <c>'}'</c>.</summary>
    private static void AppendArgument(StringBuilder sb, string s, ref int pos, in Args args, string? culture)
    {
        int braceStart = pos;
        pos++;   // skip '{'
        SkipWs(s, ref pos);
        string name = ReadIdentifier(s, ref pos);
        SkipWs(s, ref pos);

        // Simple interpolation: {name}
        if (pos < s.Length && s[pos] == '}')
        {
            pos++;   // skip '}'
            AppendValue(sb, name, args);
            return;
        }

        // Typed form: {name, type, body}
        if (pos < s.Length && s[pos] == ',')
        {
            pos++;
            SkipWs(s, ref pos);
            string type = ReadIdentifier(s, ref pos);
            SkipWs(s, ref pos);
            if (pos < s.Length && s[pos] == ',')
            {
                pos++;
                if (type == "plural") { AppendPlural(sb, s, ref pos, name, args, culture); return; }
                if (type == "select") { AppendSelect(sb, s, ref pos, name, args, culture); return; }
            }
        }

        // Malformed / unsupported type: emit the raw span verbatim (degrade, never throw) and resync past a '}' if any.
        int close = FindMatchingBrace(s, braceStart);
        if (close < 0) { sb.Append(s, braceStart, s.Length - braceStart); pos = s.Length; return; }
        sb.Append(s, braceStart, close - braceStart + 1);
        pos = close + 1;
    }

    /// <summary>Parse a <c>plural</c> body: a sequence of <c>(selector message)</c> branches. An <c>=N</c> exact match
    /// wins over the category; <c>other</c> is the fallback. The chosen branch's body is recursively formatted with the
    /// count active (so <c>#</c> substitutes).</summary>
    private static void AppendPlural(StringBuilder sb, string s, ref int pos, string name, in Args args, string? culture)
    {
        long count = ResolveCount(name, args, out bool hasCount);
        PluralCategory category = hasCount ? PluralRules.Select(culture, count) : PluralCategory.Other;

        int exactStart = -1, exactEnd = -1;     // an =N branch matching the count (highest priority, order-independent)
        int catStart = -1, catEnd = -1;         // the first matching CATEGORY branch (one/few/…)
        int otherStart = -1, otherEnd = -1;     // the 'other' fallback span

        SkipWs(s, ref pos);
        while (pos < s.Length && s[pos] != '}')
        {
            // selector
            bool isExact = false; long exact = 0; string keyword = string.Empty;
            if (s[pos] == '=')
            {
                isExact = true; pos++;
                exact = ReadInteger(s, ref pos);
            }
            else keyword = ReadIdentifier(s, ref pos);
            SkipWs(s, ref pos);

            // body: '{' message '}'
            if (pos >= s.Length || s[pos] != '{') break;   // malformed body — stop
            int bodyStart = pos + 1;
            int bodyEnd = FindBranchBodyEnd(s, pos);        // index of the matching '}'
            if (bodyEnd < 0) break;

            // Record each branch into its priority bucket. An =N exact match ALWAYS wins over a category match (ICU
            // semantics), independent of the order the branches are written; among categories the first match wins;
            // 'other' is the terminal fallback. We collect all three and pick after the scan so order can't change the
            // result.
            if (isExact) { if (hasCount && exact == count && exactStart < 0) { exactStart = bodyStart; exactEnd = bodyEnd; } }
            else if (keyword == "other") { if (otherStart < 0) { otherStart = bodyStart; otherEnd = bodyEnd; } }
            else if (PluralRules.KeywordToCategory(keyword) is { } k && k == category && catStart < 0) { catStart = bodyStart; catEnd = bodyEnd; }

            pos = bodyEnd + 1;   // skip past this branch body's '}'
            SkipWs(s, ref pos);
        }
        if (pos < s.Length && s[pos] == '}') pos++;   // consume the plural block's closing '}'

        // Priority: exact =N  >  matching category  >  other.
        int chosenStart, chosenEnd;
        if (exactStart >= 0) { chosenStart = exactStart; chosenEnd = exactEnd; }
        else if (catStart >= 0) { chosenStart = catStart; chosenEnd = catEnd; }
        else { chosenStart = otherStart; chosenEnd = otherEnd; }

        if (chosenStart >= 0)
        {
            int p = chosenStart;
            // Slice so AppendMessage stops exactly at this branch body's '}' (the slice ends before it).
            AppendMessage(sb, s.Substring(0, chosenEnd), ref p, args, culture, hasCount, count);
        }
    }

    /// <summary>Parse a <c>select</c> body: <c>(keyword message)</c> branches; the keyword equal to the argument's
    /// string value wins, else <c>other</c>. Bodies are recursively formatted (no <c>#</c>).</summary>
    private static void AppendSelect(StringBuilder sb, string s, ref int pos, string name, in Args args, string? culture)
    {
        string selector = args.TryGet(name, out var v) ? ValueToString(v) : string.Empty;

        int chosenStart = -1, chosenEnd = -1, otherStart = -1, otherEnd = -1;
        bool matched = false;

        SkipWs(s, ref pos);
        while (pos < s.Length && s[pos] != '}')
        {
            string keyword = ReadIdentifier(s, ref pos);
            SkipWs(s, ref pos);
            if (pos >= s.Length || s[pos] != '{') break;
            int bodyStart = pos + 1;
            int bodyEnd = FindBranchBodyEnd(s, pos);
            if (bodyEnd < 0) break;

            if (keyword == "other") { otherStart = bodyStart; otherEnd = bodyEnd; }
            if (!matched && string.Equals(keyword, selector, System.StringComparison.Ordinal))
            { chosenStart = bodyStart; chosenEnd = bodyEnd; matched = true; }

            pos = bodyEnd + 1;
            SkipWs(s, ref pos);
        }
        if (pos < s.Length && s[pos] == '}') pos++;

        if (!matched) { chosenStart = otherStart; chosenEnd = otherEnd; }
        if (chosenStart >= 0)
        {
            int p = chosenStart;
            AppendMessage(sb, s.Substring(0, chosenEnd), ref p, args, culture, hasCount: false, count: 0);
        }
    }

    // ── Value formatting ─────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Append the bound value of <paramref name="name"/>, or a VISIBLE missing marker (<c>{name}</c>) when the
    /// argument is absent — never throws, never silently drops.</summary>
    private static void AppendValue(StringBuilder sb, string name, in Args args)
    {
        if (args.TryGet(name, out var v)) sb.Append(ValueToString(v));
        else { sb.Append('{').Append(name).Append('}'); }   // missing arg renders visibly
    }

    private static long ResolveCount(string name, in Args args, out bool has)
    {
        if (args.TryGet(name, out var v))
        {
            switch (v)
            {
                case int i: has = true; return i;
                case long l: has = true; return l;
                case short sh: has = true; return sh;
                case byte b: has = true; return b;
                case double d: has = true; return (long)d;
                case float f: has = true; return (long)f;
                default:
                    if (v is string str && long.TryParse(str, NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsed))
                    { has = true; return parsed; }
                    break;
            }
        }
        has = false;
        return 0;
    }

    /// <summary>Stringify an argument value invariantly (the engine formats with <see cref="CultureInfo.InvariantCulture"/>
    /// under invariant globalization).</summary>
    private static string ValueToString(object v) => v switch
    {
        string s => s,
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        null => string.Empty,
        _ => v.ToString() ?? string.Empty,
    };

    // ── Scanning helpers ─────────────────────────────────────────────────────────────────────────────────────────────

    private static void SkipWs(string s, ref int pos)
    {
        while (pos < s.Length && (s[pos] == ' ' || s[pos] == '\t' || s[pos] == '\r' || s[pos] == '\n')) pos++;
    }

    /// <summary>Read an identifier (placeholder name / type / select keyword): letters, digits, <c>_</c>, <c>-</c>,
    /// <c>.</c> (dotted names allowed). Stops at whitespace, comma, or brace.</summary>
    private static string ReadIdentifier(string s, ref int pos)
    {
        int start = pos;
        while (pos < s.Length)
        {
            char c = s[pos];
            if (c == ' ' || c == '\t' || c == '\r' || c == '\n' || c == ',' || c == '{' || c == '}') break;
            pos++;
        }
        return s.Substring(start, pos - start);
    }

    private static long ReadInteger(string s, ref int pos)
    {
        int start = pos;
        if (pos < s.Length && (s[pos] == '-' || s[pos] == '+')) pos++;
        while (pos < s.Length && s[pos] >= '0' && s[pos] <= '9') pos++;
        return long.TryParse(s.AsSpan(start, pos - start), NumberStyles.Integer, CultureInfo.InvariantCulture, out long n) ? n : 0;
    }

    /// <summary>Given <paramref name="open"/> at a <c>'{'</c>, return the index of its matching <c>'}'</c> accounting for
    /// nesting (so a branch body containing nested <c>{…}</c> is spanned correctly). -1 if unbalanced.</summary>
    private static int FindBranchBodyEnd(string s, int open) => FindMatchingBrace(s, open);

    private static int FindMatchingBrace(string s, int open)
    {
        int depth = 0;
        for (int i = open; i < s.Length; i++)
        {
            char c = s[i];
            if (c == '\'' && i + 1 < s.Length && s[i + 1] is '{' or '}' or '#' or '\'') { i++; continue; }   // skip escaped
            if (c == '{') depth++;
            else if (c == '}') { depth--; if (depth == 0) return i; }
        }
        return -1;
    }
}
