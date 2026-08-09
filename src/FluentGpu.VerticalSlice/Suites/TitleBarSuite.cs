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
//  The custom title bar's NON-CLIENT REGION REPORT — the one contract that decides, for every pixel of the bar,
//  whether a press starts a window drag (Caption), lands on a control (Client), or opens the Win11 snap flyout
//  (Min/Max/Close). It is an ORDERED list: WM_NCHITTEST takes the FIRST match (Win32Platform.HitTestRegions), so the
//  interactive islands must precede the buttons and the whole-bar Caption catch-all must be last.
//
//  Nothing asserted this before — HeadlessWindow.LastTitleBarRegions existed and was read by no gate. These checks
//  drive the MERGED one-row bar (tabs + centre + trailing + captions) headlessly and assert the report itself.
// ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────
public static class TitleBarSuite
{
    const float BarW = 1400f;
    const float MinDragStrip = 48f;      // TitleBar.MinDragStrip (private) — the guaranteed drag column before the captions
    const int RegionBufferCapacity = 12; // TitleBar._regions.Length (private) — the report must never need more

    public static void Run(StringTable strings)
    {
        Console.WriteLine("\n-- TitleBar: merged-row non-client region report --");

        using var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("titlebar-regions", new Size2((int)BarW, 300), 1f));
        window.Show();
        var fonts = new HeadlessFontSystem(strings);
        var probe = new MergedTitleBarProbe();
        using var host = new AppHost(app, window, new HeadlessGpuDevice(), fonts, strings, probe);

        void Settle(int frames = 8) { for (int i = 0; i < frames; i++) host.RunFrame(); }
        Settle();

        var regions = window.LastTitleBarRegions;

        // ── (e) the report fits the control's reused, allocation-free buffer ──────────────────────────────────────
        Check("gate.titlebar.regions.buffer merged report fits the TitleBar region buffer (no growth, no per-push alloc)",
            regions.Length > 0 && regions.Length <= RegionBufferCapacity,
            $"count={regions.Length} capacity={RegionBufferCapacity}");

        // ── (a) ORDER IS THE CONTRACT: islands → buttons → the whole-bar Caption catch-all, last ──────────────────
        int firstButton = -1, lastClient = -1, captionIdx = -1, captionCount = 0;
        for (int i = 0; i < regions.Length; i++)
        {
            switch (regions[i].Hit)
            {
                case TitleBarHit.Client: lastClient = i; break;
                case TitleBarHit.Caption: captionIdx = i; captionCount++; break;
                default: if (firstButton < 0) firstButton = i; break;
            }
        }
        bool orderOk = lastClient >= 0 && firstButton > lastClient
                    && captionCount == 1 && captionIdx == regions.Length - 1;
        // …and the islands themselves are reported LEFT-to-RIGHT (an out-of-order island is a latent overlap bug).
        bool leftToRight = true;
        for (int i = 1; i <= lastClient; i++)
            if (regions[i].Hit == TitleBarHit.Client && regions[i - 1].Hit == TitleBarHit.Client
                && regions[i].RectDip.X < regions[i - 1].RectDip.X) leftToRight = false;
        // The catch-all must actually span the bar (otherwise the uncovered remainder falls through to HTCLIENT).
        RectF caption = captionIdx >= 0 ? regions[captionIdx].RectDip : default;
        bool captionSpans = caption.W >= BarW - 1f && caption.H >= TitleBar.ExpandedHeight - 1f;
        Check("gate.titlebar.regions.order islands (L→R) → Min/Max/Close → the whole-bar Caption catch-all LAST",
            orderOk && leftToRight && captionSpans,
            $"lastClient={lastClient} firstButton={firstButton} captionIdx={captionIdx}/{regions.Length - 1} " +
            $"captions={captionCount} l2r={leftToRight} captionW={caption.W:0.#}");

