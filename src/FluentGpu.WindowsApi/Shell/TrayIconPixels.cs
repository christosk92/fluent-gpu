using System;
using FluentGpu.Foundation;

namespace FluentGpu.WindowsApi.Shell;

/// <summary>A badge's pill in icon pixels (top-left origin, the icon's own coordinate space).</summary>
internal readonly record struct TrayBadgeRect(int X, int Y, int Width, int Height);

/// <summary>
/// The pure pixel decisions behind <see cref="NotifyIcon.SetIcon(ReadOnlySpan{byte}, int, int)"/> and
/// <see cref="TrayIconArt"/>: argument validation, the premultiplied contract, coverage → premultiplied tint, source-over
/// compositing, the badge pill's layout and coverage, and the icon mask. No GDI, no DirectWrite — unit-tested in
/// <c>FluentGpu.Windows.Tests</c>.
/// </summary>
internal static class TrayIconPixels
{
    /// <summary>The glyph art's size range: <see cref="NotifyIcon.SmallIconSizeForDpi"/> spans 16 (96 DPI) to 48 (288 DPI).</summary>
    public const int MinArtSize = 16, MaxArtSize = 48;
    /// <summary>The largest bitmap <see cref="NotifyIcon.SetIcon(ReadOnlySpan{byte}, int, int)"/> accepts (the ICO ceiling).</summary>
    public const int MaxBitmapSize = 256;
    /// <summary>A badge holds 1–3 characters; longer text is cut.</summary>
    public const int MaxBadgeChars = 3;

    /// <summary>White on a dark taskbar.</summary>
    public static ColorF DarkTaskbarInk => new(1f, 1f, 1f, 1f);
    /// <summary>#1B1B1B on a light taskbar (the shell's own near-black glyph ink).</summary>
    public static ColorF LightTaskbarInk => ColorF.FromRgba(0x1B, 0x1B, 0x1B);
    /// <summary>The default badge fill: Fluent's "critical" red (#C42B1C).</summary>
    public static ColorF DefaultBadgeFill => ColorF.FromRgba(0xC4, 0x2B, 0x1C);

    /// <summary>The ink a glyph gets when the caller names none: white on dark, #1B1B1B on light.</summary>
    public static ColorF DefaultInk(bool lightTaskbar) => lightTaskbar ? LightTaskbarInk : DarkTaskbarInk;

    /// <summary>The art size for a request: non-positive ⇒ 16, else clamped to 16–48.</summary>
    public static int ClampArtSize(int sizePx) => sizePx <= 0 ? MinArtSize : Math.Clamp(sizePx, MinArtSize, MaxArtSize);

    /// <summary>The badge text: trimmed, null when empty, cut to <see cref="MaxBadgeChars"/> chars (never ending on the first
    /// half of a surrogate pair).</summary>
    public static string? TrimBadge(string? badge)
    {
        if (badge is null) return null;
        string t = badge.Trim();
        if (t.Length == 0) return null;
        if (t.Length <= MaxBadgeChars) return t;
        int n = MaxBadgeChars;
        if (char.IsHighSurrogate(t[n - 1])) n--;
        return t.Substring(0, n);
    }

    /// <summary>Throw unless <paramref name="pixels"/> is exactly <paramref name="width"/>×<paramref name="height"/>×4 bytes
    /// and both sides are 1–<see cref="MaxBitmapSize"/>.</summary>
    public static void Validate(ReadOnlySpan<byte> pixels, int width, int height, string paramName)
    {
        if (width is < 1 or > MaxBitmapSize) throw new ArgumentOutOfRangeException(nameof(width), width, $"Icon width must be 1..{MaxBitmapSize} px.");
        if (height is < 1 or > MaxBitmapSize) throw new ArgumentOutOfRangeException(nameof(height), height, $"Icon height must be 1..{MaxBitmapSize} px.");
        if (pixels.Length != width * height * 4)
            throw new ArgumentException($"Expected {width}×{height}×4 = {width * height * 4} bytes of premultiplied BGRA, got {pixels.Length}.", paramName);
    }

    /// <summary>The non-throwing form of <see cref="Validate"/> (an icon source's result).</summary>
    public static bool IsValid(ReadOnlySpan<byte> pixels, int width, int height)
        => width is >= 1 and <= MaxBitmapSize && height is >= 1 and <= MaxBitmapSize && pixels.Length == width * height * 4;

