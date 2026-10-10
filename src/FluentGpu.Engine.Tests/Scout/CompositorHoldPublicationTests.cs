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
using FluentGpu.Text.Headless;
using Xunit;

namespace FluentGpu.Engine.Tests.Scout;

/// <summary>
/// <see cref="AnimEngine.SetHeld"/> / <see cref="AnimEngine.SetPaused"/> rewrite only a render-owned row's flags: no paint,
/// no reconcile, no seed revision. While the row is held the render thread poses the same value every turn, so its feedback
/// writes nothing either, and the release reaches the renderer only through a publication. A release made off a reconcile
/// (a timer, an effect on an off-thread signal) must therefore still get through the no-op publication skip, or the row
/// stays frozen on screen. Serial: it constructs a host with a force-sync render thread.
/// </summary>
[Collection(SerialTestCollection.Name)]
public sealed class CompositorHoldPublicationTests
{
    private sealed class Root : Component
    {
        public override Element Render() => new BoxEl { Width = 200f, Height = 100f, Fill = ColorF.FromRgba(20, 60, 120) };
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TheReleaseOfAHeldOrPausedRenderOwnedRow_Publishes(bool paused)
    {
        ThreadGuard.BindCurrent(ThreadGuard.ThreadRole.Ui);
        var app = new HeadlessPlatformApp();
        var strings = new StringTable();
        var window = new HeadlessWindow(new WindowDesc("main", new Size2(320, 240), 1f));
        window.Show();
        var host = new AppHost(app, window, new HeadlessGpuDevice(), new HeadlessFontSystem(strings), strings, new Root());
        try
        {
            host.InstallRenderThreadForTest();
            host.RunFrame();
            ulong WakeAndRun() { host.WakeFrameForTest(); host.RunFrame(); return host.ScenePublishSeqForTest; }

            var node = host.Scene.Root;
            var anim = host.Animation;
            anim.Animate(node, AnimChannel.Opacity, 1f, 0.2f, 60_000f);   // a long render-owned row: an ambient loop stand-in
            for (int i = 0; i < 3; i++) WakeAndRun();
            if (paused) anim.SetPaused(node, AnimChannel.Opacity, true); else anim.SetHeld(node, AnimChannel.Opacity, true);

            // Held, the row poses one value: bare wakes settle to publishing nothing.
            bool settled = false;
            for (int i = 0; i < 16 && !settled; i++) { ulong before = host.ScenePublishSeqForTest; settled = WakeAndRun() == before; }
            Assert.True(settled, "the held row never settled:" + host.NoopBlockedCensusForTest());

            ulong seq = host.ScenePublishSeqForTest;
            if (paused) anim.SetPaused(node, AnimChannel.Opacity, false); else anim.SetHeld(node, AnimChannel.Opacity, false);
            Assert.True(WakeAndRun() > seq, "the release publishes");
        }
        finally { host.Dispose(); app.Dispose(); }
    }
}
