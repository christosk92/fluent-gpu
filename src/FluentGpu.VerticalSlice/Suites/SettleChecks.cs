using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using FluentGpu.Controls;
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

namespace FluentGpu.VerticalSlice.Suites;

/// <summary>
/// EP11 / E17c — keyed measurement and the settle loop. The keyed <c>Responsive.Of</c> overloads (ungated and state-gated) build a
/// remounted region ONCE, at its remembered width, where the unkeyed box builds at its fallback and again at the measured width;
/// a whole transaction (freeze, capture, the apply remounts a page of keyed regions under a scope, settle, thaw) settles in its
/// first pass with no present while it runs; a page whose regions are unkeyed still settles, one corrective pass later; and a
/// settle pass that only waits (here: for the fonts) adds no hot-phase allocation.
/// </summary>
static class SettleChecks
{
    public static void Run(StringTable strings)
    {
        ResponsiveChecks(strings);
        TransactionChecks(strings, keyed: true);
        TransactionChecks(strings, keyed: false);
        WaitAllocCheck(strings);
        MeasureMemory.Clear();
    }

    // ── pages ─────────────────────────────────────────────────────────────────────────────────────────────────────────

    sealed class Page : Component
    {
        public readonly Signal<bool> Show = new(true);
        public bool Keyed = true;
        public bool StateGated;
        public string Scope = "tab0/page:1/";
        public readonly List<float> Builds = new();
        public readonly List<float> Measured = new();

        public override Element Render()
        {
            Element body = Show.Value
                ? new BoxEl
                {
                    Direction = 1, Width = 640f,
                    Children =
                    [
                        Region(),
                        Embed.Comp(() => new MeasuredRegion(Keyed, Measured)),
                    ],
                }
                : new BoxEl { Width = 640f, Height = 10f };
            return Ctx.Provide(MeasureMemory.Scope, Scope, new BoxEl { Direction = 1, Width = 640f, Height = 400f, Children = [body] });
        }

        Element Region()
        {
            Element Build(float w)
            {
                Builds.Add(w);
                return new BoxEl { Direction = (byte)(w > 500f ? 0 : 1), Height = 40f, Children = [new BoxEl { Width = 20f, Height = 20f }] };
            }
            if (StateGated)
                return Keyed ? Responsive.Of("region", 1, (int _, float w) => Build(w), fallback: 900f)
                             : Responsive.Of(1, (int _, float w) => Build(w), fallback: 900f);
            return Keyed ? Responsive.Of("region", Build, fallback: 900f) : Responsive.Of(Build, fallback: 900f);
        }
    }

    sealed class MeasuredRegion : Component
    {
        readonly bool _keyed;
        readonly List<float> _seen;
        public MeasuredRegion(bool keyed, List<float> seen) { _keyed = keyed; _seen = seen; }

        public override Element Render()
        {
            float w = (_keyed ? UseMeasuredWidth("columns", 4f) : UseMeasuredWidth(4f)).Value;
            _seen.Add(w);
            return new BoxEl { Height = 30f, Direction = (byte)(w > 500f ? 0 : 1), Children = [new BoxEl { Width = 10f, Height = 10f }] };
        }
    }

