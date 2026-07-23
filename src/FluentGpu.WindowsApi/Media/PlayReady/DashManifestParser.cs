using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;

namespace FluentGpu.WindowsApi.Media.PlayReady;

/// <summary>
/// The parsed source descriptor an MPD yields for the native PlayReady open path (<c>FgPlayReadyOpenDesc</c>): an explicit
/// init-segment URL plus the <c>base + prefix + number + suffix</c> media-segment template the native demuxer walks, the
/// segment range, and the PlayReady init data (<c>cenc:pssh</c> + <c>default_KID</c>). Produced by
/// <see cref="DashManifestParser"/>; consumed by <see cref="ProtectedMediaBackend"/> (mapped onto a
/// <see cref="ProtectedVideoRequest"/>). The license server URL is a SEPARATE concern (entered by the app, carried on
/// <see cref="FluentGpu.Media.DrmConfig"/> + the <c>WithDrm</c> relay) — a manifest does not carry it.
/// </summary>
public sealed record DashSourceDescriptor
{
    /// <summary>Absolute URL of the H.264 initialization segment.</summary>
    public required string InitUrl { get; init; }
    /// <summary>Base URL for the numbered media segments (the directory the segment names resolve against).</summary>
    public required string SegmentBaseUrl { get; init; }
    /// <summary>Media-segment name PREFIX (everything before the <c>$Number$</c> token, after the base).</summary>
    public required string SegmentPrefix { get; init; }
    /// <summary>Media-segment name SUFFIX (everything after the <c>$Number$</c> token, e.g. <c>.m4s</c>).</summary>
    public required string SegmentSuffix { get; init; }
    /// <summary>First segment number (<c>SegmentTemplate@startNumber</c>, default 1).</summary>
    public int StartNumber { get; init; } = 1;
    /// <summary>Number of media segments to fetch (from the <c>SegmentTimeline</c>, or <c>@duration</c>/<c>@timescale</c>
    /// against the presentation duration).</summary>
    public int SegmentCount { get; init; } = 1;
    /// <summary>Segment-number step: 1 for numbered <c>$Number$</c> content; N for time-addressed segments (Spotify names
    /// segments by absolute time — segment i = <see cref="StartNumber"/> + i*stride, stride = segment length in seconds).</summary>
    public int SegmentStride { get; init; } = 1;
    /// <summary>The PlayReady <c>cenc:pssh</c> init data (decoded from base64), or empty when the native parses it from the
    /// init segment.</summary>
    public ReadOnlyMemory<byte> Pssh { get; init; }
    /// <summary>The content key id (<c>@cenc:default_KID</c>), hex, dashless — advisory (the native derives the KID from the
    /// init segment).</summary>
    public string? DefaultKid { get; init; }
    /// <summary>The chosen video representation's <c>@id</c> (advisory / diagnostics).</summary>
    public string? RepresentationId { get; init; }
    /// <summary>The chosen representation's <c>@codecs</c> (e.g. <c>avc1.640028</c>).</summary>
    public string? Codecs { get; init; }
}

/// <summary>Thrown when an MPD cannot be parsed into a playable protected descriptor (no H.264 video representation, no
/// usable segment template, malformed XML, …). Surfaced by the app as an inline / typed error — never a silent black frame.</summary>
public sealed class DashManifestException : Exception
{
    public DashManifestException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>
/// A minimal, AOT-safe DASH MPD parser (System.Xml.Linq) for the protected-video path. It extracts the H.264 video
/// <c>Representation</c>'s <c>SegmentTemplate</c> (init + media + startNumber + segment count) and its PlayReady
/// <c>ContentProtection</c> (<c>cenc:pssh</c> + <c>@cenc:default_KID</c>), resolving relative URLs against the MPD's
/// <c>BaseURL</c> chain, and produces a <see cref="DashSourceDescriptor"/>. It handles <c>$Number$</c> /
/// <c>$RepresentationID$</c> / <c>$Bandwidth$</c> template substitution. Number-format padding (<c>$Number%05d$</c>) is
/// recognized but NOT applied (the native open ABI concatenates the raw number) — plain <c>$Number$</c> vectors (the
/// Axinom test vector, most PlayReady VOD) are fully supported.
/// </summary>
public static class DashManifestParser
{
    /// <summary>The PlayReady DASH <c>ContentProtection@schemeIdUri</c> (the PlayReady system id as a URN).</summary>
    public const string PlayReadySchemeIdUri = "urn:uuid:9a04f079-9840-4286-ab92-e65be0885f95";
    private const string CencNs = "urn:mpeg:cenc:2013";

