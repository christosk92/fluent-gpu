using System.Collections.Generic;
using System.Linq;
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
}
