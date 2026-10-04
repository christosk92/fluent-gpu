using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using FluentGpu.Media;
using FluentGpu.Media.Adaptive;
using MediaTrackKind = FluentGpu.Media.TrackKind;

namespace FluentGpu.WindowsApi.Media.PlayReady;

/// <summary>
/// Maps the engine's normalized <see cref="AdaptiveManifest"/> (the ONE DASH parser, <see cref="DashManifestParser"/>)
/// onto the protected open path's <see cref="DashSourceDescriptor"/> + <see cref="ProtectedAdaptiveCatalog"/>.
/// <para>The descriptor describes every media segment as <c>base + prefix + number + suffix</c> with a plain decimal
/// number (that is all the native demuxer concatenates). A manifest gives the engine concrete segment URLs, so the
/// template is recovered from them and then VERIFIED against every segment of the representation: a manifest the model
/// cannot express exactly (a zero-padded <c>$Number%0Nd$</c> whose segment numbers cross a digit boundary such as 9 to 10,
/// <c>$Time$</c> addressing, <c>SegmentList</c> file names, a suffix-less URL) is rejected with a
/// <see cref="DashManifestException"/>, never handed to the native side as a URL that 404s. A padded template maps exactly
/// when every segment number has the same digit count (the padding zeros fold into the prefix).</para>
/// <para>Not expressible by the descriptor and rejected too: live (dynamic) MPDs, an opened video representation with no
/// segment list, and a presentation the engine truncated (more than
/// <see cref="DashManifestParser.MaxSegmentsPerRepresentation"/> segments, or several Periods) - the manifest's stated
/// duration must be covered. The video representation is the first H.264 one of the first video adaptation set that has
/// one (the initial pick, which must map). The ABR catalog holds the other H.264 representations only when they share the
/// opened one's segment grid (the session swaps just the URL parts and maps a position to an index with ONE segment
/// length, so StartNumber, SegmentCount and the segment BOUNDARIES - start times on the same grid - must match); one that
/// does not is left out of the ladder. The paired soundtrack is the first AAC representation of the first audio adaptation
/// set; when it cannot ride the video's grid (not expressible, a different StartNumber, a segment count more than one
/// apart, boundaries off the video's grid) the source degrades to video only.</para>
/// </summary>
public static class DashDescriptorMapper
{
    /// <summary>Fetch <paramref name="mpdUrl"/> and map it into a protected source descriptor. Relative URLs resolve
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

    /// <summary>Parse an MPD document (offline / testable). <paramref name="mpdUrl"/> is the manifest's own URL - the
    /// resolution base when the document has no absolute <c>BaseURL</c>.</summary>
    public static DashSourceDescriptor Parse(string xml, string mpdUrl)
    {
        if (!Uri.TryCreate(mpdUrl, UriKind.Absolute, out Uri? source))
            throw new DashManifestException("The MPD URL is not an absolute URL: " + mpdUrl);
        AdaptiveManifest manifest;
        try { manifest = DashManifestParser.Parse(xml, source); }
        catch (Exception ex) when (ex is XmlException or FormatException or ArgumentException)
        {
            throw new DashManifestException("The MPD could not be parsed: " + ex.Message, ex);
        }
        return Map(manifest);
    }

