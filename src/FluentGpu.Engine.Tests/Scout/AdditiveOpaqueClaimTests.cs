using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Hosting;
using FluentGpu.Layout;
using FluentGpu.Pal;
using FluentGpu.Pal.Headless;
using FluentGpu.Render;
using FluentGpu.Render.Tiles;
using FluentGpu.Rhi;
using FluentGpu.Rhi.Headless;
using FluentGpu.Text;
using FluentGpu.Text.Headless;
using Xunit;

namespace FluentGpu.Engine.Tests.Scout;

/// <summary>A full-window plate in its own repaint boundary over a page box. Drawn SrcOver, its opaque square fill claims
/// <see cref="CompositeItem.Opaque"/> over the window, so the backend may skip the page under it. Drawn under the Additive
/// paint blend (a dark-theme glow plate), the fill leaves the transparent tile's alpha at 0 and composites as page + glow:
/// it must claim no cover, or occlusion drops the page the glow is meant to light. Serial: it constructs hosts.</summary>
[Collection(SerialTestCollection.Name)]
public sealed class AdditiveOpaqueClaimTests
{
    private sealed class Probe(PaintBlend blend) : Component
    {
        public override Element Render()
        {
            var page = new BoxEl
            {
                AlignSelf = FlexAlign.Start, JustifySelf = FlexAlign.Start, Margin = new Edges4(40f, 40f, 0f, 0f),
                Width = 300f, Height = 200f, Fill = ColorF.FromRgba(200, 90, 60),
            };
            var overlay = new BoxEl
            {
                AlignSelf = FlexAlign.Stretch, JustifySelf = FlexAlign.Stretch, ZStack = true, RepaintBoundary = true,
                Children = [new BoxEl { Grow = 1f, AlignSelf = FlexAlign.Stretch, JustifySelf = FlexAlign.Stretch, Blend = blend, Fill = ColorF.FromRgba(20, 22, 28) }],
            };
            return new BoxEl { Grow = 1f, ZStack = true, Children = [page, overlay] };
        }
    }

    private static RectF OverlayOpaque(PaintBlend blend)
    {
        var strings = new StringTable();
        using var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("additive-cover", new Size2(1200, 1000), 1f));
        window.Show();
        var dev = new HeadlessGpuDevice();
        using var host = new AppHost(app, window, dev, new HeadlessFontSystem(strings), strings, new Probe(blend));
        for (int i = 0; i < 20; i++) host.RunFrame();
        int effect = -1;
        foreach (var row in dev.LastCompositeSlices) if (row.Kind == SliceKind.Effect) effect = row.Id;
        Assert.True(effect >= 0, "the overlay composites as its own effect slice");
        bool found = false;
        RectF opaque = default;
        foreach (var r in dev.LastCompositeRecords)
            if (r.Kind == CompositeRecordKind.DrawItem && r.Item.SliceId == effect) { opaque = r.Item.Opaque; found = true; }
        Assert.True(found, "the overlay's item was composited");
        return opaque;
    }

    [Fact]
    public void SrcOver_plate_claims_the_window()
    {
        var o = OverlayOpaque(PaintBlend.SrcOver);
        Assert.True(o.X <= 0f && o.Y <= 0f && o.Right >= 1200f && o.Bottom >= 1000f, $"opaque={o}");
    }

    [Fact]
    public void Additive_plate_claims_no_cover()
    {
        var o = OverlayOpaque(PaintBlend.Additive);
        Assert.True(o.W <= 0f || o.H <= 0f, $"an additive plate leaves alpha 0 and must not occlude what is beneath it: opaque={o}");
    }
}
