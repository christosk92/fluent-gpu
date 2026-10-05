using System;
using System.Diagnostics;
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
/// F110: warm reuse of the pop-out host. Closing the pop-out used to dispose its host (window, swapchain, mounted tree, a
/// GPU-idle wait) and every reopen rebuilt all of it on the UI thread. <see cref="IDetachedVideoWindow.Park"/> now hides the window
/// and lets the host park through the ordinary hidden-window gate, keeping the tree and swapchain; <see cref="IDetachedVideoWindow.Unpark"/>
/// re-arms the reveal gate so the window is shown on the child's next frame. Driven through real headless hosts. Serial: several
/// hosts (process-static seams).
/// </summary>
[Collection(SerialTestCollection.Name)]
public sealed class DetachedWarmReuseTests
{
    private sealed class EmptyRoot : Component
    {
        public override Element Render() => Ui.VStack(0);
    }

    private sealed class PaintedRoot : Component
    {
        public override Element Render() => new BoxEl { Grow = 1f, Fill = ColorF.FromRgba(20, 60, 120) };
    }

    private sealed class Rig : IDisposable
    {
        public HeadlessPlatformApp App { get; } = new();
        public HeadlessWindow ChildWindow { get; }
        public AppHost Parent { get; }
        public AppHost Child { get; }
        public IDetachedVideoWindow Handle { get; }

        /// <summary>A parent and a detached child created hidden with its reveal armed, exactly as a fresh open leaves it; when
        /// <paramref name="revealed"/> the first tick has already shown it.</summary>
        public Rig(bool revealed = true)
        {
            var strings = new StringTable();
            var device = new HeadlessGpuDevice();
            var parentWindow = new HeadlessWindow(new WindowDesc("main", new Size2(320, 240), 1f));
            parentWindow.Show();
            Parent = new AppHost(App, parentWindow, device, new HeadlessFontSystem(strings), strings, new EmptyRoot());
            Parent.RunFrame();
            ChildWindow = new HeadlessWindow(new WindowDesc("pop-out", new Size2(320, 180), 1f, Composited: true, CustomFrame: true));
            ChildWindow.IsVisible = false;
            Child = new AppHost(App, ChildWindow, device, new HeadlessFontSystem(strings), strings, new PaintedRoot(),
                images: null, frameTime: null, compositeSwapchain: true, isDetachedChild: true, parentRenderThread: null);
            Child.BeginDetachedReveal(alwaysOnTop: true, Stopwatch.GetTimestamp(), windowCreateMs: 1.0, hostCtorMs: 1.0);
            Parent.AdoptDetachedChild(Child);
            Handle = Parent.CreateDetachedHandle(Child, ChildWindow);
            if (revealed) Parent.TickDetachedHosts();
        }

        public void Dispose() { Parent.Dispose(); App.Dispose(); }
    }

    private static DetachedWindowRequest Request(bool topmost = true)
        => new("again", new Size2(320, 180), new EmptyRoot(), AlwaysOnTop: topmost);

    [Fact]
    public void Park_HidesTheWindow_ParksTheHost_AndKeepsItOpenWithItsSwapchain()
    {
        using var rig = new Rig();
        Assert.True(rig.ChildWindow.IsVisible);

        Assert.True(rig.Handle.Park());

        Assert.True(rig.Handle.IsParked);
        Assert.True(rig.Handle.IsOpen);                 // parked is not closed: the host stays owned and ticked
        Assert.False(rig.ChildWindow.IsVisible);
        rig.Parent.TickDetachedHosts();
        Assert.True(rig.Child.IsParked);                // the ordinary hidden-window gate: no frames while parked
        Assert.Equal(1, rig.Parent.DetachedWindowCount);

        ulong presented = rig.Child.PresentedSequence;
        for (int i = 0; i < 5; i++) { rig.Child.RequestFullRepaintOnce(); rig.Parent.TickDetachedHosts(); }
        Assert.Equal(presented, rig.Child.PresentedSequence);   // stopped producing
        Assert.True(rig.Handle.Park());                          // idempotent while parked
    }

    [Fact]
    public void Park_LeavesFullscreenFirst_SoItReturnsWindowed()
    {
        using var rig = new Rig();
        rig.ChildWindow.SetFullscreen(true);
        Assert.True(rig.ChildWindow.IsFullscreen);

        Assert.True(rig.Handle.Park());

        Assert.False(rig.ChildWindow.IsFullscreen);
        Assert.False(rig.ChildWindow.IsVisible);
    }

