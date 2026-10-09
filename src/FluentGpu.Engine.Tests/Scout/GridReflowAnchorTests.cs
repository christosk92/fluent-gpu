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
/// A responsive grid (RepeatLayout.GridFit) scrolled deep keeps the items the user was looking at when a resize changes
/// its column count. The width change re-maps every item to a new row; the viewport used to keep its pixel offset, so row
/// 100 showed items 600-605 instead of 500-504 after a widen (about 17 rows of content skipped), and the reverse on a
/// narrow. Serial: it constructs hosts.
/// </summary>
[Collection(SerialTestCollection.Name)]
public sealed class GridReflowAnchorTests
{
    private const int Count = 2000;
    private const float Row = 250f;   // every card measures one row; the estimate matches, so offsets are exact
    private const int Anchor = 500;   // row 100 at 5 columns

    private sealed class Root(Signal<float> width, IVirtualLayout layout) : Component
    {
        public override Element Render() => new BoxEl
        {
            Direction = 1,
            Children = [new VirtualListEl
            {
                ItemCount = Count, ItemLayout = layout, RenderItem = static _ => new BoxEl { Height = Row },
                Width = width.Value, Height = 600f,
            }],
        };
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

    /// <summary>The first item of the row on screen at the top of the viewport.</summary>
    private static int TopItem(AppHost host, NodeHandle vp)
    {
        ref var sc = ref host.Scene.ScrollRef(vp);
        return sc.Extent!.IndexAt(sc.Offset);
    }

    [Theory]
    [InlineData(1000f, 1100f)]   // 5 → 6 columns
    [InlineData(1100f, 1000f)]   // 6 → 5 columns
    public void AColumnCountChange_KeepsTheAnchorItemInTheTopRow(float from, float to)
    {
        var width = new Signal<float>(from);
        var layout = new GridVirtualLayout(0, 0f, gap: 0f, minCellWidth: 180f, estimate: Row);
        var strings = new StringTable();
        using var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("grid-reflow", new Size2(1400, 700), 1f));
        window.Show();
        using var host = new AppHost(app, window, new HeadlessGpuDevice(), new HeadlessFontSystem(strings), strings, new Root(width, layout));
        for (int i = 0; i < 4; i++) host.RunFrame();
        var vp = FindViewport(host.Scene);
        Assert.False(vp.IsNull);

        int cols = layout.EffectiveColumns(from);
        host.TryGetScrollHandle(vp)!.ScrollTo(Anchor / cols * Row, ScrollMove.Immediate);
        for (int i = 0; i < 4; i++) host.RunFrame();
        int before = TopItem(host, vp);
        Assert.Equal(Anchor / cols * cols, before);

        width.Value = to;
        for (int i = 0; i < 4; i++) host.RunFrame();
        int newCols = layout.EffectiveColumns(to);
        Assert.NotEqual(cols, newCols);
        int top = TopItem(host, vp);
        Assert.Equal(before / newCols * newCols, top);   // the row that now holds the old top item, not the old row index
    }

    [Fact]
    public void AGridAtTheTop_StaysAtTheTop()
    {
        var width = new Signal<float>(1000f);
        var layout = new GridVirtualLayout(0, 0f, gap: 0f, minCellWidth: 180f, estimate: Row);
        var strings = new StringTable();
        using var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("grid-reflow-top", new Size2(1400, 700), 1f));
        window.Show();
        using var host = new AppHost(app, window, new HeadlessGpuDevice(), new HeadlessFontSystem(strings), strings, new Root(width, layout));
        for (int i = 0; i < 4; i++) host.RunFrame();
        var vp = FindViewport(host.Scene);
        width.Value = 1100f;
        for (int i = 0; i < 4; i++) host.RunFrame();
        Assert.Equal(0.0, host.Scene.ScrollRef(vp).Offset);
    }
}
