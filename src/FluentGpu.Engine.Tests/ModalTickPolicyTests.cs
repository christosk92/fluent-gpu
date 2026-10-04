using System;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Hosting;
using FluentGpu.Pal;
using FluentGpu.Pal.Headless;
using FluentGpu.Rhi.Headless;
using FluentGpu.Text.Headless;
using Xunit;

namespace FluentGpu.Engine.Tests;

/// <summary>
/// F093: one window's OS modal move/size loop runs inside a DispatchMessage on the shared UI thread, so the process frame loop
/// is suspended for the whole drag. Each beat of the loop (<see cref="IPlatformWindow.ModalLoopTick"/>) now repaints the OTHER
/// live windows, paint-only and throttled by one shared stamp; a pop-out's own swapchain resize is one latest-wins slot, so a
/// live resize costs one ResizeBuffers per render turn. The throttle and the peer rule are pure; the host wiring is driven
/// through real headless parent and child hosts and the headless window's tick seam. Serial: it constructs several hosts.
/// </summary>
[Collection(SerialTestCollection.Name)]
public sealed class ModalTickPolicyTests
{
    private sealed class EmptyRoot : Component
    {
        public override Element Render() => Ui.VStack(0);
    }

    // ── pure policy ────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void PeerThrottle_FirstTickRuns_ThenSkipsUntilTheIntervalPasses()
    {
        long last = 0;
        Assert.False(ModalPaintThrottle.ShouldSkipPeerTick(10_000, ref last));                                    // first: allowed, stamped
        Assert.Equal(10_000, last);
        Assert.True(ModalPaintThrottle.ShouldSkipPeerTick(10_000 + ModalPaintThrottle.PeerTickIntervalMs - 1, ref last));
        Assert.Equal(10_000, last);                                                                                // a skip never restamps
        Assert.False(ModalPaintThrottle.ShouldSkipPeerTick(10_000 + ModalPaintThrottle.PeerTickIntervalMs, ref last));
        Assert.Equal(10_000 + ModalPaintThrottle.PeerTickIntervalMs, last);
    }

    [Fact]
    public void PeerThrottle_ADenseBeat_RunsAtMostOncePerInterval()
    {
        long last = 0;
        int ran = 0;
        // The dragged window's 8 ms timer AND an edge resize's WM_SIZE both beat: 1 ms apart for one second.
        for (long t = 5_000; t < 6_000; t++)
            if (!ModalPaintThrottle.ShouldSkipPeerTick(t, ref last)) ran++;
        Assert.InRange(ran, 1000 / ModalPaintThrottle.PeerTickIntervalMs - 1, 1000 / ModalPaintThrottle.PeerTickIntervalMs + 1);
    }

    [Fact]
    public void PeerThrottle_AClockThatWentBackwards_NeverStarvesThePeers()
    {
        long last = 90_000;
        Assert.False(ModalPaintThrottle.ShouldSkipPeerTick(1_000, ref last));   // a reset tick source: tick and re-stamp
        Assert.Equal(1_000, last);
        Assert.True(ModalPaintThrottle.ShouldSkipPeerTick(1_001, ref last));
    }

    [Theory]
    [InlineData(false, false, false, false, true)]    // a live, unparked bystander: painted
    [InlineData(true, false, false, false, false)]    // the window in the loop paints itself
    [InlineData(false, true, false, false, false)]    // closed
    [InlineData(false, false, true, false, false)]    // minimized / hidden / cloaked / covered paints nothing
    [InlineData(false, false, false, true, false)]    // never nest a paint inside another loop's own
    public void PeerRule(bool isSource, bool closed, bool parked, bool inModalLoop, bool expected)
        => Assert.Equal(expected, ModalPaintThrottle.ShouldPaintPeer(isSource, closed, parked, inModalLoop));

    // ── host wiring ────────────────────────────────────────────────────────────────────────────────────────────────

    private sealed class Rig : IDisposable
    {
        public HeadlessPlatformApp App { get; } = new();
        public HeadlessWindow ParentWindow { get; }
        public HeadlessWindow ChildWindow { get; }
        public AppHost Parent { get; }
        public AppHost Child { get; }

        public Rig()
        {
            var strings = new StringTable();
            var device = new HeadlessGpuDevice();
            ParentWindow = new HeadlessWindow(new WindowDesc("main", new Size2(320, 240), 1f));
            ParentWindow.Show();
            Parent = new AppHost(App, ParentWindow, device, new HeadlessFontSystem(strings), strings, new EmptyRoot());
            Parent.RunFrame();
            ChildWindow = new HeadlessWindow(new WindowDesc("pop-out", new Size2(320, 180), 1f, Composited: true, CustomFrame: true));
            ChildWindow.Show();
            Child = new AppHost(App, ChildWindow, device, new HeadlessFontSystem(strings), strings, new EmptyRoot(),
                images: null, frameTime: null, compositeSwapchain: true, isDetachedChild: true, parentRenderThread: null);
            Parent.AdoptDetachedChild(Child);
            Child.RunFrame();
        }

        public void Dispose() { Parent.Dispose(); App.Dispose(); }
    }

