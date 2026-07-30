using Wavee.Backend;
using Wavee.Core;
using Xunit;

namespace Wavee.Tests;

public class StoreEntityGapsTests
{
    [Fact]
    public void RefNeedsName_UriWithoutName()
    {
        Assert.True(StoreEntityGaps.RefNeedsName(new AlbumRef("a", "spotify:album:a", "")));
        Assert.False(StoreEntityGaps.RefNeedsName(new AlbumRef("a", "spotify:album:a", "Named")));
        Assert.False(StoreEntityGaps.RefNeedsName(new AlbumRef("", "", "")));
    }

    [Fact]
    public void TrackNeedsData_TitleUriEcho()
    {
        var t = new Track("t", "spotify:track:t", "spotify:track:t", [], new AlbumRef("", "", ""), 0, false, null);
        Assert.True(StoreEntityGaps.TrackNeedsData(t));
        Assert.True(StoreEntityGaps.TrackUnnamed(t));
    }

    [Fact]
    public void NowPlayingReady_RequiresNamedAlbum()
    {
        var img = new Image("https://i.scdn.co/image/x");
        var artists = new[] { new ArtistRef("a", "spotify:artist:a", "A") };
        var thinAlbum = new Track("t", "spotify:track:t", "Title", artists, new AlbumRef("a", "spotify:album:a", ""), 1000, false, img);
        Assert.False(StoreEntityGaps.NowPlayingReady(thinAlbum));
        var ready = thinAlbum with { Album = new AlbumRef("a", "spotify:album:a", "Album") };
        Assert.True(StoreEntityGaps.NowPlayingReady(ready));
    }
}
