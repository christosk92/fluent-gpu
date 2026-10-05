using System;
using System.Diagnostics;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Hosting;
using FluentGpu.Hosting.Threading;
using FluentGpu.Pal;
using FluentGpu.Pal.Headless;
using FluentGpu.Rhi.Headless;
using FluentGpu.Text.Headless;
using Xunit;

namespace FluentGpu.Engine.Tests;

/// <summary>
/// Pop-out open and close (F115 / F117 / F110). The pop-out window is created HIDDEN and shown exactly once, after its own
/// swapchain has presented its first frame (a composited window shown earlier is a hollow rectangle); while it waits the host
/// is exempt from the hidden-window park, with a timeout as the fallback. The close reaper unregisters and disposes the child
/// inside ONE render-thread rendezvous instead of two. Serial: it constructs several hosts (process-static seams).
/// </summary>
[Collection(SerialTestCollection.Name)]
public sealed class DetachedRevealTests
{
    private sealed class EmptyRoot : Component
    {
        public override Element Render() => Ui.VStack(0);
    }

    /// <summary>Something to paint, so the child's first frame has a non-empty draw list.</summary>
    private sealed class PaintedRoot : Component
    {
        public override Element Render() => new BoxEl { Grow = 1f, Fill = ColorF.FromRgba(20, 60, 120) };
    }

    private sealed class Rig : IDisposable
    {
        public HeadlessPlatformApp App { get; } = new();
        public HeadlessGpuDevice Device { get; } = new();
        public HeadlessWindow ChildWindow { get; }
        public AppHost Parent { get; }
        public AppHost Child { get; }

        /// <summary>A parent, and a detached child whose window was created HIDDEN (never shown) with its reveal armed.</summary>
        public Rig(int revealTimeoutMs = DetachedRevealGate.TimeoutMs, bool armReveal = true, RenderThread? parentRenderThread = null,
                   bool adopt = true)
        {
            var strings = new StringTable();
            var parentWindow = new HeadlessWindow(new WindowDesc("main", new Size2(320, 240), 1f));
            parentWindow.Show();
            Parent = new AppHost(App, parentWindow, Device, new HeadlessFontSystem(strings), strings, new EmptyRoot());
            Parent.RunFrame();
            ChildWindow = new HeadlessWindow(new WindowDesc("pop-out", new Size2(320, 180), 1f, Composited: true, CustomFrame: true));
            ChildWindow.IsVisible = false;   // created hidden, exactly as OpenDetachedWindow now leaves it
            Child = new AppHost(App, ChildWindow, Device, new HeadlessFontSystem(strings), strings, new PaintedRoot(),
                images: null, frameTime: null, compositeSwapchain: true, isDetachedChild: true, parentRenderThread: parentRenderThread);
            if (armReveal) Child.BeginDetachedReveal(alwaysOnTop: true, Stopwatch.GetTimestamp(), windowCreateMs: 1.5, hostCtorMs: 2.5, revealTimeoutMs);
            if (adopt) Parent.AdoptDetachedChild(Child);
        }

        /// <summary>The child's own swapchain (the second one the device created; the first is the main window's).</summary>
        public HeadlessSwapchain ChildSwapchain => Device.CreatedSwapchains[1];

        public void Dispose()
        {
            Parent.Dispose();
            App.Dispose();
        }
    }

    [Fact]
    public void Gate_IsDueOnAPresentedFrame_OrOnceTheDeadlinePassed_AndNotBefore()
    {
        long start = 1_000;
        long deadline = DetachedRevealGate.DeadlineQpc(start, timeoutMs: 100);
        Assert.Equal(start + 100 * Stopwatch.Frequency / 1000, deadline);
        Assert.False(DetachedRevealGate.Due(presentedContent: false, nowQpc: deadline - 1, deadline));
        Assert.True(DetachedRevealGate.Due(presentedContent: false, nowQpc: deadline, deadline));
        Assert.True(DetachedRevealGate.Due(presentedContent: true, nowQpc: start, deadline));   // a presented frame beats the clock
    }