    /// <summary>Map a parsed DASH <paramref name="manifest"/> onto a descriptor (see the type remarks for what is
    /// rejected).</summary>
    public static DashSourceDescriptor Map(AdaptiveManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (manifest.IsLive)
            throw new DashManifestException("A live (dynamic) MPD cannot be mapped: the native open path walks a fixed segment range.");

        AdaptiveTrackGroup? videoGroup = null;
        var videoReps = new List<AdaptiveRepresentation>();
        AdaptiveTrackGroup? audioGroup = null;
        AdaptiveRepresentation? audioRep = null;
        foreach (AdaptiveTrackGroup group in manifest.TrackGroups)
        {
            if (group.Type == AdaptiveTrackType.Video && videoGroup is null)
            {
                for (int i = 0; i < group.Representations.Count; i++)
                    if (IsH264(group.Representations[i])) videoReps.Add(group.Representations[i]);
                if (videoReps.Count > 0) videoGroup = group;
            }
            else if (group.Type == AdaptiveTrackType.Audio && audioRep is null)
            {
                for (int i = 0; i < group.Representations.Count; i++)
                    if (IsAac(group.Representations[i])) { audioGroup = group; audioRep = group.Representations[i]; break; }
            }
        }
        if (videoGroup is null)
            throw new DashManifestException("No H.264 (avc1/avc3) video Representation was found in the MPD.");

        // The opened representation must map (a failure rejects the manifest); the rest of the ladder is best effort.
        AdaptiveRepresentation video = videoReps[0];
        RepAddress v = Address(video);
        var representations = new List<ProtectedRepresentationDescriptor>(videoReps.Count);
        int topHeight = 0;
        for (int i = 0; i < videoReps.Count; i++)
        {
            AdaptiveRepresentation rep = videoReps[i];
            RepAddress a = v;
            // The session swaps only init/base/prefix/suffix, so an alternate must share the opened one's numbering, count
            // and segment boundaries (a switch at index k fetches that segment number, so it must span the same time).
            if (i > 0 && !TryAddress(rep, out a)) continue;
            if (i > 0 && (a.StartNumber != v.StartNumber || a.Count != v.Count || !SharesGrid(rep, video))) continue;
            topHeight = Math.Max(topHeight, rep.Quality.Resolution.Height);
            representations.Add(new ProtectedRepresentationDescriptor
            {
                Id = rep.Quality.Id,
                Quality = rep.Quality,
                InitUrl = a.InitUrl,
                SegmentBaseUrl = a.Base,
                SegmentPrefix = a.Prefix,
                SegmentSuffix = a.Suffix,
                StartNumber = a.StartNumber,
                SegmentCount = a.Count,
                SegmentStride = 1,
                DefaultKid = rep.DefaultKid,
            });
        }

        RequireCoversPresentation(manifest, video);

        // The native shares StartNumber/SegmentCount/SegmentStride between the two streams, so the soundtrack must ride the
        // video's grid (numbering, count and segment boundaries: the native maps a position to an index with one segment
        // length for both streams); one that cannot is dropped (video only), never a failure. The count may differ by one:
        // the last video and audio segments rarely end on the same frame.
        RepAddress? audio = null;
        if (audioRep is not null)
        {
            if (TryAddress(audioRep, out RepAddress ra) && ra.StartNumber == v.StartNumber && Math.Abs(ra.Count - v.Count) <= 1
                && SharesGrid(audioRep, video))
                audio = ra;
            else
                audioRep = null;
        }

        var tracks = new List<ProtectedTrackDescriptor>(2)
        {
            new()
            {
                Id = 1, Kind = MediaTrackKind.Video, Language = videoGroup.Language,
                Label = topHeight > 0 ? topHeight + "p" : videoGroup.Id, Role = videoGroup.Role,
                IsDefault = true, Representations = representations,
            },
        };
        if (audio is { } a2 && audioRep is not null && audioGroup is not null)
        {
            tracks.Add(new ProtectedTrackDescriptor
            {
                Id = 2, Kind = MediaTrackKind.Audio, Language = audioGroup.Language,
                Label = audioGroup.Language ?? "audio", Role = audioGroup.Role, IsDefault = true,
                Representations = new[]
                {
                    new ProtectedRepresentationDescriptor
                    {
                        Id = audioRep.Quality.Id, Quality = audioRep.Quality,
                        InitUrl = a2.InitUrl, SegmentBaseUrl = a2.Base, SegmentPrefix = a2.Prefix, SegmentSuffix = a2.Suffix,
                        StartNumber = a2.StartNumber, SegmentCount = a2.Count, SegmentStride = 1,
                        DefaultKid = audioRep.DefaultKid,
                    },
                },
            });
        }

        double segmentMs = video.Segments[0].Duration.TotalMilliseconds;
        // Only a real PlayReady PSSH box is handed to the CDM; a Widevine-only manifest or a bare <mspr:pro> object leaves
        // the protection to the init segment.
        bool playReady = string.Equals(video.DrmScheme, "playready", StringComparison.Ordinal) && IsPlayReadyPsshBox(video.InitData.Span);
        return new DashSourceDescriptor
        {
            Catalog = new ProtectedAdaptiveCatalog { Tracks = tracks },
            InitUrl = v.InitUrl,
            SegmentBaseUrl = v.Base,
            SegmentPrefix = v.Prefix,
            SegmentSuffix = v.Suffix,
            StartNumber = v.StartNumber,
            SegmentCount = v.Count,
            SegmentStride = 1,
            SegmentLengthMs = segmentMs is > 0 and < int.MaxValue ? (int)Math.Round(segmentMs) : 0,
            DurationMs = manifest.Duration is { } total && total > TimeSpan.Zero ? (long)Math.Round(total.TotalMilliseconds) : 0,
            SegmentsStartWithKeyframe = video.SegmentsStartWithKeyframe,
            Pssh = playReady ? video.InitData : default,
            DefaultKid = video.DefaultKid ?? audioRep?.DefaultKid,
            RepresentationId = video.Quality.Id,
            Codecs = video.Codecs,
            AudioInitUrl = audio?.InitUrl,
            AudioSegmentBaseUrl = audio?.Base,
            AudioSegmentPrefix = audio?.Prefix,
            AudioSegmentSuffix = audio?.Suffix,
            AudioCodecs = audioRep?.Codecs,
        };
    }

