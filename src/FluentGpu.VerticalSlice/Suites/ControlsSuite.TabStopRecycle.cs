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
using FluentGpu.Text;
using FluentGpu.Text.Headless;
using static FluentGpu.VerticalSlice.Harness.Gate;
using static FluentGpu.VerticalSlice.Harness.Asserts;

// ── Bound ItemsView roving tab stop across a recycle (Controls/ItemsView.cs SetSlotTabStop + the window re-sync) ──────
// A bound slot keeps its scene flags when it recycles or parks, so a stop cleared BY INDEX stayed on the slot that
// scrolled away with it: clicking a row after a jump left two Focusable slot roots, a slot parked while holding the stop
// came back as another row still Focusable, and the current row re-realized in another slot with none. Pins: exactly one
// attached slot root is Focusable and it shows the current item — after a click past a no-overlap jump, while the
// current item is scrolled away (none, also when its parked slot is re-taken for another row), and once it re-realizes
// in another slot — and Tab from outside the list enters that row.
static partial class ControlsSuite
{
    sealed class TabStopRecycleProbe : Component
    {
        public const int N = 200;
        public const float RowH = 40f, ViewH = 200f;
        public readonly ItemsViewController Controller = new();
        public readonly List<RowScope> Scopes = new();        // one per rowTemplate call == one per persistent slot
        public readonly List<NodeHandle> Nodes = new();       // the slot root each scope was built for (OnRealized)

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
                    return SelectorVisualsBound.None(in scope, new BoxEl { Height = RowH, Grow = 1f })
                        with { OnRealized = h => Nodes[slot] = h };
                }, RepeatLayout.Stack(RowH), new ListOptions { SelectionMode = ItemsSelectionMode.None, Controller = Controller }),
            ],
        };
    }

    static void BoundTabStopRecycleChecks(StringTable strings)
    {
        using var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("virt-tabstop-recycle", new Size2(320, 240), 1f));
        window.Show();
        var probe = new TabStopRecycleProbe();
        using var host = new AppHost(app, window, new HeadlessGpuDevice(), new HeadlessFontSystem(strings), strings, probe);
        Settle(host);
        var scene = host.Scene;
        var ctl = probe.Controller;

        void Frames(int n) { for (int i = 0; i < n; i++) host.RunFrame(); }
        void Jump(int index, float ratio) { ctl.StartBringItemIntoView(index, ratio); Frames(3); }
        bool Attached(int k) { var n = probe.Nodes[k]; return !n.IsNull && scene.IsLive(n) && !scene.Parent(n).IsNull; }
        int SlotOf(int index)
        {
            for (int k = 0; k < probe.Scopes.Count; k++)
                if (probe.Scopes[k].Index.Peek() == index && Attached(k)) return k;
            return -1;
        }
        bool OnScreen(int k)
        {
            if (k < 0) return false;
            var r = scene.AbsoluteRect(probe.Nodes[k]);
            return r.Y >= -0.5f && r.Y + r.H <= TabStopRecycleProbe.ViewH + 0.5f;
        }
        // Every ATTACHED slot root carrying the tab-stop flag (a parked spare is outside the tab walk).
        int StopCount(out int stopSlot)
        {
            int c = 0; stopSlot = -1;
            for (int k = 0; k < probe.Scopes.Count; k++)
                if (Attached(k) && (scene.Flags(probe.Nodes[k]) & NodeFlags.Focusable) != 0) { c++; stopSlot = k; }
            return c;
        }

        // (1) click row 3: the stop moves onto its slot (A).
        int kA = SlotOf(3);
        if (kA >= 0) ClickNode(host, window, probe.Nodes[kA]);
        Frames(3);
        bool clicked3 = ctl.CurrentItemIndex == 3 && StopCount(out int s1) == 1 && s1 == kA;

        // (2) no-overlap jump: slot A rebinds to another row. Click a visible row held by a DIFFERENT slot.
        Jump(100, 0f);
        int x = -1, kX = -1;
        for (int i = 100; i < 105 && x < 0; i++)
        {
            int k = SlotOf(i);
            if (k >= 0 && k != kA && OnScreen(k)) { x = i; kX = k; }
        }
        if (kX >= 0) ClickNode(host, window, probe.Nodes[kX]);
        Frames(3);
        int afterClick = StopCount(out int s2);
        bool oneStop = x >= 0 && ctl.CurrentItemIndex == x && afterClick == 1 && s2 == kX;

        // Tab from outside the list enters the CURRENT row, the only stop.
        host.Input.SetFocus(NodeHandle.Null);
        Frames(2);
        window.QueueInput(new InputEvent(InputKind.Key, default, 0, Keys.Tab));
        Frames(3);
        bool tabEntersCurrent = kX >= 0 && host.Input.Focused == probe.Nodes[kX] && ctl.CurrentItemIndex == x;
        host.Input.SetFocus(NodeHandle.Null);
        Frames(2);

        // (3) the current row scrolls away: no attached slot shows it, so none carries the stop. It comes back
        // bottom-aligned (another window ordinal, so another slot), and that slot carries the one stop.
        Jump(0, 0f);
        int away = StopCount(out _);
        bool noneAway = x >= 0 && SlotOf(x) < 0 && away == 0;
        Jump(x, 1f);
        int kBack = SlotOf(x);
        bool returned = kBack >= 0 && StopCount(out int s3) == 1 && s3 == kBack;

        // (4) PARKED, not rebound: click a row in the window's upper band, then jump to the list end, whose shorter window
        // parks that band's slots as spares (no rebind). The next jump re-takes those spares for other rows.
        Jump(184, 0f);
        int y = 186, kY = SlotOf(y);
        if (OnScreen(kY)) ClickNode(host, window, probe.Nodes[kY]);
        Frames(3);
        bool clickedY = ctl.CurrentItemIndex == y && StopCount(out int s4) == 1 && s4 == kY;
        Jump(TabStopRecycleProbe.N - 1, 1f);
        bool parked = kY >= 0 && !Attached(kY);
        int atEnd = StopCount(out _);
        Jump(100, 0f);
        int retaken = StopCount(out int s5);
        Jump(y, 0f);
        int kYBack = SlotOf(y);
        bool yReturned = kYBack >= 0 && StopCount(out int s6) == 1 && s6 == kYBack;

        Check("gate.virt.tabStop.recycle a bound ItemsView keeps exactly ONE roving tab stop across slot recycles: a click after a no-overlap jump leaves only the clicked row's slot Focusable, Tab from outside enters that row, a scrolled-away current item leaves no stale stop (also when its slot was PARKED and re-taken for another row), and on its return its NEW slot carries the stop",
            kA >= 0 && clicked3 && kX >= 0 && oneStop && tabEntersCurrent && noneAway && returned
                && clickedY && parked && atEnd == 0 && retaken == 0 && yReturned,
            $"kA={kA} clicked3={clicked3} x={x} kX={kX} afterClick={afterClick}(slot={s2}) tab={tabEntersCurrent} away={away} kBack={kBack} returned={returned} kY={kY} clickedY={clickedY} parked={parked} atEnd={atEnd} retaken={retaken}(slot={s5}) kYBack={kYBack} yReturned={yReturned} current={ctl.CurrentItemIndex}");
    }
}
