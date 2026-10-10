using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Hosting;
using FluentGpu.Pal;
using FluentGpu.Pal.Headless;
using FluentGpu.Reconciler;
using FluentGpu.Rhi.Headless;
using FluentGpu.Scene;
using FluentGpu.Text.Headless;
using Xunit;

namespace FluentGpu.Engine.Tests.Scout;

/// <summary>
/// A natural-height measured list (Grow 0 over a MeasuredStackVirtualLayout, the ItemsView AccentPill/FullRow shape whose
/// slot root carries Margin 4,2,4,2) is as tall as its rows' margin boxes. The pre-measure refresh used to commit each
/// row's border box while arrange committed its margin box into the same table, so the viewport came out 4 DIP per row
/// short, clipped its last row and scrolled internally, every pass. Serial: it constructs hosts.
/// </summary>
[Collection(SerialTestCollection.Name)]
public sealed class NaturalListRowMarginTests
{
    private const int Count = 10;
    private const float RowH = 40f;

    private sealed class Root(IVirtualLayout layout) : Component
    {
        public override Element Render() => new BoxEl
        {
            Direction = 1,
            Children = [new VirtualListEl
            {
                ItemCount = Count, ItemLayout = layout,
                RenderItem = static _ => new BoxEl { Height = RowH, Margin = new Edges4(4f, 2f, 4f, 2f) },
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

    [Fact]
    public void ANaturalList_IsAsTallAsItsRowsMarginBoxes()
    {
        var layout = new MeasuredStackVirtualLayout(RowH);
        var strings = new StringTable();
        using var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("natural-row-margin", new Size2(600, 900), 1f));
        window.Show();
        using var host = new AppHost(app, window, new HeadlessGpuDevice(), new HeadlessFontSystem(strings), strings, new Root(layout));
        for (int i = 0; i < 6; i++) host.RunFrame();
        var vp = FindViewport(host.Scene);
        Assert.False(vp.IsNull);

        const float expected = Count * (RowH + 4f);   // 2 + 2 vertical margin per row
        ref var sc = ref host.Scene.ScrollRef(vp);
        Assert.Equal(expected, sc.ContentH);                         // arrange's margin-box extents
        Assert.Equal(expected, host.Scene.Bounds(vp).H);             // the natural viewport matches them (was 400)
        Assert.Equal(0.0, sc.Offset);
    }
}
