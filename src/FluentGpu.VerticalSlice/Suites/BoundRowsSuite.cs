using System;
using System.Collections.Generic;
using System.Linq;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Hosting;
using FluentGpu.Pal;
using FluentGpu.Pal.Headless;
using FluentGpu.Reconciler;
using FluentGpu.Rhi.Headless;
using FluentGpu.Scene;
using FluentGpu.Scroll.Runtime;
using FluentGpu.Signals;
using FluentGpu.Text.Headless;
using static FluentGpu.VerticalSlice.Harness.Gate;
using static FluentGpu.VerticalSlice.Harness.Asserts;

namespace FluentGpu.VerticalSlice.Suites;

/// <summary>
/// EP6 (technical-plan E13): bound-row templates and the stamp-driven slot rebuild. A bound list builds each slot once and
/// recycles it by an index-signal write, so a new template can only reach existing slots through
/// <see cref="VirtualListEl.TemplateStamp"/> (directly, or via <c>ItemsView.CreateBound(..., templateStamp:)</c>). The gates:
/// a stamp change on a 2,000-row list gives every realized slot, every Spare-pool slot, every content-type-pool slot and the
/// keep-alive bucket the new template in the SAME publication (one frame), with the same index signals and fresh measured
/// extents; an unchanged stamp (even with a fresh delegate every render) rebuilds nothing; the recycle hit rate after the
/// rebuild is at least the baseline; and frame phases 6-13 allocate nothing once the rebuild frame is past. Registered as
/// <c>bound-rows</c>.
/// </summary>
static class BoundRowsSuite
{
    const int N = 2_000;
    const double FlingRestDipPerS = 1.0;

    public static void Run(StringTable strings)
    {
        VirtualRebuildChecks(strings);
        ContentTypeAndKeepAliveChecks(strings);
        ItemsViewChecks(strings);
        AllocChecks(strings);
    }

    // ── probes ───────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>A bound list (<c>Virtual.ListBound</c> shape) whose template version is its <see cref="Stamp"/>. A version-v
    /// slot root has <c>1 + v</c> children (<c>+1</c> more for a type-1 row of the content-type variant), so the scene shows
    /// which template built it. Every index signal handed to a build is recorded per version.</summary>
    sealed class StampedVirtualProbe(bool typed) : Component
    {
        public readonly Signal<float> Vh = new(400f);
        public readonly Signal<int> Stamp = new(0);
        public readonly int[] Calls = new int[4];
        public readonly List<IReadSignal<int>>[] Signals = [new(), new(), new(), new()];
        public int TotalCalls => Calls[0] + Calls[1] + Calls[2] + Calls[3];

        public static int TypeOf(int index) => index % 5 == 0 ? 1 : 0;
        public static int KidsFor(int version, int index, bool typed) => 1 + version + (typed ? TypeOf(index) : 0);

        public override Element Render()
        {
            int v = Stamp.Value;
            float h = Vh.Value;
            var list = Virtual.ListBound(N, 40f, idx =>
            {
                Calls[v]++;
                Signals[v].Add(idx);
                int kids = KidsFor(v, idx.Peek(), typed);
                var children = new Element[kids];
                for (int i = 0; i < kids; i++) children[i] = new BoxEl { Width = 4f, Height = 4f };
                return new BoxEl { Height = 40f, Children = children };
            });
            if (typed) list = list with { ContentType = static i => TypeOf(i), KeepAlive = static i => i == 2 };
            return list with { Width = 300f, Height = h, TemplateStamp = v };
        }
    }

    /// <summary>An <c>ItemsView.CreateBound</c> list with a <see cref="BoundRowTemplate{TRow}"/> that gets a fresh delegate every
    /// render (a method group); only <see cref="Stamp"/> may rebuild.</summary>
    sealed class StampedItemsProbe : Component
    {
        public readonly Signal<int> Stamp = new(0);
        public readonly Signal<int> Poke = new(0);
        public readonly int[] Calls = new int[4];
        public object? SeenMetrics;
        public int Renders;
        readonly Signal<IReadOnlyList<int>> _items = new(Enumerable.Range(0, N).ToArray());
        BoundItemsSource<int>? _source;
        int _version;

