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

// ── ItemsView typeahead: an extended prefix stays on its match; no current item scans from item 0 (Controls/ItemsView.cs) ─
//
// OnRootChar scanned from current+1 on EVERY keystroke, and from max(0, current)+1 with no current item, so over
// ["Cat", "Catalog", "Dog"] typing "cat" with nothing current went c → Catalog (item 0 skipped), a → Cat, t → Catalog: an
// item's full name landed on a longer sibling, and in Single mode the selection and scroll hopped on every key. The pin:
// focus the viewport with no current item and type c/a/t one frame apart — the current item is Cat after each key.
static partial class ControlsSuite
{
    sealed class TypeaheadPrefixProbe : Component
    {
        public static readonly string[] Names = ["Cat", "Catalog", "Dog"];
        public readonly ItemsViewController Controller = new();

        public override Element Render() => new BoxEl
        {
            Width = 240f, Height = 200f,
            Children =
            [
                ItemsView.Create(Names.Length,
                    i => new BoxEl { Height = 40f, Grow = 1f, Children = [new TextEl(Names[i]) { Size = 12f }] },
                    RepeatLayout.Stack(40f),
                    new ListOptions { ItemText = i => Names[i], Controller = Controller }),
            ],
        };
    }

    static void TypeaheadPrefixChecks(StringTable strings)
    {
        using var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("iv-typeahead-prefix", new Size2(320, 240), 1f));
        window.Show();
        var probe = new TypeaheadPrefixProbe();
        using var host = new AppHost(app, window, new HeadlessGpuDevice(), new HeadlessFontSystem(strings), strings, probe);
        Settle(host);
        var ctl = probe.Controller;
        int before = ctl.CurrentItemIndex;
        var vp = FindScrollNode(host.Scene, host.Scene.Root);
        host.Input.SetFocus(vp);   // focus inside the view with NO current item: chars bubble to the root's typeahead
        var seen = new int[3];
        for (int k = 0; k < seen.Length; k++)
        {
            window.QueueInput(new InputEvent(InputKind.Char, default, 0, "cat"[k]));   // one frame apart: inside the 1 s reset
            host.RunFrame();
            seen[k] = ctl.CurrentItemIndex;
        }
        Check("gate.controls.itemsview-typeahead-prefix with no current item 'c' lands on item 0, and extending the prefix to \"ca\"/\"cat\" keeps the matching item current (every key scanned from current+1, so 'c' skipped item 0 and \"cat\" hopped Catalog→Cat→Catalog)",
            before == -1 && !vp.IsNull && seen[0] == 0 && seen[1] == 0 && seen[2] == 0,
            $"before={before} vpNull={vp.IsNull} c→{seen[0]} ca→{seen[1]} cat→{seen[2]}");
    }
}
