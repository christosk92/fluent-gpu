using System;
using System.Collections.Generic;
using System.Globalization;
using System.Xml;
using System.Xml.Linq;

namespace FluentGpu.Media.Adaptive;

/// <summary>AOT-safe MPEG-DASH MPD normalizer. Handles BaseURL inheritance, SegmentTemplate Number/Time formatting,
/// SegmentTimeline repeats, VOD and dynamic windows, multiple adaptation sets, roles, and CENC init data.</summary>
public static class DashManifestParser
{
    public static AdaptiveManifest Parse(string xml, Uri source, DateTimeOffset? now = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(xml);
        var doc = XDocument.Parse(xml, LoadOptions.None);
        XElement mpd = doc.Root ?? throw new FormatException("DASH MPD has no root element.");
        bool live = Eq(A(mpd, "type"), "dynamic");
        TimeSpan? duration = Duration(A(mpd, "mediaPresentationDuration"));
        TimeSpan mup = Duration(A(mpd, "minimumUpdatePeriod")) ?? TimeSpan.Zero;
        TimeSpan tsbd = Duration(A(mpd, "timeShiftBufferDepth")) ?? TimeSpan.Zero;
        TimeSpan delay = Duration(A(mpd, "suggestedPresentationDelay")) ?? TimeSpan.Zero;
        DateTimeOffset? ast = Date(A(mpd, "availabilityStartTime"));
        bool lowLatency = false;
        foreach (var e in mpd.Descendants())
            if (e.Name.LocalName == "ServiceDescription" || A(e, "availabilityTimeOffset") is not null) { lowLatency = true; break; }

        Uri mpdBase = ResolveBase(source, mpd);
        var groups = new List<AdaptiveTrackGroup>();
        int periodIndex = 0;
        foreach (var period in Children(mpd, "Period"))
        {
            Uri periodBase = ResolveBase(mpdBase, period);
            TimeSpan periodStart = Duration(A(period, "start")) ?? TimeSpan.Zero;
            TimeSpan? periodDuration = Duration(A(period, "duration")) ?? duration;
            int adaptationIndex = 0;
            foreach (var adaptation in Children(period, "AdaptationSet"))
            {
                Uri adaptationBase = ResolveBase(periodBase, adaptation);
                AdaptiveTrackType type = TrackType(adaptation);
                string? language = A(adaptation, "lang");
                TrackRole role = ParseRole(adaptation);
                bool isDefault = role == TrackRole.Main;
                bool forced = role == TrackRole.Captions;
                var reps = new List<AdaptiveRepresentation>();
                foreach (var representation in Children(adaptation, "Representation"))
                    reps.Add(ParseRepresentation(representation, adaptation, adaptationBase, type, periodStart,
                        periodDuration, live, ast, tsbd, now ?? DateTimeOffset.UtcNow, ref lowLatency));

                if (reps.Count > 0)
                {
                    string id = A(adaptation, "id") ?? $"p{periodIndex}-{type.ToString().ToLowerInvariant()}{adaptationIndex}";
                    groups.Add(new AdaptiveTrackGroup(id, type, language, role, reps, isDefault, forced));
                }
                adaptationIndex++;
            }
            periodIndex++;
        }

        if (groups.Count == 0) throw new FormatException("DASH MPD contains no playable representations.");
        return new AdaptiveManifest(source, AdaptiveManifestKind.Dash, live, lowLatency, duration, mup, tsbd, delay, ast, groups);
    }

