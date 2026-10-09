using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Hosting;
using FluentGpu.Pal;
using FluentGpu.Pal.Headless;
using FluentGpu.Rhi.Headless;
using FluentGpu.Signals;
using FluentGpu.Text.Headless;
using Xunit;

namespace FluentGpu.Engine.Tests.Scout;

/// <summary>
/// A Stationary item-drag must survive its SOURCE being parked by a KeepAlive navigation. That navigation is exactly
/// what a spring-load does (hover a tab, it activates), and the parked source is still live, so the park edge's
/// pointer cleanup (InputDispatcher.DeactivateSubtree) found the drag's press anchor inside the outgoing page and
/// cancelled the whole gesture: the chip went home with the button still held and nothing could be dropped on the
/// page the spring-load had just opened. A source FREED mid-drag already survives (DragController.SourceRecycled).
/// </summary>
[Collection(SerialTestCollection.Name)]   // builds AppHosts; HostDispatch.Current is process-static (see SerialTestCollection)
public sealed class ParkedDragSourceTests
{
    private sealed class Root : Component
    {
        public readonly Signal<string> Page = new("a");
        public DragLift Lift = DragLift.Stationary;
        public int Drops;
        public object? Dropped;

        Element PageA() => new BoxEl
        {
            Direction = 1, Width = 320f, Height = 240f,
            Children =
            [
                new BoxEl
                {
                    Key = "src", Width = 200f, Height = 48f, CanDrag = true,
                    Draggable = new DragSource("row", static () => "payload")
                    {
                        Style = new DragVisualStyle { Lift = Lift },
                    },
                },
            ],
        };

        Element PageB() => new BoxEl
        {
            Direction = 1, Width = 320f, Height = 240f,
            Children =
            [
                new BoxEl { Width = 10f, Height = 100f },
                new BoxEl
                {
                    Key = "sink", Width = 200f, Height = 48f,
                    DropTarget = new DropTargetSpec(["row"], OnDrop: s => { Drops++; Dropped = s.Payload; }),
                },
            ],
        };

        public override Element Render() => new BoxEl
        {
            Direction = 1, Width = 320f, Height = 240f,
            Children = [Flow.KeepAlive(() => Page.Value, static k => k, k => k == "a" ? PageA() : PageB())],
        };
    }

    [Fact]
    public void AStationaryDrag_SurvivesItsSourcePageBeingParked_AndDropsOnTheNewPage()
    {
        var strings = new StringTable();
        var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("parked-drag-source", new Size2(320, 240), 1f));
        window.Show();
        var root = new Root();
        var host = new AppHost(app, window, new HeadlessGpuDevice(), new HeadlessFontSystem(strings), strings, root);
        try
        {
            for (int i = 0; i < 3; i++) host.RunFrame();

            window.QueueInput(new InputEvent(InputKind.PointerMove, new Point2(20f, 20f), 0, 0));
            host.RunFrame();
            window.QueueInput(new InputEvent(InputKind.PointerDown, new Point2(20f, 20f), 0, 0));
            host.RunFrame();
            window.QueueInput(new InputEvent(InputKind.PointerMove, new Point2(40f, 20f), 0, 0));
            host.RunFrame();
            Assert.True(host.Input.Drag.IsActive && host.Input.DragDrop.IsActive);   // promoted past the drag box

            root.Page.Value = "b";   // the spring-load navigation: page "a" (the source) is parked, still live
            for (int i = 0; i < 3; i++) host.RunFrame();
            Assert.True(host.Input.Drag.IsActive);       // the gesture is still held...
            Assert.True(host.Input.DragDrop.IsActive);   // ...and so is its L2 session

            window.QueueInput(new InputEvent(InputKind.PointerMove, new Point2(20f, 124f), 0, 0));
            host.RunFrame();
            window.QueueInput(new InputEvent(InputKind.PointerUp, new Point2(20f, 124f), 0, 0));
            host.RunFrame();

            Assert.Equal(1, root.Drops);   // the drop lands on the page the navigation opened
            Assert.Equal("payload", root.Dropped);
            Assert.False(host.Input.Drag.IsActive || host.Input.DragDrop.IsActive);
        }
        finally { host.Dispose(); app.Dispose(); }
    }

    [Fact]
    public void AGhostDrag_StillEndsWhenItsSourcePageIsParked()
    {
        // Ghost lift's visual IS the source node, which the park takes off screen: there the cancel stays correct.
        var strings = new StringTable();
        var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("parked-drag-ghost", new Size2(320, 240), 1f));
        window.Show();
        var root = new Root { Lift = DragLift.Ghost };
        var host = new AppHost(app, window, new HeadlessGpuDevice(), new HeadlessFontSystem(strings), strings, root);
        try
        {
            for (int i = 0; i < 3; i++) host.RunFrame();
            window.QueueInput(new InputEvent(InputKind.PointerDown, new Point2(20f, 20f), 0, 0));
            host.RunFrame();
            window.QueueInput(new InputEvent(InputKind.PointerMove, new Point2(40f, 20f), 0, 0));
            host.RunFrame();
            Assert.True(host.Input.Drag.IsActive);

            root.Page.Value = "b";
            for (int i = 0; i < 3; i++) host.RunFrame();
            Assert.False(host.Input.Drag.IsActive || host.Input.DragDrop.IsActive);
        }
        finally { host.Dispose(); app.Dispose(); }
    }
}
