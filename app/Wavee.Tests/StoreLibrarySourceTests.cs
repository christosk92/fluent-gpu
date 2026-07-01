using System.Linq;
using System.Threading.Tasks;
using Wavee.Backend;
using Wavee.Backend.Library;
using Wavee.Backend.Playlists;
using Wavee.Core;
using Xunit;

namespace Wavee.Tests;

// The bridge: a catalog source that reads the persistent Store (membership sets × shared entities), joining at read.
public class StoreLibrarySourceTests
{
    static Track Trk(string id) => new(id, "spotify:track:" + id, "T" + id, [], new AlbumRef("", "", ""), 1000, false, null);

    [Fact]
    public async Task GetAlbums_JoinsSavedSetWithStore_SkippingUnhydrated()
    {
        var store = new InMemoryStore();
        store.UpsertAlbum(new Album("a1", "spotify:album:a1", "Album1", null, [], 2020, 1));
        store.SetSaved("albums", "spotify:album:a1", true, SyncState.Confirmed);
        store.SetSaved("albums", "spotify:album:missing", true, SyncState.Confirmed);   // not hydrated → skipped
        var src = new StoreLibrarySource(store);
        Assert.Equal("Album1", Assert.Single(await src.GetAlbumsAsync()).Name);
    }

    [Fact]
    public async Task GetLikedSongs_JoinsSavedTracks()
    {
        var store = new InMemoryStore();
        store.UpsertTrack(Trk("t1"));
        store.SetSaved("liked", "spotify:track:t1", true, SyncState.Confirmed);
        var src = new StoreLibrarySource(store);
        Assert.Equal("Tt1", Assert.Single(await src.GetLikedSongsAsync()).Title);
    }

    [Fact]
    public async Task GetShows_JoinsSavedShows()
    {
        var store = new InMemoryStore();
        store.UpsertShow(new Show("s1", "spotify:show:s1", "Show1", "Pub", null));
        store.SetSaved("shows", "spotify:show:s1", true, SyncState.Confirmed);
        var src = new StoreLibrarySource(store);
        Assert.Equal("Show1", Assert.Single(await src.GetShowsAsync()).Name);
    }

    [Fact]
    public async Task GetPlaylist_JoinsMembershipWithTracks_AndStampsMembershipAddedAt()
    {
        var store = new InMemoryStore();
        store.UpsertTrack(Trk("t1"));
        store.UpsertTrack(Trk("t2"));
        store.UpsertPlaylist(new Playlist("p", "spotify:playlist:p", "My Mix", null, "Me", null, 0));
        store.SetMembership("spotify:playlist:p", new[]
        {
            new PlaylistMember("i1", "spotify:track:t1", "alice", 1_700_000_000_000),
            new PlaylistMember("i2", "spotify:track:t2", null, 0),
        }, null);
        var src = new StoreLibrarySource(store);
        var pl = await src.GetPlaylistAsync("spotify:playlist:p");

        Assert.NotNull(pl);
        Assert.Equal(2, pl!.Tracks!.Count);
        Assert.Equal("Tt1", pl.Tracks[0].Title);
        Assert.Equal("alice", pl.Tracks[0].AddedBy);     // added_by comes from the membership row, not the shared entity
        Assert.NotNull(pl.Tracks[0].AddedAt);
        Assert.Null(pl.Tracks[1].AddedAt);               // added_at 0 → unknown → null
        Assert.Equal(2, pl.TrackCount);
    }

    [Fact]
    public async Task GetPlaylist_StampsContextUidFromItemId_WithoutPollutingSharedStore()
    {
        var store = new InMemoryStore();
        store.UpsertTrack(Trk("t1"));
        store.UpsertPlaylist(new Playlist("p", "spotify:playlist:p", "My Mix", null, "Me", null, 0));
        store.SetMembership("spotify:playlist:p", new[] { new PlaylistMember("rowuid-1", "spotify:track:t1", null, 0) }, null);
        var src = new StoreLibrarySource(store);
        var pl = await src.GetPlaylistAsync("spotify:playlist:p");

        Assert.Equal("rowuid-1", pl!.Tracks![0].ContextUid);          // per-row uid stamped from PlaylistMember.ItemId
        Assert.Null(store.GetTrack("spotify:track:t1")!.ContextUid);  // the SHARED stored entity is untouched (read-model only)
    }

