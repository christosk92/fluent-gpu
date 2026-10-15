using System;
using System.Collections.Generic;
using FluentGpu.Animation;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Hosting;
using FluentGpu.Hosting.Threading;
using FluentGpu.Pal;
using FluentGpu.Pal.Headless;
using FluentGpu.Rhi.Headless;
using FluentGpu.Scene;
using FluentGpu.Signals;
using FluentGpu.Text.Headless;
using Xunit;

namespace FluentGpu.Engine.Tests;

/// <summary>
/// EP20 / E21 (Classic tuning F11): an entrance (Enter, Stagger, a flow reveal: every pending-start row) starts counting at the
/// first frame in which its node is SHOWN, not at mount. A node mounted under held or hidden frames for n frames is at t = 0 in its
/// first shown frame and then follows exactly the timeline a node mounted visible follows from its mount; a node mounted visible
/// keeps today's timeline. Covered on the UI scheduler, on the render-thread compositor, and end to end on a host whose main
/// window waits behind its reveal gate (EP8). Serial: the host case constructs hosts.
/// </summary>
[Collection(SerialTestCollection.Name)]
public sealed class EntranceFirstFrameTests
{
    private static readonly LayoutTransition Fade200 = new(TransitionChannels.Opacity,
        TransitionDynamics.Tween(200f, Easing.Linear), Enter: new EnterExit(Opacity: 0f, Active: true));

    private static (SceneStore Scene, NodeHandle Node, AnimEngine Anim) Engine(bool renderOwns = false)
    {
        ThreadGuard.BindCurrent(ThreadGuard.ThreadRole.Ui);
        var scene = new SceneStore();
        var node = scene.CreateNode(1);
        scene.Root = node;
        scene.Bounds(node) = new(0, 0, 100, 100);
        return (scene, node, new AnimEngine(scene) { RenderOwnsCompositor = renderOwns });
    }

