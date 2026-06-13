using System.Collections.Generic;

namespace FluentGpu.Localization.SourceGen
{
    /// <summary>
    /// Collects the distinct argument names an ICU message template references, so the generator can emit a typed format
    /// method per parameterized key. This is intentionally NOT a full ICU parser — it does a single linear scan that
    /// captures every <c>{ident</c> placeholder, including the operand of a <c>{count, plural, ...}</c> /
    /// <c>{g, select, ...}</c> construct and any <c>{name}</c> nested inside branch bodies. First-seen order is preserved
    /// so the emitted parameter list is deterministic.
    /// </summary>
    /// <remarks>
    /// Matches the runtime <c>MessageFormatter</c> surface: a placeholder is an open brace immediately followed by an
    /// identifier (letters/digits/underscore). The character after the identifier disambiguates a simple substitution
    /// (<c>}</c>) from a plural/select construct (<c>,</c>) — both contribute the leading identifier as an argument; the
    /// scan then continues into the construct body, so nested placeholders inside branches are also collected. The ICU
    /// <c>#</c> count token is not an argument name and is ignored.
    /// </remarks>
    internal static class IcuPlaceholders
    {
        /// <summary>Return the distinct argument names referenced by <paramref name="template"/>, in first-seen order.
        /// An empty list means the template has no placeholders (a plain string key — no typed method is emitted).</summary>
        public static List<string> Collect(string template)
        {
            var names = new List<string>();
            var seen = new HashSet<string>();
            if (string.IsNullOrEmpty(template)) return names;

            int n = template.Length;
            for (int i = 0; i < n; i++)
            {
                char c = template[i];
                if (c == '\'')
                {
                    // ICU apostrophe quoting: '' is a literal apostrophe; '{' quotes a literal brace run until the next '.
                    // We approximate by skipping a quoted run so braces inside it are not treated as placeholders.
                    if (i + 1 < n && template[i + 1] == '\'') { i++; continue; }
                    int j = i + 1;
                    while (j < n && template[j] != '\'') j++;
                    i = j;
                    continue;
                }
                if (c != '{') continue;

                int k = i + 1;
                while (k < n && (template[k] == ' ' || template[k] == '\t')) k++;
                int start = k;
                while (k < n && IsIdentChar(template[k])) k++;
                if (k == start) continue;   // "{ {" or "{#" etc. — not a named placeholder

                // ICU disambiguation: an open brace introduces an ARGUMENT only when the identifier is immediately
                // followed (after optional whitespace) by '}' (simple substitution {name}) or ',' (a plural/select
                // construct {count, plural, ...}). A '{' that opens a branch BODY — e.g. the "{No files}" after a "=0"
                // category, or "{He invited {name}}" after a "male" select key — is NOT a placeholder: its leading word
                // is literal body text ("No", "He", ...). Such bodies are entered by the scan continuing character by
                // character, so any genuinely nested {name} inside them is still collected on a later iteration.
                int after = k;
                while (after < n && (template[after] == ' ' || template[after] == '\t')) after++;
                if (after >= n || (template[after] != '}' && template[after] != ',')) continue;

                string name = template.Substring(start, k - start);
                if (seen.Add(name)) names.Add(name);
                // Continue scanning from here; any nested {name} inside a plural/select body is picked up by the loop.
            }
            return names;
        }

        private static bool IsIdentChar(char c) =>
            (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '_';
    }
}
