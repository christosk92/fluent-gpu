using System;
using System.Collections.Generic;
using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Hosting;
using FluentGpu.Pal;
using FluentGpu.Pal.Headless;
using FluentGpu.Rhi.Headless;
using FluentGpu.Scene;
using FluentGpu.Signals;
using FluentGpu.Text;
using FluentGpu.Text.Headless;
using static FluentGpu.Dsl.Ui;
using static FluentGpu.VerticalSlice.Harness.Asserts;
using static FluentGpu.VerticalSlice.Harness.Gate;

static class ShelfBindingChecks
{
    public static void Run(StringTable strings)
    {
        bool previousReduced = Motion.ReducedMotion;
        Motion.ReducedMotion = true;
        try
        {
            CheckRetainedRows(strings);
            CheckMeasurement(strings);
            CheckMeasurementWidthDrivenFirstRender(strings);
            CheckLazyGridVisibility(strings);
            CheckLazyGridRenderGate(strings);
            CheckPropsDataGate(strings);
            CheckResponsiveGate(strings);
        }
        finally { Motion.ReducedMotion = previousReduced; }
    }

    static void CheckRetainedRows(StringTable strings)
    {
        using var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("shelf-binding", new Size2(640, 400), 1f));
        window.Show();
        var probe = new Probe(measured: false);
        using var host = new AppHost(app, window, new HeadlessGpuDevice(), new HeadlessFontSystem(strings), strings, probe);
        Settle(host);
        probe.Pager.GoTo(1);
        Settle(host);
        var scene = host.Scene;
        var viewport = FindScrollNode(scene, scene.Root);
        var title = FindTextNode(scene, strings, viewport, "old-3");
        var card = probe.Cards[3];
        card.Open.Value = true;
        Settle(host);
        host.Input.SetFocus(card.Anchor, visual: true);
        host.RunFrame();
        var focused = FocusedNode(scene, scene.Root);
        int mounts = probe.Mounts;
        scene.TryGetScroll(viewport, out var before);
        probe.Items.Value = Items("new", 12);
        // Ordinary metadata delivery uses the hosted reactive slice, which may yield between the shelf,
        // Responsive and card renders. Wait for the observed page-one card's label with the same finite
        // ceiling as Settle. The virtual shelf need not realize/update every offscreen item; cumulative mount
        // count is not the current realized window. Identity, focus, popup and current action are still checked.
        bool metadataReady = SettleUntil(host,
            () => !FindTextNode(scene, strings, viewport, "new-3").IsNull, out int metadataFrames);
        var updatedTitle = FindTextNode(scene, strings, viewport, "new-3");
        scene.TryGetScroll(viewport, out var after);
        Check("gate.shelf.binding.metadata retains viewport, row nodes, pager and focus",
            metadataReady && !title.IsNull && updatedTitle == title && FindScrollNode(scene, scene.Root) == viewport
            && probe.Mounts == mounts && probe.Pager.Page == 1 && MathF.Abs(before.OffsetX - after.OffsetX) < 0.5f
            && !focused.IsNull && FocusedNode(scene, scene.Root) == focused,
            $"ready={metadataReady} frames={metadataFrames} sameTitle={updatedTitle == title} mounts={mounts}->{probe.Mounts} page={probe.Pager.Page} offset={before.OffsetX}->{after.OffsetX}");
        Check("gate.shelf.binding.popup retains an open controlled popup across metadata replacement",
            card.Open.Peek() && !FindTextNode(scene, strings, scene.Root, "popup-3").IsNull,
            $"open={card.Open.Peek()}");
        card.Invoke();
        Check("gate.shelf.binding.actions invoke the current item after same-count replacement",
            probe.Invoked == "new-3", $"invoked={probe.Invoked}");

        card.Open.Value = false;
        probe.Route.Value = "other";
        Settle(host);
        int parkedMounts = probe.Mounts;
        probe.Items.Value = Items("parked", 12);
        Settle(host);
        bool quietWhileParked = probe.Mounts == parkedMounts;
        probe.Route.Value = "shelf";
        Settle(host);
        Check("gate.shelf.binding.park replays latest props into the retained viewport and pager",
            quietWhileParked && FindScrollNode(scene, scene.Root) == viewport && probe.Pager.Page == 1
            && !FindTextNode(scene, strings, viewport, "parked-3").IsNull,
            $"quiet={quietWhileParked} page={probe.Pager.Page}");

