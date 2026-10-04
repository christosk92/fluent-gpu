using FluentGpu.Foundation;
using FluentGpu.Hosting;
using FluentGpu.Pal;
using FluentGpu.Pal.Headless;
using FluentGpu.Rhi.Headless;
using FluentGpu.Text.Headless;
using static FluentGpu.VerticalSlice.Harness.Gate;

// F241: the inactive-window frame throttle covered only Anim and Caret wakes. A FrameClock.Tick poller (a lyrics stepper, an
// equalizer, a playhead ticker) marks itself due-now, so it fell through to the panel-rate path in a window nobody was looking
// at — and the render thread that presents the pop-out then served that window first. A BACKGROUND window now paces every
// autonomous wake at InactiveFrameIntervalMs (HostWaitKind.InactiveThrottle); input-driven wakes stay exempt by construction
// (they set other bits). The render thread learns the same background state through two mirrors: occluded (park like a
// minimize) and the throttle floor.
static class InactiveThrottleChecks
{
    internal static void Run(StringTable strings)
    {
        using var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("inactive-throttle", new Size2(200, 120), 1f));
        window.Show();
        var device = new HeadlessGpuDevice();
        var root = new FrameClockPollerRoot();   // two live FrameClock.Tick pollers (render-effect + ownerless hook effect)
        using var host = new AppHost(app, window, device, new HeadlessFontSystem(strings), strings, root);
        for (int i = 0; i < 6; i++) host.RunFrame();

        // Foreground: the pollers are due-now work, so the wait is never the inactive throttle's.
        WakeReasons activeWake = host.CurrentWakeReasons;
        host.RecommendedWaitMs();
        HostWaitKind activeKind = host.LastWaitKind;
        bool activeNotThrottled = activeKind != HostWaitKind.InactiveThrottle && host.RenderThrottleMsForTest == 0;

        // Background: the same poller-only work is paced at InactiveFrameIntervalMs (a 1..~40 ms quantized wait, never 0 = a poll).
        window.IsActive = false;
        host.RunFrame();
        host.RunFrame();                          // consume the WindowBlur edge
        WakeReasons inactiveWake = host.CurrentWakeReasons;
        int inactiveWait = host.RecommendedWaitMs();
        HostWaitKind inactiveKind = host.LastWaitKind;
        bool onlyPoller = (inactiveWake & WakeReasons.FrameClockPoller) != 0
                          && (inactiveWake & ~(WakeReasons.FrameClockPoller | WakeReasons.Anim | WakeReasons.Caret)) == 0;
        bool throttled = inactiveKind == HostWaitKind.InactiveThrottle && inactiveWait >= 1 && inactiveWait <= 40;
        bool mirrored = host.RenderThrottleMsForTest == host.InactiveFrameIntervalMs;

        // The knob: 0 disables the throttle (a background window then paces by its own cadence alone).
        int saved = host.InactiveFrameIntervalMs;
        host.InactiveFrameIntervalMs = 0;
        host.RecommendedWaitMs();
        bool disabled = host.LastWaitKind != HostWaitKind.InactiveThrottle;
        host.InactiveFrameIntervalMs = saved;

        // Foregrounded again: back to the full-rate path.
        window.IsActive = true;
        host.RunFrame();
        host.RunFrame();
        host.RecommendedWaitMs();
        bool restored = host.LastWaitKind != HostWaitKind.InactiveThrottle && host.RenderThrottleMsForTest == 0;

        Check("gate.inactive.throttle-autonomous-wakes a BACKGROUND window whose only work is a FrameClock.Tick poller waits on the InactiveThrottle branch (a 1..40 ms quantized wait, mirrored to the render thread as the throttle floor), a foreground one does not, InactiveFrameIntervalMs = 0 disables it, and focus restores the full-rate path",
            activeNotThrottled && onlyPoller && throttled && mirrored && disabled && restored,
            $"activeWake={activeWake} activeKind={activeKind} inactiveWake={inactiveWake} inactiveKind={inactiveKind} inactiveWait={inactiveWait} mirrored={mirrored}({host.RenderThrottleMsForTest}) disabled={disabled} restored={restored}");

