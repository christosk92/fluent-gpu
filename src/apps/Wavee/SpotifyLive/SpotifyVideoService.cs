using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using Wavee.Backend;
using Wavee.Backend.Metadata;
using Wavee.Core;
using M = Wavee.Protocol.Metadata;
using Xm = Wavee.Protocol.ExtendedMetadata;

namespace Wavee.SpotifyLive;

/// <summary>The live music-video data layer. Detects whether a track has a video and caches the video↔audio file-id map
/// over the SHARED <see cref="ExtendedMetadataSource"/> (VIDEO_ASSOCIATIONS = 99, sent with the cached etag so the server
/// can 304). Results are projected into the persistent <see cref="IStore"/> (a side table) and flip <c>Track.HasVideo</c>
/// so the list indicator lights up. DATA ONLY — it never resolves/plays the video (those files are DRM-protected and
/// resolve over their own route; this stops at caching the file id). Reverse mapping (AUDIO_ASSOCIATIONS, video→audio)
/// is a follow-up tied to the deferred player swap.</summary>
sealed partial class SpotifyVideoService : IVideoService
{
    // After this long we revalidate (cheap — the request carries the etag, so an unchanged entity comes back 304).
    static readonly TimeSpan RevalidateAfter = TimeSpan.FromHours(6);
    // Desktop never asks for more than ~300 entities per VIDEO_ASSOCIATIONS batch. Our transport does NOT bound this for
    // us — MetadataChunking splits by BYTES only and ExtensionEtagCache takes the whole list — so a 10k-track playlist
    // would otherwise go out as one request body. Sliced here, at the single chokepoint every caller funnels through.
    const int DetectBatchCap = 300;
    static readonly MessageParser<Xm.VideoAssociations> AssocParser = Xm.VideoAssociations.Parser.WithDiscardUnknownFields(true);

    readonly ExtendedMetadataSource _metadata;
    readonly ExtensionEtagCache? _extensions;
    readonly IStore _store;
    readonly WaveeLogger _log;
    readonly ConcurrentDictionary<string, Task<VideoAssociation?>> _inflight = new(StringComparer.Ordinal);

    public SpotifyVideoService(ExtendedMetadataSource metadata, IStore store, WaveeLogger log = default, ExtensionEtagCache? extensions = null)
    {
        _metadata = metadata;
        _extensions = extensions;
        _store = store;
        _log = log;
    }

    public async Task DetectAsync(IReadOnlyList<string> trackUris, CancellationToken ct = default)
    {
        if (trackUris.Count == 0) return;
        var now = DateTimeOffset.UtcNow;
        var pending = new List<string>(trackUris.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var uri in trackUris)
        {
            if (!uri.StartsWith("spotify:track:", StringComparison.Ordinal) || !seen.Add(uri)) continue;
            var cached = _store.GetVideoAssociation(uri);
            if (cached is not null && IsFresh(cached, now)) continue;   // fresh → skip the network entirely
            pending.Add(uri);
        }
        if (pending.Count == 0) return;

        for (int start = 0; start < pending.Count && !ct.IsCancellationRequested; start += DetectBatchCap)
            await DetectSliceAsync(pending.GetRange(start, Math.Min(DetectBatchCap, pending.Count - start)), now, ct)
                .ConfigureAwait(false);
    }

