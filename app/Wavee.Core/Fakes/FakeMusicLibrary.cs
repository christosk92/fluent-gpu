namespace Wavee.Core;

/// <summary>In-memory fake read-API. A small artificial delay exercises the loading/skeleton path; everything resolves
/// from <see cref="FakeData"/>. Returns <see cref="Result{T}"/> so the friendly error/empty branches are reachable.</summary>
public sealed class FakeMusicLibrary : IMusicLibrary
{
    static int IndexFromUri(string uri)
    {
        int colon = uri.LastIndexOf(':');
        var tail = colon >= 0 ? uri[(colon + 1)..] : uri;
        int i = 0;
        foreach (char c in tail) if (char.IsDigit(c)) i = i * 10 + (c - '0');
        return i;
    }

    public async Task<Playlist> GetPlaylistAsync(string id, CancellationToken ct = default)
    {
        await Task.Delay(220, ct).ConfigureAwait(false);
        return FakeData.Playlist(IndexFromUri(id));
    }

    public async Task<Album> GetAlbumAsync(string id, CancellationToken ct = default)
    {
        await Task.Delay(200, ct).ConfigureAwait(false);
        return FakeData.Album(IndexFromUri(id));
    }

    public async Task<Artist> GetArtistAsync(string id, CancellationToken ct = default)
    {
        await Task.Delay(220, ct).ConfigureAwait(false);
        return FakeData.Artist(IndexFromUri(id));
    }

    public async Task<IReadOnlyList<LibraryItem>> GetLibraryAsync(CancellationToken ct = default)
    {
        await Task.Delay(260, ct).ConfigureAwait(false);
        return FakeData.Library();
    }

    public async Task<SearchResults> SearchAsync(string query, CancellationToken ct = default)
    {
        await Task.Delay(180, ct).ConfigureAwait(false);
        return FakeData.Search(query);
    }

    public async Task<IReadOnlyList<Album>> GetAlbumsAsync(CancellationToken ct = default)
    {
        await Task.Delay(220, ct).ConfigureAwait(false);
        var a = new Album[13];
        for (int i = 0; i < a.Length; i++) a[i] = FakeData.Album(20 + i);
        return a;
    }

    public async Task<IReadOnlyList<Artist>> GetArtistsAsync(CancellationToken ct = default)
    {
        await Task.Delay(220, ct).ConfigureAwait(false);
        var a = new Artist[12];
        for (int i = 0; i < a.Length; i++) a[i] = FakeData.Artist(30 + i);
        return a;
    }

    public async Task<IReadOnlyList<Track>> GetLikedSongsAsync(CancellationToken ct = default)
    {
        await Task.Delay(240, ct).ConfigureAwait(false);
        return FakeData.LikedSongs(161);
    }

    public async Task<LibraryStats> GetStatsAsync(CancellationToken ct = default)
    {
        await Task.Delay(200, ct).ConfigureAwait(false);
        return FakeData.LibraryStats();
    }

    public async Task<IReadOnlyList<PlaylistSummary>> GetPlaylistsAsync(CancellationToken ct = default)
    {
        await Task.Delay(260, ct).ConfigureAwait(false);
        return FakeData.UserPlaylists();
    }
}
