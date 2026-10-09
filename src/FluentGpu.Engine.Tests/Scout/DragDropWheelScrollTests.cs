using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Hosting;
using FluentGpu.Pal;
using FluentGpu.Pal.Headless;
using FluentGpu.Rhi.Headless;
using FluentGpu.Scene;
using FluentGpu.Text.Headless;
using Xunit;

namespace FluentGpu.Engine.Tests.Scout;

/// <summary>
/// A wheel (or touchpad) scroll under a STATIONARY pointer during a live drag-drop session moves the list's rows out
/// from under it, and nothing re-resolved the drop target: <c>DragDropContext.Move</c> ran only on a real PointerMove or
/// for edge auto-scroll, and the hover refresh a scroll offset write triggers stands down while an item-drag holds the
/// pointer. Releasing without moving dropped onto the row that WAS under the pointer before the scroll.
/// </summary>
[Collection(SerialTestCollection.Name)]
public sealed class DragDropWheelScrollTests
{
    private const float RowH = 48f;
    private const int Rows = 60;

    private sealed class Root : Component
    {
        public int Dropped = -1;
        public int LastOver = -1;

        Element Row(int i) => new BoxEl
        {
            Key = i.ToString(), Height = RowH, Width = 200f,
            DropTarget = new DropTargetSpec(["row"], OnOver: _ => LastOver = i, OnDrop: _ => Dropped = i),
        };

        public override Element Render()
        {
            var rows = new Element[Rows];
            for (int i = 0; i < Rows; i++) rows[i] = Row(i);
            return new BoxEl
            {
                Direction = 0, Width = 320f, Height = 240f,
                Children =
                [
                    new BoxEl
                    {
                        Key = "src", Width = 100f, Height = 48f, CanDrag = true,
                        Draggable = new DragSource("row", static () => "payload"),
                    },
                    new BoxEl
                    {
                        Width = 200f, Height = 240f, Direction = 1,
                        Children = [Ui.ScrollView(new BoxEl { Direction = 1, Children = rows })],
                    },
                ],
            };
        }
    }

    [Fact]
    public void AWheelScrollUnderAStationaryDrag_RetargetsTheDropToTheRowNowUnderThePointer()
    {
        var strings = new StringTable();
        var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("drag-wheel-scroll", new Size2(320, 240), 1f));
        window.Show();
        var root = new Root();
        var host = new AppHost(app, window, new HeadlessGpuDevice(), new HeadlessFontSystem(strings), strings, root);
        try
        {
            for (int i = 0; i < 3; i++) host.RunFrame();
            var scene = host.Scene;
            NodeHandle vp = default;
            for (int i = 0; i < scene.Capacity && vp.IsNull; i++)
            {
                var h = scene.HandleAt(i);
                if (!h.IsNull && scene.IsLive(h) && scene.HasScroll(h)) vp = h;
            }
            Assert.False(vp.IsNull);

            // Press the source, promote past the drag box, then park the pointer mid-list (outside both 100px edge
            // zones of the 240px viewport, so edge auto-scroll never arms).
            var over = new Point2(200f, 120f);
            window.QueueInput(new InputEvent(InputKind.PointerMove, new Point2(20f, 20f), 0, 0));
            host.RunFrame();
            window.QueueInput(new InputEvent(InputKind.PointerDown, new Point2(20f, 20f), 0, 0));
            host.RunFrame();
            window.QueueInput(new InputEvent(InputKind.PointerMove, new Point2(40f, 20f), 0, 0));
            host.RunFrame();
            window.QueueInput(new InputEvent(InputKind.PointerMove, over, 0, 0));
            host.RunFrame();
            Assert.True(host.Input.DragDrop.IsActive);
            Assert.Equal(2, root.LastOver);   // 120 / 48 -> row 2

            // Wheel the list under the still pointer and let the glide settle.
            window.SendWheelNotch(over, 3f);
            for (int i = 0; i < 120; i++) host.RunFrame();
            double offset = scene.ScrollRef(vp).Offset;
            int expected = (int)((offset + over.Y) / RowH);
            Assert.True(expected > 2, $"the wheel did not scroll past row 2 (offset {offset})");

            Assert.Equal(expected, root.LastOver);   // the insertion cue followed the content

            window.QueueInput(new InputEvent(InputKind.PointerUp, over, 0, 0));
            host.RunFrame();
            Assert.Equal(expected, root.Dropped);    // and the release drops on the row now under the pointer
        }
        finally { host.Dispose(); app.Dispose(); }
    }
}
