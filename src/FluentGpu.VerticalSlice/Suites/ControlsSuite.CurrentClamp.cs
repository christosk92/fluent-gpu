using System;
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

// ── A count shrink must not strand ItemsView's keyboard-current index past the end (Controls/ItemsView.cs) ───────────
//
// `current` is written only by the key, pointer and focus paths, so a reactive count (ListOptions.CountSignal) that drops
// below it left a phantom index behind: CurrentItemIndex handed the app an out-of-range index, Up/Down stepped from it
// (StepEnabled stays put past the end, yet the key is still Handled), and the RenderItem roving tab stop named no realized
// container, so Tab skipped the list. The pins: End onto the last of 41 rows, then shrink the count to 38 (focus drops with
// the removed row) — the current index is the new last row in both modes, and on a RenderItem list exactly one realized
// container is the tab stop, Tab lands on it and Up moves from it.
static partial class ControlsSuite
{
    sealed class CurrentClampProbe(bool bound) : Component
    {
        public const int N = 41, Shrunk = 38;
        public const float RowH = 40f, ViewH = 200f;
        public readonly ItemsViewController Controller = new();
        public readonly Signal<int> Count = new(N);

        public override Element Render()
        {
            var options = new ListOptions { SelectionMode = ItemsSelectionMode.None, Controller = Controller, CountSignal = Count };
            return new BoxEl
            {
                Width = 240f, Height = ViewH,
                Children =
                [
                    bound
                        ? ItemsView.CreateBound(N, scope => SelectorVisualsBound.None(in scope, new BoxEl { Height = RowH, Grow = 1f }),
                            RepeatLayout.Stack(RowH), options)
                        : ItemsView.Create(N, _ => new BoxEl { Height = RowH, Grow = 1f }, RepeatLayout.Stack(RowH), options),
                ],
            };
        }
    }

    static void CurrentClampChecks(StringTable strings)
    {
        foreach (bool bound in new[] { true, false })
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("virt-current-clamp", new Size2(320, 240), 1f));
            window.Show();
            var probe = new CurrentClampProbe(bound);
            using var host = new AppHost(app, window, new HeadlessGpuDevice(), new HeadlessFontSystem(strings), strings, probe);
            Settle(host);
            var scene = host.Scene;
            var ctl = probe.Controller;
            var vp = FindScrollNode(scene, scene.Root);

            void Frames(int n) { for (int i = 0; i < n; i++) host.RunFrame(); }
            void Key(int key) { window.QueueInput(new InputEvent(InputKind.Key, default, 0, key)); Frames(3); }
            bool FocusInList()
            {
                for (var p = host.Input.Focused; !p.IsNull; p = scene.Parent(p)) if (p == vp) return true;
                return false;
            }
            int Focusables(NodeHandle n)
            {
                int c = 0;
                for (var k = scene.FirstChild(n); !k.IsNull; k = scene.NextSibling(k))
                    c += ((scene.Flags(k) & NodeFlags.Focusable) != 0 ? 1 : 0) + Focusables(k);
                return c;
            }

            var row0 = vp.IsNull || !scene.TryGetScroll(vp, out var sc0) ? NodeHandle.Null : scene.FirstChild(sc0.ContentNode);
            if (!row0.IsNull) ClickNode(host, window, row0);
            Frames(3);
            Key(Keys.End);
            Settle(host);
            int atEnd = ctl.CurrentItemIndex;

            probe.Count.Value = CurrentClampProbe.Shrunk;          // the tail goes, the current row with it
            Settle(host);
            int afterShrink = ctl.CurrentItemIndex;

            if (bound)
            {
                Check("gate.virt.currentClamp.bound a bound ItemsView whose CountSignal drops below the keyboard-current index re-targets it onto the new last row (it kept the removed row's index, so CurrentItemIndex was out of range and Up/Down stepped from a phantom index)",
                    atEnd == CurrentClampProbe.N - 1 && afterShrink == CurrentClampProbe.Shrunk - 1,
                    $"atEnd={atEnd} afterShrink={afterShrink}");
                continue;
            }

            int stops = vp.IsNull || !scene.TryGetScroll(vp, out var sc1) ? -1 : Focusables(sc1.ContentNode);
            Key(Keys.Tab);                                         // focus dropped with the removed row: Tab back in
            bool tabbedIn = FocusInList();
            int afterTab = ctl.CurrentItemIndex;
            Key(Keys.Up);
            int afterUp = ctl.CurrentItemIndex;
            Check("gate.virt.currentClamp.renderItem a RenderItem ItemsView whose CountSignal drops below the keyboard-current index re-targets it onto the new last row: that row's container is the one roving tab stop, Tab lands on it and Up moves from it (no container matched the stale index, so Tab skipped the list)",
                atEnd == CurrentClampProbe.N - 1 && afterShrink == CurrentClampProbe.Shrunk - 1 && stops == 1
                    && tabbedIn && afterTab == CurrentClampProbe.Shrunk - 1 && afterUp == CurrentClampProbe.Shrunk - 2,
                $"atEnd={atEnd} afterShrink={afterShrink} stops={stops} tabbedIn={tabbedIn} afterTab={afterTab} afterUp={afterUp}");
        }
    }
}
