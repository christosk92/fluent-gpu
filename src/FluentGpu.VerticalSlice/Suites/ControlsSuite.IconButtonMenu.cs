using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Hosting;
using FluentGpu.Pal;
using FluentGpu.Pal.Headless;
using FluentGpu.Rhi.Headless;
using FluentGpu.Scene;
using FluentGpu.Text;
using FluentGpu.Text.Headless;
using static FluentGpu.VerticalSlice.Harness.Gate;
using static FluentGpu.VerticalSlice.Harness.Asserts;

// ── IconButton.Menu + PopupOptions.Menu (docs/plans/app-authoring-ergonomics-implementation.md §2) ──────────────────
//
// An icon-only menu button in a small tool window: the menu must escape a window too small for it (PopupOptions.Menu is
// windowed-when-escaping), a re-click must close rather than close-and-reopen (the light-dismiss scrim owns the press),
// a click inside the close fade must reopen safely, an items factory with nothing enabled opens nothing, and a keyboard
// open (Down, or Space on the focused button) focuses the first row.
static partial class ControlsSuite
{
    sealed class IconMenuProbe : Component
    {
        public IOverlayService? Service;
        public int Builds;
        public bool Empty, AllDisabled;
        public int Rows = 6;
        public override Element Render() => Embed.Comp(() => new OverlayHost { Child = Embed.Comp(() => new IconMenuInner(this)) });
    }

    sealed class IconMenuInner(IconMenuProbe p) : Component
    {
        IReadOnlyList<MenuFlyoutItem> Build()
        {
            p.Builds++;
            if (p.Empty) return [];
            if (p.AllDisabled) return [new MenuFlyoutItem("im-off", Enabled: false), MenuFlyoutItem.Separator];
            var rows = new MenuFlyoutItem[p.Rows];
            for (int i = 0; i < rows.Length; i++) rows[i] = new MenuFlyoutItem("im-" + i, Invoke: () => { });
            return rows;
        }

        public override Element Render()
        {
            p.Service = UseContext(Overlay.Service);
            return new BoxEl
            {
                Direction = 1, Padding = Edges4.All(8),
                Children = [IconButton.Menu(Icons.More, Build, FlyoutPlacement.BottomEdgeAlignedLeft)],
            };
        }
    }

