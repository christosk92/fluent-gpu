using System;
using System.Collections.Generic;
using System.Linq;
using Wavee.Backend;
using Wavee.Backend.Playlists;
using Wavee.Core;
using Xunit;

namespace Wavee.Tests;

// PlaylistMutationSource.BuildMoveOps — a (possibly gapped) selection move must decompose into sequential MOV ops
// that PlaylistDiffApplier reproduces exactly. Verified by applying the emitted ops to a concrete list and
// comparing against the expected final order (a single-op shortcut would move the WRONG rows for gapped selections).
public class PlaylistMoveOpsTests
{
    static PlaylistRowRef Row(int index) => new(index, $"spotify:track:{index}", $"id{index}");

    /// <summary>Applies BuildMoveOps to a list of <paramref name="count"/> rows named by original index.</summary>
    static List<string> Simulate(int count, int[] selected, int toIndex)
    {
        var list = new List<PlaylistMember>(count);
        for (int i = 0; i < count; i++) list.Add(new PlaylistMember($"id{i}", $"spotify:track:{i}", null, 0));
        var ops = PlaylistMutationSource.BuildMoveOps(selected.Select(Row).ToArray(), toIndex);
        PlaylistDiffApplier.Apply(list, ops);
        return list.Select(m => m.ItemId).ToList();
    }

    static List<string> Expected(int count, int[] selected, int toIndex)
    {
        var sel = new SortedSet<int>(selected);
        var result = new List<string>(count);
        foreach (int i in Enumerable.Range(0, count).Where(i => !sel.Contains(i) && i < toIndex)) result.Add($"id{i}");
        foreach (int i in sel) result.Add($"id{i}");
        foreach (int i in Enumerable.Range(0, count).Where(i => !sel.Contains(i) && i >= toIndex)) result.Add($"id{i}");
        return result;
    }

    [Theory]
    [InlineData(new[] { 1, 2 }, 0)]        // contiguous run up
    [InlineData(new[] { 1, 2 }, 5)]        // contiguous run to end
    [InlineData(new[] { 0, 2 }, 5)]        // gapped selection down
    [InlineData(new[] { 1, 3 }, 0)]        // gapped selection up
    [InlineData(new[] { 0, 2, 4 }, 2)]     // gapped selection into the middle
    [InlineData(new[] { 4 }, 0)]           // single row up
    [InlineData(new[] { 0 }, 5)]           // single row to end
    [InlineData(new[] { 0, 1, 2, 3, 4 }, 0)] // everything (no-op)
    public void GappedAndContiguousMoves_LandInSelectionOrderAtTarget(int[] selected, int toIndex)
    {
        Assert.Equal(Expected(5, selected, toIndex), Simulate(5, selected, toIndex));
    }

    [Fact]
    public void NoOpMove_EmitsNoOps()
    {
        var ops = PlaylistMutationSource.BuildMoveOps(new[] { Row(0), Row(1) }, 0);
        Assert.Empty(ops);
    }

    [Fact]
    public void ContiguousRun_EmitsSingleOp()
    {
        var ops = PlaylistMutationSource.BuildMoveOps(new[] { Row(3), Row(4) }, 0);
        var op = Assert.Single(ops);
        Assert.Equal(PlaylistOpKind.Move, op.Kind);
        Assert.Equal(3, op.FromIndex);
        Assert.Equal(2, op.Length);
        Assert.Equal(0, op.ToIndex);
    }

    [Fact]
    public void RootlistMove_MovesBalancedFolderAsOneRange_AndSupportsInside()
    {
        RootlistEntry[] entries =
        [
            new(0, 0, "spotify:playlist:a", null, 0),
            new(1, 1, "spotify:start-group:g:Folder", "Folder", 0),
            new(2, 0, "spotify:playlist:b", null, 1),
            new(3, 0, "spotify:playlist:c", null, 1),
            new(4, 2, "spotify:end-group:g", null, 0),
            new(5, 0, "spotify:playlist:d", null, 0),
        ];

        Assert.True(RootlistOps.TryBuildMove(entries,
            new RootlistItemRef("spotify:playlist:a", false), new RootlistItemRef("g", true),
            RootlistDropPlacement.Inside, out var inside));
        Assert.Equal((0, 1, 4), (inside!.FromIndex, inside.Length, inside.ToIndex));

        Assert.True(RootlistOps.TryBuildMove(entries,
            new RootlistItemRef("g", true), new RootlistItemRef("spotify:playlist:d", false),
            RootlistDropPlacement.After, out var folder));
        Assert.Equal((1, 4, 6), (folder!.FromIndex, folder.Length, folder.ToIndex));
    }

    [Fact]
    public void RootlistMove_RejectsFolderIntoItsOwnSubtree()
    {
        RootlistEntry[] entries =
        [
            new(0, 1, "spotify:start-group:g:Folder", "Folder", 0),
            new(1, 0, "spotify:playlist:b", null, 1),
            new(2, 2, "spotify:end-group:g", null, 0),
        ];
        Assert.False(RootlistOps.TryBuildMove(entries,
            new RootlistItemRef("g", true), new RootlistItemRef("spotify:playlist:b", false),
            RootlistDropPlacement.Before, out _));
    }

    [Fact]
    public void LocalPlaylist_DuplicateMembershipsKeepStableIndependentRowIds()
    {
        var source = new UserPlaylistSource();
        string uri = source.CreatePlaylist("Drag target");
        var a = new Track("a", "spotify:track:a", "A", Array.Empty<ArtistRef>(),
            new AlbumRef("", "", ""), 1, false, null);
        var b = new Track("b", "spotify:track:b", "B", Array.Empty<ArtistRef>(),
            new AlbumRef("", "", ""), 1, false, null);

        source.AddTrack(uri, a);
        source.AddTrack(uri, a);
        source.InsertTracks(uri, [b], 1);
        var before = source.ResolveContext(uri)!;
        Assert.Equal(new[] { "A", "B", "A" }, before.Select(t => t.Title));
        Assert.NotEqual(before[0].ContextUid, before[2].ContextUid);

        string firstUid = before[0].ContextUid!;
        string secondUid = before[2].ContextUid!;
        source.MoveRows(uri, [new PlaylistRowRef(0, a.Uri, firstUid)], 3);
        var moved = source.ResolveContext(uri)!;
        Assert.Equal(new[] { "B", "A", "A" }, moved.Select(t => t.Title));
        Assert.Equal(firstUid, moved[2].ContextUid);

        source.RemoveRows(uri, [new PlaylistRowRef(1, a.Uri, secondUid)]);
        var removed = source.ResolveContext(uri)!;
        Assert.Equal(new[] { "B", "A" }, removed.Select(t => t.Title));
        Assert.Equal(firstUid, removed[1].ContextUid);
    }
}
