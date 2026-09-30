using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using FluentGpu.Animation;
using FluentGpu.Forms;
using FluentGpu.Hooks;
using FluentGpu.Media;
using FluentGpu.Pal;
using FluentGpu.Input;
using FluentGpu.Layout;
using FluentGpu.Reconciler;
using FluentGpu.Rhi;
using FluentGpu.Text;
using static FluentGpu.Dsl.Ui;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hosting;
using FluentGpu.Pal.Headless;
using FluentGpu.Render;
using FluentGpu.Rhi.Headless;
using FluentGpu.Scene;
using FluentGpu.Signals;
using FluentGpu.Text.Headless;
using static FluentGpu.VerticalSlice.Harness.Gate;
using static FluentGpu.VerticalSlice.Harness.Asserts;

// ── Segmented presets (E3, Wavee Home redesign — facet pivot + compact band): FluentGpu.Controls/Segmented.cs ──────
//
// Two new consumable parts (PartLabel on the item's text, PartSelectionPillSlot on the whole 3px indicator row — not
// just the pill itself, which was already PartSelectionPill) plus two new options (WrapFocus, ItemRole) that a Wavee
// page-title pivot preset (bigger label type, no pill, Role=Tab) and a compact-band preset (smaller control, pill
// kept) both build on. Everything here is behaviour — no source-text gates.

static partial class ControlsSuite
{
    static void SegmentedPresetChecks(StringTable strings)
    {
        SegmentedWrapFocusChecks();
        SegmentedItemRoleTabChecks(strings);
        SegmentedPillSlotPartChecks(strings);
        SegmentedSelectNotFollowFocusChecks();
        SegmentedItemHoverEdgeChecks();
        SegmentedItemFocusEdgeChecks();
        SegmentedItemGapChecks(strings);
        SegmentedItemPaddingChecks(strings);
        SegmentedByteIdenticalChecks(strings);
    }

    // ── gate.segmented.wrap ───────────────────────────────────────────────────────────────────────────────────────

    static Element WrapFocusItems(bool wrap) => Segmented.Create(
        [new SegmentedItem("A"), new SegmentedItem("B"), new SegmentedItem("C")],
        options: new Segmented.SegmentedOptions { WrapFocus = wrap });

    static void SegmentedWrapFocusChecks()
    {
        // WrapFocus=false (default): Left at the first (tab-stop/selected) item does not move focus off it.
        bool clampedStays;
        {
            var strings = new StringTable();
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("seg-wrap-off", new Size2(320, 80), 1f));
            window.Show();
            var device = new HeadlessGpuDevice();
            using var host = new AppHost(app, window, device, new HeadlessFontSystem(strings), strings,
                new W0fStaticProbe { Build = () => new BoxEl { Padding = Edges4.All(8), Children = [WrapFocusItems(false)] } });
            host.RunFrame();
            var items = Roles(host.Scene, AutomationRole.RadioButton);
            window.QueueInput(new InputEvent(InputKind.Key, default, 0, Keys.Tab)); host.RunFrame();
            bool tabLanded = FocusedNode(host.Scene, host.Scene.Root) == items[0];
            window.QueueInput(new InputEvent(InputKind.Key, default, 0, Keys.Left)); host.RunFrame();
            clampedStays = tabLanded && FocusedNode(host.Scene, host.Scene.Root) == items[0];
        }