    private static AdaptiveRepresentation ParseRepresentation(
        XElement rep, XElement adaptation, Uri parent, AdaptiveTrackType type, TimeSpan periodStart,
        TimeSpan? periodDuration, bool live, DateTimeOffset? availabilityStart, TimeSpan timeShiftDepth,
        DateTimeOffset now, ref bool lowLatency)
    {
        Uri baseUri = ResolveBase(parent, rep);
        string id = A(rep, "id") ?? Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
        int bandwidth = Int(A(rep, "bandwidth"));
        int width = Int(A(rep, "width"), Int(A(adaptation, "width")));
        int height = Int(A(rep, "height"), Int(A(adaptation, "height")));
        double fps = FrameRate(A(rep, "frameRate") ?? A(adaptation, "frameRate"));
        string codecs = A(rep, "codecs") ?? A(adaptation, "codecs") ?? "";
        var contentType = ContentType(type, codecs);
        var quality = new QualityVariant(id, bandwidth, new SizeI(width, height), fps, contentType,
            HdrFrom(adaptation, rep), A(rep, "label"));

        XElement? template = Child(rep, "SegmentTemplate") ?? Child(adaptation, "SegmentTemplate");
        XElement? segmentList = Child(rep, "SegmentList") ?? Child(adaptation, "SegmentList");
        var segments = new List<AdaptiveSegment>();
        Uri? initialization = null;

        if (template is not null)
        {
            long timescale = Long(A(template, "timescale"), 1);
            long startNumber = Long(A(template, "startNumber"), 1);
            long presentationOffset = Long(A(template, "presentationTimeOffset"));
            string media = A(template, "media") ?? throw new FormatException($"DASH representation '{id}' has no media template.");
            string? init = A(template, "initialization");
            if (init is not null)
                initialization = AdaptiveUri.Resolve(baseUri, AdaptiveUri.ExpandTemplate(init, id, startNumber, 0, bandwidth));
            if (A(template, "availabilityTimeOffset") is not null) lowLatency = true;

            XElement? timeline = Child(template, "SegmentTimeline");
            if (timeline is not null)
            {
                long number = startNumber, time = 0;
                foreach (var s in Children(timeline, "S"))
                {
                    long d = Long(A(s, "d"));
                    if (d <= 0) throw new FormatException("DASH SegmentTimeline entry has no positive duration.");
                    long explicitTime = Long(A(s, "t"), long.MinValue);
                    if (explicitTime != long.MinValue) time = explicitTime;
                    long repeat = Long(A(s, "r"));
                    if (repeat < 0)
                    {
                        double windowSeconds = periodDuration?.TotalSeconds ?? (timeShiftDepth > TimeSpan.Zero ? timeShiftDepth.TotalSeconds : 120);
                        repeat = Math.Max(0, Math.Min(4095, (long)Math.Ceiling(windowSeconds * timescale / d) - 1));
                    }
                    for (long k = 0; k <= repeat && segments.Count < 4096; k++, number++, time += d)
                    {
                        string rel = AdaptiveUri.ExpandTemplate(media, id, number, time, bandwidth);
                        segments.Add(new AdaptiveSegment(AdaptiveUri.Resolve(baseUri, rel), number,
                            periodStart + Ticks(time - presentationOffset, timescale), Ticks(d, timescale)));
                    }
                }
            }
            else
            {
                long d = Long(A(template, "duration"));
                if (d <= 0) throw new FormatException($"DASH representation '{id}' has no SegmentTimeline or duration.");
                double segSeconds = (double)d / timescale;
                long first = startNumber, count;
                if (live && availabilityStart is { } start)
                {
                    long current = startNumber + Math.Max(0, (long)((now - start).TotalSeconds / segSeconds));
                    long keep = Math.Max(2, (long)Math.Ceiling((timeShiftDepth > TimeSpan.Zero ? timeShiftDepth.TotalSeconds : 60) / segSeconds));
                    first = Math.Max(startNumber, current - keep + 1);
                    count = keep;
                }
                else count = Math.Max(1, (long)Math.Ceiling((periodDuration ?? TimeSpan.FromSeconds(segSeconds)).TotalSeconds / segSeconds));
                count = Math.Min(count, 4096);
                for (long k = 0; k < count; k++)
                {
                    long number = first + k;
                    long time = (number - startNumber) * d;
                    segments.Add(new AdaptiveSegment(AdaptiveUri.Resolve(baseUri,
                        AdaptiveUri.ExpandTemplate(media, id, number, time, bandwidth)), number,
                        periodStart + Ticks(time - presentationOffset, timescale), Ticks(d, timescale)));
                }
            }
        }
        else if (segmentList is not null)
        {
            long timescale = Long(A(segmentList, "timescale"), 1);
            long d = Long(A(segmentList, "duration"));
            string? init = A(Child(segmentList, "Initialization"), "sourceURL");
            if (init is not null) initialization = AdaptiveUri.Resolve(baseUri, init);
            long number = 1;
            foreach (var segmentUrl in Children(segmentList, "SegmentURL"))
            {
                string media = A(segmentUrl, "media") ?? throw new FormatException("DASH SegmentURL is missing media.");
                segments.Add(new AdaptiveSegment(AdaptiveUri.Resolve(baseUri, media), number,
                    periodStart + Ticks((number - 1) * d, timescale), Ticks(d, timescale)));
                number++;
            }
        }

        string? drmScheme = null;
        ReadOnlyMemory<byte> initData = default;
        foreach (var cp in Children(adaptation, "ContentProtection"))
        {
            string scheme = A(cp, "schemeIdUri") ?? "";
            if (scheme.Contains("9a04f079", StringComparison.OrdinalIgnoreCase)) drmScheme = "playready";
            else if (scheme.Contains("edef8ba9", StringComparison.OrdinalIgnoreCase)) drmScheme ??= "widevine";
            foreach (var child in cp.Elements())
                if (child.Name.LocalName is "pssh" or "pro" && !string.IsNullOrWhiteSpace(child.Value))
                    try { initData = Convert.FromBase64String(child.Value.Trim()); } catch (FormatException) { }
        }
        return new AdaptiveRepresentation(quality, initialization, segments, null, drmScheme, initData);
    }

