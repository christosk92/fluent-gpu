using System;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Hosting;
using FluentGpu.Pal;
using FluentGpu.Pal.Headless;
using FluentGpu.Rhi.Headless;
using FluentGpu.Text.Headless;
using Xunit;

namespace FluentGpu.Engine.Tests;

/// <summary>
/// F118: a flip-model composition swapchain does not reliably report DXGI_STATUS_OCCLUDED for a window another top-level covers,
/// so the main window kept recording and presenting invisible frames under a borderless fullscreen pop-out. The narrow fix parks
/// the main window while an ACTIVE, FULLSCREEN pop-out's rect contains its own. The verdict is pure; the host wiring (park,
/// <c>InputHooks.WindowOccluded</c>, the un-park on alt-tab / snap / exit-fullscreen) is driven through real headless windows.
/// Serial: it constructs several hosts.
/// </summary>
[Collection(SerialTestCollection.Name)]
public sealed class WindowCoverPolicyTests
{
    private static readonly RectF Monitor1 = new(0, 0, 1920, 1080);

    private static bool Covers(RectF child, RectF parent, bool fullscreen = true, bool active = true, bool visible = true)
        => WindowCoverPolicy.Covers(fullscreen, active, visible, child, parent);

    // ── pure verdict ───────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void AFullscreenActivePopOut_CoversAWindowedMainWindowOnItsMonitor()
        => Assert.True(Covers(Monitor1, new RectF(100, 100, 1200, 800)));

    [Fact]
    public void AMaximizedMainWindow_CoversThroughItsInvisibleResizeBorder()
    {
        // A maximized window's outer rect overhangs its monitor by the resize border (about 8 px; 16 px at 200% DPI).
        Assert.True(Covers(Monitor1, new RectF(-8, -8, 1936, 1096)));
        Assert.True(Covers(Monitor1, new RectF(-16, -16, 1952, 1112)));
    }

    [Fact]
    public void AMainWindowOnAnotherMonitor_IsNeverParked()
    {
        var monitor2 = new RectF(1920, 0, 1920, 1080);
        Assert.False(Covers(Monitor1, new RectF(2100, 100, 1200, 800)));      // main on the right monitor, pop-out on the left
        Assert.False(Covers(monitor2, new RectF(100, 100, 1200, 800)));       // and the other way round
        Assert.False(Covers(new RectF(-1920, 0, 1920, 1080), new RectF(100, 100, 1200, 800)));   // monitor left of the origin
    }

    [Fact]
    public void AMainWindowSpanningTwoMonitors_IsNeverParked()
        => Assert.False(Covers(Monitor1, new RectF(1500, 100, 1200, 800)));   // a third of it sticks out onto monitor 2

    [Fact]
    public void AWindowedOrSnappedPopOut_DoesNotCover()
    {
        // A snapped pop-out is a half-monitor rect and not fullscreen: the main window is partly visible.
        Assert.False(Covers(new RectF(960, 0, 960, 1080), new RectF(100, 100, 1200, 800), fullscreen: false));
        // Even a windowed pop-out that happens to be monitor-sized is not fullscreen (it has chrome and can be alt-tabbed over).
        Assert.False(Covers(Monitor1, new RectF(100, 100, 1200, 800), fullscreen: false));
    }

    [Fact]
    public void AnInactivePopOut_DoesNotCover_SoAltTabUnparksAtOnce()
        => Assert.False(Covers(Monitor1, new RectF(100, 100, 1200, 800), active: false));

    [Fact]
    public void AnInvisiblePopOut_DoesNotCover()
        => Assert.False(Covers(Monitor1, new RectF(100, 100, 1200, 800), visible: false));

    [Fact]
    public void ABackendThatCannotReportBounds_NeverParks()
    {
        Assert.False(Covers(default, new RectF(100, 100, 1200, 800)));
        Assert.False(Covers(Monitor1, default));
    }

    [Fact]
    public void ALargerChildRectStillCovers_AndAParentMuchLargerThanTheChildDoesNot()
    {
        Assert.True(Covers(new RectF(0, 0, 3840, 2160), Monitor1));            // an 8K-ish span covering a smaller main window
        Assert.False(Covers(new RectF(0, 0, 800, 450), new RectF(0, 0, 1920, 1080)));
    }

    // ── host wiring ────────────────────────────────────────────────────────────────────────────────────────────────

    private sealed class EmptyRoot : Component
    {
        public override Element Render() => Ui.VStack(0);
    }

    private sealed class Rig : IDisposable
    {
        public HeadlessPlatformApp App { get; } = new();
        public HeadlessWindow ParentWindow { get; }
        public HeadlessWindow ChildWindow { get; }
        public AppHost Parent { get; }
        public AppHost Child { get; }

        public Rig()
        {
            var strings = new StringTable();
            var device = new HeadlessGpuDevice();
            ParentWindow = new HeadlessWindow(new WindowDesc("main", new Size2(320, 240), 1f));
            ParentWindow.Show();
            ParentWindow.OuterBoundsPx = new RectF(100, 100, 1200, 800);
            Parent = new AppHost(App, ParentWindow, device, new HeadlessFontSystem(strings), strings, new EmptyRoot());
            Parent.RunFrame();
            ChildWindow = new HeadlessWindow(new WindowDesc("pop-out", new Size2(320, 180), 1f, Composited: true, CustomFrame: true));
            ChildWindow.Show();
            ChildWindow.OuterBoundsPx = new RectF(1500, 700, 320, 180);   // a small windowed pop-out to start with
            Child = new AppHost(App, ChildWindow, device, new HeadlessFontSystem(strings), strings, new EmptyRoot(),
                images: null, frameTime: null, compositeSwapchain: true, isDetachedChild: true, parentRenderThread: null);
            Parent.AdoptDetachedChild(Child);
            Child.RunFrame();
            Parent.RunFrame();
        }

