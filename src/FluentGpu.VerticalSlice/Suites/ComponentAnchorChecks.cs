using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using FluentGpu.Forms;
using FluentGpu.Media;
using FluentGpu.Pal;
using FluentGpu.Input;
using FluentGpu.Layout;
using FluentGpu.Controls;
using FluentGpu.Render;
using FluentGpu.Rhi;
using FluentGpu.Text;
using static FluentGpu.Dsl.Ui;
using System;
using FluentGpu.Animation;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Hosting;
using FluentGpu.Pal.Headless;
using FluentGpu.Reconciler;
using FluentGpu.Rhi.Headless;
using FluentGpu.Scene;
using FluentGpu.Scroll.Effects;
using FluentGpu.Scroll.Runtime;
using FluentGpu.Signals;
using FluentGpu.Text.Headless;
using FluentGpu.VerticalSlice.Harness;
using static FluentGpu.VerticalSlice.Harness.Gate;
using static FluentGpu.VerticalSlice.Harness.Asserts;

namespace FluentGpu.VerticalSlice.Suites;

/// <summary>
/// E14 (home-redesign-remediation.md §2, Appendix W0): a <see cref="ComponentEl"/> anchor never ran
/// <c>Reconciler.WriteColumns</c> — <c>Mount</c> returns at <c>MountComponent</c> before reaching it, and the
/// <c>Update</c> reuse branch (a live component is autonomous) returned early too — so every base-<see cref="Element"/>
/// prop authored directly on an <c>Embed.Comp(...)</c> record (<c>.Sticky</c>, <c>Visible</c>, <c>Enter</c>/<c>Exit</c>,
/// …) was silently dropped. <c>Reconciler.WriteAnchorColumns</c> fixes this from both paths; these four gates pin the
/// contract in isolation (the realized-row/virtualized-list end-to-end shape is
/// <c>gate.scroll-effects.sticky-in-realized-row.componentel</c>, <see cref="ZoneListSpikeChecks"/>).
/// </summary>
static class ComponentAnchorChecks
{
    public static void Run(StringTable strings)
    {
        var fonts = new HeadlessFontSystem(strings);
        StickyAnchorChecks(strings, fonts);
        VisibleAnchorChecks(strings, fonts);
        ExitAnchorChecks(strings, fonts);
        AllocAnchorChecks(strings, fonts);
    }

    // ══ 1. gate.reconcile.componentel.sticky — `.Sticky(0)` on the Embed.Comp itself pins ═══════════════════════════

    sealed class StickyLeaf : Component
    {
        public override Element Render() => new BoxEl { Height = 48f, Fill = ColorF.FromRgba(30, 34, 40) };
    }

    sealed class StickyAnchorProbe : Component
    {
        public const float Top = 100f;
        public required Signal<bool> Pinned;
        public override Element Render() => new ScrollEl
        {
            Width = 400f, Height = 300f,
            Content = new BoxEl
            {
                Direction = 1, MinWidth = 0f,
                Children =
                [
                    new BoxEl { Height = Top },
                    Embed.Comp(() => new StickyLeaf()).Sticky(0f, engaged: Pinned),
                    new BoxEl { Height = 4000f },
                ],
            },
        };
    }

    static void StickyAnchorChecks(StringTable strings, HeadlessFontSystem fonts)
    {
        var pinned = new Signal<bool>(false);
        using var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("componentel-sticky", new Size2(400, 300), 1f));
        window.Show();
        using var host = new AppHost(app, window, new HeadlessGpuDevice(), fonts, strings, new StickyAnchorProbe { Pinned = pinned });
        host.RunFrame(); host.RunFrame();
        var vp = FindScrollNode(host.Scene, host.Scene.Root);
        var handle = host.TryGetScrollHandle(vp)!;

        handle.ScrollTo(50.0, ScrollMove.Immediate);
        for (int i = 0; i < 3; i++) host.RunFrame();
        bool notPinnedBefore = !pinned.Peek();

        handle.ScrollTo(250.0, ScrollMove.Immediate);
        for (int i = 0; i < 3; i++) host.RunFrame();
        bool pinnedAfter = pinned.Peek();

        handle.ScrollTo(50.0, ScrollMove.Immediate);
        for (int i = 0; i < 3; i++) host.RunFrame();
        bool releasedBack = !pinned.Peek();

