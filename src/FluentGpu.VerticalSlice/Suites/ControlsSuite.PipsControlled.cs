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

// ── E12 (home-redesign-remediation.md §2 E12/§3.2, C:\wavee\waveemusic): PipsPager.Controlled (Controls/PipsPager.cs)
// + IconButton's bound-enabled overload (Controls/IconButton.cs). ChapterHeader is a plain element FUNCTION (no
// hooks) wired to a ShelfController: it needs a pips strip whose selected index is a plain VALUE (no internal
// signal — PipsPagerCoreBase.RenderPager is shared with Create, but PipsPagerControlledCore never materializes an
// `own` signal) where a click on the pip already named as selected still calls onSelect (the free-pannable shelf
// re-snap edge Create's own onReselect split exists to AVOID), and a chevron pair whose enabled state follows
// controller.CanPrev/CanNext with NO re-render of ChapterHeader itself.
static partial class ControlsSuite
{
    static void PipsControlledChecks(StringTable strings)
    {
        PipsControlledNoStateChecks(strings);
        PipsControlledReselectChecks(strings);
    }

    sealed class PipsControlledProbe : Component
    {
        public readonly Signal<int> SelectedIndex = new(0);
        public readonly int Count;
        public int OnSelectCalls;
        public int LastSelected = -1;

        public PipsControlledProbe(int count) => Count = count;

        public override Element Render() => new BoxEl
        {
            Direction = 1, Width = 400f, Height = 60f,
            Children =
            [
                PipsPager.Controlled(Count, SelectedIndex.Value, i => { OnSelectCalls++; LastSelected = i; }) with { Key = "pips" },
            ],
        };
    }

