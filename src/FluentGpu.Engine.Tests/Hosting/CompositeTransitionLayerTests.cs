using System;
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
/// KeepAliveOptions.CompositeTransitions (the page-mount RCA, root cause 4): a keep-alive page's enter/exit slide and fade
/// are composite parameters. The page root is cut as a translation slice (its translate is the slice's composite offset)
/// with its fade as a group layer on the marker, so the frames of a transition re-record nothing below the page roots,
/// where the multiplied-opacity / baked-translate route re-recorded every span of both pages on every frame. Serial: it
/// builds a host.
/// </summary>
[Collection(SerialTestCollection.Name)]
public sealed class CompositeTransitionLayerTests
{
    private sealed class Page(string label) : Component
    {
        public override Element Render()
        {
            var rows = new Element[40];
            for (int i = 0; i < rows.Length; i++)
                rows[i] = new BoxEl
                {
                    Direction = 0, Height = 8f, Fill = new ColorF(0.2f, 0.3f, 0.4f, 1f),
                    Children = [new TextEl(label + i) { Color = new ColorF(1f, 1f, 1f, 1f) }],
                };
            return new BoxEl { Direction = 1, Width = 300f, Height = 380f, Children = rows };
        }
    }

    private sealed class Root(bool composite) : Component
    {
        public readonly Signal<string> Route = new("a");
        private readonly KeepAliveOptions _options = new(
            TransitionFor: static (_, _) => MotionRecipes.PageSlideBack,
            SuppressLayoutTransitionsOnActivation: true,
            CompositeTransitions: composite);

        public override Element Render() => new BoxEl
        {
            Direction = 1, Width = 640f, Height = 400f,
            Children = [new BoxEl { Grow = 1f, ClipToBounds = true,
                Children = [Flow.KeepAlive(() => Route.Value, static k => k, static k => Embed.Comp(() => new Page(k)), _options)] }],
        };
    }

    private static (int transitionReRecorded, int settledCommands, bool flagged) Run(bool composite)
    {
        ThreadGuard.BindCurrent(ThreadGuard.ThreadRole.Ui);
        using var app = new HeadlessPlatformApp();
        var strings = new StringTable();
        var window = new HeadlessWindow(new WindowDesc("composite-layer", new Size2(640, 400), 1f));
        window.Show();
        var root = new Root(composite);
        using var host = new AppHost(app, window, new HeadlessGpuDevice(), new HeadlessFontSystem(strings), strings, root);
        for (int i = 0; i < 4; i++) host.Paint(0);
        root.Route.Value = "b";
        host.Paint(0);   // the commit frame: the outgoing page starts its exit (and is cut afresh when composited)
        host.Paint(0);
        int reRecorded = 0;
        for (int i = 0; i < 6; i++) reRecorded += host.Paint(0).SpansReRecorded;   // the transition's steady frames
        FrameStats last = default;
        for (int i = 0; i < 90; i++) last = host.Paint(0);
        bool flagged = false;
        for (var c = host.Scene.FirstChild(host.Scene.Root); !c.IsNull; c = host.Scene.NextSibling(c))
            flagged |= Flagged(host.Scene, c);
        return (reRecorded, last.DrawCommandCount, flagged);
    }

    private static bool Flagged(SceneStore scene, NodeHandle n)
    {
        if (scene.Paint(n).CompositeLayer) return true;
        for (var c = scene.FirstChild(n); !c.IsNull; c = scene.NextSibling(c))
            if (Flagged(scene, c)) return true;
        return false;
    }

    [Fact]
    public void A_composited_page_transition_re_records_nothing_below_the_page_roots()
    {
        var baked = Run(composite: false);
        var composited = Run(composite: true);
        Assert.False(baked.flagged);
        Assert.True(composited.flagged, "the keep-alive entry root carries NodePaint.CompositeLayer");
        // Baked: every frame re-records both pages' 80+ rows (their world and opacity move). Composited: the page roots'
        // markers only.
        Assert.True(baked.transitionReRecorded >= 6 * 40, $"baked route re-recorded {baked.transitionReRecorded} spans");
        Assert.True(composited.transitionReRecorded * 8 <= baked.transitionReRecorded,
            $"composited {composited.transitionReRecorded} vs baked {baked.transitionReRecorded} spans over the transition");
        // At rest the composited page records the same commands (its slice holds the stream the inline route held).
        Assert.True(composited.settledCommands > 0 && baked.settledCommands > 0);
    }
}
