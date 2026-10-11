using System;
using System.Collections.Generic;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Hosting;
using FluentGpu.Hosting.Tiers;
using FluentGpu.Pal;
using FluentGpu.Pal.Headless;
using FluentGpu.Rhi.Headless;
using FluentGpu.Scroll.Runtime;
using FluentGpu.Text.Headless;
using static FluentGpu.VerticalSlice.Harness.Gate;
using static FluentGpu.VerticalSlice.Harness.Asserts;

namespace FluentGpu.VerticalSlice.Suites;

/// <summary>
/// E22 window tiers (technical-plan §3): the pure <see cref="TierLadder"/> rules, the zero-allocation gates for
/// <c>Resolve</c> and the host's pending-tier commit, a first mount that resolves in its own frame, the tier-gated seeded
/// <c>Responsive.Of(key, ladder, estimate, build)</c>, and the resize-while-scrolled-to-the-end-of-a-virtual-list path
/// (the in-frame global flush that makes a plain layout-phase write snap): the tier must stay pending through that flush
/// and cross animated on the next frame. The engine-side crossing matrix lives in <c>Engine.Tests/Tiers/</c>.
/// </summary>
static class TiersSuite
{
    public static void Run(StringTable strings)
    {
        LadderChecks();
        ZeroAllocChecks(strings);
        FirstFrameChecks(strings);
        ResponsiveTierChecks(strings);
        VirtualListScrollEndChecks(strings);
    }

    // ── fixtures ──────────────────────────────────────────────────────────────────────────────────────────────────

    static readonly TierLadder Narrow600 = TierLadder.Of(new TierEdge(600f));

    /// <summary>UseTier on the viewport width + a keyed rail/card that FLIP with the tier. Optional virtual list below.</summary>
    sealed class TierProbe : Component
    {
        readonly bool _withList;
        public NodeHandle Rail, Card;
        public readonly List<int> Seen = new();
        public TierProbe(bool withList = false) => _withList = withList;

        public override Element Render()
        {
            int tier = UseTier(Narrow600, TierSource.ViewportWidth).Value;
            Seen.Add(tier);
            bool wide = tier == 1;
            var header = new BoxEl
            {
                Direction = 0, Height = 60f,
                Children =
                [
                    new BoxEl
                    {
                        Key = "rail", Width = wide ? 220f : 56f, Shrink = 0f, Fill = ColorF.FromRgba(27, 32, 40),
                        Animate = LayoutTransition.BoundsT(SizeMode.Reveal), OnRealized = h => Rail = h,
                    },
                    new BoxEl
                    {
                        Key = "card", Grow = 1f, MinWidth = 0f, Fill = ColorF.FromRgba(57, 61, 69),
                        Animate = LayoutTransition.Slide, OnRealized = h => Card = h,
                    },
                ],
            };
            if (!_withList) return new BoxEl { Direction = 1, Grow = 1f, Children = [ header ] };
            return new BoxEl
            {
                Direction = 1, Grow = 1f,
                Children =
                [
                    header,
                    ItemsView.CreateBound(200, _ => new BoxEl { MinHeight = 40f, Fill = ColorF.FromRgba(30, 30, 30) },
                        RepeatLayout.Stack(40f), new ListOptions { Grow = 1f }),
                ],
            };
        }
    }

    sealed class ResponsiveProbe : Component
    {
        public static readonly TierLadder Ladder = TierLadder.Of(new TierEdge(500f));
        public float Estimate = 400f;
        public readonly List<int> Builds = new();

        public override Element Render() => new BoxEl
        {
            Direction = 1, Grow = 1f,
            Children =
            [
                Responsive.Of("tier.box", Ladder, () => Estimate, tier =>
                {
                    Builds.Add(tier);
                    return new BoxEl { Height = 40f, Fill = ColorF.FromRgba(40, 50, 60) };
                }),
            ],
        };
    }

