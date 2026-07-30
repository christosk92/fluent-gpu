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
    // How long a cached verdict stands, and whether it revalidates conditionally, are properties of the ANSWER — a
    // positive is durable, a negative is a "not yet" — so they live on VideoAssociation, not in this fetcher.
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
        int notTrack = 0, freshPos = 0, freshNeg = 0;
        foreach (var uri in trackUris)
        {
            if (!uri.StartsWith("spotify:track:", StringComparison.Ordinal)) { notTrack++; continue; }
            if (!seen.Add(uri)) continue;
            var cached = _store.GetVideoAssociation(uri);
            if (cached is not null && cached.IsFresh(now))
            {
                if (cached.HasVideo) freshPos++; else freshNeg++;
                continue;   // fresh → skip the network entirely
            }
            pending.Add(uri);
        }
        // The REQUEST-side ledger (H2 = coverage). `pending` is what actually goes to the wire; `freshNeg` is the
        // 30-minute negative window suppressing a re-ask, and `notTrack` is a caller handing us non-track uris (the
        // discography hook does exactly that — every one of them is dropped here and only the adornment pass uses them).
        _log.Event(WaveeLogLevel.Debug, "video.assoc.request", "kind-99 detect batch admitted",
            fields:
            [
                WaveeLogField.Of("asked", trackUris.Count), WaveeLogField.Of("distinctTracks", seen.Count),
                WaveeLogField.Of("notTrackUri", notTrack), WaveeLogField.Of("freshPos", freshPos),
                WaveeLogField.Of("freshNeg", freshNeg), WaveeLogField.Of("pending", pending.Count),
                WaveeLogField.Of("kinds", "99+182"), WaveeLogField.Of("etagCache", _extensions is not null),
            ]);
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
            reqs.Add((uri, Xm.ExtensionKind.VideoAssociations, _store.GetVideoAssociation(uri)?.RevalidationEtag));
            // CONSUMPTION_EXPERIENCE_TRAIT (182, ~10 B/track) rides the SAME entity request — GzipExtensionRequest groups
            // kinds under one EntityRequest, so this costs no extra round-trip. It is the gate for canonical recovery.
            reqs.Add((uri, Xm.ExtensionKind.ConsumptionExperienceTrait, null));
        }

        var tally = new DetectTally();

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
                    bool wire = cached.TryGetValue((uri, Xm.ExtensionKind.VideoAssociations), out var res);
                    if (wire) Apply(uri, res!, now);
                    // `Missing` is the SEALED-NEGATIVE case (H3): the shared extension cache turned a 404 / an empty 200 /
                    // an entity the response simply omitted into a 24 h miss, and the fold above wrote a 30-minute
                    // `VideoAssociation.None` from it. Counted separately from "no wire entry at all".
                    bool needs = NeedsRecovery(uri);
                    bool ceRow = false, ceVideo = false;
                    if (needs)   // kind 182 is only ever consulted for a kind-99 miss — keep it that way
                    {
                        ceRow = cached.TryGetValue((uri, Xm.ExtensionKind.ConsumptionExperienceTrait), out var ce) && !ce!.Missing;
                        ceVideo = ceRow && CeHasVideo(ce!.Payload);
                        if (ceVideo) (recoverCached ??= new List<string>()).Add(uri);
                    }
                    tally.Fold(uri, wire, wire && res!.Missing, _store.GetVideoAssociation(uri), needs, ceRow, ceVideo);
                }
            LogSlice(uris.Count, tally, "etag-cache", recoverCached?.Count ?? 0);
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
                bool wire = results.TryGetValue((uri, Xm.ExtensionKind.VideoAssociations), out var res);
                if (wire) Apply(uri, res, now);
                bool needs = NeedsRecovery(uri);
                bool ceRow = false, ceVideo = false;
                if (needs)
                {
                    ceRow = results.TryGetValue((uri, Xm.ExtensionKind.ConsumptionExperienceTrait), out var ce);
                    ceVideo = ceRow && CeHasVideo(ce.Payload);
                    if (ceVideo) (recover ??= new List<string>()).Add(uri);
                }
                // The raw path's own sealed negative is a 404 or an empty 200 (Project → VideoAssociation.None); an odd
                // status leaves the plane untouched, which is why it is NOT folded in as `sealed`.
                tally.Fold(uri, wire, wire && res.Status is 404 or 200 && res.Payload is null or { IsEmpty: true },
                    _store.GetVideoAssociation(uri), needs, ceRow, ceVideo);
            }
        LogSlice(uris.Count, tally, "direct", recover?.Count ?? 0);
        if (recover is not null) await RecoverCanonicalAsync(recover, now, ct).ConfigureAwait(false);
    }

    // ── the detect ledger ─────────────────────────────────────────────────────────────────────────────────────────────
    // One line per wire slice, answering the four questions the "playlist says no video, search says it does" report
    // splits into: did we ASK for these uris (the request line above), did a kind-99 row COME BACK, was the verdict a
    // real negative or a sealed miss (H3), and did the alias/relink recovery gate (kind 182) even fire (H1).
    sealed class DetectTally
    {
        const int SampleCap = 4;

        public int WireRow, WireAbsent, WireSealed;      // kind-99 response shape
        public int Positive, Negative, NoRow;            // the resulting plane state, read back after the fold
        public int CeGate, CeNoRow, CeNoVideo;           // the kind-182 recovery gate, over the tracks that needed it
        readonly List<string> _pos = new(SampleCap);
        readonly List<string> _miss = new(SampleCap);

        public void Fold(string uri, bool wireRow, bool wireSealed, VideoAssociation? plane, bool needsRecovery,
                         bool ceRow, bool ceVideo)
        {
            if (wireRow) WireRow++; else WireAbsent++;
            if (wireSealed) WireSealed++;
            switch (plane)
            {
                case { HasVideo: true }: Positive++; Add(_pos, uri); break;
                case not null: Negative++; Add(_miss, uri); break;
                default: NoRow++; Add(_miss, uri); break;
            }
            if (!needsRecovery) return;
            if (ceVideo) CeGate++;
            else if (ceRow) CeNoVideo++;
            else CeNoRow++;
        }

        public string PositiveSample => Join(_pos);
        public string MissSample => Join(_miss);

        static void Add(List<string> to, string uri) { if (to.Count < SampleCap) to.Add(Id(uri)); }
        static string Id(string uri) => uri.Length > 14 ? uri[14..] : uri;   // drop the "spotify:track:" prefix
        static string Join(List<string> ids) => ids.Count == 0 ? "-" : string.Join(",", ids);
    }

    void LogSlice(int requested, DetectTally t, string path, int queuedForRecovery)
        => _log.Event(WaveeLogLevel.Debug, "video.assoc.detect", "kind-99 slice folded into the association plane",
            fields:
            [
                WaveeLogField.Of("path", path), WaveeLogField.Of("requested", requested),
                WaveeLogField.Of("wireRow", t.WireRow), WaveeLogField.Of("wireAbsent", t.WireAbsent),
                WaveeLogField.Of("wireSealed", t.WireSealed),
                WaveeLogField.Of("positive", t.Positive), WaveeLogField.Of("negative", t.Negative),
                WaveeLogField.Of("noRow", t.NoRow),
                WaveeLogField.Of("ceGate", t.CeGate), WaveeLogField.Of("ceNoRow", t.CeNoRow),
                WaveeLogField.Of("ceNoVideo", t.CeNoVideo), WaveeLogField.Of("recoverQueued", queuedForRecovery),
                WaveeLogField.Of("posIds", t.PositiveSample), WaveeLogField.Of("missIds", t.MissSample),
            ]);

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
        int noCanonical = 0, selfCanonical = 0;
        _recoverLinesLogged = 0;   // per-pass budget for the per-alias detail (the summary always fires)
        foreach (var alias in aliases)
        {
            string? canonical = canon.TryGetValue((alias, Xm.ExtensionKind.TrackV4), out var tv) ? CanonicalUri(tv.Payload) : null;
            if (canonical is null) { noCanonical++; LogRecover(alias, null, null, "no-canonical-uri"); continue; }
            if (string.Equals(canonical, alias, StringComparison.Ordinal))
            { selfCanonical++; LogRecover(alias, canonical, null, "canonical-is-self"); continue; }
            string? gid = canon.TryGetValue((alias, Xm.ExtensionKind.PlaybackTrait), out var pb) ? AssociatedVideoGid(pb.Payload) : null;
            pairs.Add((alias, canonical, gid));
            if (asked.Add(canonical)) second.Add((canonical, Xm.ExtensionKind.VideoAssociations, null));
        }
        if (pairs.Count == 0)
        {
            // Nothing to try. This is the H1 dead end the old code hit SILENTLY: kind 182 said "there is a video" but
            // TrackV4 offered no canonical_uri to ask kind 99 about, so the alias keeps its negative and its row stays dark.
            LogRecoverSummary(aliases.Count, 0, noCanonical, selfCanonical, 0, 0, 0);
            return;
        }

        IReadOnlyDictionary<(string Uri, Xm.ExtensionKind Kind), ExtendedMetadataSource.ExtensionResult> recovered;
        try { recovered = await _metadata.GetExtensionsWithHeadersAsync(second, ct).ConfigureAwait(false); }
        catch (Exception ex) when (ex is not OperationCanceledException) { _log.Info("VIDEO_ASSOCIATIONS canonical: " + ex.Message); return; }

        int ok = 0, canonicalNoRow = 0, canonicalNoVideo = 0;
        using (var bulk = _store.BeginBulk())
            foreach (var (alias, canonical, gid) in pairs)
            {
                if (!recovered.TryGetValue((canonical, Xm.ExtensionKind.VideoAssociations), out var res))
                { canonicalNoRow++; LogRecover(alias, canonical, gid, "canonical-no-kind99-row"); continue; }
                if (!ApplyRecovered(alias, res, now, gid))
                { canonicalNoVideo++; LogRecover(alias, canonical, gid, "canonical-no-video status=" + res.Status); continue; }
                // Stamp the derived canonical onto the resident alias row (adornment-service pattern) so miss-bridges
                // and later detects do not re-parse TrackV4. Previously discarded after use.
                if (_store.GetTrack(alias) is { } row)
                    _store.UpsertTrack(row with { CanonicalUri = canonical });
                // The alias's own 404 collapsed to a 24 h `Missing` in the SHARED extension cache; drop it so the next
                // detect re-runs this recovery instead of re-serving (and re-clearing on) the cached miss.
                _extensions?.MarkStale(alias, Xm.ExtensionKind.VideoAssociations);
                ok++;
                LogRecover(alias, canonical, gid, "recovered");
            }
        LogRecoverSummary(aliases.Count, pairs.Count, noCanonical, selfCanonical, ok, canonicalNoRow, canonicalNoVideo);
    }

    // Per-alias recovery verdict. Bounded: a slice can carry up to 300 aliases and the per-alias detail is only useful
    // for the first handful — the summary below carries the totals.
    int _recoverLinesLogged;
    const int RecoverLineCap = 24;

    void LogRecover(string alias, string? canonical, string? gid, string verdict)
    {
        if (_recoverLinesLogged >= RecoverLineCap) return;
        _recoverLinesLogged++;
        _log.Event(WaveeLogLevel.Debug, "video.assoc.recover", "alias → canonical kind-99 recovery",
            fields:
            [
                WaveeLogField.Of("alias", alias), WaveeLogField.Of("canonical", canonical ?? "-"),
                WaveeLogField.Of("gid", gid ?? "-"), WaveeLogField.Of("verdict", verdict),
            ]);
    }

    // THE H1 line. `aliases` is how many kind-99 misses kind 182 contradicted (i.e. relink suspects); everything after it
    // is where each suspect ended up. `recovered > 0` means the relink path is working; a large `noCanonicalUri` or
    // `canonicalNoKind99Row` means it is not, and the playlist row will stay dark while the canonical uri search returns
    // shows a video.
    void LogRecoverSummary(int aliases, int pairs, int noCanonicalUri, int canonicalIsSelf, int recovered,
                           int canonicalNoRow, int canonicalNoVideo)
        => _log.Event(WaveeLogLevel.Info, "video.assoc.recover.done", "relink recovery pass finished",
            fields:
            [
                WaveeLogField.Of("suspects", aliases), WaveeLogField.Of("pairs", pairs),
                WaveeLogField.Of("noCanonicalUri", noCanonicalUri), WaveeLogField.Of("canonicalIsSelf", canonicalIsSelf),
                WaveeLogField.Of("recovered", recovered), WaveeLogField.Of("canonicalNoKind99Row", canonicalNoRow),
                WaveeLogField.Of("canonicalNoVideo", canonicalNoVideo),
            ]);

    public async Task<VideoAssociation?> GetAsync(string trackUri, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(trackUri) || !trackUri.StartsWith("spotify:track:", StringComparison.Ordinal)) return null;
        var cached = _store.GetVideoAssociation(trackUri);
        if (cached is not null && cached.IsFresh(DateTimeOffset.UtcNow)) return cached;

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

    // Fold one (uri, status) result into the plane. There is nothing to mirror onto the track row: the association IS
    // the has-video answer every surface reads (VideoPresence), so a row indicator cannot disagree with the record the
    // fetch just produced — which is what used to happen when the two lived in different places.
    void Apply(string uri, ExtendedMetadataSource.ExtensionResult res, DateTimeOffset now) => Fold(_store, uri, res, now);

    /// <summary>THE kind-99 fold, shared by every fetcher of the association (the detect batch here, the single-track
    /// resolve, and <see cref="SpotifyTrackExpansionService"/>'s drawer fetch). One fold is what makes the row
    /// indicator and the expand drawer structurally unable to disagree: whichever path fetched the payload, the same
    /// projection lands in the same plane — so expanding a row that showed no video HEALS the row the moment the
    /// drawer's own fetch comes back.</summary>
    internal static void Fold(IStore store, string uri, ExtendedMetadataSource.ExtensionResult res, DateTimeOffset now)
    {
        VideoAssociation? projected;
        try { projected = Project(store, uri, res, now); }
        catch (InvalidProtocolBufferException) { return; }   // skip one malformed entity, keep the batch
        if (projected is not null) store.UpsertVideoAssociation(projected);
    }

    void Apply(string uri, CachedExtension res, DateTimeOffset now)
    {
        VideoAssociation? projected;
        try { projected = Project(uri, res, now); }
        catch (InvalidProtocolBufferException) { return; }
        if (projected is not null) _store.UpsertVideoAssociation(projected);
    }

    // Fold a CANONICAL entity's association under the ALIAS uri. Returns whether anything was recovered (only a real hit
    // counts — a canonical that also has no video leaves the alias's own cached miss alone).
    bool ApplyRecovered(string alias, ExtendedMetadataSource.ExtensionResult res, DateTimeOffset now, string? gidHex)
    {
        VideoAssociation? projected;
        try { projected = Project(_store, alias, res, now); }
        catch (InvalidProtocolBufferException) { return false; }
        if (projected is not { HasVideo: true }) return false;
        // The etag belongs to the CANONICAL entity — never persist it against the alias, or the next detect would send
        // it as the alias's conditional and 304 us onto the wrong entity's freshness.
        _store.UpsertVideoAssociation(projected with { Etag = null, VideoGidHex = gidHex ?? projected.VideoGidHex });
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

    static VideoAssociation? Project(IStore store, string uri, ExtendedMetadataSource.ExtensionResult res, DateTimeOffset now)
    {
        switch (res.Status)
        {
            case 200 when res.Payload is { } payload:
                var (counterpart, files) = ParseAssoc(payload);
                bool has = files.Count > 0 || !string.IsNullOrEmpty(counterpart);
                return new VideoAssociation(uri, has, counterpart, files, res.Etag, now, res.OfflineTtlSeconds);
            case 304:
                // Unchanged — keep the cached record, just refresh its freshness (and any rotated etag).
                var existing = store.GetVideoAssociation(uri);
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

}
