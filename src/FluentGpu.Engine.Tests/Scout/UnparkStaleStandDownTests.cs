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

/// <summary>A present the render thread makes into a window that is being minimized (or cloaked by a desktop switch) stands
/// down, and nothing presents while the window is parked, so the swapchain still reads occluded when a short park ends. The
/// un-park edge must re-present at once: otherwise the unchanged restore frame is elided, render motion stays paused and
/// <c>WindowOccluded</c> stays true until the occlusion probe.</summary>
[Collection(SerialTestCollection.Name)]
public sealed class UnparkStaleStandDownTests
{
    private sealed class PaintedRoot : Component
    {
        public override Element Render() => new BoxEl { Grow = 1f, Fill = ColorF.FromRgba(20, 60, 120) };
    }

    [Fact]
    public void AShortParkLeftOccludedByAStoodDownPresent_RepresentsOnTheRestoreFrame_NotAfterTheOcclusionProbe()
    {
        using var app = new HeadlessPlatformApp();
        var device = new HeadlessGpuDevice();
        var strings = new StringTable();
        var window = new HeadlessWindow(new WindowDesc("main", new Size2(320, 240), 1f));
        window.Show();
        var host = new AppHost(app, window, device, new HeadlessFontSystem(strings), strings, new PaintedRoot());
        try
        {
            for (int i = 0; i < 4; i++) host.RunFrame();
            var sc = device.CreatedSwapchains[0];

            window.State = WindowState.Minimized;
            host.RunFrame();
            Assert.True(host.IsParked);
            // The present that raced the minimize stood down. A real backend clears that only inside its next present, and a
            // parked window presents nothing, so the target still reads occluded when the window comes back.
            sc.Occluded = true;

            window.State = WindowState.Normal;   // restored well inside the Shallow delay: no hidden-stage restore repaints it
            int presents = sc.PresentCount;
            host.RunFrame();                     // the restore frame: nothing in the scene changed
            Assert.True(sc.PresentCount > presents, "the restore frame re-presents the stood-down target");
            Assert.True(host.RenderOccludedForTest);   // this frame still read the stale latch...

            sc.Occluded = false;                 // ...which the present it made has cleared
            host.RunFrame();
            Assert.False(host.RenderOccludedForTest);
            Assert.False(host.WindowOccludedForTest);
        }
        finally { host.Dispose(); }
    }
}