    static (AppHost host, HeadlessWindow window, PipsControlledProbe probe) NewPipsControlledHarness(StringTable strings, string name, int count)
    {
        var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc(name, new Size2(500, 100), 1f));
        window.Show();
        var probe = new PipsControlledProbe(count);
        var host = new AppHost(app, window, new HeadlessGpuDevice(), new HeadlessFontSystem(strings), strings, probe);
        return (host, window, probe);
    }

    // Roles(scene, Pager) collects the pager ROOT (also Role.Pager) first, then each pip dot in visual order — so
    // index 0 is the root and 1..count are the dots (default maxVisiblePips=5 keeps all 5 dots mounted un-clipped).
    static List<NodeHandle> PipDots(AppHost host) => Roles(host.Scene, AutomationRole.Pager);

    static void PipsControlledNoStateChecks(StringTable strings)
    {
        var (host, window, probe) = NewPipsControlledHarness(strings, "pips-controlled-no-state", 5);
        using var _ = host;
        Settle(host);

        var dots = PipDots(host);
        bool sixNodes = dots.Count == 6;   // 1 root + 5 pips

        // Click pip index 3 TWICE in a row. The probe's own SelectedIndex signal is never touched between clicks —
        // if PipsPagerControlledCore secretly kept an internal "last selected" signal (a bug reintroducing Create's
        // dedup), the second click on the same un-acknowledged pip would be suppressed or misrouted. It is not:
        // Controlled writes no state at all, so onSelect fires identically both times.
        if (sixNodes) ClickNode(host, window, dots[1 + 3]);
        int callsAfterFirst = probe.OnSelectCalls;
        int lastAfterFirst = probe.LastSelected;

        if (sixNodes) ClickNode(host, window, dots[1 + 3]);
        int callsAfterSecond = probe.OnSelectCalls;
        int lastAfterSecond = probe.LastSelected;

        Check("gate.pips.controlled.no-state PipsPager.Controlled writes NO internal selection state — clicking the same un-acknowledged pip twice (the caller's selectedIndex prop is never re-pushed between clicks) calls onSelect BOTH times with the same index, with no hidden signal to dedupe the second click",
            sixNodes && callsAfterFirst == 1 && lastAfterFirst == 3 && callsAfterSecond == 2 && lastAfterSecond == 3,
            $"dots={dots.Count} callsAfterFirst={callsAfterFirst} lastAfterFirst={lastAfterFirst} callsAfterSecond={callsAfterSecond} lastAfterSecond={lastAfterSecond}");
    }

    static void PipsControlledReselectChecks(StringTable strings)
    {
        var (host, window, probe) = NewPipsControlledHarness(strings, "pips-controlled-reselect", 5);
        using var _ = host;
        Settle(host);

        // Publish selectedIndex == 2 through a REAL re-render (the props-re-push contract) so pip 2 is the one the
        // strip currently shows as selected.
        probe.SelectedIndex.Value = 2;
        Settle(host);
        var dots = PipDots(host);
        bool sixNodes = dots.Count == 6;
        int callsBefore = probe.OnSelectCalls;

        // Click that SAME pip 2 — a re-click PipsPager.Create would fold into the separate onReselect channel and
        // skip entirely for onChange. Controlled has only ONE channel, and it must still fire: the free-pannable
        // shelf's scroll can rest mid-page while the pip reads the settled page, and the re-click is the user
        // asking ShelfController.GoTo to re-snap — which needs onSelect to fire even when the index doesn't change.
        if (sixNodes) ClickNode(host, window, dots[1 + 2]);

        Check("gate.pips.controlled.reselect a click on the pip PipsPager.Controlled's selectedIndex prop ALREADY names still calls onSelect — one channel, no onChange/onReselect split",
            sixNodes && probe.OnSelectCalls == callsBefore + 1 && probe.LastSelected == 2,
            $"dots={dots.Count} callsBefore={callsBefore} callsAfter={probe.OnSelectCalls} last={probe.LastSelected}");
    }

    // ── IconButton's bound-enabled overload ──────────────────────────────────────────────────────────────────────

    sealed class IconButtonBoundEnabledProbe : Component
    {
        public readonly Signal<bool> Enabled = new(true);
        public int Clicks;
        public int RenderCount;

        public override Element Render()
        {
            RenderCount++;
            return new BoxEl
            {
                Direction = 1, Width = 100f, Height = 60f,
                Children = [IconButton.Create("i", () => Clicks++, Enabled) with { Key = "btn" }],
            };
        }
    }

    static void IconButtonBoundEnabledChecks(StringTable strings)
    {
        var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("iconbutton-bound-enabled", new Size2(200, 100), 1f));
        window.Show();
        var probe = new IconButtonBoundEnabledProbe();
        using var host = new AppHost(app, window, new HeadlessGpuDevice(), new HeadlessFontSystem(strings), strings, probe);
        Settle(host);

        var buttons = Roles(host.Scene, AutomationRole.Button);
        bool oneButton = buttons.Count == 1;
        bool enabledAtStart = oneButton && (host.Scene.Flags(buttons[0]) & NodeFlags.Disabled) == 0;
        int rendersAtStart = probe.RenderCount;

        if (oneButton) ClickNode(host, window, buttons[0]);
        int clicksWhileEnabled = probe.Clicks;

        // Flip the BOUND signal directly — never touches the probe's own Render(). The chevron-pair scenario this
        // gate stands in for (ChapterHeader's IconButtons bound to ShelfController.CanPrev/CanNext) needs exactly
        // this: the owning row never re-renders on a page change.
        probe.Enabled.Value = false;
        Settle(host);
        var buttonsAfterDisable = Roles(host.Scene, AutomationRole.Button);
        bool stillOneButton = buttonsAfterDisable.Count == 1;
        bool disabledNow = stillOneButton && (host.Scene.Flags(buttonsAfterDisable[0]) & NodeFlags.Disabled) != 0;
        int rendersAfterDisable = probe.RenderCount;

        if (stillOneButton) ClickNode(host, window, buttonsAfterDisable[0]);   // disabled: the click is swallowed
        int clicksWhileDisabled = probe.Clicks;

        probe.Enabled.Value = true;
        Settle(host);
        var buttonsAfterReenable = Roles(host.Scene, AutomationRole.Button);
        bool reenabled = buttonsAfterReenable.Count == 1 && (host.Scene.Flags(buttonsAfterReenable[0]) & NodeFlags.Disabled) == 0;
        int rendersAfterReenable = probe.RenderCount;

        Check("gate.iconbutton.enabled-bound IconButton.Create's IReadSignal<bool> isEnabled overload tracks the signal both ways (hit-test gates on/off with it) with NO re-render of the caller that mounted it — the probe's own Render() runs exactly once across both flips",
            oneButton && enabledAtStart && clicksWhileEnabled == 1 &&
            stillOneButton && disabledNow && clicksWhileDisabled == clicksWhileEnabled &&
            reenabled && rendersAfterDisable == rendersAtStart && rendersAfterReenable == rendersAtStart,
            $"btn0={oneButton} enabledStart={enabledAtStart} clicksEnabled={clicksWhileEnabled} disabledNow={disabledNow} clicksDisabled={clicksWhileDisabled} reenabled={reenabled} renders0={rendersAtStart} rendersDisabled={rendersAfterDisable} rendersReenabled={rendersAfterReenable}");
    }
}