    // -- helpers ------------------------------------------------------------------------------------------------------

    /// <summary>One representation's URLs in the descriptor's shape.</summary>
    private readonly record struct RepAddress(string InitUrl, string Base, string Prefix, string Suffix, int StartNumber, int Count);

    // The native decodes H.264 video; a codec-less video representation is accepted best-effort.
    private static bool IsH264(AdaptiveRepresentation rep)
        => rep.Quality.Codec.Video == CodecId.H264
           || (rep.Quality.Codec.Video == CodecId.None && string.IsNullOrEmpty(rep.Codecs));

    private static bool IsAac(AdaptiveRepresentation rep)
        => rep.Quality.Codec.Audio == CodecId.Aac
           || (rep.Quality.Codec.Audio == CodecId.None && string.IsNullOrEmpty(rep.Codecs));

    /// <summary>A truncated expansion must not play as a shortened presentation: the segments have to reach the
    /// manifest's stated duration (within one segment, the last one is usually shorter).</summary>
    private static void RequireCoversPresentation(AdaptiveManifest manifest, AdaptiveRepresentation rep)
    {
        if (manifest.Duration is not { } total || total <= TimeSpan.Zero) return;
        AdaptiveSegment last = rep.Segments[^1];
        TimeSpan end = last.Start + last.Duration;
        TimeSpan slack = TimeSpan.FromSeconds(Math.Max(1d, rep.Segments[0].Duration.TotalSeconds));
        if (end + slack < total)
            throw new DashManifestException($"The video Representation '{rep.Quality.Id}' covers {end.TotalSeconds:0.###} s of the {total.TotalSeconds:0.###} s presentation: manifests with several Periods or more than {DashManifestParser.MaxSegmentsPerRepresentation} segments are not supported.");
    }

    private static readonly byte[] PlayReadySystemId =
        { 0x9A, 0x04, 0xF0, 0x79, 0x98, 0x40, 0x42, 0x86, 0xAB, 0x92, 0xE6, 0x5B, 0xE0, 0x88, 0x5F, 0x95 };

    /// <summary>True when <paramref name="data"/> is a whole ISO <c>pssh</c> box for the PlayReady system id (size, 'pssh',
    /// version/flags, system id, data size, data). A bare PlayReady Object (<c>mspr:pro</c>) is not one, and the native side
    /// would hand it to the CDM as the PSSH.</summary>
    private static bool IsPlayReadyPsshBox(ReadOnlySpan<byte> data)
        => data.Length >= 32 && data[4] == (byte)'p' && data[5] == (byte)'s' && data[6] == (byte)'s' && data[7] == (byte)'h'
           && data.Slice(12, 16).SequenceEqual(PlayReadySystemId);

