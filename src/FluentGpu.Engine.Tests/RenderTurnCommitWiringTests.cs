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
/// The render turn's composition commits, pinned through the REAL <c>AppHost</c> callbacks: a headless primary host is given
/// the force-sync render loop a windowed host would have (<c>AppHost.InstallRenderThreadForTest</c>, built by the same method
/// as the constructor's), so one <c>RunFrame</c> is one publish and one turn on the fgpu-render thread — pre-turn, the
/// children's drain, the present decision, the post-turn commit — against the headless device, which counts the commits and
/// hands out a recording video presenter. Serial: it constructs several hosts (process-static seams).
/// <list type="bullet">
/// <item>The wiring (review fix 2): the loop the host builds drains its children BEFORE the present decision and commits the
/// composition AFTER it (<c>postTurn: CommitVideoTurnAfterPresent</c>); a headless window never goes async on its own, which is
/// how dropping that argument used to fail nothing.</item>
/// <item>F080 after the F241 reorder (review fix 1): a detached child's placement change that is NOT a move (here a
/// visibility change; a first Place, a viewport, clip, radius or z change are the same path) is applied with the device commit
/// deferred, and that commit runs right after the CHILD's own present, inside the drain — before the parent's slot wait, which
/// can cross a vblank — not at the end of the turn.</item>
/// </list>
/// </summary>
[Collection(SerialTestCollection.Name)]
public sealed class RenderTurnCommitWiringTests
{
    /// <summary>Something to paint, so every frame has a non-empty draw list and presents; its width is a signal so a test
    /// can make the next RunFrame a REAL publication (a placement change alone rides a video-only post, applied in the pre-turn).</summary>
    private sealed class PaintedRoot : Component
    {
        public readonly Signal<float> W = new(200f);
        /// <summary>A video registry token: when set (and W bumped so this re-renders), the root punches a video hole for it,
        /// which is what lets the host PLACE the surface (placement follows the frame's posed hole, F070).</summary>
        public int Token;
        public override Element Render() => new BoxEl
        {
            Width = W.Value, Height = 100f, Fill = ColorF.FromRgba(20, 60, 120),
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
        public readonly AppHost Host;
        public readonly RenderThread Thread;

        public Rig()
        {
            ThreadGuard.BindCurrent(ThreadGuard.ThreadRole.Ui);
            Window = new HeadlessWindow(new WindowDesc("main", new Size2(320, 240), 1f));
            Window.Show();
            Host = new AppHost(App, Window, Device, new HeadlessFontSystem(Strings), Strings, new PaintedRoot());
            Thread = Host.InstallRenderThreadForTest();
        }

        public AppHost NewChild(PaintedRoot root)
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
    public void TheHostsRenderLoop_DrainsChildrenBeforeThePresentDecision_AndCommitsAfterThePresent()
    {
        using var rig = new Rig();
        Assert.True(rig.Thread.HasExtraDrainForTest, "the children's drain is wired (extraDrain: DrainChildRenderSources)");
        Assert.True(rig.Thread.HasPostTurnForTest, "the post-present commit is wired (postTurn: CommitVideoTurnAfterPresent)");

        var commitsAtPresents = new List<int>();
        rig.Device.OnCommitVideoComposition = () => commitsAtPresents.Add(rig.Device.PrimarySwapchain!.PresentCount);
        rig.Host.RunFrame();   // publish + one force-sync turn on the render thread
        int presents = rig.Device.PrimarySwapchain!.PresentCount;
        Assert.True(presents >= 1, $"the frame presented (presents={presents})");
        Assert.NotEmpty(commitsAtPresents);
        // The LAST commit of the turn saw this frame's present already counted: it ran after the present decision.
        Assert.Equal(presents, commitsAtPresents[^1]);
    }

    [Fact]
    public void AChildsDeferredPlacementChange_CommitsRightAfterTheChildsPresent_NotAtTheEndOfTheTurn()
    {
        using var rig = new Rig();
        var presenter = new RecordingPresenter();
        rig.Device.VideoPresenterForTest = presenter;
        rig.Host.RunFrame();
        var root = new PaintedRoot();
        var child = rig.NewChild(root);
        child.RunFrame();                                   // the child's first frame presents through the parent's turn
        var childSwapchain = rig.Device.CreatedSwapchains[^1];
        Assert.True(childSwapchain.PresentCount >= 1, "the child presented on its own swapchain");

        // Each commit is tagged with whether the child still owed an apply, the child's present count, and WHICH host phase made
        // it (the render thread's stack: the children's drain, the structural pre-turn or the post-present commit).
        var events = new List<string>();
        rig.Device.OnCommitVideoComposition = () =>
        {
            string stack = Environment.StackTrace;
            string phase = stack.Contains("DrainChildRenderSources") ? "childDrain"
                : stack.Contains("DrainVideoStructuralPreTurn") ? "preTurn"
                : stack.Contains("CommitVideoTurnAfterPresent") ? "postTurn" : "other";
            events.Add((child.HasUncommittedVideoApplyForTest ? "applied" : "clean") + "@child" + childSwapchain.PresentCount + ":" + phase);
        };

        // A surface for the child: create + bind are structural (the pre-turn applies and commits them); the FIRST Place rides the
        // child's present with the device commit deferred. It is not a move (nothing was placed before), so the Stage-B commit inside
        // the child's own drain does not cover it — this is exactly the deferred, non-move placement work F080 is about.
        var reg = child.VideoSurfacesForTest;
        int token = reg.Acquire();
        root.Token = token;
        root.W.Value = 199f;                                // re-render: the root now punches a hole for the token (a real publication)
        reg.Bind(token, 0x1234);
        reg.Place(token, new RectF(10f, 20f, 100f, 50f));
        int childPresents0 = childSwapchain.PresentCount;
        child.RunFrame();                                   // one turn: pre-turn (create + bind), child drain (present + Place + commit), parent decision, post-turn
        string all = string.Join(", ", events);
        Assert.Equal(1, presenter.Count("Bind"));
        Assert.True(presenter.Count("Place") >= 1, $"the surface was placed (calls: {string.Join(",", presenter.Calls)}; commits: {all})");
        Assert.True(childSwapchain.PresentCount > childPresents0, "the child presented this turn");
        Assert.False(child.HasUncommittedVideoApplyForTest, "nothing is owed after the turn");
        // The commit that carried the first placement ran in the children's drain, right after the child's flip (its present already
        // counted) — never the post-present commit, which sits behind the parent's slot wait and found nothing owed for the child.
        Assert.Contains("applied@child" + childSwapchain.PresentCount + ":childDrain", events);
        Assert.DoesNotContain(events, e => e.StartsWith("applied@", StringComparison.Ordinal) && e.EndsWith(":postTurn", StringComparison.Ordinal));
        Assert.Contains("clean@child" + childSwapchain.PresentCount + ":postTurn", events);
    }
}
