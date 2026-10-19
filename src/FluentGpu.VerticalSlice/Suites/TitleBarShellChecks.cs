using System;
using System.Collections.Generic;
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
using FluentGpu.Text.Headless;
using static FluentGpu.VerticalSlice.Harness.Gate;
using static FluentGpu.VerticalSlice.Harness.Asserts;

namespace FluentGpu.VerticalSlice.Harness;

// ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────
//  EP16: the SHELL rows. (1) The two-row bar: a 40 tab row with the captions over a 48 toolbar; drag space is every
//  pixel of both rows no island covers; the centre island sits on the window's centre line; the tab strip resolves its
//  tier form against the lane the bar measures. (2) Keyed children: a swapped or self-resizing child re-reports its
//  region in the SAME frame (one RunFrame), with no ContentVersion to feed.
// ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────
public static partial class TitleBarSuite
{
    const float ShellW = 1400f;
    const float CaptionW = 46f;

    static TitleBarHit HitAt(TitleBarRegion[] regions, float x, float y)
    {
        foreach (var r in regions)
        {
            RectF q = r.RectDip;
            if (x >= q.X && x < q.Right && y >= q.Y && y < q.Bottom) return r.Hit;
        }
        return (TitleBarHit)255;   // no region: falls through to HTCLIENT
    }

    static RectF LiveRectByInk(AppHost host, ColorF ink)
    {
        RectF found = default;
        void Walk(NodeHandle n)
        {
            if (n.IsNull) return;
            if (host.Scene.Paint(n).VisualKind == VisualKind.Box && host.Scene.Paint(n).Fill == ink)
            {
                var r = host.Scene.AbsoluteRect(n);
                if (r.W > found.W) found = r;
            }
            for (var c = host.Scene.FirstChild(n); !c.IsNull; c = host.Scene.NextSibling(c)) Walk(c);
        }
        Walk(host.Scene.Root);
        return found;
    }

    static List<float> TabCellWidths(AppHost host)
    {
        var widths = new List<float>();
        void Walk(NodeHandle n)
        {
            if (n.IsNull) return;
            if (host.Scene.Interaction(n).Role == AutomationRole.Tab) { widths.Add(host.Scene.AbsoluteRect(n).W); return; }
            for (var c = host.Scene.FirstChild(n); !c.IsNull; c = host.Scene.NextSibling(c)) Walk(c);
        }
        Walk(host.Scene.Root);
        return widths;
    }

    static bool HasScrollUnder(AppHost host, RectF area)
    {
        bool found = false;
        void Walk(NodeHandle n)
        {
            if (n.IsNull || found) return;
            if (host.Scene.HasScroll(n))
            {
                var r = host.Scene.AbsoluteRect(n);
                if (r.X >= area.X - 0.5f && r.Right <= area.Right + 0.5f && r.Y >= area.Y - 0.5f && r.Bottom <= area.Bottom + 0.5f)
                { found = true; return; }
            }
            for (var c = host.Scene.FirstChild(n); !c.IsNull; c = host.Scene.NextSibling(c)) Walk(c);
        }
        Walk(host.Scene.Root);
        return found;
    }

    static void ShellRowsChecks(StringTable strings)
    {
        Console.WriteLine("\n-- TitleBar: two-row shell bar (EP16) --");
        using var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("titlebar-two-row", new Size2((int)ShellW, 400), 1f));
        window.Show();
        var probe = new TwoRowProbe();
        using var host = new AppHost(app, window, new HeadlessGpuDevice(), new HeadlessFontSystem(strings), strings, probe);
        // 24 frames: a formed strip EASES its tab widths over ControlFast (167 ms, ~11 frames), so a form or tab-count change
        // settles well inside it.
        void Settle(int frames = 24) { for (int i = 0; i < frames; i++) host.RunFrame(); }
        Settle();