    /// <summary>A queued UI post is drained ONLY by a Paint (or a RunFrame): its running is the observable proof that a host
    /// painted, and a RunFrame is excluded by the tests' own structure (nothing here calls one after the post).</summary>
    private static Action Counter(out Func<int> read)
    {
        int n = 0;
        read = () => n;
        return () => n++;
    }

    [Fact]
    public void AWindowsModalBeat_PaintsEveryOtherWindow_ButNotItself()
    {
        using var rig = new Rig();
        rig.ParentWindow.InModalLoop = true;   // the parent's loop is the one running
        rig.Parent.Post(Counter(out var parentRan));
        rig.Child.Post(Counter(out var childRan));

        Assert.NotNull(rig.ParentWindow.ModalLoopTick);   // the host wired the seam on construction
        rig.ParentWindow.ModalLoopTick!.Invoke();

        Assert.Equal(1, childRan());    // the pop-out ticked while the main window is dragged
        Assert.Equal(0, parentRan());   // the dragged window paints itself through its own keep-alive, not through the peer round
        Assert.Equal(1, rig.Parent.ModalPeerPaintsForTest);
    }

    [Fact]
    public void ADraggedPopOut_TicksTheMainWindowToo()
    {
        using var rig = new Rig();
        rig.ChildWindow.InModalLoop = true;
        rig.Parent.Post(Counter(out var parentRan));
        rig.Child.Post(Counter(out var childRan));

        rig.ChildWindow.ModalLoopTick!.Invoke();

        Assert.Equal(1, parentRan());   // the main window no longer freezes for the whole pop-out drag
        Assert.Equal(0, childRan());
        Assert.Equal(1, rig.Child.ModalPeerPaintsForTest);   // the count lives on the root whichever window asks
    }

    [Fact]
    public void ABurstOfBeats_IsThrottledByOneSharedStamp()
    {
        using var rig = new Rig();
        rig.ChildWindow.InModalLoop = true;
        for (int i = 0; i < 400; i++)
        {
            rig.ChildWindow.ModalLoopTick!.Invoke();
            rig.ParentWindow.ModalLoopTick!.Invoke();   // two windows beating: still ONE stamp
        }
        long paints = rig.Parent.ModalPeerPaintsForTest;
        Assert.InRange(paints, 1, 799);   // never one per beat (the loop takes far less than a second for 800 beats)
    }

    [Fact]
    public void AParkedOrClosedPeer_IsNotPainted()
    {
        using var rig = new Rig();
        rig.ChildWindow.InModalLoop = true;
        rig.ParentWindow.State = WindowState.Minimized;   // the main window is minimized: nothing of it is on screen
        rig.Parent.Post(Counter(out var parentRan));
        rig.ChildWindow.ModalLoopTick!.Invoke();
        Assert.Equal(0, parentRan());
        Assert.Equal(0, rig.Parent.ModalPeerPaintsForTest);
    }

    [Fact]
    public void ACloseRequestedChild_IsNotPaintedByAPeerTick()
    {
        using var rig = new Rig();
        rig.ParentWindow.InModalLoop = true;
        rig.ChildWindow.IsClosed = true;   // the reaper has not run yet (it runs in the frame loop, which is suspended)
        rig.Child.Post(Counter(out var childRan));
        rig.ParentWindow.ModalLoopTick!.Invoke();
        Assert.Equal(0, childRan());
    }

    [Fact]
    public void APeerTick_NeverPumpsInput_ItIsPaintOnly()
    {
        using var rig = new Rig();
        rig.ChildWindow.InModalLoop = true;
        // An input event queued for the main window is dispatched by PumpInto, which only RunFrame calls. The peer round must
        // leave it queued: a RunFrame nested in another window's dispatch is exactly what it must not do.
        rig.ParentWindow.QueueInput(new InputEvent(InputKind.WindowFocus, default, 0, 0));
        rig.ChildWindow.ModalLoopTick!.Invoke();
        Assert.Equal(1, rig.Parent.ModalPeerPaintsForTest);   // it painted ...
        var ring = new InputEventRing();
        Assert.True(rig.ParentWindow.PumpInto(ring) > 0);     // ... and the queued event was still there
    }

    // ── coalesced own resize ───────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ALiveResize_CollapsesToOneResizeBuffersPerRenderTurn()
    {
        using var rig = new Rig();
        // A live edge-resize posts one request per WM_SIZE between two render turns.
        for (int i = 1; i <= 25; i++) rig.Child.PostOwnResizeForTest(new Size2(320 + i, 180 + i));
        rig.Child.DrainPopupRenderActionsForTest();
        Assert.Equal(1, rig.Child.OwnResizesAppliedForTest);

        rig.Child.DrainPopupRenderActionsForTest();   // nothing pending: no second ResizeBuffers
        Assert.Equal(1, rig.Child.OwnResizesAppliedForTest);

        rig.Child.PostOwnResizeForTest(new Size2(640, 360));
        rig.Child.PostOwnResizeForTest(new Size2(641, 361));
        rig.Child.DrainPopupRenderActionsForTest();
        Assert.Equal(2, rig.Child.OwnResizesAppliedForTest);   // the next turn's burst is again one
    }
}
