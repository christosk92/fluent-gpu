using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using Wavee.Backend.Spotify;
using Col = Wavee.Protocol.Collection;

namespace Wavee.Backend.Collections;

// ── The live collection (library set) fetch ──────────────────────────────────────────────────────────────────────────
// POSTs the collection2v2 service: a token-gated delta when we already have a sync token (cheap), otherwise the full page
// loop. Items fold onto the Store's set membership via CollectionDeltaApplier; the sync token advances after a successful
// apply; the changed entity uris are handed to the hydrator. Revision get/set are injected (the cold-store seam) so the
// fetcher stays decoupled from persistence and unit-testable.
public sealed class CollectionFetcher
{
    const string ContentType = "application/protobuf";

    readonly IHttpExchange _http;
    readonly Func<string> _baseUrl;
    readonly Func<string> _username;
    readonly IStore _store;
    readonly Func<string, string?> _getRevision;
    readonly Action<string, string?> _setRevision;
    readonly Func<IReadOnlyList<string>, CancellationToken, Task> _hydrate;

    public CollectionFetcher(IHttpExchange http, Func<string> baseUrl, Func<string> username, IStore store,
        Func<string, string?> getRevision, Action<string, string?> setRevision,
        Func<IReadOnlyList<string>, CancellationToken, Task> hydrate)
    {
        _http = http;
        _baseUrl = baseUrl;
        _username = username;
        _store = store;
        _getRevision = getRevision;
        _setRevision = setRevision;
        _hydrate = hydrate;
    }

    public async Task FetchSetAsync(string setId, CancellationToken ct = default)
    {
        string wireSet = WireSet(setId);
        var token = _getRevision(setId);

        if (!string.IsNullOrEmpty(token))
        {
            var delta = await DeltaAsync(wireSet, token!, ct).ConfigureAwait(false);
            if (delta.DeltaUpdatePossible)
            {
                var d = CollectionWireMapper.ParseDelta(setId, delta);
                CollectionDeltaApplier.Apply(_store, d);
                await HydrateAsync(d, ct).ConfigureAwait(false);
                _setRevision(setId, d.NewRevision);
                return;
            }
            // delta not possible (token too stale) → fall through to a full snapshot
        }

        string? pageToken = null;
        string? newRev = null;
        using (_store.BeginBulk())   // coalesce a multi-page snapshot into one change signal
        {
            do
            {
                var page = await PageAsync(wireSet, pageToken, ct).ConfigureAwait(false);
                var d = CollectionWireMapper.ParsePage(setId, page);
                CollectionDeltaApplier.Apply(_store, d);
                newRev = d.NewRevision ?? newRev;
                await HydrateAsync(d, ct).ConfigureAwait(false);
                pageToken = string.IsNullOrEmpty(page.NextPageToken) ? null : page.NextPageToken;
            } while (pageToken is not null);
        }
        _setRevision(setId, newRev);
    }

    async Task<Col.DeltaResponse> DeltaAsync(string wireSet, string lastToken, CancellationToken ct)
    {
        var body = new Col.DeltaRequest { Username = _username(), Set = wireSet, LastSyncToken = lastToken }.ToByteArray();
        using var resp = await PostAsync("/collection/v2/delta", body, ct).ConfigureAwait(false);
        return Col.DeltaResponse.Parser.ParseFrom(resp.Body);
    }

    async Task<Col.PageResponse> PageAsync(string wireSet, string? pageToken, CancellationToken ct)
    {
        var req = new Col.PageRequest { Username = _username(), Set = wireSet, Limit = 300 };
        if (!string.IsNullOrEmpty(pageToken)) req.PaginationToken = pageToken;
        using var resp = await PostAsync("/collection/v2/paging", req.ToByteArray(), ct).ConfigureAwait(false);
        return Col.PageResponse.Parser.ParseFrom(resp.Body);
    }

    async Task<HttpResp> PostAsync(string path, byte[] body, CancellationToken ct)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Content-Type"] = ContentType, ["Accept"] = ContentType };
        var resp = await _http.SendAsync(new HttpReq("POST", _baseUrl() + path, headers, body), ct).ConfigureAwait(false);
        if (resp.Status != 200) { resp.Dispose(); throw new InvalidOperationException($"collection fetch failed ({resp.Status}) for {path}"); }
        return resp;
    }

    async Task HydrateAsync(CollectionDelta d, CancellationToken ct)
    {
        var uris = new List<string>(d.Items.Count);
        for (int i = 0; i < d.Items.Count; i++)
        {
            var it = d.Items[i];
            if (!it.Removed && it.Uri.StartsWith("spotify:", StringComparison.Ordinal)) uris.Add(it.Uri);
        }
        if (uris.Count > 0) await _hydrate(uris, ct).ConfigureAwait(false);
    }

    // logical UI set_id → the wire collection set name (the only place the mapping lives).
    static string WireSet(string setId) => setId switch
    {
        "liked" => "collection",
        "albums" => "albums",
        "artists" => "artists",
        "shows" => "shows",
        "episodes" => "episodes",
        _ => setId,
    };
}
