using FluentGpu.Hooks;
using Xunit;

namespace FluentGpu.Engine.Tests.Scout;

/// <summary>A grouped Skel.Region that unmounted while still Pending stranded its group's round: the members that had
/// already reported Done never revealed, and their parked reveal thunks fired on the NEXT member Done, replaying a
/// blur/soft reveal on content that had been on screen for seconds. Unregister now completes the round itself.</summary>
public sealed class SkelGroupUnregisterTests
{
    [Fact]
    public void Departing_last_pending_member_completes_the_round()
    {
        var group = new object();
        int a = 0, b = 0;
        SkelGroupCoordinator.Loading(group, 1);
        SkelGroupCoordinator.Loading(group, 2);
        SkelGroupCoordinator.Loading(group, 3);
        SkelGroupCoordinator.Done(group, 1, () => a++);
        SkelGroupCoordinator.Done(group, 2, () => b++);
        Assert.Equal(0, a);   // still waiting on member 3
        Assert.Equal(0, b);

        SkelGroupCoordinator.Unregister(group, 3);   // member 3 unmounts while Pending
        Assert.Equal(1, a);
        Assert.Equal(1, b);

        // Member 1 refreshes alone (member 2 stayed Ready): the round is member 1's own, so it reveals at once and
        // B's old reveal must not replay.
        SkelGroupCoordinator.Loading(group, 1);
        SkelGroupCoordinator.Done(group, 1, () => a++);
        Assert.Equal(2, a);
        Assert.Equal(1, b);

        SkelGroupCoordinator.Unregister(group, 1);
        SkelGroupCoordinator.Unregister(group, 2);
    }

    [Fact]
    public void Departing_member_with_nobody_done_fires_nothing()
    {
        var group = new object();
        int a = 0;
        SkelGroupCoordinator.Loading(group, 1);
        SkelGroupCoordinator.Loading(group, 2);
        SkelGroupCoordinator.Unregister(group, 2);
        Assert.Equal(0, a);
        SkelGroupCoordinator.Done(group, 1, () => a++);   // the sole remaining member completes its own round
        Assert.Equal(1, a);
        SkelGroupCoordinator.Unregister(group, 1);
    }
}
