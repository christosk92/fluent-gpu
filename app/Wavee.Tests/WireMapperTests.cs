using Google.Protobuf;
using Wavee.Backend.Collections;
using Wavee.Backend.Playlists;
using Xunit;
using Pl = Wavee.Protocol.Playlist;
using Col = Wavee.Protocol.Collection;

namespace Wavee.Tests;

// Map the playlist4 SelectedListContent/Op wire types onto the proto-free domain (PlaylistMember/PlaylistOp).
public class PlaylistWireMapperTests
{
    [Fact]
    public void ParseContents_ReadsOrderedMembers_AndRevision()
    {
        var slc = new Pl.SelectedListContent { Revision = ByteString.CopyFrom(1, 2, 3) };
        var contents = new Pl.ListItems { Pos = 0, Truncated = false };
        contents.Items.Add(new Pl.Item { Uri = "spotify:track:a", Attributes = new Pl.ItemAttributes { AddedBy = "alice", Timestamp = 1700, ItemId = ByteString.CopyFrom(0xAB) } });
        contents.Items.Add(new Pl.Item { Uri = "spotify:track:b" });
        slc.Contents = contents;

        var (members, rev) = PlaylistWireMapper.ParseContents(slc);
        Assert.Equal(2, members.Count);
        Assert.Equal("spotify:track:a", members[0].ItemUri);
        Assert.Equal("alice", members[0].AddedBy);
        Assert.Equal(1700L, members[0].AddedAt);
        Assert.Equal("ab", members[0].ItemId);          // item_id bytes → lowercase hex
        Assert.Null(members[1].AddedBy);
        Assert.Equal(new byte[] { 1, 2, 3 }, rev);
    }

    [Fact]
    public void MapOps_MapsAddRemMov()
    {
        var add = new Pl.Op { Kind = Pl.Op.Types.Kind.Add, Add = new Pl.Add { FromIndex = 1 } };
        add.Add.Items.Add(new Pl.Item { Uri = "spotify:track:x" });
        var rem = new Pl.Op { Kind = Pl.Op.Types.Kind.Rem, Rem = new Pl.Rem { FromIndex = 2, Length = 3 } };
        var mov = new Pl.Op { Kind = Pl.Op.Types.Kind.Mov, Mov = new Pl.Mov { FromIndex = 0, Length = 1, ToIndex = 4 } };

        var ops = PlaylistWireMapper.MapOps(new[] { add, rem, mov });
        Assert.Equal(3, ops.Count);
        Assert.Equal(PlaylistOpKind.Add, ops[0].Kind);
        Assert.Equal(1, ops[0].FromIndex);
        Assert.Equal("spotify:track:x", ops[0].Items![0].ItemUri);
        Assert.Equal(PlaylistOpKind.Remove, ops[1].Kind);
        Assert.Equal(3, ops[1].Length);
        Assert.Equal(PlaylistOpKind.Move, ops[2].Kind);
        Assert.Equal(4, ops[2].ToIndex);
    }
}

// Map the collection2v2 DeltaResponse/PageResponse onto the domain CollectionDelta.
public class CollectionWireMapperTests
{
    [Fact]
    public void ParseDelta_MapsItemsAndToken()
    {
        var resp = new Col.DeltaResponse { DeltaUpdatePossible = true, SyncToken = "tok-9" };
        resp.Items.Add(new Col.CollectionItem { Uri = "spotify:album:a", AddedAt = 100, IsRemoved = false });
        resp.Items.Add(new Col.CollectionItem { Uri = "spotify:album:b", IsRemoved = true });

        var delta = CollectionWireMapper.ParseDelta("albums", resp);
        Assert.Equal("albums", delta.SetId);
        Assert.Equal("tok-9", delta.NewRevision);
        Assert.Equal(2, delta.Items.Count);
        Assert.False(delta.Items[0].Removed);
        Assert.Equal(100L, delta.Items[0].AddedAt);
        Assert.True(delta.Items[1].Removed);
    }

    [Fact]
    public void ParsePage_MapsItemsAndToken()
    {
        var resp = new Col.PageResponse { SyncToken = "tok-1", NextPageToken = "" };
        resp.Items.Add(new Col.CollectionItem { Uri = "spotify:track:a", AddedAt = 5 });

        var delta = CollectionWireMapper.ParsePage("liked", resp);
        Assert.Equal("liked", delta.SetId);
        Assert.Equal("tok-1", delta.NewRevision);
        Assert.Equal("spotify:track:a", Assert.Single(delta.Items).Uri);
    }
}
