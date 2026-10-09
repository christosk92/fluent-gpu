using System;
using System.Runtime.CompilerServices;
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
using FluentGpu.Scene;
using FluentGpu.Scroll.Effects;
using FluentGpu.Text.Headless;
using Xunit;

namespace FluentGpu.Engine.Tests.Scout;

/// <summary>
/// A sticky header with Acrylic is a translation slice root whose own group layer is cut as a nested Layer slice, so the
/// root's stream is that one marker. It used to be KEPT whole whenever the marker bytes matched, but a bare acrylic's
/// marker carries no opacity, and no layer's marker carries inherited state or focus / text-edit / scroll colours, so a
/// plain (non-group) fade on an ancestor left the header's content at the alpha it was first recorded at. The keep now
/// also requires the span input signature the marker was written under.
/// </summary>
[Collection(SerialTestCollection.Name)]
public sealed class StickyLayerKeepTests
{
    private static readonly ColorF PageFill = ColorF.FromRgba(10, 10, 12);
    private static readonly ColorF FillerFill = ColorF.FromRgba(40, 40, 40);
    private static readonly ColorF MarkFill = ColorF.FromRgba(200, 40, 40);

    private sealed class Probe(bool acrylic) : Component
    {
        public override Element Render() => new BoxEl
        {
            Direction = 1, Width = 400f, Height = 600f, Fill = PageFill,
            Children =
            [
                new ScrollEl
                {
                    Height = 400f, EdgeCues = ScrollEdgeCues.None,   // no edge fade: its group would carry the page alpha
                    Content = new BoxEl
                    {
                        Direction = 1, MinWidth = 0f,
                        Children = [Header(acrylic).Sticky(0f), new BoxEl { Height = 2000f, Fill = FillerFill }],
                    },
                },
            ],
        };
    }

    private static BoxEl Header(bool acrylic) => acrylic
        ? new BoxEl { Height = 40f, Acrylic = AcrylicSpec.InAppDefault, Fill = ColorF.FromRgba(0x2C, 0x2C, 0x2C), Children = [Mark()] }
        : new BoxEl { Height = 40f, Fill = ColorF.FromRgba(0x2C, 0x2C, 0x2C), Children = [Mark()] };

    private static BoxEl Mark() => new() { Width = 20f, Height = 20f, Fill = MarkFill };

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AnAncestorFadeReachesTheContentOfAStickyHeader(bool acrylic)
    {
        var strings = new StringTable();
        using var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("sticky", new Size2(400f, 600f), 1f));
        window.Show();
        using var host = new AppHost(app, window, new HeadlessGpuDevice(), new HeadlessFontSystem(strings), strings, new Probe(acrylic));
        host.RunFrame();
        host.RunFrame();
        var scene = host.Scene;
        var page = FindFill(scene, scene.Root, PageFill);
        Assert.False(page.IsNull);

        var rec = new Sliced();
        var spans = new SpanTable();
        var dl = new DrawList();
        rec.Record(scene, dl, spans);
        Assert.Equal(1f, MarkOpacity(dl), 3);

        // a plain (non-group) opacity write on the page, exactly as AnimScheduler writes a fade frame
        scene.Paint(page).Opacity = 0.3f;
        scene.Mark(page, NodeFlags.TransformDirty | NodeFlags.PaintDirty);
        rec.Record(scene, dl, spans);
        Assert.Equal(0.3f, MarkOpacity(dl), 3);

        // an unrelated sibling repaint re-walks the scroll content; the header's inputs are unchanged, so it stays kept
        var filler = FindFill(scene, scene.Root, FillerFill);
        scene.Paint(filler).Fill = ColorF.FromRgba(41, 40, 40);
        scene.Mark(filler, NodeFlags.PaintDirty);
        rec.Record(scene, dl, spans);
        Assert.Equal(0.3f, MarkOpacity(dl), 3);
        if (acrylic) Assert.Equal(2, rec.Slices.LastStats.Kept);   // the header's root slice + its Layer slice
    }

    /// <summary>Records the way the host does (retained slice arenas, a clean slice kept whole) and copies the headless
    /// composite's single painter-ordered stream into the caller's list (VerticalSlice's SlicedRecording).</summary>
    private sealed class Sliced
    {
        public readonly SliceRecorder Slices = new();
        private readonly SliceTable _tiles = new(256, 64, 64);
        private readonly HeadlessGpuDevice _device = new();
        private readonly ISwapchain _target;

        public Sliced() => _target = _device.CreateSwapchain(new SwapchainDesc(default, new Size2(400f, 600f)));

        public void Record(SceneStore scene, DrawList dl, SpanTable spans)
        {
            dl.Reset();
            var stats = SceneRecorder.Record(scene, dl, spans: spans, slices: Slices);
            scene.ClearRecordDirty();
            var info = new FrameInfo(new Size2(400f, 600f), 1f, default, RepaintDamage: stats.RepaintDamage);
            var frame = Slices.BuildComposite(_tiles, scene.Recording.InlineSnapshot!, in info, 0, stats.RepaintDamage, withStreams: true);
            _device.SubmitComposite(in frame, _target);
            Slices.EndComposite(_tiles, frame.RasterDone);
            dl.Reset();
            var composed = _device.LastComposedStream;
            dl.AppendRaw(composed.Bytes, composed.SortKeys, composed.CommandCount, composed.OpcodeStats);
        }
    }

    private static float MarkOpacity(DrawList dl)
    {
        ReadOnlySpan<byte> bytes = dl.Bytes;
        int pos = 0;
        while (pos + sizeof(int) <= bytes.Length)
        {
            var op = (DrawOp)MemoryMarshal.Read<int>(bytes.Slice(pos, sizeof(int)));
            pos += sizeof(int);
            if (op == DrawOp.FillRoundRect)
            {
                var f = MemoryMarshal.Read<FillRoundRectCmd>(bytes.Slice(pos));
                if (f.Fill == MarkFill) return f.Opacity;
            }
            pos += op switch
            {
                DrawOp.FillRoundRect => Unsafe.SizeOf<FillRoundRectCmd>(),
                DrawOp.PushClip => Unsafe.SizeOf<ClipCmd>(),
                DrawOp.PopClip => 0,
                DrawOp.PushLayer => Unsafe.SizeOf<PushLayerCmd>(),
                DrawOp.PopLayer => Unsafe.SizeOf<PopLayerCmd>(),
                DrawOp.CompositeSlice => Unsafe.SizeOf<CompositeSliceCmd>(),
                DrawOp.DrawRoundRectStroke => Unsafe.SizeOf<DrawRoundRectStrokeCmd>(),
                DrawOp.DrawShadow => Unsafe.SizeOf<DrawShadowCmd>(),
                DrawOp.DrawGradientRect => Unsafe.SizeOf<DrawGradientRectCmd>(),
                DrawOp.DrawGradientStroke => Unsafe.SizeOf<DrawGradientStrokeCmd>(),
                DrawOp.SetBlend => Unsafe.SizeOf<SetBlendCmd>(),
                _ => throw new InvalidOperationException($"unexpected op {op}"),
            };
        }
        return float.NaN;
    }

    private static NodeHandle FindFill(SceneStore s, NodeHandle n, ColorF fill)
    {
        if (n.IsNull) return NodeHandle.Null;
        if (s.Paint(n).Fill == fill) return n;
        for (var c = s.FirstChild(n); !c.IsNull; c = s.NextSibling(c))
        {
            var r = FindFill(s, c, fill);
            if (!r.IsNull) return r;
        }
        return NodeHandle.Null;
    }
}
