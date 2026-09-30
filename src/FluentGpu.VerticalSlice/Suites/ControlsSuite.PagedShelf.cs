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


// ── E9/E10: PagedShelf joins the ItemsView keyboard model, and ShelfLift (Controls/PagedShelf.cs —
// home-redesign-remediation.md §2 E9/E10; F21/F8) ────────────────────────────────────────────────────────────────
//
// E9 closes the pre-existing gap ControlsSuite.ShelfLead.cs's own ShelfLeadKeyboardChecks comment documents: PagedShelf's
// `cardAt` template never wired ItemsView's per-row keyboard/current machinery (BindCard never touched
// BoundItemScope<T>.Row.OnInteraction/OnFocusChanged), so a shelf built through PagedShelf.Create had no `current` item
// for OnRootKey to navigate from at all — arrow keys were a no-op and there was no keyboard/pointer invoke. These gates
// exercise PagedShelf.Create directly (no ItemsView.CreateBound workaround): BindCard now IS the slot root, and
// Create<T> takes an `onInvoke` re-pushed through ShelfProps like CardAt/KeyOf.
//
// E10 (ShelfLift) pins the headerless-shelf flush contract that replaces the app's old `Margin.Top = -12` cancellation
// hack (deleted from the PagedShelf "HEADERLESS SHELVES" doc note alongside these gates).
static partial class ControlsSuite
{
    static void ShelfKeyboardInvokeChecks(StringTable strings)
    {
        ShelfKeyboardArrowsChecks(strings);
        ShelfKeyboardInvokeFiresChecks(strings);
        ShelfKeyboardTabStopChecks(strings);
        ShelfKeyboardPageFollowChecks(strings);
        ShelfKeyboardAllocChecks(strings);
    }

    static void ShelfLiftChecks(StringTable strings)
    {
        ShelfLiftNoneFlushChecks(strings);
        ShelfLiftElevateUnchangedChecks(strings);
    }

    // Reuses ControlsSuite.ShelfController.cs's CtlItem/CtlItems/CardW/Gap/StartWidth/ItemCount/Settle (same partial
    // class): a 320-wide viewport with 100-DIP fixed cards and a 10-DIP gap fits exactly 3 columns/page; 12 items ⇒ 4
    // pages, page stride 330 DIP.

    sealed class KeyboardProbe : Component
    {
        public readonly IReadOnlyList<CtlItem> Items = CtlItems(ItemCount);
        public readonly List<(int Index, int ItemId)> Invocations = new();

        public override Element Render() => new BoxEl
        {
            Direction = 1, Width = StartWidth,
            Children =
            [
                PagedShelf.Create(Items,
                    (item, _, width) => new BoxEl { Width = width, Height = 44f, Children = [Text(item.Title)] },
                    cardHeight: static _ => 44f,
                    pager: ShelfPager.None,
                    minCardW: CardW, maxCardW: CardW, fixedCardW: CardW, gap: Gap,
                    headerGap: 0f, edgeFade: 0f,
                    keyOf: static (item, _) => item.Id.ToString(),
                    onInvoke: (item, index) => Invocations.Add((index, item.Id))) with { Key = "shelf" },
            ],
        };
    }

