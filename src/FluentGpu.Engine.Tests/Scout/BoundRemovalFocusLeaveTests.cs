using System;
using System.Collections.Generic;
using FluentGpu.Animation;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Hosting;
using FluentGpu.Pal;
using FluentGpu.Pal.Headless;
using FluentGpu.Reconciler;
using FluentGpu.Rhi.Headless;
using FluentGpu.Scene;
using FluentGpu.Scroll.Runtime;
using FluentGpu.Signals;
using FluentGpu.Text.Headless;
using Xunit;

namespace FluentGpu.Engine.Tests.Scout;

/// <summary>
/// A bound list's animated removal (ItemsViewController.BeginRemoval -> BeginVirtualRemoval) retired the realized rows
/// itself and never ran the reconciler's removal hook, so the focus leave Remove() gets was skipped. The exit orphan
/// stayed LIVE and focused through its fade: no LostFocus reached it or its ancestors, and Enter during the fade still
/// reached the removed row's handler. Without an exit the freed row's focus was only nulled silently on the next dispatch.
/// </summary>
[Collection(SerialTestCollection.Name)]   // builds an AppHost; HostDispatch.Current is process-static
public sealed class BoundRemovalFocusLeaveTests
{
    private const float RowH = 40f;

    private sealed class Rows : Component
    {
        public readonly Signal<int> Count = new(50);
        public readonly List<(int Item, int Key)> Hits = [];
        public readonly List<string> Edges = [];
        public Action<NodeHandle, IReadOnlyList<int>, EnterExit, MotionTokenId, float, Action>? Remove;

        public override Element Render()
        {
            Remove = Context.BeginVirtualRemoval;   // the seam ItemsViewController.BeginRemoval rides
            return new VirtualListEl
            {
                ItemCount = Count.Value, ItemLayout = new StackVirtualLayout(RowH), ScrollLineDip = RowH,
                Width = 200, Height = 200, Fill = ColorF.FromRgba(20, 20, 20),
                RowBind = idx => new BoxEl
                {
                    Width = 180, Height = RowH, Fill = ColorF.FromRgba(60, 60, 60),
                    OnClick = () => Hits.Add((idx.Peek(), 0)),
                    OnFocusChanged = g => Edges.Add(g ? "row+" : "row-"),
                    OnKeyDown = a =>
                    {
                        if (a.KeyCode != Keys.Enter) return;
                        Hits.Add((idx.Peek(), Keys.Enter));
                        a.Handled = true;
                    },
                },
            };
        }
    }

    private static void Press(AppHost host, HeadlessWindow window, int key)
    {
        window.QueueInput(new InputEvent(InputKind.Key, default, 0, key));
        window.QueueInput(new InputEvent(InputKind.KeyUp, default, 0, key));
        host.RunFrame();
    }

    [Theory]
    [InlineData(true)]    // exit active: the row orphans and fades, still LIVE
    [InlineData(false)]   // no exit: the row is freed at once
    public void RemovingTheFocusedBoundRowRunsTheFocusLeave(bool animate)
    {
        var strings = new StringTable();
        var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("bound-removal-focus", new Size2(320, 320), 1f));
        window.Show();
        var rows = new Rows();
        var host = new AppHost(app, window, new HeadlessGpuDevice(), new HeadlessFontSystem(strings), strings, rows);
        try
        {
            host.RunFrame();
            host.RunFrame();
            var scene = host.Scene;
            var vp = scene.Root;
            Assert.True(scene.TryGetScroll(vp, out var sc));
            var row = scene.FirstChild(sc.ContentNode);

            window.QueueInput(new InputEvent(InputKind.PointerDown, new Point2(150f, 20f), 0, 0));
            window.QueueInput(new InputEvent(InputKind.PointerUp, new Point2(150f, 20f), 0, 0));
            host.RunFrame();
            Assert.Equal(row, host.Input.Focused);   // the premise: the click focused the row showing item 0
            Assert.Equal(new[] { (0, 0) }, rows.Hits.ToArray());
            rows.Edges.Clear();

            rows.Remove!(vp, new[] { 0 }, new EnterExit(Opacity: 0f, Active: animate), MotionTokenId.StandardExit, 0f,
                         () => rows.Count.Value = 49);
            if (animate)
            {
                Assert.True(scene.IsLive(row));
                Assert.NotEqual(default(NodeFlags), scene.Flags(row) & NodeFlags.Exiting);
            }
            Assert.Equal(new[] { "row-" }, rows.Edges.ToArray());   // heard at removal, before any further input
            Assert.True(host.Input.Focused.IsNull);

            Press(host, window, Keys.Enter);
            Assert.Equal(new[] { (0, 0) }, rows.Hits.ToArray());    // neither the fading row nor the item now at index 0 activates
        }
        finally { host.Dispose(); app.Dispose(); }
    }
}
