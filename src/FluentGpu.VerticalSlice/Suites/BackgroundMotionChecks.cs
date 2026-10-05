using FluentGpu.Foundation;
using FluentGpu.Hosting;
using FluentGpu.Pal;
using FluentGpu.Pal.Headless;
using FluentGpu.Rhi.Headless;
using FluentGpu.Text.Headless;
using static FluentGpu.VerticalSlice.Harness.Gate;

// Motion policy (2026-10-03): the engine never throttles a VISIBLE window for losing focus. A FrameClock.Tick poller (a lyrics
// stepper, an equalizer, a playhead ticker) in a window that is not the active one is served exactly as in the active one: the
// same wait kind and the same wait. The ONE window state that slows render motion is occlusion (covered / cloaked / minimized:
// nothing of the window is on screen), which the UI mirrors to the render thread so the compositor parks its loops like a
// minimize does, and un-parks them when the occlusion probe hears the window is visible again (F118). The GPUI-style focus
// floor (InactiveFrameIntervalMs / HostWaitKind.InactiveThrottle) and the static weak-tier poller cap (TierPaced) are gone;
// the only ceilings left are the OS power cap (PowerCapFps) and the measured adaptive GPU governor.
static class BackgroundMotionChecks
{
    internal static void Run(StringTable strings)
    {
        using var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("background-motion", new Size2(200, 120), 1f));
        window.Show();
        var device = new HeadlessGpuDevice();
        var root = new FrameClockPollerRoot();   // two live FrameClock.Tick pollers (render-effect + ownerless hook effect)
        using var host = new AppHost(app, window, device, new HeadlessFontSystem(strings), strings, root);
        for (int i = 0; i < 6; i++) host.RunFrame();

        // Foreground: the pollers are due-now work on the display-rate path.
        WakeReasons activeWake = host.CurrentWakeReasons;
        int activeWait = host.RecommendedWaitMs();
        HostWaitKind activeKind = host.LastWaitKind;

        // Background (another window has focus; this one is still on screen): the SAME path, the same wait.
        window.IsActive = false;
        host.RunFrame();
        host.RunFrame();                          // consume the WindowBlur edge
        WakeReasons inactiveWake = host.CurrentWakeReasons;
        int inactiveWait = host.RecommendedWaitMs();
        HostWaitKind inactiveKind = host.LastWaitKind;
        bool onlyPoller = (inactiveWake & WakeReasons.FrameClockPoller) != 0
                          && (inactiveWake & ~(WakeReasons.FrameClockPoller | WakeReasons.Anim | WakeReasons.Caret)) == 0;
        bool samePath = inactiveKind == activeKind && inactiveWait == activeWait;
        bool notPaced = inactiveKind is not (HostWaitKind.Cadence or HostWaitKind.PowerCap or HostWaitKind.AdaptiveGpu);

        // Foregrounded again: unchanged.
        window.IsActive = true;
        host.RunFrame();
        host.RunFrame();
        int restoredWait = host.RecommendedWaitMs();
        bool restored = host.LastWaitKind == activeKind && restoredWait == activeWait;

        Check("gate.motion.no-focus-throttle a window that is not the active one serves its FrameClock.Tick poller on the same display-rate wait path as the active one (no focus floor, no sub-display cadence): focus is not a motion policy",
            onlyPoller && samePath && notPaced && restored,
            $"activeWake={activeWake} activeKind={activeKind} activeWait={activeWait} inactiveWake={inactiveWake} inactiveKind={inactiveKind} inactiveWait={inactiveWait} restoredWait={restoredWait} restoredKind={host.LastWaitKind}");

        // Occlusion parks render motion like a minimize: the UI mirrors the swapchain's IsOccluded to the render thread, both ways.
        // Focus never reaches that mirror (an unfocused visible window stays un-parked).
        bool occludedBefore = host.RenderOccludedForTest;
        window.IsActive = false;
        host.RunFrame();
        host.RunFrame();
        bool unfocusedNotParked = !host.RenderOccludedForTest;
        window.IsActive = true;
        device.PrimarySwapchain!.Occluded = true;
        host.RunFrame();
        host.RunFrame();
        bool occludedRose = host.RenderOccludedForTest;
        device.PrimarySwapchain!.Occluded = false;
        host.RunFrame();
        host.RunFrame();
        bool occludedFell = !host.RenderOccludedForTest;
        Check("gate.motion.occlusion-mirrors-to-render the swapchain's IsOccluded reaches the render thread's park flag within a frame, both ways; losing focus never does",
            !occludedBefore && unfocusedNotParked && occludedRose && occludedFell,
            $"before={occludedBefore} unfocusedNotParked={unfocusedNotParked} rose={occludedRose} fell={occludedFell}");
    }
}