    /// <summary>True when every colour channel is at most its pixel's alpha — the premultiplied contract (which implies an
    /// alpha-0 pixel is colour 0).</summary>
    public static bool IsPremultiplied(ReadOnlySpan<byte> bgra)
    {
        for (int i = 0; i + 3 < bgra.Length; i += 4)
        {
            byte a = bgra[i + 3];
            if (bgra[i] > a || bgra[i + 1] > a || bgra[i + 2] > a) return false;
        }
        return true;
    }

    private static byte ToByte(float v) => (byte)Math.Clamp(MathF.Round(v * 255f, MidpointRounding.AwayFromZero), 0f, 255f);

    /// <summary>One coverage sample tinted to premultiplied BGRA: alpha = ink.A × coverage, each colour = channel × alpha.
    /// Coverage 0 is transparent black (the premultiplied contract).</summary>
    public static void Tint(byte coverage, ColorF ink, Span<byte> bgra)
    {
        float a = Math.Clamp(ink.A, 0f, 1f) * (coverage / 255f);
        bgra[0] = ToByte(Math.Clamp(ink.B, 0f, 1f) * a);
        bgra[1] = ToByte(Math.Clamp(ink.G, 0f, 1f) * a);
        bgra[2] = ToByte(Math.Clamp(ink.R, 0f, 1f) * a);
        bgra[3] = ToByte(a);
    }

    /// <summary>Composite <paramref name="ink"/> × <paramref name="coverage"/> (a <paramref name="covW"/>×<paramref name="covH"/>
    /// 8-bit mask placed with its top-left at (<paramref name="x"/>, <paramref name="y"/>)) source-over onto the premultiplied
    /// <paramref name="dst"/>; whatever falls outside the destination is clipped.</summary>
    public static void SourceOver(Span<byte> dst, int dstW, int dstH, ReadOnlySpan<byte> coverage, int covW, int covH, int x, int y, ColorF ink)
    {
        Span<byte> src = stackalloc byte[4];
        for (int row = 0; row < covH; row++)
        {
            int dy = y + row;
            if ((uint)dy >= (uint)dstH) continue;
            for (int col = 0; col < covW; col++)
            {
                int dx = x + col;
                if ((uint)dx >= (uint)dstW) continue;
                byte c = coverage[row * covW + col];
                if (c == 0) continue;
                Tint(c, ink, src);
                BlendPixel(dst.Slice((dy * dstW + dx) * 4, 4), src);
            }
        }
    }

    /// <summary>Premultiplied source-over of one pixel: dst = src + dst × (1 − src.A).</summary>
    public static void BlendPixel(Span<byte> dst, ReadOnlySpan<byte> src)
    {
        int inv = 255 - src[3];
        for (int i = 0; i < 4; i++) dst[i] = (byte)Math.Min(255, src[i] + (dst[i] * inv + 127) / 255);
    }

    /// <summary>Where a box of ink <c>[inkStart, inkEnd)</c> must be shifted (a whole-pixel offset, so the rasterized
    /// coverage is placed unresampled) to centre it in <c>[boxStart, boxStart + boxLength)</c>; an odd remainder goes
    /// to the far side.</summary>
    public static int CenterOffset(int inkStart, int inkEnd, int boxStart, int boxLength)
        => boxStart + ((boxLength - (inkEnd - inkStart)) >> 1) - inkStart;

    /// <summary>The badge pill's height for an icon size: 9/16 of it, at least 7 px (16→9, 20→11, 24→14, 32→18, 48→27).</summary>
    public static int BadgeHeight(int sizePx) => Math.Max(7, (sizePx * 9 + 8) / 16);

    /// <summary>The badge's horizontal padding either side of its text.</summary>
    public static int BadgePadX(int badgeHeight) => Math.Max(1, badgeHeight / 4);

    /// <summary>The badge pill in the icon's bottom-right corner: <see cref="BadgeHeight"/> tall, wide enough for the text's
    /// ink plus padding (at least a circle, at most the icon's width).</summary>
    public static TrayBadgeRect BadgeRect(int sizePx, int textInkWidth)
    {
        int h = Math.Min(BadgeHeight(sizePx), sizePx);
        int w = Math.Clamp(Math.Max(h, Math.Max(0, textInkWidth) + 2 * BadgePadX(h)), h, sizePx);
        return new TrayBadgeRect(sizePx - w, sizePx - h, w, h);
    }

