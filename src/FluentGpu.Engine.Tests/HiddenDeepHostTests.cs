using System;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Hosting;
using FluentGpu.Pal;
using FluentGpu.Pal.Headless;
using FluentGpu.Rhi.Headless;
using FluentGpu.Scene;
using FluentGpu.Text.Headless;
using Xunit;

namespace FluentGpu.Engine.Tests;

/// <summary>The host half of the Deep hidden stage on the headless (SingleThread) path: five minutes minimized parks the images the
/// content holds (but never a keep-while-hidden one), a cover-park never reaches Deep, and a restore brings everything back with the
/// held-present latch released and the window never reading as occluded. The races of the render thread (hold bit vs. latch vs.
/// gate) need the Async loop and are covered on a device, not here.</summary>
[Collection(SerialTestCollection.Name)]
public sealed class HiddenDeepHostTests : IDisposable
{
    public HiddenDeepHostTests() => HiddenMemoryBudget.Reset();
    public void Dispose() => HiddenMemoryBudget.Reset();

    private sealed class TwoCovers : Component
    {
        public override Element Render() => Ui.VStack(0, new Element[]
        {
            new ImageEl { Source = "art://ordinary", Width = 40f, Height = 40f },
            new ImageEl { Source = "art://kept", Width = 40f, Height = 40f, KeepWhileHidden = true },
        });
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
            Host = new AppHost(App, Window, Device, new HeadlessFontSystem(strings), strings, new TwoCovers());
            for (int i = 0; i < 8; i++) Host.RunFrame();
        }

        public void Dispose() { Host.Dispose(); App.Dispose(); }
    }

    private static void Minimize(Rig rig) { rig.Window.State = WindowState.Minimized; rig.Host.RunFrame(); }

    [Fact]
    public void FiveMinutesMinimizedParksTheHeldImagesButNotTheKeptOne_AndARestoreBringsThemBack()
    {
        using var rig = new Rig();
        int resident = rig.Device.ResidentImages.Count;
        Assert.True(resident >= 2, "both covers are resident before the hide");

        Minimize(rig);
        rig.Host.AdvanceFrameClockForTest(2_500);
        rig.Host.RunFrame();
        Assert.Equal(HiddenStage.Shallow, rig.Host.HiddenStageForTest);
        Assert.Equal(resident, rig.Device.ResidentImages.Count);   // both are pinned by a node: Shallow keeps them

        rig.Host.AdvanceFrameClockForTest(HiddenMemoryBudget.DefaultDeepDelayMs);
        rig.Host.RunFrame();
        Assert.Equal(HiddenStage.Deep, rig.Host.HiddenStageForTest);
        Assert.Equal(HiddenStage.Deep, rig.Host.HiddenAppliedForTest);
        Assert.Equal(1, rig.Host.Images.ParkedCount);                        // the ordinary cover
        Assert.Equal(resident - 1, rig.Device.ResidentImages.Count);         // the kept one stays resident
        Assert.True(rig.Host.RenderHeldForTest);                             // nothing presents until a faithful frame

        rig.Window.State = WindowState.Normal;
        for (int i = 0; i < 4; i++) rig.Host.RunFrame();
        Assert.Equal(HiddenStage.Visible, rig.Host.HiddenStageForTest);
        Assert.Equal(0, rig.Host.Images.ParkedCount);
        Assert.Equal(0, rig.Host.Images.RestorePendingCount);
        Assert.Equal(resident, rig.Device.ResidentImages.Count);
        Assert.False(rig.Host.RenderHeldForTest);                            // released by the first faithful frame
        var sc = rig.Device.PrimarySwapchain!;
        Assert.False(sc.LastPresentHeld);
        Assert.False(sc.IsOccluded);                                         // a held present is never a stand-down
        Assert.False(rig.Host.RenderOccludedForTest);
    }

    [Fact]
    public void ACoverParkNeverReachesDeep()
    {
        using var rig = new Rig();
        rig.Window.SetOccluders(new RectF(0, 0, 1920, 1080));
        rig.Host.RunFrame();
        rig.Host.AdvanceFrameClockForTest(60 * 60_000);
        rig.Host.RunFrame();
        Assert.Equal(HiddenStage.Shallow, rig.Host.HiddenStageForTest);
        Assert.Equal(0, rig.Host.Images.ParkedCount);
    }

    [Fact]
    public void TheDeepSwitchDisablesJustDeep()
    {
        Assert.True(HiddenMemoryBudget.TryApply("deep=max"));
        using var rig = new Rig();
        Minimize(rig);
        rig.Host.AdvanceFrameClockForTest(20 * 60_000);
        rig.Host.RunFrame();
        Assert.Equal(HiddenStage.Shallow, rig.Host.HiddenStageForTest);
        Assert.Equal(0, rig.Host.Images.ParkedCount);
    }
}