    /// <summary>Fetch <paramref name="mpdUrl"/> and parse it into a protected source descriptor. Relative URLs resolve
    /// against the MPD's own URL + any <c>BaseURL</c> chain.</summary>
    public static async Task<DashSourceDescriptor> ParseAsync(string mpdUrl, HttpClient http, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(mpdUrl)) throw new DashManifestException("The MPD URL is empty.");
        string xml;
        try
        {
            xml = await http.GetStringAsync(mpdUrl, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new DashManifestException($"Could not fetch the MPD: {ex.Message}", ex);
        }
        return Parse(xml, mpdUrl);
    }

    /// <summary>Parse an MPD document (offline / testable). <paramref name="mpdUrl"/> is the manifest's own URL — the
    /// resolution base when the document has no absolute <c>BaseURL</c>.</summary>
    public static DashSourceDescriptor Parse(string xml, string mpdUrl)
    {
        XDocument doc;
        try { doc = XDocument.Parse(xml); }
        catch (Exception ex) { throw new DashManifestException("The MPD is not valid XML: " + ex.Message, ex); }

        var mpd = doc.Root ?? throw new DashManifestException("The MPD has no root element.");
        XNamespace ns = mpd.Name.Namespace;   // urn:mpeg:dash:schema:mpd:2011 (or empty)
        XNamespace cenc = CencNs;

        if (!Uri.TryCreate(mpdUrl, UriKind.Absolute, out var docBase))
            throw new DashManifestException("The MPD URL is not an absolute URL: " + mpdUrl);

        // ── locate the H.264 video Representation (walk Period → AdaptationSet → Representation) ──
        XElement? chosenRep = null, chosenAdaptation = null, chosenPeriod = null;
        foreach (var period in mpd.Elements(ns + "Period"))
        {
            foreach (var aset in period.Elements(ns + "AdaptationSet"))
            {
                if (!IsVideoSet(aset, ns)) continue;
                foreach (var rep in aset.Elements(ns + "Representation"))
                {
                    if (!IsH264(rep, aset)) continue;
                    // Prefer the first H.264 video representation (deterministic); good enough for a single-key vector.
                    chosenRep = rep; chosenAdaptation = aset; chosenPeriod = period;
                    break;
                }
                if (chosenRep is not null) break;
            }
            if (chosenRep is not null) break;
        }
        if (chosenRep is null || chosenAdaptation is null || chosenPeriod is null)
            throw new DashManifestException("No H.264 (avc1/avc3) video Representation with a SegmentTemplate was found in the MPD.");

        // ── resolve the effective BaseURL chain (MPD → Period → AdaptationSet → Representation) ──
        Uri baseUri = docBase;
        baseUri = ApplyBaseUrl(baseUri, mpd, ns);
        baseUri = ApplyBaseUrl(baseUri, chosenPeriod, ns);
        baseUri = ApplyBaseUrl(baseUri, chosenAdaptation, ns);
        baseUri = ApplyBaseUrl(baseUri, chosenRep, ns);

        // ── SegmentTemplate (Representation wins over AdaptationSet) ──
        var tmpl = chosenRep.Element(ns + "SegmentTemplate") ?? chosenAdaptation.Element(ns + "SegmentTemplate")
            ?? throw new DashManifestException("The H.264 video Representation has no SegmentTemplate (SegmentBase/SegmentList are not supported).");

        string? initTemplate = (string?)tmpl.Attribute("initialization");
        string? mediaTemplate = (string?)tmpl.Attribute("media");
        if (string.IsNullOrEmpty(initTemplate)) throw new DashManifestException("SegmentTemplate has no @initialization.");
        if (string.IsNullOrEmpty(mediaTemplate)) throw new DashManifestException("SegmentTemplate has no @media.");

        string repId = (string?)chosenRep.Attribute("id") ?? "";
        string bandwidth = (string?)chosenRep.Attribute("bandwidth") ?? "";
        int startNumber = ParseInt((string?)tmpl.Attribute("startNumber"), 1);

        string initSubst = SubstituteId(initTemplate!, repId, bandwidth);
        string mediaSubst = SubstituteId(mediaTemplate!, repId, bandwidth);

        string initUrl = Resolve(baseUri, initSubst);
        string mediaResolved = Resolve(baseUri, mediaSubst);   // still contains the $Number$ token

        // Split the resolved media URL at the $Number$ token → base(dir) + prefix + suffix.
        SplitNumberTemplate(mediaResolved, out string segBase, out string segPrefix, out string segSuffix);

        int segCount = ComputeSegmentCount(tmpl, mpd, ns, startNumber);

        // ── PlayReady ContentProtection (search AdaptationSet then Representation) ──
        (byte[] pssh, string? kid) = ExtractPlayReadyProtection(chosenAdaptation, chosenRep, ns, cenc);

        return new DashSourceDescriptor
        {
            InitUrl = initUrl,
            SegmentBaseUrl = segBase,
            SegmentPrefix = segPrefix,
            SegmentSuffix = segSuffix,
            StartNumber = startNumber,
            SegmentCount = segCount,
            Pssh = pssh,
            DefaultKid = kid,
            RepresentationId = repId.Length > 0 ? repId : null,
            Codecs = (string?)chosenRep.Attribute("codecs") ?? (string?)chosenAdaptation.Attribute("codecs"),
        };
    }