        /// <summary>The pop-out goes borderless fullscreen on a monitor (its rect becomes the monitor's).</summary>
        public void GoFullscreen(RectF monitor)
        {
            ChildWindow.SetFullscreen(true);
            ChildWindow.OuterBoundsPx = monitor;
        }

        public void Dispose() { Parent.Dispose(); App.Dispose(); }
    }

    [Fact]
    public void AFullscreenPopOutOverTheMainWindow_ParksIt_AndRaisesWindowOccluded()
    {
        using var rig = new Rig();
        Assert.False(rig.Parent.IsParked);
        Assert.False(rig.Parent.WindowOccludedForTest);

        rig.GoFullscreen(Monitor1);
        rig.Parent.RunFrame();

        Assert.True(rig.Parent.IsParked);               // parks exactly like a minimized window
        Assert.True(rig.Parent.WindowOccludedForTest);  // and the visualizers / lyrics hear InputHooks.WindowOccluded
        Assert.True(rig.ParentWindow.IsVisible);        // the window itself is untouched: the park is the cover's alone
        Assert.InRange(rig.Parent.RecommendedWaitMs(), 1, 250);   // a cover raises no message of its own: it polls for the un-cover
        Assert.False(rig.Child.IsParked);               // the pop-out itself keeps ticking
    }

    [Fact]
    public void AltTab_UnparksTheMainWindowAtOnce_AndRefocusParksItAgain()
    {
        using var rig = new Rig();
        rig.GoFullscreen(Monitor1);
        rig.Parent.RunFrame();
        Assert.True(rig.Parent.IsParked);

        rig.ChildWindow.IsActive = false;   // alt-tab to another application
        rig.Parent.RunFrame();
        Assert.False(rig.Parent.IsParked);
        Assert.False(rig.Parent.WindowOccludedForTest);

        rig.ChildWindow.IsActive = true;
        rig.Parent.RunFrame();
        Assert.True(rig.Parent.IsParked);
    }

    [Fact]
    public void LeavingFullscreen_OrSnapping_UnparksTheMainWindow()
    {
        using var rig = new Rig();
        rig.GoFullscreen(Monitor1);
        rig.Parent.RunFrame();
        Assert.True(rig.Parent.IsParked);

        rig.ChildWindow.SetFullscreen(false);
        rig.ChildWindow.OuterBoundsPx = new RectF(960, 0, 960, 1080);   // snapped to the right half of the monitor
        rig.Parent.RunFrame();
        Assert.False(rig.Parent.IsParked);
        Assert.False(rig.Parent.WindowOccludedForTest);
    }

    [Fact]
    public void AFullscreenPopOutOnAnotherMonitor_DoesNotParkTheMainWindow()
    {
        using var rig = new Rig();
        rig.GoFullscreen(new RectF(1920, 0, 1920, 1080));   // the main window stays on monitor 1
        rig.Parent.RunFrame();
        Assert.False(rig.Parent.IsParked);
        Assert.False(rig.Parent.WindowOccludedForTest);
    }

    [Fact]
    public void AMainWindowSpanningTwoMonitors_IsNotParkedByAFullscreenPopOutOnOne()
    {
        using var rig = new Rig();
        rig.ParentWindow.OuterBoundsPx = new RectF(1500, 100, 1200, 800);
        rig.GoFullscreen(Monitor1);
        rig.Parent.RunFrame();
        Assert.False(rig.Parent.IsParked);
    }

    [Fact]
    public void AMinimizedOrHiddenPopOut_DoesNotParkTheMainWindow()
    {
        using var rig = new Rig();
        rig.GoFullscreen(Monitor1);
        rig.ChildWindow.State = WindowState.Minimized;
        rig.Parent.RunFrame();
        Assert.False(rig.Parent.IsParked);

        rig.ChildWindow.State = WindowState.Normal;
        rig.ChildWindow.Hide();
        rig.Parent.RunFrame();
        Assert.False(rig.Parent.IsParked);
    }

    [Fact]
    public void AnUnrevealedPopOut_DoesNotParkTheMainWindow()
    {
        using var rig = new Rig();
        rig.GoFullscreen(Monitor1);
        rig.Child.BeginDetachedReveal(alwaysOnTop: true, System.Diagnostics.Stopwatch.GetTimestamp(), windowCreateMs: 1, hostCtorMs: 1);
        rig.Parent.RunFrame();
        Assert.False(rig.Parent.IsParked);   // nothing of a still-hidden pop-out is on screen
    }

    [Fact]
    public void ClosingTheFullscreenPopOut_UnparksTheMainWindow()
    {
        using var rig = new Rig();
        rig.GoFullscreen(Monitor1);
        rig.Parent.RunFrame();
        Assert.True(rig.Parent.IsParked);

        rig.ChildWindow.IsClosed = true;    // the user closed it; the reaper has not run yet
        rig.Parent.RunFrame();
        Assert.False(rig.Parent.IsParked);  // a closed window covers nothing
        rig.Parent.TickDetachedHosts();     // and the reaper then removes it
        Assert.Equal(0, rig.Parent.DetachedWindowCount);
        rig.Parent.RunFrame();
        Assert.False(rig.Parent.IsParked);
    }

    [Fact]
    public void ADetachedChild_IsNeverCoverParked()
    {
        using var rig = new Rig();
        rig.GoFullscreen(Monitor1);
        rig.Child.RunFrame();
        Assert.False(rig.Child.IsParked);   // only the primary host reads the cover verdict
    }
}
