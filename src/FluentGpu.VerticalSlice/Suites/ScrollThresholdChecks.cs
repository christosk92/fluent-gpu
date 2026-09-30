using System;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Hosting;
using FluentGpu.Pal;
using FluentGpu.Pal.Headless;
using FluentGpu.Rhi.Headless;
using FluentGpu.Scene;
using FluentGpu.Scroll.Runtime;
using FluentGpu.Text.Headless;
using static FluentGpu.VerticalSlice.Harness.Gate;
using static FluentGpu.VerticalSlice.Harness.Asserts;

// Wavee Home's compact facet band (docs/plans/wavee/home-redesign-implementation.md §E6): RenderContext.UseScrollThreshold
// flips shown once the nearest scroller's offset passes ENTER (64), and back to hidden once it drops below EXIT (56) —
// the dead band between the two holds whichever state is current. This suite scrolls a pinned ScrollEl across both
// thresholds and asserts: (1) the hook's own value matches ScrollThreshold.Next at every probed offset, (2) the reader
// component's effect — subscribed to the hook's returned signal — fires ONLY on an actual flip (never while offset moves
// inside the dead band, never on every scrolled frame), and (3) steady-state scrolling inside the band allocates nothing.
static class ScrollThresholdChecks
{
    const double Enter = 64.0, Exit = 56.0;

    // Shared mutable state a probe's descendant hook-reader writes into and the test reads back — the same idiom as a
    // captured `Action` callback, just batched into one object so the propless `Embed.Comp(() => new Reader(state))`
    // factory (frozen at mount, component-props-contract.md) only closes over ONE stable reference.
    sealed class ThresholdState
    {
        public bool Current;
        public int FlipCount;   // effect runs: 1 at mount + 1 per subsequent flip (Memo's equality cut-off gates the rest)
    }

    sealed class ThresholdReader : Component
    {
        private readonly ThresholdState _state;
        public ThresholdReader(ThresholdState state) => _state = state;

        public override Element Render()
        {
            var shown = UseScrollThreshold(Enter, Exit);   // handle: null — resolves the nearest scroller (this ScrollEl)
            UseEffect(() =>
            {
                _state.Current = shown.Value;   // subscribes: re-runs only when the memo actually notifies (a flip)
                _state.FlipCount++;
            });
            return new BoxEl { Width = 4f, Height = 4f };
        }
    }

    sealed class ThresholdProbeRoot : Component
    {
        private readonly ThresholdState _state;
        public ThresholdProbeRoot(ThresholdState state) => _state = state;

        public override Element Render() => new ScrollEl
        {
            Width = 300f, Height = 300f,
            Content = new BoxEl
            {
                Direction = 1, MinWidth = 0f,
                Children =
                [
                    Embed.Comp(() => new ThresholdReader(_state)),
                    new BoxEl { Height = 4000f },   // ample extent — every probed offset stays well short of the max
                ],
            },
        };
    }

    public static void Run(StringTable strings)
    {
        var fonts = new HeadlessFontSystem(strings);
        var state = new ThresholdState();
        using var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("scroll-threshold", new FluentGpu.Foundation.Size2(400, 400), 1f));
        window.Show();
        using var host = new AppHost(app, window, new HeadlessGpuDevice(), fonts, strings, new ThresholdProbeRoot(state));
        host.RunFrame();
        host.RunFrame();   // drain the mount-time passive effect (FlipCount==1, Current==false, the hidden rest state)

        var vp = FindScrollNode(host.Scene, host.Scene.Root);
        var handle = host.TryGetScrollHandle(vp)!;

        void ScrollTo(double offset)
        {
            handle.ScrollTo(offset, FluentGpu.Scroll.Runtime.ScrollMove.Immediate);
            host.RunFrame();
            host.RunFrame();   // drain the resulting passive effect, if any
        }

        Check("gate.hooks.scroll-threshold at-rest (offset 0) is hidden and the reader ran exactly once (mount)",
            !state.Current && state.FlipCount == 1,
            $"current={state.Current} flipCount={state.FlipCount}");

        // Sweep the dead band and just below ENTER: never flips (hidden the whole way).
        int flipsBefore = state.FlipCount;
        foreach (var offset in new[] { 10.0, 30.0, 56.0, 60.0, 63.0, Enter })
        {
            ScrollTo(offset);
            Check($"gate.hooks.scroll-threshold offset={offset} (<= enter) stays hidden, no effect re-run",
                !state.Current && state.FlipCount == flipsBefore,
                $"offset={offset} current={state.Current} flipCount={state.FlipCount} expected={flipsBefore}");
        }

        // Cross ENTER strictly: flips to shown, exactly one re-run.
        ScrollTo(Enter + 0.5);
        Check("gate.hooks.scroll-threshold crossing strictly past enter (64) flips to shown with exactly one effect re-run",
            state.Current && state.FlipCount == flipsBefore + 1,
            $"current={state.Current} flipCount={state.FlipCount} expected={flipsBefore + 1}");
        flipsBefore = state.FlipCount;

        // Oscillate inside the dead band [Exit, Enter] while shown: must hold shown with ZERO further effect re-runs —
        // "no signal writes while scrolling inside the band."
        foreach (var offset in new[] { 60.0, 57.0, 63.0, Exit, Enter, 58.0 })
        {
            ScrollTo(offset);
            Check($"gate.hooks.scroll-threshold offset={offset} inside the dead band holds shown, no effect re-run",
                state.Current && state.FlipCount == flipsBefore,
                $"offset={offset} current={state.Current} flipCount={state.FlipCount} expected={flipsBefore}");
        }

        // Drop strictly below EXIT: flips back to hidden, exactly one re-run.
        ScrollTo(Exit - 0.5);
        Check("gate.hooks.scroll-threshold dropping strictly below exit (56) flips to hidden with exactly one effect re-run",
            !state.Current && state.FlipCount == flipsBefore + 1,
            $"current={state.Current} flipCount={state.FlipCount} expected={flipsBefore + 1}");

        // Cross-check the hook's live value against the pure rule directly, at a handful of offsets, replaying the same
        // prev-state thread the hook itself carries (ScrollThreshold.Next is the ENGINE of UseScrollThreshold).
        bool predicted = false;
        foreach (var offset in new[] { 0.0, 40.0, Enter, Enter + 1.0, 58.0, Exit, Exit - 1.0, 90.0 })
        {
            predicted = ScrollThreshold.Next(predicted, offset, Enter, Exit);
            ScrollTo(offset);
            Check($"gate.hooks.scroll-threshold hook value matches the pure rule at offset={offset}",
                state.Current == predicted, $"offset={offset} hook={state.Current} pure={predicted}");
        }

        // Zero-alloc steady state: warm the hot ScrollTo/RunFrame loop, then measure a run entirely INSIDE the dead band
        // (no flip, so no signal write — this is the "no new per-frame work for a threshold subscriber" claim).
        ScrollTo(60.0);   // land inside the band, shown
        for (int i = 0; i < 16; i++) ScrollTo(60.0 + (i % 2 == 0 ? 0.1 : -0.1));   // warm JIT/caches
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 64; i++) ScrollTo(60.0 + (i % 2 == 0 ? 0.1 : -0.1));
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Check("gate.hooks.scroll-threshold-alloc-zero scrolling inside the dead band allocates nothing across 64 steps",
            allocated == 0, $"allocated={allocated}");
    }
}
