using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Data.Sqlite;
using Wavee.Backend;
using Wavee.Backend.Metadata;
using Wavee.Backend.Persistence;
using Wavee.Core;
using Xunit;

namespace Wavee.Tests;

// CachedStore dual-write + reload logic, tested against a memory cold tier (no SQLite, fully deterministic).
public class CachedStoreTests
{
    sealed class MemCold : IColdStore
    {
        public readonly Dictionary<string, (EntityKind Kind, byte[] Payload)> Entities = new();
        public readonly Dictionary<(string, string), SyncState> Saved = new();
        public IEnumerable<ColdEntity> LoadAllEntities() { foreach (var kv in Entities) yield return new ColdEntity(kv.Key, kv.Value.Kind, kv.Value.Payload); }
        public IEnumerable<ColdSaved> LoadAllSaved() { foreach (var kv in Saved) yield return new ColdSaved(kv.Key.Item1, kv.Key.Item2, kv.Value); }
        public void UpsertEntity(string uri, EntityKind kind, byte[] payload) => Entities[uri] = (kind, payload);
        public readonly Dictionary<string, byte[]> VideoAssoc = new();
        public IEnumerable<ColdVideoAssoc> LoadAllVideoAssociations() { foreach (var kv in VideoAssoc) yield return new ColdVideoAssoc(kv.Key, kv.Value); }
        public void UpsertVideoAssociation(string uri, byte[] payload) => VideoAssoc[uri] = payload;
        public void UpsertSaved(string setId, string uri, bool saved, SyncState sync) { if (saved) Saved[(setId, uri)] = sync; else Saved.Remove((setId, uri)); }
        public readonly Dictionary<string, string?> Revisions = new();
        public string? GetCollectionRevision(string setId) => Revisions.TryGetValue(setId, out var r) ? r : null;
        public void SetCollectionRevision(string setId, string? revision, long syncedAt) => Revisions[setId] = revision;
        public readonly Dictionary<string, (IReadOnlyList<ColdPlaylistItem> Rows, byte[]? Rev)> Membership = new();
        public IReadOnlyList<ColdPlaylistItem> LoadMembership(string playlistUri) => Membership.TryGetValue(playlistUri, out var m) ? m.Rows : Array.Empty<ColdPlaylistItem>();
        public void ReplaceMembership(string playlistUri, IReadOnlyList<ColdPlaylistItem> rows, byte[]? baseRev) => Membership[playlistUri] = (rows, baseRev);
        public byte[]? GetPlaylistRevision(string playlistUri) => Membership.TryGetValue(playlistUri, out var m) ? m.Rev : null;
        public IReadOnlyList<ColdRootlistEntry> Rootlist = Array.Empty<ColdRootlistEntry>();
        public IReadOnlyList<ColdRootlistEntry> LoadRootlist() => Rootlist;
        public void ReplaceRootlist(IReadOnlyList<ColdRootlistEntry> entries) => Rootlist = entries;
        public void Flush() { }
        public void Dispose() { }
    }

    static Track Trk(string id) => new(id, "spotify:track:" + id, "Title " + id,
        [new ArtistRef("a", "spotify:artist:a", "Artist")], new AlbumRef("al", "spotify:album:al", "Album"), 1000, false, null);

    [Fact]
    public void Write_DualWrites_MemoryAndCold()
    {
        var cold = new MemCold();
        var store = new CachedStore(cold);
        store.UpsertTrack(Trk("t1"));
        Assert.NotNull(store.GetTrack("spotify:track:t1"));        // in memory
        Assert.True(cold.Entities.ContainsKey("spotify:track:t1")); // and persisted
    }

    [Fact]
    public void Reload_BulkLoadsColdIntoMemory_OnStartup()
    {
        var cold = new MemCold();
        new CachedStore(cold).UpsertTrack(Trk("t1"));     // instance 1 persists
        var store2 = new CachedStore(cold);               // "restart" over the same cold tier
        var t = store2.GetTrack("spotify:track:t1");
        Assert.NotNull(t);
        Assert.Equal("Title t1", t!.Title);
        Assert.Equal("Artist", t.Artists[0].Name);        // full record round-trips
    }

