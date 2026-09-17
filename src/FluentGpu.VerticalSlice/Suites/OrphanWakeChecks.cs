using System;
using System.Diagnostics;
using System.Threading;
using FluentGpu.Animation;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Hosting;
using FluentGpu.Pal;
using FluentGpu.Pal.Headless;
using FluentGpu.Rhi.Headless;
using FluentGpu.Scene;
using FluentGpu.Text.Headless;
using static FluentGpu.VerticalSlice.Harness.Gate;

// Scheduler-policy coverage: explicitly enables render ownership before asking the host whether to wake.
// Paint restores headless ownership, so these checks do NOT claim real render-thread execution coverage.
static class OrphanWakeChecks
{
    sealed class EmptyRoot : Component
    {
        public override Element Render() => new BoxEl { Width = 200f, Height = 120f };
    }

    public static void Run(StringTable strings)
    {
        using var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("orphan-wake", new Size2(200, 120), 1f));
        window.Show();
        using var host = new AppHost(app, window, new HeadlessGpuDevice(), new HeadlessFontSystem(strings), strings, new EmptyRoot());
        Settle(host);

        NodeHandle trackless = AddOrphan(host, tracked: false);
        host.Animation.RenderOwnsCompositor = true;
        bool wakes = OrphanWake(host);
        host.RunFrame();
        Check("gate.anim.orphan-render-owned-trackless a settled orphan requests the UI frame that reclaims it",
            wakes && !host.Scene.IsLive(trackless) && host.Scene.OrphanCount == 0,
            $"wake={wakes} live={host.Scene.IsLive(trackless)} orphans={host.Scene.OrphanCount}");
        Settle(host);

        NodeHandle healthy = AddOrphan(host, tracked: true);
        host.Animation.RenderOwnsCompositor = true;
        int wait = host.RecommendedWaitMs();
        bool held = !OrphanWake(host) && host.Scene.IsLive(healthy) && host.Animation.HasTracks(healthy);
        Check("gate.anim.orphan-render-owned-pending an unfinished exit sleeps with a bounded wall-backstop wait",
            held && !host.HasActiveWork && wait > 0 && wait <= 2000,
            $"held={held} wake={host.CurrentWakeReasons} wait={wait}ms");

        // Warm the actual scheduling calls before measuring their allocation cost.
        for (int i = 0; i < 32; i++) { _ = host.CurrentWakeReasons; _ = host.RecommendedWaitMs(); }
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 128; i++) { _ = host.CurrentWakeReasons; _ = host.RecommendedWaitMs(); }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Check("gate.anim.orphan-render-owned-wait-alloc-zero checking a pending orphan and its deadline allocates nothing",
            allocated == 0, $"allocated={allocated}");

        window.Minimize();
        host.RunFrame(); // consume the park edge; the following wait measures steady minimized state
        int minimizedWait = host.RecommendedWaitMs();
        Check("gate.anim.orphan-render-owned-minimized an unfinished exit does not poll a minimized host",
            minimizedWait == -1, $"wait={minimizedWait}");
        window.State = WindowState.Normal;

        // Preserve the real enqueue timestamp: an indefinitely blocked UI cannot advance its animation clock.
        // Wait for the host's own recommended timeout, with a tiny rounding allowance, then ask for work again.
        long enqueued = host.Scene.OrphanEnqueuedTicks(0);
        while ((Stopwatch.GetTimestamp() - enqueued) * 1000.0 / Stopwatch.Frequency < 2010)
            Thread.Sleep(Math.Max(1, host.RecommendedWaitMs()));
        bool wallWakes = OrphanWake(host);
        host.RunFrame();
        Check("gate.anim.orphan-render-owned-wall-deadline a frozen animation clock still wakes and reclaims at the wall backstop",
            wallWakes && !host.Scene.IsLive(healthy) && !host.Animation.HasTracks(healthy),
            $"wake={wallWakes} live={host.Scene.IsLive(healthy)} tracks={host.Animation.HasTracks(healthy)}");
        Settle(host);

        // SceneStore exposes its animation timebase so the enqueue age can be set without waiting for UI frames.
        // Restore it before RunFrame: Paint publishes the host's own animation clock again.
        double currentClock = host.Scene.AnimClockMs;
        host.Scene.AnimClockMs = currentClock - 400d;
        NodeHandle due = AddOrphan(host, tracked: true, maxAgeMs: 350f);
        host.Scene.AnimClockMs = currentClock;
        host.Animation.RenderOwnsCompositor = true;
        bool ownWakes = OrphanWake(host);
        host.RunFrame();
        Check("gate.anim.orphan-render-owned-animation-deadline an exit past its own animation-age deadline wakes and reclaims",
            ownWakes && !host.Scene.IsLive(due) && !host.Animation.HasTracks(due),
            $"wake={ownWakes} live={host.Scene.IsLive(due)} tracks={host.Animation.HasTracks(due)}");
    }

    static bool OrphanWake(AppHost host) => (host.CurrentWakeReasons & WakeReasons.Orphans) != 0;

    static NodeHandle AddOrphan(AppHost host, bool tracked, float maxAgeMs = 0f)
    {
        var node = host.Scene.CreateNode(1);
        host.Scene.AppendChild(host.Scene.Root, node);
        host.Scene.Bounds(node) = new RectF(0, 0, 40, 40);
        if (tracked) host.Animation.Animate(node, AnimChannel.Opacity, 1f, 0f, 60_000f, Easing.Linear);
        host.Scene.Orphan(node, maxAgeMs);
        return node;
    }

    static void Settle(AppHost host)
    {
        host.Animation.RenderOwnsCompositor = false;
        for (int i = 0; i < 8 || (i < 400 && host.HasActiveWork); i++) host.RunFrame();
    }
}