        // ── (b) no island may reach into the caption-button cluster ──────────────────────────────────────────────
        float buttonsLeft = float.PositiveInfinity;
        int buttonCount = 0;
        foreach (var r in regions)
            if (r.Hit is TitleBarHit.MinButton or TitleBarHit.MaxButton or TitleBarHit.CloseButton)
            { buttonsLeft = MathF.Min(buttonsLeft, r.RectDip.X); buttonCount++; }
        bool islandsClear = true;
        foreach (var r in regions)
            if (r.Hit == TitleBarHit.Client && r.RectDip.Right > buttonsLeft + 0.5f) islandsClear = false;
        // The three buttons are flush to the right edge (the WinUI contract: the caption cluster never moves).
        float buttonsRight = 0f;
        foreach (var r in regions)
            if (r.Hit is TitleBarHit.MinButton or TitleBarHit.MaxButton or TitleBarHit.CloseButton)
                buttonsRight = MathF.Max(buttonsRight, r.RectDip.Right);
        Check("gate.titlebar.regions.no-overlap every Client island ends before the caption cluster, which stays flush right",
            buttonCount == 3 && islandsClear && Near(buttonsRight, BarW, 1.5f),
            $"buttons={buttonCount} buttonsLeft={buttonsLeft:0.#} buttonsRight={buttonsRight:0.#} clear={islandsClear}");

        // ── (c) a real, grabbable Caption band survives BETWEEN the islands ───────────────────────────────────────
        // Resolve the bar's mid-line exactly the way WM_NCHITTEST does (first match wins) and measure the longest
        // contiguous Caption run that is NOT the trailing caption strip — i.e. drag space between two islands.
        float dragRun = LongestInteriorCaptionRun(regions, buttonsLeft);
        Check("gate.titlebar.regions.drag-band a >= MinDragStrip Caption run survives BETWEEN the merged islands",
            dragRun >= MinDragStrip, $"longestInteriorCaptionRun={dragRun:0.#} min={MinDragStrip}");

        // ── (d) STALE-REGION REGRESSION: an island that resizes on its OWN state re-pushes only on ContentVersion ──
        // The centre island is its own component: flipping its signal re-renders IT, relayouts the bar, and leaves the
        // TitleBar's region-push layout effect untouched (none of its deps moved). That is the whole trap.
        RectF CenterRegion()
        {
            // The centre island is the Client region between the tabs island and the trailing island: index 2 of the
            // merged report (back? no / pane / tabs / centre / trailing) — resolve it by rect instead of by index.
            RectF best = default;
            foreach (var r in window.LastTitleBarRegions)
                if (r.Hit == TitleBarHit.Client && r.RectDip.W > best.W && r.RectDip.X > 100f) best = r.RectDip;
            return best;
        }

        RectF beforeRegion = CenterRegion();
        probe.Expanded.Value = true;                 // island expands 180 → 420 DIP, WITHOUT a version bump
        Settle();
        RectF staleRegion = CenterRegion();
        float liveW = LiveCenterIslandWidth(host);
        bool wentStale = Near(staleRegion.W, beforeRegion.W, 0.5f) && liveW > beforeRegion.W + 100f;

        probe.Version.Value++;                       // …now tell the bar its content changed
        Settle();
        RectF freshRegion = CenterRegion();
        bool repushed = freshRegion.W > staleRegion.W + 100f && Near(freshRegion.W, liveW, 1.5f);

        Check("gate.titlebar.regions.content-version a self-resizing island leaves a STALE region until ContentVersion bumps",
            wentStale && repushed,
            $"before={beforeRegion.W:0.#} stale={staleRegion.W:0.#} live={liveW:0.#} fresh={freshRegion.W:0.#} " +
            $"wentStale={wentStale} repushed={repushed}");