    [Fact]
    public void SavedLibraryState_Persists()
    {
        var cold = new MemCold();
        new CachedStore(cold).SetSaved("liked", "spotify:track:t1", true, SyncState.Confirmed);
        Assert.True(new CachedStore(cold).IsSaved("liked", "spotify:track:t1"));   // survives "restart"
    }

    [Fact]
    public void VideoAssociation_DualWrites_AndReloads()
    {
        var cold = new MemCold();
        var assoc = new VideoAssociation("spotify:track:t1", true, "spotify:track:vid",
            new[] { new VideoFileRef("ab6742d3000053b751ab106a1c8edd63fa934530", 0, 2560, 1440) },
            "etag1", DateTimeOffset.UtcNow, 2592000);
        new CachedStore(cold).UpsertVideoAssociation(assoc);
        Assert.True(cold.VideoAssoc.ContainsKey("spotify:track:t1"));   // persisted to its own cold table

        var a = new CachedStore(cold).GetVideoAssociation("spotify:track:t1");   // survives "restart"
        Assert.NotNull(a);
        Assert.True(a!.HasVideo);
        Assert.Equal("spotify:track:vid", a.CounterpartUri);
        Assert.Equal("ab6742d3000053b751ab106a1c8edd63fa934530", Assert.Single(a.Files).FileIdHex);
        Assert.Equal("etag1", a.Etag);
    }

    [Fact]
    public void AllEntityKinds_RoundTrip()
    {
        var cold = new MemCold();
        var s = new CachedStore(cold);
        s.UpsertTrack(Trk("t1"));
        s.UpsertAlbum(new Album("al", "spotify:album:al", "Album", null, [], 2020, 1));
        s.UpsertArtist(new Artist("ar", "spotify:artist:ar", "The Artist", null, Followers: 99));
        s.UpsertPlaylist(new Playlist("p", "spotify:playlist:p", "Mix", null, "Me", null, 0));

        var s2 = new CachedStore(cold);
        Assert.NotNull(s2.GetTrack("spotify:track:t1"));
        Assert.Equal("Album", s2.GetAlbum("spotify:album:al")!.Name);
        Assert.Equal(99, s2.GetArtist("spotify:artist:ar")!.Followers);
        Assert.Equal("Mix", s2.GetPlaylist("spotify:playlist:p")!.Name);
    }

    [Fact]
    public void Playlist_PersistsThin_NotTheFatTrackBlob()
    {
        var cold = new MemCold();
        var pl = new Playlist("p", "spotify:playlist:p", "Mix", null, "Me", null, 2, new[] { Trk("t1"), Trk("t2") });
        new CachedStore(cold).UpsertPlaylist(pl);

        var reloaded = new CachedStore(cold).GetPlaylist("spotify:playlist:p");   // re-deserialized from the persisted blob
        Assert.NotNull(reloaded);
        Assert.Null(reloaded!.Tracks);        // the hydrated tracklist is NOT baked into the entity blob (membership joins at read)
        Assert.Equal("Mix", reloaded.Name);   // header fields survive
    }

    [Fact]
    public void Album_PersistsThin_NotTheFatTrackBlob()
    {
        var cold = new MemCold();
        var al = new Album("al", "spotify:album:al", "Al", null, [], 2020, 2, new[] { Trk("t1"), Trk("t2") });
        new CachedStore(cold).UpsertAlbum(al);

        var reloaded = new CachedStore(cold).GetAlbum("spotify:album:al");
        Assert.NotNull(reloaded);
        Assert.Null(reloaded!.Tracks);
        Assert.Equal("Al", reloaded.Name);
    }