    [Fact]
    public void ThePopOutIsShown_ExactlyOnce_AfterItsFirstPresent_NeverBefore()
    {
        using var rig = new Rig();
        var cw = rig.ChildWindow;
        int revealed = 0;
        DetachedOpenTiming seen = default;
        rig.Child.OnRevealed = t => { revealed++; seen = t; };

        // Hidden while it is built: nothing has been shown, and the host is NOT parked despite the hidden window - its first
        // frame has to be able to paint into it.
        Assert.False(cw.IsVisible);
        Assert.Equal(0, cw.ShowCalls);
        Assert.False(rig.Child.IsParked);
        Assert.False(rig.ChildSwapchain.HasPresentedContent);

        rig.Parent.TickDetachedHosts();   // the child's first frame presents into the hidden window, then the reveal gate looks

        Assert.True(rig.ChildSwapchain.HasPresentedContent);
        Assert.Equal(1, cw.ShowCalls);
        Assert.True(cw.IsVisible);
        Assert.True(cw.Topmost);          // the request's always-on-top applies when it appears
        Assert.Equal(1, revealed);
        Assert.False(seen.TimedOut);
        Assert.Equal(1.5, seen.WindowCreateMs);
        Assert.Equal(2.5, seen.HostCtorMs);
        Assert.True(seen.FirstFrameMs >= 0.0 && seen.FirstPresentMs >= seen.FirstFrameMs, $"split {seen}");
        // F215: the render side's own stamp of that first successful present is never later than the moment the UI noticed it.
        Assert.True(seen.RenderPresentMs >= 0.0 && seen.RenderPresentMs <= seen.FirstPresentMs, $"split {seen}");
        Assert.Equal(seen, rig.Child.OpenTiming);

        for (int i = 0; i < 5; i++) rig.Parent.TickDetachedHosts();   // later ticks never show it again
        Assert.Equal(1, cw.ShowCalls);
        Assert.Equal(1, revealed);
    }

    [Fact]
    public void ThePopOutStaysHidden_WhileItsFirstPresentHasNotLanded_AndStaysUnparkedAndPolling()
    {
        using var rig = new Rig(revealTimeoutMs: 60_000);
        rig.ChildSwapchain.PresentStandDown = true;   // the backend refuses the present: no content reaches the glass

        for (int i = 0; i < 4; i++) rig.Parent.TickDetachedHosts();

        Assert.Equal(0, rig.ChildWindow.ShowCalls);   // an empty window must not be revealed
        Assert.False(rig.ChildWindow.IsVisible);
        Assert.False(rig.Child.IsParked);             // still exempt: the frame it waits for has to be producible
        int wait = rig.Child.RecommendedWaitMs();
        Assert.InRange(wait, 0, DetachedRevealGate.PollMs);   // never a block-until-message: the UI comes back to look

        rig.ChildSwapchain.PresentStandDown = false;
        rig.Child.RequestFullRepaintOnce();
        rig.Parent.TickDetachedHosts();               // the present lands -> the window appears
        Assert.Equal(1, rig.ChildWindow.ShowCalls);
    }

    [Fact]
    public void APresentStoodDownWhileHidden_IsRepresentedRightAfterTheReveal_NotAfterTheOcclusionProbe()
    {
        using var rig = new Rig(revealTimeoutMs: 60_000);
        rig.Child.RunFrame();                          // the first present lands in the hidden window; no reveal tick yet
        Assert.True(rig.ChildSwapchain.HasPresentedContent);
        Assert.Equal(0, rig.ChildWindow.ShowCalls);

        rig.ChildSwapchain.PresentStandDown = true;    // a later present (motion, or the same-tick frame) hits the hidden-window stand-down
        rig.Child.RequestFullRepaintOnce();
        rig.Child.RunFrame();
        Assert.True(rig.ChildSwapchain.IsOccluded);

        rig.Parent.TickDetachedHosts();                // reveal, with the stood-down state still latched
        Assert.Equal(1, rig.ChildWindow.ShowCalls);
        Assert.True(rig.ChildSwapchain.IsOccluded);

        rig.ChildSwapchain.PresentStandDown = false;   // the window is visible now: the next present lands
        int presents = rig.ChildSwapchain.PresentCount;
        rig.Parent.TickDetachedHosts();                // no 250 ms probe wait: the reveal itself asked for the repaint
        Assert.True(rig.ChildSwapchain.PresentCount > presents);
        Assert.False(rig.ChildSwapchain.IsOccluded);
    }

