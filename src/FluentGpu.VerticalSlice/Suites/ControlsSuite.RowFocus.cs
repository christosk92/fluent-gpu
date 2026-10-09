using System;
using System.Collections.Generic;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Hosting;
using FluentGpu.Input;
using FluentGpu.Pal;
using FluentGpu.Pal.Headless;
using FluentGpu.Rhi.Headless;
using FluentGpu.Scene;
using FluentGpu.Signals;
using FluentGpu.Text;
using FluentGpu.Text.Headless;
using static FluentGpu.VerticalSlice.Harness.Gate;
using static FluentGpu.VerticalSlice.Harness.Asserts;

// ── E1: RowScope.IsFocused on a BOUND ItemsView (Controls/SelectorVisualsBound.cs RowScope + SlotFocus, the rowBind
// focus edge in Controls/ItemsView.cs — shared-media-surface-implementation.md §6, C:\wavee\waveemusic) ───────────────
//
// The slot root owns focus (the roving tab stop); the passive content inside the slot reads the slot's focus fact. Three
// pins: (1) ONE fact per persistent slot — every RowScope the template receives carries its own IsFocused, and the
// template (the only place a scope is minted) never re-runs for a rebind; (2) the fact FOLLOWS the slot root's focus
// edge — a click, the arrow keys moving the roving stop, and focus leaving the list; (3) a slot that RECYCLES while
// focused loses focus (the rebind's OnSlotRebound drops the dispatcher's handle, so keys never reach the item the slot
// shows now) and reads false, and focusing that same node for its new item reads true again. A reader component
// subscribed to IsFocused proves every flip actually re-renders the content, not just that a peek changed.
static partial class ControlsSuite
{
    sealed class RowFocusProbe : Component
    {
        public const int N = 200;
        public const float RowH = 40f, ViewH = 200f;
        public readonly ItemsViewController Controller = new();
        public readonly List<RowScope> Scopes = new();        // one entry per rowTemplate call == one per persistent slot
        public readonly List<NodeHandle> Nodes = new();       // the slot root each scope was built for (OnRealized)
        public readonly List<bool> Rendered = new();          // the focus each slot's reader last RENDERED

        public override Element Render() => new BoxEl
        {
            Width = 240f, Height = ViewH,
            Children =
            [
                ItemsView.CreateBound(N, scope =>
                {
                    int slot = Scopes.Count;
                    Scopes.Add(scope);
                    Nodes.Add(NodeHandle.Null);
                    Rendered.Add(false);
                    var focus = scope.IsFocused;
                    var content = new BoxEl
                    {
                        Height = RowH, Grow = 1f,
                        Children = [Embed.Comp(() => new RowFocusReader(this, slot, focus))],
                    };
                    return SelectorVisualsBound.None(in scope, content) with { OnRealized = h => Nodes[slot] = h };
                }, RepeatLayout.Stack(RowH), new ListOptions { SelectionMode = ItemsSelectionMode.None, Controller = Controller }),
            ],
        };
    }

    sealed class RowFocusReader(RowFocusProbe probe, int slot, IReadSignal<bool>? focus) : Component
    {
        public override Element Render()
        {
            probe.Rendered[slot] = focus?.Value == true;   // subscribes the focus edge (and, while focused, the recycle)
            return new BoxEl();
        }
    }

    static void BoundRowFocusChecks(StringTable strings)
    {
        using var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("virt-row-focus", new Size2(320, 240), 1f));
        window.Show();
        var probe = new RowFocusProbe();
        using var host = new AppHost(app, window, new HeadlessGpuDevice(), new HeadlessFontSystem(strings), strings, probe);
        Settle(host);
        var scene = host.Scene;
        var ctl = probe.Controller;