        // Occlusion parks render motion like a minimize: the UI mirrors the swapchain's IsOccluded to the render thread, both ways.
        bool occludedBefore = host.RenderOccludedForTest;
        device.PrimarySwapchain!.Occluded = true;
        host.RunFrame();
        host.RunFrame();
        bool occludedRose = host.RenderOccludedForTest;
        device.PrimarySwapchain!.Occluded = false;
        host.RunFrame();
        host.RunFrame();
        bool occludedFell = !host.RenderOccludedForTest;
        Check("gate.inactive.occlusion-mirrors-to-render the swapchain's IsOccluded reaches the render thread's park flag within a frame, both ways",
            !occludedBefore && occludedRose && occludedFell,
            $"before={occludedBefore} rose={occludedRose} fell={occludedFell}");

        TierPollerCapChecks(strings);
    }

    // F245: a FrameClock.Tick poller marks every frame due-now, so on a weak GPU tier it ran at the panel rate. The host now serves
    // a poller-only frame every 2nd refresh on a 120 Hz panel (HostWaitKind.TierPaced, <= 60 Hz); a strong/unknown tier, a 60 Hz
    // panel (its display tick already IS the cap) and a frame that carries anything but the poller are untouched.
    static void TierPollerCapChecks(StringTable strings)
    {
        static AppHost Make(HeadlessPlatformApp app, long periodQpc, StringTable strings)
        {
            var inner = new HeadlessWindow(new WindowDesc("tier-poller", new Size2(200, 120), 1f));
            inner.Show();
            var host = new AppHost(app, new FluentGpu.VerticalSlice.Harness.TinyPeriodWindow(inner, periodQpc), new HeadlessGpuDevice(),
                new HeadlessFontSystem(strings), strings, new FrameClockPollerRoot());
            for (int i = 0; i < 6; i++) host.RunFrame();
            return host;
        }

        using var app = new HeadlessPlatformApp();
        using var fast = Make(app, System.Diagnostics.Stopwatch.Frequency / 120, strings);
        WakeReasons wake = fast.CurrentWakeReasons;
        bool onlyPoller = (wake & WakeReasons.FrameClockPoller) != 0
                          && (wake & ~(WakeReasons.FrameClockPoller | WakeReasons.Anim | WakeReasons.Caret)) == 0;

        fast.RecommendedWaitMs();
        bool strongUntouched = fast.LastWaitKind != HostWaitKind.TierPaced;      // headless is never weak
        fast.WeakTierForTest = true;
        int weakWait = fast.RecommendedWaitMs();
        bool weakCapped = fast.LastWaitKind == HostWaitKind.TierPaced && weakWait >= 1 && weakWait <= 17;
        fast.WeakTierForTest = false;
        fast.RecommendedWaitMs();
        bool strongAgain = fast.LastWaitKind != HostWaitKind.TierPaced;

        using var slow = Make(app, System.Diagnostics.Stopwatch.Frequency / 60, strings);
        slow.WeakTierForTest = true;
        slow.RecommendedWaitMs();
        bool sixtyHzUntouched = slow.LastWaitKind != HostWaitKind.TierPaced;

        Check("gate.tier.poller-cap on a weak tier a poller-only frame is paced to <= 60 Hz (TierPaced, a 1..17 ms wait at 120 Hz); a strong tier and a 60 Hz panel keep the display-rate path",
            onlyPoller && strongUntouched && weakCapped && strongAgain && sixtyHzUntouched,
            $"wake={wake} strongKind={fast.LastWaitKind} weakWait={weakWait} sixtyHzKind={slow.LastWaitKind}");
    }
}
