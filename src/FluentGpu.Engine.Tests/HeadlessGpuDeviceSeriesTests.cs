using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using FluentGpu.Foundation;
using FluentGpu.Render;
using FluentGpu.Rhi;
using FluentGpu.Rhi.Headless;
using Xunit;

namespace FluentGpu.Engine.Tests;

/// <summary>The headless half of WP-E3 (fullscreen-flagship-implementation.md §4.3/§4.4/§5.1): the chunked
/// <see cref="DrawSeriesCmd"/> contract through the real <see cref="DrawList"/> writer and the headless device's decode
/// (<see cref="HeadlessGpuDevice.LastSeries"/>), the opcode framing every stream walker shares, the slice translation, and
/// the <see cref="ISwapchain.IsOccluded"/> seam the host publishes as <c>InputHooks.WindowOccluded</c>.</summary>
public sealed class HeadlessGpuDeviceSeriesTests
{
    private static readonly ColorF White = ColorF.FromRgba(255, 255, 255);

    private static float[] Ramp(int n)
    {
        var a = new float[n];
        for (int i = 0; i < n; i++) a[i] = (float)i / (n - 1);
        return a;
    }

    private static HeadlessGpuDevice Decode(DrawList dl)
    {
        var dev = new HeadlessGpuDevice();
        dev.SubmitDrawList(dl.Bytes, dl.SortKeys, new FrameInfo(new Size2(400f, 200f), 1f, ColorF.Transparent));
        return dev;
    }

    private static SeriesSpec Spec(SeriesShape shape = SeriesShape.Mirrored, float thickness = 2f)
        => new(shape, White, null, thickness, float.NaN, 1f, 1f);

    [Fact]
    public void DrawSeriesCmd_IsUnmanagedAndUnder1024Bytes()
    {
        Assert.False(RuntimeHelpers.IsReferenceOrContainsReferences<DrawSeriesCmd>());
        Assert.True(Unsafe.SizeOf<DrawSeriesCmd>() <= 1024, $"DrawSeriesCmd is {Unsafe.SizeOf<DrawSeriesCmd>()} B; the headless translate buffer caps a payload at 1024 B");
    }

    [Theory]
    [InlineData(2, 1)]
    [InlineData(32, 1)]
    [InlineData(33, 2)]
    [InlineData(65, 3)]
    [InlineData(181, 6)]
    [InlineData(512, 17)]
    public void Series_Writer_ChunksAsCeilNMinus1Over31(int n, int chunks)
    {
        var dl = new DrawList();
        dl.Series(new RectF(0f, 0f, 310f, 100f), Spec(), Ramp(n), Affine2D.Identity, 1f);
        var dev = Decode(dl);

        Assert.Equal(chunks, dev.LastSeries.Count);
        Assert.Equal((n - 1 + 30) / 31, chunks);   // the formula the plan states, checked against the table above
        for (int c = 0; c < dev.LastSeries.Count; c++)
        {
            var cmd = dev.LastSeries[c];
            Assert.Equal(c * 31, cmd.Index);
            Assert.Equal(n, cmd.Total);
            Assert.InRange(cmd.Count, 2, SeriesSpec.ChunkSamples);
            if (c > 0)
            {
                var prev = dev.LastSeries[c - 1];
                Assert.Equal(prev.S[prev.Count - 1], cmd.S[0]);   // the shared edge sample (locals: an inline array cannot be indexed on an rvalue)
            }
        }
    }

    [Fact]
    public void Series_Writer_DropsSamplesPastTheCap()
    {
        var dl = new DrawList();
        dl.Series(new RectF(0f, 0f, 310f, 100f), Spec(), Ramp(SeriesSpec.MaxSamples + 88), Affine2D.Identity, 1f);
        var dev = Decode(dl);
        Assert.Equal(17, dev.LastSeries.Count);
        Assert.All(dev.LastSeries, c => Assert.Equal(SeriesSpec.MaxSamples, c.Total));
    }