    static void IconButtonMenuChecks(StringTable strings)
    {
        static (HeadlessPlatformApp app, HeadlessWindow w, AppHost host, IconMenuProbe probe, ManualFrameTimeSource clock)
            Make(StringTable strings, string title, Size2 size, Action<IconMenuProbe>? setup = null)
        {
            var app = new HeadlessPlatformApp();
            var w = new HeadlessWindow(new WindowDesc(title, size, 1f)); w.Show();
            var probe = new IconMenuProbe();
            setup?.Invoke(probe);
            var clock = new ManualFrameTimeSource();
            var host = new AppHost(app, w, new HeadlessGpuDevice(), new HeadlessFontSystem(strings), strings, probe, frameTime: clock);
            host.RunFrame();
            return (app, w, host, probe, clock);
        }
        static void Settle(AppHost host, ManualFrameTimeSource clock) { for (int i = 0; i < 30; i++) { clock.Advance(16f); host.RunFrame(); } }
        static NodeHandle Btn(AppHost host) => FindRole(host.Scene, host.Scene.Root, AutomationRole.Button);

        // gate.iconmenu.windowed — a 6-row menu (≈200 DIP) under a button in a 240×90 window cannot fit below it: the
        // PopupOptions.Menu open leases a popup window (NeedsPopupWindow) instead of clipping. The same menu in a
        // 480×400 window fits and stays in-window (no lease).
        {
            var (app, w, host, probe, clock) = Make(strings, "iconmenu-small", new Size2(240, 90));
            using var _a = app; using var _h = host;
            ClickNode(host, w, Btn(host)); Settle(host, clock);
            bool open = probe.Service!.AnyOpen;
            int leasedSmall = host.PopupWindows.Count;
            bool rowFound = !FindRole(host.Scene, host.Scene.Root, AutomationRole.MenuItem).IsNull;

            var (app2, w2, host2, probe2, clock2) = Make(strings, "iconmenu-big", new Size2(480, 400));
            using var _a2 = app2; using var _h2 = host2;
            ClickNode(host2, w2, Btn(host2)); Settle(host2, clock2);
            int leasedBig = host2.PopupWindows.Count;
            bool openBig = probe2.Service!.AnyOpen;
            Check("gate.iconmenu.windowed IconButton.Menu in a window smaller than its menu opens WINDOWED (PopupOptions.Menu escapes); in a roomy window it stays in-window",
                open && rowFound && leasedSmall == 1 && openBig && leasedBig == 0,
                $"open={open} rows={rowFound} leasedSmall={leasedSmall} openBig={openBig} leasedBig={leasedBig}");
        }

        // gate.iconmenu.reclick-toggles — click opens; a re-click on the button closes and does NOT reopen (the scrim
        // closes on the press and owns it, so the release is no click on the button — Builds stays 1). A click inside
        // the 83 ms close fade opens a fresh menu, and that menu survives the old fade's ClosedAction: after the fade
        // finalizes a further click still closes it (the handle cell was not nulled under it).
        {
            var (app, w, host, probe, clock) = Make(strings, "iconmenu-reclick", new Size2(480, 400));
            using var _a = app; using var _h = host;
            var btn = Btn(host);
            ClickNode(host, w, btn); Settle(host, clock);
            bool opened = probe.Service!.AnyOpen && probe.Builds == 1;
            ClickNode(host, w, btn); Settle(host, clock);
            bool closed = !probe.Service!.AnyOpen && probe.Builds == 1;

            ClickNode(host, w, btn); Settle(host, clock);               // open #2
            ClickNode(host, w, btn);                                      // close #2 — the clock is frozen: inside its fade
            ClickNode(host, w, btn);                                      // the scrim is gone: this click reopens (#3)
            bool reopenedInFade = probe.Builds == 3 && probe.Service!.AnyOpen;
            Settle(host, clock);                                          // #2's fade finalizes; its ClosedAction runs
            bool liveAfterFade = probe.Service!.AnyOpen;
            ClickNode(host, w, btn); Settle(host, clock);
            bool closedAfter = !probe.Service!.AnyOpen && probe.Builds == 3;
            Check("gate.iconmenu.reclick-toggles a re-click closes the menu without reopening; a click during the close fade reopens and that menu still closes on the next click",
                opened && closed && reopenedInFade && liveAfterFade && closedAfter,
                $"opened={opened} closed={closed} reopenInFade={reopenedInFade} liveAfterFade={liveAfterFade} closedAfter={closedAfter} builds={probe.Builds}");
        }

        // gate.iconmenu.empty-no-open — an items factory returning [] (or only disabled/separator rows) opens nothing;
        // the factory still ran (at click time, not render).
        {
            var (app, w, host, probe, clock) = Make(strings, "iconmenu-empty", new Size2(480, 400), p => p.Empty = true);
            using var _a = app; using var _h = host;
            bool notAtRender = probe.Builds == 0;
            ClickNode(host, w, Btn(host)); Settle(host, clock);
            bool emptyNoOpen = !probe.Service!.AnyOpen && probe.Builds == 1
                               && FindRole(host.Scene, host.Scene.Root, AutomationRole.MenuItem).IsNull;

            var (app2, w2, host2, probe2, clock2) = Make(strings, "iconmenu-disabled", new Size2(480, 400), p => p.AllDisabled = true);
            using var _a2 = app2; using var _h2 = host2;
            ClickNode(host2, w2, Btn(host2)); Settle(host2, clock2);
            bool disabledNoOpen = !probe2.Service!.AnyOpen && probe2.Builds == 1;
            Check("gate.iconmenu.empty-no-open an empty (or all-disabled) items factory opens nothing; the factory runs at click, not render",
                notAtRender && emptyNoOpen && disabledNoOpen,
                $"atRender={notAtRender} empty={emptyNoOpen} disabled={disabledNoOpen} builds={probe.Builds}/{probe2.Builds}");
        }

        // gate.iconmenu.keyboard-focuses-first — Down on the focused button opens the menu with the FIRST row focused;
        // Escape closes it and restores focus to the button; Space on the (now focused) button opens it again, first
        // row focused.
        {
            var (app, w, host, probe, clock) = Make(strings, "iconmenu-kbd", new Size2(480, 400));
            using var _a = app; using var _h = host;
            var btn = Btn(host);
            host.Input.SetFocus(btn, visual: true);
            host.RunFrame();
            w.QueueInput(new InputEvent(InputKind.Key, default, 0, Keys.Down)); Settle(host, clock);
            var mi = FindRole(host.Scene, host.Scene.Root, AutomationRole.MenuItem);
            bool downFirst = probe.Service!.AnyOpen && !mi.IsNull && host.Input.Focused == mi;

            w.QueueInput(new InputEvent(InputKind.Key, default, 0, Keys.Escape)); Settle(host, clock);
            bool restored = !probe.Service!.AnyOpen && host.Input.Focused == btn;

            w.QueueInput(new InputEvent(InputKind.Key, default, 0, Keys.Space));
            w.QueueInput(new InputEvent(InputKind.KeyUp, default, 0, Keys.Space));
            Settle(host, clock);
            var mi2 = FindRole(host.Scene, host.Scene.Root, AutomationRole.MenuItem);
            bool spaceFirst = probe.Service!.AnyOpen && !mi2.IsNull && host.Input.Focused == mi2;
            w.QueueInput(new InputEvent(InputKind.Key, default, 0, Keys.Escape)); Settle(host, clock);
            bool restoredAgain = !probe.Service!.AnyOpen && host.Input.Focused == btn;
            Check("gate.iconmenu.keyboard-focuses-first Down / Space on the focused IconButton.Menu open the menu with the first row focused; Escape restores focus to the button",
                downFirst && restored && spaceFirst && restoredAgain,
                $"down={downFirst} restored={restored} space={spaceFirst} restoredAgain={restoredAgain} focus={host.Input.Focused.Raw.Index}");
        }
    }
}
