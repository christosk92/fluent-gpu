using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Hosting;
using FluentGpu.Pal;
using FluentGpu.Pal.Headless;
using FluentGpu.Rhi.Headless;
using FluentGpu.Text.Headless;
using Xunit;

namespace FluentGpu.Engine.Tests.Scout;

/// <summary>A feedback trail (F6) lives only on the primary's composite route, but the device that reports it is shared with every
/// detached pop-out: a pop-out read the device-wide flag as its own and woke at panel rate for as long as the main window's
/// visualizer ran (and through a primary park, where the flag stays latched). Serial: it constructs several hosts.</summary>
[Collection(SerialTestCollection.Name)]
public sealed class PopOutFeedbackLeakTests
{
    private sealed class PaintedRoot : Component
    {
        public override Element Render() => new BoxEl { Grow = 1f, Fill = ColorF.FromRgba(20, 60, 120) };
    }

    [Fact]
    public void ThePrimarysLiveTrail_WakesThePrimary_ButNotAnIdlePopOut()
    {
        var app = new HeadlessPlatformApp();
        var strings = new StringTable();
        var device = new HeadlessGpuDevice();
        var parentWindow = new HeadlessWindow(new WindowDesc("main", new Size2(320, 240), 1f));
        parentWindow.Show();
        var parent = new AppHost(app, parentWindow, device, new HeadlessFontSystem(strings), strings, new PaintedRoot());
        try
        {
            parent.RunFrame();
            var childWindow = new HeadlessWindow(new WindowDesc("pop-out", new Size2(320, 180), 1f, Composited: true, CustomFrame: true));
            childWindow.Show();
            var child = new AppHost(app, childWindow, device, new HeadlessFontSystem(strings), strings, new PaintedRoot(),
                images: null, frameTime: null, compositeSwapchain: true, isDetachedChild: true, parentRenderThread: null);
            parent.AdoptDetachedChild(child);
            for (int i = 0; i < 30 && (i < 2 || parent.HasActiveWork || child.HasActiveWork); i++)
            {
                parent.RunFrame();
                parent.TickDetachedHosts();
            }
            Assert.False(parent.HasActiveWork);
            Assert.False(child.HasActiveWork);

            device.HasLiveFeedback = true;   // the main window's visualizer trail is settling

            Assert.True(parent.HasActiveWork);    // the primary owns the trail: it still owes the frames that advance it
            Assert.False(child.HasActiveWork);    // the pop-out has no trail: woke every tick off the shared flag before the fix
        }
        finally { parent.Dispose(); app.Dispose(); }
    }
}
