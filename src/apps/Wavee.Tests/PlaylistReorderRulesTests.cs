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
}