    private static IEnumerable<XElement> Children(XElement? p, string local)
    { if (p is not null) foreach (var e in p.Elements()) if (e.Name.LocalName == local) yield return e; }
    private static XElement? Child(XElement? p, string local)
    { if (p is not null) foreach (var e in p.Elements()) if (e.Name.LocalName == local) return e; return null; }
    private static string? A(XElement? e, string local)
    { if (e is not null) foreach (var a in e.Attributes()) if (a.Name.LocalName == local) return a.Value; return null; }
    private static bool Eq(string? a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    private static int Int(string? s, int fallback = 0) => int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : fallback;
    private static long Long(string? s, long fallback = 0) => long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out long v) ? v : fallback;
    private static DateTimeOffset? Date(string? s) => DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var v) ? v : null;
    private static TimeSpan? Duration(string? s) { if (string.IsNullOrWhiteSpace(s)) return null; try { return XmlConvert.ToTimeSpan(s); } catch (FormatException) { return null; } }
    private static TimeSpan Ticks(long value, long scale) => scale > 0 ? TimeSpan.FromSeconds((double)value / scale) : TimeSpan.Zero;
    private static Uri ResolveBase(Uri parent, XElement node) => AdaptiveUri.Resolve(parent, Child(node, "BaseURL")?.Value);
    private static double FrameRate(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return 0;
        int slash = s.IndexOf('/');
        if (slash > 0 && double.TryParse(s[..slash], NumberStyles.Float, CultureInfo.InvariantCulture, out double n)
            && double.TryParse(s[(slash + 1)..], NumberStyles.Float, CultureInfo.InvariantCulture, out double d) && d != 0) return n / d;
        return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : 0;
    }
    private static AdaptiveTrackType TrackType(XElement adaptation)
    {
        string value = A(adaptation, "contentType") ?? A(adaptation, "mimeType") ?? "";
        if (value.Contains("audio", StringComparison.OrdinalIgnoreCase)) return AdaptiveTrackType.Audio;
        if (value.Contains("text", StringComparison.OrdinalIgnoreCase) || value.Contains("subtitle", StringComparison.OrdinalIgnoreCase)
            || value.Contains("application", StringComparison.OrdinalIgnoreCase)) return AdaptiveTrackType.Text;
        return AdaptiveTrackType.Video;
    }
    private static TrackRole ParseRole(XElement adaptation)
    {
        string value = A(Child(adaptation, "Role"), "value") ?? "main";
        return value.ToLowerInvariant() switch
        {
            "alternate" => TrackRole.Alternate, "commentary" => TrackRole.Commentary,
            "description" => TrackRole.Descriptions, "caption" or "captions" => TrackRole.Captions,
            "subtitle" or "subtitles" => TrackRole.Subtitles, "sign" => TrackRole.Sign, _ => TrackRole.Main
        };
    }
    private static MediaContentType ContentType(AdaptiveTrackType type, string codecs)
    {
        CodecId video = CodecId.None, audio = CodecId.None;
        string c = codecs.ToLowerInvariant();
        if (c.Contains("avc1") || c.Contains("avc3")) video = CodecId.H264;
        else if (c.Contains("hvc1") || c.Contains("hev1")) video = CodecId.Hevc;
        else if (c.Contains("av01")) video = CodecId.Av1;
        else if (c.Contains("vp09") || c.Contains("vp9")) video = CodecId.Vp9;
        if (c.Contains("mp4a")) audio = CodecId.Aac;
        else if (c.Contains("opus")) audio = CodecId.Opus;
        return new MediaContentType(Container.Dash, type == AdaptiveTrackType.Video ? video : CodecId.None,
            type == AdaptiveTrackType.Audio ? audio : CodecId.None);
    }
    private static HdrFormat HdrFrom(XElement adaptation, XElement rep)
    {
        string value = (A(rep, "supplementalCodecs") ?? A(rep, "codecs") ?? "") + " " +
                       (A(adaptation, "supplementalCodecs") ?? A(adaptation, "codecs") ?? "");
        if (value.Contains("hlg", StringComparison.OrdinalIgnoreCase)) return HdrFormat.Hlg;
        if (value.Contains("hdr", StringComparison.OrdinalIgnoreCase) || value.Contains("hvc1.2", StringComparison.OrdinalIgnoreCase)
            || value.Contains("hev1.2", StringComparison.OrdinalIgnoreCase)) return HdrFormat.Hdr10;
        return HdrFormat.Sdr;
    }
}
