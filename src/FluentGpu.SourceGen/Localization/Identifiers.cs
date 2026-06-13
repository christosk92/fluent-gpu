using System.Collections.Generic;
using System.Text;

namespace FluentGpu.SourceGen.Localization
{
    /// <summary>
    /// Turns a JSON key segment (<c>app</c>, <c>nowPlaying</c>, <c>$comment.songs</c>) into a valid C# identifier.
    /// Class/const names are PascalCased (preserving internal camelCase humps: <c>nowPlaying → NowPlaying</c>); method
    /// parameter names are camelCased and keyword-escaped (<c>name → name</c>, <c>class → @class</c>). Non-identifier
    /// characters collapse to <c>_</c>, a leading digit is prefixed with <c>_</c>, and C# reserved words are escaped
    /// with <c>@</c>.
    /// </summary>
    internal static class Identifiers
    {
        private static readonly HashSet<string> Keywords = new(System.StringComparer.Ordinal)
        {
            "abstract","as","base","bool","break","byte","case","catch","char","checked","class","const","continue",
            "decimal","default","delegate","do","double","else","enum","event","explicit","extern","false","finally",
            "fixed","float","for","foreach","goto","if","implicit","in","int","interface","internal","is","lock","long",
            "namespace","new","null","object","operator","out","override","params","private","protected","public",
            "readonly","ref","return","sbyte","sealed","short","sizeof","stackalloc","static","string","struct","switch",
            "this","throw","true","try","typeof","uint","ulong","unchecked","unsafe","ushort","using","virtual","void",
            "volatile","while",
        };

        /// <summary>PascalCase a segment for a class/const name. Internal humps are preserved; the first letter is
        /// upper-cased. The result is always a legal identifier.</summary>
        public static string ToPascal(string segment)
        {
            string clean = Sanitize(segment);
            if (clean.Length == 0) return "_";
            char first = clean[0];
            if (char.IsUpper(first)) return EscapeIfKeyword(clean);
            // Upper-case only the first char (keep nowPlaying → NowPlaying).
            string pascal = char.ToUpperInvariant(first) + clean.Substring(1);
            return EscapeIfKeyword(pascal);
        }

        /// <summary>camelCase + keyword-escape a placeholder name for a method parameter.</summary>
        public static string ToParam(string name)
        {
            string clean = Sanitize(name);
            if (clean.Length == 0) return "_arg";
            string camel = char.ToLowerInvariant(clean[0]) + clean.Substring(1);
            // Parameters that are keywords need @-escaping to remain usable.
            return Keywords.Contains(camel) ? "@" + camel : camel;
        }

        // Replace illegal chars with '_', and ensure a non-digit start.
        private static string Sanitize(string s)
        {
            var sb = new StringBuilder(s.Length);
            foreach (char c in s)
            {
                if ((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '_')
                    sb.Append(c);
                else
                    sb.Append('_');
            }
            if (sb.Length == 0) return string.Empty;
            if (sb[0] >= '0' && sb[0] <= '9') sb.Insert(0, '_');
            return sb.ToString();
        }

        private static string EscapeIfKeyword(string id) => Keywords.Contains(id) ? "@" + id : id;
    }
}
