using System;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Hosting;
using FluentGpu.Pal;
using FluentGpu.Pal.Headless;
using FluentGpu.Rhi.Headless;
using FluentGpu.Scene;
using FluentGpu.Text.Headless;
using Xunit;

namespace FluentGpu.Engine.Tests;

/// <summary>
/// Detached pop-out hygiene around the process-shared <see cref="ImageCache"/> (F108) and a pop-out whose render path latched
/// <c>RenderFailed</c> (F109). A real child cannot fail or present on a render thread headlessly (a Headless window never
/// spawns one), so the image-pump ownership is driven through real headless hosts sharing one counting decoder, and the failed
/// child's decisions through the pure predicate and the state its drain leaves behind. Serial: it constructs several hosts.
/// </summary>
[Collection(SerialTestCollection.Name)]
public sealed class DetachedChildHygieneTests
{
    private sealed class EmptyRoot : Component
    {
        public override Element Render() => Ui.VStack(0);
    }

    /// <summary>A pop-out showing one image from the shared cache.</summary>
    private sealed class ArtRoot : Component
    {
        public override Element Render() => new ImageEl { Source = "art://pop-out", Width = 40f, Height = 40f };
    }

    /// <summary>The headless decoder behind a pump counter: how many times the SHARED cache was drained.</summary>
    private sealed class CountingDecoder : IImageDecoder
    {
        private readonly FakeImageDecoder _inner = new();
        public int Pumps;
        public bool Begin(int id, string source, int targetW, int targetH, ImagePriority priority = ImagePriority.Visible)
            => _inner.Begin(id, source, targetW, targetH, priority);
        public void Pump(ImageCompleteHandler onComplete, ImageReadyHandler onPixels)
        {
            Pumps++;
            _inner.Pump(onComplete, onPixels);
        }
    }

    private sealed class Rig : IDisposable
    {
        public HeadlessPlatformApp App { get; } = new();
        public CountingDecoder Decoder { get; } = new();
        public ImageCache Cache { get; }
        public HeadlessWindow ParentWindow { get; }
        public HeadlessWindow ChildWindow { get; }
        public AppHost Parent { get; }
        public AppHost Child { get; }

        public Rig(Component childRoot)
        {
            Cache = new ImageCache(Decoder);
            var strings = new StringTable();
            var device = new HeadlessGpuDevice();
            ParentWindow = new HeadlessWindow(new WindowDesc("main", new Size2(320, 240), 1f));
            ParentWindow.Show();
            Parent = new AppHost(App, ParentWindow, device, new HeadlessFontSystem(strings), strings, new EmptyRoot(), images: Cache);
            Parent.RunFrame();
            ChildWindow = new HeadlessWindow(new WindowDesc("pop-out", new Size2(320, 180), 1f, Composited: true, CustomFrame: true));
            ChildWindow.Show();
            Child = new AppHost(App, ChildWindow, device, new HeadlessFontSystem(strings), strings, childRoot,
                images: Cache, frameTime: null, compositeSwapchain: true, isDetachedChild: true, parentRenderThread: null);
            Parent.AdoptDetachedChild(Child);
        }

        public void Dispose() { Parent.Dispose(); App.Dispose(); }
    }

    // ── F108: one host pumps the shared cache ───────────────────────────────────────────────────────────────────────

    [Fact]
    public void ThePrimaryPumpsTheSharedCache_AndAPopOutDoesNot()
    {
        using var rig = new Rig(new EmptyRoot());
        for (int i = 0; i < 6; i++)
        {
            int before = rig.Decoder.Pumps;
            rig.Parent.RunFrame();
            int afterParent = rig.Decoder.Pumps;
            rig.Parent.TickDetachedHosts();
            int afterChild = rig.Decoder.Pumps;

            Assert.True(afterParent > before);       // the primary drains the cache it owns on every loop iteration
            Assert.Equal(afterParent, afterChild);   // ... and the pop-out's turn in the same iteration drains nothing more
        }
    }

