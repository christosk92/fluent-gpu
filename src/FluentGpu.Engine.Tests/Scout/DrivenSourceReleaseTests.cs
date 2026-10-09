using FluentGpu.Animation;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Hosting;
using FluentGpu.Pal;
using FluentGpu.Pal.Headless;
using FluentGpu.Rhi.Headless;
using FluentGpu.Signals;
using FluentGpu.Text.Headless;
using Xunit;

namespace FluentGpu.Engine.Tests.Scout;

/// <summary>UseDrivenAnimation registered its value source in the host's DrivenClockTable on every seed and nothing removed
/// it: each remount and each deps change left the old Func&lt;float&gt; (and everything it captured) pinned for the window's
/// lifetime, and the table grew with every navigation.</summary>
[Collection(SerialTestCollection.Name)]
public sealed class DrivenSourceReleaseTests
{
    private sealed class Driven(Probe probe) : Component
    {
        public override Element Render()
        {
            UseDrivenAnimation(AnimChannel.Opacity, [new Keyframe(0f, 0f), new Keyframe(1f, 1f)],
                               () => 50f, 0f, 100f, DepKey.From(probe.Seed.Value));
            return new BoxEl { Width = 40f, Height = 40f };
        }
    }

    private sealed class Probe : Component
    {
        public readonly Signal<bool> Shown = new(true);
        public readonly Signal<int> Seed = new(0);

        public override Element Render()
            => new BoxEl { Grow = 1f, Children = Shown.Value ? [Embed.Comp(() => new Driven(this))] : [] };
    }

    private static void Frames(AppHost host) { for (int i = 0; i < 3; i++) host.RunFrame(); }

    [Fact]
    public void RemountsAndReseeds_ReleaseTheirSource_AndReuseItsIndex()
    {
        var strings = new StringTable();
        using var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("drive", new Size2(800, 600), 1f));
        window.Show();
        var probe = new Probe();
        using var host = new AppHost(app, window, new HeadlessGpuDevice(), new HeadlessFontSystem(strings), strings, probe);
        Frames(host);
        DrivenClockTable clocks = host.Animation.Clocks;
        Assert.Equal(50f, clocks.Sample(0));                 // the mounted driver's source is live

        for (int n = 1; n <= 3; n++) { probe.Seed.Value = n; Frames(host); }   // deps change: re-seed in place

        for (int n = 0; n < 3; n++)
        {
            probe.Shown.Value = false; Frames(host);         // unmount
            Assert.Equal(0f, clocks.Sample(0));              // released: the dead closure is no longer reachable
            probe.Shown.Value = true; Frames(host);          // remount
        }
        Assert.Equal(50f, clocks.Sample(0));                 // the live mount reuses the freed index...
        Assert.Equal(0f, clocks.Sample(1));                  // ...so the table never grew past one source
    }
}
