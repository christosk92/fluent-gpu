using System.Runtime.InteropServices;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Hosting;
using FluentGpu.Pal;
using FluentGpu.Pal.Headless;
using FluentGpu.Render;
using FluentGpu.Render.Tiles;
using FluentGpu.Rhi;
using FluentGpu.Rhi.Headless;
using FluentGpu.Signals;
using FluentGpu.Text.Headless;
using Xunit;

namespace FluentGpu.Engine.Tests.Scout;

/// <summary>
/// An opacity group folded INLINE (the effect budget spent) composites everything it encloses at its GroupAlpha,
/// including its drop shadow's halo below its box. A tile its box misses but the halo reaches rasters the shadow at the
/// group alpha, so that tile's content want must change with the alpha: the layer's footprint in the content table used
/// to be its box alone, the tile folded only the shadow (bytes unchanged inside the group), and a fade left the halo
/// frozen at its first alpha, cut off at the tile seam. Serial: it constructs a host.
/// </summary>
[Collection(SerialTestCollection.Name)]
public sealed class InlineLayerReachTests
{
    private const float W = 600f, H = 800f;

    private sealed class Probe(Signal<float> alpha) : Component
    {
        public override Element Render()
        {
            var fades = new Element[SliceRecorder.EffectSliceCap + 4];   // opacity groups that spend the effect budget first
            for (int i = 0; i < fades.Length; i++)
                fades[i] = new BoxEl
                {
                    Width = 20f, Height = 20f, Opacity = 0.5f, OpacityGroup = true,
                    Children = [new BoxEl { Width = 10f, Height = 10f, Fill = ColorF.FromRgba(40, 40, 40) }],
                };
            return new BoxEl
            {
                Direction = 1, Width = W, Height = H, Fill = ColorF.FromRgba(250, 250, 250),
                Children =
                [
                    new BoxEl { Direction = 0, Width = W, Height = 20f, Children = fades },
                    new BoxEl
                    {
                        Width = 200f, Height = 100f, Margin = new Edges4(100f, 280f, 0f, 0f),
                        Fill = ColorF.FromRgba(200, 40, 40), OpacityGroup = true, Opacity = Prop.Of(() => alpha.Value),
                        Shadow = new ShadowSpec(16f, 20f, 0f, ColorF.FromRgba(0, 0, 0, 160)),
                    },
                ],
            };
        }
    }

    [Fact]
    public void A_folded_groups_alpha_re_rasters_the_tile_only_its_shadow_halo_reaches()
    {
        var alpha = new Signal<float>(0.3f);
        var strings = new StringTable();
        using var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("inline-layer-reach", new Size2(W, H), 1f));
        window.Show();
        using var host = new AppHost(app, window, new HeadlessGpuDevice(), new HeadlessFontSystem(strings), strings, new Probe(alpha));
        for (int i = 0; i < 4; i++) host.RunFrame();
        var rec = new Sliced();

        // The premise: the group records inline, its box (and so its PushLayer) sits in one tile row and its shadow halo
        // reaches the next one.
        var first = rec.Turn(host.Scene);
        Assert.True(first.Slice >= 0, "the shadowed opacity group records inline in a tiled slice");
        Assert.True(first.ShadowTy > first.LayerTy, $"the shadow reaches tile row {first.ShadowTy}, the layer's box row {first.LayerTy}");
        Assert.Contains((first.Slice, first.ShadowTy), first.Rasters);

        alpha.Value = 0.6f;
        for (int i = 0; i < 4; i++) host.RunFrame();
        var faded = rec.Turn(host.Scene);
        Assert.Contains((first.Slice, first.LayerTy), faded.Rasters);
        Assert.Contains((first.Slice, first.ShadowTy), faded.Rasters);   // was missing: the halo stayed at 0.3

        alpha.Value = 1f;   // the group drops at alpha 1: the shadow leaves the layer with its bytes unchanged
        for (int i = 0; i < 4; i++) host.RunFrame();
        var opaque = rec.Turn(host.Scene);
        Assert.Contains((first.Slice, first.ShadowTy), opaque.Rasters);
    }

    /// <summary>Records and composites the way the host does (retained slice arenas, one persistent tile table, a headless
    /// device that completes every raster).</summary>
    private sealed class Sliced
    {
        private readonly SliceRecorder _slices = new();
        private readonly SliceTable _tiles = new(256, 64, 64);
        private readonly HeadlessGpuDevice _device = new();
        private readonly ISwapchain _target;

        public Sliced() => _target = _device.CreateSwapchain(new SwapchainDesc(default, new Size2(W, H)));

        public (int Slice, int LayerTy, int ShadowTy, List<(int, int)> Rasters) Turn(FluentGpu.Scene.SceneStore scene)
        {
            var dl = new DrawList();
            var stats = SceneRecorder.Record(scene, dl, spans: new SpanTable(), slices: _slices);
            scene.ClearRecordDirty();
            var info = new FrameInfo(new Size2(W, H), 1f, default, RepaintDamage: stats.RepaintDamage);
            var frame = _slices.BuildComposite(_tiles, scene.Recording.InlineSnapshot!, in info, 0, stats.RepaintDamage, withStreams: true);
            int slice = -1, layerTy = -1, shadowTy = -1;
            foreach (ref readonly SliceRow row in frame.Slices)
                if (TryFindGroup(frame.StreamOf(in row), out RectF layer, out RectF shadow))
                {
                    float s = row.Frame.Scale > 0f ? row.Frame.Scale : 1f;
                    slice = row.Id;
                    layerTy = (int)MathF.Floor((layer.Bottom * s - row.Frame.OriginY) / TileGrid.H);
                    shadowTy = (int)MathF.Floor((shadow.Bottom * s - row.Frame.OriginY) / TileGrid.H);
                }
            var rasters = new List<(int, int)>();
            foreach (TileRaster r in frame.Rasters) rasters.Add((r.Key.SliceId, r.Key.Ty));
            _device.SubmitComposite(in frame, _target);
            _slices.EndComposite(_tiles, in frame);
            return (slice, layerTy, shadowTy, rasters);
        }

        // The inline opacity PushLayer that encloses a DrawShadow, and the two footprints (window DIP).
        private static bool TryFindGroup(ReadOnlySpan<byte> stream, out RectF layer, out RectF shadow)
        {
            layer = shadow = default;
            bool open = false;
            int pos = 0;
            while (pos + sizeof(int) <= stream.Length)
            {
                var op = (DrawOp)MemoryMarshal.Read<int>(stream[pos..]);
                if (!RepaintStreamSafety.TryBodySize(op, out int body) || pos + sizeof(int) + body > stream.Length) break;
                ReadOnlySpan<byte> p = stream.Slice(pos + sizeof(int), body);
                if (op == DrawOp.PushLayer && MemoryMarshal.Read<PushLayerCmd>(p).Kind == (int)LayerKind.Opacity)
                    open = SliceOpBounds.TryGet(op, p, out layer);
                else if (op == DrawOp.PopLayer) open = false;
                else if (op == DrawOp.DrawShadow && open && SliceOpBounds.TryGet(op, p, out shadow)) return true;
                pos += sizeof(int) + body;
            }
            return false;
        }
    }
}
