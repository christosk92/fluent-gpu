using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Hosting;
using FluentGpu.Pal;
using FluentGpu.Pal.Headless;
using FluentGpu.Reconciler;
using FluentGpu.Render;
using FluentGpu.Render.Tiles;
using FluentGpu.Rhi;
using FluentGpu.Rhi.Headless;
using FluentGpu.Scene;
using FluentGpu.Scroll.Effects;
using FluentGpu.Scroll.Runtime;
using FluentGpu.Text.Headless;
using Xunit;

namespace FluentGpu.Engine.Tests.Scout;

/// <summary>
/// A sticky row past a bound list's persistent prefix (a group header) pins under the item band line
/// (VirtualListEl.ItemClipTopInset). Inside the scroll content slice it records into the PinnedBand slice, in the content's
/// pose-free space, while that slice's marker carries the band clip one level up at the live pose. The pinned row was
/// walked under the band line in the VIEWPORT's space: a cut row ignores it (an Infinite-based clip), but with the effect
/// budget spent the row folds inline and culls against it, so on a list whose content re-based on the window's centre
/// (below the shown offset, after a deep jump) the line sat SliceOwnDy below the real one and the pinned header vanished.
/// </summary>
[Collection(SerialTestCollection.Name)]
public sealed class PinnedBandClipTests
{
    private const float RowH = 100f, Inset = 40f, Strip = 40f;
    private const int Header = 300;
    private const double Shown = Header * RowH + 20.0;   // the header's natural top 20 above the viewport: engaged
    private static readonly ColorF RowFill = ColorF.FromRgba(40, 40, 40);
    private static readonly ColorF HeaderFill = ColorF.FromRgba(0x2C, 0x2C, 0x2C);
    private static readonly ColorF MarkFill = ColorF.FromRgba(200, 40, 40);

    private sealed class Probe : Component
    {
        public override Element Render()
        {
            var fades = new Element[SliceRecorder.EffectSliceCap];   // opacity groups that spend the effect budget first
            for (int i = 0; i < fades.Length; i++)
                fades[i] = new BoxEl
                {
                    Width = 20f, Height = 20f, Opacity = 0.5f, OpacityGroup = true,
                    Children = [new BoxEl { Width = 10f, Height = 10f, Fill = RowFill }],
                };
            return new BoxEl
            {
                Direction = 1, Width = 400f, Height = 400f + Strip,
                Children =
                [
                    new BoxEl { Direction = 0, Width = 400f, Height = Strip, Children = fades },
                    new VirtualListEl
                    {
                        ItemCount = 600, ItemLayout = new StackVirtualLayout(RowH), ScrollLineDip = RowH,
                        Width = 400f, Height = 400f, ItemClipTopInset = Inset,
                        ContentType = static i => i == Header ? 1 : 0,
                        RowBind = static index => index.Peek() == Header
                            ? new BoxEl
                            {
                                Width = 400f, Height = RowH, Fill = HeaderFill,
                                Children = [new BoxEl { Width = 20f, Height = 20f, Fill = MarkFill }],
                            }.Sticky(Inset)
                            : new BoxEl { Width = 400f, Height = RowH, Fill = RowFill },
                    },
                ],
            };
        }
    }

    [Fact]
    public void AFoldedPinnedBandRowIsCulledAgainstTheBandLineInSliceSpace()
    {
        var strings = new StringTable();
        using var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("pinned-band", new Size2(400f, 400f + Strip), 1f));
        window.Show();
        using var host = new AppHost(app, window, new HeadlessGpuDevice(), new HeadlessFontSystem(strings), strings, new Probe());
        host.RunFrame();
        host.RunFrame();
        var scene = host.Scene;
        var vp = FindViewport(scene);
        host.TryGetScrollHandle(vp)!.ScrollTo(Shown, ScrollMove.Immediate);
        for (int i = 0; i < 30 && (i < 4 || host.HasActiveWork); i++) host.RunFrame();
        ref readonly var sc = ref scene.ScrollRef(vp);
        Assert.Equal(Shown, sc.Offset);
        // The premise: the content records re-based on the window's arrange origin, below the shown offset.
        Assert.True(sc.WindowOrigin > sc.Offset, $"window origin {sc.WindowOrigin} at offset {sc.Offset}");
        var header = FindFill(scene, sc.ContentNode, HeaderFill);
        Assert.False(header.IsNull);
        Assert.True((scene.Flags(header) & NodeFlags.StickyPinned) != 0);

