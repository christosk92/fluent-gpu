using Wavee.Backend.Hydration;
using Wavee.Core;
using Xunit;

namespace Wavee.Tests;

// The two surface tables (design §2.4). Pinned exhaustively because they are the ONLY thing deciding what a POST
// carries — the four separate services each had their own answer, which is how the album page ended up asking for
// kind 185 twice and the show page asking for nothing.
public class TraitPolicyTests
{
    static TraitPolicy Policy(bool plays = false) => new(() => plays);

    [Theory]
    [InlineData(TraitSurface.AlbumOpen, TraitSet.RowBundle | TraitSet.PlayCount | TraitSet.Publishing)]
    [InlineData(TraitSurface.ShowOpen, TraitSet.RowBundle)]
    [InlineData(TraitSurface.ArtistPopular, TraitSet.RowBundle | TraitSet.PlayCount)]
    [InlineData(TraitSurface.Queue, TraitSet.RowBundle)]
    [InlineData(TraitSurface.Search, TraitSet.RowBundle)]
    [InlineData(TraitSurface.Recents, TraitSet.IdentityTraits | TraitSet.VisualIdentity)]
    [InlineData(TraitSurface.NowPlaying, TraitSet.Video)]
    [InlineData(TraitSurface.PlaysToggle, TraitSet.PlayCount)]
    [InlineData(TraitSurface.None, TraitSet.None)]
    [InlineData(TraitSurface.Prefetch, TraitSet.None)]
    [InlineData(TraitSurface.Context, TraitSet.None)]
    [InlineData(TraitSurface.Credits, TraitSet.None)]
    public void For_IsTheTable(TraitSurface surface, TraitSet expected)
        => Assert.Equal(expected, Policy().For(surface));

    [Theory]
    [InlineData(TraitSurface.PlaylistOpen)]
    [InlineData(TraitSurface.LikedSongs)]
    public void ListSurfaces_AddPlayCounts_OnlyWhenTheColumnIsOn(TraitSurface surface)
    {
        Assert.Equal(TraitSet.RowBundle, Policy(plays: false).For(surface));
        Assert.Equal(TraitSet.RowBundle | TraitSet.PlayCount, Policy(plays: true).For(surface));
    }

    [Fact]
    public void AlbumOpen_AsksForCountsRegardlessOfTheColumn()
        // The Plays STAR is the album surface's own identity, so it does not wait for the list column setting.
        => Assert.Equal(Policy(plays: false).For(TraitSurface.AlbumOpen), Policy(plays: true).For(TraitSurface.AlbumOpen));

    [Theory]
    [InlineData(TraitSurface.Recents, "mdata_esperanto")]
    [InlineData(TraitSurface.AlbumOpen, "track_metadata_loader")]
    [InlineData(TraitSurface.PlaylistOpen, "track_metadata_loader")]
    [InlineData(TraitSurface.None, null)]
    [InlineData(TraitSurface.PreRelease, null)]
    [InlineData(TraitSurface.UserProfiles, null)]
    public void ClientFeatureId_IsTheAttributionTable(TraitSurface surface, string? expected)
        => Assert.Equal(expected, surface.ClientFeatureId());
}
