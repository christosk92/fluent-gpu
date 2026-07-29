using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using Wavee.Backend;
using Wavee.Backend.Metadata;
using Wavee.Core;
using Va = Wavee.Protocol.ContentAgnostic;
using Aa = Wavee.Protocol.AudioAttributes;
using De = Wavee.Protocol.DescriptorExtension;
using Xm = Wavee.Protocol.ExtendedMetadata;

namespace Wavee.SpotifyLive;

/// <summary>Row adornments that ride the SAME extended-metadata batch a track list already needs: the cover's dominant
/// colour (kind 179 VISUAL_IDENTITY_TRAIT) and tempo/key (kind 222 AUDIO_ATTRIBUTES_V2).
///
/// Why both in one service: they are requested together, for the same uri set, at the same moment (a list realizing),
/// and <c>GzipExtensionRequest</c> groups multiple kinds under ONE EntityRequest — so asking for both costs no extra
/// round trip over asking for either.
///
/// Why it matters: kind 179 carries the colour in the same payload as the image URLs, so the placeholder can be tinted
/// BEFORE any image byte arrives. That is the difference between a list of blank grey squares and a list that paints
/// its covers immediately.
///
/// DATA ONLY. Tempo/key/descriptors go onto the Store's Track rows; the COLOUR goes to <see cref="CoverColorPlane"/>
/// keyed by image, never onto the row — an entity-keyed copy is what used to leave album grids grey while the track
/// list beside them painted in colour.</summary>
public sealed class SpotifyTrackAdornmentService
{
    // Matches the measured server-side ceiling: several independent kinds hit exactly 300 entities per POST across the
    // captured corpus and none exceeded it. The transport chunks by BYTES only, so without this a 10k-track playlist
    // would go out as one body.
    const int BatchCap = 300;

    static readonly MessageParser<Va.VisualIdentityTrait> VisualParser =
        Va.VisualIdentityTrait.Parser.WithDiscardUnknownFields(true);
    static readonly MessageParser<Aa.AudioAttributes> AudioParser =
        Aa.AudioAttributes.Parser.WithDiscardUnknownFields(true);
    static readonly MessageParser<De.ExtensionDescriptorData> DescriptorParser =
        De.ExtensionDescriptorData.Parser.WithDiscardUnknownFields(true);

    /// <summary>How many descriptors of a track are kept. The corpus runs 1..33 per track (median ~13) in descending
    /// weight, and the tail is noise for a chip bar — the first few ARE the track's identity.</summary>
    const int MaxTagsPerTrack = 6;

    readonly ExtendedMetadataSource _metadata;
    readonly ExtensionEtagCache? _extensions;
    readonly IStore _store;
    readonly WaveeLogger _log;

    // Session-scoped negative cache. Not every track HAS a 222 (11.5k payloads against 217k TrackV4 in the corpus), and
    // a track with no adornment would otherwise be re-requested on every list realize for the whole session. The
    // durable negative lives in ExtensionEtagCache when one is wired; this covers the no-cache path and the in-session
    // repeat. Bounded so a long session over a huge library cannot grow it without limit.
    const int MaxNegative = 20_000;
    readonly ConcurrentDictionary<string, byte> _noAdornment = new(StringComparer.Ordinal);

    public SpotifyTrackAdornmentService(ExtendedMetadataSource metadata, IStore store,
                                        WaveeLogger log = default, ExtensionEtagCache? extensions = null)
    {
        _metadata = metadata;
        _store = store;
        _log = log;
        _extensions = extensions;
    }

    /// <summary>Fill in tint + tempo/key for the given tracks. Already-adorned and known-barren uris are skipped
    /// without touching the network, so calling this on every list realize is cheap. Best-effort: a failure logs and
    /// leaves the rows un-adorned (they render on the neutral placeholder), it never fails the caller.</summary>
    public async Task EnsureAsync(IReadOnlyList<string> trackUris, CancellationToken ct = default)
    {
        if (trackUris.Count == 0) return;

        var pending = new List<string>(Math.Min(trackUris.Count, BatchCap));
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var uri in trackUris)
        {
            if (uri.Length == 0 || !uri.StartsWith("spotify:track:", StringComparison.Ordinal)) continue;
            if (!seen.Add(uri)) continue;
            if (_noAdornment.ContainsKey(uri)) continue;
            // Already been through here. The colour half lands in CoverColorPlane (keyed by IMAGE, not by track), so
            // the row's own tempo is what marks this uri as fetched.
            if (_store.GetTrack(uri) is { TempoBpm: not null }) continue;
            pending.Add(uri);
        }
        if (pending.Count == 0) return;