        var rec = new Sliced();
        var dl = new DrawList();
        rec.Record(scene, dl, new SpanTable());

        // Pinned at the band line, the header's mark spans window y 80..100: drawn, under a scissor that starts at the line.
        var (markTop, scissorTop) = MarkScissor(dl);
        Assert.False(float.IsNaN(markTop), "the pinned header's content was cut away");
        Assert.Equal(Strip + Inset, markTop, 0.5f);
        Assert.True(scissorTop <= Strip + Inset + 0.5f, $"the pinned header is scissored from y {scissorTop}, below the band line at {Strip + Inset}");
    }

    /// <summary>Records the way the host does (retained slice arenas) and copies the headless composite's single
    /// painter-ordered stream into the caller's list (VerticalSlice's SlicedRecording).</summary>
    private sealed class Sliced
    {
        public readonly SliceRecorder Slices = new();
        private readonly SliceTable _tiles = new(256, 64, 64);
        private readonly HeadlessGpuDevice _device = new();
        private readonly ISwapchain _target;

        public Sliced() => _target = _device.CreateSwapchain(new SwapchainDesc(default, new Size2(400f, 400f + Strip)));

        public void Record(SceneStore scene, DrawList dl, SpanTable spans)
        {
            dl.Reset();
            var stats = SceneRecorder.Record(scene, dl, spans: spans, slices: Slices);
            scene.ClearRecordDirty();
            var info = new FrameInfo(new Size2(400f, 400f + Strip), 1f, default, RepaintDamage: stats.RepaintDamage);
            var frame = Slices.BuildComposite(_tiles, scene.Recording.InlineSnapshot!, in info, 0, stats.RepaintDamage, withStreams: true);
            _device.SubmitComposite(in frame, _target);
            Slices.EndComposite(_tiles, frame.RasterDone);
            dl.Reset();
            var composed = _device.LastComposedStream;
            dl.AppendRaw(composed.Bytes, composed.SortKeys, composed.CommandCount, composed.OpcodeStats);
        }
    }

    // The mark's top and the top of the scissor it draws under (the intersection of the open clips), window DIP.
    private static (float MarkTop, float ScissorTop) MarkScissor(DrawList dl)
    {
        ReadOnlySpan<byte> bytes = dl.Bytes;
        Span<RectF> stack = stackalloc RectF[32];
        int depth = 0, pos = 0;
        while (pos + sizeof(int) <= bytes.Length)
        {
            var op = (DrawOp)MemoryMarshal.Read<int>(bytes.Slice(pos, sizeof(int)));
            pos += sizeof(int);
            switch (op)
            {
                case DrawOp.FillRoundRect:
                {
                    var f = MemoryMarshal.Read<FillRoundRectCmd>(bytes.Slice(pos));
                    if (f.Fill == MarkFill)
                        return (f.Transform.Transform(new Point2(f.Rect.X, f.Rect.Y)).Y, depth > 0 ? stack[depth - 1].Y : float.NegativeInfinity);
                    break;
                }
                case DrawOp.PushClip:
                {
                    var r = MemoryMarshal.Read<ClipCmd>(bytes.Slice(pos)).DeviceRect;
                    stack[depth] = depth > 0 ? r.Intersect(stack[depth - 1]) : r;
                    depth++;
                    break;
                }
                case DrawOp.PopClip:
                    depth--;
                    break;
            }
            pos += op switch
            {
                DrawOp.FillRoundRect => Unsafe.SizeOf<FillRoundRectCmd>(),
                DrawOp.PushClip => Unsafe.SizeOf<ClipCmd>(),
                DrawOp.PopClip => 0,
                DrawOp.PushLayer => Unsafe.SizeOf<PushLayerCmd>(),
                DrawOp.PopLayer => Unsafe.SizeOf<PopLayerCmd>(),
                DrawOp.CompositeSlice => Unsafe.SizeOf<CompositeSliceCmd>(),
                DrawOp.SetBlend => Unsafe.SizeOf<SetBlendCmd>(),
                _ => throw new InvalidOperationException($"unexpected op {op}"),
            };
        }
        return (float.NaN, float.NaN);
    }

    private static NodeHandle FindViewport(SceneStore scene)
    {
        for (int i = 0; i < scene.Capacity; i++)
        {
            var h = scene.HandleAt(i);
            if (!h.IsNull && scene.IsLive(h) && scene.HasScroll(h)) return h;
        }
        return default;
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