    // ── helpers ──────────────────────────────────────────────────────────────────────────────────────────────────────

    private static bool IsVideoSet(XElement aset, XNamespace ns)
    {
        string? contentType = (string?)aset.Attribute("contentType");
        if (string.Equals(contentType, "video", StringComparison.OrdinalIgnoreCase)) return true;
        string? mime = (string?)aset.Attribute("mimeType");
        if (mime is not null && mime.StartsWith("video/", StringComparison.OrdinalIgnoreCase)) return true;
        // No content hint on the set — decide by whether it holds a video-mime / H.264 representation.
        foreach (var rep in aset.Elements(ns + "Representation"))
        {
            string? rmime = (string?)rep.Attribute("mimeType");
            if (rmime is not null && rmime.StartsWith("video/", StringComparison.OrdinalIgnoreCase)) return true;
            if (IsH264(rep, aset)) return true;
        }
        return false;
    }

    private static bool IsH264(XElement rep, XElement aset)
    {
        string codecs = ((string?)rep.Attribute("codecs") ?? (string?)aset.Attribute("codecs") ?? "").ToLowerInvariant();
        if (codecs.Length == 0)
        {
            // No codecs attribute: accept a video-mime representation that carries a SegmentTemplate (best effort).
            string? mime = (string?)rep.Attribute("mimeType") ?? (string?)aset.Attribute("mimeType");
            return mime is not null && mime.StartsWith("video/", StringComparison.OrdinalIgnoreCase);
        }
        return codecs.Contains("avc1") || codecs.Contains("avc3");
    }

    private static Uri ApplyBaseUrl(Uri current, XElement el, XNamespace ns)
    {
        var b = el.Element(ns + "BaseURL");
        string? val = b?.Value?.Trim();
        if (string.IsNullOrEmpty(val)) return current;
        return Uri.TryCreate(current, val, out var next) ? next : current;
    }

    private static string Resolve(Uri baseUri, string relative)
        => Uri.TryCreate(baseUri, relative, out var abs) ? abs.ToString() : relative;

    private static string SubstituteId(string template, string repId, string bandwidth)
    {
        string s = template;
        if (s.Contains("$RepresentationID$", StringComparison.Ordinal)) s = s.Replace("$RepresentationID$", repId);
        if (s.Contains("$Bandwidth$", StringComparison.Ordinal)) s = s.Replace("$Bandwidth$", bandwidth);
        // A literal "$$" escapes a single '$' in the DASH template grammar.
        if (s.Contains("$$", StringComparison.Ordinal)) s = s.Replace("$$", "$");
        return s;
    }