        public override Element Render()
        {
            Renders++;
            _ = Poke.Value;
            _version = Stamp.Value;
            _source ??= BoundItems.From(_items, -1);
            return new BoxEl
            {
                Width = 300f, Height = 400f, Direction = 1,
                Children = [ItemsView.CreateBound(_source, Template, RepeatLayout.Stack(40f), null, templateStamp: _version, metrics: "metrics")],
            };
        }

        Element Template(BoundItemScope<int> row, in RowStyleContext ctx)
        {
            int v = _version;
            Calls[v]++;
            SeenMetrics = ctx.Metrics;
            var children = new Element[1 + v];
            for (int i = 0; i < children.Length; i++) children[i] = new BoxEl { Width = 4f, Height = 4f };
            return new BoxEl { Height = 40f, Children = children };
        }
    }

    // ── helpers ──────────────────────────────────────────────────────────────────────────────────────────────────

    static void Settle(AppHost host, int max = 40) { for (int i = 0; i < max && host.HasActiveWork; i++) host.RunFrame(); }

    static NodeHandle FindViewport(SceneStore s, NodeHandle n)
    {
        if (n.IsNull) return default;
        if (s.TryGetScroll(n, out var sc) && sc.ItemCount == N) return n;
        for (var c = s.FirstChild(n); !c.IsNull; c = s.NextSibling(c))
        {
            var r = FindViewport(s, c);
            if (!r.IsNull) return r;
        }
        return default;
    }

    static NodeHandle ContentOf(SceneStore s, NodeHandle viewport) { s.TryGetScroll(viewport, out var sc); return sc.ContentNode; }

    static bool AllLiveKids(AppHost host, NodeHandle content, Func<int, int> expectedKids, out int checkedRows)
    {
        host.Scene.TryGetScroll(host.Scene.Parent(content), out var sc);
        int ord = 0; bool ok = true; checkedRows = 0;
        for (var c = host.Scene.FirstChild(content); !c.IsNull; c = host.Scene.NextSibling(c), ord++)
        {
            checkedRows++;
            if (host.Scene.ChildCount(c) != expectedKids(sc.FirstRealized + ord)) ok = false;
        }
        return ok;
    }

    static bool AllSpareKids(AppHost host, NodeHandle vp, int expectedKids)
    {
        int spares = host.Reconciler.SpareSlotCount(vp);
        for (int i = 0; i < spares; i++)
        {
            if (!host.Reconciler.TryGetSpareSlotRoot(vp, i, out var root)) return false;
            if (host.Scene.ChildCount(root) != expectedKids) return false;
        }
        return true;
    }

    // ── (1) a plain bound list: realized + Spare pool, same signals, fresh extents, recycle hit rate ─────────────────

    static void VirtualRebuildChecks(StringTable strings)
    {
        using var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("bound-rows-virtual", new Size2(640, 480), 1f)); window.Show();
        var probe = new StampedVirtualProbe(typed: false);
        using var host = new AppHost(app, window, new HeadlessGpuDevice(), new HeadlessFontSystem(strings), strings, probe);
        host.RunFrame(); Settle(host);
        var vp = FindViewport(host.Scene, host.Scene.Root);
        var content = ContentOf(host.Scene, vp);
        var handle = host.TryGetScrollHandle(vp)!;

        int Walk()
        {
            int c0 = probe.TotalCalls;
            foreach (float y in new[] { 0f, 800f, 1600f, 2400f, 3200f, 2400f, 1600f, 800f, 0f })
            {
                handle.ScrollTo(y, ScrollMove.Immediate);
                for (int i = 0; i < 6; i++) host.RunFrame();
            }
            return probe.TotalCalls - c0;
        }

