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
using AppFrameClock = FluentGpu.Hooks.FrameClock;

namespace FluentGpu.Engine.Tests.Scout;

/// <summary>A keep-alive repaint (the OS modal move/size loop, the F093 peer tick) bypasses RunFrame. It used to keep the
/// last RunFrame's frame clock, so app motion sampled on <c>Hooks.FrameClock.PresentQpc</c> (lyrics, progress) and the
/// scroll step froze for the whole drag, and after an idle wait every tick resynced the frame delta to 0.</summary>
[Collection(SerialTestCollection.Name)]
public sealed class KeepAliveFrameClockTests
{
    private sealed class Poller : Component
    {
        public long SeenPresentQpc = -1;
        public override Element Render()
        {
            _ = UseContext(AppFrameClock.Tick);
            SeenPresentQpc = AppFrameClock.PresentQpc;
            return new BoxEl { Width = 10, Height = 10 };
        }
    }

    private sealed class Gate : Component
    {
        public readonly Signal<bool> Show = new(false);
        public override Element Render()
            => Show.Value ? new BoxEl { Width = 10, Height = 10, Children = [Embed.Comp(() => new Poller())] }
                          : new BoxEl { Width = 10, Height = 10, Children = [] };
    }

    private sealed class CountingTime : IFrameTimeSource
    {
        public int Resyncs;
        public float NextDeltaMs() => 16f;
        public void Resync() => Resyncs++;
    }

    [Fact]
    public void AKeepAlivePaint_AdvancesThePublishedFrameClock()
    {
        var strings = new StringTable();
        using var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("keepalive-clock", new Size2(200, 120), 1f));
        window.Show();
        var poller = new Poller();
        using var host = new AppHost(app, window, new HeadlessGpuDevice(), new HeadlessFontSystem(strings), strings, poller);
        for (int i = 0; i < 4; i++) host.RunFrame();

        long prev = AppFrameClock.PresentQpc;
        for (int tick = 0; tick < 3; tick++)
        {
            host.Paint(0, keepAlive: true);   // a modal-loop tick: RunFrame is suspended
            Assert.True(AppFrameClock.PresentQpc > prev, $"tick {tick}: PresentQpc stayed {prev}");
            Assert.Equal(host.FrameClock.PresentQpc, AppFrameClock.PresentQpc);   // the scroll step's clock moved with it
            Assert.Equal(AppFrameClock.PresentQpc, poller.SeenPresentQpc);       // the Tick poller re-rendered on it
            prev = AppFrameClock.PresentQpc;
        }
    }

    [Fact]
    public void OnlyTheFirstKeepAlivePaintAfterAnIdleWait_ResyncsTheFrameDelta()
    {
        var strings = new StringTable();
        using var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("keepalive-resync", new Size2(200, 120), 1f));
        window.Show();
        var gate = new Gate();
        var time = new CountingTime();
        using var host = new AppHost(app, window, new HeadlessGpuDevice(), new HeadlessFontSystem(strings), strings, gate,
                                     frameTime: time);
        for (int i = 0; i < 200 && (i < 4 || host.HasActiveWork); i++) host.RunFrame();
        host.RecommendedWaitMs();
        Assert.Equal(HostWaitKind.Idle, host.LastWaitKind);   // the loop blocked idle before the drag began

        gate.Show.Value = true;          // work arrives mid-drag: mounts a per-frame poller
        int before = time.Resyncs;
        for (int tick = 0; tick < 3; tick++) host.Paint(0, keepAlive: true);
        Assert.Equal(before + 1, time.Resyncs);   // the stale idle gap is dropped once, not on every tick
    }
}
