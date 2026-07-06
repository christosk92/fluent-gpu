using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Wavee.Backend.Spotify;
using Wavee.Core;
using Pl = Wavee.Protocol.Playlist;

namespace Wavee.Backend.Playlists;

/// <summary>How a revision-gated <c>/diff</c> revalidation resolved (§2.6): ops applied in place / already current /
/// fell back to a full re-fetch (no baseline, stale revision (509), torn apply, or an unparseable response).</summary>
public enum DiffOutcome { Applied, UpToDate, FellBackToFull }

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
        var uris = new List<string>();
        if (slc.Contents is { } contents)
            foreach (var item in contents.Items) uris.Add(item.Uri);
        // the flat-marker parse lives once in RootlistTreeBuilder (shared with LibrarySync + RootlistFollowStrategy).
        _store.SetRootlist(RootlistTreeBuilder.EntriesFromUris(uris), slc.HasRevision ? slc.Revision.ToByteArray() : null);   // stop discarding the rootlist revision (§2.6)
    }

    /// <summary>Revision-gated revalidation via GET <c>/playlist/v2/{path}/diff</c> (§2.6, fixes RC5): a resident,
    /// unchanged playlist costs one up-to-date round-trip (or a 304); a changed one applies ONLY the server's ops onto the
    /// resident baseline and hydrates ONLY the added uris. No baseline / no stored revision / a stale revision (509) /
    /// a torn apply / an unparseable body all fall back to the full <see cref="FetchPlaylistAsync"/> — always converges.</summary>
    public async Task<DiffOutcome> FetchPlaylistDiffAsync(string playlistUri, CancellationToken ct = default)
    {
        var rev = _store.PlaylistRevision(playlistUri);
        var baseline = _store.Membership(playlistUri);
        if (rev is null || rev.Length < 5 || baseline.Count == 0)   // rev = 4B counter + hash; nothing to gate on → full
        {
            await FetchPlaylistAsync(playlistUri, ct).ConfigureAwait(false);
            return DiffOutcome.FellBackToFull;
        }

        // revision wire string "counter,hexhash" — the comma MUST be %2C-encoded or the gateway 509s (§2.6).
        var enc = Uri.EscapeDataString(FormatRevision(rev));
        var url = _baseUrl() + "/playlist/v2/" + PathOf(playlistUri) + "/diff?revision=" + enc + "&handlesContent=&hint_revision=" + enc;
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Accept"] = "application/protobuf" };
        byte[] body;
        int status;
        using (var resp = await _http.SendAsync(new HttpReq("GET", url, headers, null), ct).ConfigureAwait(false))
        {
            status = resp.Status;
            if (status == 304) return DiffOutcome.UpToDate;   // Not Modified = our revision is current
            if (status != 200)                                // 509 (revision too stale — editorial mixes) or anything else
            {
                await FetchPlaylistAsync(playlistUri, ct).ConfigureAwait(false);
                return DiffOutcome.FellBackToFull;
            }
            using var ms = new MemoryStream();
            await resp.Body.CopyToAsync(ms, ct).ConfigureAwait(false);   // diff bodies are small — buffer for the zstd sniff
            body = ms.ToArray();
        }

        Pl.SelectedListContent slc;
        try { slc = Pl.SelectedListContent.Parser.ParseFrom(SpotifyZstd.MaybeDecompressZstd(body)); }
        catch
        {
            await FetchPlaylistAsync(playlistUri, ct).ConfigureAwait(false);
            return DiffOutcome.FellBackToFull;
        }

        if (slc.HasUpToDate && slc.UpToDate) return DiffOutcome.UpToDate;

        if (slc.Diff is { } diff)
        {
            var list = new List<PlaylistMember>(baseline);
            var before = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < baseline.Count; i++) before.Add(baseline[i].ItemUri);
            try { PlaylistDiffApplier.Apply(list, PlaylistWireMapper.MapOps(diff.Ops)); }
            catch (ArgumentOutOfRangeException)   // torn apply — the resident baseline drifted → full re-fetch converges
            {
                await FetchPlaylistAsync(playlistUri, ct).ConfigureAwait(false);
                return DiffOutcome.FellBackToFull;
            }
            _store.SetMembership(playlistUri, list, diff.HasToRevision ? diff.ToRevision.ToByteArray() : rev);
            var added = new List<string>();
            for (int i = 0; i < list.Count; i++) { var u = list[i].ItemUri; if (!before.Contains(u)) added.Add(u); }
            if (added.Count > 0) await HydrateUrisAsync(added, ct).ConfigureAwait(false);
            return DiffOutcome.Applied;
        }

        if (slc.Contents is not null)   // some responses carry the full contents instead of ops — treat as a full refresh
        {
            var (members, newRev) = PlaylistWireMapper.ParseContents(slc);
            if (slc.Attributes is { } attr) _store.UpsertPlaylist(HeaderOf(playlistUri, attr, slc));
            _store.SetMembership(playlistUri, members, newRev ?? rev);
            await HydrateAsync(members, ct).ConfigureAwait(false);
            return DiffOutcome.FellBackToFull;
        }

        return DiffOutcome.UpToDate;   // 200 with nothing actionable — nothing changed that we can see
    }

    /// <summary>The playlist4 revision wire string: 4-byte big-endian counter + the remaining bytes as lowercase hex,
    /// joined with a comma (percent-encode when it rides a query string).</summary>
    internal static string FormatRevision(byte[] rev)
        => BinaryPrimitives.ReadInt32BigEndian(rev.AsSpan(0, 4)) + "," + Convert.ToHexStringLower(rev.AsSpan(4));

    /// <summary>Hydrate the entities behind a specific uri list (the LibrarySync in-place-apply path fills ONLY the added
    /// track/episode uris without a full re-fetch). Non-track/episode uris are skipped, mirroring <see cref="HydrateAsync"/>.</summary>
    public async Task HydrateUrisAsync(IReadOnlyList<string> uris, CancellationToken ct = default)
    {
        var filtered = new List<string>(uris.Count);
        for (int i = 0; i < uris.Count; i++)
        {
            var u = uris[i];
            if (u.StartsWith("spotify:track:", StringComparison.Ordinal) || u.StartsWith("spotify:episode:", StringComparison.Ordinal)) filtered.Add(u);
        }
        if (filtered.Count > 0) await _hydrate(filtered, ct).ConfigureAwait(false);
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
        return new Playlist(IdOf(uri), uri, name, desc, owner, CoverOf(attr), len,
            Capabilities: CapabilitiesOf(attr, slc));   // Tracks defaults null → thin
    }

    static PlaylistCapabilities CapabilitiesOf(Pl.ListAttributes attr, Pl.SelectedListContent slc)
    {
        var cap = slc.Capabilities;
        return new PlaylistCapabilities(
            CanView: cap?.CanView ?? false,
            CanEditItems: cap?.CanEditItems ?? false,
            CanEditMetadata: cap?.CanEditMetadata ?? false,
            IsCollaborative: attr.HasCollaborative && attr.Collaborative,
            IsOwner: false);
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
}
