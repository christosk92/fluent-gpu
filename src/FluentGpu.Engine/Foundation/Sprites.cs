using System;
using System.Runtime.InteropServices;

namespace FluentGpu.Foundation;

/// <summary>The closed set of shapes a <c>SpriteFieldEl</c> instance can be (visualizer F5). Closed on purpose: each kernel
/// declares its exact bounds, so the repaint halo, the slice bounds and the headless model stay exact.</summary>
public enum SpriteKernel : byte
{
    /// <summary>An ellipse: radii <see cref="Sprite.W"/>, <see cref="Sprite.H"/>, turned by <see cref="Sprite.Rot"/>.</summary>
    Disc = 0,
    /// <summary>A rounded bar: total length <see cref="Sprite.W"/> (caps included) along local +x, width <see cref="Sprite.H"/>.</summary>
    Capsule = 1,
    /// <summary>A capsule whose alpha ramps from 0 at the tail (−W/2) to full at the head (+W/2): a motion streak.</summary>
    Streak = 2,
    /// <summary>A round-capped line from (<see cref="Sprite.X"/>, <see cref="Sprite.Y"/>) to (<see cref="Sprite.W"/>,
    /// <see cref="Sprite.H"/>), <see cref="Sprite.Rot"/> DIP wide.</summary>
    Segment = 3,
}

/// <summary>One instance of a <c>SpriteFieldEl</c>: 32 bytes of POD, node-local DIP. <see cref="Soft"/> 0 = a crisp
/// anti-aliased edge, 1 = a radial falloff to 0 at the edge (a glow); for a <see cref="SpriteKernel.Streak"/> the length
/// ramp is always on. <see cref="Rgba"/> is straight-alpha 0xRRGGBBAA (<see cref="Pack"/>); the backend premultiplies.
/// <see cref="Rot"/> is radians, positive = clockwise on screen (y down), like <see cref="Affine2D"/>.</summary>
[StructLayout(LayoutKind.Sequential, Size = 32)]
public struct Sprite
{
    public float X, Y, W, H;
    public float Rot;
    public float Soft;
    public uint Rgba;
    public uint Reserved;

    /// <summary>A colour as the straight-alpha 0xRRGGBBAA <see cref="Rgba"/> word.</summary>
    public static uint Pack(ColorF c)
    {
        static uint B(float v) => (uint)Math.Clamp((int)MathF.Round(v * 255f), 0, 255);
        return (B(c.R) << 24) | (B(c.G) << 16) | (B(c.B) << 8) | B(c.A);
    }

    /// <summary>The 0xRRGGBBAA word back as a colour.</summary>
    public static ColorF Unpack(uint rgba)
        => new(((rgba >> 24) & 0xFF) / 255f, ((rgba >> 16) & 0xFF) / 255f, ((rgba >> 8) & 0xFF) / 255f, (rgba & 0xFF) / 255f);

    /// <summary>The node-local AABB this sprite can paint (kernel-exact, rotation-safe; no AA pad — the recorder and the
    /// backend add <c>RepaintCull.AaHaloDip</c>).</summary>
    public readonly RectF Bounds(SpriteKernel kernel)
    {
        switch (kernel)
        {
            case SpriteKernel.Disc:
            {
                float r = MathF.Max(MathF.Abs(W), MathF.Abs(H));
                return new RectF(X - r, Y - r, 2f * r, 2f * r);
            }
            case SpriteKernel.Segment:
            {
                float hw = MathF.Abs(Rot) * 0.5f;
                float l = MathF.Min(X, W) - hw, t = MathF.Min(Y, H) - hw;
                return new RectF(l, t, MathF.Abs(W - X) + 2f * hw, MathF.Abs(H - Y) + 2f * hw);
            }
            default:
            {
                float r = 0.5f * MathF.Sqrt(W * W + H * H);
                return new RectF(X - r, Y - r, 2f * r, 2f * r);
            }
        }
    }
}
