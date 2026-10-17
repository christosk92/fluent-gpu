using System;
using System.Collections.Generic;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Hosting;
using FluentGpu.Hosting.Threading;
using FluentGpu.Media;
using FluentGpu.Pal;
using FluentGpu.Pal.Headless;
using FluentGpu.Rhi.Headless;
using FluentGpu.Signals;
using FluentGpu.Text.Headless;
using Xunit;

namespace FluentGpu.Engine.Tests;

/// <summary>
/// E2 on the render-thread path (the VerticalSlice <c>frame-freeze</c> suite covers the single-thread one): a headless primary host
/// is given the force-sync render loop a windowed host has (<c>InstallRenderThreadForTest</c>), so one frame is one publication
/// and one turn on the fgpu-render thread. The freeze directive rides the publication, so the same capture / hold / dissolve /
/// release must come out of the render turn; video releases wait for the thaw frame; a pop-out freezes and thaws with its parent.
/// Serial: it constructs several hosts.
/// </summary>
[Collection(SerialTestCollection.Name)]
public sealed class FrameFreezeTests
{
    private static readonly ColorF OldFill = ColorF.FromRgba(200, 30, 30);
    private static readonly ColorF NewFill = ColorF.FromRgba(30, 30, 200);

    private sealed class ColorRoot : Component
    {
        public readonly Signal<bool> Alt = new(false);
        public readonly Signal<float> W = new(200f);
        /// <summary>A video registry token: when set (and a signal bumped) the root punches a hole for it.</summary>
        public int Token;

        public override Element Render() => new BoxEl
        {
            Width = W.Value, Height = 100f, Fill = Alt.Value ? NewFill : OldFill,
            Children = Token > 0 ? [new BoxEl { Width = 80f, Height = 45f, VideoHole = true, VideoSurfaceId = Token }] : [],
        };
    }

    private sealed class RecordingPresenter : IVideoPresenter
    {
        public readonly List<string> Calls = new();
        private uint _next = 1;
        public VideoSurfaceId CreateSurface() { Calls.Add("Create"); return new VideoSurfaceId(_next++); }
        public bool BindSurfaceHandle(VideoSurfaceId id, nuint dcompSurfaceHandle) { Calls.Add("Bind"); return true; }
        public void Place(VideoSurfaceId id, RectF deviceRect, float opacity, int z) => Calls.Add("Place");
        public void SetVisible(VideoSurfaceId id, bool visible) => Calls.Add("Visible");
        public void Destroy(VideoSurfaceId id) => Calls.Add("Destroy");
        public void Commit() => Calls.Add("Commit");
        public int Count(string name) { int n = 0; foreach (var c in Calls) if (c == name) n++; return n; }
    }

    private sealed class Rig : IDisposable
    {
        public readonly HeadlessPlatformApp App = new();
        public readonly StringTable Strings = new();
        public readonly HeadlessGpuDevice Device = new();
        public readonly HeadlessWindow Window;
        public readonly ColorRoot Root = new();
        public readonly AppHost Host;
        public readonly RenderThread Thread;
        public readonly HeadlessSwapchain Sc;

        public Rig()
        {
            ThreadGuard.BindCurrent(ThreadGuard.ThreadRole.Ui);
            Window = new HeadlessWindow(new WindowDesc("main", new Size2(320, 240), 1f));
            Window.Show();
            Host = new AppHost(App, Window, Device, new HeadlessFontSystem(Strings), Strings, Root);
            Thread = Host.InstallRenderThreadForTest();
            Sc = Device.PrimarySwapchain!;
            Paint(3);
        }

        public void Paint(int n = 1) { for (int i = 0; i < n; i++) Host.Paint(0); }

        public AppHost NewChild(ColorRoot root)
        {
            var window = new HeadlessWindow(new WindowDesc("pop-out", new Size2(320, 180), 1f, Composited: true, CustomFrame: true));
            window.Show();
            var child = new AppHost(App, window, Device, new HeadlessFontSystem(Strings), Strings, root,
                images: null, frameTime: null, compositeSwapchain: true, isDetachedChild: true, parentRenderThread: Thread);
            Host.AdoptDetachedChild(child);
            Host.AttachChildRenderSourceForTest(child);
            return child;
        }

        public void Dispose() { Host.Dispose(); App.Dispose(); }
    }

    [Fact]
    public void OnTheRenderThread_TheCaptureIsPresented_TheFollowingFramesAreHeld_AndTheThawDissolvesFromTheSnapshot()
    {
        using var rig = new Rig();
        var sc = rig.Sc;
        sc.RecordPresents = true;
        var old = rig.Device.SummarizeLastFrame();

        var token = rig.Host.Freeze();
        rig.Paint();                                                    // the capture frame
        Assert.True(token.IsCaptured);
        Assert.Equal(1, sc.LiveSnapshots);
        Assert.Equal(HeadlessPresentKind.Normal, sc.PresentLog[^1].Kind);
        Assert.Equal(old.Hash, sc.PresentLog[^1].Shown.Hash);

        int presents = sc.PresentCount;
        int logAtCapture = sc.PresentLog.Count;
        rig.Root.Alt.Value = true;
        rig.Paint(4);
        Assert.Equal(presents, sc.PresentCount);                        // nothing reached the glass
        for (int i = logAtCapture; i < sc.PresentLog.Count; i++) Assert.Equal(HeadlessPresentKind.Held, sc.PresentLog[i].Kind);
        var rebuilt = rig.Device.SummarizeLastFrame();
        Assert.NotEqual(old.Hash, rebuilt.Hash);                        // yet the new tree WAS recorded and submitted

        int logAtThaw = sc.PresentLog.Count;
        rig.Host.Thaw(token, new DissolveSpec(80f));
        for (int i = 0; i < 40 && rig.Host.FreezeState != FreezePhase.None; i++) rig.Paint();
        Assert.Equal(FreezePhase.None, rig.Host.FreezeState);
        Assert.Equal(FreezeEnd.Thawed, token.End);

        var thaw = sc.PresentLog.GetRange(logAtThaw, sc.PresentLog.Count - logAtThaw);
        Assert.Equal(HeadlessPresentKind.Dissolve, thaw[0].Kind);
        int dissolves = 0;
        foreach (var r in thaw)
        {
            if (r.Kind != HeadlessPresentKind.Dissolve) continue;
            dissolves++;
            Assert.Equal(old.Hash, r.From.Hash);
            Assert.Equal(rebuilt.Hash, r.Shown.Hash);
            Assert.InRange(r.T, 0.0001f, 0.9999f);
        }
        Assert.True(dissolves >= 3, $"dissolve frames: {dissolves}");
        Assert.Equal(HeadlessPresentKind.Normal, thaw[^1].Kind);
        Assert.Equal(rebuilt.Hash, thaw[^1].Shown.Hash);
        Assert.Equal(0, sc.LiveSnapshots);
    }

