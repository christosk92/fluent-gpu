using System;
using FluentGpu.Foundation;
using FluentGpu.WindowsApi.Shell;
using Xunit;

namespace FluentGpu.Windows.Tests;

/// <summary>
/// The pure pixel decisions behind <see cref="NotifyIcon.SetIcon(ReadOnlySpan{byte}, int, int)"/> and
/// <see cref="TrayIconArt"/>: argument validation, coverage → premultiplied tint, source-over, the badge's layout and pill
/// coverage, the knock-out moat, badge text trimming and contrast, and the icon mask. No GDI, no DirectWrite.
/// </summary>
public sealed class TrayIconPixelsTests
{
    // ── validation ──────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_span_that_is_not_w_by_h_by_4_is_an_argument_exception()
    {
        var ex = Assert.Throws<ArgumentException>(() => TrayIconPixels.Validate(new byte[16 * 16 * 4 - 1], 16, 16, "px"));
        Assert.Equal("px", ex.ParamName);
        TrayIconPixels.Validate(new byte[16 * 16 * 4], 16, 16, "px");   // exact: no throw
    }

    [Theory]
    [InlineData(0, 16)]
    [InlineData(16, 0)]
    [InlineData(257, 16)]
    [InlineData(-1, -1)]
    public void A_side_outside_1_to_256_is_out_of_range(int w, int h)
        => Assert.Throws<ArgumentOutOfRangeException>(() => TrayIconPixels.Validate(new byte[Math.Max(0, w * h * 4)], w, h, "px"));

    [Fact]
    public void IsValid_mirrors_Validate_without_throwing()
    {
        Assert.True(TrayIconPixels.IsValid(new byte[20 * 20 * 4], 20, 20));
        Assert.False(TrayIconPixels.IsValid(new byte[20 * 20 * 4], 20, 21));
        Assert.False(TrayIconPixels.IsValid(new byte[0], 0, 0));
    }

    [Fact]
    public void Premultiplied_means_no_colour_channel_above_its_alpha()
    {
        Assert.True(TrayIconPixels.IsPremultiplied([0, 0, 0, 0, 10, 20, 30, 30, 255, 255, 255, 255]));
        Assert.False(TrayIconPixels.IsPremultiplied([1, 0, 0, 0]));        // alpha 0 with colour
        Assert.False(TrayIconPixels.IsPremultiplied([0, 0, 200, 128]));    // red above alpha
    }

    // ── tint + composite ────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Zero_coverage_is_transparent_black()
    {
        Span<byte> px = stackalloc byte[4];
        TrayIconPixels.Tint(0, new ColorF(1f, 0.5f, 0.25f, 1f), px);
        Assert.Equal(new byte[] { 0, 0, 0, 0 }, px.ToArray());
    }

    [Fact]
    public void Full_coverage_white_is_opaque_white()
    {
        Span<byte> px = stackalloc byte[4];
        TrayIconPixels.Tint(255, TrayIconPixels.DarkTaskbarInk, px);
        Assert.Equal(new byte[] { 255, 255, 255, 255 }, px.ToArray());
    }

    [Fact]
    public void Half_coverage_scales_alpha_and_premultiplies_the_colour()
    {
        Span<byte> px = stackalloc byte[4];
        TrayIconPixels.Tint(128, TrayIconPixels.LightTaskbarInk, px);   // #1B1B1B, opaque
        Assert.Equal(128, px[3]);
        // 0x1B × 128/255 = 13.55 → 14 on every channel.
        Assert.Equal(14, px[0]); Assert.Equal(14, px[1]); Assert.Equal(14, px[2]);
    }

    [Fact]
    public void Ink_alpha_multiplies_with_coverage_and_channels_land_in_BGR_order()
    {
        Span<byte> px = stackalloc byte[4];
        TrayIconPixels.Tint(255, new ColorF(1f, 0f, 0f, 0.5f), px);   // half-transparent pure red
        Assert.Equal(new byte[] { 0, 0, 128, 128 }, px.ToArray());
    }

