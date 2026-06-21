using System;
using System.Collections.Generic;
using System.Text;
using FluentGpu.Dsl;
using FluentGpu.Foundation;

namespace Wavee;

// Renders a description that may be an HTML FRAGMENT — Spotify playlist/album blurbs carry <a href="spotify:…"> links to
// artists/playlists and <b> bold — as ONE wrapped rich-text paragraph (engine SpanTextEl, which shapes all spans as a
// single flow). Anchors become accent-colored HYPERLINKS that navigate via onNavUri; <b>/<strong> → bold; HTML
// entities are decoded; unknown tags are dropped (their text kept). The shaper has no italic axis, so <i>/<em> render
// upright. Plain text (no markup) → a single span, identical to a TextEl. Colors/size come from the caller so it
// matches whatever caption style the host uses.
public static class RichText
{
    public static Element Of(string? html, float size, ColorF color, ColorF linkColor, float width, int maxLines, Action<string>? onNavUri = null)
    {
        if (string.IsNullOrWhiteSpace(html)) return new BoxEl();
        var spans = Parse(html!, linkColor, onNavUri);
        if (spans.Count == 0) return new BoxEl();
        return new SpanTextEl(spans.ToArray())
        {
            Size = size, Color = color, LineHeight = size <= 12f ? 16f : float.NaN,
            Width = width, MaxLines = maxLines, Wrap = TextWrap.Wrap, Trim = TextTrim.CharacterEllipsis,
        };
    }

    /// <summary>A Spotify uri → the app's route key (matches ContentHost): playlist → "pl:…", album → "album:…",
    /// artist → "artist:…", saved-tracks → "liked". Null when it's not a navigable uri.</summary>
    public static string? RouteForUri(string? uri)
    {
        if (string.IsNullOrEmpty(uri)) return null;
        if (uri == "spotify:collection:tracks") return "liked";
        if (uri.Contains(":playlist:", StringComparison.Ordinal)) return "pl:" + uri;
        if (uri.Contains(":album:", StringComparison.Ordinal)) return "album:" + uri;
        if (uri.Contains(":artist:", StringComparison.Ordinal)) return "artist:" + uri;
        return null;
    }

    static List<TextSpan> Parse(string s, ColorF linkColor, Action<string>? onNavUri)
    {
        var spans = new List<TextSpan>(4);
        var buf = new StringBuilder(s.Length);
        int bold = 0;
        string? href = null;

        void Flush()
        {
            if (buf.Length == 0) return;
            string t = buf.ToString();
            buf.Clear();
            ushort w = (ushort)(bold > 0 ? 700 : 0);
            if (href is { } h && onNavUri is not null && RouteForUri(h) is { } key)
                spans.Add(new TextSpan(t, Weight: w, Color: linkColor, OnClick: () => onNavUri(key)));
            else if (href is not null)
                spans.Add(new TextSpan(t, Weight: w, Color: linkColor));   // a reference we can't route → styled, not clickable
            else
                spans.Add(new TextSpan(t, Weight: w));
        }

        int i = 0;
        while (i < s.Length)
        {
            if (s[i] == '<')
            {
                int gt = s.IndexOf('>', i);
                if (gt < 0) { buf.Append(s[i]); i++; continue; }   // stray '<' — keep it as text
                string tag = s.Substring(i + 1, gt - i - 1).Trim();
                Flush();
                string lower = tag.ToLowerInvariant();
                if (lower == "/a") href = null;
                else if (lower == "a" || lower.StartsWith("a ", StringComparison.Ordinal)) href = ExtractHref(tag);
                else if (lower is "b" or "strong") bold++;
                else if (lower is "/b" or "/strong") bold = Math.Max(0, bold - 1);
                // every other tag (br, i, em, span, p, …) is dropped; its text content is preserved
                i = gt + 1;
            }
            else
            {
                int lt = s.IndexOf('<', i);
                if (lt < 0) lt = s.Length;
                Decode(s, i, lt, buf);
                i = lt;
            }
        }
        Flush();
        return spans;
    }

    // href value from an anchor tag body ("a href=\"spotify:…\"" or unquoted "a href=spotify:…"). Null if absent.
    static string? ExtractHref(string tag)
    {
        int h = tag.IndexOf("href", StringComparison.OrdinalIgnoreCase);
        if (h < 0) return null;
        int eq = tag.IndexOf('=', h);
        if (eq < 0) return null;
        int v = eq + 1;
        while (v < tag.Length && (tag[v] == ' ' || tag[v] == '"' || tag[v] == '\'')) v++;
        int end = v;
        while (end < tag.Length && tag[end] is not ('"' or '\'' or ' ')) end++;
        return end > v ? tag.Substring(v, end - v) : null;
    }

    // Decode the common HTML entities in s[start,end) into buf (leave unknown ones literal).
    static void Decode(string s, int start, int end, StringBuilder buf)
    {
        int i = start;
        while (i < end)
        {
            char c = s[i];
            if (c == '&')
            {
                int sc = s.IndexOf(';', i);
                if (sc > i && sc < end && sc - i <= 9)
                {
                    string ent = s.Substring(i + 1, sc - i - 1);
                    string? rep = ent switch
                    {
                        "amp" => "&", "lt" => "<", "gt" => ">", "quot" => "\"", "apos" or "#39" => "'", "nbsp" => " ",
                        _ when ent.Length > 1 && ent[0] == '#' && int.TryParse(ent.AsSpan(1), out int cp) && cp > 0 && cp < 0xD800 => char.ConvertFromUtf32(cp),
                        _ => null,
                    };
                    if (rep is not null) { buf.Append(rep); i = sc + 1; continue; }
                }
            }
            buf.Append(c);
            i++;
        }
    }
}