    [Fact]
    public void Series_Writer_ShapeBaselinesAndDegenerateInputs()
    {
        var rect = new RectF(0f, 0f, 64f, 40f);
        var dl = new DrawList();
        dl.Series(rect, Spec(SeriesShape.Baseline), Ramp(4), Affine2D.Identity, 1f);
        dl.Series(rect, Spec(SeriesShape.Mirrored), Ramp(4), Affine2D.Identity, 1f);
        dl.Series(rect, Spec(SeriesShape.Stroke, 3f), Ramp(4), Affine2D.Identity, 1f);
        dl.Series(rect, Spec(), [0.5f], Affine2D.Identity, 1f);                       // one sample paints nothing
        dl.Series(rect, Spec(), [], Affine2D.Identity, 1f);                           // neither does none
        dl.Series(new RectF(0f, 0f, 0f, 40f), Spec(), Ramp(4), Affine2D.Identity, 1f); // nor a zero-width box
        var dev = Decode(dl);

        Assert.Equal(3, dev.LastSeries.Count);
        Assert.Equal(1f, dev.LastSeries[0].Baseline);     // Baseline: the bottom edge
        Assert.Equal(0.5f, dev.LastSeries[1].Baseline);   // Mirrored: the middle
        Assert.Equal(1f, dev.LastSeries[2].Baseline);
        Assert.Equal((int)SeriesShape.Stroke, dev.LastSeries[2].Shape);
        Assert.Equal(3f, dev.LastSeries[2].Thickness);
        Assert.Equal(64f / 3f, dev.LastSeries[0].Dx);
    }

    [Fact]
    public void Series_Writer_CarriesTheGradientStopsByAmplitude()
    {
        var a = ColorF.FromRgba(10, 20, 30);
        var b = ColorF.FromRgba(40, 50, 60);
        var spec = new SeriesSpec(SeriesShape.Baseline, White,
            new GradientSpec(GradientShape.Linear, 0f, [new GradientStop(0f, a), new GradientStop(0.7f, b)]), 2f, float.NaN, 1f, 1f);
        var dl = new DrawList();
        dl.Series(new RectF(0f, 0f, 64f, 40f), in spec, Ramp(4), Affine2D.Identity, 1f);
        var cmd = Decode(dl).LastSeries[0];
        Assert.Equal(2, cmd.StopCount);
        Assert.Equal(a, cmd.C0);
        Assert.Equal(b, cmd.C1);
        Assert.Equal(0f, cmd.O0);
        Assert.Equal(0.7f, cmd.O1);
    }

    [Fact]
    public void TryBodySize_KnowsDrawSeries()
    {
        Assert.True(RepaintStreamSafety.TryBodySize(DrawOp.DrawSeries, out int body));
        Assert.Equal(Unsafe.SizeOf<DrawSeriesCmd>(), body);
    }

    [Fact]
    public void DrawOpTranslate_MovesOnlyTheSeriesTransform()
    {
        var dl = new DrawList();
        dl.Series(new RectF(3f, 4f, 64f, 40f), Spec(), Ramp(5), Affine2D.Identity, 0.5f);
        int body = Unsafe.SizeOf<DrawSeriesCmd>();
        var payload = new byte[body];
        dl.Bytes.Slice(sizeof(int), body).CopyTo(payload);
        var before = MemoryMarshal.Read<DrawSeriesCmd>(payload);

        DrawOpTranslate.Apply(DrawOp.DrawSeries, payload, 10f, 20f);
        var after = MemoryMarshal.Read<DrawSeriesCmd>(payload);

        Assert.Equal(before.Transform.Dx + 10f, after.Transform.Dx);
        Assert.Equal(before.Transform.Dy + 20f, after.Transform.Dy);
        Assert.Equal(before.Rect, after.Rect);
        Assert.Equal(before.X0, after.X0);
        Assert.Equal(before.Opacity, after.Opacity);
        Assert.Equal(before.Count, after.Count);
        Assert.Equal(before.S[2], after.S[2]);
        Assert.Equal(before.S[4], after.S[4]);
    }

    [Fact]
    public void HeadlessSwapchain_IsOccluded_FoldsTheLatchAndTheStandDown()
    {
        var dev = new HeadlessGpuDevice();
        Assert.Null(dev.PrimarySwapchain);
        var first = (HeadlessSwapchain)dev.CreateSwapchain(new SwapchainDesc(default, new Size2(8, 8)));
        var second = (HeadlessSwapchain)dev.CreateSwapchain(new SwapchainDesc(default, new Size2(8, 8)));
        Assert.Same(first, dev.PrimarySwapchain);   // the FIRST swapchain created is the window's

        ISwapchain target = first;
        Assert.False(target.IsOccluded);

        first.Occluded = true;                      // a modelled DXGI occlusion latch
        Assert.True(target.IsOccluded);
        Assert.False(first.LastPresentStoodDown);   // the latch does not pretend a present stood down
        first.Occluded = false;
        Assert.False(target.IsOccluded);

        first.PresentStandDown = true;              // a minimized / cloaked / hidden target
        Assert.True(target.IsOccluded);
        first.Occluded = true;
        Assert.True(target.IsOccluded);
        first.PresentStandDown = false;
        Assert.True(target.IsOccluded);             // the latch alone still holds it
        first.Occluded = false;
        Assert.False(target.IsOccluded);

        Assert.False(second.IsOccluded);            // per-target: the other swapchain never moved
    }
}