    static (AppHost host, HeadlessWindow window, HeadlessPlatformApp app) Mount(StringTable strings, Component root, float w, float h = 320f, bool composited = true)
    {
        var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("tiers", new Size2(w, h), 1f, Composited: composited));
        window.Show();
        var host = new AppHost(app, window, new HeadlessGpuDevice(), new HeadlessFontSystem(strings), strings, root);
        host.RunFrame();
        return (host, window, app);
    }

    static void Frames(AppHost host, int n) { for (int i = 0; i < n; i++) host.RunFrame(); }

    static void Settle(AppHost host)
    {
        for (int i = 0; i < 400 && host.Animation.TrackCount > 0; i++)
        {
            double before = host.FrameClockMsForTest;
            host.RunFrame();
            if (host.FrameClockMsForTest == before) host.Paint(0);
        }
    }

    // ── gate.tiers.ladder ─────────────────────────────────────────────────────────────────────────────────────────

    static void LadderChecks()
    {
        var classic = TierLadder.Of(new TierEdge(528f, 0f, 40f), new TierEdge(1008f, 0f, 40f), new TierEdge(1920f, 40f, 0f), TierEdge.None);
        Check("gate.tiers.ladder-widen-hold Classic Small commits at 568 on widening, a fresh resolve at 528",
            classic.Resolve(567.9f, 0) == 0 && classic.Resolve(568f, 0) == 1 && classic.Resolve(528f, -1) == 1,
            $"567.9->{classic.Resolve(567.9f, 0)} 568->{classic.Resolve(568f, 0)} fresh528->{classic.Resolve(528f, -1)}");
        Check("gate.tiers.ladder-narrow-hold Classic Huge holds down to 1880, Small back below 528 with no hold",
            classic.Resolve(1880f, 3) == 3 && classic.Resolve(1879.9f, 3) == 2 && classic.Resolve(528f, 1) == 1 && classic.Resolve(527.9f, 1) == 0,
            $"1880->{classic.Resolve(1880f, 3)} 1879.9->{classic.Resolve(1879.9f, 3)}");
        Check("gate.tiers.ladder-absent-edge an absent Huge4K edge is never crossed; an empty ladder is tier 0",
            classic.Resolve(100000f, 3) == 3 && TierLadder.Empty.Resolve(5000f, -1) == 0);
    }

    // ── gate.tiers.*-zero-alloc ───────────────────────────────────────────────────────────────────────────────────

    static void ZeroAllocChecks(StringTable strings)
    {
        var ladder = TierLadder.Of(new TierAspectRule(0.8f, 1, Hold: 0.1f), new TierEdge(640f, 24f), new TierEdge(1008f, 24f), new TierEdge(1920f, 24f), TierEdge.None);
        int tier = -1;
        for (int i = 0; i < 128; i++) tier = ladder.Resolve(300f + (i % 64) * 40f, 1.6f, tier);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 8192; i++) tier = ladder.Resolve(300f + (i % 64) * 40f, 1.6f, tier);
        long resolveAlloc = GC.GetAllocatedBytesForCurrentThread() - before;
        Check("gate.tiers.resolve-zero-alloc TierLadder.Resolve (edges + aspect rule) allocates nothing across 8192 resolves",
            resolveAlloc == 0 && tier >= 0, $"allocated={resolveAlloc}B");

        int wakes = 0;
        var registry = new TierRegistry(() => wakes++);
        var slots = new TierSlot[16];
        for (int i = 0; i < slots.Length; i++) slots[i] = registry.Create();
        for (int r = 0; r < 4; r++)   // warm: the queue list grows once
        {
            for (int i = 0; i < slots.Length; i++) registry.Store(slots[i], (r & 1) + 1);
            registry.CommitPending();
        }
        before = GC.GetAllocatedBytesForCurrentThread();
        long written = 0;
        for (int r = 0; r < 1024; r++)
        {
            for (int i = 0; i < slots.Length; i++) registry.Store(slots[i], (r & 1) + 1);
            written += registry.CommitPending();
        }
        long commitAlloc = GC.GetAllocatedBytesForCurrentThread() - before;
        Check("gate.tiers.commit-zero-alloc storing 16 pending tiers and committing them (1024 rounds) allocates nothing; every changed tier is written once, none left pending",
            commitAlloc == 0 && written == 16 * 1024 && !registry.HasPending && wakes > 0,
            $"allocated={commitAlloc}B written={written} pending={registry.PendingCount}");

        var probe = new TierProbe();
        var (host, _, app) = Mount(strings, probe, 400f);
        using (app) using (host)
        {
            Frames(host, 6);
            long worst = 0;
            for (int i = 0; i < 16; i++) { var f = host.RunFrame(); if (f.HotPhaseAllocBytes > worst) worst = f.HotPhaseAllocBytes; }
            Check("gate.tiers.steady-frame-zero-alloc a mounted UseTier with no crossing adds no hot-phase allocation on steady frames",
                worst == 0, $"worst hot-phase alloc={worst}B");
        }
    }

    // ── gate.tiers.first-frame ────────────────────────────────────────────────────────────────────────────────────

    static void FirstFrameChecks(StringTable strings)
    {
        var probe = new TierProbe();
        var (host, _, app) = Mount(strings, probe, 900f);
        using (app) using (host)
        {
            Frames(host, 4);
            Check("gate.tiers.first-frame mounting at 900 wide resolves the wide tier in the FIRST render (no deferral, no corrective render), laid out wide from frame one",
                probe.Seen.Count == 1 && probe.Seen[0] == 1 && Near(host.Scene.AbsoluteRect(probe.Rail).W, 220f) && host.Animation.TrackCount == 0
                && !host.TiersForTest.HasPending,
                $"seen=[{string.Join(",", probe.Seen)}] railW={host.Scene.AbsoluteRect(probe.Rail).W:0.#} tracks={host.Animation.TrackCount}");
        }
    }

    // ── gate.tiers.responsive-* ───────────────────────────────────────────────────────────────────────────────────

    static void ResponsiveTierChecks(StringTable strings)
    {
        // Seeded by an estimate that agrees with the slot: one build in the first frame, never a corrective one.
        {
            var probe = new ResponsiveProbe { Estimate = 400f };
            var (host, window, app) = Mount(strings, probe, 400f);
            using (app) using (host)
            {
                bool firstFrame = probe.Builds.Count == 1 && probe.Builds[0] == 0;
                Frames(host, 6);
                bool noCorrection = probe.Builds.Count == 1;
                window.ClientSizePx = new Size2(450f, 320f);   // inside the tier: the box measures a new width and does NOT rebuild
                Frames(host, 6);
                bool insideTier = probe.Builds.Count == 1;
                window.ClientSizePx = new Size2(700f, 320f);   // crosses 500
                Frames(host, 8);
                Check("gate.tiers.responsive-gated Responsive.Of(key, ladder, estimate, build) builds once in the first frame from the estimate (no fixed fallback), never inside a tier, once per crossing",
                    firstFrame && noCorrection && insideTier && probe.Builds.Count == 2 && probe.Builds[1] == 1,
                    $"builds=[{string.Join(",", probe.Builds)}] first={firstFrame} stable={noCorrection} insideTier={insideTier}");
            }
        }

        // The estimate decides the first frame even when the slot later disagrees.
        {
            var probe = new ResponsiveProbe { Estimate = 1200f };   // the estimate says wide ...
            var (host, window, app) = Mount(strings, probe, 400f);  // ... the slot is 400
            using (app) using (host)
            {
                bool seeded = probe.Builds.Count >= 1 && probe.Builds[0] == 1;
                Frames(host, 8);
                Check("gate.tiers.responsive-seeded the first build uses the supplied estimate; once the slot is measured the tier follows it with one pending commit",
                    seeded && probe.Builds.Count == 2 && probe.Builds[1] == 0 && !host.TiersForTest.HasPending,
                    $"builds=[{string.Join(",", probe.Builds)}] pending={host.TiersForTest.PendingCount}");
            }
        }
    }

    // ── gate.tiers.virtual-scroll-end ─────────────────────────────────────────────────────────────────────────────

    static void VirtualListScrollEndChecks(StringTable strings)
    {
        // The path that makes a plain layout-phase write snap (§3.3 rule 3): scrolled to the END of a virtual list, a resize that
        // moves the scroll plan re-realizes the list and runs the in-frame global flush after layout. The pending tier must
        // survive that flush untouched and cross, animated, on the next frame.
        var probe = new TierProbe(withList: true);
        var (host, window, app) = Mount(strings, probe, 400f, 480f);
        using (app) using (host)
        {
            Frames(host, 4);
            var vp = FindScrollNode(host.Scene, host.Scene.Root);
            host.TryGetScrollHandle(vp)?.ScrollTo(40.0 * 200, ScrollMove.Immediate);   // clamped to the end
            Frames(host, 4);
            Settle(host);

            window.ClientSizePx = new Size2(900f, 640f);   // wider (crosses 600) and taller (the end-pinned plan moves)
            var resized = host.RunFrame();
            bool keptOld = probe.Seen.Count == 1 && host.TiersForTest.PendingCount == 1
                && Near(host.Scene.AbsoluteRect(probe.Rail).W, 56f) && !host.Animation.HasTracks(probe.Rail);
            var next = host.RunFrame();
            bool crossed = probe.Seen.Count == 2 && probe.Seen[1] == 1 && !host.TiersForTest.HasPending
                && Near(host.Scene.AbsoluteRect(probe.Rail).W, 220f)
                && host.Animation.HasTracks(probe.Rail) && host.Animation.HasTracks(probe.Card);
            Check("gate.tiers.virtual-scroll-end resize at the end of a virtual list: the tier stays pending through the post-layout global flush and crosses with FLIP tracks on the next frame",
                keptOld && crossed,
                $"resizedFrame.Rendered={resized.Rendered} keptOld={keptOld} crossed={crossed} seen=[{string.Join(",", probe.Seen)}] pending={host.TiersForTest.PendingCount} railTracks={host.Animation.HasTracks(probe.Rail)}");
        }
    }
}
