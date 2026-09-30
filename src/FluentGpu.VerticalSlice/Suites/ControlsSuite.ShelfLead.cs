using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using FluentGpu.Animation;
using FluentGpu.Dsl;
using FluentGpu.Forms;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Hosting;
using FluentGpu.Media;
using FluentGpu.Pal;
using FluentGpu.Input;
using FluentGpu.Layout;
using FluentGpu.Pal.Headless;
using FluentGpu.Reconciler;
using FluentGpu.Controls;
using FluentGpu.Render;
using FluentGpu.Rhi;
using FluentGpu.Rhi.Headless;
using FluentGpu.Scene;
using FluentGpu.Signals;
using FluentGpu.Text;
using FluentGpu.Text.Headless;
using static FluentGpu.Dsl.Ui;
using static FluentGpu.VerticalSlice.Harness.Gate;
using static FluentGpu.VerticalSlice.Harness.Asserts;

// ── E7: PagedShelf's LEAD SPAN (Controls/PagedShelf.cs + Engine/Scene/VirtualLayout.cs — FillRowVirtualLayout's
// SetLeadSpan/EffectiveLeadSpan/CellsOf/FirstItemOfPage) — a wide "hero" first card (CoverShelf/MixedCovers's lead
// when it carries a header image; item 0 occupies `leadSpan` cells instead of one). These gates pin the four things
// E7 promises: (1) page count and the page boundary's ITEM shift with the span while the page STRIDE (pixels) stays
// a card edge; (2) `FillRowVirtualLayout.ItemRect` being span-aware makes XY keyboard nav walk the wide lead and its
// neighbours correctly with NO shelf-side special case; (3) `leadSpan` is a LIVE prop — 1→2→1 with no remount, same
// as PagedShelf's Title/Header; (4) all of it stays zero-alloc on a steady frame.
static partial class ControlsSuite
{
    static void ShelfLeadChecks(StringTable strings)
    {
        ShelfLeadPagesChecks(strings);
        ShelfLeadKeyboardChecks(strings);
        ShelfLeadLiveChecks(strings);
        ShelfLeadAllocChecks(strings);
        ShelfLeadTemplateChecks(strings);
        ShelfLeadMinColumnsChecks(strings);
    }

    // Uniform 100-DIP cards, 10-DIP gaps, fixed width — no fit ambiguity (mirrors ControlsSuite.ShelfController.cs's
    // own CardW/Gap, renamed to avoid a duplicate-member clash across the merged partial class).
    const float LCardW = 100f, LGap = 10f;

    /// <summary>Wait for a viewport's scroll to reach a TRUE rest (Motion.IsMoving == false) instead of guessing a
    /// frame count — the same idiom as ScrollSuite.SettleScroll, local here since that one lives in a different
    /// suite. An animated bring-into-view (PagedShelf's page-nav glide) can outlast a handful of frames on a stride
    /// this large; polling the actual motion state is what makes the assertion honest rather than timing-sensitive.</summary>
    static void SettleScrollIdle(AppHost host, NodeHandle vp, int maxFrames = 200)
    {
        for (int i = 0; i < maxFrames; i++)
        {
            host.RunFrame();
            if (host.Scene.TryGetScroll(vp, out var s) && !s.Motion.IsMoving && !s.UserScrollActive && i > 2) return;
        }
    }

    // ── (1) Page count + stride ──────────────────────────────────────────────────────────────────────────────────

    sealed class LeadPagesProbe : Component
    {
        public readonly ShelfController Controller = new();
        public readonly IReadOnlyList<CtlItem> Items;
        readonly int _leadSpan;
        public LeadPagesProbe(int itemCount, int leadSpan) { Items = CtlItems(itemCount); _leadSpan = leadSpan; }

