using System;
using FluentGpu.Media.Codecs.Wic;
using Xunit;

namespace FluentGpu.Windows.Tests;

/// <summary>
/// The WIC codec decodes a camera JPEG upright: the EXIF Orientation tag (1..8) is applied, and the returned dims are
/// the DISPLAY dims (swapped for the 90/270 cases), so ImageFit.Cover/Contain fit against the real aspect. The source
/// is a 64x32 JPEG of four solid quadrants (red, green / blue, white) with a hand-built APP1 Exif segment spliced in
/// after the JFIF header; the decode is read back by quadrant colour. Real WIC, no GPU.
/// </summary>
public sealed class WicExifOrientationTests
{
    // 64x32 baseline JPEG, quality 100: top-left red, top-right green, bottom-left blue, bottom-right white. No metadata.
    private const string QuadrantsJpeg =
        "/9j/4AAQSkZJRgABAQEAYABgAAD/2wBDAAEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQH/2wBDAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQH/wAARCAAgAEADASIAAhEBAxEB/8QAHwAAAQUBAQEBAQEAAAAAAAAAAAECAwQFBgcICQoL/8QAtRAAAgEDAwIEAwUFBAQAAAF9AQIDAAQRBRIhMUEGE1FhByJxFDKBkaEII0KxwRVS0fAkM2JyggkKFhcYGRolJicoKSo0NTY3ODk6Q0RFRkdISUpTVFVWV1hZWmNkZWZnaGlqc3R1dnd4eXqDhIWGh4iJipKTlJWWl5iZmqKjpKWmp6ipqrKztLW2t7i5usLDxMXGx8jJytLT1NXW19jZ2uHi4+Tl5ufo6erx8vP09fb3+Pn6/8QAHwEAAwEBAQEBAQEBAQAAAAAAAAECAwQFBgcICQoL/8QAtREAAgECBAQDBAcFBAQAAQJ3AAECAxEEBSExBhJBUQdhcRMiMoEIFEKRobHBCSMzUvAVYnLRChYkNOEl8RcYGRomJygpKjU2Nzg5OkNERUZHSElKU1RVVldYWVpjZGVmZ2hpanN0dXZ3eHl6goOEhYaHiImKkpOUlZaXmJmaoqOkpaanqKmqsrO0tba3uLm6wsPExcbHyMnK0tPU1dbX2Nna4uPk5ebn6Onq8vP09fb3+Pn6/9oADAMBAAIRAxEAPwD8X6KKK/ynP+/gKKKKAP6UKKKK/wAtz/wrwooooA/g3ooor/2mD/qwCiiigD/cQooor/gHP6oCiiigD//Z";

    /// <summary>The JPEG with an APP1 Exif segment carrying one IFD0 entry, Orientation (0x0112, SHORT) = <paramref name="orientation"/>,
    /// inserted right after the 20-byte SOI + JFIF APP0 header.</summary>
    private static byte[] WithOrientation(ushort orientation)
    {
        byte[] jpeg = Convert.FromBase64String(QuadrantsJpeg);
        byte[] app1 =
        [
            0xFF, 0xE1, 0x00, 0x22,                               // APP1, length 34
            (byte)'E', (byte)'x', (byte)'i', (byte)'f', 0, 0,     // "Exif\0\0"
            (byte)'M', (byte)'M', 0x00, 0x2A, 0, 0, 0, 8,         // big-endian TIFF header, IFD0 at offset 8
            0, 1,                                                 // one entry
            0x01, 0x12, 0x00, 0x03, 0, 0, 0, 1,                   // tag 274 Orientation, SHORT, count 1
            (byte)(orientation >> 8), (byte)orientation, 0, 0,    // value (left-justified)
            0, 0, 0, 0,                                           // no next IFD
        ];
        const int at = 20;
        var r = new byte[jpeg.Length + app1.Length];
        jpeg.AsSpan(0, at).CopyTo(r);
        app1.CopyTo(r, at);
        jpeg.AsSpan(at).CopyTo(r.AsSpan(at + app1.Length));
        return r;
    }

    private static char Quadrant(byte[] px, int w, int h, int qx, int qy)
    {
        int x = qx == 0 ? w / 4 : 3 * w / 4, y = qy == 0 ? h / 4 : 3 * h / 4;
        int i = (y * w + x) * 4;
        byte b = px[i], g = px[i + 1], r = px[i + 2];
        if (r > 200 && g > 200 && b > 200) return 'W';
        if (r > 200 && g < 60 && b < 60) return 'R';
        if (g > 200 && r < 60 && b < 60) return 'G';
        if (b > 200 && r < 60 && g < 60) return 'B';
        return '?';
    }

    // Expected display quadrants "TL TR BL BR" per EXIF orientation, from the stored "R G B W".
    [Theory]
    [InlineData((ushort)1, 64, 32, "RGBW")]   // normal
    [InlineData((ushort)2, 64, 32, "GRWB")]   // mirror horizontal
    [InlineData((ushort)3, 64, 32, "WBGR")]   // rotate 180
    [InlineData((ushort)4, 64, 32, "BWRG")]   // mirror vertical
    [InlineData((ushort)5, 32, 64, "RBGW")]   // transpose
    [InlineData((ushort)6, 32, 64, "BRWG")]   // rotate 90 CW (portrait phone photo)
    [InlineData((ushort)7, 32, 64, "WGBR")]   // transverse
    [InlineData((ushort)8, 32, 64, "GWRB")]   // rotate 270 CW
    public void Exif_orientation_is_applied_and_the_dims_are_the_display_dims(ushort orientation, int expectW, int expectH, string expect)
    {
        using var codec = new WicImageCodec();
        var dst = new byte[64 * 64 * 4];
        Assert.True(codec.DecodeConstrained(WithOrientation(orientation), 64, 64, dst, out int w, out int h));
        Assert.Equal((expectW, expectH), (w, h));
        string got = new([Quadrant(dst, w, h, 0, 0), Quadrant(dst, w, h, 1, 0), Quadrant(dst, w, h, 0, 1), Quadrant(dst, w, h, 1, 1)]);
        Assert.Equal(expect, got);
    }

    [Fact]
    public void A_rotated_source_fits_the_target_box_by_its_display_aspect()
    {
        // Stored 64x32, Orientation=6 → displayed 32x64. Into a 16x16 box the contain-fit is 8x16, not 16x8.
        using var codec = new WicImageCodec();
        var dst = new byte[16 * 16 * 4];
        Assert.True(codec.DecodeConstrained(WithOrientation(6), 16, 16, dst, out int w, out int h));
        Assert.Equal((8, 16), (w, h));
        Assert.Equal('B', Quadrant(dst, w, h, 0, 0));
        Assert.Equal('G', Quadrant(dst, w, h, 1, 1));
    }
}