    static (AppHost host, HeadlessWindow window, HeadlessPlatformApp app, HeadlessGpuDevice device) Mount(StringTable strings, Component root)
    {
        var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("settle", new Size2(700, 480), 1f));
        window.Show();
        var device = new HeadlessGpuDevice();
        var host = new AppHost(app, window, device, new HeadlessFontSystem(strings), strings, root);
        for (int i = 0; i < 3; i++) host.Paint(0);
        return (host, window, app, device);
    }

    static int PaintUntil(AppHost host, Task t, int max = 64)
    {
        int n = 0;
        while (!t.IsCompleted && n < max) { host.Paint(0); n++; }
        return n;
    }

    static SettleResult? ResultOf(Task<SettleResult> t) => t.IsCompletedSuccessfully ? t.GetAwaiter().GetResult() : null;

    // ── gate.settle.keyed-responsive ──────────────────────────────────────────────────────────────────────────────────

    static void ResponsiveChecks(StringTable strings)
    {
        foreach (bool stateGated in new[] { false, true })
        {
            foreach (bool keyed in new[] { true, false })
            {
                MeasureMemory.Clear();
                var page = new Page { Keyed = keyed, StateGated = stateGated };
                var (host, _, app, _) = Mount(strings, page);
                using (app) using (host)
                {
                    page.Show.Value = false;
                    for (int i = 0; i < 2; i++) host.Paint(0);
                    page.Builds.Clear();
                    page.Show.Value = true;
                    for (int i = 0; i < 4; i++) host.Paint(0);
                    string kind = stateGated ? "state-gated" : "ungated";
                    if (keyed)
                        Check($"gate.settle.keyed-responsive-{kind} a keyed Responsive.Of that remounts under the same scope builds ONCE, at its remembered width (not its 900 fallback)",
                            page.Builds.Count == 1 && page.Builds[0] == 640f, $"builds=[{string.Join(",", page.Builds)}]");
                    else
                        Check($"gate.settle.unkeyed-responsive-{kind} (control) the unkeyed box builds at its fallback, then again at the measured width",
                            page.Builds.Count == 2 && page.Builds[0] == 900f && page.Builds[1] == 640f, $"builds=[{string.Join(",", page.Builds)}]");
                }
            }
        }
    }

    // ── gate.settle.transaction ───────────────────────────────────────────────────────────────────────────────────────

    static void TransactionChecks(StringTable strings, bool keyed)
    {
        MeasureMemory.Clear();
        var page = new Page { Keyed = keyed };
        var (host, _, app, device) = Mount(strings, page);
        using (app) using (host)
        {
            page.Show.Value = false;   // the page as the outgoing theme left it: its regions were measured and remembered
            for (int i = 0; i < 2; i++) host.Paint(0);
            var sc = device.PrimarySwapchain!;

            var token = host.Freeze();
            host.Paint(0);                                    // the capture frame
            int presents0 = sc.PresentCount;
            page.Show.Value = true;                           // the apply: the page remounts behind the frozen frame
            page.Builds.Clear();
            page.Measured.Clear();
            var settle = host.Settle();
            PaintUntil(host, settle);
            var r = ResultOf(settle);
            bool held = sc.PresentCount == presents0;
            host.Thaw(token, DissolveSpec.Cut);
            for (int i = 0; i < 3 && host.FreezeState != FreezePhase.None; i++) host.Paint(0);

            if (keyed)
                Check("gate.settle.transaction-keyed freeze, capture, a page of keyed regions remounts, Settle: quiet in its FIRST pass (no work pass, no timeout), every region built once at its final width, nothing presented while it ran, and the thaw completes",
                    r is { TimedOut: false, Cut: false, Passes: 1, WorkPasses: 0 } && held && page.Builds.Count == 1 && page.Builds[0] == 640f
                    && page.Measured.Count == 1 && page.Measured[0] == 640f && host.FreezeState == FreezePhase.None,
                    $"passes={r?.Passes} work={r?.WorkPasses} timedOut={r?.TimedOut} held={held} builds=[{string.Join(",", page.Builds)}] measured=[{string.Join(",", page.Measured)}]");
            else
                Check("gate.settle.transaction-unkeyed the same transaction with unkeyed regions still settles within the cap: one corrective work pass, then quiet, at the final widths",
                    r is { TimedOut: false, Cut: false, Passes: 2, WorkPasses: 1 } && held && page.Builds.Count == 2 && page.Builds[^1] == 640f
                    && page.Measured[^1] == 640f,
                    $"passes={r?.Passes} work={r?.WorkPasses} timedOut={r?.TimedOut} held={held} builds=[{string.Join(",", page.Builds)}] measured=[{string.Join(",", page.Measured)}]");
        }
    }

    // ── gate.settle.wait-zero-alloc ───────────────────────────────────────────────────────────────────────────────────

    static void WaitAllocCheck(StringTable strings)
    {
        MeasureMemory.Clear();
        var page = new Page();
        var (host, _, app, _) = Mount(strings, page);
        using (app) using (host)
        {
            bool ready = false;
            using var snap = SnapScope.Enter(host);
            var settle = host.Settle(new SettleSpec { FontsReady = () => ready });
            for (int i = 0; i < 3; i++) host.Paint(0);       // warm: the passes settle into waiting
            long worst = 0;
            for (int i = 0; i < 8; i++) { var f = host.Paint(0); if (f.HotPhaseAllocBytes > worst) worst = f.HotPhaseAllocBytes; }
            bool waited = !settle.IsCompleted;
            ready = true;
            PaintUntil(host, settle);
            var r = ResultOf(settle);
            Check("gate.settle.wait-zero-alloc settle passes that only wait (the fonts are not ready) add no hot-phase allocation, and the settle completes once they are",
                worst == 0 && waited && r is { TimedOut: false }, $"worst hot-phase alloc={worst}B waited={waited} timedOut={r?.TimedOut}");
        }
    }
}
