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

/// <summary>A glow blob <c>BoxEl { Blend = Additive, Blur = 20 }</c>: while the effect budget is free the blur group is cut
/// as its own effect slice, past <see cref="SliceRecorder.EffectSliceCap"/> it records inline inside its SetBlend bracket.
/// The cut walk started its arena source-over, so the glow occluded what was under it on a quiet page and added light on a
/// busy one. Both routes must record the glow's fill under the additive blend. Serial: it constructs hosts.</summary>
[Collection(SerialTestCollection.Name)]
public sealed class AdditiveEffectSliceTests
{
    private static readonly ColorF Glow = ColorF.FromRgba(40, 200, 255);

    private sealed class Probe(bool spendBudget) : Component
    {
        public override Element Render()
        {
            var kids = new List<Element>();
            if (spendBudget)
                for (int i = 0; i < SliceRecorder.EffectSliceCap; i++)
                    kids.Add(new BoxEl { Width = 8f, Height = 8f, RepaintBoundary = true, Fill = ColorF.FromRgba(30, 30, 30) });
            kids.Add(new BoxEl { Blend = PaintBlend.Additive, Blur = 20f, Width = 100f, Height = 100f, Fill = Glow });
            return new BoxEl { Width = 320f, Height = 240f, Children = [.. kids] };
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void An_additive_blur_glow_paints_additive_cut_or_folded(bool spendBudget)
    {
        var strings = new StringTable();
        using var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("additive-blur", new Size2(320, 240), 1f));
        window.Show();
        using var host = new AppHost(app, window, new HeadlessGpuDevice(), new HeadlessFontSystem(strings), strings, new Probe(spendBudget));
        host.RunFrame();
        host.RunFrame();

        SliceRecorder slices = host.UiSlices;
        Span<(int NodeIndex, uint Gen, SliceRole Role, SliceKind Kind)> order = stackalloc (int, uint, SliceRole, SliceKind)[64];
        int n = Math.Min(slices.CopySliceOrder(order), order.Length);
        int glowFills = 0, additiveGlowFills = 0, effectSlices = 0;
        for (int i = 0; i < n; i++)
        {
            if (order[i].Kind == SliceKind.Effect) effectSlices++;
            ReadOnlySpan<byte> bytes = slices.SliceBytes(order[i].NodeIndex, order[i].Gen, order[i].Role);
            bool additive = false;
            int pos = 0;
            while (pos + sizeof(int) <= bytes.Length)
            {
                var op = (DrawOp)MemoryMarshal.Read<int>(bytes[pos..]);
                Assert.True(RepaintStreamSafety.TryBodySize(op, out int body));
                ReadOnlySpan<byte> payload = bytes[(pos + sizeof(int))..];
                if (op == DrawOp.SetBlend)
                    additive = MemoryMarshal.Read<SetBlendCmd>(payload).Mode == (int)PaintBlend.Additive;
                else if (op == DrawOp.FillRoundRect && MemoryMarshal.Read<FillRoundRectCmd>(payload).Fill == Glow)
                {
                    glowFills++;
                    if (additive) additiveGlowFills++;
                }
                pos += sizeof(int) + body;
            }
        }
        // the budget decides the route: the glow is its own effect slice only while the budget is free
        Assert.Equal(spendBudget ? SliceRecorder.EffectSliceCap : 1, effectSlices);
        Assert.Equal(1, glowFills);
        Assert.Equal(1, additiveGlowFills);
    }
}
