using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Hosting;
using FluentGpu.Pal;
using FluentGpu.Pal.Headless;
using FluentGpu.Rhi.Headless;
using FluentGpu.Signals;
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

// A leaked per-frame poller pins the host at panel rate forever, and for a whole release the only evidence was a COUNT
// nothing printed: `[wake]` said `frameClockPoller` held the loop on 100 % of runs and could not say WHO. The census now
// NAMES its subscribers, and this gate holds both halves of that: the names are right for BOTH subscription shapes, and
// count + names + wake bit all fall back to zero when the pollers unmount. The second half is the regression itself —
// the one Signal.SubscriberCount's own doc asks a soak check to make ("returns to 0 when playback/animation stops").
static class FrameClockPollerCensusChecks
{
    internal static void Run(StringTable strings)
    {
        using var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("frameclock-pollers", new Size2(200, 120), 1f));
        window.Show();
        FrameClockStepperProbe.TotalSteps = 0;
        var root = new FrameClockPollerRoot();
        using var host = new AppHost(app, window, new HeadlessGpuDevice(), new HeadlessFontSystem(strings), strings, root);
        for (int i = 0; i < 3; i++) host.RunFrame();   // mount + settle

        var sb = new System.Text.StringBuilder();
        host.DescribeFrameClockPollers(sb);
        string live = sb.ToString();
        // Both shapes subscribe: the render-effect (UseContext) and the ownerless hook effect (UseContextSignal +
        // UseSignalEffect) — the latter is the shape an owner-chain walk could never have named.
        bool counted = host.FrameClockPollerCount == 2;
        bool named = live.Contains(nameof(FrameClockReadProbe), StringComparison.Ordinal)
                  && live.Contains(nameof(FrameClockStepperProbe), StringComparison.Ordinal)
                  && !live.Contains('?');   // both stamped — no anonymous subscriber
        bool bitOn = (host.CurrentWakeReasons & WakeReasons.FrameClockPoller) != 0;
        bool stepped = FrameClockStepperProbe.TotalSteps > 0;   // the hook effect really is driven by the tick

        root.Show.Value = false;   // unmount both pollers, exactly as a gate going false does in the app
        host.RunFrame();
        int settle = 0;
        for (; settle < 200 && host.HasActiveWork; settle++) host.RunFrame();
        sb.Clear();
        host.DescribeFrameClockPollers(sb);
        string after = sb.ToString();
        bool released = host.FrameClockPollerCount == 0 && after == " | pollers=0";
        bool bitOff = (host.CurrentWakeReasons & WakeReasons.FrameClockPoller) == 0 && settle < 200;

        Check("gate.frameclock.poller-census the [wake] census names every live FrameClock.Tick poller by owning component type for BOTH subscription shapes (render-effect and ownerless hook effect), and count, names and the FrameClockPoller wake bit all return to zero once they unmount",
            counted && named && bitOn && stepped && released && bitOff,
            $"live=\"{live.Trim()}\" after=\"{after.Trim()}\" count={host.FrameClockPollerCount} bitOn={bitOn} stepped={stepped} released={released} bitOff={bitOff} settle={settle}");
    }
}

/// <summary>The <c>LyricsFrameStepper</c> shape: the tick is read inside a hook-owned signal effect, so the subscriber
/// is an <c>owner: null</c> <c>Effect</c> and the component itself NEVER re-renders. That is what makes this shape the
/// dangerous one — it holds the frame clock without showing up in any render/reconcile count, and its mount gate is
/// only re-evaluated when its PARENT re-renders (so a parked or quiescent parent leaves it running forever).</summary>
sealed class FrameClockStepperProbe : Component
{
    /// <summary>Static so the gate can assert the effect actually RAN without holding the instance — an
    /// <c>Embed.Comp</c> factory must mint a fresh component (props freeze at mount), so the gate cannot keep a
    /// reference to the mounted one. Reset at the top of the gate.</summary>
    internal static int TotalSteps;
    public override Element Render()
    {
        var tick = UseContextSignal(AppFrameClock.Tick);
        UseSignalEffect(() => { _ = tick.Value; TotalSteps++; });
        return new BoxEl { HitTestVisible = false, Width = 0f, Height = 0f };
    }
}

