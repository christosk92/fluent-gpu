using Xunit;

namespace Wavee.Tests;

/// <summary>
/// The pure rail ↔ docked-video coupling rules (<see cref="RailVideoCoupling"/>) — the four edge cases connecting the
/// right rail's own open/closed + mode state to the video surface's placement (docked-video design §3.5, B1/B6-B8/B10-B14).
/// Reuses the <see cref="PlacementCoreTests"/> helpers' shape (<c>Off</c>/<c>At</c>) locally so this file stays
/// self-contained and, like its sibling, engine-free.
/// </summary>
public class RailVideoCouplingTests
{
    static readonly PlacementPolicy Policy = PlacementPolicy.Video;
    const PlacementSet All = PlacementSet.Docked | PlacementSet.Floating | PlacementSet.Detached | PlacementSet.Fullscreen;

    static PlacementState Off(PlacementSet available = All)
        => PlacementState.Initial(Policy) with { Available = available };

    static PlacementState At(SurfacePlacement p, PlacementSet available = All)
        => PlacementCore.OpenAt(Off(available), p);

    // ── ModeOnDock — B1/B12: docking a closed rail opens Video mode; docking an already-open rail leaves it alone ─────

    [Fact]
    public void ModeOnDock_OpensVideoMode_OnlyWhenTheRailWasClosed()
    {
        Assert.Equal(RailMode.Video, RailVideoCoupling.ModeOnDock(railOpen: false, current: RailMode.Lyrics));
        Assert.Null(RailVideoCoupling.ModeOnDock(railOpen: true, current: RailMode.Lyrics));
        Assert.Null(RailVideoCoupling.ModeOnDock(railOpen: true, current: RailMode.Video));
    }

    // ── OnRailClosed — B6: closing the rail demotes a DOCKED video; every other placement is untouched ────────────────

    [Fact]
    public void RailClosed_WhileFloating_IsInert()
    {
        Assert.Equal(SurfacePlacement.None, RailVideoCoupling.OnRailClosed(At(SurfacePlacement.Floating)));
        Assert.Equal(SurfacePlacement.None, RailVideoCoupling.OnRailClosed(At(SurfacePlacement.Detached)));
        Assert.Equal(SurfacePlacement.None, RailVideoCoupling.OnRailClosed(Off()));   // no video at all
    }

    [Fact]
    public void RailClosed_WhileDocked_DemotesToFloating()
        => Assert.Equal(SurfacePlacement.Floating, RailVideoCoupling.OnRailClosed(At(SurfacePlacement.Docked)));

    // ── ReDockOnRailOpen — B7/B8: only when the rail itself is what took the video away ─────────────────────────────────

    [Fact]
    public void ReDock_OnlyWhenPreferredIsDocked()
    {
        // The rail took it away (Preferred still Docked, now sitting at the Floating fallback) → re-dock.
        var demoted = PlacementCore.Demote(At(SurfacePlacement.Docked), SurfacePlacement.Floating);
        Assert.Equal(SurfacePlacement.Floating, demoted.Requested);
        Assert.True(RailVideoCoupling.ReDockOnRailOpen(demoted));

        // The user deliberately picked "Mini player" from the menu (Preferred = Floating) → never re-dock (B8).
        Assert.False(RailVideoCoupling.ReDockOnRailOpen(At(SurfacePlacement.Floating)));

        // Video is off entirely → nothing to re-dock.
        Assert.False(RailVideoCoupling.ReDockOnRailOpen(Off() with { Preferred = SurfacePlacement.Docked }));

        // Already docked → no-op, it never left.
        Assert.False(RailVideoCoupling.ReDockOnRailOpen(At(SurfacePlacement.Docked)));

        // Fullscreen entered FROM Docked must not re-fire the moment the rail happens to still be open beneath it.
        var fs = PlacementCore.EnterFullscreen(At(SurfacePlacement.Docked));
        Assert.False(RailVideoCoupling.ReDockOnRailOpen(fs));
    }

    // ── CloseRailOnVideoLeft — B13/B14: only the Video-mode body has nothing left to show ────────────────────────────

    [Fact]
    public void CloseRailOnVideoLeft_OnlyInVideoMode()
    {
        Assert.True(RailVideoCoupling.CloseRailOnVideoLeft(RailMode.Video, videoTurnedOff: true));
        Assert.False(RailVideoCoupling.CloseRailOnVideoLeft(RailMode.Video, videoTurnedOff: false));
        Assert.False(RailVideoCoupling.CloseRailOnVideoLeft(RailMode.Lyrics, videoTurnedOff: true));
        Assert.False(RailVideoCoupling.CloseRailOnVideoLeft(RailMode.Queue, videoTurnedOff: true));
        Assert.False(RailVideoCoupling.CloseRailOnVideoLeft(RailMode.Details, videoTurnedOff: true));
        Assert.False(RailVideoCoupling.CloseRailOnVideoLeft(RailMode.Friends, videoTurnedOff: true));
    }
}
