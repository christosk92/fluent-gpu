using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using Wavee.Backend;
using Wavee.Backend.Metadata;
using Wavee.Core;
using Xm = Wavee.Protocol.ExtendedMetadata;

namespace Wavee.SpotifyLive;

/// <summary>The live music-video data layer. Detects whether a track has a video and caches the video↔audio file-id map
/// over the SHARED <see cref="ExtendedMetadataSource"/> (VIDEO_ASSOCIATIONS = 99, sent with the cached etag so the server
/// can 304). Results are projected into the persistent <see cref="IStore"/> (a side table) and flip <c>Track.HasVideo</c>
/// so the list indicator lights up. DATA ONLY — it never resolves/plays the video (those files are DRM-protected and
/// resolve over their own route; this stops at caching the file id). Reverse mapping (AUDIO_ASSOCIATIONS, video→audio)
/// is a follow-up tied to the deferred player swap.</summary>
sealed class SpotifyVideoService : IVideoService
{
    // After this long we revalidate (cheap — the request carries the etag, so an unchanged entity comes back 304).
    static readonly TimeSpan RevalidateAfter = TimeSpan.FromHours(6);
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
        var reqs = new List<(string Uri, Xm.ExtensionKind Kind, string? Etag)>(trackUris.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var uri in trackUris)
        {
            if (!uri.StartsWith("spotify:track:", StringComparison.Ordinal) || !seen.Add(uri)) continue;
            var cached = _store.GetVideoAssociation(uri);
            if (cached is not null && IsFresh(cached, now)) continue;   // fresh → skip the network entirely
            reqs.Add((uri, Xm.ExtensionKind.VideoAssociations, cached?.Etag));
        }
        if (reqs.Count == 0) return;

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

            using var bulkCached = _store.BeginBulk();
            foreach (var (uri, _, _) in reqs)
                if (cached.TryGetValue((uri, Xm.ExtensionKind.VideoAssociations), out var res))
                    Apply(uri, res, now);
            return;
        }

        IReadOnlyDictionary<(string Uri, Xm.ExtensionKind Kind), ExtendedMetadataSource.ExtensionResult> results;
        try { results = await _metadata.GetExtensionsWithHeadersAsync(reqs, ct).ConfigureAwait(false); }
        catch (Exception ex) when (ex is not OperationCanceledException) { _log.Info("VIDEO_ASSOCIATIONS detect: " + ex.Message); return; }

        using var bulk = _store.BeginBulk();   // coalesce the per-track HasVideo bumps into one change signal
        foreach (var (uri, _, _) in reqs)
            if (results.TryGetValue((uri, Xm.ExtensionKind.VideoAssociations), out var res))
                Apply(uri, res, now);
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
                var cached = await _extensions.GetAsync(new[] { (uri, Xm.ExtensionKind.VideoAssociations) }, ct)
                    .ConfigureAwait(false);
                if (cached.TryGetValue((uri, Xm.ExtensionKind.VideoAssociations), out var ext))
                    Apply(uri, ext, now);
                return _store.GetVideoAssociation(uri);
            }

            var results = await _metadata.GetExtensionsWithHeadersAsync(
                new[] { (uri, Xm.ExtensionKind.VideoAssociations, etag) }, ct).ConfigureAwait(false);
            if (results.TryGetValue((uri, Xm.ExtensionKind.VideoAssociations), out var wire))
                Apply(uri, wire, now);
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
