using System;
using Wavee.Core;
using Xunit;

namespace Wavee.Tests;

public class TrackFilterModelTests
{
    static Track Song(
        string title = "Blue Monday", string artist = "New Order", string album = "Power, Corruption & Lies",
        long duration = 450_000, bool explicitTrack = false, bool video = false,
        TrackOrigin origin = TrackOrigin.Streamed, Availability availability = Availability.Playable,
        DateTimeOffset? added = null) =>
        new("1", "spotify:track:1", title,
            [new ArtistRef("a", "spotify:artist:a", artist)],
            new AlbumRef("b", "spotify:album:b", album),
            duration, explicitTrack, null, added, HasVideo: video, Origin: origin, Availability: availability);

    [Fact]
    public void SearchScope_UsesOnlySelectedMetadata()
    {
        var song = Song();
        var title = new TrackFilterState(SearchScope: TrackSearchScope.Title);
        var artist = new TrackFilterState(SearchScope: TrackSearchScope.Artist);

        Assert.False(TrackFilterModel.Matches(song, "New Order", title, false, false, DateTimeOffset.UtcNow));
        Assert.True(TrackFilterModel.Matches(song, "New Order", artist, false, false, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void AlbumTrack_SupportsDurationAndAvailabilityFacets()
    {
        var song = Song(availability: Availability.Unavailable);
        var longOnly = new TrackFilterState(Duration: TrackDurationRange.OverFiveMinutes);
        var playable = new TrackFilterState(Flags: TrackFilterFlags.PlayableOnly);

        Assert.True(TrackFilterModel.Matches(song, "", longOnly, false, false, DateTimeOffset.UtcNow));
        Assert.False(TrackFilterModel.Matches(song, "", playable, false, false, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void PlaylistTrack_SupportsDateAndTraitModes()
    {
        var now = new DateTimeOffset(2026, 7, 27, 0, 0, 0, TimeSpan.Zero);
        var song = Song(explicitTrack: true, video: true, added: now.AddDays(-3));
        var filter = new TrackFilterState(
            ExplicitMode: TrackTraitMode.Hide,
            VideoMode: TrackTraitMode.Only,
            Added: TrackAddedRange.LastSevenDays);

        Assert.False(TrackFilterModel.Matches(song, "", filter, song.HasVideo, false, now));
        Assert.Equal(3, filter.ActiveCount);
    }

    [Theory]
    [InlineData(TrackTraitMode.All, false, true)]
    [InlineData(TrackTraitMode.All, true, true)]
    [InlineData(TrackTraitMode.Hide, false, true)]
    [InlineData(TrackTraitMode.Hide, true, false)]
    [InlineData(TrackTraitMode.Only, false, false)]
    [InlineData(TrackTraitMode.Only, true, true)]
    public void ExplicitTraitMode_ImplementsAllHideOnly(TrackTraitMode mode, bool isExplicit, bool expected)
    {
        var song = Song(explicitTrack: isExplicit);
        var filter = new TrackFilterState(ExplicitMode: mode);

        Assert.Equal(expected, TrackFilterModel.Matches(song, "", filter, false, false, DateTimeOffset.UtcNow));
    }

    [Theory]
    [InlineData(TrackTraitMode.All, false, true)]
    [InlineData(TrackTraitMode.All, true, true)]
    [InlineData(TrackTraitMode.Hide, false, true)]
    [InlineData(TrackTraitMode.Hide, true, false)]
    [InlineData(TrackTraitMode.Only, false, false)]
    [InlineData(TrackTraitMode.Only, true, true)]
    public void VideoTraitMode_ImplementsAllHideOnly(TrackTraitMode mode, bool hasVideo, bool expected)
    {
        var song = Song();
        var filter = new TrackFilterState(VideoMode: mode);

        Assert.Equal(expected, TrackFilterModel.Matches(song, "", filter, hasVideo, false, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void OriginAndLikedFacetsCompose()
    {
        var song = Song(origin: TrackOrigin.Local);
        var filter = new TrackFilterState(
            Flags: TrackFilterFlags.LikedOnly,
            Origin: TrackOriginFilter.Local);

        Assert.True(TrackFilterModel.Matches(song, "", filter, false, true, DateTimeOffset.UtcNow));
        Assert.False(TrackFilterModel.Matches(song, "", filter, false, false, DateTimeOffset.UtcNow));
    }
}
