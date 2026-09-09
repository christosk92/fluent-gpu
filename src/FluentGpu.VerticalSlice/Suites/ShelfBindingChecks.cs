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
            CheckLazyGridVisibility(strings);
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
        // Prop delivery and every mounted item settle in the same flush/frame.
        host.RunFrame();
        var updatedTitle = FindTextNode(scene, strings, viewport, "new-3");
        scene.TryGetScroll(viewport, out var after);
        Check("gate.shelf.binding.metadata retains viewport, row nodes, pager and focus",
            !title.IsNull && updatedTitle == title && FindScrollNode(scene, scene.Root) == viewport
            && probe.Mounts == mounts && probe.Pager.Page == 1 && MathF.Abs(before.OffsetX - after.OffsetX) < 0.5f
            && !focused.IsNull && FocusedNode(scene, scene.Root) == focused,
            $"sameTitle={updatedTitle == title} mounts={mounts}->{probe.Mounts} page={probe.Pager.Page} offset={before.OffsetX}->{after.OffsetX}");
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

    static void Settle(AppHost host) { for (int i = 0; i < 12; i++) host.RunFrame(); }
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