/// <summary>Mounts both poller shapes behind one conditional — the real app's gate shape (a plain ternary in the
/// parent's render, as in <c>LyricsView</c>'s <c>needsTicks</c>), so flipping <see cref="Show"/> unmounts them the way
/// a gate going false does in production.</summary>
sealed class FrameClockPollerRoot : Component
{
    public readonly Signal<bool> Show = new(true);
    public override Element Render()
    {
        if (!Show.Value) return new BoxEl { Width = 10, Height = 10, Children = [] };
        return new BoxEl
        {
            Width = 10, Height = 10,
            Children = [Embed.Comp(() => new FrameClockReadProbe()), Embed.Comp(() => new FrameClockStepperProbe())],
        };
    }
}

// Tonight's real-app log: a window with `frameClockPoller=500 | sole: frameClockPoller=244 | pollers=0` — a poller
// held 244 frames awake by itself and had already unmounted before the 30 s report printed, so `pollers=` (survivors
// only) named nobody. `pollersSeen=` fixes that by tallying names DURING the window, not at print time. This gate
// proves the survivorship gap is closed: mount a poller, hold it 5 frames, unmount it, run 5 MORE frames with it long
// gone, and pollersSeen must still name it with a frame count >= 5 even though pollers= now reads 0.
static class FrameClockPollersSeenChecks
{
    internal static void Run(StringTable strings)
    {
        using var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("frameclock-pollersseen", new Size2(200, 120), 1f));
        window.Show();
        var root = new SeenPollerGateRoot();
        using var host = new AppHost(app, window, new HeadlessGpuDevice(), new HeadlessFontSystem(strings), strings, root);
        host.RunFrame();   // mount

        for (int i = 0; i < 5; i++) host.RunFrame();   // 5 frames while the poller is live and kept-awake

        root.Show.Value = false;   // unmount — exactly as a gate going false does in the app
        host.RunFrame();
        int settle = 0;
        for (; settle < 200 && host.HasActiveWork; settle++) host.RunFrame();   // let the unmount fully settle

        for (int i = 0; i < 5; i++) host.RunFrame();   // 5 MORE frames, long after the poller is gone

        var sb = new System.Text.StringBuilder();
        host.DescribeFrameClockPollers(sb);
        string pollersNow = sb.ToString();
        bool goneNow = pollersNow == " | pollers=0";   // the survivor view sees nobody

        sb.Clear();
        host.DescribeFrameClockPollersSeen(sb);
        string seen = sb.ToString();
        int seenFrames = ExtractFrameCount(seen, nameof(FrameClockReadProbe));
        bool namedWithCount = seenFrames >= 5;

        Check("gate.frameclock.pollers-seen a FrameClock.Tick poller that mounts, holds 5 kept-awake frames, and unmounts is still NAMED (with a frame count >= 5) in pollersSeen= after 5 more frames run with it long gone, even though pollers= (survivors at print time) has already fallen back to 0",
            goneNow && namedWithCount && settle < 200,
            $"pollersNow=\"{pollersNow}\" seen=\"{seen}\" seenFrames={seenFrames} settle={settle}");
    }

    /// <summary>Pull the frame count for <paramref name="name"/> out of a <c>pollersSeen=N:Name×frames,…</c> line —
    /// test-only parsing, not a production code path.</summary>
    static int ExtractFrameCount(string line, string name)
    {
        int idx = line.IndexOf(name, StringComparison.Ordinal);
        if (idx < 0) return -1;
        int mul = line.IndexOf('×', idx);
        if (mul < 0) return -1;
        int start = mul + 1, end = start;
        while (end < line.Length && char.IsDigit(line[end])) end++;
        return int.TryParse(line.AsSpan(start, end - start), out int n) ? n : -1;
    }
}

/// <summary>One <c>FrameClock.Tick</c> poller behind a gate, mirroring the real app's ternary-gate shape — so flipping
/// <see cref="Show"/> unmounts it exactly the way a gate going false does in production.</summary>
sealed class SeenPollerGateRoot : Component
{
    public readonly Signal<bool> Show = new(true);
    public override Element Render()
    {
        if (!Show.Value) return new BoxEl { Width = 10, Height = 10, Children = [] };
        return new BoxEl { Width = 10, Height = 10, Children = [Embed.Comp(() => new FrameClockReadProbe())] };
    }
}