        float rowsH = TitleBar.TabRowHeight + TitleBar.ExpandedHeight;
        var regions = window.LastTitleBarRegions;
        RectF catchAll = regions.Length > 0 ? regions[^1].RectDip : default;
        bool catchAllOk = regions.Length > 0 && regions[^1].Hit == TitleBarHit.Caption
                       && Near(catchAll.X, 0f) && Near(catchAll.Y, 0f) && Near(catchAll.W, ShellW) && Near(catchAll.H, rowsH);
        int buttons = 0; bool buttonsInRow1 = true; float buttonsRight = 0f, buttonsLeft = float.PositiveInfinity;
        foreach (var r in regions)
            if (r.Hit is TitleBarHit.MinButton or TitleBarHit.MaxButton or TitleBarHit.CloseButton)
            {
                buttons++;
                if (!Near(r.RectDip.Y, 0f) || !Near(r.RectDip.H, TitleBar.TabRowHeight) || !Near(r.RectDip.W, CaptionW)) buttonsInRow1 = false;
                buttonsRight = MathF.Max(buttonsRight, r.RectDip.Right);
                buttonsLeft = MathF.Min(buttonsLeft, r.RectDip.X);
            }
        Check("gate.titlebar.rows.two-row-geometry the bar is 40 + 48 tall, ONE Caption catch-all spans both rows (last), the captions are 46x40 in row 1, flush right",
            catchAllOk && buttons == 3 && buttonsInRow1 && Near(buttonsRight, ShellW, 1f),
            $"catchAll={catchAll.X:0},{catchAll.Y:0} {catchAll.W:0}x{catchAll.H:0} last={(regions.Length > 0 ? regions[^1].Hit : default)} buttons={buttons} row1={buttonsInRow1} right={buttonsRight:0.#}");

        // Every Client region lies inside ONE row; nine of them: tabs + 3 lead + centre + 4 trailing.
        int clients = 0; bool rowBound = true;
        foreach (var r in regions)
        {
            if (r.Hit != TitleBarHit.Client) continue;
            clients++;
            RectF q = r.RectDip;
            bool inTop = q.Y >= -0.5f && q.Bottom <= TitleBar.TabRowHeight + 0.5f;
            bool inBar = q.Y >= TitleBar.TabRowHeight - 0.5f && q.Bottom <= rowsH + 0.5f;
            if (!inTop && !inBar) rowBound = false;
        }
        Check("gate.titlebar.rows.row-bound-islands every Client region sits inside one row (tabs island + 3 lead + centre + 4 trailing = 9)",
            rowBound && clients == 9, $"clients={clients} rowBound={rowBound}");

        // Drag space = the empty pixels of BOTH rows: the gap between two keyed children, the tab row after the strip, the
        // toolbar strip above a 36-tall button, the wordmark-free left half. The children themselves stay Client.
        RectF friends = LiveRectByInk(host, TwoRowProbe.FriendsInk), bell = LiveRectByInk(host, TwoRowProbe.BellInk);
        RectF menu = LiveRectByInk(host, TwoRowProbe.MenuInk);
        RectF tabsIsland = default;
        foreach (var r in regions)
            if (r.Hit == TitleBarHit.Client && r.RectDip.Y < TitleBar.TabRowHeight - 0.5f) tabsIsland = r.RectDip;
        float rowMid = TitleBar.TabRowHeight + TitleBar.ExpandedHeight * 0.5f;
        var gapHit = HitAt(regions, (friends.Right + bell.X) * 0.5f, rowMid);
        var tabRowHit = HitAt(regions, tabsIsland.Right + 20f, TitleBar.TabRowHeight * 0.5f);
        var aboveButtonHit = HitAt(regions, menu.X + menu.W * 0.5f, TitleBar.TabRowHeight + 1f);
        var onButtonHit = HitAt(regions, menu.X + menu.W * 0.5f, rowMid);
        var onBellHit = HitAt(regions, bell.X + bell.W * 0.5f, rowMid);
        Check("gate.titlebar.rows.drag-space the gaps between keyed children, the tab row after the strip and the strip above a button are Caption; the buttons are Client",
            bell.X - friends.Right >= 3.5f && gapHit == TitleBarHit.Caption && tabRowHit == TitleBarHit.Caption
                && aboveButtonHit == TitleBarHit.Caption && onButtonHit == TitleBarHit.Client && onBellHit == TitleBarHit.Client,
            $"gap={bell.X - friends.Right:0.#} gapHit={gapHit} tabRow={tabRowHit} aboveButton={aboveButtonHit} onButton={onButtonHit} onBell={onBellHit}");

        RectF center = LiveRectByInk(host, TwoRowProbe.CenterInk);
        Check("gate.titlebar.rows.window-centred CenterAlign.Window puts the 520 search on the window's centre line, whatever the clusters weigh",
            Near(center.W, 520f, 1f) && Near(center.X + center.W * 0.5f, ShellW * 0.5f, 1f),
            $"center x={center.X:0.#} w={center.W:0.#} mid={center.X + center.W * 0.5f:0.#}");

