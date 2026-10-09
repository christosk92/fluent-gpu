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
/// A page's overlay scrollbar paints over its whole content, but the lane hit-test and the hover reveal only ever looked
/// at the NEAREST scroller under the pointer. A nested scroller reaching under the page's 12 DIP lane (a paged shelf
/// bleeding into the gutter, a full-width horizontal row) shadowed it: a press on the visible page thumb clicked the card
/// beneath and the page bar was told the pointer left. A suppressed bar (a paged shelf) also kept an invisible lane that
/// paged the shelf instead of clicking its cards.
/// </summary>
[Collection(SerialTestCollection.Name)]
public sealed class NestedScrollerLaneTests
{
    private const float W = 320f, H = 240f, ShelfH = 120f;

    private sealed class Root(bool suppressShelfBar) : Component
    {
        public int Clicks;

        public override Element Render() => new BoxEl
        {
            Direction = 1, Width = W, Height = H,
            Children =
            [
                new ScrollEl
                {
                    Width = W, Height = H,
                    Content = new BoxEl
                    {
                        Direction = 1, Width = W,
                        Children =
                        [
                            new ScrollEl
                            {
                                Horizontal = true, Width = W, Height = ShelfH, SuppressScrollBar = suppressShelfBar,
                                Content = new BoxEl { Width = 800f, Height = ShelfH, OnClick = () => Clicks++ },
                            },
                            new BoxEl { Width = W, Height = 2000f },
                        ],
                    },
                },
            ],
        };
    }

    private static void Run(bool suppressShelfBar, Action<AppHost, HeadlessWindow, Root, NodeHandle, NodeHandle> body)
    {
        var strings = new StringTable();
        var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("nested-lane", new Size2(320, 240), 1f));
        window.Show();
        var root = new Root(suppressShelfBar);
        var host = new AppHost(app, window, new HeadlessGpuDevice(), new HeadlessFontSystem(strings), strings, root);
        try
        {
            for (int i = 0; i < 3; i++) host.RunFrame();
            var scene = host.Scene;
            NodeHandle page = default, shelf = default;
            for (int i = 0; i < scene.Capacity; i++)
            {
                var h = scene.HandleAt(i);
                if (h.IsNull || !scene.IsLive(h) || !scene.HasScroll(h)) continue;
                if (scene.ScrollRef(h).Orientation == 1) shelf = h; else page = h;
            }
            Assert.False(page.IsNull);
            Assert.False(shelf.IsNull);
            body(host, window, root, page, shelf);
        }
        finally { host.Dispose(); app.Dispose(); }
    }

    private static void Click(AppHost host, HeadlessWindow window, Point2 p)
    {
        window.QueueInput(new InputEvent(InputKind.PointerDown, p, 0, 0));
        host.RunFrame();
        window.QueueInput(new InputEvent(InputKind.PointerUp, p, 0, 0));
        for (int i = 0; i < 60; i++) host.RunFrame();
    }

    [Fact]
    public void APressOnThePageLaneOverANestedScroller_PagesThePageInsteadOfClickingTheCard()
        => Run(false, (host, window, root, page, shelf) =>
        {
            // Inside the page's 12 DIP lane, mid-shelf (the shelf's own lane is its bottom 12), below the page thumb.
            Click(host, window, new Point2(W - 4f, 60f));
            Assert.Equal(0, root.Clicks);
            Assert.True(host.Scene.ScrollRef(page).Offset > 0.5, $"the page did not page (offset {host.Scene.ScrollRef(page).Offset})");
        });

    [Fact]
    public void HoveringThePageLaneOverANestedScroller_RevealsThePageBar()
        => Run(false, (host, window, root, page, shelf) =>
        {
            window.QueueInput(new InputEvent(InputKind.PointerMove, new Point2(W - 4f, 60f), 0, 0));
            host.RunFrame();
            var chrome = host.Scene.ScrollChrome;
            Assert.True(chrome.TryGet((int)page.Raw.Index, out var row) && row.PointerOver && row.PointerOverScrollbar);
            Assert.False(chrome.TryGet((int)shelf.Raw.Index, out var inner) && inner.PointerOver);
        });

    [Fact]
    public void APressOnASuppressedBarsLane_ClicksTheContentUnderIt()
        => Run(true, (host, window, root, page, shelf) =>
        {
            // The shelf's bottom 12 DIP, clear of the page lane, right of its would-be thumb: no bar is drawn here.
            Click(host, window, new Point2(200f, ShelfH - 4f));
            Assert.Equal(1, root.Clicks);
            Assert.True(host.Scene.ScrollRef(shelf).Offset < 0.5, $"an invisible lane paged the shelf (offset {host.Scene.ScrollRef(shelf).Offset})");
        });
}