    [Fact]
    public void ShowAndEpisode_RoundTrip()
    {
        var cold = new MemCold();
        var s = new CachedStore(cold);
        s.UpsertShow(new Show("sh", "spotify:show:sh", "My Show", "Acme Media", null));
        s.UpsertEpisode(new Episode("ep", "spotify:episode:ep", "Ep 1", "My Show", null, 5000, DateTimeOffset.UnixEpoch));

        var s2 = new CachedStore(cold);                       // "restart" over the same cold tier
        Assert.Equal("My Show", s2.GetShow("spotify:show:sh")!.Name);
        Assert.Equal("Acme Media", s2.GetShow("spotify:show:sh")!.Publisher);
        Assert.Equal("Ep 1", s2.GetEpisode("spotify:episode:ep")!.Title);
        Assert.Equal("My Show", s2.GetEpisode("spotify:episode:ep")!.ShowName);
    }
}

// The REAL SQLite tier — durability across process-like restarts + bulk write-behind.
public class SqliteColdStoreTests
{
    static string TempDb() => Path.Combine(Path.GetTempPath(), "wavee-test-" + Guid.NewGuid().ToString("N") + ".db");
    static void TryDelete(string p) { foreach (var f in new[] { p, p + "-wal", p + "-shm" }) { try { File.Delete(f); } catch { } } }
    static Track Trk(string id) => new(id, "spotify:track:" + id, "Title " + id, [], new AlbumRef("", "", ""), 1000, false, null);

    [Fact]
    public void Persists_AcrossInstances()
    {
        var path = TempDb();
        try
        {
            using (var store = new CachedStore(new SqliteColdStore(path)))
            {
                store.UpsertTrack(Trk("t1"));
                store.UpsertAlbum(new Album("al", "spotify:album:al", "Al", null, [], 2020, 1));
                store.SetSaved("liked", "spotify:track:t1", true, SyncState.Confirmed);
                store.Flush();   // make the write-behind durable before we drop the instance
            }   // Dispose drains + closes

            using var store2 = new CachedStore(new SqliteColdStore(path));   // reopen the same file
            Assert.Equal("Title t1", store2.GetTrack("spotify:track:t1")!.Title);
            Assert.NotNull(store2.GetAlbum("spotify:album:al"));
            Assert.True(store2.IsSaved("liked", "spotify:track:t1"));
        }
        finally { TryDelete(path); }
    }

    [Fact]
    public void BulkDualWrite_10k_AllPersist()
    {
        var path = TempDb();
        try
        {
            using (var store = new CachedStore(new SqliteColdStore(path)))
            {
                for (int i = 0; i < 10_000; i++) store.UpsertTrack(Trk("t" + i));   // synchronous part: memory + enqueue
                store.Flush();
            }
            using var store2 = new CachedStore(new SqliteColdStore(path));
            Assert.Equal(10_000, store2.QueryTracks(limit: 20_000).Count);   // write-behind persisted every one
        }
        finally { TryDelete(path); }
    }

    [Fact]
    public void Migration_V0_FoldsLegacySavedIntoCollectionItems()
    {
        var path = TempDb();
        try
        {
            // Seed a legacy v0 db by hand: the old `saved` table, no `meta` schema_version.
            using (var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path }.ToString()))
            {
                c.Open();
                using var cmd = c.CreateCommand();
                cmd.CommandText =
                    "CREATE TABLE saved(setid TEXT NOT NULL, uri TEXT NOT NULL, sync INTEGER NOT NULL, PRIMARY KEY(setid,uri));" +
                    "INSERT INTO saved VALUES('liked','spotify:track:t1',0);";
                cmd.ExecuteNonQuery();
            }

            // Open with the current store → the migration runner folds saved → collection_items and drops saved.
            using (var store = new CachedStore(new SqliteColdStore(path)))
                Assert.True(store.IsSaved("liked", "spotify:track:t1"));   // migrated membership is loaded on startup

