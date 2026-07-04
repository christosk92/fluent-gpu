using System;
using System.Collections.Generic;
using System.Linq;
using Wavee;
using Wavee.Core;
using Xunit;

namespace Wavee.Tests;

// Phase 7 (§4.6) — the pure keyed row diff behind the realtime tracklist choreography: identity via ContextUid (ItemId)
// with a uri#occurrence fallback, structural-only reset classification, and shift-displaced survivors in Moves.
public class MembershipDiffTests
{
    static Track T(string id, string? uid = null) => new(
        Id: id, Uri: "spotify:track:" + id, Title: id, Artists: Array.Empty<ArtistRef>(),
        Album: new AlbumRef("", "", ""), DurationMs: 1000, IsExplicit: false, Image: null, ContextUid: uid);

    static IReadOnlyList<Track> Tracks(params string[] ids) => ids.Select(i => T(i, "uid-" + i)).ToArray();

    [Fact]
    public void SingleMove_YieldsMoveChangesOnly_NoReset()
    {
        // a→c b→a c→b is one MOV: every survivor's index changed, zero structural changes.
        var d = MembershipDiff.Diff(Tracks("a", "b", "c"), Tracks("c", "a", "b"));
        Assert.Empty(d.Adds);
        Assert.Empty(d.Removes);
        Assert.Equal(3, d.Moves.Count);
        Assert.Equal(1.0, d.RetainedFraction);
        Assert.False(d.IsReset);
        var c = d.Moves.Single(m => m.Key == "uid-c");
        Assert.Equal((2, 0), (c.OldIndex!.Value, c.NewIndex!.Value));
    }

    [Fact]
    public void InsertAtTop_OneAdd_SurvivorsShift_NotAReset()
    {
        var old = Tracks(Enumerable.Range(0, 100).Select(i => "t" + i).ToArray());
        var next = new List<Track> { T("new", "uid-new") };
        next.AddRange(old);

        var d = MembershipDiff.Diff(old, next.ToArray());
        Assert.Single(d.Adds);
        Assert.Equal((null, 0), (d.Adds[0].OldIndex, d.Adds[0].NewIndex!.Value));
        Assert.Empty(d.Removes);
        Assert.Equal(100, d.Moves.Count);          // every survivor displaced by one (the FLIP pass needs these)
        Assert.False(d.IsReset);                   // ONE structural change — never a reset despite 100 shifted rows
    }

    [Fact]
    public void RemoveAndAdd_Combined()
    {
        var d = MembershipDiff.Diff(Tracks("a", "b", "c"), Tracks("a", "x", "c"));
        Assert.Equal("uid-x", Assert.Single(d.Adds).Key);
        Assert.Equal("uid-b", Assert.Single(d.Removes).Key);
        Assert.Equal(1, Assert.Single(d.Removes).OldIndex);
        Assert.Empty(d.Moves);                     // a and c kept their indices
        Assert.False(d.IsReset);
    }

    [Fact]
    public void CuratedRecut_MostContentReplaced_IsReset()
    {
        // Discover-Weekly style: 30 rows all replaced → retained 0 → reset (whole-list crossfade, no row storm).
        var old = Tracks(Enumerable.Range(0, 30).Select(i => "old" + i).ToArray());
        var next = Tracks(Enumerable.Range(0, 30).Select(i => "new" + i).ToArray());
        var d = MembershipDiff.Diff(old, next);
        Assert.True(d.IsReset);
        Assert.Equal(0.0, d.RetainedFraction);
    }

    [Fact]
    public void BulkEdit_ManyStructuralChanges_IsReset()
    {
        // retained fraction high (100 survive of 141) but 41 adds > the structural cap → still a reset.
        var old = Tracks(Enumerable.Range(0, 141).Select(i => "t" + i).ToArray());
        var next = old.Take(100).Concat(Tracks(Enumerable.Range(0, 41).Select(i => "n" + i).ToArray())).ToArray();
        var d = MembershipDiff.Diff(old, next);
        Assert.Equal(41 + 41, d.Adds.Count + d.Removes.Count);
        Assert.True(d.IsReset);
    }

    [Fact]
    public void FirstFill_EmptyOld_NeverAReset()
    {
        var d = MembershipDiff.Diff(Array.Empty<Track>(), Tracks("a", "b"));
        Assert.Equal(2, d.Adds.Count);
        Assert.False(d.IsReset);
        Assert.Equal(1.0, d.RetainedFraction);
    }

    [Fact]
    public void NoChange_IsEmpty()
    {
        var d = MembershipDiff.Diff(Tracks("a", "b"), Tracks("a", "b"));
        Assert.True(d.IsEmpty);
        Assert.False(d.IsReset);
    }

    [Fact]
    public void DuplicateUris_WithoutItemIds_StayDistinctViaOccurrence()
    {
        // no ContextUid → uri#occurrence keys; removing ONE of two duplicate rows is one remove, the other survives.
        var a1 = T("dup"); var a2 = T("dup"); var b = T("b");
        var d = MembershipDiff.Diff(new[] { a1, a2, b }, new[] { a1, b });
        Assert.Equal("spotify:track:dup#1", Assert.Single(d.Removes).Key);
        Assert.Empty(d.Adds);
        Assert.Single(d.Moves);                    // b shifted 2→1
    }
}
