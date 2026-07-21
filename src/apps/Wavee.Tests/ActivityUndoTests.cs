using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Wavee;
using Wavee.Core;
using Xunit;

namespace Wavee.Tests;

// ActivityUndoExecutor: the full undo-mapping table against a fake IUndoTarget + a fake library, plus stale-undo → false
// and "an undo does not re-record" (suppression). Engine-free — the executor depends only on IUndoTarget / IMusicLibrary /
// ActivityLog.
public class ActivityUndoTests
{
    static ActivityLog NewLog() => new(new InMemoryActivityStore());

    [Fact]
    public async Task Save_Undo_UnsavesAndDoesNotReRecord()
    {
        var log = NewLog();
        var target = new FakeUndoTarget(log);
        var exec = new ActivityUndoExecutor(target, new FakeLibrary(), log);

        long id = log.Record(ActivityKind.Save, "spotify:track:a");
        bool ok = await exec.UndoAsync(log.Snapshot[0]);

        Assert.True(ok);
        Assert.Equal(("spotify:track:a", false), Assert.Single(target.Saves));
        Assert.Single(log.Snapshot);                                    // the fake re-recorded nothing (suppressed)
        Assert.Equal(ActivityStatus.Undone, log.Snapshot[0].Status);
    }

    [Fact]
    public async Task Unsave_Undo_ReSaves()
    {
        var log = NewLog();
        var target = new FakeUndoTarget(log);
        var exec = new ActivityUndoExecutor(target, new FakeLibrary(), log);
        log.Record(ActivityKind.Unsave, "spotify:album:x");
        Assert.True(await exec.UndoAsync(log.Snapshot[0]));
        Assert.Equal(("spotify:album:x", true), Assert.Single(target.Saves));
    }

    [Fact]
    public async Task Rename_Undo_RestoresOldName()
    {
        var log = NewLog();
        var target = new FakeUndoTarget(log);
        var exec = new ActivityUndoExecutor(target, new FakeLibrary(), log);
        log.Record(ActivityKind.PlaylistRename, "spotify:playlist:p", "New", new ActivityPayload(OldName: "Old", NewName: "New"));
        Assert.True(await exec.UndoAsync(log.Snapshot[0]));
        Assert.Equal(("spotify:playlist:p", "Old"), Assert.Single(target.Renames));
    }

    [Fact]
    public async Task Visibility_Undo_Inverts()
    {
        var log = NewLog();
        var target = new FakeUndoTarget(log);
        var exec = new ActivityUndoExecutor(target, new FakeLibrary(), log);
        log.Record(ActivityKind.PlaylistVisibility, "spotify:playlist:p", null, new ActivityPayload(NewIsPublic: true));
        Assert.True(await exec.UndoAsync(log.Snapshot[0]));
        Assert.Equal(("spotify:playlist:p", false), Assert.Single(target.Visibilities));
    }

    [Fact]
    public async Task Add_Undo_RemovesResolvedRows()
    {
        var log = NewLog();
        var lib = new FakeLibrary
        {
            Playlist = MakePlaylist("spotify:playlist:p",
                ("spotify:track:a", "uid-a"), ("spotify:track:b", "uid-b")),
        };
        var target = new FakeUndoTarget(log);
        var exec = new ActivityUndoExecutor(target, lib, log);
        log.Record(ActivityKind.PlaylistAddTracks, "spotify:playlist:p", null,
            new ActivityPayload(Tracks: new[] { new ActivityTrackRef("spotify:track:a"), new ActivityTrackRef("spotify:track:b") }));

        Assert.True(await exec.UndoAsync(log.Snapshot[0]));
        Assert.Equal(("spotify:playlist:p", 2), Assert.Single(target.Removes));
    }

    [Fact]
    public async Task Add_Undo_RowsGone_Fails_KeepsDone()
    {
        var log = NewLog();
        var lib = new FakeLibrary { Playlist = MakePlaylist("spotify:playlist:p") };   // empty → nothing to remove
        var target = new FakeUndoTarget(log);
        var exec = new ActivityUndoExecutor(target, lib, log);
        log.Record(ActivityKind.PlaylistAddTracks, "spotify:playlist:p", null,
            new ActivityPayload(Tracks: new[] { new ActivityTrackRef("spotify:track:a") }));

        Assert.False(await exec.UndoAsync(log.Snapshot[0]));
        Assert.Empty(target.Removes);
        Assert.Equal(ActivityStatus.Done, log.Snapshot[0].Status);   // not flipped to Undone on failure
    }

    [Fact]
    public async Task Remove_Undo_ReAddsStoredTracks()
    {
        var log = NewLog();
        var target = new FakeUndoTarget(log);
        var exec = new ActivityUndoExecutor(target, new FakeLibrary(), log);
        log.Record(ActivityKind.PlaylistRemoveTracks, "spotify:playlist:p", null,
            new ActivityPayload(Tracks: new[] { new ActivityTrackRef("spotify:track:a", "Song A"), new ActivityTrackRef("spotify:track:b", "Song B") }));

        Assert.True(await exec.UndoAsync(log.Snapshot[0]));
        Assert.Equal(("spotify:playlist:p", 2), Assert.Single(target.Adds));
    }