    [Fact]
    public void Park_DeliversAPendingSettledBounds_BeforeItHides()
    {
        using var rig = new Rig();
        int delivered = 0;
        rig.Handle.BoundsChanged = _ => delivered++;
        rig.ChildWindow.OuterBoundsPx = new RectF(10, 10, 320, 180);
        rig.Parent.TickDetachedHosts();       // the sampler sees the move, not yet settled
        Assert.Equal(0, delivered);

        Assert.True(rig.Handle.Park());

        Assert.Equal(1, delivered);           // the settle may never have run: the close path's flush, now at park
    }

    [Fact]
    public void Unpark_ReusesTheHost_AndRevealsItOnTheNextChildFrame()
    {
        using var rig = new Rig();
        Assert.True(rig.Handle.Park());
        rig.Parent.TickDetachedHosts();
        int showsBefore = rig.ChildWindow.ShowCalls;

        Assert.True(rig.Handle.Unpark(Request(topmost: false)));
        int revealed = 0;
        DetachedOpenTiming seen = default;
        rig.Handle.OnRevealed = t => { revealed++; seen = t; };   // set AFTER the call, like after an open

        Assert.False(rig.Handle.IsParked);
        Assert.False(rig.ChildWindow.IsVisible);                  // shown by the reveal gate on a frame, not inside Unpark
        Assert.False(rig.Child.IsParked);                         // exempt from the hidden-window park while the reveal is pending

        rig.Parent.TickDetachedHosts();

        Assert.Equal(1, revealed);
        Assert.False(seen.TimedOut);
        Assert.True(rig.ChildWindow.IsVisible);
        Assert.Equal(showsBefore + 1, rig.ChildWindow.ShowCalls);
        Assert.False(rig.ChildWindow.Topmost);                    // the request's always-on-top applies, not the previous open's
        Assert.True(rig.Handle.IsOpen);
        Assert.Equal(1, rig.Parent.DetachedWindowCount);          // the same host, nothing rebuilt
        for (int i = 0; i < 3; i++) rig.Parent.TickDetachedHosts();
        Assert.Equal(1, revealed);                                // shown exactly once per reuse
        Assert.Equal(showsBefore + 1, rig.ChildWindow.ShowCalls);
    }

    [Fact]
    public void AWindowThatIsNotParked_IsNotUnparked_AndAParkedOneCanStillBeClosedAndReaped()
    {
        using var rig = new Rig();
        Assert.False(rig.Handle.Unpark(Request()));               // a live window is not reusable for an open

        int closed = 0;
        rig.Handle.OnClosed = () => closed++;
        Assert.True(rig.Handle.Park());
        rig.ChildWindow.IsClosed = true;                          // the owner's idle timeout (or the app closing) closes it
        rig.Parent.TickDetachedHosts();                           // the ordinary reaper disposes it

        Assert.Equal(1, closed);
        Assert.Equal(0, rig.Parent.DetachedWindowCount);
        Assert.False(rig.Handle.IsOpen);
        Assert.False(rig.Handle.IsParked);
        Assert.False(rig.Handle.Unpark(Request()));
    }

    [Fact]
    public void AWindowStillWaitingForItsReveal_CannotBeParked()
    {
        using var rig = new Rig(revealed: false);

        Assert.False(rig.Handle.Park());                          // the caller closes it instead

        Assert.False(rig.Handle.IsParked);
        Assert.False(rig.ChildWindow.IsVisible);
        Assert.Equal(0, rig.ChildWindow.ShowCalls);
    }

    [Fact]
    public void ABackendWithoutWarmReuse_RefusesBothVerbs()
    {
        IDetachedVideoWindow window = new NoWarmReuse();
        Assert.False(window.IsParked);
        Assert.False(window.Park());
        Assert.False(window.Unpark(Request()));
    }

    private sealed class NoWarmReuse : IDetachedVideoWindow
    {
        public bool IsOpen => true;
        public void SetTopmost(bool topmost) { }
        public void SetBounds(RectF outerBoundsPx) { }
        public void Close() { }
        public Action? OnClosed { get; set; }
        public Action<RectF>? BoundsChanged { get; set; }
        public Action? OnRenderFailed { get; set; }
    }
}