    /// <summary>Antialiased coverage (0..1) of the pixel whose centre is (<paramref name="px"/>, <paramref name="py"/>)
    /// by a capsule filling <paramref name="r"/> — radius half the short side — grown by <paramref name="inflate"/> px.</summary>
    public static float PillCoverage(float px, float py, in TrayBadgeRect r, float inflate = 0f)
    {
        float radius = Math.Min(r.Width, r.Height) * 0.5f;
        float cy = r.Y + r.Height * 0.5f, cx = r.X + r.Width * 0.5f;
        bool horizontal = r.Width >= r.Height;
        float d;
        if (horizontal)
        {
            float x0 = r.X + radius, x1 = r.X + r.Width - radius;
            float qx = Math.Clamp(px, x0, x1);
            d = MathF.Sqrt((px - qx) * (px - qx) + (py - cy) * (py - cy));
        }
        else
        {
            float y0 = r.Y + radius, y1 = r.Y + r.Height - radius;
            float qy = Math.Clamp(py, y0, y1);
            d = MathF.Sqrt((px - cx) * (px - cx) + (py - qy) * (py - qy));
        }
        return Math.Clamp(radius + inflate - d + 0.5f, 0f, 1f);
    }

    /// <summary>Fill the badge pill with <paramref name="fill"/>, source-over.</summary>
    public static void FillPill(Span<byte> dst, int dstW, int dstH, in TrayBadgeRect r, ColorF fill)
    {
        Span<byte> src = stackalloc byte[4];
        for (int y = Math.Max(0, r.Y); y < Math.Min(dstH, r.Y + r.Height); y++)
            for (int x = Math.Max(0, r.X); x < Math.Min(dstW, r.X + r.Width); x++)
            {
                float c = PillCoverage(x + 0.5f, y + 0.5f, r);
                if (c <= 0f) continue;
                Tint((byte)MathF.Round(c * 255f), fill, src);
                BlendPixel(dst.Slice((y * dstW + x) * 4, 4), src);
            }
    }

    /// <summary>Clear a <paramref name="ring"/>-px moat around the pill (scale what is there by 1 − coverage of the grown
    /// pill), so the badge reads as cut out of the glyph instead of stacked on it.</summary>
    public static void KnockOut(Span<byte> dst, int dstW, int dstH, in TrayBadgeRect r, float ring)
    {
        int grow = (int)MathF.Ceiling(ring) + 1;
        for (int y = Math.Max(0, r.Y - grow); y < Math.Min(dstH, r.Y + r.Height + grow); y++)
            for (int x = Math.Max(0, r.X - grow); x < Math.Min(dstW, r.X + r.Width + grow); x++)
            {
                float c = PillCoverage(x + 0.5f, y + 0.5f, r, ring);
                if (c <= 0f) continue;
                int keep = 255 - (int)MathF.Round(c * 255f);
                Span<byte> p = dst.Slice((y * dstW + x) * 4, 4);
                for (int i = 0; i < 4; i++) p[i] = (byte)((p[i] * keep + 127) / 255);
            }
    }

    /// <summary>The badge text colour that reads on <paramref name="fill"/>: near-black on a light fill (relative luminance
    /// above 0.5), white otherwise.</summary>
    public static ColorF ContrastInk(ColorF fill)
    {
        static float Lin(float c) => c <= 0.04045f ? c / 12.92f : MathF.Pow((c + 0.055f) / 1.055f, 2.4f);
        float lum = 0.2126f * Lin(fill.R) + 0.7152f * Lin(fill.G) + 0.0722f * Lin(fill.B);
        return lum > 0.5f ? LightTaskbarInk : DarkTaskbarInk;
    }

    /// <summary>Premultiplied BGRA → the straight alpha a 32bpp icon's colour bitmap carries (like an <c>.ico</c> frame).</summary>
    public static void Unpremultiply(ReadOnlySpan<byte> premultiplied, Span<byte> straight)
    {
        for (int i = 0; i + 3 < premultiplied.Length; i += 4)
            PngWriter.Unpremultiply(premultiplied.Slice(i, 4), straight.Slice(i, 4));
    }

    /// <summary>Bytes per row of a 1bpp <c>CreateBitmap</c> mask: rows are padded to a 16-bit WORD.</summary>
    public static int MaskStride(int width) => ((width + 15) / 16) * 2;

    /// <summary>The icon's AND mask: bit set (screen kept) where the pixel is fully transparent, clear where it has any
    /// coverage. MSB-first within each byte. <paramref name="mask"/> is <see cref="MaskStride"/> × height bytes.</summary>
    public static void BuildMask(ReadOnlySpan<byte> bgra, int width, int height, Span<byte> mask)
    {
        int stride = MaskStride(width);
        mask.Slice(0, stride * height).Clear();
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
                if (bgra[(y * width + x) * 4 + 3] == 0)
                    mask[y * stride + (x >> 3)] |= (byte)(0x80 >> (x & 7));
    }
}
