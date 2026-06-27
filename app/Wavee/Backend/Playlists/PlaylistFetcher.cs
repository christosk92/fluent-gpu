using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Wavee.Backend.Spotify;
using Wavee.Core;
using Pl = Wavee.Protocol.Playlist;

namespace Wavee.Backend.Playlists;

// ── The live membership fetch (SpotifyLive boundary, but Backend so the orchestration is unit-tested) ─────────────────
// GETs /playlist/v2/{path}?decorate=... → SelectedListContent, projects a THIN playlist header + the ordered membership
// into the Store, and hands the membership uris to a hydrator (MetadataService.SyncAllAsync) to fill the shared entities.
// The same path serves a playlist and the rootlist (the rootlist is just a playlist of playlist-uri + group markers).
public sealed class PlaylistFetcher
{
    const string Decorate = "?decorate=revision,attributes,length,owner,capabilities,picture";

    readonly IHttpExchange _http;
    readonly Func<string> _baseUrl;
    readonly IStore _store;
    readonly Func<IReadOnlyList<string>, CancellationToken, Task> _hydrate;

    public PlaylistFetcher(IHttpExchange http, Func<string> baseUrl, IStore store, Func<IReadOnlyList<string>, CancellationToken, Task> hydrate)
    {
        _http = http;
        _baseUrl = baseUrl;
        _store = store;
        _hydrate = hydrate;
    }

    public async Task FetchPlaylistAsync(string playlistUri, CancellationToken ct = default)
    {
        var slc = await GetAsync(playlistUri, ct).ConfigureAwait(false);
        var (members, rev) = PlaylistWireMapper.ParseContents(slc);
        if (slc.Attributes is { } attr) _store.UpsertPlaylist(HeaderOf(playlistUri, attr, slc));   // thin header (no tracklist baked)
        _store.SetMembership(playlistUri, members, rev);
        await HydrateAsync(members, ct).ConfigureAwait(false);
    }

    /// <summary>Fetch + store ONLY a playlist's header (name / cover / owner / count) — no membership, no track hydration.
    /// Populates the rootlist playlists' names + covers for the home + sidebar without pulling every playlist's tracks.</summary>
    public async Task FetchPlaylistHeaderAsync(string playlistUri, CancellationToken ct = default)
    {
        var slc = await GetAsync(playlistUri, ct).ConfigureAwait(false);
        if (slc.Attributes is { } attr) _store.UpsertPlaylist(HeaderOf(playlistUri, attr, slc));
    }

    public async Task FetchRootlistAsync(string rootlistUri, CancellationToken ct = default)
    {
        var slc = await GetAsync(rootlistUri, ct).ConfigureAwait(false);
        var entries = new List<RootlistEntry>();
        if (slc.Contents is { } contents)
        {
            int pos = 0, depth = 0;
            foreach (var item in contents.Items)
            {
                var uri = item.Uri;
                if (uri.StartsWith("spotify:start-group:", StringComparison.Ordinal))
                {
                    entries.Add(new RootlistEntry(pos++, 1, uri, GroupName(uri), depth));
                    depth++;
                }
                else if (uri.StartsWith("spotify:end-group:", StringComparison.Ordinal))
                {
                    depth = Math.Max(0, depth - 1);
                    entries.Add(new RootlistEntry(pos++, 2, uri, null, depth));
                }
                else
                {
                    entries.Add(new RootlistEntry(pos++, 0, uri, null, depth));
                }
            }
        }
        _store.SetRootlist(entries);
    }

    async Task<Pl.SelectedListContent> GetAsync(string uri, CancellationToken ct)
    {
        var url = _baseUrl() + "/playlist/v2/" + PathOf(uri) + Decorate;
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Accept"] = "application/protobuf" };
        using var resp = await _http.SendAsync(new HttpReq("GET", url, headers, null), ct).ConfigureAwait(false);
        if (resp.Status != 200) throw new InvalidOperationException($"playlist fetch failed ({resp.Status}) for {uri}");
        return Pl.SelectedListContent.Parser.ParseFrom(resp.Body);   // stream-parse: a 10k-item body never lands on the LOH
    }

    async Task HydrateAsync(IReadOnlyList<PlaylistMember> members, CancellationToken ct)
    {
        var uris = new List<string>(members.Count);
        for (int i = 0; i < members.Count; i++)
        {
            var u = members[i].ItemUri;
            if (u.StartsWith("spotify:track:", StringComparison.Ordinal) || u.StartsWith("spotify:episode:", StringComparison.Ordinal)) uris.Add(u);
        }
        if (uris.Count > 0) await _hydrate(uris, ct).ConfigureAwait(false);
    }

    // "spotify:playlist:abc" → "playlist/abc"; "spotify:user:bob:rootlist" → "user/bob/rootlist".
    static string PathOf(string uri) => uri.StartsWith("spotify:", StringComparison.Ordinal) ? uri.Substring(8).Replace(':', '/') : uri.Replace(':', '/');

    static Playlist HeaderOf(string uri, Pl.ListAttributes attr, Pl.SelectedListContent slc)
    {
        string name = attr.HasName ? attr.Name : "";
        string? desc = attr.HasDescription ? attr.Description : null;
        string owner = slc.HasOwnerUsername ? slc.OwnerUsername : "";
        int len = slc.HasLength ? slc.Length : 0;
        return new Playlist(IdOf(uri), uri, name, desc, owner, CoverOf(attr), len);   // Tracks defaults null → thin
    }

    // The playlist cover: the server's pre-sized URLs first (largest), else the raw picture file id → the image CDN.
    static Image? CoverOf(Pl.ListAttributes attr)
    {
        for (int i = attr.PictureSize.Count - 1; i >= 0; i--)
            if (!string.IsNullOrEmpty(attr.PictureSize[i].Url)) return new Image(attr.PictureSize[i].Url);
        if (attr.Picture.Length > 0) return new Image("https://i.scdn.co/image/" + Convert.ToHexStringLower(attr.Picture.Span));
        return null;
    }

    static string IdOf(string uri) { int i = uri.LastIndexOf(':'); return i >= 0 ? uri.Substring(i + 1) : uri; }
    static string? GroupName(string uri) { var p = uri.Split(':'); return p.Length >= 4 ? Uri.UnescapeDataString(p[3]) : null; }
}
