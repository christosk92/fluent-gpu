using System;
using System.Collections.Generic;
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
using FluentGpu.Text;
using FluentGpu.Text.Headless;
using Xunit;

namespace FluentGpu.Engine.Tests.Scout;

/// <summary>A repaint boundary inside a <c>BoxEl.ClipPath</c> heart: while the effect budget is free it was cut as its own
/// slice, and its marker carries only rectangular / rounded clips to the composite (the tile replay skips the marker, the
/// stencil never reaches the child's item), so the red child composited as the heart's AABB square. Past
/// <see cref="SliceRecorder.EffectSliceCap"/> it folded inline inside the stencil and was a heart again. Both routes must
/// record the child's fill inside the stencil scope. Serial: it constructs hosts.</summary>
[Collection(SerialTestCollection.Name)]
public sealed class StencilClipSliceCutTests
{
    private static readonly ColorF Red = ColorF.FromRgba(230, 30, 40);
    private static readonly PathData Heart = PathDataParser.Parse(
        "M16 29 C7 21 2 15.5 2 10 A6.5 6.5 0 0 1 16 6.5 A6.5 6.5 0 0 1 30 10 C30 15.5 25 21 16 29 Z",
        PathContentEpoch.Mint(), FillRule.NonZero);

    private sealed class Probe(bool spendBudget) : Component
    {
        public override Element Render()
        {
            var kids = new List<Element>();
            if (spendBudget)
                for (int i = 0; i < SliceRecorder.EffectSliceCap; i++)
                    kids.Add(new BoxEl { Width = 8f, Height = 8f, RepaintBoundary = true, Fill = ColorF.FromRgba(30, 30, 30) });
            kids.Add(new BoxEl
            {
                Width = 200f, Height = 200f,
                ClipPath = Heart, ClipPathRule = FillRule.NonZero, ClipPathViewBoxW = 32f, ClipPathViewBoxH = 32f,
                Children = [new BoxEl { Width = 200f, Height = 200f, RepaintBoundary = true, Fill = Red }],
            });
            return new BoxEl { Width = 320f, Height = 240f, Children = [.. kids] };
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_boundary_inside_a_path_clip_records_inside_the_stencil(bool spendBudget)
    {
        var strings = new StringTable();
        using var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("stencil-cut", new Size2(320, 240), 1f));
        window.Show();
        using var host = new AppHost(app, window, new HeadlessGpuDevice(), new HeadlessFontSystem(strings), strings, new Probe(spendBudget));
        host.RunFrame();
        host.RunFrame();

        SliceRecorder slices = host.UiSlices;
        Span<(int NodeIndex, uint Gen, SliceRole Role, SliceKind Kind)> order = stackalloc (int, uint, SliceRole, SliceKind)[64];
        int n = Math.Min(slices.CopySliceOrder(order), order.Length);
        int redFills = 0, stenciledRedFills = 0;
        for (int i = 0; i < n; i++)
        {
            ReadOnlySpan<byte> bytes = slices.SliceBytes(order[i].NodeIndex, order[i].Gen, order[i].Role);
            int stencils = 0;
            int pos = 0;
            while (pos + sizeof(int) <= bytes.Length)
            {
                var op = (DrawOp)MemoryMarshal.Read<int>(bytes[pos..]);
                Assert.True(RepaintStreamSafety.TryBodySize(op, out int body));
                ReadOnlySpan<byte> payload = bytes[(pos + sizeof(int))..];
                if (op == DrawOp.PushStencilClip) stencils++;
                else if (op == DrawOp.PopStencilClip) stencils--;
                else if (op == DrawOp.FillRoundRect && MemoryMarshal.Read<FillRoundRectCmd>(payload).Fill == Red)
                {
                    redFills++;
                    if (stencils > 0) stenciledRedFills++;
                }
                pos += sizeof(int) + body;
            }
        }
        // cut or folded, the child's fill is drawn through the heart's stencil, never as its own unclipped slice
        Assert.Equal(1, redFills);
        Assert.Equal(1, stenciledRedFills);
    }
}