    [Fact]
    public void AParkedPrimary_LeavesThePumpToThePopOut_SoItsArtworkStillDecodes()
    {
        using var rig = new Rig(new EmptyRoot());
        rig.ParentWindow.State = WindowState.Minimized;
        for (int i = 0; i < 4; i++)
        {
            rig.Parent.RunFrame();                    // parked: returns before any pump
            int afterParent = rig.Decoder.Pumps;
            rig.Parent.TickDetachedHosts();
            Assert.True(rig.Decoder.Pumps > afterParent);   // nobody else will drain it, so the pop-out does
        }

        rig.ParentWindow.State = WindowState.Normal;   // restored: the primary owns the cache again
        rig.Parent.RunFrame();
        int restored = rig.Decoder.Pumps;
        rig.Parent.TickDetachedHosts();
        Assert.Equal(restored, rig.Decoder.Pumps);
    }

    [Fact]
    public void APopOutWithItsOwnCache_StillPumpsIt()
    {
        // A child that does NOT share its parent's cache (nothing else would ever drain it) keeps pumping.
        var strings = new StringTable();
        using var app = new HeadlessPlatformApp();
        var device = new HeadlessGpuDevice();
        var pw = new HeadlessWindow(new WindowDesc("main", new Size2(320, 240), 1f));
        pw.Show();
        using var parent = new AppHost(app, pw, device, new HeadlessFontSystem(strings), strings, new EmptyRoot());
        parent.RunFrame();
        var ownDecoder = new CountingDecoder();
        var cw = new HeadlessWindow(new WindowDesc("pop-out", new Size2(320, 180), 1f, Composited: true, CustomFrame: true));
        cw.Show();
        var child = new AppHost(app, cw, device, new HeadlessFontSystem(strings), strings, new EmptyRoot(),
            images: new ImageCache(ownDecoder), frameTime: null, compositeSwapchain: true, isDetachedChild: true, parentRenderThread: null);
        parent.AdoptDetachedChild(child);

        parent.RunFrame();
        parent.TickDetachedHosts();
        Assert.True(ownDecoder.Pumps > 0);
    }

    [Fact]
    public void ACompletionForThePopOutsOwnImage_WakesThePopOut_WhenThePrimaryPumpsIt()
    {
        using var rig = new Rig(new ArtRoot());
        // Let the pop-out mount and request its artwork (the decode is now queued in the shared decoder).
        for (int i = 0; i < 6; i++) rig.Child.RunFrame();

        rig.Parent.RunFrame();   // the primary's pump applies the decode: ImageStatusChanged fires for the pop-out's image

        int pumpsBeforeChild = rig.Decoder.Pumps;
        rig.Child.RunFrame();
        Assert.Equal(pumpsBeforeChild, rig.Decoder.Pumps);   // the pop-out did not pump to find out
        Assert.True(rig.Child.LastStats.SpansReRecorded > 0); // it was woken by the per-id completion and painted the landing
    }

    [Fact]
    public void ReapingAPopOut_DetachesItsImageRoute_FromTheSharedCache()
    {
        var rig = new Rig(new ArtRoot());
        using (rig)
        {
            for (int i = 0; i < 3; i++) { rig.Parent.RunFrame(); rig.Parent.TickDetachedHosts(); }
            rig.ChildWindow.IsClosed = true;
            rig.Parent.TickDetachedHosts();   // reap

            // A completion after the reap reaches only the primary: the dead child's handler is gone (no throw, no wake of a
            // disposed host, and the shared cache no longer roots the child).
            rig.Cache.Request("art://after-reap", 8, 8);
            rig.Parent.RunFrame();
            rig.Parent.RunFrame();
        }
    }

    // ── F109: a failed child does nothing device-related ────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(false, false, true, true)]    // on screen, healthy, a retained frame for this target: motion runs
    [InlineData(true, false, true, false)]    // parked / occluded
    [InlineData(false, true, true, false)]    // latched RenderFailed: never, however live its animation rows are
    [InlineData(false, false, false, false)]  // no frame valid for the current target
    [InlineData(true, true, false, false)]
    public void RenderMotionMayRun_NeverForAFailedChild(bool paused, bool failed, bool hasCurrentFrame, bool expected)
        => Assert.Equal(expected, AppHost.RenderMotionMayRun(paused, failed, hasCurrentFrame));

    [Fact]
    public void AFailedChildsDrain_ClearsTheRetryStateThatWouldKeepTheSharedLoopTicking()
    {
        using var rig = new Rig(new EmptyRoot());
        // A deferred / refused present left behind by the frame that failed (HasRenderMotion folds both): it must not survive.
        Assert.False(rig.Child.RetryStateAfterFailedDrainForTest(owed: true));
        Assert.False(rig.Child.RetryStateAfterFailedDrainForTest(owed: false));
    }
}
