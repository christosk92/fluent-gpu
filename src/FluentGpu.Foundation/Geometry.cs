namespace FluentGpu.Foundation;

public readonly record struct Size2(float Width, float Height)
{
    public static Size2 Zero => default;
}

public readonly record struct Point2(float X, float Y)
{
    public static Point2 Zero => default;
    public static Point2 operator +(Point2 a, Point2 b) => new(a.X + b.X, a.Y + b.Y);
    public static Point2 operator -(Point2 a, Point2 b) => new(a.X - b.X, a.Y - b.Y);
}

/// <summary>Axis-aligned rectangle in DIPs. Local to its node (P8: Bounds are node-LOCAL).</summary>
public readonly record struct RectF(float X, float Y, float W, float H)
{
    public float Right => X + W;
    public float Bottom => Y + H;
    public Size2 Size => new(W, H);
    public bool Contains(Point2 p) => p.X >= X && p.X < Right && p.Y >= Y && p.Y < Bottom;
    public static RectF FromLTRB(float l, float t, float r, float b) => new(l, t, r - l, b - t);
}

/// <summary>Per-edge thickness (margin/padding/border). [InlineArray]-shaped logically; struct of 4 floats here.</summary>
public readonly record struct Edges4(float Left, float Top, float Right, float Bottom)
{
    public static Edges4 All(float v) => new(v, v, v, v);
    public float Horizontal => Left + Right;
    public float Vertical => Top + Bottom;
}

public readonly record struct CornerRadius4(float TopLeft, float TopRight, float BottomRight, float BottomLeft)
{
    public static CornerRadius4 All(float v) => new(v, v, v, v);
    public bool IsUniform => TopLeft == TopRight && TopRight == BottomRight && BottomRight == BottomLeft;
}

/// <summary>Straight-alpha sRGB color (P8: brush color = straight-alpha sRGB float4).</summary>
public readonly record struct ColorF(float R, float G, float B, float A)
{
    public static ColorF Transparent => default;
    public static ColorF FromRgba(byte r, byte g, byte b, byte a = 255)
        => new(r / 255f, g / 255f, b / 255f, a / 255f);

    /// <summary>Linear interpolation between two colors (t in 0..1) — for animated color transitions.</summary>
    public static ColorF Lerp(ColorF a, ColorF b, float t)
        => new(a.R + (b.R - a.R) * t, a.G + (b.G - a.G) * t, a.B + (b.B - a.B) * t, a.A + (b.A - a.A) * t);
}

/// <summary>2x3 affine (local→parent). 2.5D/perspective out of scope (per spec).</summary>
public readonly record struct Affine2D(float M11, float M12, float M21, float M22, float Dx, float Dy)
{
    public static Affine2D Identity => new(1, 0, 0, 1, 0, 0);
    public static Affine2D Translation(float dx, float dy) => new(1, 0, 0, 1, dx, dy);

    public Point2 Transform(Point2 p) => new(M11 * p.X + M21 * p.Y + Dx, M12 * p.X + M22 * p.Y + Dy);

    /// <summary>this ∘ other (apply <paramref name="other"/> first, then this).</summary>
    public Affine2D Multiply(in Affine2D o) => new(
        M11 * o.M11 + M21 * o.M12,
        M12 * o.M11 + M22 * o.M12,
        M11 * o.M21 + M21 * o.M22,
        M12 * o.M21 + M22 * o.M22,
        M11 * o.Dx + M21 * o.Dy + Dx,
        M12 * o.Dx + M22 * o.Dy + Dy);
}
