using System.Runtime.CompilerServices;

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

    public bool IsEmpty => W <= 0f || H <= 0f;

    /// <summary>True if the two rects share any area (touching edges do not count).</summary>
    public bool Overlaps(in RectF o) => X < o.Right && o.X < Right && Y < o.Bottom && o.Y < Bottom;

    /// <summary>The overlap of two rects (an empty rect if they do not overlap). Clip-stack intersection.</summary>
    public RectF Intersect(in RectF o)
    {
        float l = MathF.Max(X, o.X), t = MathF.Max(Y, o.Y);
        float r = MathF.Min(Right, o.Right), b = MathF.Min(Bottom, o.Bottom);
        return (r <= l || b <= t) ? default : new RectF(l, t, r - l, b - t);
    }

    /// <summary>A sentinel "unbounded" clip — larger than any real surface; intersecting with it is a no-op.</summary>
    public static RectF Infinite => new(-1e9f, -1e9f, 2e9f, 2e9f);

    /// <summary>True for the <see cref="Infinite"/> sentinel (a node with no authored clip-rect override).</summary>
    public bool IsInfinite => X <= -1e9f;
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

    /// <summary>Linear interpolation between two colors (t in 0..1) in straight-sRGB channels — cheap, for value fades.</summary>
    public static ColorF Lerp(ColorF a, ColorF b, float t)
        => new(a.R + (b.R - a.R) * t, a.G + (b.G - a.G) * t, a.B + (b.B - a.B) * t, a.A + (b.A - a.A) * t);

    /// <summary>Perceptually-correct interpolation in LINEAR light, ALPHA-WEIGHTED (true premultiplied — the engine's
    /// linear-blend/premultiplied color contract) — used for every surface/text cross-fade (hover/press ramps, implicit
    /// BrushTransitions, gradient morphs). The weighting matters when the endpoints' alphas differ: a translucent
    /// white-tinted card fill (#0DFFFFFF) cross-fading to an opaque dark solid must stay DARK mid-flight — a straight
    /// per-channel lerp passes through bright half-transparent grey (the "white flash"). For SAME-alpha endpoints the
    /// result is identical to the straight linear lerp. Alloc-free (static local helpers, no captures).</summary>
    public static ColorF LerpLinear(ColorF a, ColorF b, float t)
    {
        static float S2L(float c) => c <= 0.04045f ? c / 12.92f : MathF.Pow((c + 0.055f) / 1.055f, 2.4f);
        static float L2S(float c) => c <= 0.0031308f ? c * 12.92f : 1.055f * MathF.Pow(MathF.Max(c, 0f), 1f / 2.4f) - 0.055f;
        float wa = a.A * (1f - t), wb = b.A * t;
        float alpha = wa + wb;
        if (alpha <= 1e-6f)   // transparent → transparent: no coverage to weight by; keep a plain RGB ramp
            return new(
                L2S(S2L(a.R) + (S2L(b.R) - S2L(a.R)) * t),
                L2S(S2L(a.G) + (S2L(b.G) - S2L(a.G)) * t),
                L2S(S2L(a.B) + (S2L(b.B) - S2L(a.B)) * t),
                a.A + (b.A - a.A) * t);
        float inv = 1f / alpha;
        return new(
            L2S((S2L(a.R) * wa + S2L(b.R) * wb) * inv),
            L2S((S2L(a.G) * wa + S2L(b.G) * wb) * inv),
            L2S((S2L(a.B) * wa + S2L(b.B) * wb) * inv),
            a.A + (b.A - a.A) * t);
    }

    // ── HSV (for ColorPicker) ── h in [0,360), s/v/a in [0,1]. Straight-channel HSV (the conventional color-wheel space).
    public static ColorF FromHsv(float h, float s, float v, float a = 1f)
    {
        h = ((h % 360f) + 360f) % 360f;
        s = Math.Clamp(s, 0f, 1f); v = Math.Clamp(v, 0f, 1f);
        float c = v * s;
        float x = c * (1f - MathF.Abs((h / 60f) % 2f - 1f));
        float m = v - c;
        float r, g, b;
        if (h < 60f) { r = c; g = x; b = 0f; }
        else if (h < 120f) { r = x; g = c; b = 0f; }
        else if (h < 180f) { r = 0f; g = c; b = x; }
        else if (h < 240f) { r = 0f; g = x; b = c; }
        else if (h < 300f) { r = x; g = 0f; b = c; }
        else { r = c; g = 0f; b = x; }
        return new(r + m, g + m, b + m, a);
    }

    public (float H, float S, float V) ToHsv()
    {
        float max = MathF.Max(R, MathF.Max(G, B));
        float min = MathF.Min(R, MathF.Min(G, B));
        float d = max - min;
        float h = 0f;
        if (d > 1e-6f)
        {
            if (max == R) h = 60f * (((G - B) / d % 6f + 6f) % 6f);
            else if (max == G) h = 60f * ((B - R) / d + 2f);
            else h = 60f * ((R - G) / d + 4f);
        }
        if (h < 0f) h += 360f;
        float s = max <= 0f ? 0f : d / max;
        return (h, s, max);
    }

    static int B255(float c) => Math.Clamp((int)MathF.Round(c * 255f), 0, 255);
    public string ToHex() => $"{B255(R):X2}{B255(G):X2}{B255(B):X2}";

    /// <summary>Parse a 6- or 8-hex-digit RGB/RGBA string (with or without a leading '#'). Returns false on malformed input.</summary>
    public static bool TryParseHex(string s, out ColorF color)
    {
        color = default;
        if (string.IsNullOrEmpty(s)) return false;
        if (s[0] == '#') s = s[1..];
        if (s.Length != 6 && s.Length != 8) return false;
        Span<byte> bytes = stackalloc byte[4] { 0, 0, 0, 255 };
        for (int i = 0; i < s.Length; i += 2)
        {
            int hi = HexVal(s[i]), lo = HexVal(s[i + 1]);
            if (hi < 0 || lo < 0) return false;
            bytes[i / 2] = (byte)(hi * 16 + lo);
        }
        color = FromRgba(bytes[0], bytes[1], bytes[2], bytes[3]);
        return true;

        static int HexVal(char c) => c switch
        {
            >= '0' and <= '9' => c - '0',
            >= 'a' and <= 'f' => c - 'a' + 10,
            >= 'A' and <= 'F' => c - 'A' + 10,
            _ => -1,
        };
    }
}