    /// <summary>Opacity per frame of an entrance seeded on frame 0 with <paramref name="heldFrames"/> unshown frames first.</summary>
    private static List<float> UiTimeline(int heldFrames, int shownFrames, float stepMs = 16f)
    {
        var (scene, node, anim) = Engine();
        anim.SeedEnter(node, Fade200.Enter, Fade200);
        var shown = new List<float>();
        for (int i = 0; i < heldFrames; i++)
        {
            anim.HoldPendingStarts = true;
            anim.Tick(stepMs);
            Assert.Equal(0f, scene.Paint(node).Opacity, 4);   // nothing moves while nobody can see it
        }
        for (int i = 0; i < shownFrames; i++)
        {
            anim.HoldPendingStarts = false;
            anim.Tick(stepMs);
            shown.Add(scene.Paint(node).Opacity);
        }
        return shown;
    }

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(30)]
    public void UiScheduler_HeldFrames_StartTheEntranceAtTheFirstShownFrame(int held)
    {
        List<float> visible = UiTimeline(0, 8);
        List<float> late = UiTimeline(held, 8);
        Assert.Equal(0f, late[0], 4);                  // the first shown frame poses t = 0
        for (int i = 0; i < visible.Count; i++)        // and the rest is today's timeline exactly
            Assert.Equal(visible[i], late[i], 4);
    }

    [Fact]
    public void UiScheduler_AVisibleMount_KeepsTodaysTimeline()
    {
        var (scene, node, anim) = Engine();
        anim.SeedEnter(node, Fade200.Enter, Fade200);
        anim.Tick(16f);
        Assert.Equal(0f, scene.Paint(node).Opacity, 4);                // the seed frame holds t = 0
        anim.Tick(16f);
        Assert.Equal(16f / 200f, scene.Paint(node).Opacity, 3);        // then one step per frame
        anim.Tick(16f);
        Assert.Equal(32f / 200f, scene.Paint(node).Opacity, 3);
    }

    [Fact]
    public void UiScheduler_AStaggerDelay_IsNotSpentWhileHeld()
    {
        var (scene, node, anim) = Engine();
        anim.SeedEnter(node, Fade200.Enter, MotionTok.Get(MotionTokenId.StandardEnter), delayMs: 48f);
        anim.HoldPendingStarts = true;
        for (int i = 0; i < 10; i++) anim.Tick(16f);                   // 160 ms of unshown frames: the delay is untouched
        anim.HoldPendingStarts = false;
        anim.Tick(16f);                                                // first shown frame: t = 0
        anim.Tick(16f);
        anim.Tick(16f);
        Assert.Equal(0f, scene.Paint(node).Opacity, 4);                // 32 ms of the 48 ms delay spent: still waiting
    }

    [Fact]
    public void RenderCompositor_HeldTurns_StartTheEntranceAtTheFirstShownTurn()
    {
        var (scene, node, anim) = Engine(renderOwns: true);
        anim.SeedEnter(node, Fade200.Enter, Fade200);
        var desired = new CompositorAnimationSnapshot();
        anim.CaptureCompositorAnimations(desired, 0);
        var snapshot = new SceneRecordingSnapshot();
        snapshot.Capture(scene);
        var renderer = new RenderCompositorAnimations { HoldPendingStarts = true };

        renderer.Adopt(desired, snapshot, 0);
        for (int i = 1; i <= 6; i++)
        {
            renderer.Tick(snapshot, i * 16.0);
            Assert.Equal(0f, snapshot.Paint(node).Opacity, 4);
        }
        renderer.HoldPendingStarts = false;
        renderer.Tick(snapshot, 7 * 16.0);                             // the first shown turn holds t = 0 …
        Assert.Equal(0f, snapshot.Paint(node).Opacity, 4);
        renderer.Tick(snapshot, 8 * 16.0);                             // … and the start resolves from it
        Assert.Equal(16f / 200f, snapshot.Paint(node).Opacity, 3);
    }

    // ── end to end: the main window's reveal gate ────────────────────────────────────────────────────────────────────

    private const float EnterW = 113f;

    private sealed class EnterRoot : Component
    {
        public readonly Signal<bool> Show = new(false);
        public override Element Render()
        {
            Element[] kids = Show.Value
                ? [new BoxEl { Width = EnterW, Height = 20f, Fill = ColorF.FromRgba(30, 30, 200), Enter = new EnterExit(Opacity: 0f, Active: true) }]
                : [];
            return new BoxEl { Direction = 1, Width = 300f, Height = 200f, Children = kids };
        }
    }

    private static NodeHandle Find(SceneStore scene, NodeHandle n, float width)
    {
        if (n.IsNull) return n;
        if (scene.Layout(n).Width == width) return n;
        for (var c = scene.FirstChild(n); !c.IsNull; c = scene.NextSibling(c))
        {
            var r = Find(scene, c, width);
            if (!r.IsNull) return r;
        }
        return default;
    }

    /// <summary>Opacity of the entering node on every frame from its first SHOWN frame on.</summary>
    private static List<float> HostTimeline(int hiddenFramesAfterMount, int shownFrames)
    {
        ThreadGuard.BindCurrent(ThreadGuard.ThreadRole.Ui);
        var strings = new StringTable();
        var app = new HeadlessPlatformApp();
        bool gated = hiddenFramesAfterMount > 0;
        var window = new HeadlessWindow(new WindowDesc("entrance-first-frame", new Size2(320, 240), 1f, Composited: true)) { IsVisible = !gated };
        var root = new EnterRoot();
        var host = new AppHost(app, window, new HeadlessGpuDevice(), new HeadlessFontSystem(strings), strings, root);
        try
        {
            bool ready = !gated;
            if (gated) host.BeginRevealGate(new RevealGateOptions { FirstFrameReady = () => ready, TimeoutMs = 60_000 });
            for (int i = 0; i < 3; i++) host.RunFrame();
            root.Show.Value = true;                                    // mounts with its Enter …
            for (int i = 0; i < hiddenFramesAfterMount; i++)           // … under frames the hidden window never shows
            {
                host.RunFrame();
                Assert.Equal(0, window.ShowCalls);
            }
            ready = true;
            var shown = new List<float>();
            NodeHandle node = default;
            while (shown.Count < shownFrames)
            {
                host.RunFrame();
                if (gated && window.ShowCalls == 0) continue;          // not revealed yet: not a shown frame
                if (node.IsNull) node = Find(host.Scene, host.Scene.Root, EnterW);
                Assert.False(node.IsNull);
                shown.Add(host.Scene.Paint(node).Opacity);
            }
            return shown;
        }
        finally { host.Dispose(); app.Dispose(); }
    }

    [Fact]
    public void Host_AnEntranceMountedBehindTheRevealGate_StartsOnTheRevealedFrame()
    {
        List<float> visible = HostTimeline(0, 6);
        List<float> gated = HostTimeline(8, 6);
        Assert.Equal(0f, visible[0], 4);                               // today: the mount frame poses t = 0 …
        Assert.True(visible[1] > 0f);                                  // … and the next one moves
        Assert.Equal(0f, gated[0], 4);                                 // the revealed frame poses t = 0, after 8 hidden frames
        for (int i = 0; i < visible.Count; i++)
            Assert.Equal(visible[i], gated[i], 4);                     // the same choreography from there
    }
}
