using Wavee.Backend.Persistence;
using Wavee.Backend.Playlists;
using Wavee.Core;
using Xunit;

namespace Wavee.Tests;

// Flat rootlist marker stream (playlist uris + start/end-group markers) → the sidebar PlaylistNode tree.
public class RootlistTreeBuilderTests
{
    static PlaylistSummary Resolve(string uri) => new(uri, "Name-" + uri.Split(':')[^1], "Owner", 0, null);

    [Fact]
    public void TopLevelPlaylists_BecomeLeaves()
    {
        var entries = new[]
        {
            new ColdRootlistEntry(0, 0, "spotify:playlist:p1", null, 0),
            new ColdRootlistEntry(1, 0, "spotify:playlist:p2", null, 0),
        };
        var tree = RootlistTreeBuilder.Build(entries, Resolve);
        Assert.Equal(2, tree.Count);
        Assert.Equal("spotify:playlist:p1", Assert.IsType<PlaylistLeaf>(tree[0]).Playlist.Uri);
        Assert.Equal("spotify:playlist:p2", Assert.IsType<PlaylistLeaf>(tree[1]).Playlist.Uri);
    }

    [Fact]
    public void Folder_GroupsItsPlaylists_AndLeavesFollow()
    {
        var entries = new[]
        {
            new ColdRootlistEntry(0, 1, "spotify:start-group:g1:My%20Folder", "My Folder", 0),
            new ColdRootlistEntry(1, 0, "spotify:playlist:p1", null, 1),
            new ColdRootlistEntry(2, 0, "spotify:playlist:p2", null, 1),
            new ColdRootlistEntry(3, 2, "spotify:end-group:g1", null, 0),
            new ColdRootlistEntry(4, 0, "spotify:playlist:p3", null, 0),
        };
        var tree = RootlistTreeBuilder.Build(entries, Resolve);
        Assert.Equal(2, tree.Count);
        var folder = Assert.IsType<PlaylistFolder>(tree[0]);
        Assert.Equal("My Folder", folder.Name);
        Assert.Equal(2, folder.Items.Count);
        Assert.Equal("spotify:playlist:p1", folder.Items[0].Uri);
        Assert.Equal("spotify:playlist:p3", Assert.IsType<PlaylistLeaf>(tree[1]).Playlist.Uri);
    }

    [Fact]
    public void UnbalancedOpenFolder_StillFlushes()
    {
        var entries = new[]
        {
            new ColdRootlistEntry(0, 1, "spotify:start-group:g1:F", "F", 0),
            new ColdRootlistEntry(1, 0, "spotify:playlist:p1", null, 1),
            // missing end-group → must still surface the folder with its child
        };
        var tree = RootlistTreeBuilder.Build(entries, Resolve);
        Assert.Single(Assert.IsType<PlaylistFolder>(Assert.Single(tree)).Items);
    }
}