    [Fact]
    public void Source_over_clips_to_the_destination_and_blends_premultiplied()
    {
        var dst = new byte[2 * 2 * 4];
        // A 2×2 full-coverage mask placed at (1,1): only the bottom-right destination pixel is inside.
        TrayIconPixels.SourceOver(dst, 2, 2, [255, 255, 255, 255], 2, 2, 1, 1, TrayIconPixels.DarkTaskbarInk);
        Assert.Equal(new byte[] { 255, 255, 255, 255 }, dst.AsSpan(12, 4).ToArray());
        Assert.All(dst.AsSpan(0, 12).ToArray(), b => Assert.Equal(0, b));

        // Opaque black over opaque white replaces it; the result stays premultiplied.
        TrayIconPixels.SourceOver(dst, 2, 2, [255], 1, 1, 1, 1, new ColorF(0f, 0f, 0f, 1f));
        Assert.Equal(new byte[] { 0, 0, 0, 255 }, dst.AsSpan(12, 4).ToArray());
        Assert.True(TrayIconPixels.IsPremultiplied(dst));
    }

    [Fact]
    public void Ink_defaults_follow_the_taskbar()
    {
        Assert.Equal(new ColorF(1f, 1f, 1f, 1f), TrayIconPixels.DefaultInk(lightTaskbar: false));
        Assert.Equal(ColorF.FromRgba(0x1B, 0x1B, 0x1B), TrayIconPixels.DefaultInk(lightTaskbar: true));
    }

    [Theory]
    [InlineData(0, 16)]
    [InlineData(8, 16)]
    [InlineData(24, 24)]
    [InlineData(96, 48)]
    public void Art_sizes_clamp_to_16_through_48(int requested, int expected)
        => Assert.Equal(expected, TrayIconPixels.ClampArtSize(requested));

    [Theory]
    [InlineData(0, 4, 0, 16, 6)]     // 4 px of ink in 16: shift right by 6
    [InlineData(-2, 13, 0, 16, 2)]   // 15 px of ink starting at -2: the odd px goes right
    [InlineData(3, 8, 7, 9, 6)]      // 5 px of ink centred in a 9 px pill at x=7 → starts at 9
    public void Centering_is_a_whole_pixel_shift(int inkStart, int inkEnd, int boxStart, int boxLength, int shift)
        => Assert.Equal(shift, TrayIconPixels.CenterOffset(inkStart, inkEnd, boxStart, boxLength));

    // ── badge ───────────────────────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(16, 9)]
    [InlineData(20, 11)]
    [InlineData(24, 14)]
    [InlineData(32, 18)]
    [InlineData(48, 27)]
    public void The_badge_is_nine_sixteenths_of_the_icon_tall(int size, int height)
        => Assert.Equal(height, TrayIconPixels.BadgeHeight(size));

    [Theory]
    //          size inkW   x   y   w   h
    [InlineData(16,  0,     7,  7,  9,  9)]    // no text: a circle in the corner
    [InlineData(16,  5,     7,  7,  9,  9)]    // one digit fits the circle (5 + 2×2 = 9)
    [InlineData(16, 10,     2,  7, 14,  9)]    // "42": grows left
    [InlineData(16, 30,     0,  7, 16,  9)]    // never wider than the icon
    [InlineData(32, 20,     4, 14, 28, 18)]
    [InlineData(48,  8,    21, 21, 27, 27)]
    public void The_badge_hugs_the_bottom_right_corner(int size, int inkW, int x, int y, int w, int h)
        => Assert.Equal(new TrayBadgeRect(x, y, w, h), TrayIconPixels.BadgeRect(size, inkW));

    [Fact]
    public void The_pill_is_solid_inside_and_clear_beyond_its_rounded_corners()
    {
        var r = new TrayBadgeRect(2, 7, 14, 9);
        Assert.Equal(1f, TrayIconPixels.PillCoverage(9f, 11.5f, r));          // centre
        Assert.Equal(1f, TrayIconPixels.PillCoverage(6.5f, 11.5f, r));        // inside the left cap
        Assert.Equal(0f, TrayIconPixels.PillCoverage(2.5f, 7.5f, r));         // top-left corner pixel: outside the cap
        Assert.Equal(0f, TrayIconPixels.PillCoverage(9f, 4f, r));             // above it
        float edge = TrayIconPixels.PillCoverage(9f, 7.5f, r);                // top edge row: partial
        Assert.InRange(edge, 0.5f, 1f);
        Assert.True(TrayIconPixels.PillCoverage(9f, 6.5f, r, inflate: 1f) > 0f);   // the moat reaches a row above
    }

