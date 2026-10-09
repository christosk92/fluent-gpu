using System;
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
using FluentGpu.Scroll.Runtime;
using FluentGpu.Signals;
using FluentGpu.Text.Headless;
using Xunit;

namespace FluentGpu.Engine.Tests.Scout;

/// <summary>
/// A page whose mount layout effect jumps its (RenderItem) virtual list deep into the content windows the new rows in
/// the host's mid-frame pass, after the 6.5 layout-effect drain. Those rows queue their own layout effects (entrance /
/// transition seeds, measured-size registration, gesture installs) and must run them before they paint, like every other
/// mount; left to the next frame they present once unseeded (an entrance shows at full opacity, then snaps to 0 and replays).
/// </summary>
[Collection(SerialTestCollection.Name)]
public sealed class MidFrameRealizeLayoutEffectsTests
{
    private const int N = 1000;
    private const float RowH = 48f;
    private const int Target = 600;

    private sealed class Root(Signal<bool> show, HashSet<int> ran) : Component
    {
        public override Element Render()
            => show.Value ? Embed.Comp(() => new Page(ran)) : new BoxEl { Width = 320f, Height = 480f };
    }

    private sealed class Page(HashSet<int> ran) : Component
    {
        public override Element Render()
        {
            var handle = UseMemo(static () => new ScrollHandle(), DepKey.Empty);
            UseLayoutEffect(() => handle.ScrollTo(Target * RowH, ScrollMove.Immediate), DepKey.Empty);
            return new VirtualListEl
            {
                ItemCount = N, EstimatedExtent = RowH, Handle = handle,
                RenderItem = i => Embed.Comp(() => new Row(i, ran)),
                Width = 320f, Height = 480f, Grow = 1f,
            };
        }
    }

    private sealed class Row(int index, HashSet<int> ran) : Component
    {
        public override Element Render()
        {
            UseLayoutEffect(() => { ran.Add(index); }, DepKey.Empty);
            return new BoxEl { Height = RowH };
        }
    }

    /// <summary>The first scroll viewport under <paramref name="node"/> (depth-first), or a null handle when there is none.</summary>
    private static NodeHandle FindScrollViewport(SceneStore scene, NodeHandle node)
    {
        if (scene.HasScroll(node)) return node;
        for (var child = scene.FirstChild(node); !child.IsNull; child = scene.NextSibling(child))
        {
            var found = FindScrollViewport(scene, child);
            if (!found.IsNull) return found;
        }
        return default;
    }

    [Fact]
    public void RowsRealizedByALayoutEffectScrollRunTheirLayoutEffectsBeforeTheyPaint()
    {
        var ran = new HashSet<int>();
        var show = new Signal<bool>(false);
        var strings = new StringTable();
        var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("midframe-layout-effects", new Size2(320, 480), 1f));
        window.Show();
        var host = new AppHost(app, window, new HeadlessGpuDevice(), new HeadlessFontSystem(strings), strings, new Root(show, ran));
        try
        {
            for (int i = 0; i < 20 && (i < 3 || host.HasActiveWork); i++) host.RunFrame();

            show.Value = true;   // open the page: its layout effect jumps the list to row 600
            host.RunFrame();

            var viewport = FindScrollViewport(host.Scene, host.Scene.Root);
            Assert.False(viewport.IsNull);
            Assert.Equal(Target * (double)RowH, host.Scene.ScrollRef(viewport).Offset);   // the jump showed this frame
            Assert.Contains(Target, ran);   // and the rows it windowed in ran their layout effects in the same frame
        }
        finally { host.Dispose(); }
    }
}
