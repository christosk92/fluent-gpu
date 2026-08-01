using Xunit;

namespace Wavee.Tests;

/// <summary>The same-list drag-reorder gate. A drop names an insertion position through the DISPLAYED row order and the
/// commit maps it back to an original membership index, so the gate must refuse whenever that map is not the identity
/// — not only for a re-sort (the case it originally covered), but for a text query and any advanced filter too.</summary>
public class PlaylistReorderRulesTests
{
    [Fact]
    public void NaturalUnfilteredOrderAllowsTheMove()
    {
        Assert.True(PlaylistReorderRules.AllowsSameListMove(true, "", TrackFilterState.Default));
    }

    [Fact]
    public void ASortRefusesTheMove()
    {
        Assert.False(PlaylistReorderRules.AllowsSameListMove(false, "", TrackFilterState.Default));
    }

    [Fact]
    public void ATextQueryRefusesTheMove()
    {
        Assert.False(PlaylistReorderRules.AllowsSameListMove(true, "daft", TrackFilterState.Default));
    }

    [Theory]
    [MemberData(nameof(ActiveFilters))]
    public void AnyActiveFilterRefusesTheMove(TrackFilterState filter)
    {
        Assert.False(filter.IsDefault);
        Assert.False(PlaylistReorderRules.AllowsSameListMove(true, "", filter));
    }

    public static TheoryData<TrackFilterState> ActiveFilters() => new()
    {
        TrackFilterState.Default with { Flags = TrackFilterFlags.LikedOnly },
        TrackFilterState.Default with { Flags = TrackFilterFlags.PlayableOnly },
        TrackFilterState.Default with { ExplicitMode = TrackTraitMode.Hide },
        TrackFilterState.Default with { VideoMode = TrackTraitMode.Only },
        TrackFilterState.Default with { Duration = TrackDurationRange.UnderThreeMinutes },
        TrackFilterState.Default with { Added = TrackAddedRange.LastSevenDays },
        TrackFilterState.Default with { Origin = TrackOriginFilter.Local },
        TrackFilterState.Default with { Tempo = TrackTempoBand.From120To139 },
        TrackFilterState.Default with { CamelotCode = "8B" },
        TrackFilterState.Default with { Tag = "K-Pop" },
    };

    // ── display<->original mapping (Wave 4): the drag payload carries ORIGINAL membership indices, while the
    // framework-owned virtual-removal math counts DISPLAY positions. Getting this backwards hides the wrong rows and
    // mis-sizes the gap, so it is pinned here against production code.

    [Fact]
    public void DisplayRowOf_IsTheIdentityInNaturalOrder()
    {
        int[] view = [0, 1, 2, 3, 4];
        Assert.Equal(0, PlaylistReorderRules.DisplayRowOf(0, view));
        Assert.Equal(3, PlaylistReorderRules.DisplayRowOf(3, view));
        Assert.Equal(4, PlaylistReorderRules.DisplayRowOf(4, view));
    }

    [Fact]
    public void DisplayRowOf_InvertsAReorderedOrFilteredView()
    {
        // A sorted/filtered view: display 0 shows original 4, display 1 shows original 0, display 2 shows original 2.
        int[] view = [4, 0, 2];
        Assert.Equal(1, PlaylistReorderRules.DisplayRowOf(0, view));
        Assert.Equal(2, PlaylistReorderRules.DisplayRowOf(2, view));
        Assert.Equal(0, PlaylistReorderRules.DisplayRowOf(4, view));
    }

    [Fact]
    public void DisplayRowOf_ReportsMinusOneForARowThatIsNotDisplayed()
    {
        int[] view = [4, 0, 2];
        Assert.Equal(-1, PlaylistReorderRules.DisplayRowOf(1, view));
        Assert.Equal(-1, PlaylistReorderRules.DisplayRowOf(9, view));
        Assert.Equal(-1, PlaylistReorderRules.DisplayRowOf(0, System.Array.Empty<int>()));
    }

    // ── Alt+Up / Alt+Down block move (Wave 6) ──────────────────────────────────────────────────────

