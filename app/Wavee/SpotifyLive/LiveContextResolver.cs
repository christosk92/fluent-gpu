using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Wavee.Backend;
using Wavee.Backend.Metadata;
using Wavee.Core;

namespace Wavee.SpotifyLive;

// ── The live context resolver (the proto-free IContextResolver impl) ──────────────────────────────────────────────────
// Maps an opaque context uri → ordered, hydrated tracks via ONE unified server call: GET /context-resolve/v1/{uri}. The
// FG mapping of WaveeMusic's 700-line ContextResolver:
//   • its 3 bespoke caches + retry/cooldown dict   → none here: the GET is cheap; the cost is the metadata, and that is
//                                                     already cached by MetadataService (SWR over the Resource engine).
//   • its protobuf JsonParser.Parse<Context>       → a streaming Utf8JsonReader (proto-free, no full-doc alloc), the same
//                                                     choice DealerFrameParser makes.
//   • its bespoke batched-metadata client          → MetadataService.SyncAllAsync (partial-cache-aware, batched, gzipped).
// The server decides order + sorting; we preserve it and apply skip_to (uid→uri→index) on top. Collections are URI-only
// on the wire — their sort/filter rides on context.url's query, which we forward verbatim.
public sealed class LiveContextResolver : IContextResolver
{
    // Eager-load at most this many pages on resolve; the rest are lazy via LoadMoreAsync (Phase B wires the queue refill).
    const int MaxEagerPages = 8;

    readonly ITransport _transport;
    readonly MetadataService _metadata;
    readonly IStore _store;
    readonly Action<string>? _log;

    public LiveContextResolver(ITransport transport, MetadataService metadata, IStore store, Action<string>? log = null)
    {
        _transport = transport;
        _metadata = metadata;
        _store = store;
        _log = log;
    }

    public async Task<ResolvedContext> ResolveAsync(ContextSpec spec, CancellationToken ct = default)
    {
        // 1) The command embedded a custom-ordered page (a sorted/filtered playlist sent inline) → play it verbatim.
        if (spec.EmbeddedPages is { Count: > 0 } embedded)
        {
            var hydratedEmbedded = await HydrateAsync(embedded, ct).ConfigureAwait(false);
            int s = ContextResolve.FindStartIndex(hydratedEmbedded, spec.SkipToTrackUri, spec.SkipToTrackUid, spec.SkipToIndex);
            return new ResolvedContext(hydratedEmbedded, s, null, null, ContextResolve.IsInfinite(spec.Uri));
        }

        // 2) Resolve via the unified context-resolve endpoint, eager-loading a bounded number of pages.
        var refs = new List<QueuedRef>();
        string? sorting = null, nextPage = null;
        var resp = await _transport.Request(Channel.Spclient, ResolvePath(spec), default, ct).ConfigureAwait(false);
        if (!resp.Ok || resp.Body is null || resp.Body.Length == 0)
        {
            _log?.Invoke($"context-resolve failed ({resp.Status}): {spec.Uri}");
            return ResolvedContext.Empty;
        }
        ContextJson.Parse(resp.Body, refs, ref sorting, ref nextPage);

        int pages = 1;
        while (!string.IsNullOrEmpty(nextPage) && pages < MaxEagerPages && !ContextResolve.IsInfinite(spec.Uri))
        {
            var more = await _transport.Request(PageChannel(nextPage!), PageRoute(nextPage!), default, ct).ConfigureAwait(false);
            if (!more.Ok || more.Body is null || more.Body.Length == 0) break;
            string? pageNext = null, _s = null;
            ContextJson.Parse(more.Body, refs, ref _s, ref pageNext);
            nextPage = pageNext;
            pages++;
        }

        if (refs.Count == 0) { _log?.Invoke("context-resolve: 0 tracks for " + spec.Uri); return ResolvedContext.Empty; }

        var tracks = await HydrateAsync(refs, ct).ConfigureAwait(false);
        int start = ContextResolve.FindStartIndex(tracks, spec.SkipToTrackUri, spec.SkipToTrackUid, spec.SkipToIndex);
        return new ResolvedContext(tracks, start, sorting, nextPage, ContextResolve.IsInfinite(spec.Uri));
    }

    public async Task<IReadOnlyList<QueuedTrack>> LoadMoreAsync(string nextPageUrl, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(nextPageUrl)) return Array.Empty<QueuedTrack>();
        var resp = await _transport.Request(PageChannel(nextPageUrl), PageRoute(nextPageUrl), default, ct).ConfigureAwait(false);
        if (!resp.Ok || resp.Body is null || resp.Body.Length == 0) return Array.Empty<QueuedTrack>();
        var refs = new List<QueuedRef>();
        string? _s = null, _n = null;
        ContextJson.Parse(resp.Body, refs, ref _s, ref _n);
        return refs.Count == 0 ? Array.Empty<QueuedTrack>() : await HydrateAsync(refs, ct).ConfigureAwait(false);
    }

    // Pull display + duration metadata for the resolved order (cache-aware, batched, gzipped). The expensive work lives
    // here and MetadataService caches it, so re-resolving the same context is near-free. Misses become uri-only
    // placeholders (preserving indices so skip_to-by-index stays valid).
    public async Task<IReadOnlyList<QueuedTrack>> HydrateAsync(IReadOnlyList<QueuedRef> refs, CancellationToken ct = default)
    {
        var uris = new string[refs.Count];
        for (int i = 0; i < refs.Count; i++) uris[i] = refs[i].Uri;
        try { await _metadata.SyncAllAsync(uris, ct).ConfigureAwait(false); }
        catch (Exception ex) { _log?.Invoke("context hydrate: " + ex.Message); }   // best-effort: placeholders below

        var tracks = new QueuedTrack[refs.Count];
        for (int i = 0; i < refs.Count; i++)
        {
            var uri = refs[i].Uri;
            tracks[i] = new QueuedTrack(_store.GetTrack(uri) ?? Placeholder(uri), refs[i].Uid);
        }
        return tracks;
    }

    static Track Placeholder(string uri)
    {
        int i = uri.LastIndexOf(':');
        string id = i >= 0 && i + 1 < uri.Length ? uri[(i + 1)..] : uri;
        return new Track(id, uri, uri, Array.Empty<ArtistRef>(), new AlbumRef("", "", ""), 0, false, null);
    }

    // GET /context-resolve/v1/{escaped uri}. A collection's sort/filter rides on context.url's query string — forward it.
    static string ResolvePath(ContextSpec spec)
    {
        var path = "/context-resolve/v1/" + Uri.EscapeDataString(spec.Uri);
        if (spec.Url is { } url)
        {
            int q = url.IndexOf('?');
            if (q >= 0 && q + 1 < url.Length) path += url[q..];
        }
        return path;
    }

    // A page cursor is either an hm:// mercury ident or an spclient path/URL; route each on the right channel.
    static Channel PageChannel(string pageUrl) => pageUrl.StartsWith("hm://", StringComparison.Ordinal) ? Channel.ApMercury : Channel.Spclient;

    static string PageRoute(string pageUrl)
    {
        if (pageUrl.StartsWith("hm://", StringComparison.Ordinal)) return pageUrl;
        if (pageUrl.StartsWith("https://", StringComparison.Ordinal))
        {
            int slash = pageUrl.IndexOf('/', "https://".Length);
            return slash >= 0 ? pageUrl[slash..] : pageUrl;   // strip host → spclient path+query
        }
        return pageUrl;
    }
}
