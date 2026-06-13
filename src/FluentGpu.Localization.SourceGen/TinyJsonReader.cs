using System.Collections.Generic;
using System.Text;

namespace FluentGpu.Localization.SourceGen
{
    /// <summary>
    /// A tiny, dependency-free JSON scanner that flattens a localization-resource object into an ordered list of
    /// (dottedKey, value) string pairs. A netstandard2.0 generator host cannot reliably load System.Text.Json, so this
    /// hand-rolls exactly the subset the loc JSON uses: a root object, nested objects, and string leaf values. Numbers,
    /// booleans, null and arrays are not part of the loc-resource shape; if one is encountered the scanner records a
    /// soft failure (handled by the caller as a diagnostic) and skips the value rather than crashing.
    /// </summary>
    /// <remarks>
    /// Meta keys whose final segment starts with <c>$</c> (e.g. <c>$culture</c>, <c>$comment</c>, <c>$comment.songs</c>)
    /// are dropped — they are author metadata, not user-facing strings. Source order is preserved so the emitted code is
    /// deterministic and byte-identical across builds.
    /// </remarks>
    internal sealed class TinyJsonReader
    {
        private readonly string _s;
        private int _i;

        /// <summary>Set when the input is malformed / contains an unsupported value kind. The caller turns this into a
        /// build diagnostic and emits an empty <c>Strings</c> class (so the build does not fail).</summary>
        public bool HadError { get; private set; }

        /// <summary>A short human-readable description of the first error (for the diagnostic message).</summary>
        public string? ErrorMessage { get; private set; }

        private TinyJsonReader(string s)
        {
            _s = s;
            _i = 0;
        }

        /// <summary>Parse <paramref name="json"/> into an ordered list of flattened (dottedKey, value) pairs. On any
        /// structural error the returned list holds whatever was parsed before the failure and <see cref="HadError"/>
        /// is set.</summary>
        public static List<KeyValuePair<string, string>> Parse(string json, out bool hadError, out string? error)
        {
            var reader = new TinyJsonReader(json ?? string.Empty);
            var result = new List<KeyValuePair<string, string>>();
            reader.SkipWs();
            if (!reader.TryConsume('{'))
            {
                hadError = true;
                error = "root is not a JSON object";
                return result;
            }
            reader.ParseObjectBody(prefix: string.Empty, result);
            hadError = reader.HadError;
            error = reader.ErrorMessage;
            return result;
        }

        // Parses the body of an object: zero or more "key": value pairs separated by commas, up to the closing '}'.
        // The opening '{' has already been consumed.
        private void ParseObjectBody(string prefix, List<KeyValuePair<string, string>> sink)
        {
            SkipWs();
            if (TryConsume('}')) return;   // empty object {}

            while (true)
            {
                SkipWs();
                if (!TryReadString(out string key))
                {
                    Fail("expected a string key");
                    return;
                }
                SkipWs();
                if (!TryConsume(':'))
                {
                    Fail("expected ':' after key");
                    return;
                }
                SkipWs();

                // A leaf key segment starting with '$' is metadata (e.g. $culture, $comment) and is dropped along with
                // its value (whatever kind it is).
                bool isMeta = key.Length > 0 && key[0] == '$';
                string dotted = prefix.Length == 0 ? key : prefix + "." + key;

                char c = Peek();
                if (c == '{')
                {
                    _i++;   // consume '{'
                    // Recurse. A meta OBJECT (unlikely) is still walked but its leaves carry the $-prefixed segment, so
                    // they are filtered at their own leaf below; simplest is to recurse with the prefix regardless.
                    ParseObjectBody(isMeta ? prefix : dotted, sink);
                }
                else if (c == '"')
                {
                    if (!TryReadString(out string value))
                    {
                        Fail("malformed string value");
                        return;
                    }
                    if (!isMeta) sink.Add(new KeyValuePair<string, string>(dotted, value));
                }
                else
                {
                    // Unsupported value kind for loc resources (number/bool/null/array). Skip it gracefully.
                    Fail("unsupported value kind (only objects and strings are supported)");
                    SkipValue();
                }

                SkipWs();
                if (TryConsume(',')) continue;
                if (TryConsume('}')) return;
                Fail("expected ',' or '}'");
                return;
            }
        }

        // Best-effort skip of a non-object/non-string value so a single bad value doesn't desync the whole parse.
        private void SkipValue()
        {
            int depth = 0;
            while (_i < _s.Length)
            {
                char c = _s[_i];
                if (c == '"') { TryReadString(out _); continue; }
                if (c == '{' || c == '[') { depth++; _i++; continue; }
                if (c == '}' || c == ']') { if (depth == 0) return; depth--; _i++; continue; }
                if (c == ',' && depth == 0) return;
                _i++;
            }
        }

        private bool TryReadString(out string value)
        {
            value = string.Empty;
            if (Peek() != '"') return false;
            _i++;   // opening quote
            var sb = new StringBuilder();
            while (_i < _s.Length)
            {
                char c = _s[_i++];
                if (c == '"') { value = sb.ToString(); return true; }
                if (c == '\\')
                {
                    if (_i >= _s.Length) return false;
                    char e = _s[_i++];
                    switch (e)
                    {
                        case '"': sb.Append('"'); break;
                        case '\\': sb.Append('\\'); break;
                        case '/': sb.Append('/'); break;
                        case 'b': sb.Append('\b'); break;
                        case 'f': sb.Append('\f'); break;
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        case 'u':
                            if (_i + 4 <= _s.Length && TryHex4(_s, _i, out int cp))
                            {
                                sb.Append((char)cp);
                                _i += 4;
                            }
                            else
                            {
                                sb.Append('?');
                            }
                            break;
                        default: sb.Append(e); break;
                    }
                }
                else
                {
                    sb.Append(c);
                }
            }
            return false;   // unterminated
        }

        private static bool TryHex4(string s, int at, out int value)
        {
            value = 0;
            for (int k = 0; k < 4; k++)
            {
                char c = s[at + k];
                int d;
                if (c >= '0' && c <= '9') d = c - '0';
                else if (c >= 'a' && c <= 'f') d = c - 'a' + 10;
                else if (c >= 'A' && c <= 'F') d = c - 'A' + 10;
                else return false;
                value = (value << 4) | d;
            }
            return true;
        }

        private char Peek() => _i < _s.Length ? _s[_i] : '\0';

        private bool TryConsume(char c)
        {
            if (_i < _s.Length && _s[_i] == c) { _i++; return true; }
            return false;
        }

        private void SkipWs()
        {
            while (_i < _s.Length)
            {
                char c = _s[_i];
                if (c == ' ' || c == '\t' || c == '\r' || c == '\n' || c == '﻿') { _i++; continue; }
                break;
            }
        }

        private void Fail(string message)
        {
            if (!HadError)
            {
                HadError = true;
                ErrorMessage = message;
            }
        }
    }
}
