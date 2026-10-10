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
/// A bound virtual list with <see cref="VirtualListEl.KeepAlive"/> parks every row that leaves the window in a keep-alive
/// bucket (detached, quiesced, still mounted). The bucket is bounded: a long scroll over keep-alive rows evicts the least
/// recently parked rows instead of keeping a mounted subtree per row it ever passed. And the parked rows die with the
/// list: they are detached, so the list's own subtree free cannot reach them, and its unmount has to free them itself.
/// Serial: it constructs hosts.
/// </summary>
[Collection(SerialTestCollection.Name)]
public sealed class VirtualKeepAliveTests
{
    private const int Count = 300;
    private const float Extent = 40f;

    private sealed class Counters { public int Mounted, Cleaned; public int Live => Mounted - Cleaned; }

    /// <summary>One row: a mount effect whose cleanup only runs when the row's component unmounts.</summary>
    private sealed class Row(Counters c) : Component
    {
        public override Element Render()
        {
            UseEffect(() => { c.Mounted++; return () => c.Cleaned++; }, DepKey.From(0));
            return Ui.VStack(0);
        }
    }

    private sealed class Root(Counters c, Signal<bool> show) : Component
    {
        public override Element Render()
        {
            var list = new VirtualListEl
            {
                ItemCount = Count,
                ItemLayout = new StackVirtualLayout(Extent),
                RowBind = _ => new BoxEl { MinHeight = Extent, Children = [Embed.Comp(() => new Row(c))] },
                KeepAlive = static _ => true,
                Grow = 1f,
            };
            return new BoxEl { Width = 360f, Height = 200f, Children = [Flow.Show(() => show.Value, list)] };
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

    /// <summary>Scroll top to bottom in steps shorter than the window, so every row is realized and then leaves it.</summary>
    private static void Sweep(AppHost host, HeadlessWindow window)
    {
        var vp = FindViewport(host.Scene);
        Assert.False(vp.IsNull);
        for (float y = 300f; y <= Count * Extent; y += 300f)
        {
            host.TryGetScrollHandle(vp)?.ScrollTo(y, ScrollMove.Immediate);
            window.QueueInput(new InputEvent(InputKind.PointerMove, new Point2(8f, 8f), 0, 0));
            for (int k = 0; k < 4; k++) host.RunFrame();
        }
    }

    private static (AppHost Host, HeadlessWindow Window, HeadlessPlatformApp App) Build(Counters c, Signal<bool> show)
    {
        var strings = new StringTable();
        var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("ka-list", new Size2(360, 240), 1f));
        window.Show();
        var host = new AppHost(app, window, new HeadlessGpuDevice(), new HeadlessFontSystem(strings), strings, new Root(c, show));
        host.RunFrame();
        return (host, window, app);
    }

    [Fact]
    public void ALongScrollOverKeepAliveRows_KeepsTheParkedBucketBounded()
    {
        var c = new Counters();
        var (host, window, app) = Build(c, new Signal<bool>(true));
        using (app)
        using (host)
        {
            Sweep(host, window);
            Assert.True(c.Mounted > 100, $"the sweep should realize most rows (mounted={c.Mounted})");
            // The realized window (~15 rows at a 200 DIP viewport with its overscan band) plus a small parked bucket;
            // never one mounted subtree per row the sweep passed.
            Assert.True(c.Live < 60, $"live rows {c.Live} (mounted={c.Mounted}, cleaned={c.Cleaned})");
        }
    }

    [Fact]
    public void UnmountingTheList_UnmountsItsParkedKeepAliveRows()
    {
        var c = new Counters();
        var show = new Signal<bool>(true);
        var (host, window, app) = Build(c, show);
        using (app)
        using (host)
        {
            Sweep(host, window);
            Assert.True(c.Cleaned < c.Mounted);   // rows are parked or realized, so some are still mounted

            show.Value = false;
            for (int k = 0; k < 4; k++) host.RunFrame();

            Assert.Equal(c.Mounted, c.Cleaned);   // every row component, parked or realized, ran its cleanup
        }
    }
}
