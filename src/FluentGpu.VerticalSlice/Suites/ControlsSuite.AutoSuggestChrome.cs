using System;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hosting;
using FluentGpu.Pal;
using FluentGpu.Pal.Headless;
using FluentGpu.Rhi.Headless;
using FluentGpu.Scene;
using FluentGpu.Signals;
using FluentGpu.Text.Headless;
using static FluentGpu.VerticalSlice.Harness.Gate;
using static FluentGpu.VerticalSlice.Harness.Asserts;

// ── AutoSuggestBoxPresenter.Chrome (Controls/AutoSuggestBox.cs) ──
//
// The suggestion popup was hard-wired to PopupChrome.Static (the WinUI behaviour: the list vanishes the instant it closes),
// so a rich presenter drawing a card list had no way to get the animated exit leg. AutoSuggestBoxPresenter.Chrome lets a
// presenter pick its chrome; the default stays Static, so the stock box and every existing presenter are unchanged.
// Reuses FocusDepartureProbe for the host/overlay-service plumbing.
static partial class ControlsSuite
{
    const string ChromeProbeRow = "chrome-probe-row";

    static void AutoSuggestChromeChecks(StringTable strings)
    {
        // Returns (opened, leftVisibleOneFrameAfterEscape, goneAfterSettle).
        (bool Opened, bool AliveAfterOneFrame, bool Gone, bool Escaped) Run(string name, PopupChrome? chrome)
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc(name, new Size2(480, 480), 1f)); window.Show();
            var text = new Signal<string>("");
            var presenter = chrome is { } c
                ? new AutoSuggestBoxPresenter(
                    ctx => new BoxEl { Width = 240, Height = 40, Children = [new TextEl(ChromeProbeRow) { Size = 14f }] }, Chrome: c)
                : new AutoSuggestBoxPresenter(
                    ctx => new BoxEl { Width = 240, Height = 40, Children = [new TextEl(ChromeProbeRow) { Size = 14f }] });
            var probe = new FocusDepartureProbe
            {
                Field = _ => AutoSuggestBox.Create([], "Search", 260f, text, queryIcon: null, debounceMs: 0f, presenter: presenter),
            };
            var clock = new ManualFrameTimeSource();
            using var host = new AppHost(app, window, new HeadlessGpuDevice(), new HeadlessFontSystem(strings), strings, probe, frameTime: clock);
            void Frames(int n) { for (int i = 0; i < n; i++) { clock.Advance(16f); host.RunFrame(); } }
            Frames(20);
            var svc = probe.Service!;
            var editor = host.Input.FirstFocusableIn(FindRole(host.Scene, host.Scene.Root, AutomationRole.ComboBox));

            host.Input.SetFocus(editor, visual: false);
            window.QueueInput(new InputEvent(InputKind.Char, default, 0, 'b'));
            Frames(20);
            bool opened = svc.AnyOpen && !FindTextNode(host.Scene, strings, host.Scene.Root, ChromeProbeRow).IsNull;

            window.QueueInput(new InputEvent(InputKind.Key, default, 0, Keys.Escape));
            Frames(2);   // the close has started; a Dropdown exit leg (167 ms) is still painting
            bool escaped = !svc.AnyOpen;
            bool alive = !FindTextNode(host.Scene, strings, host.Scene.Root, ChromeProbeRow).IsNull;
            Frames(30);
            bool gone = FindTextNode(host.Scene, strings, host.Scene.Root, ChromeProbeRow).IsNull;
            return (opened, alive, gone, escaped);
        }

        var rich = Run("asb-presenter-dropdown", PopupChrome.Dropdown);
        var stock = Run("asb-presenter-default", null);

        Check("gate.controls.autosuggest-presenter-chrome AutoSuggestBoxPresenter.Chrome = Dropdown runs an exit leg after Escape and is then gone, while the default presenter (Static) closes at once",
            rich.Opened && rich.Escaped && rich.AliveAfterOneFrame && rich.Gone
            && stock.Opened && stock.Escaped && !stock.AliveAfterOneFrame && stock.Gone,
            $"dropdown(opened={rich.Opened} escaped={rich.Escaped} alive={rich.AliveAfterOneFrame} gone={rich.Gone}) default(opened={stock.Opened} escaped={stock.Escaped} alive={stock.AliveAfterOneFrame} gone={stock.Gone})");
    }
}