    [Fact]
    public async Task Move_Undo_MovesBackToFromIndex()
    {
        var log = NewLog();
        var lib = new FakeLibrary { Playlist = MakePlaylist("spotify:playlist:p", ("spotify:track:a", "uid-a")) };
        var target = new FakeUndoTarget(log);
        var exec = new ActivityUndoExecutor(target, lib, log);
        log.Record(ActivityKind.PlaylistMoveTracks, "spotify:playlist:p", null,
            new ActivityPayload(Tracks: new[] { new ActivityTrackRef("spotify:track:a", null, "uid-a") }, FromIndex: 0, ToIndex: 3));

        Assert.True(await exec.UndoAsync(log.Snapshot[0]));
        var move = Assert.Single(target.Moves);
        Assert.Equal("spotify:playlist:p", move.uri);
        Assert.Equal(0, move.toIndex);   // back to FromIndex
    }

    [Fact]
    public async Task Move_Undo_ItemIdMissing_Fails()
    {
        var log = NewLog();
        var lib = new FakeLibrary { Playlist = MakePlaylist("spotify:playlist:p", ("spotify:track:a", "different-uid")) };
        var target = new FakeUndoTarget(log);
        var exec = new ActivityUndoExecutor(target, lib, log);
        log.Record(ActivityKind.PlaylistMoveTracks, "spotify:playlist:p", null,
            new ActivityPayload(Tracks: new[] { new ActivityTrackRef("spotify:track:a", null, "uid-a") }, FromIndex: 0, ToIndex: 3));

        Assert.False(await exec.UndoAsync(log.Snapshot[0]));
        Assert.Empty(target.Moves);
    }

    [Fact]
    public async Task NonUndoableKind_ReturnsFalse_NoCalls()
    {
        var log = NewLog();
        var target = new FakeUndoTarget(log);
        var exec = new ActivityUndoExecutor(target, new FakeLibrary(), log);
        log.Record(ActivityKind.PlaylistCreate, "spotify:playlist:p", "My List");

        Assert.False(await exec.UndoAsync(log.Snapshot[0]));
        Assert.Empty(target.Saves);
        Assert.Empty(target.Renames);
    }

    // ── fakes ────────────────────────────────────────────────────────────────────────────────────────────────────────
    static Playlist MakePlaylist(string uri, params (string uri, string uid)[] tracks)
    {
        var list = new List<Track>(tracks.Length);
        foreach (var (u, uid) in tracks)
            list.Add(new Track(u.Substring(u.LastIndexOf(':') + 1), u, "T", Array.Empty<ArtistRef>(), new AlbumRef("", "", ""), 0, false, null, ContextUid: uid));
        return new Playlist("p", uri, "P", null, "me", null, list.Count, list);
    }

    sealed class FakeUndoTarget : IUndoTarget
    {
        readonly ActivityLog? _log;
        public FakeUndoTarget(ActivityLog? log = null) => _log = log;

        public List<(string uri, bool saved)> Saves { get; } = new();
        public List<(string uri, string? name)> Renames { get; } = new();
        public List<(string uri, bool pub)> Visibilities { get; } = new();
        public List<(string uri, int count)> Adds { get; } = new();
        public List<(string uri, int count)> Removes { get; } = new();
        public List<(string uri, int toIndex, int count)> Moves { get; } = new();

        // Simulate the real LibraryBridge recording — proves the executor's SuppressRecording keeps the undo from logging.
        public void SetSaved(string uri, bool saved)
        {
            Saves.Add((uri, saved));
            _log?.Record(saved ? ActivityKind.Save : ActivityKind.Unsave, uri);
        }

        public Task UpdatePlaylistDetailsAsync(string playlistUri, string? name, string? description, bool? collaborative, string? previousName = null, CancellationToken ct = default)
        { Renames.Add((playlistUri, name)); return Task.CompletedTask; }

        public Task SetPlaylistVisibilityAsync(string playlistUri, bool isPublic, CancellationToken ct = default)
        { Visibilities.Add((playlistUri, isPublic)); return Task.CompletedTask; }

        public Task AddTracksAsync(string playlistUri, IReadOnlyList<Track> tracks, CancellationToken ct = default)
        { Adds.Add((playlistUri, tracks.Count)); return Task.CompletedTask; }

        public Task RemovePlaylistRowsAsync(string playlistUri, IReadOnlyList<PlaylistRowRef> rows, IReadOnlyList<ActivityTrackRef>? removedTracks = null, CancellationToken ct = default)
        { Removes.Add((playlistUri, rows.Count)); return Task.CompletedTask; }

        public Task MovePlaylistRowsAsync(string playlistUri, IReadOnlyList<PlaylistRowRef> rows, int toIndex, CancellationToken ct = default)
        { Moves.Add((playlistUri, toIndex, rows.Count)); return Task.CompletedTask; }
    }

    sealed class FakeLibrary : IMusicLibrary
    {
        public Playlist Playlist { get; set; } = new("p", "spotify:playlist:p", "P", null, "me", null, 0, new List<Track>());
        public Task<Playlist> GetPlaylistAsync(string id, CancellationToken ct = default) => Task.FromResult(Playlist);

        // Unused by the undo path.
        public Task<Album> GetAlbumAsync(string id, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Artist> GetArtistAsync(string id, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<DiscographyPage> GetDiscographyAsync(string artistUri, DiscographyKind kind, int offset, int limit, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<LibraryItem>> GetLibraryAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<SearchResults> SearchAsync(string query, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<SearchResults> SearchAsync(string query, SearchFacet facet, int offset, int limit, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<string>> SuggestAsync(string query, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<SearchSuggestions> SuggestRichAsync(string query, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<Album>> GetAlbumsAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<Artist>> GetArtistsAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<Track>> GetLikedSongsAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<LibraryStats> GetStatsAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<PlaylistSummary>> GetPlaylistsAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public IAsyncEnumerable<TrackPage> StreamTracksAsync(string contextUri, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<HomeFeed> GetHomeAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<Show>> GetShowsAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Show?> GetShowAsync(string uri, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
