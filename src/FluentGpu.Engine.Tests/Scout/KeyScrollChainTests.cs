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
/// Keyboard scrolling stopped at the focused node's NEAREST scroller: Down on a card in a horizontal shelf inside a
/// vertical page did nothing (the arrow does not map on the shelf's axis), and PageDown on a row of an inner list already
/// at its end was spent on the pinned list while the page stayed put. The key now walks up to the nearest scroller that
/// can move that way (ScrollRouter's keyboard contract).
/// </summary>
[Collection(SerialTestCollection.Name)]
public sealed class KeyScrollChainTests
{
    private sealed class Root(bool innerHorizontal) : Component
    {
        public override Element Render()
        {
            var items = new Element[20];
            for (int i = 0; i < items.Length; i++)
                items[i] = innerHorizontal
                    ? new BoxEl { Key = i.ToString(), Width = 100f, Height = 100f, Shrink = 0f, OnClick = () => { } }
                    : new BoxEl { Key = i.ToString(), Width = 300f, Height = 40f, Shrink = 0f, OnClick = () => { } };
            var inner = innerHorizontal
                ? Ui.ScrollView(new BoxEl { Children = items }, horizontal: true)
                : Ui.ScrollView(new BoxEl { Direction = 1, Children = items });
            return new BoxEl
            {
                Width = 320f, Height = 240f,
                Children =
                [
                    Ui.ScrollView(new BoxEl
                    {
                        Direction = 1,
                        Children =
                        [
                            new BoxEl { Width = 320f, Height = 120f, Shrink = 0f, Children = [inner] },
                            new BoxEl { Width = 320f, Height = 1000f, Shrink = 0f },
                        ],
                    }),
                ],
            };
        }
    }

    private static void Run(bool innerHorizontal, Action<AppHost, HeadlessWindow, NodeHandle, NodeHandle> body)
    {
        var strings = new StringTable();
        var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("key-scroll-chain", new Size2(320, 240), 1f));
        window.Show();
        var host = new AppHost(app, window, new HeadlessGpuDevice(), new HeadlessFontSystem(strings), strings, new Root(innerHorizontal));
        try
        {
            for (int i = 0; i < 3; i++) host.RunFrame();
            var scene = host.Scene;
            NodeHandle page = default, inner = default, item = default;
            for (int i = 0; i < scene.Capacity; i++)
            {
                var h = scene.HandleAt(i);
                if (h.IsNull || !scene.IsLive(h) || !scene.HasScroll(h)) continue;
                if (scene.ScrollRef(h).ViewportH > 200f) page = h; else inner = h;
            }
            Assert.False(page.IsNull);
            Assert.False(inner.IsNull);
            Assert.True(scene.ScrollRef(page).MaxOffset > 0.5);
            Assert.True(scene.ScrollRef(inner).MaxOffset > 0.5);
            for (int i = 0; i < scene.Capacity && item.IsNull; i++)
            {
                var h = scene.HandleAt(i);
                if (h.IsNull || !scene.IsLive(h) || (scene.Flags(h) & NodeFlags.Focusable) == 0) continue;
                for (var p = scene.Parent(h); !p.IsNull; p = scene.Parent(p))
                    if (p == inner) { item = h; break; }
            }
            Assert.False(item.IsNull);
            host.Input.SetFocus(item);
            host.RunFrame();
            body(host, window, page, inner);
        }
        finally { host.Dispose(); app.Dispose(); }
    }

    private static void Key(AppHost host, HeadlessWindow window, int vk)
    {
        window.QueueInput(new InputEvent(InputKind.Key, default, 0, vk));
        for (int i = 0; i < 120; i++) host.RunFrame();
    }

    [Fact]
    public void Down_OnACardInAHorizontalShelf_ScrollsTheEnclosingPage() => Run(innerHorizontal: true, (host, window, page, shelf) =>
    {
        var scene = host.Scene;
        Key(host, window, Keys.Down);
        Assert.True(scene.ScrollRef(page).Offset > 0.5, $"Down did not reach the page (offset {scene.ScrollRef(page).Offset})");
        Assert.True(scene.ScrollRef(shelf).Offset < 0.5, "Down moved the horizontal shelf");

        Key(host, window, Keys.Right);   // the shelf's own axis still glides the shelf
        Assert.True(scene.ScrollRef(shelf).Offset > 0.5, "Right no longer glides the shelf");
    });

    [Fact]
    public void PageDown_OnARowOfAnInnerListAtItsEnd_ChainsToTheEnclosingPage() => Run(innerHorizontal: false, (host, window, page, list) =>
    {
        var scene = host.Scene;
        Key(host, window, Keys.End);     // the inner list can move: End drives it, not the page
        Assert.True(scene.ScrollRef(list).Offset > scene.ScrollRef(list).MaxOffset - 0.5, $"End did not pin the inner list (offset {scene.ScrollRef(list).Offset})");
        Assert.True(scene.ScrollRef(page).Offset < 0.5, "End on a movable inner list moved the page");

        Key(host, window, Keys.PageDown);   // the list is pinned at its end: the page takes the key
        Assert.True(scene.ScrollRef(page).Offset > 0.5, $"PageDown was spent on the pinned inner list (page offset {scene.ScrollRef(page).Offset})");
    });
}
