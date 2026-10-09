using System;
using System.Linq;
using System.Threading;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Hosting;
using FluentGpu.Pal;
using FluentGpu.Pal.Headless;
using FluentGpu.Render.Tiles;
using FluentGpu.Rhi.Headless;
using FluentGpu.Scene;
using FluentGpu.Text.Headless;
using Xunit;

namespace FluentGpu.Engine.Tests;

/// <summary>The host half of the hidden-window Shallow stage on the headless (SingleThread) path: a window parked for long enough
/// releases its tiles and the image textures nothing holds, keeps what is pinned or held, and un-parks into a state the next
/// frame rebuilds from. The stage machine itself is covered purely in <see cref="HiddenMemoryPolicyTests"/>.</summary>
[Collection(SerialTestCollection.Name)]
public sealed class HiddenStageHostTests : IDisposable
{
    private static readonly RectF Monitor = new(0, 0, 1920, 1080);

    public HiddenStageHostTests() => HiddenMemoryBudget.Reset();
    public void Dispose() => HiddenMemoryBudget.Reset();

    private sealed class PinnedArt : Component
    {
        public override Element Render() => new ImageEl { Source = "art://pinned", Width = 40f, Height = 40f };
    }

    private sealed class Rig : IDisposable
    {
        public HeadlessPlatformApp App { get; } = new();
        public HeadlessGpuDevice Device { get; } = new();
        public HeadlessWindow Window { get; }
        public AppHost Host { get; }

        public Rig()
        {
            var strings = new StringTable();
            Window = new HeadlessWindow(new WindowDesc("main", new Size2(320, 240), 1f));
            Window.Show();
            Window.OuterBoundsPx = new RectF(100, 100, 320, 240);
            Host = new AppHost(App, Window, Device, new HeadlessFontSystem(strings), strings, new PinnedArt());
            for (int i = 0; i < 8; i++) Host.RunFrame();
        }

        public void Dispose() { Host.Dispose(); App.Dispose(); }
    }

    private static ImageHandle Land(Rig rig, string src)
    {
        var h = rig.Host.Images.Request(src, 64, 64);
        for (int i = 0; i < 3; i++) rig.Host.RunFrame();
        return h;
    }

    private static void Minimize(Rig rig) { rig.Window.State = WindowState.Minimized; rig.Host.RunFrame(); }

    [Fact]
    public void AMinimizedWindowReleasesTilesAndUnpinnedImagesAfterTheDelay_AndKeepsPinnedOnes()
    {
        using var rig = new Rig();
        var loose = Land(rig, "art://loose");
        int tiles = rig.Host.UiSliceTable.ResidentTiles;
        Assert.True(tiles > 0, "the headless host composites at least one retained tile");
        Assert.True(rig.Device.ResidentImages.ContainsKey(loose.Id));
        int residentBefore = rig.Device.ResidentImages.Count;

        Minimize(rig);
        Assert.Equal(HiddenStage.Visible, rig.Host.HiddenStageForTest);   // not before the delay
        Assert.Empty(rig.Device.HiddenReleases);
        Assert.InRange(rig.Host.RecommendedWaitMs(), 1, 2_000);           // the parked loop wakes once for the release

        rig.Host.AdvanceFrameClockForTest(2_500);
        rig.Host.RunFrame();

        Assert.Equal(HiddenStage.Shallow, rig.Host.HiddenStageForTest);
        Assert.Equal(new[] { HiddenStage.Shallow, HiddenStage.Shallow }, rig.Device.HiddenReleases.ToArray());   // before and after the evict drain
        Assert.Equal(0, rig.Host.UiSliceTable.ResidentTiles);
        Assert.Equal(tiles, rig.Device.TrimmedTileSlots);                                // every tile's texture handed back
        Assert.False(rig.Device.ResidentImages.ContainsKey(loose.Id));               // unpinned: released
        Assert.Equal(residentBefore - 1, rig.Device.ResidentImages.Count);           // the pinned cover stays
        Assert.Equal((int)(HiddenMemoryBudget.DefaultDeepDelayMs - 2_500), rig.Host.RecommendedWaitMs());   // and the parked loop sleeps until Deep (HiddenDeepHostTests), no sooner
    }