        void Frames(int n) { for (int i = 0; i < n; i++) host.RunFrame(); }
        void Key(int key) { window.QueueInput(new InputEvent(InputKind.Key, default, 0, key)); Frames(3); }
        // The ATTACHED slot showing an item (a parked spare keeps its index but has no parent).
        int SlotOf(int index)
        {
            for (int k = 0; k < probe.Scopes.Count; k++)
            {
                var n = probe.Nodes[k];
                if (probe.Scopes[k].Index.Peek() == index && !n.IsNull && scene.IsLive(n) && !scene.Parent(n).IsNull) return k;
            }
            return -1;
        }
        bool Live(int k) => k >= 0 && probe.Scopes[k].IsFocused is { } f && f.Peek();
        int LiveCount() { int c = 0; for (int k = 0; k < probe.Scopes.Count; k++) if (Live(k)) c++; return c; }
        int RenderedCount() { int c = 0; foreach (bool r in probe.Rendered) if (r) c++; return c; }
        // Exactly this slot reads focused — by peek AND by what its subscribed reader rendered.
        bool Only(int k) => k >= 0 && Live(k) && probe.Rendered[k] && LiveCount() == 1 && RenderedCount() == 1;

        // ── (1) one focus fact per slot, none focused at rest ─────────────────────────────────────────────────────────
        int slots0 = probe.Scopes.Count;
        var distinct = new HashSet<object>(ReferenceEqualityComparer.Instance);
        bool allPresent = slots0 >= 5;
        foreach (var s in probe.Scopes)
        {
            if (s.IsFocused is { } f) distinct.Add(f);
            else allPresent = false;
        }
        bool restClear = LiveCount() == 0 && RenderedCount() == 0;

        // ── (2) follows the slot root's focus edge ─────────────────────────────────────────────────────────────────
        int k0 = SlotOf(0);
        if (k0 >= 0) ClickNode(host, window, probe.Nodes[k0]);
        Frames(3);
        bool click0 = Only(k0) && host.Input.Focused == probe.Nodes[k0];
        Key(Keys.Down);                                       // the roving stop moves onto row 1's slot, off row 0's
        int k1 = SlotOf(1);
        bool down1 = Only(k1) && host.Input.Focused == probe.Nodes[k1];
        Key(Keys.Up);
        bool up0 = Only(k0);
        host.Input.SetFocus(NodeHandle.Null);                 // focus leaves the list entirely
        Frames(3);
        bool blurred = LiveCount() == 0 && RenderedCount() == 0;

        Check("gate.virt.rowFocus.follows RowScope.IsFocused follows the slot root's focus edge on a bound ItemsView: a click focuses exactly row 0's slot, Down/Up move it with the roving tab stop, and focus leaving the list clears it — every flip re-renders the subscribed reader",
            k0 >= 0 && k1 >= 0 && click0 && down1 && up0 && blurred,
            $"k0={k0} k1={k1} click0={click0} down1={down1} up0={up0} blurred={blurred} live={LiveCount()} rendered={RenderedCount()}");

        // ── (3) recycle while focused, then re-focus the SAME node for its new item ────────────────────────────────
        ctl.StartBringItemIntoView(100, 0f);
        Frames(3);
        int kA = SlotOf(102);                                 // a row in the middle of the viewport
        var nodeA = kA >= 0 ? probe.Nodes[kA] : NodeHandle.Null;
        if (kA >= 0) ClickNode(host, window, nodeA);
        Frames(3);
        bool focusedA = Only(kA) && host.Input.Focused == nodeA;

        ctl.StartBringItemIntoView(150, 0f);                  // no overlap: every slot rebinds in place
        Frames(3);
        int shownNow = kA >= 0 ? probe.Scopes[kA].Index.Peek() : -1;
        bool recycled = shownNow >= 0 && shownNow != 102;
        bool focusDropped = host.Input.Focused.IsNull;        // keys must not reach the item the slot shows now
        bool readsFalse = kA >= 0 && !Live(kA) && !probe.Rendered[kA] && LiveCount() == 0 && RenderedCount() == 0;

        var rect = nodeA.IsNull ? default : scene.AbsoluteRect(nodeA);
        bool visible = !nodeA.IsNull && rect.Y >= -0.5f && rect.Y + rect.H <= RowFocusProbe.ViewH + 0.5f;
        if (visible) ClickNode(host, window, nodeA);          // pointer focus lands on the same node for its new item
        Frames(3);
        bool refocused = Only(kA) && host.Input.Focused == nodeA && ctl.CurrentItemIndex == shownNow
                         && probe.Scopes[kA].Index.Peek() == shownNow;