        Check("gate.reconcile.componentel.sticky a `.Sticky(0)` on Embed.Comp(...) pins: the ComponentEl anchor itself (no wrapper BoxEl) engages the sticky edge once scrolled past its natural offset and releases scrolling back",
            notPinnedBefore && pinnedAfter && releasedBack,
            $"before={notPinnedBefore} after={pinnedAfter} back={releasedBack}");
    }

    // ══ 2. gate.reconcile.componentel.visible — Visible=false collapses the anchor ════════════════════════════════

    sealed class VisAnchorLeaf : Component
    {
        public NodeHandle Anchor;
        public override Element Render()
        {
            Anchor = Context.AnchorNode;   // this component's own anchor == the node WriteAnchorColumns writes to
            return new BoxEl { Width = 40f, Height = 20f };
        }
    }

    sealed class VisibleStaticAnchorProbe : Component
    {
        public VisAnchorLeaf? Leaf;
        public NodeHandle Last;
        public override Element Render() => new BoxEl
        {
            Direction = 0, Width = 300f, Height = 40f, Gap = 10f,
            Children =
            [
                new BoxEl { Width = 40f, Height = 20f },
                Embed.Comp(() => Leaf = new VisAnchorLeaf()) with { Visible = false },
                new BoxEl { Width = 40f, Height = 20f, OnRealized = h => Last = h },
            ],
        };
    }

    sealed class VisibleBoundAnchorProbe : Component
    {
        public readonly Signal<bool> Shown = new(true);
        public VisAnchorLeaf? Leaf;
        public NodeHandle Last;
        public override Element Render() => new BoxEl
        {
            Direction = 0, Width = 300f, Height = 40f, Gap = 10f,
            Children =
            [
                new BoxEl { Width = 40f, Height = 20f },
                Embed.Comp(() => Leaf = new VisAnchorLeaf()) with { Visible = Prop.Of(() => Shown.Value) },
                new BoxEl { Width = 40f, Height = 20f, OnRealized = h => Last = h },
            ],
        };
    }

    static void VisibleAnchorChecks(StringTable strings, HeadlessFontSystem fonts)
    {
        // ── static Visible=false on mount collapses the anchor itself (not merely its rendered child) ──
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("componentel-visible-static", new Size2(300, 60), 1f));
            window.Show();
            var probe = new VisibleStaticAnchorProbe();
            using var host = new AppHost(app, window, new HeadlessGpuDevice(), fonts, strings, probe);
            host.RunFrame();
            var anchor = probe.Leaf!.Anchor;
            bool collapsed = !anchor.IsNull && host.Scene.IsCollapsed(anchor);
            float lastX = host.Scene.AbsoluteRect(probe.Last).X;
            Check("gate.reconcile.componentel.visible.static a statically Visible=false Embed.Comp(...) collapses its OWN anchor at mount and its sibling closes the flow gap (no reserved gap slot)",
                collapsed && Near(lastX, 50f), $"collapsed={collapsed} lastX={lastX:0.#}(exp 50)");
        }

        // ── bound flip collapses/restores the anchor with no component re-render (bind-scoped, like Fill/Opacity) ──
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("componentel-visible-bound", new Size2(300, 60), 1f));
            window.Show();
            var probe = new VisibleBoundAnchorProbe();
            using var host = new AppHost(app, window, new HeadlessGpuDevice(), fonts, strings, probe);
            host.RunFrame();
            var anchor = probe.Leaf!.Anchor;
            bool visibleBefore = !anchor.IsNull && !host.Scene.IsCollapsed(anchor);
            float lastXBefore = host.Scene.AbsoluteRect(probe.Last).X;

            probe.Shown.Value = false;
            var f = host.RunFrame();
            bool collapsedAfter = host.Scene.IsCollapsed(anchor);
            float lastXAfter = host.Scene.AbsoluteRect(probe.Last).X;
            bool scoped = f.ComponentsRendered == 0;

            probe.Shown.Value = true;
            host.RunFrame();
            bool restoredAfter = !host.Scene.IsCollapsed(anchor);

            Check("gate.reconcile.componentel.visible a bound Visible on Embed.Comp(...) collapses/restores the ANCHOR node itself (closing the flow gap) with NO component re-render — E14's previously-dropped Visible channel, wired via BindNode at mount",
                visibleBefore && Near(lastXBefore, 100f) && collapsedAfter && Near(lastXAfter, 50f) && scoped && restoredAfter,
                $"before(visible={visibleBefore} lastX={lastXBefore:0.#}) after(collapsed={collapsedAfter} lastX={lastXAfter:0.#} scoped={scoped}) restored={restoredAfter}");
        }
    }

    // ══ 3. gate.reconcile.componentel.exit — a keyed Embed.Comp with Exit orphan-fades ═══════════════════════════

    sealed class ExitLeaf : Component
    {
        public override Element Render() => new BoxEl { Width = 40f, Height = 40f };
    }

    /// <summary>Mirrors <c>AnimSuite.FlipCellProbe</c> exactly, except the keyed/animated node is the <see cref="ComponentEl"/>
    /// anchor itself (not a BoxEl child) — the exact shape E14 dropped: <c>Reconciler.Remove</c> reads
    /// <c>Anim.TryGetTransition(node, ...)</c>, which was never seeded for a component anchor pre-fix, so the old
    /// instance vanished instantly instead of orphan-fading.</summary>
    sealed class ExitAnchorProbe : Component
    {
        public readonly Signal<int> Value = new(0);
        public override Element Render()
        {
            int v = Value.Value;
            return new BoxEl
            {
                Width = 40f, Height = 40f, ClipToBounds = true,
                Children =
                [
                    Embed.Comp(() => new ExitLeaf()) with
                    {
                        Key = "d" + v,
                        Enter = new EnterExit(Dy: 14f, Opacity: 0f, Active: true),
                        Exit = new EnterExit(Dy: -14f, Opacity: 0f, Active: true),
                        Transition = MotionTok.ControlFast,
                    },
                ],
            };
        }
    }

    static void ExitAnchorChecks(StringTable strings, HeadlessFontSystem fonts)
    {
        using var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("componentel-exit", new Size2(200, 120), 1f));
        window.Show();
        var probe = new ExitAnchorProbe();
        using var host = new AppHost(app, window, new HeadlessGpuDevice(), fonts, strings, probe);
        for (int i = 0; i < 4; i++) host.RunFrame();

        int settle0 = 0;
        for (; settle0 < 400 && host.HasActiveWork; settle0++) host.RunFrame();
        bool quietBefore = !host.HasActiveWork && host.Animation.TrackCount == 0 && host.Scene.OrphanCount == 0;

        probe.Value.Value = 1;   // key mismatch ⇒ the old ComponentEl anchor orphans; the new one mounts + enters
        host.RunFrame();
        bool armed = host.Animation.TrackCount > 0 || host.Scene.OrphanCount > 0;

        int settle = 0;
        for (; settle < 400 && host.HasActiveWork; settle++) host.RunFrame();
        bool quietAfter = !host.HasActiveWork;
        int tracks = host.Animation.TrackCount, orphans = host.Scene.OrphanCount;

        Check("gate.reconcile.componentel.exit a keyed Embed.Comp(...) carrying Exit orphan-fades on removal (the old anchor is held + animated out, not hard-removed) and the exit track + orphan both retire once the fade settles",
            quietBefore && armed && quietAfter && tracks == 0 && orphans == 0,
            $"quietBefore={quietBefore} armed={armed} quietAfter={quietAfter} tracks={tracks} orphans={orphans} settleFrames={settle}(cap 400)");
    }

    // ══ 4. gate.reconcile.componentel.alloc — steady reuse cost from WriteAnchorColumns does not grow ═════════════

    sealed class AllocLeaf : Component
    {
        public override Element Render() => new BoxEl { Width = 20f, Height = 20f };
    }

    sealed class AllocAnchorProbe : Component
    {
        public readonly Signal<int> Tick = new(0);
        static readonly EnterExit s_exit = new(Opacity: 0f, Active: true);

        public override Element Render()
        {
            _ = Tick.Value;   // subscribe: bumping Tick re-renders this component, hitting the ComponentEl REUSE branch
            return new BoxEl
            {
                Direction = 1,
                Children =
                [
                    Embed.Comp(() => new AllocLeaf()).Sticky(0f) with { Visible = true, Exit = s_exit, Transition = MotionTok.ControlFaster },
                ],
            };
        }
    }

    static void AllocAnchorChecks(StringTable strings, HeadlessFontSystem fonts)
    {
        using var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("componentel-alloc", new Size2(200, 100), 1f));
        window.Show();
        var probe = new AllocAnchorProbe();
        using var host = new AppHost(app, window, new HeadlessGpuDevice(), fonts, strings, probe);
        host.RunFrame(); host.RunFrame();   // mount + settle

        var samples = new long[6];
        for (int i = 0; i < samples.Length; i++)
        {
            probe.Tick.Value = i + 1;
            long before = GC.GetAllocatedBytesForCurrentThread();
            host.RunFrame();
            samples[i] = GC.GetAllocatedBytesForCurrentThread() - before;
        }
        // Steady: no growth once warmed — every capacity WriteAnchorColumns' side tables (_morphKeyByNode, _relativeKey,
        // _childStagger, the interact-targets/scroll-effects rows) touch has grown by the third sample, so a LATER
        // sample exceeding it would mean an unbounded per-reconcile cost, not a one-time warm-up.
        bool steady = samples[^1] <= samples[2];
        Check("gate.reconcile.componentel.alloc reusing a ComponentEl anchor across repeated parent re-renders (WriteAnchorColumns runs on every reuse) allocates a STABLE amount per reconcile — no per-frame growth from the new anchor-side tables",
            steady, "samples=" + string.Join("/", samples));
    }
}