        public override Element Render() => new BoxEl
        {
            Direction = 1, Width = 650f,   // Fit(650,100,100,10,fixed=100) = floor(660/110) = 6 cols/page
            Children =
            [
                PagedShelf.Create(Items,
                    (item, _, width) => new BoxEl { Width = width, Height = 44f, Children = [Text(item.Title)] },
                    cardHeight: static _ => 44f,
                    pager: ShelfPager.None,
                    minCardW: LCardW, maxCardW: LCardW, fixedCardW: LCardW, gap: LGap,
                    headerGap: 0f, edgeFade: 0f,
                    keyOf: static (item, _) => item.Id.ToString(),
                    controller: Controller,
                    leadSpan: _leadSpan) with { Key = "shelf" },
            ],
        };
    }

    static (AppHost host, HeadlessWindow window, LeadPagesProbe probe) NewLeadPagesHarness(
        StringTable strings, string name, int itemCount, int leadSpan)
    {
        var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc(name, new Size2(700, 200), 1f));
        window.Show();
        var probe = new LeadPagesProbe(itemCount, leadSpan);
        var host = new AppHost(app, window, new HeadlessGpuDevice(), new HeadlessFontSystem(strings), strings, probe);
        return (host, window, probe);
    }

    static void ShelfLeadPagesChecks(StringTable strings)
    {
        // (a) The span PUSHES a whole extra page once its extra cell tips the count past a page boundary: 18 items at
        // 6 cols/page lands EXACTLY on 3 pages unspanned (ceil(18/6) = 3), but a 2-span lead needs 4 (CellsOf(18) =
        // 18+2-1 = 19 ⇒ ceil(19/6) = 4) — the very scenario VirtualLayoutLeadSpanTests pins as pure math, now proven
        // through a running shelf's own Controller.PageCount.
        var (hostSpanned, _, probeSpanned) = NewLeadPagesHarness(strings, "shelf-lead-pages-spanned", 18, 2);
        using var _s = hostSpanned;
        Settle(hostSpanned);
        int spannedPages = probeSpanned.Controller.PageCount.Peek();

        var (hostPlain, _, probePlain) = NewLeadPagesHarness(strings, "shelf-lead-pages-plain", 18, 1);
        using var _p = hostPlain;
        Settle(hostPlain);
        int plainPages = probePlain.Controller.PageCount.Peek();

        // (b) The page STRIDE (pixels) is UNCHANGED — still 6 cols × 110 = 660 px — even though the span shifted
        // which ITEM the boundary lands on. 11 items, 2-span lead: CellsOf(11) = 12 cells over 6/page ⇒ EXACTLY 2
        // pages (no partial trailing page), so page 1's stride boundary coincides with the content's max scroll
        // offset exactly — no clamp ambiguity to reason about.
        var (hostStride, _, probeStride) = NewLeadPagesHarness(strings, "shelf-lead-pages-stride", 11, 2);
        using var _t = hostStride;
        Settle(hostStride);
        var scene = hostStride.Scene;
        var viewport = FindScrollNode(scene, scene.Root);
        int pageCountAtStride = probeStride.Controller.PageCount.Peek();

        probeStride.Controller.GoTo(1);
        // GoTo animates (ScrollMove.Glide — PagedShelf's bring-into-view animates a NAV, never a re-fit), so the
        // strip doesn't land on the boundary within a handful of frames the way an Immediate ScrollTo would; wait for
        // the viewport's own Motion to report a true rest (mirrors ScrollSuite.SettleScroll's idiom) instead of
        // guessing a frame count — a fixed 12-frame Settle here previously caught the glide mid-flight (544px, not
        // yet at the 660px target) and looked like a stride bug that wasn't one.
        SettleScrollIdle(hostStride, viewport);
        scene.TryGetScroll(viewport, out var atOne);
        bool strideLanded = MathF.Abs(atOne.OffsetX - 660f) < 1f;
        bool lastPage = probeStride.Controller.Page.Peek() == 1 && !probeStride.Controller.CanNext.Peek();

        Check("gate.shelf.lead.pages a 2-span lead grows the page count once its extra cell tips a boundary (18 items: 4 pages spanned vs 3 plain), and the page STRIDE stays a card edge (11 items: page 1 lands exactly at 660px, still the last page)",
            spannedPages == 4 && plainPages == 3 && pageCountAtStride == 2 && strideLanded && lastPage,
            $"spanned18={spannedPages} plain18={plainPages} strideCount={pageCountAtStride} strideOffset={atOne.OffsetX:0} lastPage={lastPage}");
    }

    // ── (2) XY keyboard navigation ────────────────────────────────────────────────────────────────────────────────
    //
    // PagedShelf's own `cardAt` template does NOT wire ItemsView's per-row keyboard/current machinery: it hands the
    // caller a plain (item, index, width) => Element and PagedShelfCore.ShelfCardSlot never touches
    // BoundItemScope<T>.Row.OnInteraction/OnFocusChanged. That wiring is required for ItemsView's arrow-key
    // OnRootKey handler to have a `current` item to navigate FROM at all (a hand-authored Focusable+OnClick BoxEl,
    // as this gate first tried, gets real engine focus on click but never touches ItemsView.current — so
    // OnRootKey's `from = current.Peek()` stays -1 and every arrow key no-ops). This is a PRE-EXISTING PagedShelf
    // gap, not something E7 introduces or can fix from VirtualLayout.cs/PagedShelf.cs alone — so this gate proves
    // the actual E7 claim (FillRowVirtualLayout.ItemRect being span-aware ⇒ ItemsView's NavigateGeometric walks a
    // wide lead correctly) directly against ItemsView.CreateBound, wired the documented way
    // (SelectorVisualsBound.None — RowScope.OnInteraction/OnFocusChanged on the row root, the same recipe
    // ItemContainer.Build uses; see SelectorVisualsBound.cs's RowScope doc comment).
    sealed class LeadKeyboardProbe : Component
    {
        static readonly Signal<IReadOnlyList<int>> Ids = new(new int[] { 0, 1, 2, 3, 4, 5 });
        // Same layout FillRowVirtualLayout PagedShelf itself hoists (create once, reuse) — Rows=1, fixed 100-DIP
        // cards, 10-DIP gap, a 2-span lead. Wide enough (950) that all 7 cells (CellsOf(6)=6+2-1=7) fit on one page.
        readonly FillRowVirtualLayout Layout = new(LCardW, LCardW, LGap, rows: 1, fixedCardW: LCardW);

        public LeadKeyboardProbe() => Layout.SetLeadSpan(2);

        public override Element Render() => new BoxEl
        {
            Direction = 1, Width = 950f,
            Children =
            [
                new BoxEl
                {
                    Height = 44f,   // the strip's cross size — FillRowVirtualLayout.SetViewport needs it
                    Children =
                    [
                        ItemsView.CreateBound(BoundItems.From(Ids, -1),
                            scope =>
                            {
                                var row = scope.Row;
                                return SelectorVisualsBound.None(in row, new BoxEl
                                {
                                    Direction = 1, Grow = 1f,
                                    AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                                    Children = [Text(scope.Item.Peek().ToString())],
                                });
                            },
                            RepeatLayout.Custom(Layout, horizontal: true),
                            new ListOptions<int> { SelectionMode = ItemsSelectionMode.None, Grow = 1f }) with { Key = "shelf" },
                    ],
                },
            ],
        };
    }

    static void ShelfLeadKeyboardChecks(StringTable strings)
    {
        var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("shelf-lead-keyboard", new Size2(1000, 200), 1f));
        window.Show();
        var probe = new LeadKeyboardProbe();
        using var host = new AppHost(app, window, new HeadlessGpuDevice(), new HeadlessFontSystem(strings), strings, probe);
        Settle(host);
        var scene = host.Scene;

        var buttons = Roles(scene, AutomationRole.Button);
        bool sixButtons = buttons.Count == 6;
        // The lead cell is visibly WIDER than a plain card — proof the span reached the arranged geometry this
        // test's keyboard nav then walks (2*100+10 = 210 vs a plain card's 100).
        bool leadWider = sixButtons && scene.Bounds(buttons[0]).W > scene.Bounds(buttons[1]).W + 50f;

        if (sixButtons) ClickNode(host, window, buttons[0]);   // focus the lead item
        Settle(host);
        bool startsOnLead = FocusedNode(scene, scene.Root) == buttons[0];

        window.QueueInput(new InputEvent(InputKind.Key, default, 0, Keys.Right));
        host.RunFrame();
        bool right1 = FocusedNode(scene, scene.Root) == buttons[1];

        window.QueueInput(new InputEvent(InputKind.Key, default, 0, Keys.Right));
        host.RunFrame();
        bool right2 = FocusedNode(scene, scene.Root) == buttons[2];

        window.QueueInput(new InputEvent(InputKind.Key, default, 0, Keys.Left));
        host.RunFrame();
        bool backToOne = FocusedNode(scene, scene.Root) == buttons[1];

        window.QueueInput(new InputEvent(InputKind.Key, default, 0, Keys.Left));
        host.RunFrame();
        bool backToLead = FocusedNode(scene, scene.Root) == buttons[0];

        Check("gate.shelf.lead.keyboard XY keyboard nav walks the lead's wide cell and its neighbours in visual order for free — FillRowVirtualLayout.ItemRect is span-aware, so NavigateGeometric (the engine's nearest-in-direction scan, RepeatKind.Custom) needs no shelf-side special case",
            sixButtons && leadWider && startsOnLead && right1 && right2 && backToOne && backToLead,
            $"buttons={buttons.Count} leadWider={leadWider} start={startsOnLead} right1={right1} right2={right2} backToOne={backToOne} backToLead={backToLead}");
    }

    // ── (3) leadSpan is LIVE — 1→2→1, no remount ─────────────────────────────────────────────────────────────────

    sealed class LeadLiveProbe : Component
    {
        public readonly Signal<int> LeadSpanSig = new(1);
        public readonly IReadOnlyList<CtlItem> Items = CtlItems(6);

        public override Element Render()
        {
            int span = LeadSpanSig.Value;   // subscribe → a signal write re-renders us, re-pushing PagedShelf's leadSpan
            return new BoxEl
            {
                Direction = 1, Width = 1000f,
                Children =
                [
                    PagedShelf.Create(Items,
                        (item, _, width) => new BoxEl
                        {
                            Role = AutomationRole.Button, Focusable = true,
                            Width = width, Height = 44f, Children = [Text(item.Title)],
                        },
                        cardHeight: static _ => 44f,
                        pager: ShelfPager.None,
                        minCardW: LCardW, maxCardW: LCardW, fixedCardW: LCardW, gap: LGap,
                        headerGap: 0f, edgeFade: 0f,
                        keyOf: static (item, _) => item.Id.ToString(),
                        leadSpan: span) with { Key = "shelf" },
                ],
            };
        }
    }

    static void ShelfLeadLiveChecks(StringTable strings)
    {
        var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("shelf-lead-live", new Size2(1050, 200), 1f));
        window.Show();
        var probe = new LeadLiveProbe();
        using var host = new AppHost(app, window, new HeadlessGpuDevice(), new HeadlessFontSystem(strings), strings, probe);
        Settle(host);
        var scene = host.Scene;
        var viewport0 = FindScrollNode(scene, scene.Root);

        var buttons1 = Roles(scene, AutomationRole.Button);
        float widthAt1 = buttons1.Count > 0 ? scene.Bounds(buttons1[0]).W : -1f;

        probe.LeadSpanSig.Value = 2;
        Settle(host);
        var viewportAt2 = FindScrollNode(scene, scene.Root);
        var buttons2 = Roles(scene, AutomationRole.Button);
        float widthAt2 = buttons2.Count > 0 ? scene.Bounds(buttons2[0]).W : -1f;

        probe.LeadSpanSig.Value = 1;
        Settle(host);
        var viewportAt1Again = FindScrollNode(scene, scene.Root);
        var buttons3 = Roles(scene, AutomationRole.Button);
        float widthBackAt1 = buttons3.Count > 0 ? scene.Bounds(buttons3[0]).W : -1f;

        // The SAME scroll viewport node throughout — a live prop push, never a remount (the ShelfCardSlot/ItemsView
        // identity survives; only the lead cell's width follows the span).
        bool noRemount = !viewport0.IsNull && viewport0 == viewportAt2 && viewportAt2 == viewportAt1Again;
        bool widened = MathF.Abs(widthAt1 - LCardW) < 1f && MathF.Abs(widthAt2 - (2 * LCardW + LGap)) < 1f;
        bool backToPlain = MathF.Abs(widthBackAt1 - LCardW) < 1f;

        Check("gate.shelf.lead.live leadSpan flips 1→2→1 LIVE — no remount (the same scroll viewport node throughout) — and the lead cell's own width follows it each time",
            noRemount && widened && backToPlain,
            $"noRemount={noRemount} w1={widthAt1:0} w2={widthAt2:0} w1again={widthBackAt1:0}");
    }

    // ── (4) Zero-alloc ───────────────────────────────────────────────────────────────────────────────────────────

    static void ShelfLeadAllocChecks(StringTable strings)
    {
        var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("shelf-lead-alloc", new Size2(1000, 200), 1f));
        window.Show();
        var probe = new LeadKeyboardProbe();   // 6 items, leadSpan 2, everything fits on one page
        using var host = new AppHost(app, window, new HeadlessGpuDevice(), new HeadlessFontSystem(strings), strings, probe);
        Settle(host);
        var steady = host.RunFrame();

        Check("gate.shelf.lead.alloc a steady frame on a 2-span-lead shelf (nothing changed since settle) adds zero hot-phase allocation",
            steady.HotPhaseAllocBytes == 0,
            $"steadyBytes={steady.HotPhaseAllocBytes}");
    }

    // ── (5) E21/E22 — the lead TEMPLATE follows the EFFECTIVE span; a per-shelf lead minimum ─────────────────────
    //
    // E21: `PagedShelf.Create(…, leadCardAt:)` builds item 0 only while its span is actually in effect
    // (FillRowVirtualLayout.EffectiveLeadSpan > 1); once the fit narrows past the threshold the shelf mounts cardAt for
    // item 0 like every other item — the template follows the CELL, never the data, so a wide hero is never
    // letterboxed into one square cell. E22: `leadMinColumns` replaces the engine's half-row threshold (2*span+1) with
    // max(span+1, leadMinColumns) — through FillRowVirtualLayout.MinColsFor, the ONE rule the layout's geometry, the
    // shelf's page count and the lead slot's template choice all share.

    const string LeadText = "LEAD-TEMPLATE";

    sealed class LeadTemplateProbe : Component
    {
        public readonly Signal<float> Width;
        public readonly ShelfController Controller = new();
        public readonly IReadOnlyList<CtlItem> Items = CtlItems(8);
        readonly int _leadMinColumns;
        public LeadTemplateProbe(float width, int leadMinColumns) { Width = new(width); _leadMinColumns = leadMinColumns; }

        public override Element Render()
        {
            float w = Width.Value;   // subscribe → a resize re-fits the shelf (6 ↔ 4 ↔ 3 columns at 650/430/320)
            return new BoxEl
            {
                Direction = 1, Width = w,
                Children =
                [
                    PagedShelf.Create(Items,
                        (item, _, width) => new BoxEl
                        {
                            Role = AutomationRole.Button, Width = width, Height = 44f, Children = [Text(item.Title)],
                        },
                        cardHeight: static _ => 44f,
                        pager: ShelfPager.None,
                        minCardW: LCardW, maxCardW: LCardW, fixedCardW: LCardW, gap: LGap,
                        headerGap: 0f, edgeFade: 0f,
                        keyOf: static (item, _) => item.Id.ToString(),
                        controller: Controller,
                        leadSpan: 2,
                        leadCardAt: (item, _, width) => new BoxEl
                        {
                            Role = AutomationRole.Button, Width = width, Height = 44f, Children = [Text(LeadText)],
                        },
                        leadMinColumns: _leadMinColumns) with { Key = "shelf" },
                ],
            };
        }
    }

    /// <summary>The lead's observable state: the arranged CELL width (the slot root ItemsView placed from
    /// FillRowVirtualLayout.ItemRect), the mounted card's own width (the template's width hint), and whether the lead
    /// TEMPLATE (not cardAt) is what item 0 mounted. Role order is pre-order: [slot root 0, card 0, slot root 1, …].</summary>
    static (float Cell, float Card, bool LeadMounted, int Buttons) LeadState(AppHost host, StringTable strings)
    {
        var scene = host.Scene;
        var buttons = Roles(scene, AutomationRole.Button);
        float cell = buttons.Count > 0 ? scene.Bounds(buttons[0]).W : -1f;
        float card = buttons.Count > 1 ? scene.Bounds(buttons[1]).W : -1f;
        bool lead = !FindTextNode(scene, strings, scene.Root, LeadText).IsNull;
        return (cell, card, lead, buttons.Count);
    }

    static (AppHost host, LeadTemplateProbe probe) NewLeadTemplateHarness(StringTable strings, string name, float width, int leadMinColumns)
    {
        var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc(name, new Size2(700, 200), 1f));
        window.Show();
        var probe = new LeadTemplateProbe(width, leadMinColumns);
        var host = new AppHost(app, window, new HeadlessGpuDevice(), new HeadlessFontSystem(strings), strings, probe);
        return (host, probe);
    }

    static void ShelfLeadTemplateChecks(StringTable strings)
    {
        // 650 wide ⇒ 6 columns (Fit: floor(660/110)); the default half-row rule honours a 2-span lead at ≥ 5, so item 0
        // mounts leadCardAt in a 2-cell-wide cell (2*100+10 = 210) with the same width hint.
        var (host, probe) = NewLeadTemplateHarness(strings, "shelf-lead-template", 650f, leadMinColumns: 0);
        using var _ = host;
        Settle(host);
        var scene = host.Scene;
        var viewport0 = FindScrollNode(scene, scene.Root);
        var wide = LeadState(host, strings);

        // Re-fit to 430 ⇒ 4 columns: 4 < 5 collapses the span, so item 0 must now mount cardAt in a PLAIN cell — the
        // template follows the effective span, not the unchanged `leadSpan: 2` request. No remount of the strip.
        probe.Width.Value = 430f;
        Settle(host);
        var viewportNarrow = FindScrollNode(scene, scene.Root);
        var narrow = LeadState(host, strings);

        // Back to 650 ⇒ the lead template remounts in its 2-cell cell.
        probe.Width.Value = 650f;
        Settle(host);
        var viewportWide = FindScrollNode(scene, scene.Root);
        var wideAgain = LeadState(host, strings);

        bool noRemount = !viewport0.IsNull && viewport0 == viewportNarrow && viewportNarrow == viewportWide;
        bool leadAt6 = wide.LeadMounted && Near(wide.Cell, 2 * LCardW + LGap) && Near(wide.Card, 2 * LCardW + LGap);
        bool plainAt4 = !narrow.LeadMounted && Near(narrow.Cell, LCardW) && Near(narrow.Card, LCardW);
        bool leadBackAt6 = wideAgain.LeadMounted && Near(wideAgain.Cell, 2 * LCardW + LGap) && Near(wideAgain.Card, 2 * LCardW + LGap);

        Check("gate.shelf.lead-template-follows-span leadCardAt mounts item 0 only while the EFFECTIVE span holds: a 6-column fit mounts the lead template in a 2-cell cell (210), a re-fit to 4 columns (leadMinColumns 0 ⇒ the half-row rule collapses the span) mounts cardAt in a plain 100 cell, and 6 columns remounts the lead — same strip viewport throughout, the cell AND the card width following the span each time",
            noRemount && leadAt6 && plainAt4 && leadBackAt6,
            $"noRemount={noRemount} at6=(lead={wide.LeadMounted} cell={wide.Cell:0} card={wide.Card:0} buttons={wide.Buttons}) at4=(lead={narrow.LeadMounted} cell={narrow.Cell:0} card={narrow.Card:0}) at6again=(lead={wideAgain.LeadMounted} cell={wideAgain.Cell:0} card={wideAgain.Card:0})");
    }

    static void ShelfLeadMinColumnsChecks(StringTable strings)
    {
        // 430 wide ⇒ 4 columns. leadMinColumns 4 ⇒ MinColsFor(2, 4) = max(3, 4) = 4 ⇒ the 2-span lead is HONOURED at
        // 4 columns (lead + two ordinary cells): a 210 cell, the lead template, and a page count that counts the
        // lead's extra cell — 8 items ⇒ CellsOf(8) = 9 cells over 4/page ⇒ 3 pages.
        var (hostMin4, probeMin4) = NewLeadTemplateHarness(strings, "shelf-lead-min-columns-4", 430f, leadMinColumns: 4);
        using var _a = hostMin4;
        Settle(hostMin4);
        var min4 = LeadState(hostMin4, strings);
        int pagesMin4 = probeMin4.Controller.PageCount.Peek();

        // The SAME fit with the default rule (leadMinColumns 0): 4 < 2*2+1 collapses the span — a plain 100 cell,
        // cardAt, and ceil(8/4) = 2 pages. This is the byte-identical-default half of the contract.
        var (hostMin0, probeMin0) = NewLeadTemplateHarness(strings, "shelf-lead-min-columns-0", 430f, leadMinColumns: 0);
        using var _b = hostMin0;
        Settle(hostMin0);
        var min0 = LeadState(hostMin0, strings);
        int pagesMin0 = probeMin0.Controller.PageCount.Peek();

        // 320 wide ⇒ 3 columns: leadMinColumns 4 is NOT met, so the span collapses here exactly like the default —
        // a plain cell and cardAt (ceil(8/3) = 3 pages, the plain count).
        var (hostMin4Narrow, probeNarrow) = NewLeadTemplateHarness(strings, "shelf-lead-min-columns-4-narrow", 320f, leadMinColumns: 4);
        using var _c = hostMin4Narrow;
        Settle(hostMin4Narrow);
        var narrow = LeadState(hostMin4Narrow, strings);
        int pagesNarrow = probeNarrow.Controller.PageCount.Peek();

        bool honouredAt4 = min4.LeadMounted && Near(min4.Cell, 2 * LCardW + LGap) && Near(min4.Card, 2 * LCardW + LGap) && pagesMin4 == 3;
        bool defaultCollapses = !min0.LeadMounted && Near(min0.Cell, LCardW) && Near(min0.Card, LCardW) && pagesMin0 == 2;
        bool collapsesAt3 = !narrow.LeadMounted && Near(narrow.Cell, LCardW) && Near(narrow.Card, LCardW) && pagesNarrow == 3;

        Check("gate.shelf.lead-min-columns leadMinColumns 4 keeps a 2-span lead at 4 columns — a 210 cell, the lead template, and a page count that counts the lead's extra cell (8 items ⇒ 3 pages, vs 2 with the default half-row rule at the same fit) — while 3 columns still collapses it; FillRowVirtualLayout.MinColsFor is the one rule the layout's geometry, PageCountFor and the lead slot share",
            honouredAt4 && defaultCollapses && collapsesAt3,
            $"min4@4=(lead={min4.LeadMounted} cell={min4.Cell:0} card={min4.Card:0} pages={pagesMin4}) min0@4=(lead={min0.LeadMounted} cell={min0.Cell:0} card={min0.Card:0} pages={pagesMin0}) min4@3=(lead={narrow.LeadMounted} cell={narrow.Cell:0} card={narrow.Card:0} pages={pagesNarrow})");
    }
}
