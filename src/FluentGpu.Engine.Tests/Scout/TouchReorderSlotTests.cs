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
/// A touch drag-reorder of a row inside a scroller must give its contact back when the finger lifts. The down over the
/// scroller records it as the contact's pan candidate, and the reorder claim used to leave that candidate in place, so
/// the lifted contact kept its capture slot (and its gesture arena) forever: ten reorders with fresh pointer ids filled
/// the 10-seat table and every later touch was dropped, and a reused id carried the stale scroller into its next
/// gesture, so a drag over inert chrome scrolled the list.
/// </summary>
[Collection(SerialTestCollection.Name)]   // builds AppHosts; HostDispatch.Current is process-static (see SerialTestCollection)
public sealed class TouchReorderSlotTests
{
    private sealed class Root : Component
    {
        public int Completed;

        public override Element Render() => new BoxEl
        {
            Direction = 0, Width = 520f, Height = 300f,
            Children =
            [
                Ui.ScrollView(new BoxEl
                {
                    Direction = 1,
                    Children =
                    [
                        // Direction=0 strip ⇒ a row's reorder axis is X, across the scroller's Y pan axis.
                        new BoxEl
                        {
                            Direction = 0, Gap = 8, Height = 80,
                            Children =
                            [
                                new BoxEl { Key = "a", Width = 120, Height = 80, CanDrag = true, OnDragCompleted = _ => Completed++ },
                                new BoxEl { Key = "b", Width = 120, Height = 80, CanDrag = true },
                            ],
                        },
                        new BoxEl { Width = 300, Height = 1600f },   // tall filler ⇒ vertical overflow
                    ],
                }) with { Width = 320f, Height = 300f, Grow = 0f },
                new BoxEl { Width = 200f, Height = 300f },   // inert chrome beside the list: no handlers, no scroller
            ],
        };
    }

    private uint _ms = 1000;

    private void Touch(HeadlessWindow window, AppHost host, InputKind kind, uint id, float x, float y)
    {
        _ms += 16;
        window.QueueInput(new InputEvent(kind, new Point2(x, y), 0, 0, KeyModifiers.None, PointerKind.Touch, false, _ms, id, 1f));
        host.RunFrame();
    }

    private void Gesture(HeadlessWindow window, AppHost host, uint id, Point2 from, Point2 to)
    {
        Touch(window, host, InputKind.PointerDown, id, from.X, from.Y);
        for (int i = 1; i <= 8; i++)
            Touch(window, host, InputKind.PointerMove, id, from.X + (to.X - from.X) * i / 8f, from.Y + (to.Y - from.Y) * i / 8f);
        Touch(window, host, InputKind.PointerUp, id, to.X, to.Y);
        for (int i = 0; i < 30; i++) host.RunFrame();   // let the drop glide settle before the next gesture
    }

    private static NodeHandle FindScrollable(SceneStore s, NodeHandle n)
    {
        if ((s.Flags(n) & NodeFlags.Scrollable) != 0) return n;
        for (var c = s.FirstChild(n); !c.IsNull; c = s.NextSibling(c))
        {
            var r = FindScrollable(s, c);
            if (!r.IsNull) return r;
        }
        return NodeHandle.Null;
    }

    [Fact]
    public void ElevenReorders_WithFreshPointerIds_AllComplete()
    {
        var strings = new StringTable();
        var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("touch-reorder-slots", new Size2(520, 300), 1f));
        window.Show();
        var root = new Root();
        var host = new AppHost(app, window, new HeadlessGpuDevice(), new HeadlessFontSystem(strings), strings, root);
        try
        {
            for (int i = 0; i < 3; i++) host.RunFrame();
            for (uint id = 100; id < 111; id++)   // one more reorder than the contact table has seats
                Gesture(window, host, id, new Point2(60f, 40f), new Point2(200f, 40f));
            Assert.Equal(11, root.Completed);
        }
        finally { host.Dispose(); app.Dispose(); }
    }

    [Fact]
    public void AReusedPointerId_DoesNotPanTheOldScroller_FromInertChrome()
    {
        var strings = new StringTable();
        var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("touch-reorder-reuse", new Size2(520, 300), 1f));
        window.Show();
        var root = new Root();
        var host = new AppHost(app, window, new HeadlessGpuDevice(), new HeadlessFontSystem(strings), strings, root);
        try
        {
            for (int i = 0; i < 3; i++) host.RunFrame();
            var scroller = FindScrollable(host.Scene, host.Scene.Root);
            Gesture(window, host, 31, new Point2(60f, 40f), new Point2(200f, 40f));
            Assert.Equal(1, root.Completed);
            host.Scene.TryGetScroll(scroller, out var before);

            Gesture(window, host, 31, new Point2(420f, 260f), new Point2(420f, 60f));   // the same id drags the inert chrome
            host.Scene.TryGetScroll(scroller, out var after);
            Assert.Equal(before.OffsetY, after.OffsetY);
        }
        finally { host.Dispose(); app.Dispose(); }
    }
}
