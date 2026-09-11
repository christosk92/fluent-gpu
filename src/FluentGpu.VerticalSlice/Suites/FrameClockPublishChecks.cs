using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Hosting;
using FluentGpu.Pal;
using FluentGpu.Pal.Headless;
using FluentGpu.Rhi.Headless;
using FluentGpu.Text.Headless;
using static FluentGpu.VerticalSlice.Harness.Gate;
// `FrameClock` is ambiguous between FluentGpu.Hooks (the app-facing static) and FluentGpu.Pal (the host's per-frame
// record) under these usings — every reference below is spelled through these two aliases.
using AppFrameClock = FluentGpu.Hooks.FrameClock;

// Hooks.FrameClock.FrameQpc / PresentQpc: the host publishes THIS frame's target time (its Pal.FrameClock pair) at the top
// of RunFrame, before input, posts, timers, the Tick publish and the flush — so a component rendering in the frame reads
// the frame's own vsync-lattice time instead of a ~15.6 ms-quantized wall clock. Headless it is deterministic (the
// FixedFrameTimeSource accumulator; PresentQpc = FrameQpc + one refresh period).
static class FrameClockPublishChecks
{
    internal static void Run(StringTable strings)
    {
        using var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("frameclock-publish", new Size2(200, 120), 1f));
        window.Show();
        var probe = new FrameClockReadProbe();
        using var host = new AppHost(app, window, new HeadlessGpuDevice(), new HeadlessFontSystem(strings), strings, probe);
        for (int i = 0; i < 3; i++) host.RunFrame();   // mount + settle (the Tick subscription keeps frames painting)

        bool published = true, renderSaw = true, lead = true, advances = true;
        long prevFrame = AppFrameClock.FrameQpc;
        int rendersBefore = probe.Renders;
        for (int i = 0; i < 4; i++)
        {
            host.RunFrame();
            var hostClock = host.FrameClock;   // the Pal.FrameClock this RunFrame built
            published &= AppFrameClock.FrameQpc == hostClock.FrameQpc && AppFrameClock.PresentQpc == hostClock.PresentQpc;
            renderSaw &= probe.SeenFrameQpc == hostClock.FrameQpc && probe.SeenPresentQpc == hostClock.PresentQpc;
            lead &= AppFrameClock.PresentQpc > AppFrameClock.FrameQpc;
            advances &= AppFrameClock.FrameQpc > prevFrame;
            prevFrame = AppFrameClock.FrameQpc;
        }
        bool rendered = probe.Renders - rendersBefore == 4;
        Check("gate.frameclock.app-publish Hooks.FrameClock.FrameQpc/PresentQpc equal the host's frame clock every RunFrame, are already set when a Tick-subscribed component renders in that frame, lead (Present > Frame) and advance deterministically headless",
            published && renderSaw && lead && advances && rendered,
            $"published={published} renderSaw={renderSaw} lead={lead} advances={advances} renders+{probe.Renders - rendersBefore} (want 4) frame={AppFrameClock.FrameQpc} present={AppFrameClock.PresentQpc}");
    }
}

/// <summary>Reads <c>FrameClock.Tick</c> (re-render every frame) and records the app-facing frame-clock statics it saw
/// while rendering — the "set before the flush" half of <see cref="FrameClockPublishChecks"/>.</summary>
sealed class FrameClockReadProbe : Component
{
    public long SeenFrameQpc = -1, SeenPresentQpc = -1;
    public int Renders;
    public override Element Render()
    {
        _ = UseContext(AppFrameClock.Tick);
        Renders++;
        SeenFrameQpc = AppFrameClock.FrameQpc;
        SeenPresentQpc = AppFrameClock.PresentQpc;
        return new BoxEl { Width = 10, Height = 10 };
    }
}