        // A viewport that shrinks at rest parks the surplus rows in the Spare pool.
        Walk();
        probe.Vh.Value = 160f;
        host.RunFrame(); Settle(host);
        int live = host.Scene.ChildCount(content), spare = host.Reconciler.SpareSlotCount(vp);
        Check("gate.boundrows.setup a 2,000-row bound list at rest has realized slots and a populated Spare pool",
            live > 0 && spare > 0, $"live={live} spare={spare}");

        int baselineCold = Walk();
        host.Scene.TryGetScroll(vp, out var scBefore);
        var extentBefore = scBefore.Extent;
        int calls0 = probe.Calls[0];

        probe.Stamp.Value = 1;
        host.RunFrame();   // ONE frame: the publication that carries the new stamp

        host.Scene.TryGetScroll(vp, out var scAfter);
        int liveAfter = host.Scene.ChildCount(content), spareAfter = host.Reconciler.SpareSlotCount(vp);
        bool liveNew = AllLiveKids(host, content, i => StampedVirtualProbe.KidsFor(1, i, false), out int rows);
        int firstRealized = scAfter.FirstRealized;
        bool spareNew = AllSpareKids(host, vp, StampedVirtualProbe.KidsFor(1, 0, false));
        Check("gate.boundrows.sameFrame one frame after the stamp changes every realized slot and every Spare-pool slot carries the new template (one build per slot, none of the old template)",
            liveNew && spareNew && liveAfter == live && spareAfter == spare && probe.Calls[1] == live + spare && probe.Calls[0] == calls0,
            $"liveNew={liveNew} spareNew={spareNew} live={live}->{liveAfter} spare={spare}->{spareAfter} builds v1={probe.Calls[1]} v0={probe.Calls[0]} (was {calls0}) rows={rows} first={firstRealized}");

        var oldSet = new HashSet<IReadSignal<int>>(probe.Signals[0], ReferenceEqualityComparer.Instance);
        int reused = probe.Signals[1].Count(s => oldSet.Contains(s));
        bool sameSignals = reused == probe.Signals[1].Count;
        Check("gate.boundrows.sameIndexSignals the rebuilt slots read the SAME index signals the old slots did (no new signal, no index lost)",
            sameSignals && probe.Signals[1].Count > 0, $"v1 signals={probe.Signals[1].Count} reused={reused}");

        Check("gate.boundrows.extentsReset the stamp change drops the viewport's measured extent source (re-measure against the new template)",
            !ReferenceEquals(extentBefore, scAfter.Extent) && scAfter.Extent is not null,
            $"before={extentBefore?.GetType().Name} after={scAfter.Extent?.GetType().Name}");

        // Scrolling afterwards still serves only new-template rows, from the (rebuilt) pool.
        int coldAfter = Walk();
        host.Scene.TryGetScroll(vp, out var scEnd);
        bool tail = AllLiveKids(host, content, i => StampedVirtualProbe.KidsFor(1, i, false), out _);
        Check("gate.boundrows.recycleHitRate after the rebuild the same scroll walk cold-builds no more rows than before it, and every live row is the new template",
            coldAfter <= baselineCold && tail && probe.Calls[0] == calls0,
            $"cold baseline={baselineCold} after={coldAfter} tail={tail} v0Builds={probe.Calls[0]}");

