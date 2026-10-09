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
using FluentGpu.Scroll.Runtime;
using FluentGpu.Signals;
using FluentGpu.Text.Headless;
using Xunit;

namespace FluentGpu.Engine.Tests.Scout;

/// <summary>
/// A bound virtual list with <see cref="VirtualListEl.ContentType"/> (every Wavee track table, the sidebar, recents and
/// the library) pools its surplus rows like the default recycler: a window that shrinks parks the rows it no longer
/// needs, and the next grow takes them back instead of cold-mounting the same component rows again. A content-type
/// change at the window edge (a header entering while a track leaves) parks the leaving row too, so a second pass over
/// rows already seen mounts nothing. Serial: it constructs hosts.
/// </summary>
[Collection(SerialTestCollection.Name)]
public sealed class ContentTypeSlotPoolTests
{
    private const int Count = 300;
    private const float Extent = 40f;

    private sealed class Counters { public int Mounted, Cleaned; }

    /// <summary>One row: a mount effect whose cleanup only runs when the row's component unmounts.</summary>
    private sealed class Row(Counters c) : Component
    {
        public override Element Render()
        {
            UseEffect(() => { c.Mounted++; return () => c.Cleaned++; }, DepKey.From(0));
            return Ui.VStack(0);
        }
    }

    private sealed class Root(Counters c, Func<int, int> contentType) : Component
    {
        public override Element Render()
        {
            var list = new VirtualListEl
            {
                ItemCount = Count,
                ItemLayout = new StackVirtualLayout(Extent),
                RowBind = _ => new BoxEl { MinHeight = Extent, Children = [Embed.Comp(() => new Row(c))] },
                ContentType = contentType,
                Grow = 1f,
            };
            return new BoxEl { Width = 360f, Height = 200f, Children = [list] };
        }
    }

    private static NodeHandle FindViewport(SceneStore s)
    {
        NodeHandle found = default;
        void Visit(NodeHandle n)
        {
            if (n.IsNull) return;
            if (s.TryGetScroll(n, out var sc) && sc.ItemCount == Count) found = n;
            for (var ch = s.FirstChild(n); !ch.IsNull; ch = s.NextSibling(ch)) Visit(ch);
        }
        Visit(s.Root);
        return found;
    }

    private static void ScrollTo(AppHost host, HeadlessWindow window, NodeHandle vp, float y)
    {
        host.TryGetScrollHandle(vp)?.ScrollTo(y, ScrollMove.Immediate);
        window.QueueInput(new InputEvent(InputKind.PointerMove, new Point2(8f, 8f), 0, 0));
        for (int k = 0; k < 4; k++) host.RunFrame();
    }

    private static (AppHost Host, HeadlessWindow Window, HeadlessPlatformApp App, NodeHandle Viewport) Build(Counters c, Func<int, int> contentType)
    {
        var strings = new StringTable();
        var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("ct-pool", new Size2(360, 240), 1f));
        window.Show();
        var host = new AppHost(app, window, new HeadlessGpuDevice(), new HeadlessFontSystem(strings), strings, new Root(c, contentType));
        host.RunFrame();
        var vp = FindViewport(host.Scene);
        Assert.False(vp.IsNull);
        return (host, window, app, vp);
    }

    [Fact]
    public void AWindowThatShrinksAndGrowsBack_ReusesItsParkedRows()
    {
        var c = new Counters();
        var (host, window, app, vp) = Build(c, static _ => 0);
        using (app)
        using (host)
        {
            // Mid-list the window carries overscan on both sides; at the top the leading band is clamped away, so the
            // window shrinks by the rows that band held and grows back by the same rows on the return trip.
            ScrollTo(host, window, vp, 2000f);
            int warm = c.Mounted;

            ScrollTo(host, window, vp, 0f);
            Assert.True(host.Reconciler.SpareSlotCount(vp) > 0, "the shrink should park its surplus rows");
            Assert.Equal(0, c.Cleaned);   // a surplus row is parked, not unmounted

            ScrollTo(host, window, vp, 2000f);
            Assert.Equal(warm, c.Mounted);   // the grow takes the parked rows back: no row mounts twice
            Assert.Equal(0, c.Cleaned);
        }
    }

    [Fact]
    public void CrossingSectionHeaders_MountsNothingOnASecondPass()
    {
        var c = new Counters();
        var (host, window, app, vp) = Build(c, static i => i % 10 == 0 ? 1 : 0);   // a header row every ten tracks
        using (app)
        using (host)
        {
            for (float y = 400f; y <= 4000f; y += Extent) ScrollTo(host, window, vp, y);
            int warm = c.Mounted;

            for (float y = 400f; y <= 4000f; y += Extent) ScrollTo(host, window, vp, y);

            Assert.Equal(warm, c.Mounted);   // every header/track swap at the edge reuses a parked row of that type
            Assert.Equal(0, c.Cleaned);
        }
    }
}
