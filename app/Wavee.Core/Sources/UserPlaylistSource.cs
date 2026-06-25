using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;

namespace Wavee.Core;

/// <summary>User-created playlists — the Mutations facet's playlist edits (docs/architecture.md §2 "Playlists, mutations
/// &amp; folders"). An in-process catalog source owning <c>wavee:playlist:*</c> that holds the session's created
/// playlists + their tracks (snapshots), so a created / added-to playlist appears in the sidebar list and opens through
/// the shared detail surface; <see cref="ResolveContext"/> lets the player play it. Session-only for now (the saved-state
/// outbox is the persisted store); item reorder + folder trees are the next increment.</summary>
public sealed class UserPlaylistSource : ICatalogSource
{
    sealed class Entry { public string Name = ""; public readonly List<Track> Tracks = new(); }
    readonly Dictionary<string, Entry> _playlists = new();
    readonly SimpleSubject<int> _changed = new(0);
    int _seq, _version;

    public string Id => "user-playlists";
    public bool Owns(string uri) => uri.StartsWith("wavee:playlist:", System.StringComparison.Ordinal);
    public SourceCapabilities Capabilities => SourceCapabilities.Catalog | SourceCapabilities.Mutations;

    /// <summary>Bumps on every create / add — the bridge mirrors it so the sidebar re-reads the playlist list.</summary>
    public IObservable<int> PlaylistsChanged => _changed;

    public string CreatePlaylist(string name)
    {
        string uri = "wavee:playlist:" + (++_seq);
        _playlists[uri] = new Entry { Name = string.IsNullOrWhiteSpace(name) ? "New Playlist" : name };
        Bump();
        return uri;
    }

    public void AddTrack(string playlistUri, Track track)
    {
        if (_playlists.TryGetValue(playlistUri, out var e) && e.Tracks.All(t => t.Uri != track.Uri))
        { e.Tracks.Add(track); Bump(); }
    }

    /// <summary>Ensure at least one user playlist exists (for a no-picker "add to playlist") and return its uri + name.</summary>
    public (string Uri, string Name) DefaultPlaylist()
    {
        var first = _playlists.FirstOrDefault();
        if (first.Key is { } uri) return (uri, first.Value.Name);
        string created = CreatePlaylist("My Playlist");
        return (created, _playlists[created].Name);
    }

    /// <summary>Player context resolver: a user playlist's tracks (null if not one of ours) — wired into the fake player.</summary>
    public IReadOnlyList<Track>? ResolveContext(string contextUri) =>
        _playlists.TryGetValue(contextUri, out var e) ? e.Tracks.ToArray() : null;

    void Bump() { _version++; _changed.OnNext(_version); }

    // ── ICatalogSource: only the playlist reads are non-empty ──
    public Task<Playlist?> GetPlaylistAsync(string uri, CancellationToken ct = default)
    {
        if (!_playlists.TryGetValue(uri, out var e)) return Task.FromResult<Playlist?>(null);
        return Task.FromResult<Playlist?>(new Playlist("up", uri, e.Name, "Created on this device", "You", null,
            e.Tracks.Count, e.Tracks.ToArray(),
            Owner: new Owner("you", "You", null),
            Capabilities: new PlaylistCapabilities(true, true, true, false, true), Source: "user"));
    }

    public Task<IReadOnlyList<PlaylistSummary>> GetPlaylistsAsync(CancellationToken ct = default)
    {
        var list = _playlists.Select(kv => new PlaylistSummary(kv.Key, kv.Value.Name, "You", kv.Value.Tracks.Count, null)).ToList();
        return Task.FromResult<IReadOnlyList<PlaylistSummary>>(list);
    }

    public async IAsyncEnumerable<TrackPage> StreamTracksAsync(string contextUri, [EnumeratorCancellation] CancellationToken ct = default)
    {
        await Task.CompletedTask;
        var t = _playlists.TryGetValue(contextUri, out var e) ? e.Tracks.ToArray() : System.Array.Empty<Track>();
        yield return new TrackPage(t, t.Length, t.Length);
    }

    public Task<Album?> GetAlbumAsync(string uri, CancellationToken ct = default) => Task.FromResult<Album?>(null);
    public Task<Artist?> GetArtistAsync(string uri, CancellationToken ct = default) => Task.FromResult<Artist?>(null);
    public Task<IReadOnlyList<LibraryItem>> GetLibraryAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<LibraryItem>>(System.Array.Empty<LibraryItem>());
    public Task<IReadOnlyList<Album>> GetAlbumsAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Album>>(System.Array.Empty<Album>());
    public Task<IReadOnlyList<Artist>> GetArtistsAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Artist>>(System.Array.Empty<Artist>());
    public Task<IReadOnlyList<Track>> GetLikedSongsAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Track>>(System.Array.Empty<Track>());
    public Task<SearchResults> SearchAsync(string query, CancellationToken ct = default) => Task.FromResult(SearchResults.Empty);
    public Task<HomeContribution> GetHomeAsync(CancellationToken ct = default) => Task.FromResult(new HomeContribution(System.Array.Empty<HomeGroup>(), 60));
    public Task<LibraryStats> GetStatsAsync(CancellationToken ct = default) => Task.FromResult(new LibraryStats(0, 0, 0, 0));
}
