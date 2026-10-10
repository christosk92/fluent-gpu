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

namespace FluentGpu.Engine.Tests.Scout;

/// <summary>
/// The PRIMARY window left on another virtual desktop is DWM-cloaked: it keeps <c>WS_VISIBLE</c> and its placement, but every
/// present stands down. It parks exactly like a cloaked pop-out (no Paint, no occlusion-probe repaint every 250 ms,
/// <c>UseIsActive</c> false) once the cloak has held for <see cref="CloakParkGate.EnterDelayMs"/>, polls for the un-cloak, and
/// releases hidden memory on the cover delay. Serial: it constructs hosts and mutates <see cref="HiddenMemoryBudget"/>.
/// </summary>
[Collection(SerialTestCollection.Name)]
public sealed class PrimaryCloakParkTests : IDisposable
{
    public PrimaryCloakParkTests() => HiddenMemoryBudget.Reset();
    public void Dispose() => HiddenMemoryBudget.Reset();

    private sealed class EmptyRoot : Component
    {
        public override Element Render() => Ui.VStack(0);
    }

    private sealed class Rig : IDisposable
    {
        public HeadlessPlatformApp App { get; } = new();
        public HeadlessWindow Window { get; }
        public AppHost Host { get; }

        public Rig()
        {
            var strings = new StringTable();
            Window = new HeadlessWindow(new WindowDesc("main", new Size2(320, 240), 1f));
            Window.Show();
            Host = new AppHost(App, Window, new HeadlessGpuDevice(), new HeadlessFontSystem(strings), strings, new EmptyRoot());
            Host.RunFrame();
        }

        public void Dispose() { Host.Dispose(); App.Dispose(); }
    }

    // The headless timer clock only advances on a painted frame, so force one per step (16 ms each).
    private static void PaintFrames(AppHost host, int count)
    {
        for (int i = 0; i < count; i++) { host.RequestFullRepaintOnce(); host.RunFrame(); }
    }

    [Fact]
    public void ACloakedPrimary_ParksAfterTheDebounce_PollsForTheUncloak_AndUnparksWhenItClears()
    {
        using var rig = new Rig();
        PaintFrames(rig.Host, 3);
        Assert.False(rig.Host.IsParked);

        rig.Window.IsCloaked = true;
        PaintFrames(rig.Host, 3);
        Assert.False(rig.Host.IsParked);   // inside the debounce: a shell transition never parks

        PaintFrames(rig.Host, (int)(CloakParkGate.EnterDelayMs / 16) + 4);
        Assert.True(rig.Host.IsParked);
        Assert.True(rig.Window.IsVisible);   // still "shown": the park is the cloak's alone

        // No message announces an un-cloak, so a cloak-parked primary polls instead of blocking forever.
        Assert.InRange(rig.Host.RecommendedWaitMs(), 1, 250);

        rig.Window.IsCloaked = false;
        rig.Host.RunFrame();
        Assert.False(rig.Host.IsParked);
    }

    [Fact]
    public void AnIdleCloakedPrimary_WakesWhenTheDebounceEnds()
    {
        using var rig = new Rig();
        PaintFrames(rig.Host, 3);
        Assert.Equal(-1, rig.Host.RecommendedWaitMs());   // uncloaked and idle: block until a message

        rig.Window.IsCloaked = true;
        rig.Host.RunFrame();
        Assert.False(rig.Host.IsParked);
        Assert.InRange(rig.Host.RecommendedWaitMs(), 1, (int)CloakParkGate.EnterDelayMs);
    }

    [Fact]
    public void ACloakedPrimary_ReleasesHiddenMemoryOnTheCoverDelay()
    {
        using var rig = new Rig();
        rig.Window.IsCloaked = true;
        PaintFrames(rig.Host, (int)(CloakParkGate.EnterDelayMs / 16) + 4);
        Assert.True(rig.Host.IsParked);

        rig.Host.AdvanceFrameClockForTest(5_000);   // longer than the minimize delay, shorter than the cover delay
        rig.Host.RunFrame();
        Assert.Equal(HiddenStage.Visible, rig.Host.HiddenStageForTest);   // a desktop round trip costs nothing

        rig.Host.AdvanceFrameClockForTest(HiddenMemoryBudget.DefaultCoverShallowDelayMs);
        rig.Host.RunFrame();
        Assert.Equal(HiddenStage.Shallow, rig.Host.HiddenStageForTest);

        rig.Window.IsCloaked = false;
        rig.Host.RunFrame();
        Assert.False(rig.Host.IsParked);
        Assert.Equal(HiddenStage.Visible, rig.Host.HiddenStageForTest);
    }
}
