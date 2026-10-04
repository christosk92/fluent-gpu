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
/// F099: a device loss rebuilds EVERY swapchain, but a detached pop-out has no <c>DeviceLostCoordinator</c> of its own, so the
/// parent's recovery must reach it: invalidate its render-side state (target epoch, elision baseline, owed present) on the
/// render thread, then post it a full repaint on its UI thread. The recovery itself needs a real render thread (a Headless
/// window never spawns one), so both halves are driven directly on a real headless child through the seams the parent's
/// <c>RecoverDeviceAfterDump</c> calls. Serial: it constructs several hosts.
/// </summary>
[Collection(SerialTestCollection.Name)]
public sealed class DetachedDeviceRecoveryTests
{
    private sealed class EmptyRoot : Component
    {
        public override Element Render() => Ui.VStack(0);
    }

    /// <summary>Something to paint, so a child frame has a non-empty draw list.</summary>
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

        public Rig()
        {
            var strings = new StringTable();
            var parentWindow = new HeadlessWindow(new WindowDesc("main", new Size2(320, 240), 1f));
            parentWindow.Show();
            Parent = new AppHost(App, parentWindow, Device, new HeadlessFontSystem(strings), strings, new EmptyRoot());
            Parent.RunFrame();
            ChildWindow = new HeadlessWindow(new WindowDesc("pop-out", new Size2(320, 180), 1f, Composited: true, CustomFrame: true));
            ChildWindow.Show();
            Child = new AppHost(App, ChildWindow, Device, new HeadlessFontSystem(strings), strings, new PaintedRoot(),
                images: null, frameTime: null, compositeSwapchain: true, isDetachedChild: true, parentRenderThread: null);
            Parent.AdoptDetachedChild(Child);
        }

        /// <summary>Run the child until its static scene is on screen and it has gone idle.</summary>
        public void Settle()
        {
            for (int i = 0; i < 6; i++) Child.RunFrame();
        }

        public void Dispose() { Parent.Dispose(); App.Dispose(); }
    }

    [Fact]
    public void AnIdleStaticChild_IsRepaintedInFull_WhenTheRecoveryPostsItsRepaint()
    {
        using var rig = new Rig();
        rig.Settle();
        Assert.True(rig.Device.DirectSubmitCount >= 1);
        rig.Child.RunFrame();
        rig.Child.RunFrame();
        int settled = rig.Device.DirectSubmitCount;   // an idle static scene: the elision baseline the loss makes stale

        // What RecoverDeviceAfterDump does for each child: the render side first, then the UI half posted to the child's own queue.
        rig.Child.InvalidateRenderStateAfterDeviceLoss();
        rig.Child.Post(rig.Child.RepaintAfterDeviceRecovery);
        rig.Child.RunFrame();

        Assert.True(rig.Device.DirectSubmitCount > settled);   // the rebuilt (empty) target is repainted in full, not elided as identical
        Assert.True(rig.Child.LastStats.Rendered);
    }

    [Fact]
    public void ARepaintPostedToAReapedChild_IsInert()
    {
        using var rig = new Rig();
        rig.Settle();

        rig.Child.Post(rig.Child.RepaintAfterDeviceRecovery);   // queued behind the recovery...
        rig.ChildWindow.IsClosed = true;
        rig.Parent.TickDetachedHosts();                         // ...and the user closes the pop-out first: reap forwards its queue

        rig.Parent.RunFrame();                                  // the forwarded repaint runs on the parent's UI thread and does nothing
        rig.Parent.RunFrame();
        Assert.Equal(0, rig.Parent.PendingUiPostCount);
    }

    [Fact]
    public void InvalidatingARenderStateTwice_IsHarmless()
    {
        using var rig = new Rig();
        rig.Settle();
        rig.Child.InvalidateRenderStateAfterDeviceLoss();
        rig.Child.InvalidateRenderStateAfterDeviceLoss();
        rig.Child.Post(rig.Child.RepaintAfterDeviceRecovery);
        rig.Child.Post(rig.Child.RepaintAfterDeviceRecovery);
        rig.Child.RunFrame();
        rig.Child.RunFrame();
        Assert.False(rig.Child.RenderFailed);
    }
}
