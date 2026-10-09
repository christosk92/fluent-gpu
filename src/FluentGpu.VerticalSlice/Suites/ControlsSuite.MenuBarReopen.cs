using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Hosting;
using FluentGpu.Pal;
using FluentGpu.Pal.Headless;
using FluentGpu.Rhi.Headless;
using FluentGpu.Scene;
using FluentGpu.Text.Headless;
using static FluentGpu.VerticalSlice.Harness.Gate;
using static FluentGpu.VerticalSlice.Harness.Asserts;

// ── A menu reopened inside its predecessor's close fade must survive that fade's ClosedAction (Controls/MenuBar.cs) ──
//
// BeginClose drops IsOpen at close START, but ClosedAction runs at Finalize, after the 83 ms fade. Hovering Edit and back
// onto File inside that fade reopened File into the same handle cell, and the OLD handle's ClosedAction then nulled the
// cell and reset the bar's OpenIndex to -1: the new File menu stayed up with no selected title, hover-switch stopped, and
// invoking a command ran it without closing the menu (its Close targeted the nulled cell). The pins: OpenIndex 0 -> 1 -> 0
// with the clock frozen (the reopen lands inside the fade), then let the fade finish - the bar still owns File, and
// invoking File's command closes it and the bar.
static partial class ControlsSuite
{
    sealed class MenuBarReopenProbe : Component
    {
        public readonly MenuBar.BarState Bar = new() { Count = 2, Nodes = new NodeHandle[2] };
        public IOverlayService? Service;
        public int Invoked;
        public override Element Render() => Embed.Comp(() => new OverlayHost { Child = Embed.Comp(() => new MenuBarReopenInner(this)) });
    }

    sealed class MenuBarReopenInner(MenuBarReopenProbe p) : Component
    {
        public override Element Render()
        {
            p.Service = UseContext(Overlay.Service);
            MenuFlyoutItem[] items = [new MenuFlyoutItem("mbr-cmd", Invoke: () => p.Invoked++)];
            return new BoxEl
            {
                Width = 480, Height = 360, Direction = 1,
                Children =
                [
                    new BoxEl
                    {
                        Direction = 0, OnRealized = h => p.Bar.Root = h,
                        Children =
                        [
                            Embed.Comp(() => new MenuBar.MenuBarButton { Index = 0, Title = "File", Items = items, Bar = p.Bar }) with { Key = "mbr:0" },
                            Embed.Comp(() => new MenuBar.MenuBarButton { Index = 1, Title = "Edit", Items = items, Bar = p.Bar }) with { Key = "mbr:1" },
                        ],
                    },
                ],
            };
        }
    }

    static void MenuBarReopenChecks(StringTable strings)
    {
        using var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("menubar-reopen", new Size2(480, 360), 1f));
        window.Show();
        var probe = new MenuBarReopenProbe();
        var clock = new ManualFrameTimeSource();
        using var host = new AppHost(app, window, new HeadlessGpuDevice(), new HeadlessFontSystem(strings), strings, probe, frameTime: clock);
        void SettleMenus() { for (int i = 0; i < 20; i++) { clock.Advance(16f); host.RunFrame(); } }
        void Frozen() { host.RunFrame(); host.RunFrame(); }   // no clock advance: the 83 ms close fade cannot finish
        host.RunFrame();
        var bar = probe.Bar;
        var svc = probe.Service!;

        bar.OpenIndex.Value = 0; SettleMenus();               // File open and settled
        bool opened = svc.AnyOpen;
        bar.OpenIndex.Value = 1; Frozen();                    // hover Edit: File starts its close fade
        bar.OpenIndex.Value = 0; Frozen();                    // back onto File inside that fade: File reopens
        SettleMenus();                                        // the first File menu's fade finalizes
        int idxAfterFade = bar.OpenIndex.Peek();
        bool liveAfterFade = svc.AnyOpen;

        ClickNode(host, window, FindTextNode(host.Scene, strings, host.Scene.Root, "mbr-cmd"));
        SettleMenus();
        int idxAfterInvoke = bar.OpenIndex.Peek();
        bool closedAfterInvoke = !svc.AnyOpen;

        Check("gate.menubar.reopen-in-fade a MenuBar title reopened inside its previous menu's 83 ms close fade keeps the bar's OpenIndex when that fade finalizes, and invoking its command closes it (the stale ClosedAction nulled the live handle and reset OpenIndex to -1, orphaning a menu that no command or hover could close)",
            opened && idxAfterFade == 0 && liveAfterFade && probe.Invoked == 1 && closedAfterInvoke && idxAfterInvoke == -1,
            $"opened={opened} idxAfterFade={idxAfterFade} liveAfterFade={liveAfterFade} invoked={probe.Invoked} closedAfterInvoke={closedAfterInvoke} idxAfterInvoke={idxAfterInvoke}");
    }
}