    [Fact]
    public void VideoReleases_AreDeferredWhileFrozen_AndAppliedByTheThawFrame()
    {
        // Control: with no freeze the release is applied by the next presented turn.
        int unfrozenDestroys;
        using (var rig = new Rig())
        {
            var presenter = new RecordingPresenter();
            rig.Device.VideoPresenterForTest = presenter;
            int video = PlaceVideo(rig, presenter);
            ReleaseVideo(rig, video);
            rig.Paint(2);
            unfrozenDestroys = presenter.Count("Destroy");
        }
        Assert.Equal(1, unfrozenDestroys);

        using (var rig = new Rig())
        {
            var presenter = new RecordingPresenter();
            rig.Device.VideoPresenterForTest = presenter;
            int video = PlaceVideo(rig, presenter);
            var token = rig.Host.Freeze();
            rig.Paint();                                                // the capture frame: video still applied normally
            ReleaseVideo(rig, video);
            rig.Paint(3);                                               // held frames
            Assert.Equal(0, presenter.Count("Destroy"));                // the release waits for the thaw
            rig.Host.Thaw(token, DissolveSpec.Cut);
            rig.Paint(2);
            Assert.Equal(1, presenter.Count("Destroy"));                // ... and the thaw frame applies it
        }
    }

    private static int PlaceVideo(Rig rig, RecordingPresenter presenter)
    {
        var reg = rig.Host.VideoSurfacesForTest;
        int token = reg.Acquire();
        rig.Root.Token = token;
        rig.Root.W.Value = 199f;                                        // re-render: the root now punches a hole for the token
        reg.Bind(token, 0x1234);
        reg.Place(token, new RectF(10f, 20f, 80f, 45f));
        rig.Paint(3);
        Assert.True(presenter.Count("Place") >= 1, $"the surface was placed (calls: {string.Join(",", presenter.Calls)})");
        return token;
    }

    private static void ReleaseVideo(Rig rig, int token)
    {
        rig.Host.VideoSurfacesForTest.Release(token);
        rig.Root.Token = 0;
        rig.Root.W.Value = 198f;                                        // a publication that no longer carries the hole
    }

    [Fact]
    public void APopOut_FreezesAndThawsWithItsParent()
    {
        using var rig = new Rig();
        var childRoot = new ColorRoot();
        var child = rig.NewChild(childRoot);
        child.RunFrame();
        var childSc = rig.Device.CreatedSwapchains[^1];
        childSc.RecordPresents = true;
        Assert.True(childSc.PresentCount >= 1);

        var token = rig.Host.Freeze();
        Assert.Equal(FreezePhase.Frozen, child.FreezeState);
        Assert.True(child.IsSnapping);
        rig.Paint();
        childRoot.W.Value = 201f;                                       // a publication for the child (it owes its capture frame)
        child.RunFrame();
        Assert.Equal(1, childSc.LiveSnapshots);                         // the child captured ITS outgoing frame

        childRoot.Alt.Value = true;
        rig.Root.Alt.Value = true;
        int held = childSc.HeldPresentCount;
        child.RunFrame();
        rig.Paint();
        Assert.True(childSc.HeldPresentCount > held, "the pop-out's present is held too");

        rig.Host.Thaw(token, new DissolveSpec(64f));
        for (int i = 0; i < 40 && (rig.Host.FreezeState != FreezePhase.None || child.FreezeState != FreezePhase.None); i++)
        {
            rig.Paint();
            child.RunFrame();
        }
        Assert.Equal(FreezePhase.None, rig.Host.FreezeState);
        Assert.Equal(FreezePhase.None, child.FreezeState);
        Assert.True(childSc.DissolvedPresentCount >= 2, $"the pop-out dissolved ({childSc.DissolvedPresentCount})");
        Assert.Equal(0, childSc.LiveSnapshots);
        Assert.Equal(0, rig.Sc.LiveSnapshots);
        Assert.False(child.IsSnapping);
    }

    [Fact]
    public void ARefusedFreeze_OfAnAlreadyFrozenHost_Throws_AndAThawOfAnotherHostsTokenIsRejected()
    {
        using var rig = new Rig();
        var token = rig.Host.Freeze();
        Assert.Throws<InvalidOperationException>(() => rig.Host.Freeze());
        using var other = new Rig();
        Assert.Throws<ArgumentException>(() => other.Host.Thaw(token));
        rig.Host.Thaw(token, DissolveSpec.Cut);
        rig.Paint(2);
        Assert.Equal(FreezePhase.None, rig.Host.FreezeState);
    }
}