    [Fact]
    public void TheRevealTimeout_ShowsTheWindowAnyway_ForABackendThatNeverReportsAPresent()
    {
        using var rig = new Rig(revealTimeoutMs: 0);
        rig.ChildSwapchain.PresentStandDown = true;
        DetachedOpenTiming seen = default;
        rig.Child.OnRevealed = t => seen = t;

        rig.Parent.TickDetachedHosts();

        Assert.Equal(1, rig.ChildWindow.ShowCalls);
        Assert.True(seen.TimedOut);
        Assert.Equal(-1.0, seen.RenderPresentMs);   // F215: no present was seen, so there is no render-side stamp
        Assert.False(rig.ChildSwapchain.HasPresentedContent);
        rig.Parent.TickDetachedHosts();
        Assert.Equal(1, rig.ChildWindow.ShowCalls);
    }

    [Fact]
    public void AHiddenChildWithNoReveal_StillParksLikeAnyHiddenWindow()
    {
        using var rig = new Rig(armReveal: false);   // not an unrevealed pop-out: an ordinary hidden window
        rig.Child.RunFrame();
        Assert.True(rig.Child.IsParked);
        Assert.Equal(0, rig.ChildWindow.ShowCalls);
    }

    [Fact]
    public void TheAlwaysOnTopChosenWhileHidden_IsTheOneApplied_AtReveal()
    {
        using var rig = new Rig(revealTimeoutMs: 60_000);
        rig.ChildSwapchain.PresentStandDown = true;
        rig.Parent.TickDetachedHosts();
        Assert.Equal(0, rig.ChildWindow.ShowCalls);

        // The handle's SetTopmost (a preference toggled before the window appeared) updates what the reveal applies.
        var handle = rig.Parent.CreateDetachedHandle(rig.Child, rig.ChildWindow);
        handle.SetTopmost(false);
        Assert.False(rig.ChildWindow.Topmost);

        rig.ChildSwapchain.PresentStandDown = false;
        rig.Child.RequestFullRepaintOnce();
        rig.Parent.TickDetachedHosts();
        Assert.Equal(1, rig.ChildWindow.ShowCalls);
        Assert.False(rig.ChildWindow.Topmost);   // not re-forced to the stale open-time value
    }

    [Fact]
    public void ReapingAChild_TakesOneRenderThreadRendezvous_NotTwo()
    {
        ThreadGuard.BindCurrent(ThreadGuard.ThreadRole.Ui);
        var seam = new SceneFramePublisher();
        var rt = new RenderThread(seam, _ => { }, async: false);
        try
        {
            using var rig = new Rig(parentRenderThread: rt);
            long afterOpen = rt.QuiesceCount;
            Assert.Equal(1L, afterOpen);   // opening: the child's swapchain creation is the ONE park (the attach publishes without parking)

            rig.ChildWindow.IsClosed = true;
            rig.Parent.TickDetachedHosts();   // reap: unregister + dispose

            Assert.Equal(afterOpen + 1, rt.QuiesceCount);   // detach and dispose share a single rendezvous
            Assert.Equal(0, rig.Parent.DetachedWindowCount);
        }
        finally { rt.Dispose(); }
    }

    [Fact]
    public void DisposingAChildDirectly_StillParksOnce()
    {
        ThreadGuard.BindCurrent(ThreadGuard.ThreadRole.Ui);
        var seam = new SceneFramePublisher();
        var rt = new RenderThread(seam, _ => { }, async: false);
        try
        {
            using var rig = new Rig(parentRenderThread: rt, adopt: false);   // not tracked by the parent: this test disposes it itself
            long before = rt.QuiesceCount;
            rig.Child.Dispose();
            Assert.Equal(before + 1, rt.QuiesceCount);
        }
        finally { rt.Dispose(); }
    }
}
