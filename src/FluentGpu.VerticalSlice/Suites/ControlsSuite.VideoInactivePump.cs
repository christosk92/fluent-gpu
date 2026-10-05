using System;
using static FluentGpu.VerticalSlice.Harness.Gate;
using static FluentGpu.VerticalSlice.Harness.Asserts;

// ── gate.media.el.inactive-state-pump ───────────────────────────────────────────────────────────────────────────────────
// The shipped defect: a non-decorative player surface that goes INACTIVE (the immersive stage covers the docked video by
// collapsing the surface's presence, or a pop-out hand-off hides the outgoing presenter) fell out of the inactive hide
// into the PLACED pump. A presence-collapsed surface keeps stale non-zero descendant bounds, but its own ClipToBounds
// ancestor is 0x0, so ClipToAncestors produced an empty viewport and PumpNow returned BEFORE Player.PumpVideo: the
// covered video's Position, State and Ended froze. The inactive branch must therefore pump the session's STATE with the
// inert default binding (IsValid false: no bind / Place / SetVisible(true) / stream resize) and return, ahead of any
// geometry or viewport work. A headless host cannot drive UseIsActive plus a collapsed clip ancestor cheaply, so the
// branch shape is pinned as an ordering of PumpNow's source (the same technique as gate.media.el.player-inactive-still-pumps).
static partial class ControlsSuite
{
    static void VideoInactivePumpChecks()
    {
        string? src = ReadRepoFile("src/FluentGpu.Controls/Media/MediaPlayerElement.cs");
        bool found = src is not null;
        const string inactiveGuard = "if (!active)";
        const string decorativeReturn = "if (IsDecorative) return;";
        const string statePump = "Player.PumpVideo(default, default, s);";
        const string emptyViewport = "if (viewport.W <= 0.5f || viewport.H <= 0.5f)";
        int guard = src is null ? -1 : src.IndexOf(inactiveGuard, StringComparison.Ordinal);
        int decorative = src is null || guard < 0 ? -1 : src.IndexOf(decorativeReturn, guard, StringComparison.Ordinal);
        int pump = src is null || decorative < 0 ? -1 : src.IndexOf(statePump, decorative, StringComparison.Ordinal);
        int viewport = src is null || guard < 0 ? -1 : src.IndexOf(emptyViewport, guard, StringComparison.Ordinal);
        // guard, then the decorative bail, then the inert state pump, all BEFORE the clip/empty-viewport early return.
        bool ordered = guard >= 0 && decorative > guard && pump > decorative && viewport > pump;
        Check("gate.media.el.inactive-state-pump", found && ordered,
            $"found={found} guard={guard} decorativeReturn={decorative} statePump={pump} emptyViewportReturn={viewport} "
            + "(want guard < decorativeReturn < statePump < emptyViewportReturn)");
    }
}
