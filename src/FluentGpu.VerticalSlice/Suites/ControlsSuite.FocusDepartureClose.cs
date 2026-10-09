using System;
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

// ── Tab out of a field whose popup is open closes the popup (Controls/AutoSuggestBox.cs, Controls/ComboBox.cs) ──
//
// AutoSuggestBox's suggestion list and the editable ComboBox dropdown keep focus in the text field, with no focus trap,
// and nothing closed them when focus LEFT: Tab moved focus to the next control while the list stayed attached under a
// field that no longer had focus (Up/Down/Enter now drove the other control), and the editable ComboBox held its commit
// back until a later click light-dismissed the dropdown. WinUI closes both in LostFocus. A press on a popup row also
// blurs the field (rows focus on the press edge), so the close must skip focus that moved INTO the popup: the ASB arm
// re-opens the list and clicks a row, which must still submit.
static partial class ControlsSuite
{
    sealed class FocusDepartureProbe : Component
    {
        public required Func<FocusDepartureProbe, Element> Field;
        public IOverlayService? Service;
        public NodeHandle Next;
        public string? Submitted;
        public override Element Render() => Embed.Comp(() => new OverlayHost { Child = Embed.Comp(() => new FocusDepartureInner(this)) });
    }

    sealed class FocusDepartureInner(FocusDepartureProbe p) : Component
    {
        public override Element Render()
        {
            p.Service = UseContext(Overlay.Service);
            return new BoxEl
            {
                Width = 480, Height = 480, Direction = 1,
                Children =
                [
                    p.Field(p),
                    // The next tab stop after the field (base content precedes the overlay layer in tab order).
                    new BoxEl { Width = 80, Height = 32, Focusable = true, OnRealized = h => p.Next = h },
                ],
            };
        }
    }

    static void FocusDepartureCloseChecks(StringTable strings)
    {
        // ── AutoSuggestBox ──
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("asb-tab-close", new Size2(480, 480), 1f)); window.Show();
            var text = new Signal<string>("");
            var probe = new FocusDepartureProbe
            {
                Field = p => AutoSuggestBox.Create(["burger", "bun", "cake"], "Search", 260f, text,
                    onQuerySubmitted: s => p.Submitted = s, queryIcon: null, debounceMs: 0f),
            };
            var clock = new ManualFrameTimeSource();
            using var host = new AppHost(app, window, new HeadlessGpuDevice(), new HeadlessFontSystem(strings), strings, probe, frameTime: clock);
            void Settle() { for (int i = 0; i < 20; i++) { clock.Advance(16f); host.RunFrame(); } }
            Settle();
            var svc = probe.Service!;
            var editor = host.Input.FirstFocusableIn(FindRole(host.Scene, host.Scene.Root, AutomationRole.ComboBox));

            host.Input.SetFocus(editor, visual: false);
            window.QueueInput(new InputEvent(InputKind.Char, default, 0, 'b'));
            window.QueueInput(new InputEvent(InputKind.Char, default, 0, 'u'));
            Settle();
            bool opened = svc.AnyOpen;

            window.QueueInput(new InputEvent(InputKind.Key, default, 0, Keys.Tab));
            Settle();
            bool tabbedAway = host.Input.Focused == probe.Next;
            bool closedOnTab = !svc.AnyOpen;

            // Guard: a row press focuses the row (inside the popup) — that blur must not close the list under the click.
            host.Input.SetFocus(editor, visual: false);
            window.QueueInput(new InputEvent(InputKind.Char, default, 0, 'r'));
            Settle();
            bool reopened = svc.AnyOpen;
            var row = FindTextNode(host.Scene, strings, host.Scene.Root, "burger");
            if (!row.IsNull) ClickNode(host, window, row);
            Settle();
            bool rowSubmitted = probe.Submitted == "burger" && !svc.AnyOpen;

            Check("gate.controls.autosuggest-tab-close Tab out of an AutoSuggestBox closes its open suggestion list (it stayed attached under the blurred field), while a row click still submits",
                opened && tabbedAway && closedOnTab && reopened && rowSubmitted,
                $"opened={opened} tabbedAway={tabbedAway} closedOnTab={closedOnTab} reopened={reopened} rowNull={row.IsNull} submitted='{probe.Submitted}' open={svc.AnyOpen}");
        }

        // ── Editable ComboBox ──
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("combo-tab-close", new Size2(480, 480), 1f)); window.Show();
            var sel = new Signal<int>(-1);
            var text = new Signal<string>("");
            var probe = new FocusDepartureProbe
            {
                Field = _ => ComboBox.Create(["Apple", "Banana", "Cherry"], sel, editable: true, text: text, openOnMount: true),
            };
            var clock = new ManualFrameTimeSource();
            using var host = new AppHost(app, window, new HeadlessGpuDevice(), new HeadlessFontSystem(strings), strings, probe, frameTime: clock);
            void Settle() { for (int i = 0; i < 20; i++) { clock.Advance(16f); host.RunFrame(); } }
            Settle();
            var svc = probe.Service!;
            bool opened = svc.AnyOpen;
            // The field (base content) precedes the dropdown's own ComboBox-role list root in tree order.
            var editor = host.Input.FirstFocusableIn(FindRole(host.Scene, host.Scene.Root, AutomationRole.ComboBox));
            host.Input.SetFocus(editor, visual: false);
            window.QueueInput(new InputEvent(InputKind.Char, default, 0, 'B'));   // search-as-you-type → "Banana"
            Settle();
            bool uncommitted = sel.Peek() == -1;

            window.QueueInput(new InputEvent(InputKind.Key, default, 0, Keys.Tab));
            Settle();   // the 167 ms dropdown close finalizes; its ClosedAction commits the search
            bool tabbedAway = host.Input.Focused == probe.Next;
            bool closed = !svc.AnyOpen;
            bool committed = sel.Peek() == 1;

            Check("gate.controls.combobox-editable-tab-close Tab out of an editable ComboBox with its dropdown open closes the dropdown and commits the search (it stayed open and held the commit until a later light-dismiss)",
                opened && uncommitted && tabbedAway && closed && committed,
                $"opened={opened} uncommitted={uncommitted} tabbedAway={tabbedAway} closed={closed} sel={sel.Peek()} text='{text.Peek()}'");
        }
    }
}
