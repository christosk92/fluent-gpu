using System;
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
/// E7 (EP13): <c>Flow.Persist</c>. Gates: a root swap re-parents the persisted subtree (same component instance and state) and
/// renders it exactly once, against the new root's provider, in the swap frame; a slot that leaves with no adopter parks its
/// subtree (inactive, not unmounted), steady frames with a parked subtree allocate nothing on the hot phases, and the parked
/// subtree unmounts once the park time has passed. The matrix (keyed moves, key changes, duplicates, freeze, video) lives in
/// Engine.Tests/PersistSubtreeTests.
/// </summary>
static class PersistSubtreeSuite
{
    static readonly Context<string> Variant = new("none");

    sealed class Leaf : Component
    {
        public static int Instances, Unmounts, Renders;
        public static string? Seen;
        public static IReadSignal<bool>? Active;
        public static Signal<int>? Clicks;

        sealed class Tracker : IDisposable { public void Dispose() => Unmounts++; }

        public Leaf() => Instances++;

        public override Element Render()
        {
            Renders++;
            UseDisposable(() => new Tracker());
            Seen = UseContext(Variant);
            Active = UseIsActive();
            Clicks = UseSignal(0);
            return new BoxEl { Width = 80f, Height = 40f, Fill = ColorF.FromRgba(40, 40, 60) };
        }
    }

    sealed class Root : Component
    {
        public readonly Signal<int> Shape = new(0);
        public readonly Signal<bool> Present = new(true);

        public override Element Render()
        {
            int shape = Shape.Value;
            Element[] kids = Present.Value ? [Flow.Persist("stage", static () => Embed.Comp(static () => new Leaf()))] : [];
            return shape == 0
                ? Ctx.Provide(Variant, "classic", new BoxEl { Width = 300f, Height = 200f, Children = kids })
                : new GridEl { Width = 300f, Height = 200f, Children = [Ctx.Provide(Variant, "zune", new BoxEl { Children = kids })] };
        }
    }

    public static void Run(StringTable strings)
    {
        Leaf.Instances = Leaf.Unmounts = Leaf.Renders = 0;
        using var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("persist-subtrees", new Size2(300, 200), 1f));
        window.Show();
        var clock = new ManualFrameTimeSource();
        var root = new Root();
        using var host = new AppHost(app, window, new HeadlessGpuDevice(), new HeadlessFontSystem(strings), strings, root, frameTime: clock);
        for (int i = 0; i < 3; i++) host.RunFrame();

        Leaf.Clicks!.Value = 7;                          // component state the swap must keep
        host.RunFrame();
        int renders = Leaf.Renders;
        root.Shape.Value = 1;                            // a different root element type: removed, then mounted
        host.RunFrame();
        Check("gate.persist.adopt a root swap keeps the persisted component (one instance, its state) and renders it exactly once against the new provider",
            Leaf.Instances == 1 && Leaf.Unmounts == 0 && Leaf.Clicks!.Peek() == 7 && Leaf.Seen == "zune" && Leaf.Renders - renders == 1
            && Leaf.Active!.Peek() && !host.IsPersistParked("stage"),
            $"instances={Leaf.Instances} unmounts={Leaf.Unmounts} clicks={Leaf.Clicks!.Peek()} seen={Leaf.Seen} renders={Leaf.Renders - renders}");

        root.Present.Value = false;                      // no adopter
        host.RunFrame();
        Check("gate.persist.park a slot that leaves with no adopter parks its subtree: inactive, not unmounted",
            host.IsPersistParked("stage") && Leaf.Unmounts == 0 && !Leaf.Active!.Peek(),
            $"parked={host.IsPersistParked("stage")} unmounts={Leaf.Unmounts} active={Leaf.Active!.Peek()}");

        for (int i = 0; i < 6; i++) { clock.Advance(16f); host.Paint(); }
        long worst = 0;
        for (int i = 0; i < 16; i++) { clock.Advance(16f); var f = host.Paint(); if (f.HotPhaseAllocBytes > worst) worst = f.HotPhaseAllocBytes; }
        Check("gate.persist.zero-alloc steady frames with a parked subtree allocate nothing on the hot phases", worst == 0, $"worst={worst}B");

        // Past the park time: the host timer unmounts it. Headless, the timer clock moves only on a painted frame, so paint (a real
        // window's wall clock makes the due timer itself the wake).
        clock.Advance(Flow.PersistParkMs);
        host.Paint();
        host.Paint();
        Check("gate.persist.expire a parked subtree with no adopter is unmounted after Flow.PersistParkMs",
            !host.IsPersistParked("stage") && Leaf.Unmounts == 1, $"parked={host.IsPersistParked("stage")} unmounts={Leaf.Unmounts}");

        root.Present.Value = true;
        host.RunFrame();
        Check("gate.persist.fresh a slot after the expiry builds fresh content", Leaf.Instances == 2 && Leaf.Seen == "zune",
            $"instances={Leaf.Instances} seen={Leaf.Seen}");
    }
}