        Check("gate.virt.rowFocus.recycle a bound slot that recycles while focused loses focus (the engine drops its focus handle on the rebind, so keys never act on the item the slot shows now) and reads IsFocused=false, and landing focus on that same node for its new item reads true again",
            kA >= 0 && focusedA && recycled && focusDropped && readsFalse && visible && refocused,
            $"kA={kA} focusedA={focusedA} shownNow={shownNow} recycled={recycled} focusDropped={focusDropped} readsFalse={readsFalse} visible={visible}(y={rect.Y:0.#}) refocused={refocused} current={ctl.CurrentItemIndex} live={LiveCount()} rendered={RenderedCount()}");

        // ── (1) checked last: two no-overlap jumps rebound every slot, yet the template (the only place a RowScope — and its
        // focus fact — is minted) ran exactly once per slot the pool holds: attached + parked == template calls. ──────────
        var vp = FindScrollNode(scene, scene.Root);
        int attached = vp.IsNull || !scene.TryGetScroll(vp, out var sc) ? -1 : scene.ChildCount(sc.ContentNode);
        int spare = vp.IsNull ? 0 : host.Reconciler.SpareSlotCount(vp);
        bool onePerSlot = allPresent && distinct.Count == slots0 && attached >= 0 && probe.Scopes.Count == attached + spare;
        Check("gate.virt.rowFocus.perSlot every RowScope a bound ItemsView hands its template carries its OWN IsFocused (non-null, pairwise distinct — one per persistent slot), none reads focused at rest, and rebinding every slot (two no-overlap jumps) mints none: template calls == attached + parked slots",
            onePerSlot && restClear,
            $"slots0={slots0} distinct={distinct.Count} allPresent={allPresent} restClear={restClear} templates={probe.Scopes.Count} attached={attached} spare={spare}");

