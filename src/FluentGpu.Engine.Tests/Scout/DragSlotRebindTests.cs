using System;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Hosting;
using FluentGpu.Pal;
using FluentGpu.Pal.Headless;
using FluentGpu.Reconciler;
using FluentGpu.Rhi.Headless;
using FluentGpu.Scene;
using FluentGpu.Scroll.Diag;
using FluentGpu.Scroll.Runtime;
using FluentGpu.Text.Headless;
using Xunit;

namespace FluentGpu.Engine.Tests.Scout;

/// <summary>
/// A bound virtual list recycles a slot by writing its index signal: the slot root KEEPS its handle and starts showing
/// another item. A drag whose source row sits in that slot keyed everything on the handle, so once the source scrolled
/// out mid-gesture (wheel, or the drag's own edge auto-scroll) the dim / ghost lift and the hit-test opt-out followed the
/// slot onto the item it shows now. These drive a real headless host: press a row, promote the drag, wheel the list until
/// that row's slot is rebound, then check the slot carries no drag visuals and the gesture let go of it.
/// </summary>
[Collection(SerialTestCollection.Name)]
public sealed class DragSlotRebindTests
{
    private const float RowH = 40f;

    private sealed class BoundRows(DragLift lift) : Component
    {
        public override Element Render() => new VirtualListEl
        {
            ItemCount = 200, ItemLayout = new StackVirtualLayout(RowH), ScrollLineDip = RowH,
            Width = 200, Height = 200, Fill = ColorF.FromRgba(20, 20, 20),
            RowBind = idx => new BoxEl
            {
                Width = 180, Height = RowH, Fill = ColorF.FromRgba(60, 60, 60),
                CanDrag = true,
                Draggable = new DragSource("row", () => idx.Peek())
                {
                    Style = new DragVisualStyle { Lift = lift, Opacity = 0.4f },
                },
            },
        };
    }

    /// <summary>The item a realized slot currently shows: FirstRealized + its ordinal under the content node (the bound
    /// recycler's arrange contract).</summary>
    private static int ItemOf(SceneStore scene, NodeHandle slot)
    {
        if (!scene.TryGetScroll(scene.Root, out var sc)) return -1;
        int ord = 0;
        for (var c = scene.FirstChild(sc.ContentNode); !c.IsNull; c = scene.NextSibling(c), ord++)
            if (c == slot) return sc.FirstRealized + ord;
        return -1;
    }

    /// <summary>Press the top row, promote the drag sideways, then wheel the list down past it with the button held.</summary>
    private static NodeHandle DragTopRowThenScrollItAway(AppHost host, HeadlessWindow window)
    {
        var press = new Point2(60f, 20f);
        var held = new Point2(80f, 20f);
        window.QueueInput(new InputEvent(InputKind.PointerDown, press, 0, 0));
        host.RunFrame();
        window.QueueInput(new InputEvent(InputKind.PointerMove, held, 0, 0));
        host.RunFrame();
        var src = host.Input.Drag.ActiveNode;
        Assert.False(src.IsNull);
        Assert.Equal(0, ItemOf(host.Scene, src));

        float notch = (float)ScrollTunables.Current.WheelNotchDip;
        for (int i = 0; i < 12; i++)
        {
            var wheel = new ScrollInputEvent(ScrollSource.MouseWheel, ScrollGesture.Notch, 0, held, 0f, RowH / notch, 0, KeyModifiers.None);
            window.QueueInput(InputEvent.ForScroll(in wheel));
            host.RunFrame();
        }
        for (int i = 0; i < 30; i++) host.RunFrame();

        // The premise: the slot was REBOUND (still live, still realized, now another item), not freed.
        Assert.True(host.Scene.IsLive(src));
        int now = ItemOf(host.Scene, src);
        Assert.True(now > 0, $"the source slot should be realized on another item, got {now}");
        return src;
    }

    private static (HeadlessPlatformApp App, HeadlessWindow Window, AppHost Host) Build(DragLift lift)
    {
        var strings = new StringTable();
        var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("drag-slot-rebind", new Size2(320, 320), 1f));
        window.Show();
        var host = new AppHost(app, window, new HeadlessGpuDevice(), new HeadlessFontSystem(strings), strings, new BoundRows(lift));
        host.RunFrame();
        host.RunFrame();
        return (app, window, host);
    }

    [Fact]
    public void AStationaryDragLetsGoOfItsSourceSlotWhenTheSlotIsRebound()
    {
        var (app, window, host) = Build(DragLift.Stationary);
        using (app)
        using (host)
        {
            var slot = DragTopRowThenScrollItAway(host, window);
            var scene = host.Scene;

            Assert.Equal(1f, scene.Paint(slot).Opacity);                                   // the new item is not dimmed
            Assert.NotEqual(0u, (uint)(scene.Flags(slot) & NodeFlags.HitTestVisible));      // nor hidden from hit-tests
            Assert.True(host.Input.Drag.IsActive);                                          // the chip still carries the gesture
            Assert.True(host.Input.Drag.SourceRecycled);
            Assert.True(host.Input.Drag.ActiveNode.IsNull);
            Assert.Equal(scene.Root, host.Input.DragDrop.Session.Source);                   // L2 reparented, not the slot

            window.QueueInput(new InputEvent(InputKind.PointerUp, new Point2(80f, 20f), 0, 0));
            host.RunFrame();
            Assert.False(host.Input.Drag.IsActive);
            Assert.Equal(1f, scene.Paint(slot).Opacity);
        }
    }

    [Fact]
    public void AGhostDragAbortsAndUnliftsWhenItsSourceSlotIsRebound()
    {
        var (app, window, host) = Build(DragLift.Ghost);
        using (app)
        using (host)
        {
            var slot = DragTopRowThenScrollItAway(host, window);
            var scene = host.Scene;

            Assert.False(host.Input.Drag.IsActive);                                         // the ghost WAS the row: abort
            Assert.False(host.Input.DragDrop.IsActive);
            Assert.True(scene.DragGhost.IsNull);
            Assert.Equal(0u, (uint)(scene.Flags(slot) & NodeFlags.DragGhost));
            Assert.Equal(1f, scene.Paint(slot).Opacity);
            Assert.NotEqual(0u, (uint)(scene.Flags(slot) & NodeFlags.HitTestVisible));
        }
    }
}