        // ── tab forms against the measured lane (lane = the row minus the 48 drag strip and the captions) ──────────
        float lane = ShellW - TitleBarSpacing.Default.MinDragStrip - 3f * CaptionW;
        float chrome = 8f + 6f + 5f + 35f;   // TabStrip.ChromeFixedWidth(add: true)
        RectF TabsIsland()
        {
            foreach (var r in window.LastTitleBarRegions)
                if (r.Hit == TitleBarHit.Client && r.RectDip.Y < TitleBar.TabRowHeight - 0.5f) return r.RectDip;
            return default;
        }
        static bool All(List<float> ws, float w) { foreach (var x in ws) if (!Near(x, w, 1f)) return false; return ws.Count > 0; }

        var ladder200 = TabCellWidths(host);
        float island200 = TabsIsland().W;
        Check("gate.titlebar.rows.tab-ladder Ladder(200): two 200 tabs, the strip island hugs them (tabs + chrome), the rest of row 1 is drag band",
            ladder200.Count == 2 && All(ladder200, 200f) && Near(island200, chrome + 400f, 1f),
            $"tabs=[{string.Join(",", ladder200)}] island={island200:0.#} expect={chrome + 400f}");

        probe.Form.Value = TabStripForm.Compact(140f, 36f);
        probe.Tabs.Value = TwoRowProbe.MakeTabs(4);
        Settle();
        var compact = TabCellWidths(host);
        bool compactShape = compact.Count == 4 && Near(compact[0], 140f, 1f) && Near(compact[1], 36f, 1f) && Near(compact[3], 36f, 1f);
        Check("gate.titlebar.rows.tab-compact Compact(140, 36): the selected tab keeps its label at 140, the others are 36 icon tabs",
            compactShape && Near(TabsIsland().W, chrome + 140f + 3f * 36f, 1f),
            $"tabs=[{string.Join(",", compact)}] island={TabsIsland().W:0.#}");

