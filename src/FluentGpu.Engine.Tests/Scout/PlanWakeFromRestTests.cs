using System.Diagnostics;
using System.Threading;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Hosting;
using FluentGpu.Hosting.Threading;
using FluentGpu.Pal;
using FluentGpu.Pal.Headless;
using FluentGpu.Rhi.Headless;
using FluentGpu.Scene;
using FluentGpu.Text.Headless;
using Xunit;

namespace FluentGpu.Engine.Tests.Scout;

/// <summary>A plan written at rest (the first wheel notch, an animated ScrollTo) is posed by the render thread on the turn its
/// write wakes, without waiting for the UI frame's publication. The wake used to find the render poser's HasActive false (it
/// reports the LAST tick, when every plan had settled) and no fresh publication, so the turn returned without posing.
/// Serial: it constructs a host.</summary>
[Collection(SerialTestCollection.Name)]
public sealed class PlanWakeFromRestTests
{
    private sealed class Root : Component
    {
        public override Element Render() => Ui.ScrollView(new BoxEl { Height = 20_000f, Direction = 1 });
    }

    [Fact]
    public void APlanWriteAtRest_IsPosedByTheRenderThread_BeforeAnyUiFrame()
    {
        ThreadGuard.BindCurrent(ThreadGuard.ThreadRole.Ui);
        var app = new HeadlessPlatformApp();
        var strings = new StringTable();
        var device = new HeadlessGpuDevice();
        var window = new HeadlessWindow(new WindowDesc("main", new Size2(320, 240), 1f));
        window.Show();
        var host = new AppHost(app, window, device, new HeadlessFontSystem(strings), strings, new Root());
        try
        {
            RenderThread thread = host.InstallRenderThreadForTest();
            host.RunFrame();
            // Settle the UI: the scrollbar chrome's reveal/hide timeline runs ~2.5 s of frame clock after mount.
            for (int i = 0, quiet = 0; quiet < 5; i++)
            {
                Assert.True(i < 600, "the host never stopped publishing");
                ulong before = host.ScenePublishSeqForTest;
                host.WakeFrameForTest();
                host.RunFrame();
                quiet = host.ScenePublishSeqForTest == before ? quiet + 1 : 0;
            }
            // ... and the render loop: no composite for 300 ms (every plan settled, no render motion).
            var clock = Stopwatch.StartNew();
            int composites = device.CompositeFrameCount;
            long quietSince = 0;
            while (clock.ElapsedMilliseconds - quietSince < 300)
            {
                Assert.True(clock.ElapsedMilliseconds < 5000, "the render loop never went quiet");
                Thread.Sleep(10);
                int now = device.CompositeFrameCount;
                if (now != composites) { composites = now; quietSince = clock.ElapsedMilliseconds; }
            }

            var scene = host.Scene;
            NodeHandle vp = default;
            for (int i = 0; i < scene.Capacity && vp.IsNull; i++)
            {
                var h = scene.HandleAt(i);
                if (!h.IsNull && scene.IsLive(h) && scene.HasScroll(h)) vp = h;
            }
            Assert.False(vp.IsNull);
            ulong seq = host.ScenePublishSeqForTest;

            // The plan write, then the render wake OnPlanWritten makes on a windowed (async) host. No UI frame runs.
            host.TryGetScrollHandle(vp)!.ScrollTo(4_000.0);
            thread.WakeAsync();

            clock.Restart();
            while (device.CompositeFrameCount == composites)
            {
                Assert.True(clock.ElapsedMilliseconds < 2000,
                    "the render thread did not pose the plan written at rest: its first pixel waits for the UI frame");
                Thread.Sleep(5);
            }
            Assert.Equal(seq, host.ScenePublishSeqForTest);   // a render-side pose of the retained publication, not a new one
        }
        finally
        {
            host.Dispose();
            app.Dispose();
        }
    }
}
