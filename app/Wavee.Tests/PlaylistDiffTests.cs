using System;
using System.Collections.Generic;
using System.Linq;
using Wavee.Backend.Playlists;
using Xunit;

namespace Wavee.Tests;

// The pure, proto-free ordered-membership op applier — the heart of incremental playlist sync. Indices are interpreted
// against the evolving list (relative to all preceding ops), matching the reference /diff semantics.
public class PlaylistDiffApplierTests
{
    static List<PlaylistMember> List(params string[] ids) =>
        ids.Select(u => new PlaylistMember(u, "spotify:track:" + u, null, 0)).ToList();
    static string[] Ids(IReadOnlyList<PlaylistMember> l) =>
        l.Select(m => m.ItemUri.Replace("spotify:track:", "")).ToArray();
    static PlaylistMember M(string id) => new(id, "spotify:track:" + id, null, 0);

    [Fact]
    public void Add_AtIndex_Inserts()
    {
        var l = List("a", "b", "c");
        PlaylistDiffApplier.Apply(l, new[] { new PlaylistOp(PlaylistOpKind.Add, FromIndex: 1, Items: new[] { M("x") }) });
        Assert.Equal(new[] { "a", "x", "b", "c" }, Ids(l));
    }

    [Fact]
    public void Add_First_And_Last()
    {
        var l = List("a", "b");
        PlaylistDiffApplier.Apply(l, new[]
        {
            new PlaylistOp(PlaylistOpKind.Add, AddFirst: true, Items: new[] { M("h") }),
            new PlaylistOp(PlaylistOpKind.Add, AddLast: true, Items: new[] { M("t") }),
        });
        Assert.Equal(new[] { "h", "a", "b", "t" }, Ids(l));
    }

    [Fact]
    public void Remove_Range()
    {
        var l = List("a", "b", "c", "d");
        PlaylistDiffApplier.Apply(l, new[] { new PlaylistOp(PlaylistOpKind.Remove, FromIndex: 1, Length: 2) });
        Assert.Equal(new[] { "a", "d" }, Ids(l));
    }

    [Fact]
    public void Move_Range_ToPostRemovalIndex()
    {
        var l = List("a", "b", "c", "d");
        // move [a] (index 0, len 1) to post-removal index 2: remove → b,c,d ; insert a at 2 → b,c,a,d
        PlaylistDiffApplier.Apply(l, new[] { new PlaylistOp(PlaylistOpKind.Move, FromIndex: 0, Length: 1, ToIndex: 2) });
        Assert.Equal(new[] { "b", "c", "a", "d" }, Ids(l));
    }

    [Fact]
    public void Sequence_IndicesAreRelativeToEvolvingList()
    {
        var l = List("a", "b", "c");
        PlaylistDiffApplier.Apply(l, new[]
        {
            new PlaylistOp(PlaylistOpKind.Remove, FromIndex: 0, Length: 1),                       // → b,c
            new PlaylistOp(PlaylistOpKind.Add, FromIndex: 1, Items: new[] { M("x") }),            // → b,x,c
        });
        Assert.Equal(new[] { "b", "x", "c" }, Ids(l));
    }

    [Fact]
    public void UpdateItem_MergesAddedAttributes()
    {
        var l = List("a", "b");
        PlaylistDiffApplier.Apply(l, new[] { new PlaylistOp(PlaylistOpKind.UpdateItem, FromIndex: 1, Items: new[] { new PlaylistMember("b", "spotify:track:b", "alice", 999) }) });
        Assert.Equal("alice", l[1].AddedBy);
        Assert.Equal(999, l[1].AddedAt);
    }

    [Fact]
    public void UpdateList_IsNoOp_OnMembership()
    {
        var l = List("a", "b");
        PlaylistDiffApplier.Apply(l, new[] { new PlaylistOp(PlaylistOpKind.UpdateList) });
        Assert.Equal(new[] { "a", "b" }, Ids(l));   // list-level attrs (name/desc) are the header's concern, not membership
    }

    [Theory]
    [InlineData(PlaylistOpKind.Remove, 5, 1, 0)]   // REM out of range
    [InlineData(PlaylistOpKind.Add, 9, 0, 0)]      // ADD index past end
    [InlineData(PlaylistOpKind.Move, 0, 1, 9)]     // MOV dest out of range
    public void OutOfRange_Throws_SoCallerCanFullRefetch(PlaylistOpKind kind, int from, int len, int to)
    {
        var l = List("a");
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            PlaylistDiffApplier.Apply(l, new[] { new PlaylistOp(kind, FromIndex: from, Length: len, ToIndex: to, Items: new[] { M("z") }) }));
    }
}