        for (int start = 0; start < pending.Count && !ct.IsCancellationRequested; start += BatchCap)
        {
            int count = Math.Min(BatchCap, pending.Count - start);
            await EnsureSliceAsync(pending.GetRange(start, count), ct).ConfigureAwait(false);
        }
    }

    async Task EnsureSliceAsync(List<string> uris, CancellationToken ct)
    {
        var reqs = new List<(string Uri, Xm.ExtensionKind Kind, string? Etag)>(uris.Count * 3);
        foreach (var uri in uris)
        {
            reqs.Add((uri, Xm.ExtensionKind.VisualIdentityTrait, null));
            reqs.Add((uri, Xm.ExtensionKind.AudioAttributesV2, null));
            // Kind 6 rides the SAME batch as the colour and tempo: it is the descriptor plane behind Liked Songs'
            // content-filter chips, and fetching it separately would double the request count for the same rows.
            reqs.Add((uri, Xm.ExtensionKind.TrackDescriptor, null));
        }

        if (_extensions is not null)
        {
            IReadOnlyDictionary<(string Uri, Xm.ExtensionKind Kind), CachedExtension> cached;
            try
            {
                cached = await _extensions.GetAsync(reqs.ConvertAll(x => (x.Uri, x.Kind)), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                _log.Event(WaveeLogLevel.Warning, "adorn.cache.fail", "cached adornment read failed", ex: ex);
                return;
            }

            using var bulkCached = _store.BeginBulk();
            foreach (var uri in uris)
            {
                cached.TryGetValue((uri, Xm.ExtensionKind.VisualIdentityTrait), out var vis);
                cached.TryGetValue((uri, Xm.ExtensionKind.AudioAttributesV2), out var aud);
                cached.TryGetValue((uri, Xm.ExtensionKind.TrackDescriptor), out var desc);
                Apply(uri, vis?.Missing == true ? null : vis?.Payload, aud?.Missing == true ? null : aud?.Payload,
                      desc?.Missing == true ? null : desc?.Payload);
            }
            return;
        }

        IReadOnlyDictionary<(string Uri, Xm.ExtensionKind Kind), ExtendedMetadataSource.ExtensionResult> results;
        try { results = await _metadata.GetExtensionsWithHeadersAsync(reqs, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _log.Event(WaveeLogLevel.Warning, "adorn.fetch.fail", "adornment batch failed", ex: ex,
                fields: [WaveeLogField.Of("uris", uris.Count)]);
            return;
        }

        // One bulk scope so N per-track upserts coalesce into a single change signal — a realizing list must not emit
        // 300 separate store bumps.
        using var bulk = _store.BeginBulk();
        foreach (var uri in uris)
        {
            results.TryGetValue((uri, Xm.ExtensionKind.VisualIdentityTrait), out var vis);
            results.TryGetValue((uri, Xm.ExtensionKind.AudioAttributesV2), out var aud);
            results.TryGetValue((uri, Xm.ExtensionKind.TrackDescriptor), out var desc);
            Apply(uri, vis.Payload, aud.Payload, desc.Payload);
        }
    }

    void Apply(string uri, ByteString? visual, ByteString? audio, ByteString? descriptors)
    {
        bool colored = FeedColors(uri, visual);
        var (bpm, key, camelot, camelotColor) = ParseAudio(uri, audio);
        var tags = ParseTags(uri, descriptors);

        if (!colored && bpm is null && key is null && tags is null)
        {
            // Nothing came back for this track on either plane. Remember it so the next list realize does not re-ask.
            if (_noAdornment.Count < MaxNegative) _noAdornment.TryAdd(uri, 0);
            return;
        }

        if (bpm is null && key is null && tags is null) return;   // colour landed in the plane; no row to rewrite

        var current = _store.GetTrack(uri);
        if (current is null) return;   // adornments never CREATE a row — they decorate one the list already has

        _store.UpsertTrack(current with
        {
            TempoBpm = bpm ?? current.TempoBpm,
            MusicalKey = key ?? current.MusicalKey,
            CamelotCode = camelot ?? current.CamelotCode,
            CamelotColor = camelotColor ?? current.CamelotColor,
            Tags = tags ?? current.Tags,
        });
    }

    /// <summary>Kind 179 → <see cref="CoverColorPlane"/>, keyed by the image URLs THE PAYLOAD ITSELF carries rather
    /// than by this entity's uri. That pairing is the whole point of the trait: one track's response also tints its
    /// album's grid card, its playlist's hero and every other slot that shows the same cover, and it lands before a
    /// single image byte does.
    ///
    /// Takes <c>colors.base.background_base</c> — NOT <c>colors.flat</c>, which is a light desaturated accent
    /// (#ACB8F5 for a navy cover) where background_base is the dominant tone (#101040) an art placeholder wants.
    /// The schemes are DARK-only (base/darker/darkest are elevation levels — see visual_identity_trait.proto), so the
    /// plane files this as a dark grading and light theme waits for getDynamicColorsByUris.</summary>
    bool FeedColors(string uri, ByteString? payload)
    {
        if (payload is null || payload.IsEmpty) return false;
        if (CoverColorPlane.Current is not { } plane) return false;
        try
        {
            var identity = VisualParser.ParseFrom(payload)?.VisualIdentity;
            var scheme = identity?.Colors?.Base;
            if (identity is null || scheme?.BackgroundBase is null) return false;

            var graded = new CoverColorPlane.Scheme(
                Pack(scheme.BackgroundBase), Pack(scheme.BackgroundTintedBase), Pack(scheme.TextBase),
                Pack(scheme.TextSubdued), Pack(scheme.TextBrightAccent));

            bool any = false;
            foreach (var entry in identity.Images)
            {
                if (entry?.Image?.Url is not { Length: > 0 } url) continue;
                plane.SetDark(url, graded);
                any = true;
            }
            return any;
        }
        catch (InvalidProtocolBufferException ex)
        {
            // One malformed entity must not sink the batch — the other 299 rows still get their colour.
            _log.Event(WaveeLogLevel.Warning, "adorn.179.parse", "visual-identity parse failed", uri, ex: ex);
            return false;
        }
    }

    /// <summary>Descriptor tags (kind 6). Takes <c>display_name</c> — the presentation form ("K-Pop") — falling back to
    /// the lowercase match token when the server omits it. Wire order is descending weight, so the first N are the
    /// strongest; no re-sorting.</summary>
    IReadOnlyList<string>? ParseTags(string uri, ByteString? payload)
    {
        if (payload is null || payload.IsEmpty) return null;
        try
        {
            var data = DescriptorParser.ParseFrom(payload);
            if (data is null || data.Descriptors.Count == 0) return null;
            var list = new List<string>(Math.Min(data.Descriptors.Count, MaxTagsPerTrack));
            foreach (var d in data.Descriptors)
            {
                if (list.Count >= MaxTagsPerTrack) break;
                string label = d.DisplayName is { Length: > 0 } dn ? dn : d.Text;
                if (label.Length > 0) list.Add(label);
            }
            return list.Count > 0 ? list : null;
        }
        catch (InvalidProtocolBufferException ex)
        {
            _log.Event(WaveeLogLevel.Warning, "adorn.6.parse", "descriptor parse failed", uri, ex: ex);
            return null;
        }
    }

    (double? Bpm, string? Key, string? Camelot, uint? CamelotColor) ParseAudio(string uri, ByteString? payload)
    {
        if (payload is null || payload.IsEmpty) return (null, null, null, null);
        try
        {
            var attrs = AudioParser.ParseFrom(payload);
            if (attrs is null) return (null, null, null, null);

            // tempo is a DOUBLE on the wire. A 0 tempo is "unknown", not "silent" — never surface it as 0 BPM.
            double? bpm = attrs.Tempo > 0d ? attrs.Tempo : null;
            string? key = attrs.Key is { Name.Length: > 0 } k ? k.Name : null;
            string? camelot = attrs.Key?.Camelot is { Code.Length: > 0 } c ? c.Code : null;
            uint? colour = ParseHexColor(attrs.Key?.Camelot?.Color);
            return (bpm, key, camelot, colour);
        }
        catch (InvalidProtocolBufferException ex)
        {
            _log.Event(WaveeLogLevel.Warning, "adorn.222.parse", "audio-attributes parse failed", uri, ex: ex);
            return (null, null, null, null);
        }
    }

    // Opaque ARGB, the packing CoverColorPlane stores. SpotifyColor owns the clamping + the
    // alpha-0-means-unspecified rule so every surface packs colour identically.
    /// <summary>A role's RGBA → opaque ARGB; 0 when the server omitted that role (the plane treats 0 as "absent").</summary>
    static uint Pack(Va.Rgba? c) => c is null ? 0u : SpotifyColor.Pack(c.R, c.G, c.B, c.A);

    /// <summary>"#56d9f8" → opaque ARGB. Delegates to the shared parser; kept as a named member so the Camelot colour
    /// path reads clearly at the call site.</summary>
    internal static uint? ParseHexColor(string? hex) => SpotifyColor.FromHex(hex);
}
