using System.Buffers;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using FluentGpu.Animation;
using FluentGpu.Forms;
using FluentGpu.Media;
using FluentGpu.Input;
using FluentGpu.Layout;
using FluentGpu.Reconciler;
using FluentGpu.Render;
using FluentGpu.Rhi;
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
using FluentGpu.Scroll.Runtime;
using FluentGpu.Signals;
using FluentGpu.Text;
using FluentGpu.Text.Headless;
using static FluentGpu.Dsl.Ui;
using static FluentGpu.VerticalSlice.Harness.Asserts;
using static FluentGpu.VerticalSlice.Harness.Gate;

// ── E4: the PagedShelf EXTERNAL pager controller (Controls/PagedShelf.cs — ShelfController) ────────────────────────
//
// The app's Home renders the pager (chevrons + PipsPager) in its OWN sticky chapter header, OUTSIDE the shelf
// (`pager: ShelfPager.None`) — so the shelf publishes its page state into a caller-owned ShelfController instead of
// drawing its own header row. These gates pin: (1) GoTo actually moves the bound shelf; (2) a free settle (no
// controller action at all) still syncs the controller's Page — the controller is a MIRROR of the shelf's own page
// signal, not a second source of truth; (3) a resize re-fit republishes PageCount; (4) the controller's action
// delegates and its Publish path stay allocation-free and correctly bound across many renders (no re-mount, no
// per-render churn) — see `docs/design/subsystems/component-props-contract.md` "An external pager is a bound
// instance, not a prop."
static partial class ControlsSuite
{
    static void ShelfControllerChecks(StringTable strings)
    {
        ShelfControllerGoToChecks(strings);
        ShelfControllerSettleSyncChecks(strings);
        ShelfControllerPageCountRefitChecks(strings);
        ShelfControllerStableActionsChecks(strings);
    }

    sealed record CtlItem(int Id, string Title);

    static IReadOnlyList<CtlItem> CtlItems(int count)
    {
        var items = new CtlItem[count];
        for (int i = 0; i < count; i++) items[i] = new CtlItem(i, "ctl-" + i);
        return items;
    }

    // Fixed 100-wide cards, 10 gap, 320-wide viewport ⇒ 3 columns/page (FillRowVirtualLayout.Fit(320,100,100,10) =
    // floor((320+10)/110) = 3); 12 items ⇒ 4 pages; page stride 330 px.
    const float CardW = 100f, Gap = 10f, StartWidth = 320f;
    const int ItemCount = 12;

    /// <summary>The probe: an external "sticky chapter header" (chevrons, Role.Button, wired straight to the
    /// controller) mounted as the shelf's SIBLING — never inside it (pager: ShelfPager.None) — exactly the app shape
    /// E4 exists for.</summary>
    sealed class ControllerProbe : Component
    {
        public readonly Signal<float> Width = new(StartWidth);
        public readonly ShelfController Controller = new();
        public readonly IReadOnlyList<CtlItem> Items = CtlItems(ItemCount);
        public int PrevClicks, NextClicks;

        public override Element Render()
        {
            float w = Width.Value;   // subscribe → live resize re-fits the shelf beneath (gate .pagecount-refit)
            return new BoxEl
            {
                Direction = 1, Width = w,
                Children =
                [
                    new BoxEl
                    {
                        Key = "header", Direction = 0, Gap = 8f,
                        Children =
                        [
                            new BoxEl { Role = AutomationRole.Button, Focusable = true, Width = 24f, Height = 24f,
                                OnClick = () => { PrevClicks++; Controller.Prev(); }, Children = [Text("‹")] },
                            new BoxEl { Role = AutomationRole.Button, Focusable = true, Width = 24f, Height = 24f,
                                OnClick = () => { NextClicks++; Controller.Next(); }, Children = [Text("›")] },
                        ],
                    },
                    PagedShelf.Create(Items,
                        (item, _, width) => new BoxEl { Width = width, Height = 44f, Children = [Text(item.Title)] },
                        cardHeight: static _ => 44f,
                        pager: ShelfPager.None,   // the pager lives OUTSIDE the shelf — the header row above
                        minCardW: CardW, maxCardW: CardW, fixedCardW: CardW, gap: Gap,
                        headerGap: 0f, edgeFade: 0f,
                        keyOf: static (item, _) => item.Id.ToString(),
                        controller: Controller) with { Key = "shelf" },
                ],
            };
        }
    }

