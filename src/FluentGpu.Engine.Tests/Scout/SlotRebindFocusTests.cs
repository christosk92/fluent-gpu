using System.Collections.Generic;
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
/// A virtual list recycles a row node for another item while keeping its handle: the bound path writes the slot's index
/// signal, the keyed RenderItem path rewrites the node's columns in place. Focus is keyed on that handle, so a row focused
/// by a click kept keyboard focus after a wheel scroll rebound it, and Enter/Space or typing then acted on whichever item
/// the node shows now (Wavee: click a track, scroll two screens, press Enter, and a different track plays). These drive a
/// real headless host: focus the top row, wheel it out of the window, press keys, and check nothing reached the new item.
/// </summary>
[Collection(SerialTestCollection.Name)]
public sealed class SlotRebindFocusTests
{
    private const float RowH = 40f;

    /// <summary>What reached a row: (the item the row showed at the time, the key code or 0 for a click).</summary>
    private sealed class Log
    {
        public readonly List<(int Item, int Key)> Hits = [];
    }

    private sealed class BoundRows(Log log, bool innerField) : Component
    {
        public override Element Render() => new VirtualListEl
        {
            ItemCount = 200, ItemLayout = new StackVirtualLayout(RowH), ScrollLineDip = RowH,
            Width = 200, Height = 200, Fill = ColorF.FromRgba(20, 20, 20),
            RowBind = idx => new BoxEl
            {
                Width = 180, Height = RowH, Fill = ColorF.FromRgba(60, 60, 60),
                OnClick = () => log.Hits.Add((idx.Peek(), 0)),
                OnKeyDown = a =>
                {
                    if (a.KeyCode != Keys.Enter) return;
                    log.Hits.Add((idx.Peek(), Keys.Enter));
                    a.Handled = true;
                },
                Children = innerField
                    ?
                    [
                        new BoxEl   // an inline field (a rename box): focusable, takes the keys typed into it
                        {
                            Width = 80, Height = 24, Focusable = true,
                            OnKeyDown = a => { log.Hits.Add((idx.Peek(), a.KeyCode)); a.Handled = true; },
                        },
                    ]
                    : [],
            },
        };
    }

    private sealed class KeyedRows(Log log) : Component
    {
        public override Element Render() => new VirtualListEl
        {
            ItemCount = 200, ItemLayout = new StackVirtualLayout(RowH), ScrollLineDip = RowH,
            Width = 200, Height = 200, Fill = ColorF.FromRgba(20, 20, 20),
            RenderItem = i => new BoxEl   // a plain box: recyclable, so a scrolled-out node is rewritten for a new item
            {
                Width = 180, Height = RowH, Fill = ColorF.FromRgba(60, 60, 60),
                OnClick = () => log.Hits.Add((i, 0)),
            },
        };
    }

