using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Threading;
using FluentGpu.Media.Codecs.Wic;
using Xunit;

namespace FluentGpu.Windows.Tests;

/// <summary>
/// WIC reads the encoded bytes through the IWICStream pointer for the whole decode (CopyPixels pulls the scan data), so the
/// codec must keep a managed source buffer pinned until it is done. Each iteration decodes a FRESH small-object-heap copy of
/// a 120x120 stored-deflate PNG while another thread forces compacting gen0 GCs and churns filler; an unpinned source
/// moves mid-decode and the decode fails or comes back with wrong pixels. Real WIC, no GPU.
/// </summary>
public sealed class WicSourcePinTests
{
    private const int W = 120, H = 120;   // 43 KB PNG: under the LOH threshold, so the GC can move it

    private static byte Px(int x, int y, int c) => (byte)(c switch { 0 => x * 7 + y * 13, 1 => x * x + y * 3, _ => (x ^ y) * 5 + 17 });

    private static byte[] Png()
    {
        var raw = new byte[H * (1 + W * 3)];
        for (int y = 0, i = 0; y < H; y++) { raw[i++] = 0; for (int x = 0; x < W; x++) for (int c = 0; c < 3; c++) raw[i++] = Px(x, y, c); }
        var z = new MemoryStream();
        using (var zs = new ZLibStream(z, CompressionLevel.NoCompression, leaveOpen: true)) zs.Write(raw);
        var o = new MemoryStream();
        o.Write([0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A]);
        Chunk(o, "IHDR", [0, 0, 0, W, 0, 0, 0, H, 8, 2, 0, 0, 0]);   // 8-bit RGB
        Chunk(o, "IDAT", z.ToArray());
        Chunk(o, "IEND", []);
        return o.ToArray();
    }

    private static void Chunk(Stream o, string type, byte[] data)
    {
        byte[] body = new byte[4 + data.Length];
        for (int i = 0; i < 4; i++) body[i] = (byte)type[i];
        data.CopyTo(body, 4);
        Be(o, (uint)data.Length); o.Write(body);
        uint crc = 0xFFFFFFFF;
        foreach (byte b in body) { crc ^= b; for (int k = 0; k < 8; k++) crc = (crc >> 1) ^ (0xEDB88320u & (uint)-(int)(crc & 1)); }
        Be(o, ~crc);
    }

    private static void Be(Stream o, uint v) { o.WriteByte((byte)(v >> 24)); o.WriteByte((byte)(v >> 16)); o.WriteByte((byte)(v >> 8)); o.WriteByte((byte)v); }

    private static bool Matches(byte[] d)
    {
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                int i = (y * W + x) * 4;   // 32bppPBGRA, opaque
                if (d[i] != Px(x, y, 2) || d[i + 1] != Px(x, y, 1) || d[i + 2] != Px(x, y, 0) || d[i + 3] != 255) return false;
            }
        return true;
    }

    [Fact]
    public void A_compacting_gc_during_decode_does_not_corrupt_the_source_read()
    {
        byte[] png = Png();
        using var codec = new WicImageCodec();
        var dst = new byte[W * H * 4];
        Assert.True(codec.DecodeConstrained((byte[])png.Clone(), W, H, dst, out int w0, out int h0));   // quiet baseline
        Assert.Equal((W, H), (w0, h0));
        Assert.True(Matches(dst));

        using var stop = new CancellationTokenSource();
        var churn = new Thread(() =>
        {
            var keep = new byte[16][]; int k = 0;
            while (!stop.IsCancellationRequested)
            {
                for (int i = 0; i < 8; i++) { var f = new byte[4096]; f.AsSpan().Fill(0xA5); keep[k++ & 15] = f; }
                GC.Collect(0, GCCollectionMode.Forced, blocking: true, compacting: true);
            }
        }) { IsBackground = true };
        churn.Start();

        int iters = 0, bad = 0;
        try
        {
            var sw = Stopwatch.StartNew();
            while (sw.Elapsed < TimeSpan.FromSeconds(1.5) && bad == 0)
            {
                iters++;
                byte[] enc = (byte[])png.Clone();   // fresh gen0 array, like a pooled fetch rental
                dst.AsSpan().Clear();
                bool ok = codec.DecodeConstrained(enc, W, H, dst, out int w, out int h);
                if (!ok || (w, h) != (W, H) || !Matches(dst)) bad++;
                GC.KeepAlive(enc);
            }
        }
        finally { stop.Cancel(); churn.Join(); }

        Assert.True(bad == 0, $"decode {iters} read a moved source buffer (failed or wrong pixels)");
    }
}
