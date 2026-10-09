using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using FluentGpu.Foundation;
using FluentGpu.Render;
using FluentGpu.Scene;
using Xunit;

namespace FluentGpu.Engine.Tests.Scout;

/// <summary>
/// A virtual list's reveal band shows its rows [first, first + count) down to top + presented while the rows after it ride
/// the band's edge. Hit testing clips the revealed rows to that edge, but a clip the recorder only CULLS with leaves a
/// half-revealed row's glyphs painted at full height, under the row sliding over the same pixels (two rows of sidebar text
/// overlapping for the whole expand). The row must record under a scissor ending at the band's edge, in the in-order pass
/// and the sticky-pinned pass alike.
/// </summary>
public sealed class DisclosureRowScissorTests
{
    private const float RowH = 40f, Top = 40f, Extent = 80f, Progress = 0.6f;
    private static readonly ColorF[] Fills =
    [
        ColorF.FromRgba(10, 20, 30), ColorF.FromRgba(40, 50, 60), ColorF.FromRgba(200, 40, 40),
        ColorF.FromRgba(40, 90, 220), ColorF.FromRgba(70, 80, 90),
    ];

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AHalfRevealedRowIsScissoredToTheRevealBandsEdge(bool pinned)
    {
        var scene = new SceneStore();
        var viewport = scene.CreateNode(1); scene.Root = viewport;
        scene.Bounds(viewport) = new RectF(0f, 0f, 100f, 200f);
        scene.SetFlagBits(viewport, NodeFlags.ClipsToBounds);
        var content = scene.CreateNode(1); scene.AppendChild(viewport, content);
        scene.Bounds(content) = new RectF(0f, 0f, 100f, 200f);
        for (int i = 0; i < Fills.Length; i++)
        {
            var row = scene.CreateNode(1); scene.AppendChild(content, row);
            scene.Bounds(row) = new RectF(0f, RowH * i, 100f, RowH);
            ref NodePaint p = ref scene.Paint(row);
            p.VisualKind = VisualKind.Box; p.Fill = Fills[i];
            if (pinned && i == 2) scene.SetFlagBits(row, NodeFlags.StickyPinned);
        }
        ref ScrollState scroll = ref scene.ScrollRef(viewport);
        scroll.Orientation = 0;
        scroll.ContentNode = content;
        scroll.ItemCount = Fills.Length;
        scroll.FirstRealized = 0;
        // rows 1..2 (content y 40..120) are revealing; at 60 % the band's edge is at y 88 and cuts row 2 (80..120), while
        // row 3 rides the edge to 88..128 over the rest of it
        Assert.True(scene.SetRevealBand(viewport, 0, first: 1, count: 2, top: Top, extent: Extent, opening: true, presented: Extent * Progress));

        var dl = new DrawList();
        SceneRecorder.Record(scene, dl);
        var (rowTop, scissorBottom) = RowScissor(dl.Bytes, Fills[2]);
        Assert.False(float.IsNaN(rowTop));   // the row is revealed, not culled
        float bandBottom = Top + Extent * Progress;
        Assert.True(scissorBottom <= bandBottom + 0.01f,
            $"the half-revealed row paints down to y {scissorBottom}, past the reveal band's edge at {bandBottom}");
    }

    // The fill's top and the bottom of the innermost scissor it draws under: what the backend applies (a PushClip
    // replaces the scissor, it does not intersect).
    private static (float Top, float ScissorBottom) RowScissor(ReadOnlySpan<byte> bytes, ColorF fill)
    {
        Span<RectF> stack = stackalloc RectF[32];
        int depth = 0, pos = 0;
        float top = float.NaN, bottom = float.PositiveInfinity;
        while (pos + sizeof(int) <= bytes.Length)
        {
            var op = (DrawOp)MemoryMarshal.Read<int>(bytes.Slice(pos, sizeof(int)));
            pos += sizeof(int);
            switch (op)
            {
                case DrawOp.FillRoundRect:
                {
                    var f = MemoryMarshal.Read<FillRoundRectCmd>(bytes.Slice(pos));
                    if (f.Fill == fill)
                    {
                        top = f.Transform.Transform(new Point2(f.Rect.X, f.Rect.Y)).Y;
                        bottom = depth > 0 ? stack[depth - 1].Bottom : float.PositiveInfinity;
                    }
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
                DrawOp.SetBlend => Unsafe.SizeOf<SetBlendCmd>(),
                DrawOp.CompositeSlice => Unsafe.SizeOf<CompositeSliceCmd>(),
                _ => throw new InvalidOperationException($"unexpected op {op}"),
            };
        }
        return (top, bottom);
    }
}