    /// <summary>Split a resolved media URL that contains a <c>$Number$</c> (or <c>$Number%0Nd$</c>) token into a base
    /// directory, a filename prefix, and a suffix so the native open builds <c>base + prefix + number + suffix</c>.</summary>
    private static void SplitNumberTemplate(string mediaResolved, out string segBase, out string segPrefix, out string segSuffix)
    {
        int tok = mediaResolved.IndexOf("$Number", StringComparison.Ordinal);
        if (tok < 0)
            throw new DashManifestException("The media SegmentTemplate has no $Number$ token (time-based templates are not supported).");
        int close = mediaResolved.IndexOf('$', tok + 1);
        if (close < 0)
            throw new DashManifestException("Malformed $Number$ token in the media SegmentTemplate.");

        string before = mediaResolved[..tok];            // e.g. https://…/singlekey/video-H264-720-2100k_
        segSuffix = mediaResolved[(close + 1)..];         // e.g. .m4s
        int slash = before.LastIndexOf('/');
        if (slash >= 0) { segBase = before[..(slash + 1)]; segPrefix = before[(slash + 1)..]; }
        else { segBase = ""; segPrefix = before; }
        if (segSuffix.Length == 0) segSuffix = ".m4s";
    }

    private static int ComputeSegmentCount(XElement tmpl, XElement mpd, XNamespace ns, int startNumber)
    {
        // 1. SegmentTimeline: sum (1 + @r) across the S runs. An open-ended run (@r = -1) falls through to duration math.
        var timeline = tmpl.Element(ns + "SegmentTimeline");
        if (timeline is not null)
        {
            int count = 0; bool openEnded = false;
            foreach (var sEl in timeline.Elements(ns + "S"))
            {
                int r = ParseInt((string?)sEl.Attribute("r"), 0);
                if (r < 0) { openEnded = true; break; }
                count += 1 + r;
            }
            if (count > 0 && !openEnded) return count;
        }

        // 2. @duration + @timescale against the presentation duration.
        double segDurTicks = ParseDouble((string?)tmpl.Attribute("duration"), 0);
        double timescale = ParseDouble((string?)tmpl.Attribute("timescale"), 1);
        if (segDurTicks > 0 && timescale > 0)
        {
            double totalSeconds = ParseIsoDuration((string?)mpd.Attribute("mediaPresentationDuration"));
            double segSeconds = segDurTicks / timescale;
            if (totalSeconds > 0 && segSeconds > 0)
                return Math.Max(1, (int)Math.Ceiling(totalSeconds / segSeconds));
        }

        // 3. Unknown — the native tolerates over-fetch (it stops at the first missing segment); a small safe default.
        _ = startNumber;
        return 6;
    }

    private static (byte[] pssh, string? kid) ExtractPlayReadyProtection(XElement aset, XElement rep, XNamespace ns, XNamespace cenc)
    {
        byte[] pssh = Array.Empty<byte>();
        string? kid = null;
        foreach (var scope in new[] { aset, rep })
        {
            foreach (var cp in scope.Elements(ns + "ContentProtection"))
            {
                string scheme = ((string?)cp.Attribute("schemeIdUri") ?? "").Trim();
                // default_KID may live on the generic cenc ContentProtection element; grab it wherever present.
                kid ??= NormalizeKid((string?)cp.Attribute(cenc + "default_KID"));

                if (string.Equals(scheme, PlayReadySchemeIdUri, StringComparison.OrdinalIgnoreCase))
                {
                    var psshEl = cp.Element(cenc + "pssh");
                    if (psshEl is not null && pssh.Length == 0)
                    {
                        string b64 = psshEl.Value.Trim();
                        try { pssh = Convert.FromBase64String(b64); } catch { /* leave native to parse from init */ }
                    }
                }
            }
        }
        return (pssh, kid);
    }

    private static string? NormalizeKid(string? kid)
    {
        if (string.IsNullOrWhiteSpace(kid)) return null;
        return kid.Replace("-", "").Trim().ToLowerInvariant();
    }

    private static int ParseInt(string? s, int fallback)
        => int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : fallback;

    private static double ParseDouble(string? s, double fallback)
        => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : fallback;

    private static double ParseIsoDuration(string? iso)
    {
        if (string.IsNullOrWhiteSpace(iso)) return 0;
        try { return XmlConvert.ToTimeSpan(iso).TotalSeconds; } catch { return 0; }
    }
}