    [Fact]
    public void BlockMove_InheritsTheDragGateAndAddsTheWriteGate()
    {
        Assert.True(PlaylistReorderRules.AllowsBlockMove(true, true, "", TrackFilterState.Default));
        // A read-only playlist has nothing to reorder no matter how clean its display order is.
        Assert.False(PlaylistReorderRules.AllowsBlockMove(false, true, "", TrackFilterState.Default));
        // …and every ambiguity that refuses the DRAG refuses the keystroke identically.
        Assert.False(PlaylistReorderRules.AllowsBlockMove(true, false, "", TrackFilterState.Default));
        Assert.False(PlaylistReorderRules.AllowsBlockMove(true, true, "daft", TrackFilterState.Default));
        Assert.False(PlaylistReorderRules.AllowsBlockMove(true, true, "",
            TrackFilterState.Default with { ExplicitMode = TrackTraitMode.Only }));
    }

    [Fact]
    public void BlockMoveTarget_UsesThePreMoveInsertionConvention()
    {
        // [A,B,C,D]: moving B (1) DOWN one means "insert before the row currently at index 3" — the jumped row is
        // still counted at that moment, which is why the answer is max + 2 and not max + 1.
        Assert.Equal(3, PlaylistReorderRules.BlockMoveTarget([1], 4, +1));
        // Moving C (2) UP one is simply "insert before B".
        Assert.Equal(1, PlaylistReorderRules.BlockMoveTarget([2], 4, -1));
        // A contiguous block of two behaves the same way about its own extremes.
        Assert.Equal(4, PlaylistReorderRules.BlockMoveTarget([1, 2], 5, +1));
        Assert.Equal(0, PlaylistReorderRules.BlockMoveTarget([1, 2], 5, -1));
    }

    [Fact]
    public void BlockMoveTarget_RefusesAtTheBoundaries()
    {
        Assert.Equal(-1, PlaylistReorderRules.BlockMoveTarget([0], 4, -1));
        Assert.Equal(-1, PlaylistReorderRules.BlockMoveTarget([3], 4, +1));
        Assert.Equal(-1, PlaylistReorderRules.BlockMoveTarget([2, 3], 4, +1));
        Assert.Equal(-1, PlaylistReorderRules.BlockMoveTarget([0, 1], 4, -1));
    }

    [Fact]
    public void BlockMoveTarget_RefusesAGappedSelectionRatherThanInventingAMove()
    {
        // "One row up" has no single meaning for {B, D}: any answer would also close the gap between them, which is a
        // different edit than the one the keystroke promises.
        Assert.Equal(-1, PlaylistReorderRules.BlockMoveTarget([1, 3], 5, +1));
        Assert.Equal(-1, PlaylistReorderRules.BlockMoveTarget([1, 3], 5, -1));
        // Unordered input is still a contiguous run and is accepted; duplicates are not a run at all.
        Assert.Equal(4, PlaylistReorderRules.BlockMoveTarget([2, 1], 5, +1));
        Assert.Equal(-1, PlaylistReorderRules.BlockMoveTarget([1, 1], 5, +1));
    }

    [Fact]
    public void BlockMoveTarget_RefusesNonsenseInputs()
    {
        Assert.Equal(-1, PlaylistReorderRules.BlockMoveTarget([], 4, +1));
        Assert.Equal(-1, PlaylistReorderRules.BlockMoveTarget([0], 0, +1));
        Assert.Equal(-1, PlaylistReorderRules.BlockMoveTarget([0], 4, 0));    // only ±1 is a "block move"
        Assert.Equal(-1, PlaylistReorderRules.BlockMoveTarget([0], 4, +2));
        Assert.Equal(-1, PlaylistReorderRules.BlockMoveTarget([9], 4, -1));   // out of range
    }

    // ── The drop caption's semantic claim ──────────────────────────────────────────────────────────

    [Fact]
    public void VerbFor_DistinguishesAMoveFromACopyAndFromAnUnknownCount()
    {
        // Same playlist + membership rows behind it: the rows LEAVE their slots. That is a different edit than a copy
        // and the chip has to say so — the line and the gap look identical either way.
        Assert.Equal(PlaylistDropVerb.MoveRows, PlaylistReorderRules.VerbFor(true, 3, 3));
        // A foreign drop with a track snapshot knows exactly how many it will add.
        Assert.Equal(PlaylistDropVerb.AddTracks, PlaylistReorderRules.VerbFor(false, 0, 12));
        // A container still behind a cold resolver does NOT — so it must caption without a number rather than say "1".
        Assert.Equal(PlaylistDropVerb.AddContainer, PlaylistReorderRules.VerbFor(false, 0, 0));
        // "Same list" with no rows to move is not a move at all; nothing truthful is left to say.
        Assert.Equal(PlaylistDropVerb.None, PlaylistReorderRules.VerbFor(true, 0, 5));
    }
}
