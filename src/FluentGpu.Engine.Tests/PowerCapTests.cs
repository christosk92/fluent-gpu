using FluentGpu.Animation;
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

/// <summary><see cref="AppHost.PowerCapFps"/>: the one host-wide frame-rate ceiling (for the OS asking for less work).
/// A loop with no explicit cadence runs at the display rate; under the ceiling the same frame is paced onto the vblank
/// lattice at the cap, and 0 removes it again. Driven through a real headless <see cref="AppHost"/>.</summary>
[Collection(SerialTestCollection.Name)]
public sealed class PowerCapTests
{
    private sealed class Root : Component
    {
        public override Element Render() => Ui.VStack(0);
    }

    private static bool IsDisplayRate(HostWaitKind k)
        => k is HostWaitKind.DisplayRate or HostWaitKind.DisplayTick or HostWaitKind.SoftwarePace;

    [Fact]
    public void A_cadence_less_loop_runs_at_display_rate_and_the_power_cap_paces_it()
    {
        using var app = new HeadlessPlatformApp();
        var strings = new StringTable();
        var window = new HeadlessWindow(new WindowDesc("power-cap", new Size2(320, 240), 1f));
        window.Show();
        using var host = new AppHost(app, window, new HeadlessGpuDevice(), new HeadlessFontSystem(strings), strings, new Root());
        for (int i = 0; i < 8 && host.HasActiveWork; i++) host.RunFrame();

        host.Animation.Keyframes(host.Scene.Root, AnimChannel.Opacity,
            new[] { new Keyframe(0f, 0.4f, Easing.Linear), new Keyframe(1f, 1f, Easing.Linear) }, 800f, loop: true);
        host.RunFrame();

        host.RecommendedWaitMs();
        Assert.True(IsDisplayRate(host.LastWaitKind), $"a plain loop is display-rate work, got {host.LastWaitKind}");

        host.PowerCapFps = 30;
        int capped = host.RecommendedWaitMs();
        Assert.Equal(HostWaitKind.PowerCap, host.LastWaitKind);
        Assert.InRange(capped, 1, 34);                                 // one 30 fps period at most, never a 0 spin

        host.PowerCapFps = 0;
        host.RecommendedWaitMs();
        Assert.True(IsDisplayRate(host.LastWaitKind), $"0 removes the ceiling, got {host.LastWaitKind}");
    }

    [Fact]
    public void Interaction_is_never_power_capped_but_frame_clock_pollers_are()
    {
        Assert.NotEqual(WakeReasons.None, AppHost.PowerCapNeverPace & WakeReasons.ScrollAnim);
        Assert.NotEqual(WakeReasons.None, AppHost.PowerCapNeverPace & WakeReasons.DragActive);
        Assert.NotEqual(WakeReasons.None, AppHost.PowerCapNeverPace & WakeReasons.TouchPress);
        Assert.Equal(WakeReasons.None, AppHost.PowerCapNeverPace & WakeReasons.FrameClockPoller);
        Assert.Equal(WakeReasons.None, AppHost.PowerCapNeverPace & WakeReasons.Anim);
    }
}