        // WrapFocus=true: Left at the first item wraps to the last enabled item; Right at the last wraps to the first.
        bool wrapsLeftToLast, wrapsRightToFirst;
        {
            var strings = new StringTable();
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("seg-wrap-on", new Size2(320, 80), 1f));
            window.Show();
            var device = new HeadlessGpuDevice();
            using var host = new AppHost(app, window, device, new HeadlessFontSystem(strings), strings,
                new W0fStaticProbe { Build = () => new BoxEl { Padding = Edges4.All(8), Children = [WrapFocusItems(true)] } });
            host.RunFrame();
            var items = Roles(host.Scene, AutomationRole.RadioButton);
            window.QueueInput(new InputEvent(InputKind.Key, default, 0, Keys.Tab)); host.RunFrame();
            window.QueueInput(new InputEvent(InputKind.Key, default, 0, Keys.Left)); host.RunFrame();
            wrapsLeftToLast = FocusedNode(host.Scene, host.Scene.Root) == items[2];
            window.QueueInput(new InputEvent(InputKind.Key, default, 0, Keys.Right)); host.RunFrame();
            wrapsRightToFirst = FocusedNode(host.Scene, host.Scene.Root) == items[0];
        }

        Check("gate.segmented.wrap SegmentedOptions.WrapFocus: default off clamps Left at the first item; on, Left from the first wraps to the last and Right from there wraps back to the first",
            clampedStays && wrapsLeftToLast && wrapsRightToFirst,
            $"clampedStays={clampedStays} wrapLeftToLast={wrapsLeftToLast} wrapRightToFirst={wrapsRightToFirst}");
    }

    // ── gate.segmented.role.tab ───────────────────────────────────────────────────────────────────────────────────

    static void SegmentedItemRoleTabChecks(StringTable strings)
    {
        var defaultEl = Segmented.Create([new SegmentedItem("All"), new SegmentedItem("Music")]);
        var tabEl = Segmented.Create(
            [new SegmentedItem("All"), new SegmentedItem("Music")],
            options: new Segmented.SegmentedOptions { ItemRole = AutomationRole.Tab });

        var scene = LayoutTree(strings, new BoxEl { Direction = 1, Children = [defaultEl, tabEl] });

        var radios = Roles(scene, AutomationRole.RadioButton);
        var tabs = Roles(scene, AutomationRole.Tab);
        Check("gate.segmented.role.tab SegmentedOptions.ItemRole: default RadioButton is unchanged (today's behaviour); AutomationRole.Tab re-roles every item for a facet/pivot tablist preset",
            radios.Count == 2 && tabs.Count == 2,
            $"radios={radios.Count} tabs={tabs.Count}");
    }

    // ── gate.segmented.pillslot.part ──────────────────────────────────────────────────────────────────────────────

    static void SegmentedPillSlotPartChecks(StringTable strings)
    {
        // Baseline: the pill-row part is untouched — captured via the part itself, it is visible and the selected
        // item's SelectionPill sub-part is present in the tree.
        NodeHandle baseSlot = default;
        var baseParts = new TemplateParts { [Segmented.PartSelectionPillSlot] = b => b with { OnRealized = h => baseSlot = h } };
        var baseScene = LayoutTree(strings, new BoxEl
        {
            Children = [Segmented.Create(
                [new SegmentedItem("All"), new SegmentedItem("Music")],
                options: new Segmented.SegmentedOptions { Parts = baseParts })],
        });
        bool baselineVisible = !baseSlot.IsNull && (baseScene.Flags(baseSlot) & NodeFlags.Visible) != 0;
        var baselinePill = FindFillNode(baseScene, baseScene.Root, Segmented.DefaultStyle.SelectionPill);
        bool baselineHasPill = !baselinePill.IsNull;

        // A caller collapses the whole pill-ROW via the part — e.g. the page-title pivot preset, which drops the
        // indicator in favour of bigger label type. PartSelectionPillSlot targets the row, not just the pill
        // (PartSelectionPill): collapsing it (Visible=false) clears the row's own Visible flag, so it and its pill
        // child are out of layout/paint/hit-test.
        NodeHandle hiddenSlot = default;
        var hiddenParts = new TemplateParts { [Segmented.PartSelectionPillSlot] = b => b with { Visible = false, OnRealized = h => hiddenSlot = h } };
        var hiddenScene = LayoutTree(strings, new BoxEl
        {
            Children = [Segmented.Create(
                [new SegmentedItem("All"), new SegmentedItem("Music")],
                options: new Segmented.SegmentedOptions { Parts = hiddenParts })],
        });
        bool hiddenCollapsed = !hiddenSlot.IsNull && (hiddenScene.Flags(hiddenSlot) & NodeFlags.Visible) == 0;

        Check("gate.segmented.pillslot.part Segmented.PartSelectionPillSlot targets the whole 3px indicator row (distinct from PartSelectionPill, the pill itself): unstyled it stays Visible and carries the selected pill; Visible=false collapses the row (out of layout/paint/hit-test)",
            baselineVisible && baselineHasPill && hiddenCollapsed,
            $"baselineVisible={baselineVisible} baselineHasPill={baselineHasPill} hiddenCollapsed={hiddenCollapsed}");

        SegmentedLabelPartChecks(strings);
    }

    // Companion check for Segmented.PartLabel (folded into the pillslot gate's suite call, still its own Check so a
    // failure names itself precisely) — a caller re-sizes just the label text, e.g. the 20/28 page-title pivot type.
    static void SegmentedLabelPartChecks(StringTable strings)
    {
        using var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("seg-label-part", new Size2(320, 100), 1f));
        window.Show();
        var device = new HeadlessGpuDevice();
        var parts = new TemplateParts();
        parts.Set<TextEl>(Segmented.PartLabel, t => t with { Size = 28f });
        using var host = new AppHost(app, window, device, new HeadlessFontSystem(strings), strings,
            new W0fStaticProbe { Build = () => new BoxEl { Padding = Edges4.All(8), Children = [Segmented.Create(
                [new SegmentedItem("All"), new SegmentedItem("Music")],
                options: new Segmented.SegmentedOptions { Parts = parts })] } });
        host.RunFrame();
        host.RunFrame();
        float sizeAll = -1f;
        foreach (var g in device.LastGlyphs)
            if (strings.Resolve(g.Text) == "All") sizeAll = g.FontSize;
        Check("gate.segmented.pillslot.part.label Segmented.PartLabel restyles the item's label TextEl (e.g. a page-title pivot's 28px type), independent of PartSelectionPillSlot",
            Near(sizeAll, 28f), $"labelSize={sizeAll}");
    }

    // ── gate.segmented.select-not-follow-focus ───────────────────────────────────────────────────────────────────

    static void SegmentedSelectNotFollowFocusChecks()
    {
        var strings = new StringTable();
        using var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("seg-focus-not-select", new Size2(320, 80), 1f));
        window.Show();
        var device = new HeadlessGpuDevice();
        var selected = new Signal<int>(-1);
        int changes = 0;
        using var host = new AppHost(app, window, device, new HeadlessFontSystem(strings), strings,
            new W0fStaticProbe { Build = () => new BoxEl { Padding = Edges4.All(8), Children = [Segmented.Create(
                [new SegmentedItem("All"), new SegmentedItem("Music"), new SegmentedItem("Podcasts")],
                selected,
                onChange: _ => changes++)] } });
        host.RunFrame();
        var items = Roles(host.Scene, AutomationRole.RadioButton);

        window.QueueInput(new InputEvent(InputKind.Key, default, 0, Keys.Tab)); host.RunFrame();
        bool tabLanded = FocusedNode(host.Scene, host.Scene.Root) == items[0];
        window.QueueInput(new InputEvent(InputKind.Key, default, 0, Keys.Right)); host.RunFrame();
        bool focusMovedNoSelect = FocusedNode(host.Scene, host.Scene.Root) == items[1] && selected.Value == 0 && changes == 0;
        window.QueueInput(new InputEvent(InputKind.Key, default, 0, Keys.Right)); host.RunFrame();
        bool focusMovedAgainNoSelect = FocusedNode(host.Scene, host.Scene.Root) == items[2] && selected.Value == 0 && changes == 0;
        // WinUI ButtonBase semantics: the down edge arms the press, the click fires on key-UP.
        window.QueueInput(new InputEvent(InputKind.Key, default, 0, Keys.Enter));
        window.QueueInput(new InputEvent(InputKind.KeyUp, default, 0, Keys.Enter)); host.RunFrame();
        bool enterSelectsFocused = selected.Value == 2 && changes == 1;

        Check("gate.segmented.select-not-follow-focus arrows move the roving focus stop without touching the selection signal; Enter commits the FOCUSED item — the focus-not-selection keyboard model is unchanged by E3",
            tabLanded && focusMovedNoSelect && focusMovedAgainNoSelect && enterSelectsFocused,
            $"tab={tabLanded} right1={focusMovedNoSelect} right2={focusMovedAgainNoSelect} enter={enterSelectsFocused} selected={selected.Value} changes={changes}");
    }

    // ── gate.segmented.item-hover-edge (E13, Wavee Home redesign — replaces Facet.UI.cs's per-item OnHoverMove/
    // OnPointerExit closure counter, F24) ────────────────────────────────────────────────────────────────────────

    static void SegmentedItemHoverEdgeChecks()
    {
        var strings = new StringTable();
        using var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("seg-hover-edge", new Size2(480, 80), 1f));
        window.Show();
        var device = new HeadlessGpuDevice();
        var log = new List<int>();
        using var host = new AppHost(app, window, device, new HeadlessFontSystem(strings), strings,
            new W0fStaticProbe { Build = () => new BoxEl { Padding = Edges4.All(8), Children = [Segmented.Create(
                [new SegmentedItem("All"), new SegmentedItem("Music"), new SegmentedItem("Podcasts")],
                options: new Segmented.SegmentedOptions { OnItemHoverChanged = i => log.Add(i) })] } });
        host.RunFrame();
        var items = Roles(host.Scene, AutomationRole.RadioButton);
        var c0 = CenterOf(host.Scene, items[0]);
        var c1 = CenterOf(host.Scene, items[1]);
        var r0 = host.Scene.AbsoluteRect(items[0]);
        var outside = new Point2(r0.X - 40f, r0.Y - 40f);

        // Enter item 0 → fires once with 0.
        window.QueueInput(new InputEvent(InputKind.PointerMove, c0, 0, 0)); host.RunFrame();
        bool enteredOnce = log.Count == 1 && log[0] == 0;

        // A second move that stays WITHIN item 0 is not a new edge — no duplicate fire.
        window.QueueInput(new InputEvent(InputKind.PointerMove, new Point2(c0.X + 1f, c0.Y), 0, 0)); host.RunFrame();
        bool noDuplicateWithin = log.Count == 1;

        // Move to item 1 → item 0's edge releases (-1) and item 1's edge fires (1); order isn't the contract, the
        // final settled index and no duplicate mid-item spam are.
        window.QueueInput(new InputEvent(InputKind.PointerMove, c1, 0, 0)); host.RunFrame();
        bool movedToItem1 = log.Count >= 2 && log[^1] == 1 && log.Contains(-1);

        // Leave the control entirely → releases to -1, exactly once.
        int countBeforeLeave = log.Count;
        window.QueueInput(new InputEvent(InputKind.PointerMove, outside, 0, 0)); host.RunFrame();
        window.QueueInput(new InputEvent(InputKind.PointerMove, new Point2(outside.X - 1f, outside.Y), 0, 0)); host.RunFrame();
        bool leftOnce = log.Count == countBeforeLeave + 1 && log[^1] == -1;

        Check("gate.segmented.item-hover-edge SegmentedOptions.OnItemHoverChanged fires index on enter / -1 on leave — EDGES only, never a continuous move stream and never a duplicate fire for the same edge",
            enteredOnce && noDuplicateWithin && movedToItem1 && leftOnce,
            $"log=[{string.Join(",", log)}] enteredOnce={enteredOnce} noDup={noDuplicateWithin} moved={movedToItem1} left={leftOnce}");
    }

    // ── gate.segmented.item-focus-edge ────────────────────────────────────────────────────────────────────────────

    static void SegmentedItemFocusEdgeChecks()
    {
        var strings = new StringTable();
        using var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("seg-focus-edge", new Size2(480, 80), 1f));
        window.Show();
        var device = new HeadlessGpuDevice();
        var log = new List<int>();
        using var host = new AppHost(app, window, device, new HeadlessFontSystem(strings), strings,
            new W0fStaticProbe { Build = () => new BoxEl { Padding = Edges4.All(8), Children = [Segmented.Create(
                [new SegmentedItem("All"), new SegmentedItem("Music"), new SegmentedItem("Podcasts")],
                options: new Segmented.SegmentedOptions { OnItemFocusChanged = i => log.Add(i) })] } });
        host.RunFrame();

        // Tab in → item 0 gains focus: fires once with 0.
        window.QueueInput(new InputEvent(InputKind.Key, default, 0, Keys.Tab)); host.RunFrame();
        bool gainedZero = log.Count == 1 && log[0] == 0;

        // Right moves the roving focus stop to item 1: item 0 loses (-1), item 1 gains (1) — no duplicate mid-way.
        window.QueueInput(new InputEvent(InputKind.Key, default, 0, Keys.Right)); host.RunFrame();
        bool movedToOne = log.Count >= 2 && log[^1] == 1 && log.Contains(-1);

        // Right again to item 2 — same edge contract, no repeat of an index already current.
        int countBeforeSecondMove = log.Count;
        window.QueueInput(new InputEvent(InputKind.Key, default, 0, Keys.Right)); host.RunFrame();
        bool movedToTwo = log.Count > countBeforeSecondMove && log[^1] == 2;

        Check("gate.segmented.item-focus-edge SegmentedOptions.OnItemFocusChanged fires index on focus-gain / -1 on focus-loss — EDGES only, tracking the roving FOCUS stop (not selection)",
            gainedZero && movedToOne && movedToTwo,
            $"log=[{string.Join(",", log)}] gainedZero={gainedZero} movedToOne={movedToOne} movedToTwo={movedToTwo}");
    }

    // ── gate.segmented.item-gap ───────────────────────────────────────────────────────────────────────────────────

    static void SegmentedItemGapChecks(StringTable strings)
    {
        var gapStyle = Segmented.DefaultStyle with { ItemGap = 24f };
        var scene = LayoutTree(strings, new BoxEl
        {
            Padding = Edges4.All(8),
            Children = [Segmented.Create(
                [new SegmentedItem("All"), new SegmentedItem("Music")],
                options: new Segmented.SegmentedOptions { Style = gapStyle })],
        });
        var items = Roles(scene, AutomationRole.RadioButton);
        float measuredGap = items.Count == 2
            ? scene.AbsoluteRect(items[1]).X - scene.AbsoluteRect(items[0]).Right
            : -1f;

        Check("gate.segmented.item-gap Style.ItemGap is the root's item-row Gap — 24 DIP between the word boxes when set",
            items.Count == 2 && Near(measuredGap, 24f, 0.5f),
            $"items={items.Count} measuredGap={measuredGap:0.0}");
    }

    // ── gate.segmented.item-padding (E19, Wavee Home redesign — compact facet band item breathing room) ────────────

    static void SegmentedItemPaddingChecks(StringTable strings)
    {
        // A single item with no ItemMinWidth constraint so its measured width is driven purely by its content + the
        // item root's own Padding — ItemPadding (12,0,12,0) must make it exactly 24 DIP (12 left + 12 right) wider
        // than the same item with the default (zero) ItemPadding, everything else equal.
        var plainStyle = Segmented.DefaultStyle with { ItemMinWidth = 0f };
        var paddedStyle = plainStyle with { ItemPadding = new Edges4(12f, 0f, 12f, 0f) };

        Element Build(Segmented.Style style) => new BoxEl
        {
            Padding = Edges4.All(8),
            Children = [Segmented.Create(
                [new SegmentedItem("All")],
                options: new Segmented.SegmentedOptions { Style = style })],
        };

        var plainScene = LayoutTree(strings, Build(plainStyle));
        var paddedScene = LayoutTree(new StringTable(), Build(paddedStyle));
        var plainItems = Roles(plainScene, AutomationRole.RadioButton);
        var paddedItems = Roles(paddedScene, AutomationRole.RadioButton);

        float plainW = plainItems.Count == 1 ? plainScene.AbsoluteRect(plainItems[0]).W : -1f;
        float paddedW = paddedItems.Count == 1 ? paddedScene.AbsoluteRect(paddedItems[0]).W : -1f;
        float widened = paddedW - plainW;

        Check("gate.segmented.item-padding Style.ItemPadding is applied to each item root's Padding — (12,0,12,0) lays out 24 DIP wider than no padding",
            plainItems.Count == 1 && paddedItems.Count == 1 && Near(widened, 24f, 0.5f),
            $"plainItems={plainItems.Count} paddedItems={paddedItems.Count} plainW={plainW:0.0} paddedW={paddedW:0.0} widened={widened:0.0}");
    }

    // ── gate.segmented.byte-identical ─────────────────────────────────────────────────────────────────────────────

    static void SegmentedByteIdenticalChecks(StringTable strings)
    {
        // No options ⇒ E13 wires nothing extra: geometry (item positions/widths, zero gap) and the hover/focus-less
        // interaction surface match the pre-E13 control exactly — SegmentedOptions() (every field at its default,
        // including the new OnItemHoverChanged/OnItemFocusChanged/ItemGap) resolves identically to Segmented.Create's
        // own null-options default.
        Element BuildDefault() => new BoxEl { Padding = Edges4.All(8), Children = [Segmented.Create(
            [new SegmentedItem("All"), new SegmentedItem("Music"), new SegmentedItem("Podcasts")])] };
        Element BuildExplicitDefaults() => new BoxEl { Padding = Edges4.All(8), Children = [Segmented.Create(
            [new SegmentedItem("All"), new SegmentedItem("Music"), new SegmentedItem("Podcasts")],
            options: new Segmented.SegmentedOptions())] };

        var sceneA = LayoutTree(strings, BuildDefault());
        var sceneB = LayoutTree(new StringTable(), BuildExplicitDefaults());
        var itemsA = Roles(sceneA, AutomationRole.RadioButton);
        var itemsB = Roles(sceneB, AutomationRole.RadioButton);

        bool sameCount = itemsA.Count == 3 && itemsB.Count == 3;
        bool sameGeometry = sameCount;
        float zeroGap = -1f;
        if (sameCount)
        {
            for (int i = 0; i < 3; i++)
            {
                var ra = sceneA.AbsoluteRect(itemsA[i]);
                var rb = sceneB.AbsoluteRect(itemsB[i]);
                if (!Near(ra.X, rb.X, 0.01f) || !Near(ra.W, rb.W, 0.01f) || !Near(ra.Y, rb.Y, 0.01f) || !Near(ra.H, rb.H, 0.01f))
                    sameGeometry = false;
            }
            zeroGap = sceneA.AbsoluteRect(itemsA[1]).X - sceneA.AbsoluteRect(itemsA[0]).Right;
        }

        // A bare hover/key probe on the no-options control must not throw or misbehave now that OnHoverMove/
        // OnFocusChanged are conditionally wired — with no callback supplied they stay null (no WantsPointer churn
        // beyond what OnClick already needs).
        using var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("seg-byteident", new Size2(480, 80), 1f));
        window.Show();
        var device = new HeadlessGpuDevice();
        var liveStrings = new StringTable();
        using var host = new AppHost(app, window, device, new HeadlessFontSystem(liveStrings), liveStrings,
            new W0fStaticProbe { Build = BuildDefault });
        host.RunFrame();
        var liveItems = Roles(host.Scene, AutomationRole.RadioButton);
        var c0 = CenterOf(host.Scene, liveItems[0]);
        window.QueueInput(new InputEvent(InputKind.PointerMove, c0, 0, 0));
        bool noCrash = true;
        try { host.RunFrame(); } catch { noCrash = false; }

        Check("gate.segmented.byte-identical no options (or an all-default SegmentedOptions) ⇒ E13's item-hover/item-focus/item-gap wiring is inert: geometry matches the pre-E13 control 1:1, the item gap is still 0, and hovering doesn't throw with no callback wired",
            sameCount && sameGeometry && Near(zeroGap, 0f, 0.01f) && noCrash,
            $"sameCount={sameCount} sameGeometry={sameGeometry} zeroGap={zeroGap:0.00} noCrash={noCrash}");
    }
}