    static (AppHost host, HeadlessWindow window, KeyboardProbe probe) NewKeyboardHarness(StringTable strings, string name)
    {
        var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc(name, new Size2(640, 200), 1f));
        window.Show();
        var probe = new KeyboardProbe();
        var host = new AppHost(app, window, new HeadlessGpuDevice(), new HeadlessFontSystem(strings), strings, probe);
        return (host, window, probe);
    }

    // ── (1) Right from card 0's focused slot root moves focus to card 1's slot root ─────────────────────────────────
    static void ShelfKeyboardArrowsChecks(StringTable strings)
    {
        var (host, window, probe) = NewKeyboardHarness(strings, "shelf-keyboard-arrows");
        using var _ = host;
        Settle(host);
        var scene = host.Scene;

        var buttons = Roles(scene, AutomationRole.Button);
        bool twoButtons = buttons.Count >= 2;   // the first page's cards (3 columns) are all realized

        if (twoButtons) ClickNode(host, window, buttons[0]);
        Settle(host);
        bool startsOnCard0 = twoButtons && FocusedNode(scene, scene.Root) == buttons[0];

        window.QueueInput(new InputEvent(InputKind.Key, default, 0, Keys.Right));
        host.RunFrame();
        bool right1 = twoButtons && FocusedNode(scene, scene.Root) == buttons[1];

        window.QueueInput(new InputEvent(InputKind.Key, default, 0, Keys.Left));
        host.RunFrame();
        bool backToCard0 = twoButtons && FocusedNode(scene, scene.Root) == buttons[0];

        Check("gate.shelf.keyboard.arrows Right/Left from a shelf card's focused slot root walks to its neighbour's slot root — BindCard now consumes RowScope.OnInteraction/OnFocusChanged (E9), so ItemsView's own roving-focus/OnRootKey model has a `current` item to navigate from",
            twoButtons && startsOnCard0 && right1 && backToCard0,
            $"buttons={buttons.Count} start={startsOnCard0} right1={right1} backToCard0={backToCard0}");
    }

    // ── (2) Enter on the current card calls onInvoke(item, index) exactly once ──────────────────────────────────────
    static void ShelfKeyboardInvokeFiresChecks(StringTable strings)
    {
        var (host, window, probe) = NewKeyboardHarness(strings, "shelf-keyboard-invoke");
        using var _ = host;
        Settle(host);
        var scene = host.Scene;
        var buttons = Roles(scene, AutomationRole.Button);
        bool hasButtons = buttons.Count > 0;

        // The click itself is ALSO a Tap invoke (SelectionMode.None never blocks Tap) — clear it so Enter is isolated.
        if (hasButtons) ClickNode(host, window, buttons[0]);
        Settle(host);
        probe.Invocations.Clear();

        window.QueueInput(new InputEvent(InputKind.Key, default, 0, Keys.Enter));
        host.RunFrame();

        bool invokedOnce = probe.Invocations.Count == 1 && probe.Invocations[0].Index == 0 && probe.Invocations[0].ItemId == 0;

        Check("gate.shelf.keyboard.invoke Enter on the current (focused) card calls onInvoke(item, index) exactly once — PagedShelf.Create's onInvoke re-pushed through ShelfProps and wired onto ListOptions<T>.OnInvokedTyped (E9)",
            hasButtons && invokedOnce,
            $"buttons={buttons.Count} invocations={probe.Invocations.Count}" +
            (probe.Invocations.Count > 0 ? $" first=(index={probe.Invocations[0].Index},id={probe.Invocations[0].ItemId})" : ""));
    }

    // ── (3) exactly one focusable slot root at rest — the roving SINGLE tab stop ────────────────────────────────────
    static void ShelfKeyboardTabStopChecks(StringTable strings)
    {
        var (host, window, probe) = NewKeyboardHarness(strings, "shelf-keyboard-tabstop");
        using var _ = host;
        Settle(host);
        var scene = host.Scene;
        var buttons = Roles(scene, AutomationRole.Button);
        bool hasButtons = buttons.Count > 1;

        int focusableAtRest = 0;
        bool card0FocusableAtRest = false;
        foreach (var b in buttons)
        {
            bool f = (scene.Flags(b) & NodeFlags.Focusable) != 0;
            if (f) focusableAtRest++;
            if (f && b == buttons[0]) card0FocusableAtRest = true;
        }

        // Click card 1 — the roving stop MOVES to it (SetSlotTabStop), toggled imperatively, never a re-render.
        if (hasButtons) ClickNode(host, window, buttons[1]);
        Settle(host);
        int focusableAfterClick = 0;
        bool card1FocusableAfterClick = false;
        foreach (var b in buttons)
        {
            bool f = (scene.Flags(b) & NodeFlags.Focusable) != 0;
            if (f) focusableAfterClick++;
            if (f && b == buttons[1]) card1FocusableAfterClick = true;
        }

        Check("gate.shelf.keyboard.tab-stop exactly one shelf card slot root is Focusable at rest (the un-interacted-with shelf's item 0), and the single roving stop MOVES to whichever card is clicked (SetSlotTabStop) — no card template declares its own Focusable/OnClick",
            hasButtons && focusableAtRest == 1 && card0FocusableAtRest && focusableAfterClick == 1 && card1FocusableAfterClick,
            $"buttons={buttons.Count} focusableAtRest={focusableAtRest}(card0={card0FocusableAtRest}) focusableAfterClick={focusableAfterClick}(card1={card1FocusableAfterClick})");
    }

    // ── (4) moving current past the page edge pages the shelf ───────────────────────────────────────────────────────
    //
    // ItemsView's own arrow navigation only MINIMAL-scrolls the new current card into view (a ~one-card nudge, ~98 DIP
    // here), and the settle re-sync (OnShelfScroll → PageFromOffset) rounds that rest back to page 0 — so on its own an
    // arrow past the boundary never pages the shelf. PagedShelfCore.FollowFocusToPage closes it: a slot gaining
    // KEYBOARD focus (FocusVisual) on another page calls GoToPage, the one pager entry point a chevron uses, so the
    // strip glides onto the page boundary itself (330 DIP = page 1 × 3 columns × 110 stride) and the settle re-sync
    // then agrees — Controller.Page republishes 1.
    sealed class PageFollowProbe : Component
    {
        public readonly ShelfController Controller = new();
        public readonly IReadOnlyList<CtlItem> Items = CtlItems(ItemCount);

        public override Element Render() => new BoxEl
        {
            Direction = 1, Width = StartWidth,
            Children =
            [
                PagedShelf.Create(Items,
                    (item, _, width) => new BoxEl { Width = width, Height = 44f, Children = [Text(item.Title)] },
                    cardHeight: static _ => 44f,
                    pager: ShelfPager.None,
                    minCardW: CardW, maxCardW: CardW, fixedCardW: CardW, gap: Gap,
                    headerGap: 0f, edgeFade: 0f,
                    keyOf: static (item, _) => item.Id.ToString(),
                    controller: Controller) with { Key = "shelf" },
            ],
        };
    }

    static void ShelfKeyboardPageFollowChecks(StringTable strings)
    {
        var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("shelf-keyboard-page-follow", new Size2(640, 200), 1f));
        window.Show();
        var probe = new PageFollowProbe();
        using var host = new AppHost(app, window, new HeadlessGpuDevice(), new HeadlessFontSystem(strings), strings, probe);
        Settle(host);
        var scene = host.Scene;
        var viewport = FindScrollNode(scene, scene.Root);   // the shelf's own viewport — the only scroller here

        var buttons = Roles(scene, AutomationRole.Button);
        bool threeButtons = buttons.Count >= 3;

        if (threeButtons) ClickNode(host, window, buttons[0]);
        Settle(host);
        bool startsPage0 = probe.Controller.Page.Peek() == 0;

        // Right ×3: card 0 → 1 → 2 → 3. Item 3 is the first item of page 1 (3 columns/page) — walking onto it must
        // bring the shelf's own viewport into view of page 1 and, once that scroll settles, page-follow.
        for (int i = 0; i < 3; i++)
        {
            window.QueueInput(new InputEvent(InputKind.Key, default, 0, Keys.Right));
            host.RunFrame();
        }
        SettleScrollIdle(host, viewport);   // the bring-into-view glide + the settle re-sync both need real rest
        // The settle re-sync writes the shelf's page in the passive effect of the frame the glide RESTS on; the
        // controller republishes it on the shelf's next render — give that render its frames before reading Page.
        Settle(host);

        int pageAfterArrows = probe.Controller.Page.Peek();
        bool followedToPage1 = pageAfterArrows == 1;
        // Page-ALIGNED rest, not a minimal nudge that merely rounds to page 1: the glide is GoToPage's own.
        float restX = scene.TryGetScroll(viewport, out var rest) ? rest.OffsetX : float.NaN;
        bool pageAligned = Near(restX, 3 * (CardW + Gap), 1f);

        Check("gate.shelf.keyboard.page-follow arrowing `current` past the page-0/page-1 boundary pages the shelf: the card gaining KEYBOARD focus on page 1 drives GoToPage (PagedShelfCore.FollowFocusToPage), the strip glides onto the page-1 boundary, and the settle re-sync (OnShelfScroll) republishes Controller.Page to 1",
            threeButtons && startsPage0 && followedToPage1 && pageAligned,
            $"buttons={buttons.Count} startPage={(threeButtons ? "0" : "n/a")} pageAfterArrows={pageAfterArrows}(exp 1) restX={restX:0.#}(exp {3 * (CardW + Gap):0})");
    }

    // ── (5) zero-alloc while arrowing between two already-realized, same-page cards ─────────────────────────────────
    static void ShelfKeyboardAllocChecks(StringTable strings)
    {
        var (host, window, probe) = NewKeyboardHarness(strings, "shelf-keyboard-alloc");
        using var _ = host;
        Settle(host);
        var scene = host.Scene;
        var buttons = Roles(scene, AutomationRole.Button);
        bool hasButtons = buttons.Count > 1;
        if (hasButtons) ClickNode(host, window, buttons[0]);
        Settle(host);

        window.QueueInput(new InputEvent(InputKind.Key, default, 0, Keys.Right));
        var arrowFrame = host.RunFrame();

        Check("gate.shelf.keyboard.alloc arrowing between two already-realized, same-page shelf cards (no scroll/page-follow needed — pure focus move + roving-stop toggle) adds zero hot-phase allocation",
            hasButtons && arrowFrame.HotPhaseAllocBytes == 0,
            $"buttons={buttons.Count} arrowFrameBytes={arrowFrame.HotPhaseAllocBytes}");
    }

    // ══ E10 — ShelfLift ══════════════════════════════════════════════════════════════════════════════════════════

    sealed class LiftProbe : Component
    {
        public readonly IReadOnlyList<CtlItem> Items = CtlItems(6);
        readonly ShelfLift _lift;
        public LiftProbe(ShelfLift lift) => _lift = lift;

        // Headerless (no title, no header, pager: None) — the E4/E10 external-controller shape this exists for.
        public override Element Render() => new BoxEl
        {
            Direction = 1, Width = 500f,
            Children =
            [
                PagedShelf.Create(Items,
                    (item, _, width) => new BoxEl { Width = width, Height = 44f, Children = [Text(item.Title)] },
                    cardHeight: static _ => 44f,
                    pager: ShelfPager.None,
                    minCardW: CardW, maxCardW: CardW, fixedCardW: CardW, gap: Gap,
                    headerGap: 0f, edgeFade: 0f,
                    keyOf: static (item, _) => item.Id.ToString(),
                    lift: _lift) with { Key = "shelf" },
            ],
        };
    }

    static (AppHost host, LiftProbe probe) NewLiftHarness(StringTable strings, string name, ShelfLift lift)
    {
        var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc(name, new Size2(500, 200), 1f));
        window.Show();
        var probe = new LiftProbe(lift);
        var host = new AppHost(app, window, new HeadlessGpuDevice(), new HeadlessFontSystem(strings), strings, probe);
        return (host, probe);
    }

    // The CARD's visual top, not the slot root's. Since E9 the Role=Button node is the slot ROOT (BindCard — the ItemsView
    // roving-focus target), and ShelfLift's clearance is that root's own top PADDING: the root's box always starts at
    // the strip's top (0), and the card content is laid out LiftClearance (Elevate) or 0 (None) below it. So the
    // measured edge is the slot root's FIRST child — the ShelfCardSlot content box that holds the card — which sits
    // exactly at the root's padded content top.
    static (bool HasButton, float SlotTopY, float CardTopY) LiftCardTop(SceneStore scene, List<NodeHandle> buttons)
    {
        if (buttons.Count == 0) return (false, float.NaN, float.NaN);
        var slot = buttons[0];
        var content = Child(scene, slot, 0);
        float slotTop = scene.AbsoluteRect(slot).Y;
        return (!content.IsNull, slotTop, content.IsNull ? float.NaN : scene.AbsoluteRect(content).Y);
    }

    static void ShelfLiftNoneFlushChecks(StringTable strings)
    {
        var (host, probe) = NewLiftHarness(strings, "shelf-lift-none-flush", ShelfLift.None);
        using var _ = host;
        Settle(host);
        var scene = host.Scene;
        var buttons = Roles(scene, AutomationRole.Button);
        var (hasButton, slotTopY, cardTopY) = LiftCardTop(scene, buttons);

        Check("gate.shelf.lift.none-flush ShelfLift.None reserves no LiftClearance/ShadowClearance padding on the slot root — a headerless shelf's PartRoot top edge sits flush with the first card's visual top (0 DIP gap, no -12 DIP margin needed)",
            hasButton && Near(cardTopY, 0f),
            $"buttons={buttons.Count} slotTopY={slotTopY:0.##} cardTopY={cardTopY:0.##}(exp 0)");
    }

    static void ShelfLiftElevateUnchangedChecks(StringTable strings)
    {
        var (host, probe) = NewLiftHarness(strings, "shelf-lift-elevate-unchanged", ShelfLift.Elevate);
        using var _ = host;
        Settle(host);
        var scene = host.Scene;
        var buttons = Roles(scene, AutomationRole.Button);
        var (hasButton, slotTopY, cardTopY) = LiftCardTop(scene, buttons);

        // The default (Elevate, or `lift` simply omitted) is byte-identical to a shelf built before ShelfLift
        // existed: the pre-E10 12 DIP LiftClearance gap between a headerless PartRoot's top and the first card's
        // visual top is unchanged.
        Check("gate.shelf.lift.elevate-unchanged ShelfLift.Elevate (the default) reproduces the pre-E10 shelf exactly — the headerless shelf's 12 DIP LiftClearance gap above the first card is unchanged",
            hasButton && Near(cardTopY, 12f),
            $"buttons={buttons.Count} slotTopY={slotTopY:0.##} cardTopY={cardTopY:0.##}(exp 12)");
    }
}