    const int MaxSettleFrames = 12;
    static void Settle(AppHost host) { for (int i = 0; i < MaxSettleFrames; i++) host.RunFrame(); }

    static (AppHost host, HeadlessWindow window, ControllerProbe probe) NewHarness(StringTable strings, string name)
    {
        var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc(name, new Size2(640, 400), 1f));
        window.Show();
        var probe = new ControllerProbe();
        var host = new AppHost(app, window, new HeadlessGpuDevice(), new HeadlessFontSystem(strings), strings, probe);
        return (host, window, probe);
    }

    // ── (1) GoTo actually drives the bound shelf — the shelf's real scroll offset moves, CanPrev/CanNext follow, and
    // a header BUTTON wired to Controller.Next/Prev (the whole point of E4) reaches the same shelf. ──────────────────
    static void ShelfControllerGoToChecks(StringTable strings)
    {
        var (host, window, probe) = NewHarness(strings, "shelf-controller-goto");
        using var _ = host;
        Settle(host);
        var scene = host.Scene;
        var viewport = FindScrollNode(scene, scene.Root);
        scene.TryGetScroll(viewport, out var atStart);

        bool startsRight = probe.Controller.PageCount.Peek() == 4 && probe.Controller.Page.Peek() == 0
            && !probe.Controller.CanPrev.Peek() && probe.Controller.CanNext.Peek();

        probe.Controller.GoTo(2);
        Settle(host);
        scene.TryGetScroll(viewport, out var atTwo);
        bool wentTo2 = probe.Controller.Page.Peek() == 2 && probe.Controller.CanPrev.Peek() && probe.Controller.CanNext.Peek()
            && atTwo.OffsetX > atStart.OffsetX + 100f;   // the shelf's OWN scroll actually moved, not just the signal

        // The header's Next BUTTON — mounted OUTSIDE the shelf, wired only through the controller. Taken from the
        // `Key="header"` subtree ONLY: since E9 every shelf card slot root is ALSO Role=Button (the ItemsView roving-focus
        // target), so a whole-scene role sweep returns the two chevrons PLUS the realized cards. The header is the
        // probe's FIRST child, so in the pre-order sweep its chevrons come first — their parent IS the header row, and
        // collecting just that subtree yields exactly the two affordances this gate drives.
        var allButtons = Roles(scene, AutomationRole.Button);
        var buttons = new List<NodeHandle>(2);
        if (allButtons.Count > 0) CollectRole(scene, scene.Parent(allButtons[0]), AutomationRole.Button, buttons);
        bool twoButtons = buttons.Count == 2;
        if (twoButtons) ClickNode(host, window, buttons[1]);   // Next
        Settle(host);
        bool nextButtonAdvanced = probe.Controller.Page.Peek() == 3 && probe.NextClicks == 1 && !probe.Controller.CanNext.Peek();

        if (twoButtons) ClickNode(host, window, buttons[0]);   // Prev
        Settle(host);
        bool prevButtonReceded = probe.Controller.Page.Peek() == 2 && probe.PrevClicks == 1;

        Check("gate.shelf.controller.goto GoTo moves the bound shelf's real scroll offset and republishes CanPrev/CanNext; a header button OUTSIDE the shelf, wired only through the controller, reaches it",
            startsRight && wentTo2 && twoButtons && nextButtonAdvanced && prevButtonReceded,
            $"start(count={probe.Controller.PageCount.Peek()},page={probe.Controller.Page.Peek()},prev={probe.Controller.CanPrev.Peek()},next={probe.Controller.CanNext.Peek()}) " +
            $"goto2(page={probe.Controller.Page.Peek()} offset={atStart.OffsetX:0}->{atTwo.OffsetX:0}) buttons={buttons.Count}(all={allButtons.Count}) nextAdv={nextButtonAdvanced} prevRec={prevButtonReceded}");
    }

    // ── (2) A settle with NO controller action at all — a plain programmatic scroll settling at rest — still syncs
    // Controller.Page. The controller mirrors the shelf's OWN page signal; it is not a second, action-only channel. ──
    static void ShelfControllerSettleSyncChecks(StringTable strings)
    {
        var (host, window, probe) = NewHarness(strings, "shelf-controller-settle-sync");
        using var _ = host;
        Settle(host);
        var scene = host.Scene;
        var viewport = FindScrollNode(scene, scene.Root);
        var handle = host.TryGetScrollHandle(viewport);

        // Page stride = 3 cols × (100+10) = 330 px; land exactly on page 2's boundary (660 px) with NO gesture and
        // NO Controller.GoTo call — a free scroll settling is the only thing that should move this.
        handle?.ScrollTo(660f, ScrollMove.Immediate);
        Settle(host);
        bool settledAt2 = probe.Controller.Page.Peek() == 2;

        // And back to page 0 — the mirror follows BOTH directions.
        handle?.ScrollTo(0f, ScrollMove.Immediate);
        Settle(host);
        bool settledBackAt0 = probe.Controller.Page.Peek() == 0 && !probe.Controller.CanPrev.Peek();

        Check("gate.shelf.controller.settle-sync a programmatic scroll settling at rest (no Controller action) still syncs Controller.Page, in both directions",
            settledAt2 && settledBackAt0,
            $"settledAt2={settledAt2} (page={probe.Controller.Page.Peek()}) settledBackAt0={settledBackAt0}");
    }

    // ── (3) A resize re-fits the shelf's column count, and Controller.PageCount republishes the new count LIVE — no
    // remount, same running shelf. ──────────────────────────────────────────────────────────────────────────────────
    static void ShelfControllerPageCountRefitChecks(StringTable strings)
    {
        var (host, window, probe) = NewHarness(strings, "shelf-controller-pagecount-refit");
        using var _ = host;
        Settle(host);
        var scene = host.Scene;
        var viewport = FindScrollNode(scene, scene.Root);

        int countAt320 = probe.Controller.PageCount.Peek();   // 3 cols/page over 12 items ⇒ 4 pages

        // Narrow to 220 px ⇒ floor((220+10)/110) = 2 cols/page ⇒ 6 pages.
        probe.Width.Value = 220f;
        Settle(host);
        bool sameShelf = FindScrollNode(scene, scene.Root) == viewport;   // re-fit in place, not a remount
        int countAt220 = probe.Controller.PageCount.Peek();

        // Back to 320 — the republish is not one-shot; it tracks every width change.
        probe.Width.Value = StartWidth;
        Settle(host);
        int countBackAt320 = probe.Controller.PageCount.Peek();

        Check("gate.shelf.controller.pagecount-refit a live width change re-fits the shelf's column count and republishes PageCount on the SAME shelf instance, both directions",
            countAt320 == 4 && sameShelf && countAt220 == 6 && countBackAt320 == 4,
            $"320={countAt320} 220={countAt220} back320={countBackAt320} sameShelf={sameShelf}");
    }

    // ── (4) The controller's actions/publish path stay correctly bound and allocation-free across many renders — no
    // per-render delegate churn (they were cached once, at the shelf's construction), no re-mount. ────────────────────
    static void ShelfControllerStableActionsChecks(StringTable strings)
    {
        var (host, window, probe) = NewHarness(strings, "shelf-controller-stable-actions");
        using var _ = host;
        Settle(host);
        var scene = host.Scene;
        var viewport0 = FindScrollNode(scene, scene.Root);

        // Churn the component through several UNRELATED renders (a handful of resizes) — none of them re-mount the
        // shelf, so if GoTo/Prev/Next were bound at construction (not re-captured per render) they still reach the
        // SAME instance afterward.
        for (int i = 0; i < 6; i++)
        {
            probe.Width.Value = 260f + i * 10f;
            host.RunFrame();
        }
        Settle(host);
        var viewportAfterChurn = FindScrollNode(scene, scene.Root);
        bool noRemount = viewportAfterChurn == viewport0 && !viewportAfterChurn.IsNull;

        int pageBefore = probe.Controller.Page.Peek();
        probe.Controller.Next();
        Settle(host);
        bool stillWired = probe.Controller.Page.Peek() == pageBefore + 1;

        // A steady frame (nothing changed since the last settle) must add no hot-phase allocation — Publish's
        // SetIfChanged compares and returns; GoTo/Prev/Next forward through cached delegates; none of it allocates.
        var steady = host.RunFrame();

        Check("gate.shelf.controller.stable-actions the controller's actions stay bound to the same shelf instance across many unrelated renders (no re-mount, no drift), and a steady frame adds no hot-phase allocation",
            noRemount && stillWired && steady.HotPhaseAllocBytes == 0,
            $"noRemount={noRemount} stillWired={stillWired} (page {pageBefore}->{probe.Controller.Page.Peek()}) steadyBytes={steady.HotPhaseAllocBytes}");
    }
}
