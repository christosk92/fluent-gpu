using System;
using System.Collections.Generic;
using Wavee.Backend;
using Wavee.Core;
using Xunit;

namespace Wavee.Tests;

// Repro asserts for StoreEntityMerge.{Playlist,Show,Episode,Track} — the ClearDescription / ClearPicture /
// dead-letter / thin-ShowName / IsPublic-without-permission cases that blocked a blanket-NonEmpty Playlist merge.
public class StoreEntityMergeTests
{
    const string PlUri = "spotify:playlist:p1";
    const string EpUri = "spotify:episode:e1";
    const string TrUri = "spotify:track:t1";

    static Playlist Pl(
        string name = "My List",
        string? description = "hello",
        Image? cover = null,
        bool isPublic = true,
        string? basePermRev = null,
        PlaylistCapabilities? caps = null) =>
        new("p1", PlUri, name, description, "owner",
            cover ?? new Image("https://i.scdn.co/image/abc"),
            TrackCount: 3,
            Capabilities: caps ?? new PlaylistCapabilities(true, true, true, false, true),
            IsPublic: isPublic,
            BasePermissionRevision: basePermRev);

    [Fact]
    public void ClearDescription_NullSticks()
    {
        var store = new InMemoryStore();
        store.UpsertPlaylist(Pl(description: "keep me"));
        store.UpsertPlaylist(Pl(description: null));   // ClearDescription
        Assert.Null(store.GetPlaylist(PlUri)!.Description);
    }

    [Fact]
    public void ClearPicture_NullCoverSticks()
    {
        var store = new InMemoryStore();
        store.UpsertPlaylist(Pl(cover: new Image("https://i.scdn.co/image/keep")));
        store.UpsertPlaylist(Pl(cover: null));   // ClearPicture
        Assert.Null(store.GetPlaylist(PlUri)!.Cover);
    }

    [Fact]
    public void DeadLetterRollback_RestoresPriorHeader()
    {
        var store = new InMemoryStore();
        var prior = Pl(name: "Before", description: "old");
        store.UpsertPlaylist(prior);
        var snapshot = store.GetPlaylist(PlUri)!;
        store.UpsertPlaylist(Pl(name: "Broken", description: "oops"));
        store.UpsertPlaylist(snapshot);   // Mutation dead-letter rollback
        var got = store.GetPlaylist(PlUri)!;
        Assert.Equal("Before", got.Name);
        Assert.Equal("old", got.Description);
    }

    [Fact]
    public void ThinEpisodeShowName_CannotClobberKnownName()
    {
        var store = new InMemoryStore();
        store.UpsertEpisode(new Episode("e1", EpUri, "Ep", "Real Show", null, 60_000, DateTimeOffset.UtcNow));
        store.UpsertEpisode(new Episode("e1", EpUri, "Ep", "", null, 60_000, DateTimeOffset.UtcNow));
        Assert.Equal("Real Show", store.GetEpisode(EpUri)!.ShowName);
    }

    [Fact]
    public void HeaderRefetchWithoutPermissionFields_CannotResetIsPublic()
    {
        var store = new InMemoryStore();
        store.UpsertPlaylist(Pl(isPublic: false, basePermRev: "rev-1"));
        // Header-only refetch: no BasePermissionRevision → IsPublic stays private.
        store.UpsertPlaylist(Pl(isPublic: true, basePermRev: null));
        Assert.False(store.GetPlaylist(PlUri)!.IsPublic);
        Assert.Equal("rev-1", store.GetPlaylist(PlUri)!.BasePermissionRevision);
    }

    [Fact]
    public void Track_TitleUriEcho_DoesNotClobberResolvedTitle()
    {
        var store = new InMemoryStore();
        store.UpsertTrack(new Track("t1", TrUri, "Real Title", [], new AlbumRef("", "", ""), 1000, false, null));
        store.UpsertTrack(new Track("t1", TrUri, TrUri, [], new AlbumRef("", "", ""), 1000, false, null));
        Assert.Equal("Real Title", store.GetTrack(TrUri)!.Title);
    }

    [Fact]
    public void Track_SameSourceCover_PrefersIncoming()
    {
        const string url = "https://i.scdn.co/image/same";
        var current = new Track("t1", TrUri, "T", [], new AlbumRef("", "", ""), 1000, false,
            new Image(url, Width: 64, Height: 64));
        var incoming = new Track("t1", TrUri, "T", [], new AlbumRef("", "", ""), 1000, false,
            new Image(url, Width: 640, Height: 640));
        var merged = StoreEntityMerge.Track(current, incoming);
        Assert.Equal(640, merged.Image!.Width);
    }

    [Fact]
    public void Track_DifferentSource_ChoosesHigherQuality()
    {
        var current = new Track("t1", TrUri, "T", [], new AlbumRef("", "", ""), 1000, false,
            new Image("https://i.scdn.co/image/hi", Width: 640, Height: 640));
        var incoming = new Track("t1", TrUri, "T", [], new AlbumRef("", "", ""), 1000, false,
            new Image("https://i.scdn.co/image/lo", Width: 64, Height: 64));
        var merged = StoreEntityMerge.Track(current, incoming);
        Assert.Equal("https://i.scdn.co/image/hi", merged.Image!.Url);
    }
}
