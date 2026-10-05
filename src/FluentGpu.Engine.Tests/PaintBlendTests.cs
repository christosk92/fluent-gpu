using System.Runtime.CompilerServices;
using FluentGpu.Foundation;
using FluentGpu.Render;
using FluentGpu.Rhi;
using Xunit;

namespace FluentGpu.Engine.Tests;

/// <summary>Visualizer F4: the <see cref="DrawOp.SetBlend"/> op is framed by the shared size table and counted, and a
/// composite item carries Screen in <see cref="CompositeItem.BlendCopy"/>.</summary>
public sealed class PaintBlendTests
{
    [Fact]
    public void SetBlend_is_a_small_framed_counted_op()
    {
        var dl = new DrawList();
        dl.SetBlend(PaintBlend.Additive);
        dl.SetBlend(PaintBlend.SrcOver);
        Assert.Equal(2, dl.OpcodeStats.SetBlend);
        Assert.True(RepaintStreamSafety.TryBodySize(DrawOp.SetBlend, out int body));
        Assert.Equal(Unsafe.SizeOf<SetBlendCmd>(), body);
        Assert.Equal(2 * (sizeof(int) + body), dl.Bytes.Length);
    }

    [Fact]
    public void Screen_is_its_own_blend_code()
    {
        Assert.NotEqual(CompositeItem.BlendCopyWrite, CompositeItem.BlendScreen);
        Assert.NotEqual(0, CompositeItem.BlendScreen);
    }
}