    [Fact]
    public void AnImageSomethingStillHoldsIsNotReleased()
    {
        using var rig = new Rig();
        var held = Land(rig, "art://row-cell");
        rig.Host.Images.SetHeldImageSource(set => set.Add(held.Id));   // what a list row's image cell looks like to the cache: held, not pinned
        Minimize(rig);
        rig.Host.AdvanceFrameClockForTest(2_500);
        rig.Host.RunFrame();
        Assert.Equal(HiddenStage.Shallow, rig.Host.HiddenStageForTest);
        Assert.True(rig.Device.ResidentImages.ContainsKey(held.Id));
    }

    [Fact]
    public void AShortMinimizeReleasesNothing()
    {
        using var rig = new Rig();
        var loose = Land(rig, "art://loose");
        int tiles = rig.Host.UiSliceTable.ResidentTiles;
        Assert.True(tiles > 0, "the headless host composites at least one retained tile");
        Minimize(rig);
        rig.Host.AdvanceFrameClockForTest(1_900);
        rig.Host.RunFrame();
        rig.Window.State = WindowState.Normal;
        rig.Host.RunFrame();
        Assert.Empty(rig.Device.HiddenReleases);
        Assert.Equal(tiles, rig.Host.UiSliceTable.ResidentTiles);
        Assert.True(rig.Device.ResidentImages.ContainsKey(loose.Id));
    }

    [Fact]
    public void ARestoreLiftsTheStageAndTheFirstFrameRebuilds()
    {
        using var rig = new Rig();
        var loose = Land(rig, "art://loose");
        int tiles = rig.Host.UiSliceTable.ResidentTiles;
        Minimize(rig);
        rig.Host.AdvanceFrameClockForTest(2_500);
        rig.Host.RunFrame();
        Assert.Equal(HiddenStage.Shallow, rig.Host.HiddenStageForTest);
        Assert.Equal(0, rig.Host.UiSliceTable.ResidentTiles);

        rig.Window.State = WindowState.Normal;
        rig.Host.RunFrame();
        Assert.Equal(HiddenStage.Visible, rig.Host.HiddenStageForTest);
        Assert.Equal(HiddenStage.Visible, rig.Device.HiddenReleases[^1]);
        Assert.Equal(tiles, rig.Host.UiSliceTable.ResidentTiles);          // the restore frame re-rastered every tile the release dropped
        Assert.Equal(0, rig.Host.UiSliceTable.CountExposedMissing());

        // The released image comes back when a row asks for it again (a node pins it on realize).
        rig.Host.Images.Pin(loose);
        for (int i = 0; i < 3; i++) rig.Host.RunFrame();
        Assert.True(rig.Device.ResidentImages.ContainsKey(loose.Id));
    }

    [Fact]
    public void ACoveredWindowWaitsTheLongerCoverDelay()
    {
        using var rig = new Rig();
        int tiles = rig.Host.UiSliceTable.ResidentTiles;
        Assert.True(tiles > 0, "the headless host composites at least one retained tile");
        rig.Window.SetOccluders(Monitor);   // a maximized browser over it
        rig.Host.RunFrame();
        Assert.True(rig.Host.IsParked);
        rig.Host.AdvanceFrameClockForTest(5_000);
        rig.Host.RunFrame();
        Assert.Equal(HiddenStage.Visible, rig.Host.HiddenStageForTest);   // alt-tab territory: 5 s behind a window costs nothing
        Assert.Equal(tiles, rig.Host.UiSliceTable.ResidentTiles);
        rig.Host.AdvanceFrameClockForTest(HiddenMemoryBudget.DefaultCoverShallowDelayMs);
        rig.Host.RunFrame();
        Assert.Equal(HiddenStage.Shallow, rig.Host.HiddenStageForTest);
        Assert.Equal(0, rig.Host.UiSliceTable.ResidentTiles);

        rig.Window.SetOccluders();          // uncovered
        rig.Host.RunFrame();
        Assert.False(rig.Host.IsParked);
        Assert.Equal(HiddenStage.Visible, rig.Host.HiddenStageForTest);
    }

