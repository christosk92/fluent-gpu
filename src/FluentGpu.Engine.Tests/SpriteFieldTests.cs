using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using FluentGpu.Foundation;
using FluentGpu.Render;
using FluentGpu.Rhi;
using Xunit;

namespace FluentGpu.Engine.Tests;

/// <summary>Visualizer F5: <see cref="Sprite"/> packing and kernel bounds, and <see cref="DrawList.Sprites"/> chunking
/// (≤ 16 per POD chunk, each chunk's rect = the union of its sprites, the op framed by the shared size table).</summary>
public sealed class SpriteFieldTests
{
    [Fact]
    public void Sprite_is_32_bytes_and_packs_colour_round_trip()
    {
        Assert.Equal(32, Unsafe.SizeOf<Sprite>());
        var c = new ColorF(1f, 0.5f, 0.25f, 0.75f);
        ColorF back = Sprite.Unpack(Sprite.Pack(c));
        Assert.Equal(c.R, back.R, 2);
        Assert.Equal(c.G, back.G, 2);
        Assert.Equal(c.B, back.B, 2);
        Assert.Equal(c.A, back.A, 2);
    }

    [Fact]
    public void Kernel_bounds_contain_the_shape_at_any_rotation()
    {
        var capsule = new Sprite { X = 50f, Y = 50f, W = 40f, H = 10f, Rot = 1.1f };
        RectF b = capsule.Bounds(SpriteKernel.Capsule);
        Assert.True(b.X <= 30f && b.X + b.W >= 70f);                 // half the diagonal covers every rotation
        var seg = new Sprite { X = 0f, Y = 0f, W = 30f, H = 40f, Rot = 4f };
        RectF s = seg.Bounds(SpriteKernel.Segment);
        Assert.Equal(-2f, s.X, 3);
        Assert.Equal(34f, s.W, 3);
        var disc = new Sprite { X = 5f, Y = 5f, W = 3f, H = 2f };
        Assert.Equal(new RectF(2f, 2f, 6f, 6f), disc.Bounds(SpriteKernel.Disc));
    }

    [Fact]
    public void Forty_sprites_record_three_chunks_with_exact_rects()
    {
        var dl = new DrawList();
        var sprites = new Sprite[40];
        for (int i = 0; i < sprites.Length; i++) sprites[i] = new Sprite { X = i * 10f, Y = 20f, W = 2f, H = 2f, Rgba = 0xFFFFFFFFu };
        dl.Sprites(SpriteKernel.Disc, sprites, Affine2D.Identity, 1f);
        Assert.Equal(3, dl.OpcodeStats.DrawSprites);
        Assert.True(RepaintStreamSafety.TryBodySize(DrawOp.DrawSprites, out int body));
        Assert.Equal(Unsafe.SizeOf<DrawSpritesCmd>(), body);
        Assert.True(body <= 1024);                                       // the headless translate cap

        ReadOnlySpan<byte> bytes = dl.Bytes;
        var first = MemoryMarshal.Read<DrawSpritesCmd>(bytes.Slice(sizeof(int)));
        Assert.Equal(16, first.Count);
        Assert.Equal(40, first.Total);
        Assert.Equal(-2f, first.Rect.X, 3);                              // sprite 0's left edge
        Assert.Equal(15 * 10f + 2f, first.Rect.X + first.Rect.W, 3);     // sprite 15's right edge
        var last = MemoryMarshal.Read<DrawSpritesCmd>(bytes.Slice(2 * (sizeof(int) + body) + sizeof(int)));
        Assert.Equal(8, last.Count);
        Assert.Equal(32, last.Index);
    }
}
