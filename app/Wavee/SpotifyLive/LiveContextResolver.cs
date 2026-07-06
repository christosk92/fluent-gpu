using System;
using System.Collections.Generic;
using Google.Protobuf;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Wavee.Backend;
using Wavee.Backend.Metadata;
using Wavee.Core;
using Wavee.Protocol.Playback;

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
        ContextJson.Result jsonInfo = default;
        var resp = await _transport.Request(Channel.Spclient, ResolvePath(spec), default, ct).ConfigureAwait(false);
        if (!resp.Ok || resp.Body is null || resp.Body.Length == 0)
        {
            _log?.Invoke($"context-resolve failed ({resp.Status}): {spec.Uri}");
            return ResolvedContext.Empty;
        }
        ContextJson.Parse(resp.Body, refs, ref sorting, ref nextPage, out jsonInfo);

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
        return new ResolvedContext(tracks, start, sorting, nextPage, ContextResolve.IsInfinite(spec.Uri),
            jsonInfo.Metadata, string.IsNullOrEmpty(jsonInfo.ContextUri) ? null : jsonInfo.ContextUri);
    }

    public async Task<ContextPage> LoadMoreAsync(string nextPageUrl, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(nextPageUrl)) return ContextPage.Empty;
        var resp = await _transport.Request(PageChannel(nextPageUrl), PageRoute(nextPageUrl), default, ct).ConfigureAwait(false);
        if (!resp.Ok || resp.Body is null || resp.Body.Length == 0) return ContextPage.Empty;
        var refs = new List<QueuedRef>();
        string? _s = null, _n = null;
        ContextJson.Parse(resp.Body, refs, ref _s, ref _n);
        if (refs.Count == 0) return new ContextPage(Array.Empty<QueuedTrack>(), _n);
        return new ContextPage(await HydrateAsync(refs, ct).ConfigureAwait(false), _n);
    }

    public async Task<ResolvedContext> ResolveAutoplayAsync(string contextUri, IReadOnlyList<string> recentTrackUris,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(contextUri)) return ResolvedContext.Empty;
        try
        {
            return contextUri.StartsWith("spotify:track:", StringComparison.Ordinal)
                ? await ResolveRadioApolloAsync(contextUri, recentTrackUris, ct).ConfigureAwait(false)
                : await ResolveContextAutoplayAsync(contextUri, recentTrackUris, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _log?.Invoke("autoplay resolve failed for " + contextUri + ": " + ex.Message);
            return ResolvedContext.Empty;
        }
    }

    public async Task<ResolvedContext> ResolveAutopodcastAsync(string contextUri, IReadOnlyList<string> recentEpisodeUris,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(contextUri)) return ResolvedContext.Empty;
        var request = new AutoplayContextRequest { ContextUri = contextUri, IsVideo = false };
        for (int i = 0; i < recentEpisodeUris.Count; i++)
            if (!string.IsNullOrEmpty(recentEpisodeUris[i])) request.RecentTrackUri.Add(recentEpisodeUris[i]);

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Content-Type"] = "application/x-protobuf",
            ["Accept"] = "application/json",
        };
        var resp = await _transport.Request(Channel.Spclient, "/context-resolve/v1/autopodcast",
            request.ToByteArray(), ct, "POST", headers).ConfigureAwait(false);
        if (!resp.Ok || resp.Body is null || resp.Body.Length == 0)
        {
            _log?.Invoke($"autopodcast failed ({resp.Status}): {contextUri}");
            return ResolvedContext.Empty;
        }

        var refs = new List<QueuedRef>();
        string? sorting = null, nextPage = null;
        ContextJson.Parse(resp.Body, refs, ref sorting, ref nextPage, out var info);
        if (refs.Count == 0) return ResolvedContext.Empty;
        var tracks = Tag(await HydrateAsync(refs, ct).ConfigureAwait(false), "autoplay");
        return new ResolvedContext(tracks, 0, sorting, nextPage, true, info.Metadata, info.ContextUri ?? contextUri);
    }

    async Task<ResolvedContext> ResolveContextAutoplayAsync(string contextUri, IReadOnlyList<string> recentTrackUris, CancellationToken ct)
    {
        var request = new AutoplayContextRequest { ContextUri = contextUri, IsVideo = false };
        for (int i = 0; i < recentTrackUris.Count; i++)
            if (!string.IsNullOrEmpty(recentTrackUris[i])) request.RecentTrackUri.Add(recentTrackUris[i]);

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Content-Type"] = "application/x-protobuf",
            ["Accept"] = "application/json",
        };
        var resp = await _transport.Request(Channel.Spclient, "/context-resolve/v1/autoplay",
            request.ToByteArray(), ct, "POST", headers).ConfigureAwait(false);
        if (!resp.Ok || resp.Body is null || resp.Body.Length == 0)
        {
            _log?.Invoke($"autoplay endpoint failed ({resp.Status}): {contextUri}");
            return ResolvedContext.Empty;
        }

        var refs = new List<QueuedRef>();
        string? sorting = null, nextPage = null;
        ContextJson.Parse(resp.Body, refs, ref sorting, ref nextPage, out var info);
        if (refs.Count == 0) return ResolvedContext.Empty;
        var tracks = Tag(await HydrateAsync(refs, ct).ConfigureAwait(false), "autoplay");
        var stationUri = string.IsNullOrEmpty(info.ContextUri) ? contextUri : info.ContextUri;
        return new ResolvedContext(tracks, 0, sorting, nextPage, true, info.Metadata, stationUri);
    }

    async Task<ResolvedContext> ResolveRadioApolloAsync(string seedTrackUri, IReadOnlyList<string> recentTrackUris, CancellationToken ct)
    {
        string seedId = StripTrackPrefix(seedTrackUri);
        var prev = new List<string>(recentTrackUris.Count);
        for (int i = 0; i < recentTrackUris.Count; i++)
        {
            var id = StripTrackPrefix(recentTrackUris[i]);
            if (!string.IsNullOrEmpty(id)) prev.Add(Uri.EscapeDataString(id));
        }

        int salt = Random.Shared.Next(100_000, 1_000_000);
        var route = "/radio-apollo/v3/tracks/spotify:station:track:" + seedId
            + "?salt=" + salt.ToString(System.Globalization.CultureInfo.InvariantCulture)
            + "&autoplay=true&count=50&isVideo=false"
            + "&prev_tracks=" + string.Join(',', prev)
            + "&pageNum=2&minimal=true";
        var resp = await _transport.Request(Channel.Spclient, route, default, ct).ConfigureAwait(false);
        if (!resp.Ok || resp.Body is null || resp.Body.Length == 0)
        {
            _log?.Invoke($"radio-apollo failed ({resp.Status}): {seedTrackUri}");
            return ResolvedContext.Empty;
        }

        var refs = new List<QueuedRef>();
        string? sorting = null, nextPage = null;
        ContextJson.Parse(resp.Body, refs, ref sorting, ref nextPage);
        if (refs.Count == 0) return ResolvedContext.Empty;
        var tracks = Tag(await HydrateAsync(refs, ct).ConfigureAwait(false), "autoplay");
        return new ResolvedContext(tracks, 0, null, nextPage, true, null, "spotify:station:track:" + seedId);
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
            string provider = string.IsNullOrEmpty(refs[i].Provider) ? "context" : refs[i].Provider;
            tracks[i] = new QueuedTrack(_store.GetTrack(uri) ?? Placeholder(uri), refs[i].Uid, provider, refs[i].Metadata, RowKindOf(uri));
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
    static IReadOnlyList<QueuedTrack> Tag(IReadOnlyList<QueuedTrack> tracks, string provider)
    {
        if (tracks.Count == 0) return tracks;
        var tagged = new QueuedTrack[tracks.Count];
        for (int i = 0; i < tracks.Count; i++) tagged[i] = tracks[i] with { Provider = provider };
        return tagged;
    }

    static string StripTrackPrefix(string uri) =>
        uri.StartsWith("spotify:track:", StringComparison.Ordinal) ? uri["spotify:track:".Length..] : uri;

    static QueueRowKind RowKindOf(string uri)
    {
        if (uri == "spotify:delimiter") return QueueRowKind.Delimiter;
        if (uri.StartsWith("spotify:meta:page:", StringComparison.Ordinal)) return QueueRowKind.PageMarker;
        return QueueRowKind.Playable;
    }

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
