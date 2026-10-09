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
using FluentGpu.Rhi.Headless;
using FluentGpu.Scene;
using FluentGpu.Scroll.Runtime;
using FluentGpu.Text.Headless;
using Xunit;

namespace FluentGpu.Engine.Tests.Scout;

/// <summary>
/// A row removed from a list with a sticky item band (ScrollState.ItemClipTopInset) animates out as an exit orphan of the
/// scroll content, recorded INLINE in the content slice under the band's scissor. The content slice records in its
/// pose-free space, so the band line the exiting row is walked under must be the one offset into that space: a clipping
/// row builds its own scissor from it, and that push REPLACES the band's. Walked with the viewport-space line, a list
/// scrolled down let the exiting row's content paint above the band, over the sticky header.
/// </summary>
[Collection(SerialTestCollection.Name)]
public sealed class ExitingRowBandClipTests
{
    private const float RowH = 100f, Inset = 40f;
    private const double Scrolled = 250.0;
    private static readonly ColorF RowFill = ColorF.FromRgba(40, 40, 40);
    private static readonly ColorF ExitFill = ColorF.FromRgba(40, 90, 220);
    private static readonly ColorF MarkFill = ColorF.FromRgba(200, 40, 40);

    private sealed class Probe : Component
    {
        public override Element Render()
        {
            var rows = new Element[20];
            for (int i = 0; i < rows.Length; i++)
                rows[i] = i == 2
                    ? new BoxEl
                    {
                        Direction = 1, Height = RowH, Fill = ExitFill, ClipToBounds = true,
                        Children = [new BoxEl { Width = 200f, Height = RowH, Fill = MarkFill }],
                    }
                    : new BoxEl { Height = RowH, Fill = RowFill };
            return new BoxEl
            {
                Direction = 1, Width = 400f, Height = 600f,
                Children =
                [
                    new ScrollEl
                    {
                        Height = 400f, SuppressScrollBar = true, EdgeCues = ScrollEdgeCues.None,
                        Content = new BoxEl { Direction = 1, MinWidth = 0f, Children = rows },
                    },
                ],
            };
        }
    }

    [Fact]
    public void AnExitingRowsOwnClipStaysBelowTheBandLineInTheContentSlice()
    {
        var strings = new StringTable();
        using var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("exit-band", new Size2(400f, 600f), 1f));
        window.Show();
        using var host = new AppHost(app, window, new HeadlessGpuDevice(), new HeadlessFontSystem(strings), strings, new Probe());
        for (int i = 0; i < 3; i++) host.RunFrame();
        var scene = host.Scene;
        var vp = FindViewport(scene);
        Assert.False(vp.IsNull);
        host.TryGetScrollHandle(vp)!.ScrollTo(Scrolled, ScrollMove.Immediate);
        for (int i = 0; i < 30 && (i < 2 || host.HasActiveWork); i++) host.RunFrame();
        Assert.Equal(Scrolled, scene.ScrollRef(vp).Offset);

        // the sticky-header inset ListOptions.ItemClipTopInset publishes, and the row 2 removal BeginVirtualRemoval orphans
        scene.ScrollRef(vp).ItemClipTopInset = Inset;
        var content = scene.ScrollRef(vp).ContentNode;
        var row = FindFill(scene, content, ExitFill);
        Assert.False(row.IsNull);
        scene.Orphan(row);

        var slices = new SliceRecorder();
        SceneRecorder.Record(scene, new DrawList(), spans: new SpanTable(), slices: slices);
        ReadOnlySpan<byte> bytes = slices.SliceBytes((int)content.Raw.Index, content.Raw.Gen, SliceRole.Main);
        Assert.False(bytes.IsEmpty);

        // The row sits at content y 200..300; scrolled 250, the band line (viewport top + 40) crosses it at content y 290,
        // 90 below its top. The slice records at the content's offset-0 pose, so that is 90 below the row's recorded top.
        var (rowTop, markScissorTop) = ExitScissor(bytes);
        Assert.False(float.IsNaN(rowTop));
        Assert.False(float.IsNaN(markScissorTop));
        float line = rowTop + (float)Scrolled + Inset - 2f * RowH;
        Assert.True(markScissorTop >= line - 0.5f,
            $"the exiting row's content is scissored from y {markScissorTop}, above the band line at {line}");
    }

    // The exiting row's own fill top and the top of the scissor its content (MarkFill) draws under — the innermost push,
    // which is what the backend applies (a PushClip replaces the scissor, it does not intersect).
    private static (float RowTop, float ScissorTop) ExitScissor(ReadOnlySpan<byte> bytes)
    {
        Span<RectF> stack = stackalloc RectF[32];
        int depth = 0, pos = 0;
        float rowTop = float.NaN, scissorTop = float.NaN;
        while (pos + sizeof(int) <= bytes.Length)
        {
            var op = (DrawOp)MemoryMarshal.Read<int>(bytes.Slice(pos, sizeof(int)));
            pos += sizeof(int);
            switch (op)
            {
                case DrawOp.FillRoundRect:
                {
                    var f = MemoryMarshal.Read<FillRoundRectCmd>(bytes.Slice(pos));
                    float top = f.Transform.Transform(new Point2(f.Rect.X, f.Rect.Y)).Y;
                    if (f.Fill == ExitFill) rowTop = top;
                    else if (f.Fill == MarkFill && depth > 0) scissorTop = stack[depth - 1].Y;
                    break;
                }
                case DrawOp.PushClip:
                    stack[depth++] = MemoryMarshal.Read<ClipCmd>(bytes.Slice(pos)).DeviceRect;
                    break;
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
                _ => throw new InvalidOperationException($"unexpected op {op}"),
            };
        }
        return (rowTop, scissorTop);
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
