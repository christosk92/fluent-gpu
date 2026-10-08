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

// ── ItemsViewController.FocusItem + ListOptions.OnEdgeNavigate: a fixed head hands keyboard focus down into a bound
// list, and the list hands it back when the arrows run off an end. ────────────────────────────────────────────────
static partial class ControlsSuite
{
    sealed class ItemsFocusProbe : Component
    {
        public const int N = 20;
        public readonly ItemsViewController Controller = new();
        public NodeHandle Item2 = NodeHandle.Null;
        public int LastEdge;

        public override Element Render() => new BoxEl
        {
            Width = 240f, Height = 200f,
            Children =
            [
                ItemsView.CreateBound(N, scope =>
                {
                    int item = scope.Index.Peek();
                    return SelectorVisualsBound.None(in scope, new BoxEl { Height = 40f, Grow = 1f })
                        with { OnRealized = h => { if (item == 2) Item2 = h; } };
                }, RepeatLayout.Stack(40f), new ListOptions
                {
                    SelectionMode = ItemsSelectionMode.None, Controller = Controller,
                    OnEdgeNavigate = dir => LastEdge = dir,
                }),
            ],
        };
    }

    static void ItemsFocusApiChecks(StringTable strings)
    {
        using var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("items-focus", new Size2(240, 200), 1f));
        window.Show();
        var probe = new ItemsFocusProbe();
        using var host = new AppHost(app, window, new HeadlessGpuDevice(), new HeadlessFontSystem(strings), strings, probe);
        void Frames(int n) { for (int i = 0; i < n; i++) host.RunFrame(); }
        void Key(int key) { window.QueueInput(new InputEvent(InputKind.Key, default, 0, key)); Frames(3); }
        Frames(4);

        probe.Controller.FocusItem(2);
        Frames(3);
        bool current2 = probe.Controller.CurrentItemIndex == 2;
        bool focused2 = !probe.Item2.IsNull && host.Input.Focused == probe.Item2;
        probe.Controller.FocusItem(0);
        Frames(3);
        Key(Keys.Up);
        bool upEdge = probe.LastEdge == -1 && probe.Controller.CurrentItemIndex == 0;
        probe.LastEdge = 0;
        Key(Keys.Down);
        bool noEdgeInside = probe.LastEdge == 0 && probe.Controller.CurrentItemIndex == 1;

        Check("gate.virt.itemsFocus FocusItem makes an item current and focuses its slot; Up at the first item reports OnEdgeNavigate(−1) and keeps the item; Down inside the list reports nothing",
            current2 && focused2 && upEdge && noEdgeInside,
            $"current2={current2} focused2={focused2} upEdge={upEdge} noEdgeInside={noEdgeInside} current={probe.Controller.CurrentItemIndex} edge={probe.LastEdge}");
    }
}