    private static (HeadlessPlatformApp App, HeadlessWindow Window, AppHost Host) Build(Component root)
    {
        var strings = new StringTable();
        var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("slot-rebind-focus", new Size2(320, 320), 1f));
        window.Show();
        var host = new AppHost(app, window, new HeadlessGpuDevice(), new HeadlessFontSystem(strings), strings, root);
        host.RunFrame();
        host.RunFrame();
        return (app, window, host);
    }

    /// <summary>The item a realized row node currently shows: FirstRealized + its ordinal under the content node.</summary>
    private static int ItemOf(SceneStore scene, NodeHandle row)
    {
        if (!scene.TryGetScroll(scene.Root, out var sc)) return -1;
        int ord = 0;
        for (var c = scene.FirstChild(sc.ContentNode); !c.IsNull; c = scene.NextSibling(c), ord++)
            if (c == row) return sc.FirstRealized + ord;
        return -1;
    }

    private static NodeHandle TopRow(SceneStore scene)
    {
        Assert.True(scene.TryGetScroll(scene.Root, out var sc));
        return scene.FirstChild(sc.ContentNode);
    }

    /// <summary>Wheel the list well past its first screen with the pointer resting over it (no button held).</summary>
    private static void WheelAway(AppHost host, HeadlessWindow window)
    {
        float notch = (float)ScrollTunables.Current.WheelNotchDip;
        for (int i = 0; i < 12; i++)
        {
            var wheel = new ScrollInputEvent(ScrollSource.MouseWheel, ScrollGesture.Notch, 0, new Point2(150f, 100f), 0f,
                                             RowH / notch, 0, KeyModifiers.None);
            window.QueueInput(InputEvent.ForScroll(in wheel));
            host.RunFrame();
        }
        for (int i = 0; i < 30; i++) host.RunFrame();
    }

    private static void Press(AppHost host, HeadlessWindow window, int key)
    {
        window.QueueInput(new InputEvent(InputKind.Key, default, 0, key));
        window.QueueInput(new InputEvent(InputKind.KeyUp, default, 0, key));
        host.RunFrame();
    }

    private static void ClickTopRow(AppHost host, HeadlessWindow window)
    {
        window.QueueInput(new InputEvent(InputKind.PointerDown, new Point2(150f, 20f), 0, 0));
        window.QueueInput(new InputEvent(InputKind.PointerUp, new Point2(150f, 20f), 0, 0));
        host.RunFrame();
    }

    [Fact]
    public void ABoundSlotRecycledWhileFocusedDropsFocus_SoEnterAndSpaceDoNotActivateItsNewItem()
    {
        var log = new Log();
        var (app, window, host) = Build(new BoundRows(log, innerField: false));
        using (app)
        using (host)
        {
            var row = TopRow(host.Scene);
            ClickTopRow(host, window);
            Assert.Equal(row, host.Input.Focused);   // the premise: the click focused the row showing item 0
            Assert.Equal([(0, 0)], log.Hits);

            WheelAway(host, window);
            Assert.True(host.Scene.IsLive(row));
            Assert.True(ItemOf(host.Scene, row) > 0, "the focused slot should be rebound to another item, not parked");

            Press(host, window, Keys.Enter);
            Press(host, window, Keys.Space);
            Assert.Equal([(0, 0)], log.Hits);        // nothing reached the item the slot shows now
            Assert.True(host.Input.Focused.IsNull);
        }
    }

    [Fact]
    public void AFocusedFieldInsideARecycledBoundSlotLosesFocus_SoTypingDoesNotReachTheNewItem()
    {
        var log = new Log();
        var (app, window, host) = Build(new BoundRows(log, innerField: true));
        using (app)
        using (host)
        {
            var row = TopRow(host.Scene);
            var field = host.Scene.FirstChild(row);
            host.Input.SetFocus(field, visual: true);
            Press(host, window, Keys.A);
            Assert.Equal([(0, Keys.A)], log.Hits);   // the premise: keys typed into item 0's field reach it

            WheelAway(host, window);
            Assert.True(ItemOf(host.Scene, row) > 0, "the slot holding the field should be rebound to another item");

            Press(host, window, Keys.B);
            Assert.Equal([(0, Keys.A)], log.Hits);
            Assert.True(host.Input.Focused.IsNull);
            Assert.Equal(default(NodeFlags), host.Scene.Flags(field) & (NodeFlags.Focused | NodeFlags.FocusVisual));
        }
    }

    [Fact]
    public void AKeyedRowRecycledWhileFocusedDropsFocus_SoEnterDoesNotClickItsNewItem()
    {
        var log = new Log();
        var (app, window, host) = Build(new KeyedRows(log));
        using (app)
        using (host)
        {
            var row = TopRow(host.Scene);
            ClickTopRow(host, window);
            Assert.Equal(row, host.Input.Focused);
            Assert.Equal([(0, 0)], log.Hits);

            WheelAway(host, window);
            Assert.True(host.Scene.IsLive(row));
            Assert.True(ItemOf(host.Scene, row) > 0, "the focused row node should be recycled for another item, not freed");

            Press(host, window, Keys.Enter);
            Assert.Equal([(0, 0)], log.Hits);
            Assert.True(host.Input.Focused.IsNull);
        }
    }
}