    [Fact]
    public async Task GetArtist_StaleOverview_Revalidates_ThenFreshDoesNot()
    {
        var store = new InMemoryStore();
        // A resident artist WITH top tracks but an old/epoch freshness stamp — i.e. a record an earlier build persisted.
        var resident = new Artist("ar", "spotify:artist:ar", "Stale", null, TopTracks: new[] { Trk("t1") });
        store.UpsertArtist(resident);   // FetchedAt defaults to epoch → reads as stale
        var src = new StoreLibrarySource(store);

        int fetches = 0;
        src.OnDemandFetch = (uri, ct) =>
        {
            fetches++;
            store.UpsertArtist(resident with { MonthlyListeners = 123, FetchedAt = DateTimeOffset.UtcNow });   // overview lands, stamped fresh
            return Task.CompletedTask;
        };

        var a1 = await src.GetArtistAsync("spotify:artist:ar");
        Assert.Equal(1, fetches);                 // stale (epoch) → revalidated, even though it had top tracks
        Assert.Equal(123, a1!.MonthlyListeners);  // served the refreshed record

        var a2 = await src.GetArtistAsync("spotify:artist:ar");
        Assert.Equal(1, fetches);                 // now FetchedAt == now → fresh → NOT re-fetched
        Assert.Equal(123, a2!.MonthlyListeners);
    }

    [Fact]
    public async Task GetPlaylist_ReturnsNull_ForUnknown()
    {
        var src = new StoreLibrarySource(new InMemoryStore());
        Assert.Null(await src.GetPlaylistAsync("spotify:playlist:nope"));
    }

    [Fact]
    public async Task GetPlaylists_FromRootlist_WithMembershipCount()
    {
        var store = new InMemoryStore();
        store.UpsertPlaylist(new Playlist("p1", "spotify:playlist:p1", "One", null, "Me", null, 0));
        store.SetRootlist(new[]
        {
            new RootlistEntry(0, 1, "spotify:start-group:g:F", "F", 0),   // a folder marker — not a playlist row
            new RootlistEntry(1, 0, "spotify:playlist:p1", null, 1),
        });
        store.SetMembership("spotify:playlist:p1", new[] { new PlaylistMember("i", "spotify:track:x", null, 0) }, null);
        var src = new StoreLibrarySource(store);
        var pls = await src.GetPlaylistsAsync();
        Assert.Equal("One", Assert.Single(pls).Name);
        Assert.Equal(1, pls[0].TrackCount);
    }

    [Fact]
    public async Task GetStats_CountsEachSet()
    {
        var store = new InMemoryStore();
        store.SetSaved("albums", "spotify:album:a", true, SyncState.Confirmed);
        store.SetSaved("artists", "spotify:artist:b", true, SyncState.Confirmed);
        store.SetSaved("liked", "spotify:track:c", true, SyncState.Confirmed);
        store.SetSaved("shows", "spotify:show:d", true, SyncState.Confirmed);
        var src = new StoreLibrarySource(store);
        var st = await src.GetStatsAsync();
        Assert.Equal(1, st.Albums);
        Assert.Equal(1, st.Artists);
        Assert.Equal(1, st.LikedSongs);
        Assert.Equal(1, st.Podcasts);
    }

    [Fact]
    public void CollectionsChanged_FiresForTheSetKind_OnStoreBump()
    {
        var store = new InMemoryStore();
        var src = new StoreLibrarySource(store);
        CollectionKind? seen = null;
        using var sub = src.CollectionsChanged.Subscribe(new Obs(k => seen = k));
        store.SetSaved("albums", "spotify:album:a", true, SyncState.Confirmed);   // bumps spotify:album:a
        Assert.Equal(CollectionKind.Albums, seen);
    }

    sealed class Obs(System.Action<CollectionKind> onNext) : System.IObserver<CollectionKind>
    {
        public void OnNext(CollectionKind v) => onNext(v);
        public void OnCompleted() { }
        public void OnError(System.Exception e) { }
    }
}
