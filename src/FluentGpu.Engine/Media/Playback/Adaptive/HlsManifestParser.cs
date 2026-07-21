using System;
using System.Collections.Generic;
using System.Globalization;

namespace FluentGpu.Media.Adaptive;

/// <summary>AOT-safe RFC 8216 / Low-Latency HLS normalizer. Master playlists become selectable track groups;
/// media playlists become an ordered timeline including byte ranges, discontinuities, gaps, parts and live state.</summary>
public static class HlsManifestParser
{
    public static AdaptiveManifest Parse(string text, Uri source)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        string[] lines = Lines(text);
        RequireHeader(lines);
        bool master = false;
        for (int i = 0; i < lines.Length; i++)
            if (lines[i].StartsWith("#EXT-X-STREAM-INF:", StringComparison.Ordinal) ||
                lines[i].StartsWith("#EXT-X-MEDIA:", StringComparison.Ordinal)) { master = true; break; }
        return master ? ParseMaster(lines, source) : ParseMedia(lines, source, null);
    }

    /// <summary>Parse a child media playlist while retaining the master representation's quality identity.</summary>
    public static AdaptiveManifest ParseMedia(string text, Uri source, QualityVariant? quality)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        string[] lines = Lines(text);
        RequireHeader(lines);
        return ParseMedia(lines, source, quality);
    }

    private static AdaptiveManifest ParseMaster(string[] lines, Uri source)
    {
        var video = new List<AdaptiveRepresentation>();
        var audioGroups = new Dictionary<string, List<AdaptiveRepresentation>>(StringComparer.Ordinal);
        var textGroups = new Dictionary<string, List<AdaptiveRepresentation>>(StringComparer.Ordinal);
        var metadata = new List<(string Id, AdaptiveTrackType Type, string? Language, TrackRole Role, bool Default, bool Forced, List<AdaptiveRepresentation> Reps)>();

        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];
            if (line.StartsWith("#EXT-X-MEDIA:", StringComparison.Ordinal))
            {
                var a = Attributes(line.AsSpan(13));
                string typeText = Get(a, "TYPE") ?? "";
                AdaptiveTrackType type = typeText.Equals("SUBTITLES", StringComparison.OrdinalIgnoreCase) ||
                    typeText.Equals("CLOSED-CAPTIONS", StringComparison.OrdinalIgnoreCase) ? AdaptiveTrackType.Text : AdaptiveTrackType.Audio;
                string group = Get(a, "GROUP-ID") ?? (type == AdaptiveTrackType.Audio ? "audio" : "text");
                string renditionId = group + ":" + (Get(a, "NAME") ?? metadata.Count.ToString(CultureInfo.InvariantCulture));
                string? uriText = Get(a, "URI");
                string codecs = type == AdaptiveTrackType.Audio ? "mp4a" : "wvtt";
                var q = new QualityVariant(renditionId, 0, SizeI.Zero, 0, ContentType(type, codecs), HdrFormat.Sdr, Get(a, "NAME"));
                var rep = new AdaptiveRepresentation(q, null, Array.Empty<AdaptiveSegment>(),
                    uriText is null ? null : AdaptiveUri.Resolve(source, uriText).AbsoluteUri);
                if (type == AdaptiveTrackType.Audio) GetList(audioGroups, group).Add(rep); else GetList(textGroups, group).Add(rep);
                metadata.Add((renditionId, type, Get(a, "LANGUAGE"), Role(Get(a, "CHARACTERISTICS"), type), Yes(a, "DEFAULT"), Yes(a, "FORCED"), new List<AdaptiveRepresentation> { rep }));
                continue;
            }

            if (!line.StartsWith("#EXT-X-STREAM-INF:", StringComparison.Ordinal)) continue;
            var attrs = Attributes(line.AsSpan(18));
            string? child = NextUri(lines, ref i);
            if (child is null) throw new FormatException("HLS EXT-X-STREAM-INF has no following playlist URI.");
            string codecsValue = Get(attrs, "CODECS") ?? "";
            (int w, int h) = Resolution(Get(attrs, "RESOLUTION"));
            int bandwidth = Integer(Get(attrs, "AVERAGE-BANDWIDTH"), Integer(Get(attrs, "BANDWIDTH")));
            double frameRate = Double(Get(attrs, "FRAME-RATE"));
            string id = "v" + video.Count.ToString(CultureInfo.InvariantCulture);
            HdrFormat hdr = Hdr(Get(attrs, "VIDEO-RANGE"), codecsValue);
            var qv = new QualityVariant(id, bandwidth, new SizeI(w, h), frameRate,
                ContentType(AdaptiveTrackType.Video, codecsValue), hdr, Label(w, h, bandwidth, hdr));
            video.Add(new AdaptiveRepresentation(qv, null, Array.Empty<AdaptiveSegment>(),
                AdaptiveUri.Resolve(source, child).AbsoluteUri, null, default, Get(attrs, "AUDIO"), Get(attrs, "SUBTITLES")));
        }

        var groups = new List<AdaptiveTrackGroup>();
        if (video.Count > 0) groups.Add(new AdaptiveTrackGroup("video", AdaptiveTrackType.Video, null, TrackRole.Main, video, true));
        foreach (var m in metadata)
            groups.Add(new AdaptiveTrackGroup(m.Id, m.Type, m.Language, m.Role, m.Reps, m.Default, m.Forced));
        if (groups.Count == 0) throw new FormatException("HLS master contains no playable variants or renditions.");
        return new AdaptiveManifest(source, AdaptiveManifestKind.Hls, false, false, null, TimeSpan.Zero,
            TimeSpan.Zero, TimeSpan.Zero, null, groups);
    }

    private static AdaptiveManifest ParseMedia(string[] lines, Uri source, QualityVariant? quality)
    {
        bool endList = false, independent = false, lowLatency = false, gap = false;
        long mediaSequence = 0, number = 0, rangeOffset = -1, nextRangeOffset = -1;
        long rangeLength = -1;
        int discontinuity = 0;
        TimeSpan cursor = TimeSpan.Zero, target = TimeSpan.Zero, partTarget = TimeSpan.Zero;
        double pendingDuration = -1;
        DateTimeOffset? programTime = null;
        Uri? map = null;
        string? drm = null;
        var initData = ReadOnlyMemory<byte>.Empty;
        var segments = new List<AdaptiveSegment>();

        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];
            if (line.Length == 0) continue;
            if (line[0] != '#')
            {
                double seconds = pendingDuration >= 0 ? pendingDuration : target.TotalSeconds;
                long offset = rangeLength >= 0 ? (rangeOffset >= 0 ? rangeOffset : Math.Max(0, nextRangeOffset)) : -1;
                segments.Add(new AdaptiveSegment(AdaptiveUri.Resolve(source, line), number++, cursor,
                    TimeSpan.FromSeconds(Math.Max(0, seconds)), false, offset, rangeLength, discontinuity, programTime, gap));
                if (rangeLength >= 0) nextRangeOffset = offset + rangeLength;
                cursor += TimeSpan.FromSeconds(Math.Max(0, seconds));
                if (programTime is { } pdt) programTime = pdt + TimeSpan.FromSeconds(Math.Max(0, seconds));
                pendingDuration = -1; rangeLength = -1; rangeOffset = -1; gap = false;
                continue;
            }

            if (line.StartsWith("#EXT-X-MEDIA-SEQUENCE:", StringComparison.Ordinal))
            { mediaSequence = Long(line.AsSpan(22)); number = mediaSequence; }
            else if (line.StartsWith("#EXT-X-TARGETDURATION:", StringComparison.Ordinal)) target = TimeSpan.FromSeconds(Double(line.AsSpan(22)));
            else if (line.StartsWith("#EXT-X-PART-INF:", StringComparison.Ordinal))
            { partTarget = TimeSpan.FromSeconds(Double(Get(Attributes(line.AsSpan(16)), "PART-TARGET"))); lowLatency = true; }
            else if (line.StartsWith("#EXT-X-SERVER-CONTROL:", StringComparison.Ordinal)) lowLatency = true;
            else if (line.StartsWith("#EXTINF:", StringComparison.Ordinal)) pendingDuration = DoubleBeforeComma(line.AsSpan(8));
            else if (line.StartsWith("#EXT-X-BYTERANGE:", StringComparison.Ordinal))
            { (rangeLength, rangeOffset) = ByteRange(line.AsSpan(17)); }
            else if (line.StartsWith("#EXT-X-MAP:", StringComparison.Ordinal))
            { string? u = Get(Attributes(line.AsSpan(11)), "URI"); if (u is not null) map = AdaptiveUri.Resolve(source, u); }
            else if (line.StartsWith("#EXT-X-PART:", StringComparison.Ordinal))
            {
                var a = Attributes(line.AsSpan(12));
                string? u = Get(a, "URI");
                double d = Double(Get(a, "DURATION"));
                if (u is not null && d > 0)
                {
                    (long len, long off) = ByteRange((Get(a, "BYTERANGE") ?? "").AsSpan());
                    segments.Add(new AdaptiveSegment(AdaptiveUri.Resolve(source, u), number, cursor,
                        TimeSpan.FromSeconds(d), true, off, len, discontinuity, programTime, false));
                    cursor += TimeSpan.FromSeconds(d); lowLatency = true;
                }
            }
            else if (line == "#EXT-X-DISCONTINUITY") discontinuity++;
            else if (line == "#EXT-X-GAP") gap = true;
            else if (line == "#EXT-X-ENDLIST") endList = true;
            else if (line == "#EXT-X-INDEPENDENT-SEGMENTS") independent = true;
            else if (line.StartsWith("#EXT-X-PROGRAM-DATE-TIME:", StringComparison.Ordinal) &&
                DateTimeOffset.TryParse(line.AsSpan(25), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dt)) programTime = dt;
            else if (line.StartsWith("#EXT-X-KEY:", StringComparison.Ordinal))
            {
                var a = Attributes(line.AsSpan(11));
                string method = Get(a, "METHOD") ?? "";
                string keyFormat = Get(a, "KEYFORMAT") ?? "identity";
                if (method.Equals("SAMPLE-AES", StringComparison.OrdinalIgnoreCase) || method.Equals("SAMPLE-AES-CTR", StringComparison.OrdinalIgnoreCase))
                    drm = keyFormat.Contains("playready", StringComparison.OrdinalIgnoreCase) ? "playready" : keyFormat;
                string? uri = Get(a, "URI");
                if (uri is not null && uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) initData = DecodeDataUri(uri);
            }
        }

        if (segments.Count == 0) throw new FormatException("HLS media playlist contains no segments or parts.");
        quality ??= new QualityVariant("media", 0, SizeI.Zero, 0,
            new MediaContentType(Container.Hls, CodecId.H264, CodecId.Aac));
        var rep = new AdaptiveRepresentation(quality, map, segments, null, drm, initData);
        TimeSpan duration = cursor;
        bool live = !endList;
        TimeSpan dvr = live ? duration : TimeSpan.Zero;
        TimeSpan delay = lowLatency ? TimeSpan.FromTicks(Math.Max(partTarget.Ticks * 3, target.Ticks)) : TimeSpan.FromTicks(target.Ticks * 3);
        var group = new AdaptiveTrackGroup("media", quality.Codec.Audio != CodecId.None && quality.Codec.Video == CodecId.None ? AdaptiveTrackType.Audio : AdaptiveTrackType.Video,
            null, TrackRole.Main, new[] { rep }, true);
        _ = independent; // retained in parse validation; scheduling does not require it.
        _ = mediaSequence;
        return new AdaptiveManifest(source, AdaptiveManifestKind.Hls, live, lowLatency, live ? null : duration,
            target, dvr, delay, null, new[] { group });
    }

    private static Dictionary<string, string> Attributes(ReadOnlySpan<char> text)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        int i = 0;
        while (i < text.Length)
        {
            while (i < text.Length && (text[i] == ',' || char.IsWhiteSpace(text[i]))) i++;
            int eq = text[i..].IndexOf('=');
            if (eq < 0) break;
            eq += i;
            string key = text[i..eq].Trim().ToString();
            i = eq + 1;
            string value;
            if (i < text.Length && text[i] == '"')
            {
                int start = ++i;
                while (i < text.Length && text[i] != '"') i++;
                value = text[start..i].ToString();
                if (i < text.Length) i++;
            }
            else
            {
                int comma = text[i..].IndexOf(',');
                if (comma < 0) { value = text[i..].Trim().ToString(); i = text.Length; }
                else { value = text.Slice(i, comma).Trim().ToString(); i += comma + 1; }
            }
            if (key.Length > 0) result[key] = value;
        }
        return result;
    }

    private static string[] Lines(string text) => text.Replace("\r", "", StringComparison.Ordinal).Split('\n', StringSplitOptions.TrimEntries);
    private static void RequireHeader(string[] lines)
    { if (lines.Length == 0 || !string.Equals(lines[0], "#EXTM3U", StringComparison.Ordinal)) throw new FormatException("HLS playlist is missing #EXTM3U."); }
    private static string? NextUri(string[] lines, ref int i)
    { while (++i < lines.Length) if (lines[i].Length > 0 && lines[i][0] != '#') return lines[i]; return null; }
    private static string? Get(Dictionary<string, string> a, string key) => a.TryGetValue(key, out string? v) ? v : null;
    private static List<AdaptiveRepresentation> GetList(Dictionary<string, List<AdaptiveRepresentation>> d, string key)
    { if (!d.TryGetValue(key, out var v)) d[key] = v = new List<AdaptiveRepresentation>(); return v; }
    private static bool Yes(Dictionary<string, string> a, string key) => string.Equals(Get(a, key), "YES", StringComparison.OrdinalIgnoreCase);
    private static int Integer(string? s, int fallback = 0) => int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : fallback;
    private static long Long(ReadOnlySpan<char> s) => long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out long v) ? v : 0;
    private static double Double(string? s) => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : 0;
    private static double Double(ReadOnlySpan<char> s) => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : 0;
    private static double DoubleBeforeComma(ReadOnlySpan<char> s) { int c = s.IndexOf(','); return Double(c < 0 ? s : s[..c]); }
    private static (int W, int H) Resolution(string? value)
    { if (value is null) return default; int x = value.IndexOf('x'); return x > 0 ? (Integer(value[..x]), Integer(value[(x + 1)..])) : default; }
    private static (long Length, long Offset) ByteRange(ReadOnlySpan<char> value)
    {
        if (value.IsEmpty) return (-1, -1);
        int at = value.IndexOf('@');
        long len = Long(at < 0 ? value : value[..at]);
        long off = at < 0 ? -1 : Long(value[(at + 1)..]);
        return (len > 0 ? len : -1, off);
    }
    private static string Label(int w, int h, int bandwidth, HdrFormat hdr)
    { string size = h > 0 ? h.ToString(CultureInfo.InvariantCulture) + "p" : (bandwidth / 1000).ToString(CultureInfo.InvariantCulture) + " kbps"; return hdr == HdrFormat.Sdr ? size : size + " " + hdr; }
    private static TrackRole Role(string? characteristics, AdaptiveTrackType type)
    {
        string c = characteristics ?? "";
        if (c.Contains("describes-video", StringComparison.OrdinalIgnoreCase)) return TrackRole.Descriptions;
        if (c.Contains("transcribes-spoken-dialog", StringComparison.OrdinalIgnoreCase)) return TrackRole.Captions;
        return type == AdaptiveTrackType.Text ? TrackRole.Subtitles : TrackRole.Main;
    }
    private static HdrFormat Hdr(string? range, string codecs)
    {
        if (string.Equals(range, "PQ", StringComparison.OrdinalIgnoreCase)) return HdrFormat.Hdr10;
        if (string.Equals(range, "HLG", StringComparison.OrdinalIgnoreCase)) return HdrFormat.Hlg;
        if (codecs.Contains("dvhe", StringComparison.OrdinalIgnoreCase) || codecs.Contains("dvh1", StringComparison.OrdinalIgnoreCase)) return HdrFormat.Hdr10;
        return HdrFormat.Sdr;
    }
    private static MediaContentType ContentType(AdaptiveTrackType type, string codecs)
    {
        string c = codecs.ToLowerInvariant();
        CodecId video = c.Contains("av01") ? CodecId.Av1 : c.Contains("hvc1") || c.Contains("hev1") || c.Contains("dvhe") || c.Contains("dvh1") ? CodecId.Hevc : c.Contains("vp09") ? CodecId.Vp9 : type == AdaptiveTrackType.Video ? CodecId.H264 : CodecId.None;
        CodecId audio = c.Contains("opus") ? CodecId.Opus : c.Contains("flac") ? CodecId.Flac : c.Contains("mp3") ? CodecId.Mp3 : type == AdaptiveTrackType.Audio || c.Contains("mp4a") || c.Contains("ac-3") || c.Contains("ec-3") ? CodecId.Aac : CodecId.None;
        return new MediaContentType(Container.Hls, video, audio);
    }
    private static ReadOnlyMemory<byte> DecodeDataUri(string uri)
    { int comma = uri.IndexOf(','); if (comma < 0) return default; try { return Convert.FromBase64String(uri[(comma + 1)..]); } catch (FormatException) { return default; } }
}
