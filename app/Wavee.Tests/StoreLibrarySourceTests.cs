using System.Linq;
using System.Collections.Generic;
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
    public async Task GetPlaylist_OverlaysResolvedOwnerAndCollaborators_WithoutChangingTrackAddedBy()
    {
        var store = new InMemoryStore();
        store.UpsertTrack(Trk("t1"));
        store.UpsertTrack(Trk("t2"));
        store.UpsertTrack(Trk("t3"));
        store.UpsertPlaylist(new Playlist("p", "spotify:playlist:p", "My Mix", null, "owner_raw", null, 0,
            Capabilities: new PlaylistCapabilities(false, false, false, true, false)));
        store.SetMembership("spotify:playlist:p", new[]
        {
            new PlaylistMember("i1", "spotify:track:t1", "owner_raw", 1),
            new PlaylistMember("i2", "spotify:track:t2", "friend_raw", 2),
            new PlaylistMember("i3", "spotify:track:t3", "friend_raw", 3),
        }, null);
        var profiles = new FakeUserProfiles(
            new Owner("owner_raw", "Owner Display", new Image("https://img/owner")),
            new Owner("friend_raw", "Friend Display", new Image("https://img/friend")));
        var src = new StoreLibrarySource(store) { UserProfiles = profiles };

        var pl = await src.GetPlaylistAsync("spotify:playlist:p");

        Assert.NotNull(pl);
        Assert.Equal("Owner Display", pl!.OwnerName);
        Assert.Equal("Owner Display", pl.Owner!.Name);
        Assert.Equal("https://img/owner", pl.Owner.Avatar!.Url);
        Assert.Equal(new[] { "Owner Display", "Friend Display" }, pl.Collaborators!.Select(o => o.Name).ToArray());
        Assert.Equal(new[] { "owner_raw", "friend_raw" }, pl.Collaborators.Select(o => o.Id).ToArray());
        Assert.Equal("owner_raw", pl.Tracks![0].AddedBy);
        Assert.Equal("friend_raw", pl.Tracks[1].AddedBy);
        Assert.Contains("spotify:user:owner_raw", profiles.Prefetched);
        Assert.Contains("spotify:user:friend_raw", profiles.Prefetched);
    }

    [Fact]
    public async Task GetPlaylists_OverlaysResolvedOwnerName_ForSidebarAndHomeSummaries()
    {
        var store = new InMemoryStore();
        store.UpsertPlaylist(new Playlist("p1", "spotify:playlist:p1", "One", null, "owner_raw", null, 0));
        store.SetRootlist(new[] { new RootlistEntry(0, 0, "spotify:playlist:p1", null, 0) });
        var src = new StoreLibrarySource(store)
        {
            UserProfiles = new FakeUserProfiles(new Owner("owner_raw", "Owner Display", null)),
        };

        var pls = await src.GetPlaylistsAsync();

        Assert.Equal("Owner Display", Assert.Single(pls).OwnerName);
    }

    [Fact]
    public async Task ProfileChangeInvalidatesOpenPlaylistAndPlaylistCollection()
    {
        var store = new InMemoryStore();
        const string uri = "spotify:playlist:p";
        store.UpsertTrack(Trk("t1"));
        store.UpsertPlaylist(new Playlist("p", uri, "My Mix", null, "owner_raw", null, 0));
        store.SetMembership(uri, new[] { new PlaylistMember("i1", "spotify:track:t1", "friend_raw", 1) }, null);
        store.SetRootlist(new[] { new RootlistEntry(0, 0, uri, null, 0) });
        var profiles = new FakeUserProfiles(new Owner("owner_raw", "Owner Display", null));
        var src = new StoreLibrarySource(store) { UserProfiles = profiles };
        _ = await src.GetPlaylistAsync(uri);
        _ = await src.GetPlaylistsAsync();

        int playlistBumps = 0;
        CollectionKind? collection = null;
        using var storeSub = store.Changes.Subscribe(new StoreObs(c => { if (c.Uri == uri) playlistBumps++; }));
        using var collectionSub = src.CollectionsChanged.Subscribe(new Obs(k => collection = k));

        profiles.Publish("friend_raw");
        profiles.Publish("owner_raw");

        Assert.True(playlistBumps >= 1);
        Assert.Equal(CollectionKind.Playlists, collection);
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

    sealed class StoreObs(System.Action<StoreChange> onNext) : System.IObserver<StoreChange>
    {
        public void OnNext(StoreChange v) => onNext(v);
        public void OnCompleted() { }
        public void OnError(System.Exception e) { }
    }

    sealed class FakeUserProfiles : IUserProfileService
    {
        readonly Dictionary<string, Owner> _profiles = new(System.StringComparer.Ordinal);
        readonly SimpleEvent<string> _changed = new();

        public FakeUserProfiles(params Owner[] profiles)
        {
            for (int i = 0; i < profiles.Length; i++)
            {
                var canonical = UserProfileIds.Normalize(profiles[i].Id);
                if (canonical is not null) _profiles[canonical] = profiles[i];
            }
        }

        public List<string> Prefetched { get; } = new();
        public System.IObservable<string> Changed => _changed;

        public Owner? Get(string userUriOrId)
        {
            var canonical = UserProfileIds.Normalize(userUriOrId);
            return canonical is not null && _profiles.TryGetValue(canonical, out var profile) ? profile : null;
        }

        public void Prefetch(IEnumerable<string> userUriOrIds)
        {
            foreach (var id in userUriOrIds)
                if (UserProfileIds.Normalize(id) is { } canonical) Prefetched.Add(canonical);
        }

        public void Publish(string userUriOrId)
        {
            if (UserProfileIds.Normalize(userUriOrId) is { } canonical) _changed.OnNext(canonical);
        }
    }

    // ── the collections default order: added-date DESC (newest first), the add time stamped on the read-model row ─────
    [Fact]
    public async Task GetLikedSongs_OrdersByAddedDesc_AndStampsAddedAt()
    {
        var store = new InMemoryStore();
        foreach (var id in new[] { "a", "b", "c", "z" }) store.UpsertTrack(Trk(id));
        store.SetSaved("liked", "spotify:track:a", true, SyncState.Confirmed, 1_000_000);
        store.SetSaved("liked", "spotify:track:b", true, SyncState.Confirmed, 3_000_000);   // newest
        store.SetSaved("liked", "spotify:track:c", true, SyncState.Confirmed, 2_000_000);
        store.SetSaved("liked", "spotify:track:z", true, SyncState.Confirmed);              // no timestamp → sinks last
        var src = new StoreLibrarySource(store);

        var liked = await src.GetLikedSongsAsync();
        Assert.Equal(new[] { "spotify:track:b", "spotify:track:c", "spotify:track:a", "spotify:track:z" },
            liked.Select(t => t.Uri).ToArray());
        Assert.Equal(System.DateTimeOffset.FromUnixTimeMilliseconds(3_000_000), liked[0].AddedAt);
        Assert.Null(liked[3].AddedAt);
    }

    // added_at semantics at the store: 0 preserves, non-zero refines silently (no extra change signal), survives unlike→relike reset.
    [Fact]
    public void SetSaved_AddedAt_PreservesOnZero_RefinesSilently()
    {
        var store = new InMemoryStore();
        var col = new ChangeCollector();
        store.SetSaved("liked", "spotify:track:x", true, SyncState.Pending, 5_000);   // optimistic like stamps local now
        using var sub = store.Changes.Subscribe(col);
        store.SetSaved("liked", "spotify:track:x", true, SyncState.Confirmed);        // ack: 0 → timestamp preserved
        Assert.Equal(5_000, Assert.Single(store.SavedItems("liked")).AddedAtMs);
        int after = col.All.Count;
        store.SetSaved("liked", "spotify:track:x", true, SyncState.Confirmed, 7_000); // server echo refines → silent
        Assert.Equal(7_000, Assert.Single(store.SavedItems("liked")).AddedAtMs);
        Assert.Equal(after, col.All.Count);                                           // no extra change signal
    }
}
