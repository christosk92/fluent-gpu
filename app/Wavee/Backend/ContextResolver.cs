using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Wavee.Core;

namespace Wavee.Backend;

// ── Connect context resolution (the proto-free contract) ──────────────────────────────────────────────────────────────
// A Connect "play" command carries an OPAQUE context uri (spotify:album:…, :playlist:…, the artist :list:…,
// :user:…:collection, :station:…) + a skip_to target — NEVER a track list. Turning that uri into ordered tracks is one
// unified server call (GET /context-resolve/v1/{uri}); the live impl lives in SpotifyLive (JSON + HTTP), so this Backend
// contract stays proto-free + unit-testable. (Contrast WaveeMusic, which hand-rolls a 3-layer context cache + a
// retry/cooldown dict inside one 700-line ContextResolver; here the SWR + in-flight dedup is the shared Resource engine
// and metadata hydration is MetadataService — no bespoke caches.)

/// <summary>One track in a resolved/queued context: the domain <see cref="Track"/> plus its provider-assigned context
/// <c>uid</c> (skip_to-by-uid, the PutState player_state track.uid, outbound page uids). Uid is "" for synthetic /
/// user-queued items. A struct wrapping the already-heap-allocated Track — no extra per-track allocation.</summary>
public readonly record struct QueuedTrack(Track Track, string Uid)
{
    public string Uri => Track.Uri;
}

/// <summary>What an inbound play command asks us to play, parsed proto-free from the command envelope. <see cref="Url"/>
/// carries a collection's sort/filter query (collections are URI-only on the wire); <see cref="EmbeddedPages"/> is
/// non-null only when the command embedded a custom-ordered page (a sorted playlist) — then we play it verbatim instead
/// of resolving.</summary>
public readonly record struct ContextSpec(
    string Uri, string? Url,
    IReadOnlyList<QueuedRef>? EmbeddedPages,
    string? SkipToTrackUri, string? SkipToTrackUid, int? SkipToIndex)
{
    /// <summary>A UI/local play intent: just a context uri + an optional start index (no remote skip_to/pages).</summary>
    public static ContextSpec ForUri(string uri, int startIndex = 0) => new(uri, null, null, null, null, startIndex);
}

/// <summary>A bare (uri, uid) pair as it appears in a command's embedded context page — pre-hydration.</summary>
public readonly record struct QueuedRef(string Uri, string Uid);

/// <summary>One entry in an outbound set_queue snapshot. <c>IsQueued</c> ⇒ provider:"queue" + metadata{is_queued:"true"}
/// (a user-queued row); otherwise provider:"context" + metadata{} (a context-continuation row).</summary>
public readonly record struct QueueWireEntry(string Uri, string Uid, bool IsQueued);

/// <summary>The resolved context: ordered, metadata-hydrated tracks, where to start, and the paging/sort metadata the
/// publisher needs. Lean by design — only what the controller + PutState consume.</summary>
public readonly record struct ResolvedContext(
    IReadOnlyList<QueuedTrack> Tracks, int StartIndex,
    string? SortingCriteria, string? NextPageUrl, bool IsInfinite)
{
    public static readonly ResolvedContext Empty = new(Array.Empty<QueuedTrack>(), 0, null, null, false);
    public int Count => Tracks.Count;
}

/// <summary>Resolves an opaque context uri to ordered, metadata-hydrated tracks. ONE unified path for every context type
/// (the server decides order + sorting); pagination is lazy via <see cref="LoadMoreAsync"/>. The live impl is
/// SpotifyLive/LiveContextResolver; tests inject a fake.</summary>
public interface IContextResolver
{
    Task<ResolvedContext> ResolveAsync(ContextSpec spec, CancellationToken ct = default);
    Task<IReadOnlyList<QueuedTrack>> LoadMoreAsync(string nextPageUrl, CancellationToken ct = default);
    /// <summary>Hydrate loose track refs (add_to_queue / set_queue items) into queued tracks with display + duration metadata.</summary>
    Task<IReadOnlyList<QueuedTrack>> HydrateAsync(IReadOnlyList<QueuedRef> refs, CancellationToken ct = default);
}

/// <summary>Pure helpers shared by every IContextResolver impl (proto-free, alloc-free, unit-testable).</summary>
public static class ContextResolve
{
    /// <summary>Pick the start track in a resolved list: uid (handles sorted/filtered contexts) → uri → explicit index → 0.</summary>
    public static int FindStartIndex(IReadOnlyList<QueuedTrack> tracks, string? trackUri, string? trackUid, int? fallbackIndex)
    {
        if (!string.IsNullOrEmpty(trackUid))
            for (int i = 0; i < tracks.Count; i++) if (tracks[i].Uid == trackUid) return i;
        if (!string.IsNullOrEmpty(trackUri))
            for (int i = 0; i < tracks.Count; i++) if (tracks[i].Track.Uri == trackUri) return i;
        if (fallbackIndex is int fi && fi >= 0 && fi < tracks.Count) return fi;
        return 0;
    }

    /// <summary>True for algorithmic/endless contexts (station/radio/autoplay) — their end triggers autoplay, not Ended.</summary>
    public static bool IsInfinite(string uri) =>
        uri.Contains(":station:", StringComparison.Ordinal)
        || uri.Contains(":radio:", StringComparison.Ordinal)
        || uri.Contains(":autoplay", StringComparison.Ordinal);

    /// <summary>Collection contexts (Liked Songs) are URI-only on the wire — sort/filter rides on context.url.</summary>
    public static bool IsCollection(string uri) => uri.Contains(":collection", StringComparison.OrdinalIgnoreCase);

    /// <summary>A uri-only placeholder Track (duration 0) for a hydration miss / no-store fallback — preserves the uri so
    /// the queue/state and skip_to-by-uri stay valid even when metadata isn't available.</summary>
    public static Track Synthetic(string uri)
    {
        int i = uri.LastIndexOf(':');
        string id = i >= 0 && i + 1 < uri.Length ? uri[(i + 1)..] : uri;
        return new Track(id, uri, uri, Array.Empty<ArtistRef>(), new AlbumRef("", "", ""), 0, false, null);
    }
}

/// <summary>The offline / no-store fallback — every resolve yields nothing (the controller logs "0 tracks" and bails).
/// Wired when the real backend store isn't available (the in-memory fake backend / pre-login).</summary>
public sealed class EmptyContextResolver : IContextResolver
{
    public static readonly EmptyContextResolver Instance = new();
    public Task<ResolvedContext> ResolveAsync(ContextSpec spec, CancellationToken ct = default) => Task.FromResult(ResolvedContext.Empty);
    public Task<IReadOnlyList<QueuedTrack>> LoadMoreAsync(string nextPageUrl, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<QueuedTrack>>(Array.Empty<QueuedTrack>());
    public Task<IReadOnlyList<QueuedTrack>> HydrateAsync(IReadOnlyList<QueuedRef> refs, CancellationToken ct = default)
    {
        var arr = new QueuedTrack[refs.Count];
        for (int i = 0; i < refs.Count; i++) arr[i] = new QueuedTrack(ContextResolve.Synthetic(refs[i].Uri), refs[i].Uid);
        return Task.FromResult<IReadOnlyList<QueuedTrack>>(arr);
    }
}