        probe.Items.Value = Items("grown", 35);
        Settle(host);
        bool grewInPlace = FindScrollNode(scene, scene.Root) == viewport && probe.Pager.PageCount > 4;
        card.Open.Value = false;
        probe.Items.Value = Items("short", 2);
        Settle(host);
        Check("gate.shelf.binding.count grows and shrinks on the retained viewport and clamps its pager",
            grewInPlace && FindScrollNode(scene, scene.Root) == viewport && probe.Pager.Page == 0 && probe.Pager.PageCount == 1,
            $"grew={grewInPlace} page={probe.Pager.Page}/{probe.Pager.PageCount}");
        var steady = host.RunFrame();
        Check("gate.shelf.binding.steady adds no hot-phase allocations",
            steady.HotPhaseAllocBytes == 0, $"bytes={steady.HotPhaseAllocBytes}");
    }

    static void CheckMeasurement(StringTable strings)
    {
        using var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("shelf-measure-binding", new Size2(640, 400), 1f));
        window.Show();
        var probe = new Probe(measured: true);
        using var host = new AppHost(app, window, new HeadlessGpuDevice(), new HeadlessFontSystem(strings), strings, probe);
        Settle(host);
        var scene = host.Scene;
        var viewport = FindScrollNode(scene, scene.Root);
        scene.TryGetScroll(viewport, out var before);
        probe.Items.Value = Items("taller", 12, height: 84f);
        Settle(host);
        scene.TryGetScroll(viewport, out var taller);
        bool same = FindScrollNode(scene, scene.Root) == viewport;
        probe.Items.Value = Items("shorter", 12, height: 36f);
        Settle(host);
        scene.TryGetScroll(viewport, out var shorter);
        Check("gate.shelf.binding.measurement same-count content changes remeasure without replacing the viewport",
            !viewport.IsNull && same && FindScrollNode(scene, scene.Root) == viewport
            && taller.ViewportH > before.ViewportH + 20f && shorter.ViewportH < taller.ViewportH - 20f,
            $"heights={before.ViewportH}->{taller.ViewportH}->{shorter.ViewportH} sameViewport={same}");
    }

    // ── Regression: a measured shelf whose card HEIGHT is WIDTH-DRIVEN (16:9 thumb + fixed text block, exactly the
    // shape of a real Album/Artist shelf card) with a small enough item count to complete its probe in ONE pass
    // (ShelfProbeMath.Chunk=4, 3 items here). At mount the shelf's self-measured width is 0 for its first render
    // (FillRowVirtualLayout.Fit(0, …) ⇒ cardW=minCardW), so a WIDTH-DRIVEN card's height at that fit differs from its
    // height at the real fit that lands moments later (a fixed/square card would not reproduce this — its height is
    // the same regardless of cardW). Before the ResetProbePass fix this races: the min-width pass can complete and
    // lock _measuredH, ResetProbePass then clears the probe node handles, and the immediately-following real-width
    // re-probe re-emits the SAME KEYED cells while the old ones are still mounted — the reconciler reuses the nodes,
    // OnRealized never refires (mount-only), and the handles stay null forever (maxH=0, ArmProbeRetry × MaxProbeRetries,
    // stuck). The gate is simply: after Settle, the locked viewport height matches the REAL fit's card height (never
    // the w=0 min-width one, and never zero/stuck), and it stays quiet — no probe cells still mounted, no further churn.
    static void CheckMeasurementWidthDrivenFirstRender(StringTable strings)
    {
        using var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("shelf-measure-width-driven", new Size2(640, 400), 1f));
        window.Show();
        var probe = new WidthDrivenProbe();
        using var host = new AppHost(app, window, new HeadlessGpuDevice(), new HeadlessFontSystem(strings), strings, probe);
        Settle(host);
        var scene = host.Scene;
        var viewport = FindScrollNode(scene, scene.Root);
        scene.TryGetScroll(viewport, out var vp);

        // The shelf's own available width is its parent's content width (320f — same fixed parent shape CheckMeasurement
        // uses), the fit a real width-driven card lands at once the real width is known. Computed the SAME way PagedShelf
        // itself computes it (the public, exposed-static Fit), not re-derived some other way.
        var (_, fitCardW) = FillRowVirtualLayout.Fit(320f, WidthDrivenProbe.MinCardW, WidthDrivenProbe.MaxCardW, WidthDrivenProbe.Gap);
        float expectedCardH = MathF.Round(fitCardW * 9f / 16f) + 72f;
        // PagedShelfCore.ShadowClearance + LiftClearance (12f each): the vertical-halo headroom the viewport reserves
        // above/below the locked card height.
        float expectedViewportH = expectedCardH + 24f;
        bool lockedRight = !viewport.IsNull && MathF.Abs(vp.ViewportH - expectedViewportH) < 1f;

        // A stuck/oscillating pass keeps re-arming its UseTimeout continuation (ArmProbeRetry/AdvanceProbe), so a
        // still frame right after Settle would still show component renders and probe-node allocations; a converged
        // shelf renders nothing further.
        var steady = host.RunFrame();
        scene.TryGetScroll(viewport, out var afterSteady);
        bool quiet = MathF.Abs(afterSteady.ViewportH - vp.ViewportH) < 0.5f && steady.HotPhaseAllocBytes == 0
                  && steady.ComponentsRendered == 0;

        Check("gate.shelf.binding.measurement a width-driven card whose first-render (w=0) fit differs from the real fit still locks the correct height",
            lockedRight && quiet,
            $"cardW={fitCardW:0.#} expectedH={expectedCardH:0.#} expectedViewport={expectedViewportH:0.#} actualViewport={vp.ViewportH:0.#} quiet={quiet} steadyBytes={steady.HotPhaseAllocBytes} steadyRenders={steady.ComponentsRendered}");
    }

    sealed class WidthDrivenProbe : Component
    {
        public const float MinCardW = 140f, MaxCardW = 200f, Gap = 12f;
        static readonly IReadOnlyList<Item> WidthDrivenItems = ShelfBindingChecks.Items("wd", 3);

        public override Element Render() => new BoxEl
        {
            Width = 320f, Direction = 1,
            Children =
            [
                PagedShelf.Create(WidthDrivenItems, Card,
                    minCardW: MinCardW, maxCardW: MaxCardW, gap: Gap,
                    headerGap: 0f, edgeFade: 0f, measured: true,
                    keyOf: static (item, _) => item.Id.ToString()),
            ],
        };

        // The shape that makes the bug reproducible: a 16:9 thumb sized to the fitted card WIDTH plus a fixed text
        // block below it. Its height therefore MOVES between the w=0 first-render fit (cardW=minCardW) and the real
        // fit that lands moments later — a fixed/square card's height would not move and could never reproduce the race.
        static Element Card(Item item, int index, float width)
        {
            float h = MathF.Round(width * 9f / 16f) + 72f;
            return new BoxEl { Width = width, Height = h, Direction = 1, Children = [Text(item.Title)] };
        }
    }

    const int MaxSettleFrames = 12;
    static void Settle(AppHost host) { for (int i = 0; i < MaxSettleFrames; i++) host.RunFrame(); }
    static bool SettleUntil(AppHost host, Func<bool> ready, out int frames)
    {
        for (frames = 1; frames <= MaxSettleFrames; frames++)
        {
            host.RunFrame();
            if (ready()) return true;
        }
        frames = MaxSettleFrames;
        return false;
    }
    static void CheckLazyGridVisibility(StringTable strings)
    {
        using var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("grid-demand", new Size2(640, 400), 1f));
        window.Show();
        var probe = new GridVisibilityProbe();
        using var host = new AppHost(app, window, new HeadlessGpuDevice(), new HeadlessFontSystem(strings), strings, probe);
        Settle(host);
        var viewport = FindScrollNode(host.Scene, host.Scene.Root);
        var first = probe.Visible;
        host.ScrollKernel.Port.Post(FluentGpu.Scroll.ScrollInput.ScrollTo((int)viewport.Raw.Index, 1200, immediate: true));
        Settle(host);
        var outside = probe.Visible;
        host.ScrollKernel.Port.Post(FluentGpu.Scroll.ScrollInput.ScrollTo((int)viewport.Raw.Index, 0, immediate: true));
        Settle(host);
        Check("gate.lazygrid.visibility publishes an empty demand window offscreen and restores it on return",
            first.LastIndexExclusive > first.FirstIndex && outside.LastIndexExclusive == outside.FirstIndex
            && probe.Visible.LastIndexExclusive > probe.Visible.FirstIndex && FindScrollNode(host.Scene, host.Scene.Root) == viewport,
            $"visible={first} outside={outside} returned={probe.Visible}");
    }

    sealed class GridVisibilityProbe : Component
    {
        readonly Signal<float> _offset = new(0f);
        public LazyGridVisibleRange Visible;
        public override Element Render() => Ctx.Provide(LazyScroll.Slot, (IReadSignal<float>)_offset,
            ScrollView(new BoxEl
            {
                Direction = 1,
                Children =
                [
                    Embed.Comp(() => new LazyGrid(() => 6,
                        (index, width) => new BoxEl { Height = width, Children = [Text("release-" + index)] },
                        (_, _) => { }, onVisibleRangeChanged: range => Visible = range)),
                    new BoxEl { Height = 1800f },
                ],
            }) with
            {
                Grow = 1f,
                OnScrollGeometryChanged = (g => (int)g.OffsetY, g => _offset.SetIfChanged(g.OffsetY)),
            });
    }

    // ── The render gate: the realized window is the render's ONLY scroll subscription ───────────────────────────────
    // LazyGrid used to read one (realizedKey, visibleRange) tuple in its render, so every row crossing — about every
    // frame at wheel speed — re-rendered the grid and rebuilt the whole realized slice (~450 KB a frame on the artist
    // page). The render now subscribes to the realized-window key alone; the exact visible range feeds the
    // onVisibleRangeChanged effect through its own signal. GridSlice calls the cell builder once per realized index on
    // every grid render, so the probe's cell-build counter is a direct render counter: zero builds ⇔ no grid render.
    // Geometry (content width 600, minColWidth 180, gap 12 ⇒ 3 columns of 192; rowH = 192 + 56 = 248; viewport 400;
    // overscan 2; 30 items ⇒ 10 rows):
    //   offset   0: visible rows 0-1 (items 0..6), realized rows 0..3
    //   offset  40: visible rows 0-1 (items 0..6), realized rows 0..3   → neither moved: no render, no callback
    //   offset 200: visible rows 0-2 (items 0..9), realized rows 0..4   → the band grew a row: one render + a callback
    //   offset 270: visible rows 1-2 (items 3..9), realized rows 0..4   → visible moved, band did not: callback, NO render
    static void CheckLazyGridRenderGate(StringTable strings)
    {
        using var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("grid-render-gate", new Size2(640, 400), 1f));
        window.Show();
        var probe = new GridRenderGateProbe();
        using var host = new AppHost(app, window, new HeadlessGpuDevice(), new HeadlessFontSystem(strings), strings, probe);
        Settle(host);
        var viewport = FindScrollNode(host.Scene, host.Scene.Root);
        var atTop = probe.Visible;
        bool mounted = probe.CellBuilds > 0 && atTop.FirstIndex == 0 && atTop.LastIndexExclusive > 0;

        int ScrollAndCount(float offset)
        {
            probe.CellBuilds = 0; probe.VisibleCallbacks = 0;
            host.ScrollKernel.Port.Post(FluentGpu.Scroll.ScrollInput.ScrollTo((int)viewport.Raw.Index, offset, immediate: true));
            int rendered = 0;
            for (int i = 0; i < 12; i++) rendered += host.RunFrame().ComponentsRendered;
            return rendered;
        }

        int subRowRendered = ScrollAndCount(40f);
        host.Scene.TryGetScroll(viewport, out var subRow);
        Check("gate.lazygrid.render a sub-row scroll (visible rows and realized band unchanged) renders nothing and publishes nothing",
            mounted && subRow.OffsetY > 30f && probe.CellBuilds == 0 && probe.VisibleCallbacks == 0 && probe.Visible == atTop,
            $"offset={subRow.OffsetY} cellBuilds={probe.CellBuilds} callbacks={probe.VisibleCallbacks} componentsRendered={subRowRendered} visible={probe.Visible} atTop={atTop}");

        ScrollAndCount(200f);
        var grown = probe.Visible;
        Check("gate.lazygrid.render a row entering the realized band re-renders the grid and publishes the wider range",
            probe.CellBuilds > 0 && probe.VisibleCallbacks >= 1
            && grown.FirstIndex == atTop.FirstIndex && grown.LastIndexExclusive > atTop.LastIndexExclusive,
            $"cellBuilds={probe.CellBuilds} callbacks={probe.VisibleCallbacks} visible={grown} atTop={atTop}");

        int crossedRendered = ScrollAndCount(270f);
        var crossed = probe.Visible;
        Check("gate.lazygrid.render a row crossing inside the realized band publishes the visible range WITHOUT a render",
            probe.CellBuilds == 0 && probe.VisibleCallbacks >= 1
            && crossed.FirstIndex > grown.FirstIndex && crossed.LastIndexExclusive == grown.LastIndexExclusive,
            $"cellBuilds={probe.CellBuilds} callbacks={probe.VisibleCallbacks} componentsRendered={crossedRendered} visible={crossed} before={grown}");

        probe.CellBuilds = 0;
        probe.Count.Value = 33;
        Settle(host);
        int countBuilds = probe.CellBuilds;

        probe.CellBuilds = 0;
        probe.Count.Value = 33;   // an equal write: the signal coalesces it, the grid must not notice
        Settle(host);
        var steady = host.RunFrame();
        Check("gate.lazygrid.render a count-signal change re-renders the grid; an equal write and a still frame do not",
            countBuilds > 0 && probe.CellBuilds == 0 && steady.HotPhaseAllocBytes == 0,
            $"countBuilds={countBuilds} equalWriteBuilds={probe.CellBuilds} bytes={steady.HotPhaseAllocBytes}");
    }

    sealed class GridRenderGateProbe : Component
    {
        readonly Signal<float> _offset = new(0f);
        public readonly Signal<int> Count = new(30);
        public int CellBuilds, VisibleCallbacks;
        public LazyGridVisibleRange Visible;
        public override Element Render() => Ctx.Provide(LazyScroll.Slot, (IReadSignal<float>)_offset,
            ScrollView(new BoxEl
            {
                Direction = 1, Width = 600f,
                Children =
                [
                    // The churn-free overload: the grid's render subscribes to Count and its realized-window key only.
                    Embed.Comp(() => new LazyGrid(Count,
                        (index, _) => { CellBuilds++; return new BoxEl { Height = 200f, Children = [Text("card-" + index)] }; },
                        (_, _) => { }, onVisibleRangeChanged: range => { VisibleCallbacks++; Visible = range; })),
                    new BoxEl { Height = 1800f },
                ],
            }) with
            {
                Grow = 1f,
                OnScrollGeometryChanged = (g => (int)g.OffsetY, g => _offset.SetIfChanged(g.OffsetY)),
            });
    }
    // ── The props DATA GATE (docs/design/subsystems/component-props-contract.md "Retained shelf authoring") ──────────
    // ShelfProps' equality IS the reconciler's re-render gate. It compares the item snapshot (reference, else the
    // MaxItems-clamped prefix element-by-element) and MaxItems; it IGNORES the card/key/pager/range delegates (a fresh
    // closure every render can never compare equal) and it excludes the header/title, which ride the chrome signal.
    // These four checks pin exactly that split — cards are rebuilt by DATA, never by a parent re-render or chrome.
    static void CheckPropsDataGate(StringTable strings)
    {
        using var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("shelf-props-gate", new Size2(640, 400), 1f));
        window.Show();
        var probe = new GateProbe();
        using var host = new AppHost(app, window, new HeadlessGpuDevice(), new HeadlessFontSystem(strings), strings, probe);
        Settle(host);
        var scene = host.Scene;

        // (a) A parent re-render hands a NEW list instance of EQUAL items, NEW closures and a NEW header element.
        int mounted = probe.HeaderCards;
        probe.HeaderCards = probe.TitleCards = 0;
        probe.Epoch.Value++;
        Settle(host);
        Check("gate.shelf.props.equal-items rebuild no cards when only the parent re-rendered",
            probe.HeaderCards == 0 && probe.TitleCards == 0,
            $"mountedCards={mounted} headerCards={probe.HeaderCards} titleCards={probe.TitleCards}");

        // (c) CHROME: a rebuilt header Element (new reference) re-renders the shelf's header row, never its cards.
        probe.HeaderCards = probe.TitleCards = 0;
        probe.HeaderText.Value = "head-b";
        probe.TitleText.Value = "title-b";
        Settle(host);
        Check("gate.shelf.props.chrome refreshes the header and title without rebuilding cards",
            !FindTextNode(scene, strings, scene.Root, "head-b").IsNull
            && !FindTextNode(scene, strings, scene.Root, "title-b").IsNull
            && probe.HeaderCards == 0 && probe.TitleCards == 0,
            $"headerCards={probe.HeaderCards} titleCards={probe.TitleCards}");

        // (b) ONE item's value changes. Every REALIZED card re-renders (they all read the one gated props signal — the
        // shelf keys its cards for node reuse, not for per-item render scoping), and the changed card shows its value.
        probe.HeaderCards = probe.TitleCards = 0;
        probe.Bumped.Value = 2;
        Settle(host);
        int oneItemRebuilds = probe.HeaderCards;
        Check("gate.shelf.props.changed-item rebuilds the shelf's realized cards",
            oneItemRebuilds > 0 && !FindTextNode(scene, strings, scene.Root, "g-2!").IsNull,
            $"rebuilt={oneItemRebuilds} (all realized cards; the props signal is the shelf-wide data seam)");

        // (d) MaxItems is DATA — it changes what is rendered, so it must pass the gate.
        probe.HeaderCards = probe.TitleCards = 0;
        probe.MaxItems.Value = 3;
        Settle(host);
        Check("gate.shelf.props.maxItems change passes the data gate",
            probe.HeaderCards > 0, $"rebuilt={probe.HeaderCards}");

        var steady = host.RunFrame();
        Check("gate.shelf.props.gate adds no hot-phase allocations",
            steady.HotPhaseAllocBytes == 0, $"bytes={steady.HotPhaseAllocBytes}");
    }

    // ── Responsive.Of — the two documented gates (see Responsive.cs / ResponsiveBox.Props) ───────────────────────────
    static void CheckResponsiveGate(StringTable strings)
    {
        using var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("responsive-gate", new Size2(640, 400), 1f));
        window.Show();
        var probe = new ResponsiveGateProbe();
        using var host = new AppHost(app, window, new HeadlessGpuDevice(), new HeadlessFontSystem(strings), strings, probe);
        Settle(host);

        probe.GatedBuilds = probe.LooseBuilds = 0;
        probe.Epoch.Value++;
        Settle(host);
        Check("gate.responsive.props.state-gated build is skipped when only the parent re-rendered",
            probe.GatedBuilds == 0, $"gated={probe.GatedBuilds}");
        Check("gate.responsive.props.ungated closure rebuilds with its parent (the closure IS its data channel)",
            probe.LooseBuilds > 0, $"loose={probe.LooseBuilds}");

        probe.GatedBuilds = probe.LooseBuilds = 0;
        probe.State.Value = "s1";
        Settle(host);
        Check("gate.responsive.props.state change rebuilds the state-gated child",
            probe.GatedBuilds > 0 && !FindTextNode(host.Scene, strings, host.Scene.Root, "gated-s1").IsNull,
            $"gated={probe.GatedBuilds}");
    }

    sealed class GateProbe : Component
    {
        public readonly Signal<int> Epoch = new(0);
        public readonly Signal<string> HeaderText = new("head-a");
        public readonly Signal<string> TitleText = new("title-a");
        public readonly Signal<int> Bumped = new(-1);
        public readonly Signal<int> MaxItems = new(int.MaxValue);
        public int HeaderCards, TitleCards;

        public override Element Render()
        {
            if (Epoch.Value < 0) return new BoxEl();
            string header = HeaderText.Value;
            string title = TitleText.Value;
            int bumped = Bumped.Value;
            int max = MaxItems.Value;
            // A FRESH array of EQUAL records every render — exactly what an app that re-projects its model publishes.
            var items = new Item[8];
            for (int i = 0; i < 8; i++) items[i] = new Item(i, i == bumped ? "g-" + i + "!" : "g-" + i, 44f);
            return new BoxEl
            {
                Width = 320f, Direction = 1,
                Children =
                [
                    PagedShelf.Create(items,
                        (item, _, width) => { HeaderCards++; return new BoxEl { Width = width, Height = 44f, Children = [Text(item.Title)] }; },
                        cardHeight: static _ => 44f,
                        header: new BoxEl { Children = [Text(header)] },
                        minCardW: 100f, maxCardW: 100f, fixedCardW: 100f, gap: 10f,
                        headerGap: 0f, edgeFade: 0f,
                        keyOf: static (item, _) => item.Id.ToString(),
                        maxItems: max),
                    PagedShelf.Create(items,
                        (item, _, width) => { TitleCards++; return new BoxEl { Width = width, Height = 44f, Children = [Text("t/" + item.Title)] }; },
                        cardHeight: static _ => 44f,
                        title: title,
                        minCardW: 100f, maxCardW: 100f, fixedCardW: 100f, gap: 10f,
                        headerGap: 0f, edgeFade: 0f,
                        keyOf: static (item, _) => "t" + item.Id),
                ],
            };
        }
    }

    sealed class ResponsiveGateProbe : Component
    {
        public readonly Signal<int> Epoch = new(0);
        public readonly Signal<string> State = new("s0");
        public int GatedBuilds, LooseBuilds;

        public override Element Render()
        {
            if (Epoch.Value < 0) return new BoxEl();
            string state = State.Value;
            return new BoxEl
            {
                Width = 320f, Direction = 1,
                Children =
                [
                    // Non-static lambda on purpose: a fresh closure every render is the case the gate must ignore.
                    Responsive.Of(state, (s, _) => { GatedBuilds++; return new BoxEl { Height = 20f, Children = [Text("gated-" + s)] }; }, fallback: 300f),
                    Responsive.Of(_ => { LooseBuilds++; return new BoxEl { Height = 20f, Children = [Text("loose-" + state)] }; }, fallback: 300f),
                ],
            };
        }
    }

    static IReadOnlyList<Item> Items(string prefix, int count, float height = 44f)
    {
        var items = new Item[count];
        for (int i = 0; i < count; i++) items[i] = new(i, prefix + "-" + i, height);
        return items;
    }

    sealed record Item(int Id, string Title, float Height);

    sealed class Probe(bool measured) : Component
    {
        public readonly Signal<IReadOnlyList<Item>> Items = new(ShelfBindingChecks.Items("old", 12));
        public readonly Dictionary<int, Card> Cards = new();
        public readonly Signal<string> Route = new("shelf");
        public ShelfPagerContext Pager;
        public int Mounts;
        public string? Invoked;
        public override Element Render() => Embed.Comp(() => new OverlayHost
        {
            Child = Flow.KeepAlive(() => Route.Value, static key => key,
                key => key == "shelf" ? Embed.Comp(() => new Body(this, measured)) : Text("other"),
                new KeepAliveOptions(MaxEntries: 2))
        });
    }

    sealed class Body(Probe owner, bool measured) : Component
    {
        public override Element Render() => new BoxEl
        {
            Width = 320f, Direction = 1,
            Children =
            [
                PagedShelf.Create(owner.Items.Value,
                    (item, index, width) => Responsive.Of(_ =>
                        Embed.Comp(new Card.Props(item, width), () => new Card(owner)), fallback: width),
                    cardHeight: static _ => 44f,
                    minCardW: 100f, maxCardW: 100f, fixedCardW: 100f, gap: 10f,
                    headerGap: 0f, edgeFade: 0f, measured: measured,
                    customPager: pager => { owner.Pager = pager; return new BoxEl(); },
                    keyOf: static (item, _) => item.Id.ToString())
            ]
        };
    }

    sealed class Card : Component
    {
        public sealed record Props(Item Item, float Width);
        readonly Probe _owner;
        public readonly Signal<bool> Open = new(false);
        public NodeHandle Anchor;
        public Card(Probe owner) { _owner = owner; owner.Mounts++; }
        public void Invoke() => _owner.Invoked = UseProps<Props>().Item.Title;
        public override Element Render()
        {
            var p = UseProps<Props>();
            _owner.Cards[p.Item.Id] = this;
            return Popup.Create(
                new BoxEl
                {
                    Width = p.Width, Height = p.Item.Height, Direction = 1,
                    Focusable = true, OnClick = Invoke, OnRealized = node => Anchor = node,
                    Children = [Text(p.Item.Title)]
                },
                () => Text("popup-" + p.Item.Id), Open);
        }
    }
}