        BoundInvokePolicyChecks(strings);
        BoundSelectAllSelectableChecks(strings);
    }

    // ── WP1: a Ctrl/Shift double-click is a selection gesture, never an invoke ──────────────────────────────────────
    sealed class InvokePolicyProbe(ItemsSelectionMode mode, Func<int, bool>? selectable, List<int> invoked) : Component
    {
        public const float RowH = 40f;
        public readonly ItemsViewController Controller = new();

        public override Element Render() => new BoxEl
        {
            Width = 240f, Height = 400f,
            Children =
            [
                ItemsView.CreateBound(10, scope =>
                    SelectorVisualsBound.None(in scope, new BoxEl { Height = RowH, Grow = 1f }),
                    RepeatLayout.Stack(RowH),
                    new ListOptions
                    {
                        SelectionMode = mode, Controller = Controller, IsItemInvokedEnabled = true,
                        OnInvoked = invoked.Add, IsItemSelectable = selectable,
                    }),
            ],
        };
    }

    static void BoundInvokePolicyChecks(StringTable strings)
    {
        using var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("virt-invoke-policy", new Size2(320, 420), 1f));
        window.Show();
        var invoked = new List<int>();
        var probe = new InvokePolicyProbe(ItemsSelectionMode.Extended, null, invoked);
        using var host = new AppHost(app, window, new HeadlessGpuDevice(), new HeadlessFontSystem(strings), strings, probe);
        Settle(host);
        var sel = probe.Controller.Selection!;

        uint t = 10_000;
        void Click(int row, KeyModifiers mods)
        {
            var p = new Point2(100f, InvokePolicyProbe.RowH * row + InvokePolicyProbe.RowH * 0.5f);
            window.QueueInput(new InputEvent(InputKind.PointerDown, p, 0, 0, mods, TimestampMs: t));
            window.QueueInput(new InputEvent(InputKind.PointerUp, p, 0, 0, mods, TimestampMs: t + 40));
            host.RunFrame();
            t += 100;   // inside the double-click window and slop, so the second click promotes to ClickCount 2
        }

        // Control: an UNMODIFIED double-click invokes exactly once (harness + invoke matrix sanity).
        Click(3, KeyModifiers.None); Click(3, KeyModifiers.None);
        bool plainInvokes = invoked.Count == 1 && invoked[0] == 3;
        t += 5_000; invoked.Clear();
        sel.DeselectAll();
        Click(5, KeyModifiers.None);                       // selects 5; this single click never invokes (Tap)
        int selBefore = sel.SelectedCount;
        t += 5_000;
        Click(3, KeyModifiers.Ctrl); Click(3, KeyModifiers.Ctrl);   // toggle on, toggle off - a DoubleTap-shaped pair
        bool ctrlNoInvoke = invoked.Count == 0;
        bool ctrlNetUnchanged = sel.SelectedCount == selBefore && sel.IsSelected(5) && !sel.IsSelected(3);
        t += 5_000;
        Click(7, KeyModifiers.Shift); Click(7, KeyModifiers.Shift);
        bool shiftNoInvoke = invoked.Count == 0;

        Check("gate.virt.invoke.modifiedDoubleTap on an Extended ItemsView a Ctrl- or Shift-modified double-click is a SELECTION gesture, never an invoke (deliberate deviation from WinUI): Ctrl+click twice inside the double-click window never raises ItemInvoked and leaves the selection net-unchanged, while an unmodified double-click still invokes exactly once",
            plainInvokes && ctrlNoInvoke && ctrlNetUnchanged && shiftNoInvoke,
            $"plainInvokes={plainInvokes} ctrlNoInvoke={ctrlNoInvoke} ctrlNetUnchanged={ctrlNetUnchanged} shiftNoInvoke={shiftNoInvoke} invoked={invoked.Count} selected={sel.SelectedCount}");
    }

    // ── WP1: Ctrl+A and interaction honour ListOptions.IsItemSelectable ─────────────────────────────────────────────
    static void BoundSelectAllSelectableChecks(StringTable strings)
    {
        using var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("virt-selectall-selectable", new Size2(320, 420), 1f));
        window.Show();
        var invoked = new List<int>();
        var probe = new InvokePolicyProbe(ItemsSelectionMode.Extended, static i => i >= 2, invoked);   // rows 0-1 = a hero prefix
        using var host = new AppHost(app, window, new HeadlessGpuDevice(), new HeadlessFontSystem(strings), strings, probe);
        Settle(host);
        var sel = probe.Controller.Selection!;

        void Click(int row)
        {
            var p = new Point2(100f, InvokePolicyProbe.RowH * row + InvokePolicyProbe.RowH * 0.5f);
            window.QueueInput(new InputEvent(InputKind.PointerDown, p, 0, 0));
            window.QueueInput(new InputEvent(InputKind.PointerUp, p, 0, 0));
            host.RunFrame();
        }
        void CtrlA() { window.QueueInput(new InputEvent(InputKind.Key, default, 0, Keys.A, KeyModifiers.Ctrl)); host.RunFrame(); host.RunFrame(); }

        Click(0);                                           // a non-selectable row: focus/current only, no selector call
        bool prefixClickInert = sel.SelectedCount == 0;
        Click(4);
        bool selectableClick = sel.SelectedCount == 1 && sel.IsSelected(4);
        CtrlA();
        int afterFirst = sel.SelectedCount;
        bool allSelectable = afterFirst == 8 && !sel.IsSelected(0) && !sel.IsSelected(1) && sel.IsSelected(2) && sel.IsSelected(9);
        CtrlA();
        bool secondClears = sel.SelectedCount == 0;

        Check("gate.virt.selectAll.selectableOnly with IsItemSelectable = i >= 2 on an Extended list, Ctrl+A selects only the selectable rows 2..9 (the hero/header prefix is never swept in), a second Ctrl+A clears, and a click on a non-selectable row runs no selector",
            prefixClickInert && selectableClick && allSelectable && secondClears,
            $"prefixClickInert={prefixClickInert} selectableClick={selectableClick} allSelectable={allSelectable} afterFirst={afterFirst} secondClears={secondClears} count={sel.SelectedCount}");
    }
}