        // An unchanged stamp never rebuilds: a fresh RowBind closure per render (the viewport height changes) only serves NEW slots.
        int builds = probe.TotalCalls;
        probe.Vh.Value = 400f;
        host.RunFrame(); Settle(host);
        int buildsAfterResize = probe.TotalCalls;
        probe.Vh.Value = 160f;
        host.RunFrame(); Settle(host);
        Check("gate.boundrows.sameStampNoRebuild a re-render with a fresh RowBind and the SAME stamp rebuilds no existing slot",
            probe.Calls[0] == calls0 && AllLiveKids(host, content, i => StampedVirtualProbe.KidsFor(1, i, false), out _),
            $"builds={builds}->{buildsAfterResize} (growth builds new slots only) v0={probe.Calls[0]}");
    }

    // ── (2) content-type pools and the keep-alive bucket ────────────────────────────────────────────────────────

    static void ContentTypeAndKeepAliveChecks(StringTable strings)
    {
        using var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("bound-rows-typed", new Size2(640, 480), 1f)); window.Show();
        var probe = new StampedVirtualProbe(typed: true);
        using var host = new AppHost(app, window, new HeadlessGpuDevice(), new HeadlessFontSystem(strings), strings, probe);
        host.RunFrame(); Settle(host);
        var vp = FindViewport(host.Scene, host.Scene.Root);
        var content = ContentOf(host.Scene, vp);
        var handle = host.TryGetScrollHandle(vp)!;

        // Shrink first, so the scroll below realizes at the small size and leaves surplus slots in the Spare pools; item 2 is
        // keep-alive: scrolling far away parks it in the bucket instead of recycling it.
        probe.Vh.Value = 160f;
        host.RunFrame(); Settle(host);
        handle.ScrollTo(20_000f, ScrollMove.Immediate);
        for (int i = 0; i < 8; i++) host.RunFrame();
        handle.ScrollTo(24_000f, ScrollMove.Immediate);
        for (int i = 0; i < 8; i++) host.RunFrame();
        Settle(host);

        int live = host.Scene.ChildCount(content), spare = host.Reconciler.SpareSlotCount(vp), kept = host.Reconciler.KeptSlotCount(vp);
        Check("gate.boundrows.typedSetup a content-type + keep-alive bound list has realized slots, Spare slots and a kept slot",
            live > 0 && spare > 0 && kept == 1, $"live={live} spare={spare} kept={kept}");

        int calls0 = probe.Calls[0];
        probe.Stamp.Value = 1;
        host.RunFrame();

        host.Scene.TryGetScroll(vp, out var sc);
        bool liveNew = AllLiveKids(host, content, i => StampedVirtualProbe.KidsFor(1, i, true), out _);
        // A spare keeps the content type of the row it last held: the new template gives it 2 children (type 0) or 3 (type 1).
        bool keptNew = host.Reconciler.TryGetKeptSlotRoot(vp, 2, out var keptRoot) && host.Scene.ChildCount(keptRoot) == StampedVirtualProbe.KidsFor(1, 2, true);
        bool spareNew = true;
        for (int i = 0; i < host.Reconciler.SpareSlotCount(vp); i++)
        {
            if (!host.Reconciler.TryGetSpareSlotRoot(vp, i, out var root)) { spareNew = false; break; }
            int kids = host.Scene.ChildCount(root);
            if (kids != 2 && kids != 3) spareNew = false;   // new template: 1+1 (type 0) or 1+1+1 (type 1)
        }
        Check("gate.boundrows.poolsRebuilt one frame after the stamp changes the realized, Spare (per content type) and keep-alive slots all carry the new template",
            liveNew && spareNew && keptNew
                && host.Reconciler.SpareSlotCount(vp) == spare && host.Reconciler.KeptSlotCount(vp) == kept
                && probe.Calls[1] == live + spare + kept && probe.Calls[0] == calls0,
            $"liveNew={liveNew} spareNew={spareNew} keptNew={keptNew} spare {spare}->{host.Reconciler.SpareSlotCount(vp)} kept {kept}->{host.Reconciler.KeptSlotCount(vp)} builds v1={probe.Calls[1]} expect={live + spare + kept} v0={probe.Calls[0]} (was {calls0}) first={sc.FirstRealized}");
    }

    // ── (3) ItemsView.CreateBound with a BoundRowTemplate ───────────────────────────────────────────────────────

    static void ItemsViewChecks(StringTable strings)
    {
        using var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("bound-rows-itemsview", new Size2(640, 480), 1f)); window.Show();
        var probe = new StampedItemsProbe();
        using var host = new AppHost(app, window, new HeadlessGpuDevice(), new HeadlessFontSystem(strings), strings, probe);
        host.RunFrame(); Settle(host);
        var vp = FindViewport(host.Scene, host.Scene.Root);
        var content = ContentOf(host.Scene, vp);
        int live = host.Scene.ChildCount(content);
        Check("gate.boundrows.itemsViewMount a BoundRowTemplate list mounts one template build per slot and hands the template its metrics",
            live > 0 && probe.Calls[0] >= live && Equals(probe.SeenMetrics, "metrics"), $"live={live} builds={probe.Calls[0]} metrics={probe.SeenMetrics}");

        int builds0 = probe.Calls[0];
        probe.Poke.Value++;   // the embedder re-renders and hands the list a FRESH delegate, the same stamp
        host.RunFrame(); Settle(host);
        Check("gate.boundrows.itemsViewSameStamp a re-render that hands CreateBound a fresh template delegate and the same stamp rebuilds nothing",
            probe.Calls[0] == builds0 && AllLiveKids(host, content, i => 1, out _), $"builds={builds0}->{probe.Calls[0]}");

        probe.Stamp.Value = 1;
        host.RunFrame();
        bool allNew = AllLiveKids(host, content, i => 2, out int rows);
        Check("gate.boundrows.itemsViewStamp one frame after the stamp changes every live slot of the ItemsView carries the new template",
            allNew && host.Scene.ChildCount(content) == live
                && probe.Calls[1] == live + host.Reconciler.SpareSlotCount(vp) + host.Reconciler.KeptSlotCount(vp) && probe.Calls[0] == builds0,
            $"allNew={allNew} rows={rows} live={live} spare={host.Reconciler.SpareSlotCount(vp)} v1={probe.Calls[1]} v0={probe.Calls[0]} (was {builds0})");
    }

    // ── (4) allocations: 0 in frame phases 6-13 outside the rebuild flush ───────────────────────────────────────────

    static void AllocChecks(StringTable strings)
    {
        using var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("bound-rows-alloc", new Size2(640, 480), 1f)); window.Show();
        var probe = new StampedVirtualProbe(typed: false);
        using var host = new AppHost(app, window, new HeadlessGpuDevice(), new HeadlessFontSystem(strings), strings, probe);
        host.RunFrame(); Settle(host);
        var vp = FindViewport(host.Scene, host.Scene.Root);
        host.TryGetScrollHandle(vp)?.ScrollTo(20_000f, ScrollMove.Immediate);
        for (int i = 0; i < 40; i++) host.RunFrame();
        var prod = new HeadlessScrollProducer(window, host, new Point2(150, 200)) { Device = DeviceClassIgnored };

        (long WorstHot, int Templates) Fling(float dy, bool measure)
        {
            long worstHot = 0; int t0 = probe.TotalCalls;
            void Acc(FrameStats f) { if (measure && f.HotPhaseAllocBytes > worstHot) worstHot = f.HotPhaseAllocBytes; }
            prod.ContactBegin(0f); Acc(prod.Step(16));
            for (int i = 0; i < 10; i++) { prod.ContactUpdate(dy); Acc(prod.Step(16)); }
            prod.ContactEnd();
            for (int f = 0; f < 120; f++)
            {
                Acc(prod.Step(16));
                host.Scene.TryGetScroll(vp, out var s);
                if (Math.Abs(s.Velocity) <= FlingRestDipPerS) break;
            }
            return (worstHot, probe.TotalCalls - t0);
        }

        Fling(60f, measure: false);
        Fling(-60f, measure: false);

        // The rebuild frame itself is the one flush allowed to build; everything after it is the steady state again.
        probe.Stamp.Value = 1;
        host.RunFrame(); Settle(host);
        Fling(60f, measure: false);   // re-warm the new template's code paths
        Fling(-60f, measure: false);

        var fwd = Fling(60f, measure: true);
        var rev = Fling(-60f, measure: true);
        Check("gate.boundrows.noHotAllocAfterRebuild a warm forward+reverse fling after a stamp rebuild allocates 0 bytes in frame phases 6-13 and runs zero template builds",
            fwd.WorstHot == 0 && rev.WorstHot == 0 && fwd.Templates == 0 && rev.Templates == 0,
            $"hotWorst fwd={fwd.WorstHot}B rev={rev.WorstHot}B templates fwd={fwd.Templates} rev={rev.Templates}");
    }
}