    [Fact]
    public void TheRenderThreadAppliesTheStageBeforeItsNextTurn_AndALaterRestoreLiftsIt()
    {
        var strings = new StringTable();
        using var app = new HeadlessPlatformApp();
        var device = new HeadlessGpuDevice();
        var window = new HeadlessWindow(new WindowDesc("main", new Size2(320, 240), 1f));
        window.Show();
        using var host = new AppHost(app, window, device, new HeadlessFontSystem(strings), strings, new PinnedArt());
        host.InstallRenderThreadForTest();
        for (int i = 0; i < 8; i++) host.RunFrame();
        int tiles = host.RenderTilesResidentForTest;
        Assert.True(tiles > 0, "the render thread's table holds the composited tiles");

        window.State = WindowState.Minimized;
        host.RunFrame();
        host.AdvanceFrameClockForTest(2_500);
        host.RunFrame();
        Assert.Equal(HiddenStage.Shallow, host.HiddenStageForTest);
        Assert.True(SpinWait.SpinUntil(() => host.HiddenAppliedForTest == HiddenStage.Shallow, 5_000), "the render thread never applied the stage");
        Assert.Equal(0, host.RenderTilesResidentForTest);   // the RENDER table, not the UI one
        Assert.Equal(new[] { HiddenStage.Shallow, HiddenStage.Shallow }, device.HiddenReleases.ToArray());

        window.State = WindowState.Normal;
        host.RunFrame();
        Assert.True(SpinWait.SpinUntil(() => host.HiddenAppliedForTest == HiddenStage.Visible, 5_000));
        Assert.Equal(HiddenStage.Visible, device.HiddenReleases[^1]);
    }

    [Fact]
    public void ARestoreThatPaintsThroughPaintRequestedLiftsTheStageBeforeThePaint()
    {
        using var rig = new Rig();
        Minimize(rig);
        rig.Host.AdvanceFrameClockForTest(2_500);
        rig.Host.RunFrame();
        Assert.Equal(HiddenStage.Shallow, rig.Host.HiddenStageForTest);
        int releasesBefore = rig.Device.HiddenReleases.Count;

        rig.Window.State = WindowState.Normal;   // WM_SIZE(SIZE_RESTORED) paints synchronously, before any RunFrame sees the un-park
        rig.Host.Paint(0, keepAlive: true);

        Assert.Equal(HiddenStage.Visible, rig.Host.HiddenStageForTest);
        Assert.Equal(HiddenStage.Visible, rig.Device.HiddenReleases[^1]);
        Assert.Equal(releasesBefore + 1, rig.Device.HiddenReleases.Count);   // exactly the lift, no Shallow release after the paint
        rig.Host.RunFrame();
        Assert.Equal(releasesBefore + 1, rig.Device.HiddenReleases.Count);
    }

    private sealed class EmptyRoot : Component
    {
        public override Element Render() => Ui.VStack(0);
    }

    [Fact]
    public void AVisiblePopOutKeepsTheDeviceWideSharedResourcesWhileTheMainWindowIsHidden()
    {
        var strings = new StringTable();
        using var app = new HeadlessPlatformApp();
        var device = new HeadlessGpuDevice();
        var window = new HeadlessWindow(new WindowDesc("main", new Size2(320, 240), 1f));
        window.Show();
        using var host = new AppHost(app, window, device, new HeadlessFontSystem(strings), strings, new PinnedArt());
        var rt = host.InstallRenderThreadForTest();
        for (int i = 0; i < 8; i++) host.RunFrame();
        var cw = new HeadlessWindow(new WindowDesc("pop-out", new Size2(320, 180), 1f, Composited: true, CustomFrame: true));
        cw.Show();
        var child = new AppHost(app, cw, device, new HeadlessFontSystem(strings), strings, new EmptyRoot(),
            images: null, frameTime: null, compositeSwapchain: true, isDetachedChild: true, parentRenderThread: rt);
        host.AdoptDetachedChild(child);
        host.AttachChildRenderSourceForTest(child);
        child.RunFrame();

        window.State = WindowState.Minimized;
        host.RunFrame();
        host.AdvanceFrameClockForTest(2_500);
        host.RunFrame();
        Assert.True(SpinWait.SpinUntil(() => host.HiddenAppliedForTest == HiddenStage.Shallow, 5_000));
        Assert.NotEmpty(device.HiddenReleaseSharedScope);
        Assert.All(device.HiddenReleaseSharedScope, shared => Assert.True(shared, "scratch / blur pyramids / pooling are shared with the visible pop-out"));
    }

    [Fact]
    public void MaxDisablesTheStage()
    {
        HiddenMemoryBudget.TryApply("max:max");
        using var rig = new Rig();
        int tiles = rig.Host.UiSliceTable.ResidentTiles;
        Assert.True(tiles > 0, "the headless host composites at least one retained tile");
        Minimize(rig);
        rig.Host.AdvanceFrameClockForTest(10 * 60_000);
        rig.Host.RunFrame();
        Assert.Equal(HiddenStage.Visible, rig.Host.HiddenStageForTest);
        Assert.Empty(rig.Device.HiddenReleases);
        Assert.Equal(-1, rig.Host.RecommendedWaitMs());
    }
}
