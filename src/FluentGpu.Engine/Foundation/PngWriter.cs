using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;

namespace FluentGpu.Foundation;

/// <summary>
/// Minimal pure-managed PNG encoder for debug frame captures — no WIC / System.Drawing, AOT-safe, zero new deps.
/// Emits an opaque RGB PNG (color type 2) — or, for a capture that must keep the window's translucency, an RGBA PNG
/// (color type 6) — from a tightly-packed top-down BGRA8 buffer (the D3D12 readback format). Used only by capture
/// tooling (<c>--screenshot</c>, the evidence bundle, the Store shot export); never on the hot frame path.
/// </summary>
public static class PngWriter
{
    /// <summary>Write a tightly-packed, top-down BGRA8 buffer (stride = width*4) as an opaque RGB PNG at <paramref name="path"/>.</summary>
    public static void WriteBgra(string path, ReadOnlySpan<byte> bgra, int width, int height)
        => WriteBgra(path, bgra, width, height, keepAlpha: false);

    /// <summary>Write a tightly-packed, top-down BGRA8 buffer as a PNG. <paramref name="keepAlpha"/> false: opaque RGB, the
    /// alpha byte dropped (the colour bytes are written as they are). True: straight-alpha RGBA, the colour UN-premultiplied
    /// from the back buffer's premultiplied alpha (<see cref="Unpremultiply"/>), so a Mica/acrylic window's see-through
    /// regions stay see-through when the PNG is composited over another background.</summary>
    public static void WriteBgra(string path, ReadOnlySpan<byte> bgra, int width, int height, bool keepAlpha)
    {
        using var fs = File.Create(path);
        ReadOnlySpan<byte> sig = [137, 80, 78, 71, 13, 10, 26, 10];
        fs.Write(sig);

        Span<byte> ihdr = stackalloc byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr, width);
        BinaryPrimitives.WriteInt32BigEndian(ihdr.Slice(4), height);
        ihdr[8] = 8;    // bit depth
        ihdr[9] = keepAlpha ? (byte)6 : (byte)2;    // color type: truecolor + alpha / truecolor RGB
        ihdr[10] = 0;   // deflate
        ihdr[11] = 0;   // filter method 0
        ihdr[12] = 0;   // no interlace
        WriteChunk(fs, "IHDR", ihdr);

        // Filtered scanlines: a leading filter byte (0 = None) then RGB(A), ready for zlib.
        int channels = keepAlpha ? 4 : 3;
        int rowBytes = width * channels;
        byte[] raw = new byte[(rowBytes + 1) * height];
        int w = 0;
        Span<byte> px = stackalloc byte[4];
        for (int y = 0; y < height; y++)
        {
            raw[w++] = 0;
            int srow = y * width * 4;
            for (int x = 0; x < width; x++)
            {
                int s = srow + x * 4;
                if (keepAlpha)
                {
                    Unpremultiply(bgra.Slice(s, 4), px);
                    raw[w++] = px[2];   // R
                    raw[w++] = px[1];   // G
                    raw[w++] = px[0];   // B
                    raw[w++] = px[3];   // A
                }
                else
                {
                    raw[w++] = bgra[s + 2];   // R
                    raw[w++] = bgra[s + 1];   // G
                    raw[w++] = bgra[s + 0];   // B
                }
            }
        }

        using var ms = new MemoryStream();
        using (var z = new ZLibStream(ms, CompressionLevel.Fastest, leaveOpen: true)) z.Write(raw, 0, raw.Length);
        WriteChunk(fs, "IDAT", ms.GetBuffer().AsSpan(0, (int)ms.Length));

        WriteChunk(fs, "IEND", default);
    }

    /// <summary>One premultiplied BGRA8 pixel to straight alpha (<paramref name="dst"/> = B, G, R, A). A fully transparent
    /// pixel becomes transparent black; a colour channel above its alpha (a blend that overshot) clamps to 255. Rounded
    /// to nearest, so a pixel that round-trips through premultiply comes back within one step.</summary>
    public static void Unpremultiply(ReadOnlySpan<byte> bgraPremultiplied, Span<byte> dst)
    {
        byte a = bgraPremultiplied[3];
        dst[3] = a;
        if (a == 0) { dst[0] = dst[1] = dst[2] = 0; return; }
        if (a == 255) { dst[0] = bgraPremultiplied[0]; dst[1] = bgraPremultiplied[1]; dst[2] = bgraPremultiplied[2]; return; }
        for (int c = 0; c < 3; c++) dst[c] = (byte)Math.Min(255, (bgraPremultiplied[c] * 255 + a / 2) / a);
    }

    private static void WriteChunk(Stream fs, string type, ReadOnlySpan<byte> data)
    {
        Span<byte> len = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(len, data.Length);
        fs.Write(len);
        Span<byte> t = stackalloc byte[4] { (byte)type[0], (byte)type[1], (byte)type[2], (byte)type[3] };
        fs.Write(t);
        if (!data.IsEmpty) fs.Write(data);
        Span<byte> crc = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crc, Crc32(t, data));
        fs.Write(crc);
    }

    private static uint Crc32(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        uint crc = 0xFFFFFFFFu;
        foreach (byte x in a) crc = Step(crc, x);
        foreach (byte x in b) crc = Step(crc, x);
        return crc ^ 0xFFFFFFFFu;
    }

    private static uint Step(uint crc, byte b)
    {
        crc ^= b;
        for (int k = 0; k < 8; k++) crc = (crc >> 1) ^ (0xEDB88320u & (uint)(-(int)(crc & 1)));
        return crc;
    }
}