            // The legacy table is gone and the schema is versioned.
            using var verify = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path }.ToString());
            verify.Open();
            using var q = verify.CreateCommand();
            q.CommandText = "SELECT (SELECT count(*) FROM sqlite_master WHERE type='table' AND name='saved'), (SELECT value FROM meta WHERE key='schema_version');";
            using var r = q.ExecuteReader();
            Assert.True(r.Read());
            Assert.Equal(0L, r.GetInt64(0));        // `saved` dropped
            Assert.Equal("1", r.GetString(1));      // schema_version = 1
        }
        finally { TryDelete(path); }
    }

    [Fact]
    public void CollectionRevision_RoundTrips_AcrossInstances()
    {
        var path = TempDb();
        try
        {
            using (var s = new SqliteColdStore(path))
            {
                Assert.Null(s.GetCollectionRevision("liked"));   // unset → null
                s.SetCollectionRevision("liked", "5,abc123", 1700);
                s.Flush();
            }
            using var s2 = new SqliteColdStore(path);
            Assert.Equal("5,abc123", s2.GetCollectionRevision("liked"));
        }
        finally { TryDelete(path); }
    }

    [Fact]
    public void PlaylistMembership_RoundTrips_OrderedWithRevision()
    {
        var path = TempDb();
        try
        {
            var rev = new byte[] { 1, 2, 3, 4, 0xAB };
            using (var s = new SqliteColdStore(path))
                s.ReplaceMembership("spotify:playlist:p", new[]
                {
                    new ColdPlaylistItem("id1", "spotify:track:a", "alice", 100),
                    new ColdPlaylistItem("id2", "spotify:track:b", null, 200),
                }, rev);

            using var s2 = new SqliteColdStore(path);   // reopen → membership + revision durable
            var rows = s2.LoadMembership("spotify:playlist:p");
            Assert.Equal(2, rows.Count);
            Assert.Equal("spotify:track:a", rows[0].ItemUri);     // ordered by position
            Assert.Equal("id1", rows[0].ItemId);
            Assert.Equal("alice", rows[0].AddedBy);
            Assert.Equal(100, rows[0].AddedAt);
            Assert.Null(rows[1].AddedBy);                          // a null added_by round-trips
            Assert.Equal(rev, s2.GetPlaylistRevision("spotify:playlist:p"));
        }
        finally { TryDelete(path); }
    }

    [Fact]
    public void ReplaceMembership_ReplacesNotAppends()
    {
        var path = TempDb();
        try
        {
            using var s = new SqliteColdStore(path);
            s.ReplaceMembership("p", new[] { new ColdPlaylistItem("a", "spotify:track:a", null, 0), new ColdPlaylistItem("b", "spotify:track:b", null, 0) }, null);
            s.ReplaceMembership("p", new[] { new ColdPlaylistItem("c", "spotify:track:c", null, 0) }, null);
            var rows = s.LoadMembership("p");
            Assert.Single(rows);                                   // the prior two rows are gone, not appended to
            Assert.Equal("spotify:track:c", rows[0].ItemUri);
        }
        finally { TryDelete(path); }
    }

    [Fact]
    public void Rootlist_RoundTrips_OrderedWithFolders()
    {
        var path = TempDb();
        try
        {
            using (var s = new SqliteColdStore(path))
                s.ReplaceRootlist(new[]
                {
                    new ColdRootlistEntry(0, 1, "spotify:start-group:g1:Folder", "Folder", 0),
                    new ColdRootlistEntry(1, 0, "spotify:playlist:p1", null, 1),
                    new ColdRootlistEntry(2, 2, "spotify:end-group:g1", null, 0),
                });

            using var s2 = new SqliteColdStore(path);
            var rl = s2.LoadRootlist();
            Assert.Equal(3, rl.Count);
            Assert.Equal("spotify:playlist:p1", rl[1].Uri);
            Assert.Equal("Folder", rl[0].GroupName);
            Assert.Equal(1, rl[1].Depth);
        }
        finally { TryDelete(path); }
    }
}
