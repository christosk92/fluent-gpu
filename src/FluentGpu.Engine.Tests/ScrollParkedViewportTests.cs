using System;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Hosting;
using FluentGpu.Pal;
using FluentGpu.Pal.Headless;
using FluentGpu.Rhi.Headless;
using FluentGpu.Scene;
using FluentGpu.Scroll.Runtime;
using FluentGpu.Text.Headless;
using Xunit;

namespace FluentGpu.Engine.Tests;

/// <summary>
/// A PARKED viewport (a KeepAlive page that is not showing) is not on screen: the UI frame step must not evaluate it —
/// exactly as the coverage fill (what the render poser sees) already skips it. Evaluating it anyway costs a plan
/// evaluation + virtualization window per parked scroller per frame, and worse, an unsettled plan on a parked page
/// counts as live scroll motion, holding the frame loop awake (ScrollAnim) for a list nobody can see.
/// </summary>
[Collection(SerialTestCollection.Name)]
public sealed class ScrollParkedViewportTests
{
    private sealed class Root : Component
    {
        public override Element Render()
            => Ui.ScrollView(new BoxEl { Height = 20_000f, Direction = 1 });
    }

    [Fact]
    public void AParkedViewportWithALivePlanIsNotEvaluatedByTheFrameStep()
    {
        var strings = new StringTable();
        var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("parked-scroll", new Size2(320, 240), 1f));
        window.Show();
        var host = new AppHost(app, window, new HeadlessGpuDevice(), new HeadlessFontSystem(strings), strings, new Root());
        try
        {
            for (int i = 0; i < 3; i++) host.RunFrame();
            NodeHandle vp = default;
            var scene = host.Scene;
            for (int i = 0; i < scene.Capacity && vp.IsNull; i++)
            {
                var h = scene.HandleAt(i);
                if (!h.IsNull && scene.IsLive(h) && scene.HasScroll(h)) vp = h;
            }
            Assert.False(vp.IsNull);
            var handle = host.TryGetScrollHandle(vp);
            Assert.NotNull(handle);

            handle!.ScrollTo(8_000.0);                  // a live glide…
            scene.Mark(vp, NodeFlags.Parked);          // …on a page that has just been parked
            host.RunFrame();

            Assert.Equal(0, host.ScrollActiveCensus);  // no unsettled motion counted for an off-screen viewport
            Assert.Equal(0.0, scene.ScrollRef(vp).Offset);   // and the parked scroller's offset was not stepped
        }
        finally { host.Dispose(); }
    }
}
