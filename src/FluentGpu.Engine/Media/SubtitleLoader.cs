using System;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace FluentGpu.Media;

/// <summary>AOT-safe external WebVTT/SRT loader and parser. Parsing is cold; playback consumes the resulting
/// <see cref="CueTrack"/> without reparsing or allocating on the video pump.</summary>
public static class SubtitleLoader
{
    public static async ValueTask<CueTrack> LoadAsync(HttpClient client, SubtitleSource source,
        NetworkOptions? network, CancellationToken cancellationToken)
    {
        string text;
        if (Uri.TryCreate(source.Uri, UriKind.Absolute, out Uri? uri) && uri.Scheme is "http" or "https")
        {
            var model = new NetworkRequest { Uri = source.Uri };
            model = network?.OnRequest?.Invoke(model) ?? model;
            using var request = new HttpRequestMessage(HttpMethod.Get, model.Uri);
            for (int i = 0; i < model.Headers.Count; i++)
            {
                var (name, value) = model.Headers[i];
                request.Headers.TryAddWithoutValidation(name, value);
            }
            using HttpResponseMessage response = await client.SendAsync(request,
                HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }
        else text = await File.ReadAllTextAsync(source.Uri, cancellationToken).ConfigureAwait(false);

        return LooksLikeWebVtt(source.Uri, text) ? ParseWebVtt(text) : ParseSrt(text);
    }

    public static CueTrack ParseWebVtt(string text) => Parse(text, webVtt: true);
    public static CueTrack ParseSrt(string text) => Parse(text, webVtt: false);

    private static CueTrack Parse(string text, bool webVtt)
    {
        var track = new CueTrack();
        string normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        string[] blocks = normalized.Split("\n\n", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        for (int b = 0; b < blocks.Length; b++)
        {
            string[] lines = blocks[b].Split('\n', StringSplitOptions.TrimEntries);
            int timing = -1;
            for (int i = 0; i < lines.Length; i++)
                if (lines[i].Contains("-->", StringComparison.Ordinal)) { timing = i; break; }
            if (timing < 0) continue; // WEBVTT header, NOTE, STYLE, REGION, malformed block

            int arrow = lines[timing].IndexOf("-->", StringComparison.Ordinal);
            string left = lines[timing][..arrow].Trim();
            string right = lines[timing][(arrow + 3)..].Trim();
            int setting = right.IndexOf(' ');
            if (setting >= 0) right = right[..setting];
            if (!TryTimestamp(left, webVtt, out TimeSpan start) || !TryTimestamp(right, webVtt, out TimeSpan end) || end <= start)
                continue;

            var body = new StringBuilder();
            for (int i = timing + 1; i < lines.Length; i++)
            {
                if (body.Length > 0) body.Append('\n');
                body.Append(StripMarkup(lines[i]));
            }
            if (body.Length > 0) track.Add(new TimedCue(start, end, WebUtility.HtmlDecode(body.ToString()), CueStyle.Default, null));
        }
        return track;
    }

    private static bool TryTimestamp(string value, bool webVtt, out TimeSpan time)
    {
        value = value.Trim().Replace(',', '.');
        string[] parts = value.Split(':');
        if (parts.Length is < 2 or > 3) { time = default; return false; }
        int h = 0, m;
        string seconds;
        if (parts.Length == 3)
        {
            if (!int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out h) ||
                !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out m))
            { time = default; return false; }
            seconds = parts[2];
        }
        else
        {
            if (!webVtt || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out m))
            { time = default; return false; }
            seconds = parts[1];
        }
        if (!double.TryParse(seconds, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out double s))
        { time = default; return false; }
        time = TimeSpan.FromHours(h) + TimeSpan.FromMinutes(m) + TimeSpan.FromSeconds(s);
        return true;
    }

    private static string StripMarkup(string line)
    {
        int first = line.IndexOf('<');
        if (first < 0) return line;
        var result = new StringBuilder(line.Length);
        bool tag = false;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (c == '<') { tag = true; continue; }
            if (c == '>') { tag = false; continue; }
            if (!tag) result.Append(c);
        }
        return result.ToString();
    }

    private static bool LooksLikeWebVtt(string uri, string text)
        => uri.EndsWith(".vtt", StringComparison.OrdinalIgnoreCase) ||
           text.AsSpan().TrimStart().StartsWith("WEBVTT", StringComparison.Ordinal);
}