        probe.Form.Value = TabStripForm.Ladder(160f, 110f);
        probe.Tabs.Value = TwoRowProbe.MakeTabs(6);
        Settle();
        var roomy = TabCellWidths(host);
        probe.Tabs.Value = TwoRowProbe.MakeTabs(9);
        Settle();
        var squeezed = TabCellWidths(host);
        float squeezedW = (lane - chrome) / 9f;
        bool squeezedScrolls = HasScrollUnder(host, TabsIsland());
        probe.Tabs.Value = TwoRowProbe.MakeTabs(12);
        Settle();
        var full = TabCellWidths(host);
        RectF fullIsland = TabsIsland();
        bool fullScrolls = HasScrollUnder(host, fullIsland);
        Check("gate.titlebar.rows.tab-ladder-narrowing Ladder(160, 110): 160 while they fit, then narrower to share the lane, then 110 and the strip scrolls inside its lane",
            All(roomy, 160f) && All(squeezed, squeezedW) && !squeezedScrolls && All(full, 110f) && fullScrolls
                && fullIsland.Right <= buttonsLeft - TitleBarSpacing.Default.MinDragStrip + 0.5f,
            $"6=[{(roomy.Count > 0 ? roomy[0] : 0):0.#}] 9=[{(squeezed.Count > 0 ? squeezed[0] : 0):0.#} expect {squeezedW:0.#} scroll={squeezedScrolls}] " +
            $"12=[{(full.Count > 0 ? full[0] : 0):0.#} scroll={fullScrolls} islandRight={fullIsland.Right:0.#} limit={buttonsLeft - TitleBarSpacing.Default.MinDragStrip:0.#}]");
    }

    static void KeyedChildrenChecks(StringTable strings)
    {
        Console.WriteLine("\n-- TitleBar: keyed children re-report in the same publication (EP16) --");
        using var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("titlebar-keyed", new Size2((int)ShellW, 300), 1f));
        window.Show();
        var probe = new KeyedProbe();
        using var host = new AppHost(app, window, new HeadlessGpuDevice(), new HeadlessFontSystem(strings), strings, probe);
        for (int i = 0; i < 8; i++) host.RunFrame();

        bool HasClient(RectF live)
        {
            foreach (var r in window.LastTitleBarRegions)
                if (r.Hit == TitleBarHit.Client && Near(r.RectDip.X, live.X, 0.75f) && Near(r.RectDip.W, live.W, 0.75f)) return true;
            return false;
        }

        RectF bell = LiveRectByInk(host, KeyedProbe.BellInk);
        RectF grow0 = LiveRectByInk(host, KeyedProbe.GrowInk);
        RectF mark = LiveRectByInk(host, KeyedProbe.MarkInk);
        var markHit = HitAt(window.LastTitleBarRegions, mark.X + mark.W * 0.5f, mark.Y + mark.H * 0.5f);
        Check("gate.titlebar.keyed.one-row-shell a one-row shell bar reports each keyed child; the Wordmark is drag space",
            bell.W > 0f && HasClient(bell) && HasClient(grow0) && markHit == TitleBarHit.Caption,
            $"bell={bell.W:0.#} grow={grow0.W:0.#} markHit={markHit}");

        // SWAP: the bell (key "bell", 40) becomes a search field (key "search", 200). ONE frame.
        probe.Trail.Value = KeyedProbe.Swapped;
        host.RunFrame();
        RectF search = LiveRectByInk(host, KeyedProbe.SearchInk);
        RectF growAfterSwap = LiveRectByInk(host, KeyedProbe.GrowInk);
        bool bellGone = true;
        foreach (var r in window.LastTitleBarRegions)
            if (r.Hit == TitleBarHit.Client && Near(r.RectDip.W, 40f, 0.5f) && Near(r.RectDip.X, bell.X, 0.5f)) bellGone = false;
        Check("gate.titlebar.keyed.swap a child swap (new key, new width) re-reports in the SAME frame: the new child's rect is Client, the old one is gone, its neighbours moved with it",
            search.W > 199f && HasClient(search) && HasClient(growAfterSwap) && bellGone,
            $"search={search.X:0.#}+{search.W:0.#} grow={growAfterSwap.X:0.#}+{growAfterSwap.W:0.#} bellGone={bellGone}");

        // SELF-RESIZE: a keyed child grows on its OWN signal (no version, no bar re-render). Settle first so no feedback
        // render of the bar (the swap moved the centre column) is still in flight: then ONE frame.
        for (int i = 0; i < 8; i++) host.RunFrame();
        probe.Expanded.Value = true;
        host.RunFrame();
        RectF grown = LiveRectByInk(host, KeyedProbe.GrowInk);
        Check("gate.titlebar.keyed.self-resize a keyed child that resizes on its own state re-reports in the SAME frame (no ContentVersion)",
            grown.W > grow0.W + 100f && HasClient(grown),
            $"before={grow0.W:0.#} live={grown.W:0.#} reported={HasClient(grown)}");
    }
}

/// <summary>The two-row WinUI shell bar: a Chrome tab strip with a live form over a toolbar of keyed children and a
/// window-centred 520 search.</summary>
sealed class TwoRowProbe : Component
{
    public static readonly ColorF MenuInk = ColorF.FromRgba(0x31, 0x01, 0x01, 0xFF);
    public static readonly ColorF FriendsInk = ColorF.FromRgba(0x32, 0x02, 0x02, 0xFF);
    public static readonly ColorF BellInk = ColorF.FromRgba(0x33, 0x03, 0x03, 0xFF);
    public static readonly ColorF CenterInk = ColorF.FromRgba(0x34, 0x04, 0x04, 0xFF);

    public readonly Signal<TabStripForm> Form = new(TabStripForm.Ladder(200f));
    public readonly Signal<IReadOnlyList<TabViewItem>> Tabs = new(MakeTabs(2));

    public static IReadOnlyList<TabViewItem> MakeTabs(int n)
    {
        var items = new TabViewItem[n];
        for (int i = 0; i < n; i++) items[i] = new TabViewItem { Key = "t" + i, Header = "Tab number " + i, Icon = Icons.Home };
        return items;
    }

    static Element Tool(ColorF ink) => new BoxEl { Width = 40f, Height = 36f, Fill = ink, Role = AutomationRole.Button, OnClick = static () => { } };