    [Fact]
    public void FillPill_paints_only_inside_the_pill()
    {
        const int size = 16;
        var px = new byte[size * size * 4];
        TrayBadgeRect r = TrayIconPixels.BadgeRect(size, 0);
        TrayIconPixels.FillPill(px, size, size, r, TrayIconPixels.DefaultBadgeFill);
        int centre = ((r.Y + r.Height / 2) * size + r.X + r.Width / 2) * 4;
        Assert.Equal(255, px[centre + 3]);
        Assert.Equal(0, px[3]);                                   // the icon's top-left is untouched
        Assert.Equal(0, px[((r.Y) * size + r.X) * 4 + 3]);        // the pill rect's own corner is rounded away
        Assert.True(TrayIconPixels.IsPremultiplied(px));
    }

    [Fact]
    public void KnockOut_clears_the_glyph_under_and_around_the_badge_and_leaves_the_rest()
    {
        const int size = 16;
        var px = new byte[size * size * 4];
        px.AsSpan().Fill(255);                                    // an opaque white glyph everywhere
        TrayBadgeRect r = TrayIconPixels.BadgeRect(size, 0);
        TrayIconPixels.KnockOut(px, size, size, r, ring: 1f);
        Assert.Equal(0, px[((r.Y + r.Height / 2) * size + r.X + r.Width / 2) * 4 + 3]);   // under the badge
        Assert.Equal(255, px[3]);                                                          // far corner
        Assert.True(TrayIconPixels.IsPremultiplied(px));
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("   ", null)]
    [InlineData(" 7 ", "7")]
    [InlineData("100", "100")]
    [InlineData("1000", "100")]
    [InlineData("12😀", "12")]   // never ends on half a surrogate pair
    public void Badge_text_is_trimmed_to_three_chars(string? input, string? expected)
        => Assert.Equal(expected, TrayIconPixels.TrimBadge(input));

    [Fact]
    public void Badge_text_contrasts_with_its_fill()
    {
        Assert.Equal(TrayIconPixels.DarkTaskbarInk, TrayIconPixels.ContrastInk(TrayIconPixels.DefaultBadgeFill));   // white on red
        Assert.Equal(TrayIconPixels.LightTaskbarInk, TrayIconPixels.ContrastInk(ColorF.FromRgba(0xFF, 0xD3, 0x3D)));  // dark on yellow
    }

    // ── the icon's bitmaps ─────────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(1, 2)]
    [InlineData(16, 2)]
    [InlineData(17, 4)]
    [InlineData(48, 6)]
    public void Mask_rows_pad_to_a_word(int width, int stride) => Assert.Equal(stride, TrayIconPixels.MaskStride(width));

    [Fact]
    public void The_mask_marks_exactly_the_fully_transparent_pixels_msb_first()
    {
        const int w = 3, h = 2;
        var px = new byte[w * h * 4];
        px[(0 * w + 1) * 4 + 3] = 1;      // (1,0) barely covered
        px[(1 * w + 0) * 4 + 3] = 255;    // (0,1) opaque
        var mask = new byte[TrayIconPixels.MaskStride(w) * h];
        TrayIconPixels.BuildMask(px, w, h, mask);
        Assert.Equal(0b1010_0000, mask[0]);   // row 0: (0) transparent, (1) covered, (2) transparent
        Assert.Equal(0b0110_0000, mask[2]);   // row 1: (0) covered, (1)(2) transparent
    }

    [Fact]
    public void The_colour_bitmap_is_straight_alpha()
    {
        byte[] premul = [64, 32, 0, 128, 0, 0, 0, 0];
        var straight = new byte[premul.Length];
        TrayIconPixels.Unpremultiply(premul, straight);
        Assert.Equal(new byte[] { 128, 64, 0, 128, 0, 0, 0, 0 }, straight);
    }
}