        // …and the re-push kept every other invariant (order + the drag band) intact at the new size.
        var after = window.LastTitleBarRegions;
        float afterButtonsLeft = float.PositiveInfinity;
        foreach (var r in after)
            if (r.Hit is TitleBarHit.MinButton or TitleBarHit.MaxButton or TitleBarHit.CloseButton)
                afterButtonsLeft = MathF.Min(afterButtonsLeft, r.RectDip.X);
        bool afterClear = true;
        foreach (var r in after)
            if (r.Hit == TitleBarHit.Client && r.RectDip.Right > afterButtonsLeft + 0.5f) afterClear = false;
        Check("gate.titlebar.regions.stable-after-repush the widened island still clears the captions and keeps a drag band",
            afterClear && after[^1].Hit == TitleBarHit.Caption
                && LongestInteriorCaptionRun(after, afterButtonsLeft) >= MinDragStrip && after.Length <= RegionBufferCapacity,
            $"clear={afterClear} last={after[^1].Hit} run={LongestInteriorCaptionRun(after, afterButtonsLeft):0.#} count={after.Length}");

        RailBaselineChecks(strings);
    }

    /// <summary>ShowRailBaseline=false drops only the 1-DIP seam INK: the drag bands (and therefore the whole region
    /// report) are untouched. A flag that also removed the bands would silently hand the caption strip to HTCLIENT.</summary>
    static void RailBaselineChecks(StringTable strings)
    {
        (int Hairlines, TitleBarRegion[] Regions) Mount(bool rail)
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("titlebar-rail", new Size2((int)BarW, 300), 1f));
            window.Show();
            using var host = new AppHost(app, window, new HeadlessGpuDevice(),
                new HeadlessFontSystem(strings), strings, new RailFlagProbe { Rail = rail });
            for (int i = 0; i < 8; i++) host.RunFrame();

            int lines = 0;
            void Walk(NodeHandle n)
            {
                if (n.IsNull) return;
                RectF b = host.Scene.Bounds(n);
                ref var p = ref host.Scene.Paint(n);
                if (p.VisualKind == VisualKind.Box && b.H is > 0f and <= 1.01f && b.W > 1f && p.Fill.A > 0f) lines++;
                for (var c = host.Scene.FirstChild(n); !c.IsNull; c = host.Scene.NextSibling(c)) Walk(c);
            }
            Walk(host.Scene.Root);
            return (lines, window.LastTitleBarRegions);
        }

        var on = Mount(true);
        var off = Mount(false);
        bool sameReport = on.Regions.Length == off.Regions.Length;
        if (sameReport)
            for (int i = 0; i < on.Regions.Length; i++)
                if (on.Regions[i].Hit != off.Regions[i].Hit
                    || !Near(on.Regions[i].RectDip.X, off.Regions[i].RectDip.X, 0.5f)
                    || !Near(on.Regions[i].RectDip.W, off.Regions[i].RectDip.W, 0.5f)) sameReport = false;

        Check("gate.titlebar.rail-baseline ShowRailBaseline=false removes the seam ink and NOTHING else (drag bands + region report identical)",
            on.Hairlines > 0 && off.Hairlines == 0 && sameReport,
            $"inkOn={on.Hairlines} inkOff={off.Hairlines} regions={on.Regions.Length}/{off.Regions.Length} identical={sameReport}");
    }

    /// <summary>The longest contiguous x-run on the bar's mid-line that resolves to <see cref="TitleBarHit.Caption"/>
    /// under FIRST-MATCH-WINS, restricted to the area LEFT of the caption buttons — i.e. real drag space between the
    /// islands, not the trailing strip the buttons sit in.</summary>
    static float LongestInteriorCaptionRun(TitleBarRegion[] regions, float buttonsLeft)
    {
        float y = TitleBar.ExpandedHeight * 0.5f;
        float best = 0f, run = 0f;
        float limit = float.IsInfinity(buttonsLeft) ? BarW : buttonsLeft;
        for (float x = 0f; x < limit; x += 1f)
        {
            TitleBarHit hit = TitleBarHit.Client;
            bool matched = false;
            foreach (var r in regions)
            {
                RectF q = r.RectDip;
                if (x >= q.X && x < q.Right && y >= q.Y && y < q.Bottom) { hit = r.Hit; matched = true; break; }
            }
            if (matched && hit == TitleBarHit.Caption) { run += 1f; if (run > best) best = run; }
            else run = 0f;
        }
        return best;
    }

    /// <summary>The centre island's LIVE laid-out width straight from the scene (what the region SHOULD say).</summary>
    static float LiveCenterIslandWidth(AppHost host)
    {
        float w = 0f;
        void Walk(NodeHandle n)
        {
            if (n.IsNull) return;
            if (host.Scene.Paint(n).VisualKind == VisualKind.Box
                && host.Scene.Paint(n).Fill == MergedCenterIsland.Ink)
            {
                float bw = host.Scene.AbsoluteRect(n).W;
                if (bw > w) w = bw;
            }
            for (var c = host.Scene.FirstChild(n); !c.IsNull; c = host.Scene.NextSibling(c)) Walk(c);
        }
        Walk(host.Scene.Root);
        return w;
    }
}