    async Task DetectSliceAsync(List<string> uris, DateTimeOffset now, CancellationToken ct)
    {
        var reqs = new List<(string Uri, Xm.ExtensionKind Kind, string? Etag)>(uris.Count * 2);
        foreach (var uri in uris)
        {
            reqs.Add((uri, Xm.ExtensionKind.VideoAssociations, _store.GetVideoAssociation(uri)?.Etag));
            // CONSUMPTION_EXPERIENCE_TRAIT (182, ~10 B/track) rides the SAME entity request — GzipExtensionRequest groups
            // kinds under one EntityRequest, so this costs no extra round-trip. It is the gate for canonical recovery.
            reqs.Add((uri, Xm.ExtensionKind.ConsumptionExperienceTrait, null));
        }

        if (_extensions is not null)
        {
            IReadOnlyDictionary<(string Uri, Xm.ExtensionKind Kind), CachedExtension> cached;
            try
            {
                cached = await _extensions.GetAsync(
                    reqs.ConvertAll(x => (x.Uri, x.Kind)),
                    ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { _log.Info("VIDEO_ASSOCIATIONS detect: " + ex.Message); return; }

            List<string>? recoverCached = null;
            using (var bulkCached = _store.BeginBulk())
                foreach (var uri in uris)
                {
                    if (cached.TryGetValue((uri, Xm.ExtensionKind.VideoAssociations), out var res))
                        Apply(uri, res, now);
                    if (NeedsRecovery(uri)
                        && cached.TryGetValue((uri, Xm.ExtensionKind.ConsumptionExperienceTrait), out var ce)
                        && !ce.Missing && CeHasVideo(ce.Payload))
                        (recoverCached ??= new List<string>()).Add(uri);
                }
            if (recoverCached is not null) await RecoverCanonicalAsync(recoverCached, now, ct).ConfigureAwait(false);
            return;
        }

        IReadOnlyDictionary<(string Uri, Xm.ExtensionKind Kind), ExtendedMetadataSource.ExtensionResult> results;
        try { results = await _metadata.GetExtensionsWithHeadersAsync(reqs, ct).ConfigureAwait(false); }
        catch (Exception ex) when (ex is not OperationCanceledException) { _log.Info("VIDEO_ASSOCIATIONS detect: " + ex.Message); return; }

        List<string>? recover = null;
        using (var bulk = _store.BeginBulk())   // coalesce the per-track HasVideo bumps into one change signal
            foreach (var uri in uris)
            {
                if (results.TryGetValue((uri, Xm.ExtensionKind.VideoAssociations), out var res))
                    Apply(uri, res, now);
                if (NeedsRecovery(uri)
                    && results.TryGetValue((uri, Xm.ExtensionKind.ConsumptionExperienceTrait), out var ce)
                    && CeHasVideo(ce.Payload))
                    (recover ??= new List<string>()).Add(uri);
            }
        if (recover is not null) await RecoverCanonicalAsync(recover, now, ct).ConfigureAwait(false);
    }

    // A kind-99 miss that kind 182 contradicts is the alias/relinked-id case (kind 99 is keyed by the CANONICAL id and
    // 404s on an alias; 182 is canonical-computed, so it still reports the video).
    bool NeedsRecovery(string uri) => _store.GetVideoAssociation(uri) is null or { HasVideo: false };

    /// <summary>Canonical-id recovery for alias/relinked track ids. For each alias: TrackV4 → <c>canonical_uri</c>
    /// (field 36 — the FULL <c>Metadata.Track</c>, not <c>LeanTrack</c>, which drops it) plus PLAYBACK_TRAIT (212) for
    /// the associated video's gid in ONE batch, then one kind-99 batch on the canonical ids. Everything lands under the
    /// REQUESTED (alias) uri — the store indexes <c>a.Uri</c> and every consumer looks up the uri it rendered/played.</summary>
    async Task RecoverCanonicalAsync(IReadOnlyList<string> aliases, DateTimeOffset now, CancellationToken ct)
    {
        var reqs = new List<(string Uri, Xm.ExtensionKind Kind, string? Etag)>(aliases.Count * 2);
        foreach (var alias in aliases)
        {
            reqs.Add((alias, Xm.ExtensionKind.TrackV4, null));
            reqs.Add((alias, Xm.ExtensionKind.PlaybackTrait, null));   // field 2 → the associated video's 16-byte gid
        }

        IReadOnlyDictionary<(string Uri, Xm.ExtensionKind Kind), ExtendedMetadataSource.ExtensionResult> canon;
        try { canon = await _metadata.GetExtensionsWithHeadersAsync(reqs, ct).ConfigureAwait(false); }
        catch (Exception ex) when (ex is not OperationCanceledException) { _log.Info("VIDEO_ASSOCIATIONS canonical: " + ex.Message); return; }

        var pairs = new List<(string Alias, string Canonical, string? GidHex)>(aliases.Count);
        var second = new List<(string Uri, Xm.ExtensionKind Kind, string? Etag)>(aliases.Count);
        var asked = new HashSet<string>(StringComparer.Ordinal);
        foreach (var alias in aliases)
        {
            string? canonical = canon.TryGetValue((alias, Xm.ExtensionKind.TrackV4), out var tv) ? CanonicalUri(tv.Payload) : null;
            if (canonical is null || string.Equals(canonical, alias, StringComparison.Ordinal)) continue;
            string? gid = canon.TryGetValue((alias, Xm.ExtensionKind.PlaybackTrait), out var pb) ? AssociatedVideoGid(pb.Payload) : null;
            pairs.Add((alias, canonical, gid));
            if (asked.Add(canonical)) second.Add((canonical, Xm.ExtensionKind.VideoAssociations, null));
        }
        if (pairs.Count == 0) return;

        IReadOnlyDictionary<(string Uri, Xm.ExtensionKind Kind), ExtendedMetadataSource.ExtensionResult> recovered;
        try { recovered = await _metadata.GetExtensionsWithHeadersAsync(second, ct).ConfigureAwait(false); }
        catch (Exception ex) when (ex is not OperationCanceledException) { _log.Info("VIDEO_ASSOCIATIONS canonical: " + ex.Message); return; }

        using var bulk = _store.BeginBulk();
        foreach (var (alias, canonical, gid) in pairs)
        {
            if (!recovered.TryGetValue((canonical, Xm.ExtensionKind.VideoAssociations), out var res)) continue;
            if (!ApplyRecovered(alias, res, now, gid)) continue;
            // The alias's own 404 collapsed to a 24 h `Missing` in the SHARED extension cache; drop it so the next
            // detect re-runs this recovery instead of re-serving (and re-clearing on) the cached miss.
            _extensions?.MarkStale(alias, Xm.ExtensionKind.VideoAssociations);
            _log.Debug($"[video] canonical recovery {alias} → {canonical} hasVideo=true gid={gid ?? "-"}");
        }
    }

    public async Task<VideoAssociation?> GetAsync(string trackUri, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(trackUri) || !trackUri.StartsWith("spotify:track:", StringComparison.Ordinal)) return null;
        var cached = _store.GetVideoAssociation(trackUri);
        if (cached is not null && IsFresh(cached, DateTimeOffset.UtcNow)) return cached;

        // Coalesce concurrent single fetches for the same uri (the batch DetectAsync is the bulk path).
        var task = _inflight.GetOrAdd(trackUri, u => FetchOneAsync(u, cached?.Etag, ct));
        try { return await task.ConfigureAwait(false); }
        finally { _inflight.TryRemove(trackUri, out _); }
    }

    async Task<VideoAssociation?> FetchOneAsync(string uri, string? etag, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        try
        {
            if (_extensions is not null)
            {
                var cached = await _extensions.GetAsync(
                    new[] { (uri, Xm.ExtensionKind.VideoAssociations), (uri, Xm.ExtensionKind.ConsumptionExperienceTrait) }, ct)
                    .ConfigureAwait(false);
                if (cached.TryGetValue((uri, Xm.ExtensionKind.VideoAssociations), out var ext))
                    Apply(uri, ext, now);
                if (NeedsRecovery(uri)
                    && cached.TryGetValue((uri, Xm.ExtensionKind.ConsumptionExperienceTrait), out var ceCached)
                    && !ceCached.Missing && CeHasVideo(ceCached.Payload))
                    await RecoverCanonicalAsync(new[] { uri }, now, ct).ConfigureAwait(false);
                return _store.GetVideoAssociation(uri);
            }

            var results = await _metadata.GetExtensionsWithHeadersAsync(
                new[]
                {
                    (uri, Xm.ExtensionKind.VideoAssociations, etag),
                    (uri, Xm.ExtensionKind.ConsumptionExperienceTrait, (string?)null),
                }, ct).ConfigureAwait(false);
            if (results.TryGetValue((uri, Xm.ExtensionKind.VideoAssociations), out var wire))
                Apply(uri, wire, now);
            if (NeedsRecovery(uri)
                && results.TryGetValue((uri, Xm.ExtensionKind.ConsumptionExperienceTrait), out var ce)
                && CeHasVideo(ce.Payload))
                await RecoverCanonicalAsync(new[] { uri }, now, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { _log.Info("VIDEO_ASSOCIATIONS get: " + ex.Message); }
        return _store.GetVideoAssociation(uri);
    }

    // Fold one (uri, status) result into the cache and, on a positive, flip the track's HasVideo so the list shows it.
    void Apply(string uri, ExtendedMetadataSource.ExtensionResult res, DateTimeOffset now)
    {
        VideoAssociation? projected;
        try { projected = Project(uri, res, now); }
        catch (InvalidProtocolBufferException) { return; }   // skip one malformed entity, keep the batch
        if (projected is null) return;
        _store.UpsertVideoAssociation(projected);
        if (projected.HasVideo && _store.GetTrack(uri) is { HasVideo: false } t)
            _store.UpsertTrack(t with { HasVideo = true });   // merge ORs HasVideo → TrackRow movie icon
    }

    void Apply(string uri, CachedExtension res, DateTimeOffset now)
    {
        VideoAssociation? projected;
        try { projected = Project(uri, res, now); }
        catch (InvalidProtocolBufferException) { return; }
        if (projected is null) return;
        _store.UpsertVideoAssociation(projected);
        if (projected.HasVideo && _store.GetTrack(uri) is { HasVideo: false } t)
            _store.UpsertTrack(t with { HasVideo = true });
    }

    // Fold a CANONICAL entity's association under the ALIAS uri. Returns whether anything was recovered (only a real hit
    // counts — a canonical that also has no video leaves the alias's own cached miss alone).
    bool ApplyRecovered(string alias, ExtendedMetadataSource.ExtensionResult res, DateTimeOffset now, string? gidHex)
    {
        VideoAssociation? projected;
        try { projected = Project(alias, res, now); }
        catch (InvalidProtocolBufferException) { return false; }
        if (projected is not { HasVideo: true }) return false;
        // The etag belongs to the CANONICAL entity — never persist it against the alias, or the next detect would send
        // it as the alias's conditional and 304 us onto the wrong entity's freshness.
        _store.UpsertVideoAssociation(projected with { Etag = null, VideoGidHex = gidHex ?? projected.VideoGidHex });
        if (_store.GetTrack(alias) is { HasVideo: false } t)
            _store.UpsertTrack(t with { HasVideo = true });
        return true;
    }

    // Kind 182 (CONSUMPTION_EXPERIENCE_TRAIT): field 4 is a LENGTH-DELIMITED blob of small experience ids; the byte 0x02
    // is the music-video experience (live-probed — video tracks carry 01 02 04, plain audio 01 04). Hand-decoded: no
    // generated message for 182 ships here, and only this one bit is wanted.
    static bool CeHasVideo(ByteString? payload)
    {
        if (payload is null || payload.IsEmpty) return false;
        try
        {
            var input = payload.CreateCodedInput();
            uint tag;
            while ((tag = input.ReadTag()) != 0)
            {
                if (WireFormat.GetTagFieldNumber(tag) == 4 && WireFormat.GetTagWireType(tag) == WireFormat.WireType.LengthDelimited)
                {
                    var b = input.ReadBytes();
                    for (int i = 0; i < b.Length; i++) if (b.Span[i] == 0x02) return true;
                }
                else input.SkipLastField();
            }
        }
        catch (InvalidProtocolBufferException) { }
        return false;
    }

    // TrackV4 → canonical_uri (field 36). Parsed with the FULL Metadata.Track: the lean view used for bulk hydration
    // discards field 36, which is the whole point of this read.
    static string? CanonicalUri(ByteString? payload)
    {
        if (payload is null || payload.IsEmpty) return null;
        try
        {
            var t = M.Track.Parser.ParseFrom(payload);
            if (!t.HasCanonicalUri || t.CanonicalUri.Length == 0) return null;
            return t.CanonicalUri.StartsWith("spotify:", StringComparison.Ordinal) ? t.CanonicalUri
                 : t.CanonicalUri.Length == 22 ? "spotify:track:" + t.CanonicalUri
                 : null;
        }
        catch (InvalidProtocolBufferException) { return null; }
    }

    // Kind 212 (PLAYBACK_TRAIT): field 2 carries the associated video's 16-byte gid — either directly or one nesting
    // level in. That gid IS the video manifest_id / Connect's associated_video_id, so decoding it here saves the
    // resolve round-trip later.
    static string? AssociatedVideoGid(ByteString? payload)
    {
        if (payload is null || payload.IsEmpty) return null;
        try
        {
            var input = payload.CreateCodedInput();
            uint tag;
            while ((tag = input.ReadTag()) != 0)
            {
                if (WireFormat.GetTagFieldNumber(tag) == 2 && WireFormat.GetTagWireType(tag) == WireFormat.WireType.LengthDelimited)
                {
                    var b = input.ReadBytes();
                    if (b.Length == 16) return Convert.ToHexStringLower(b.Span);
                    if (NestedGid(b, depth: 3) is { } nested) return nested;
                }
                else input.SkipLastField();
            }
        }
        catch (InvalidProtocolBufferException) { }
        return null;
    }

    static string? NestedGid(ByteString msg, int depth)
    {
        try
        {
            var input = msg.CreateCodedInput();
            uint tag;
            while ((tag = input.ReadTag()) != 0)
            {
                if (WireFormat.GetTagWireType(tag) == WireFormat.WireType.LengthDelimited)
                {
                    var b = input.ReadBytes();
                    if (b.Length == 16) return Convert.ToHexStringLower(b.Span);
                    if (depth > 1 && b.Length > 1 && NestedGid(b, depth - 1) is { } nested) return nested;
                }
                else input.SkipLastField();
            }
        }
        catch (InvalidProtocolBufferException) { }
        return null;
    }

    VideoAssociation? Project(string uri, ExtendedMetadataSource.ExtensionResult res, DateTimeOffset now)
    {
        switch (res.Status)
        {
            case 200 when res.Payload is { } payload:
                var (counterpart, files) = ParseAssoc(payload);
                bool has = files.Count > 0 || !string.IsNullOrEmpty(counterpart);
                return new VideoAssociation(uri, has, counterpart, files, res.Etag, now, res.OfflineTtlSeconds);
            case 304:
                // Unchanged — keep the cached record, just refresh its freshness (and any rotated etag).
                var existing = _store.GetVideoAssociation(uri);
                return existing is null ? null : existing with { FetchedAt = now, Etag = res.Etag ?? existing.Etag };
            case 404:
            case 200:   // 200 with an empty payload ⇒ no association
                return VideoAssociation.None(uri, res.Etag, now, res.OfflineTtlSeconds);
            default:
                return null;   // an error/odd status — leave any existing cache untouched
        }
    }

    VideoAssociation? Project(string uri, CachedExtension res, DateTimeOffset now)
    {
        if (res.Missing || res.Payload is null || res.Payload.IsEmpty)
            return VideoAssociation.None(uri, res.Etag, now, res.OfflineTtlSeconds);

        var (counterpart, files) = ParseAssoc(res.Payload);
        bool has = files.Count > 0 || !string.IsNullOrEmpty(counterpart);
        return new VideoAssociation(uri, has, counterpart, files, res.Etag, now, res.OfflineTtlSeconds);
    }

    static (string? Counterpart, IReadOnlyList<VideoFileRef> Files) ParseAssoc(ByteString payload)
    {
        var va = AssocParser.ParseFrom(payload);
        if (va.Association is not { } assoc) return (null, VideoAssociation.NoFiles);
        var files = new List<VideoFileRef>(assoc.Files?.File.Count ?? 0);
        if (assoc.Files is { } group)
            foreach (var f in group.File)
            {
                if (f.FileId.Length == 0) continue;
                files.Add(new VideoFileRef(Convert.ToHexStringLower(f.FileId.Span), f.Variant, f.Width, f.Height));
            }
        return (assoc.HasAssociatedUri ? assoc.AssociatedUri : null, files);
    }

    static bool IsFresh(VideoAssociation a, DateTimeOffset now) => now - a.FetchedAt < RevalidateAfter;
}