    /// <summary>True when <paramref name="rep"/>'s segment start times sit on <paramref name="video"/>'s grid: for every
    /// index both have, the starts agree within an eighth of a video segment. Real packagers drift a little (an audio
    /// grid of 3.947 s against a 4 s video one is about 0.07 s off at the second segment); a 3 s grid against a 4 s one is a
    /// second off and is rejected.</summary>
    private static bool SharesGrid(AdaptiveRepresentation rep, AdaptiveRepresentation video)
    {
        TimeSpan tolerance = video.Segments[0].Duration / 8;
        int shared = Math.Min(rep.Segments.Count, video.Segments.Count);
        for (int k = 0; k < shared; k++)
            if ((rep.Segments[k].Start - video.Segments[k].Start).Duration() > tolerance) return false;
        return true;
    }

    private static bool TryAddress(AdaptiveRepresentation rep, out RepAddress address)
    {
        try { address = Address(rep); return true; }
        catch (DashManifestException) { address = default; return false; }
    }

    /// <summary>Recover <c>base + prefix + number + suffix</c> from the representation's concrete segment URLs and prove
    /// it reproduces every one of them. Throws when no such template exists.</summary>
    private static RepAddress Address(AdaptiveRepresentation rep)
    {
        string id = rep.Quality.Id;
        IReadOnlyList<AdaptiveSegment> segs = rep.Segments;
        if (segs.Count == 0)
            throw new DashManifestException($"Representation '{id}' has no addressable media segments (SegmentBase and single-file representations are not supported).");
        if (rep.Initialization is null)
            throw new DashManifestException($"Representation '{id}' has no initialization segment.");
        long first = segs[0].Number;
        if (first < 0 || first > int.MaxValue - segs.Count)
            throw new DashManifestException($"Representation '{id}' numbers its segments from {first}, outside the range the native open path takes.");

        string firstUrl = segs[0].Uri.AbsoluteUri;
        string number = first.ToString(CultureInfo.InvariantCulture);
        var candidates = new List<int>();
        for (int at = firstUrl.IndexOf(number, StringComparison.Ordinal); at >= 0;
             at = firstUrl.IndexOf(number, at + 1, StringComparison.Ordinal))
            candidates.Add(at);

        // The last occurrence is the file name's own number; an earlier one only wins when the later ones do not
        // reproduce the whole list.
        for (int c = candidates.Count - 1; c >= 0; c--)
        {
            string prefix = firstUrl[..candidates[c]];
            string suffix = firstUrl[(candidates[c] + number.Length)..];
            int slash = prefix.LastIndexOf('/');
            // The native defaults an empty suffix to ".m4s", so a suffix-less URL is not expressible.
            if (slash < 0 || suffix.Length == 0 || !Reproduces(segs, first, prefix, suffix)) continue;
            return new RepAddress(rep.Initialization.AbsoluteUri, prefix[..(slash + 1)], prefix[(slash + 1)..], suffix,
                (int)first, segs.Count);
        }
        throw new DashManifestException($"Representation '{id}' addresses its segments in a way the native template (base + prefix + number + suffix, plain decimal number) cannot express - a zero-padded $Number%0Nd$ whose segment numbers cross a digit boundary (9 to 10, 99 to 100), $Time$ addressing, a SegmentList, or no file extension. First segment: {firstUrl}");
    }

    private static bool Reproduces(IReadOnlyList<AdaptiveSegment> segs, long first, string prefix, string suffix)
    {
        for (int k = 0; k < segs.Count; k++)
        {
            long n = first + k;
            if (segs[k].Number != n) return false;
            string expected = string.Concat(prefix, n.ToString(CultureInfo.InvariantCulture), suffix);
            if (!string.Equals(segs[k].Uri.AbsoluteUri, expected, StringComparison.Ordinal)) return false;
        }
        return true;
    }
}