    static readonly TitleBarItem[] LeadItems =
    [
        new("menu", () => Tool(MenuInk)),
        new("back", () => Tool(ColorF.FromRgba(0x20, 0x20, 0x20))),
        new("forward", () => Tool(ColorF.FromRgba(0x21, 0x21, 0x21))),
    ];

    static readonly TitleBarItem[] TrailItems =
    [
        new("pin", () => Tool(ColorF.FromRgba(0x22, 0x22, 0x22))),
        new("friends", () => Tool(FriendsInk)),
        new("bell", () => Tool(BellInk)),
        new("avatar", () => new BoxEl { Width = 32f, Height = 32f, Fill = ColorF.FromRgba(0x6b, 0x5b, 0x4a) }),
    ];

    public override Element Render() => new BoxEl
    {
        Direction = 1,
        Children =
        [
            TitleBar.Create(new TitleBarOptions
            {
                Rows = 2,
                Tabs = () => Embed.Comp(() => new TabStrip
                {
                    Appearance = TabStripAppearance.Chrome,
                    ItemsSource = () => Tabs.Value,
                    Form = () => Form.Value,
                    TabHeight = 36f,
                    IsAddTabButtonVisible = true,
                    CloseButtonOverlayMode = TabViewCloseButtonOverlayMode.OnPointerOver,
                }),
                Lead = () => LeadItems,
                CenterContent = avail => Embed.Comp(() => new ShellCenterBox { Avail = avail, Ink = CenterInk }),
                CenterAlign = TitleBarCenterAlign.Window,
                TrailingItems = () => TrailItems,
                ShowCaptionButtons = true,
            }),
            new BoxEl { Grow = 1f },
        ],
    };
}

/// <summary>A search stand-in: 520 wide, clamped to the bar's live centre width.</summary>
sealed class ShellCenterBox : Component
{
    public IReadSignal<float>? Avail;
    public ColorF Ink;

    public override Element Render() => new BoxEl
    {
        Width = MathF.Min(520f, Avail?.Value ?? 520f), Height = 32f, Fill = Ink,
    };
}

/// <summary>A one-row shell bar (tabs off): a wordmark, and trailing keyed children held in a signal, one of which
/// resizes on its own state.</summary>
sealed class KeyedProbe : Component
{
    public static readonly ColorF BellInk = ColorF.FromRgba(0x41, 0x01, 0x01, 0xFF);
    public static readonly ColorF SearchInk = ColorF.FromRgba(0x42, 0x02, 0x02, 0xFF);
    public static readonly ColorF GrowInk = ColorF.FromRgba(0x43, 0x03, 0x03, 0xFF);
    public static readonly ColorF MarkInk = ColorF.FromRgba(0x44, 0x04, 0x04, 0xFF);

    public static readonly Signal<bool> SharedExpanded = new(false);
    public Signal<bool> Expanded => SharedExpanded;

    static readonly TitleBarItem Grow = new("grow", () => Embed.Comp(() => new SelfSizingChild()));
    static readonly TitleBarItem Avatar = new("avatar", () => new BoxEl { Width = 32f, Height = 32f, Fill = ColorF.FromRgba(0x6b, 0x5b, 0x4a) });

    public static readonly IReadOnlyList<TitleBarItem> Initial =
    [
        new("bell", () => new BoxEl { Width = 40f, Height = 36f, Fill = BellInk, Role = AutomationRole.Button, OnClick = static () => { } }),
        Grow,
        Avatar,
    ];

    public static readonly IReadOnlyList<TitleBarItem> Swapped =
    [
        new("search", () => new BoxEl { Width = 200f, Height = 32f, Fill = SearchInk }),
        Grow,
        Avatar,
    ];

    public readonly Signal<IReadOnlyList<TitleBarItem>> Trail = new(Initial);

    public KeyedProbe() => SharedExpanded.Value = false;

    public override Element Render() => new BoxEl
    {
        Direction = 1,
        Children =
        [
            TitleBar.Create(new TitleBarOptions
            {
                Wordmark = () => new BoxEl { Width = 64f, Height = 16f, Fill = MarkInk },
                TrailingItems = () => Trail.Value,
                ShowCaptionButtons = true,
            }),
            new BoxEl { Grow = 1f },
        ],
    };

    sealed class SelfSizingChild : Component
    {
        public override Element Render() => new BoxEl
        {
            Width = SharedExpanded.Value ? 420f : 180f, Height = 32f, Fill = GrowInk,
        };
    }
}