/// <summary>The merged one-row title bar under test: nav cluster + text tab strip + a self-resizing centre island +
/// a hugging trailing identity island + engine-drawn caption buttons.</summary>
sealed class MergedTitleBarProbe : Component
{
    public readonly Signal<bool> Expanded = new(false);
    public readonly Signal<int> Version = new(0);

    public override Element Render() => new BoxEl
    {
        Direction = 1,
        Children =
        [
            Embed.Comp(() => new TitleBar
            {
                Title = "probe",
                ShowPaneToggle = true,
                Tabs = () => Embed.Comp(() => new TabStrip
                {
                    Appearance = TabStripAppearance.Text,
                    Items =
                    [
                        new TabViewItem { Header = "home", IsClosable = false },
                        new TabViewItem { Header = "library", IsClosable = false },
                    ],
                    IsAddTabButtonVisible = false,
                    MinTabWidth = 80f,
                    MaxTabWidth = 160f,
                }),
                TabsVersion = () => 0,
                CenterContent = _ => Embed.Comp(() => new MergedCenterIsland { Expanded = Expanded }),
                Trailing = () => new BoxEl { Width = 32f, Height = 32f, Fill = ColorF.FromRgba(0x40, 0x40, 0x40) },
                ContentVersion = () => Version.Value,
                ShowCaptionButtons = true,
            }),
            new BoxEl { Grow = 1f },
        ],
    };
}

/// <summary>The same merged bar, parameterised on the rail-seam flag only.</summary>
sealed class RailFlagProbe : Component
{
    public bool Rail = true;

    public override Element Render() => new BoxEl
    {
        Direction = 1,
        Children =
        [
            Embed.Comp(() => new TitleBar
            {
                Title = "rail",
                Tabs = () => Embed.Comp(() => new TabStrip
                {
                    Appearance = TabStripAppearance.Text,
                    Items = [new TabViewItem { Header = "one", IsClosable = false }],
                    IsAddTabButtonVisible = false,
                    MinTabWidth = 80f,
                    MaxTabWidth = 160f,
                }),
                TabsVersion = () => 0,
                CenterContent = _ => new BoxEl { Width = 200f, Height = 32f, Fill = ColorF.FromRgba(0x22, 0x22, 0x22) },
                Trailing = () => new BoxEl { Width = 32f, Height = 32f, Fill = ColorF.FromRgba(0x40, 0x40, 0x40) },
                ContentVersion = () => 0,
                ShowRailBaseline = Rail,
                ShowCaptionButtons = true,
            }),
            new BoxEl { Grow = 1f },
        ],
    };
}

/// <summary>An island that resizes on its OWN signal — the exact shape (a search box that expands on focus) whose
/// relayout the TitleBar cannot observe without <see cref="TitleBar.ContentVersion"/>.</summary>
sealed class MergedCenterIsland : Component
{
    public static readonly ColorF Ink = ColorF.FromRgba(0x11, 0x22, 0x33, 0xFF);
    public Signal<bool>? Expanded;

    public override Element Render() => new BoxEl
    {
        Width = Expanded is { } e && e.Value ? 420f : 180f,
        Height = 32f,
        Fill = Ink,
    };
}