/// <summary>2x3 affine (local→parent). 2.5D/perspective out of scope (per spec).</summary>
public readonly record struct Affine2D(float M11, float M12, float M21, float M22, float Dx, float Dy)
{
    public static Affine2D Identity => new(1, 0, 0, 1, 0, 0);
    public static Affine2D Translation(float dx, float dy) => new(1, 0, 0, 1, dx, dy);
    public static Affine2D Scale(float sx, float sy) => new(sx, 0, 0, sy, 0, 0);
    public static Affine2D Rotation(float radians)
    {
        float c = MathF.Cos(radians), s = MathF.Sin(radians);
        return new(c, s, -s, c, 0, 0);
    }
    public bool IsIdentity => M11 == 1f && M12 == 0f && M21 == 0f && M22 == 1f && Dx == 0f && Dy == 0f;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Point2 Transform(Point2 p) => new(M11 * p.X + M21 * p.Y + Dx, M12 * p.X + M22 * p.Y + Dy);

    /// <summary>The axis-aligned bounding box of <paramref name="r"/> after this transform (device-space clip/cull rect).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public RectF TransformBounds(in RectF r)
    {
        float x = M11 * r.X + M21 * r.Y + Dx;
        float y = M12 * r.X + M22 * r.Y + Dy;
        float wx = M11 * r.W, wy = M12 * r.W;
        float hx = M21 * r.H, hy = M22 * r.H;
        float minX = x + MathF.Min(0f, wx) + MathF.Min(0f, hx);
        float minY = y + MathF.Min(0f, wy) + MathF.Min(0f, hy);
        float maxX = x + MathF.Max(0f, wx) + MathF.Max(0f, hx);
        float maxY = y + MathF.Max(0f, wy) + MathF.Max(0f, hy);
        return new RectF(minX, minY, maxX - minX, maxY - minY);
    }

    /// <summary>this ∘ other (apply <paramref name="other"/> first, then this).</summary>
    public Affine2D Multiply(in Affine2D o) => new(
        M11 * o.M11 + M21 * o.M12,
        M12 * o.M11 + M22 * o.M12,
        M11 * o.M21 + M21 * o.M22,
        M12 * o.M21 + M22 * o.M22,
        M11 * o.Dx + M21 * o.Dy + Dx,
        M12 * o.Dx + M22 * o.Dy + Dy);

    /// <summary>Inverse-map a DEVICE point back through this transform into the pre-transform frame — the hit-test
    /// mirror of <see cref="Transform"/> (scale-aware pointer routing into Viewbox/zoomed content). False when the
    /// matrix is degenerate (a zero scale renders nothing hit-testable).</summary>
    public bool TryInverseTransform(Point2 p, out Point2 inv)
    {
        float det = M11 * M22 - M21 * M12;
        if (MathF.Abs(det) < 1e-6f) { inv = default; return false; }
        float x = p.X - Dx, y = p.Y - Dy;
        inv = new Point2((M22 * x - M21 * y) / det, (M11 * y - M12 * x) / det);
        return true;
    }
}
