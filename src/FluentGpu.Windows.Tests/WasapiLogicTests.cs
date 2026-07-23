using System.Buffers.Binary;
using FluentGpu.Media;
using FluentGpu.Windows.Wasapi;
using Xunit;

namespace FluentGpu.Windows.Tests;

/// <summary>
/// M2 tests for the WASAPI leaf's HEADLESSLY-testable logic (spec §7.1/§7.6) — the format negotiation + clock math —
/// against a FAKE device description (no real WASAPI / IAudioClient is created; that path is on-box only). The COM
/// plumbing (<see cref="WasapiAudioDevice"/>/<see cref="MmDeviceWatcher"/>) is exercised by the user on-box; these pure
/// helpers are what the negotiation/position correctness rides on.
/// </summary>
public sealed class WasapiLogicTests
{
    // ── format negotiation (spec §7.1: one fixed internal mix format, device opens once) ─────────────────────────────

    [Theory]
    [InlineData(48000)]
    [InlineData(44100)]
    [InlineData(96000)]
    public void Negotiate_AdoptsDeviceRate_FixedStereo(int rate)
    {
        var fmt = WasapiFormatNegotiation.Negotiate(new DeviceFormatDesc(rate, Channels: 2, BitsPerSample: 32, IsFloat: true));
        Assert.Equal(rate, fmt.SampleRate);
        Assert.Equal(2, fmt.Channels);
    }

    [Fact]
    public void Negotiate_ForcesStereo_EvenWhenDeviceIsMultichannel()
    {
        var fmt = WasapiFormatNegotiation.Negotiate(new DeviceFormatDesc(48000, Channels: 6, BitsPerSample: 32, IsFloat: true));
        Assert.Equal(2, fmt.Channels);
    }

    [Fact]
    public void Negotiate_FallsBackTo48k_OnBadRate()
    {
        var fmt = WasapiFormatNegotiation.Negotiate(new DeviceFormatDesc(0, 2, 32, true));
        Assert.Equal(48000, fmt.SampleRate);
    }

    [Fact]
    public void CanWriteFloatDirectly_OnlyForFloat32()
    {
        Assert.True(WasapiFormatNegotiation.CanWriteFloatDirectly(new DeviceFormatDesc(48000, 2, 32, IsFloat: true)));
        Assert.False(WasapiFormatNegotiation.CanWriteFloatDirectly(new DeviceFormatDesc(48000, 2, 16, IsFloat: false)));
        Assert.False(WasapiFormatNegotiation.CanWriteFloatDirectly(new DeviceFormatDesc(48000, 2, 24, IsFloat: false)));
    }

    [Fact]
    public void NeedsChannelConform_WhenNotStereo()
    {
        Assert.False(WasapiFormatNegotiation.NeedsChannelConform(new DeviceFormatDesc(48000, 2, 32, true)));
        Assert.True(WasapiFormatNegotiation.NeedsChannelConform(new DeviceFormatDesc(48000, 6, 32, true)));
        Assert.True(WasapiFormatNegotiation.NeedsChannelConform(new DeviceFormatDesc(48000, 1, 32, true)));
    }

    // ── sample conversion for non-float endpoints (spec §7.1; the leaf's WriteConverted rides on these) ───────────────
    // Only the pure scalar scale is unit-tested here; the COM GetBuffer/pointer write itself stays on-box-only, as above.

    [Fact]
    public void ToInt16_ScalesToFullScale_AndClampedEndsMap()
    {
        Assert.Equal((short)0, WasapiFormatNegotiation.ToInt16(0f));
        Assert.Equal((short)32767, WasapiFormatNegotiation.ToInt16(1f));
        Assert.Equal((short)-32767, WasapiFormatNegotiation.ToInt16(-1f));
        Assert.Equal((short)16383, WasapiFormatNegotiation.ToInt16(0.5f));   // 0.5 * 32767 = 16383.5 → truncates to 16383
    }

    [Fact]
    public void ToInt32_ScalesToFullScale_WithoutOverflow()
    {
        Assert.Equal(0, WasapiFormatNegotiation.ToInt32(0f));
        Assert.Equal(2147483647, WasapiFormatNegotiation.ToInt32(1f));
        Assert.Equal(-2147483647, WasapiFormatNegotiation.ToInt32(-1f));
    }

    [Fact]
    public void ToInt24_PacksLittleEndianBytes_AtFullScale()
    {
        // Full-scale +1 → 0x7FFFFF; the leaf writes the low 3 bytes little-endian (FF FF 7F).
        int v = WasapiFormatNegotiation.ToInt24(1f);
        Assert.Equal(0x7FFFFF, v);
        Assert.Equal(0xFF, (byte)v);
        Assert.Equal(0xFF, (byte)(v >> 8));
        Assert.Equal(0x7F, (byte)(v >> 16));

        // Full-scale -1 → -8388607 (0xFF800001); low 3 bytes little-endian (01 00 80).
        int n = WasapiFormatNegotiation.ToInt24(-1f);
        Assert.Equal(-8388607, n);
        Assert.Equal(0x01, (byte)n);
        Assert.Equal(0x00, (byte)(n >> 8));
        Assert.Equal(0x80, (byte)(n >> 16));

        Assert.Equal(0, WasapiFormatNegotiation.ToInt24(0f));
    }

    // ── the full byte-layout converting write (WasapiFormatNegotiation.ConvertBlock) ──────────────────────────────────
    // The COM Write's GetBuffer pointer is on-box-only, so Write delegates its non-float byte layout + scale + clamp +
    // channel conform to this pure static; here we drive it against a managed byte[] (a fake device buffer). The write path
    // picks it precisely when CanWriteFloatDirectly is false — the 16-bit device case — so confirm that gate too.

    [Fact]
    public void CanWriteFloatDirectly_False_For16BitStereo_SelectsConvertBlock()
        => Assert.False(WasapiFormatNegotiation.CanWriteFloatDirectly(new DeviceFormatDesc(48000, 2, 16, IsFloat: false)));

    [Fact]
    public void ConvertBlock_16BitStereo_WritesLittleEndianInterleaved()
    {
        // 16-bit stereo device: 4 B/frame, 2 samples/frame. Two frames: {+1,-1} then {0.5, 0}.
        var src = new float[] { 1f, -1f, 0.5f, 0f };
        var dst = new byte[2 * 4];
        bool ok = WasapiFormatNegotiation.ConvertBlock(dst, src, frameOffset: 0, frames: 2, devCh: 2, devBits: 16, strideBytes: 4);
        Assert.True(ok);
        // frame 0: L=+1 → 32767 (0x7FFF LE = FF 7F); R=-1 → -32767 (0x8001 LE = 01 80)
        Assert.Equal(new byte[] { 0xFF, 0x7F, 0x01, 0x80 }, dst[0..4]);
        // frame 1: L=0.5 → 16383 (0x3FFF LE = FF 3F); R=0 → 0
        Assert.Equal(new byte[] { 0xFF, 0x3F, 0x00, 0x00 }, dst[4..8]);
    }

    [Fact]
    public void ConvertBlock_16Bit_ClampsOutOfRangeAndGuardsNonFinite()
    {
        // A limiter-miss transient (>±1) must clamp to full-scale (not overflow the truncating cast); NaN/Inf → silence.
        var src = new float[] { 2f, -2f, float.NaN, float.PositiveInfinity };
        var dst = new byte[2 * 4];
        WasapiFormatNegotiation.ConvertBlock(dst, src, 0, 2, devCh: 2, devBits: 16, strideBytes: 4);
        Assert.Equal((short)32767, BinaryPrimitives.ReadInt16LittleEndian(dst.AsSpan(0)));   // +2 → +1 → 32767
        Assert.Equal((short)-32767, BinaryPrimitives.ReadInt16LittleEndian(dst.AsSpan(2)));  // -2 → -1 → -32767
        Assert.Equal((short)0, BinaryPrimitives.ReadInt16LittleEndian(dst.AsSpan(4)));       // NaN → 0
        Assert.Equal((short)0, BinaryPrimitives.ReadInt16LittleEndian(dst.AsSpan(6)));       // +Inf → 0
    }

    [Fact]
    public void ConvertBlock_32BitStereo_WritesFullScaleLittleEndian()
    {
        var src = new float[] { 1f, -1f };
        var dst = new byte[1 * 8];   // 32-bit stereo = 8 B/frame
        bool ok = WasapiFormatNegotiation.ConvertBlock(dst, src, 0, 1, devCh: 2, devBits: 32, strideBytes: 8);
        Assert.True(ok);
        Assert.Equal(2147483647, BinaryPrimitives.ReadInt32LittleEndian(dst.AsSpan(0)));
        Assert.Equal(-2147483647, BinaryPrimitives.ReadInt32LittleEndian(dst.AsSpan(4)));
    }

    [Fact]
    public void ConvertBlock_24BitStereo_PacksLittleEndian3Bytes()
    {
        var src = new float[] { 1f, -1f };
        var dst = new byte[1 * 6];   // 24-bit stereo = 6 B/frame
        bool ok = WasapiFormatNegotiation.ConvertBlock(dst, src, 0, 1, devCh: 2, devBits: 24, strideBytes: 6);
        Assert.True(ok);
        Assert.Equal(new byte[] { 0xFF, 0xFF, 0x7F }, dst[0..3]);   // +1 → 0x7FFFFF LE
        Assert.Equal(new byte[] { 0x01, 0x00, 0x80 }, dst[3..6]);   // -1 → -8388607 (0xFF800001) low 3 LE
    }

    [Fact]
    public void ConvertBlock_MonoDevice_DownmixesLR()
    {
        // Mono 16-bit device: 2 B/frame, one sample = (L+R)/2 = (1+0)/2 = 0.5 → 16383.
        var src = new float[] { 1f, 0f };
        var dst = new byte[1 * 2];
        WasapiFormatNegotiation.ConvertBlock(dst, src, 0, 1, devCh: 1, devBits: 16, strideBytes: 2);
        Assert.Equal((short)16383, BinaryPrimitives.ReadInt16LittleEndian(dst.AsSpan(0)));
    }

    [Fact]
    public void ConvertBlock_MultichannelDevice_WritesLR_ZerosExtras()
    {
        // A 6-channel 16-bit device (12 B/frame): L/R written, channels 2..5 actively zeroed.
        var src = new float[] { 1f, -1f };
        var dst = new byte[1 * 12];
        for (int i = 0; i < dst.Length; i++) dst[i] = 0xAA;   // prime so the zero-fill is proven, not incidental
        WasapiFormatNegotiation.ConvertBlock(dst, src, 0, 1, devCh: 6, devBits: 16, strideBytes: 12);
        Assert.Equal((short)32767, BinaryPrimitives.ReadInt16LittleEndian(dst.AsSpan(0)));
        Assert.Equal((short)-32767, BinaryPrimitives.ReadInt16LittleEndian(dst.AsSpan(2)));
        for (int c = 2; c < 6; c++) Assert.Equal((short)0, BinaryPrimitives.ReadInt16LittleEndian(dst.AsSpan(c * 2)));
    }

    [Fact]
    public void ConvertBlock_HonorsFrameOffset()
    {
        // src holds 3 frames; convert only the 2nd (offset 1) into a one-frame buffer.
        var src = new float[] { 0f, 0f, 1f, -1f, 0.5f, 0.5f };
        var dst = new byte[4];
        WasapiFormatNegotiation.ConvertBlock(dst, src, frameOffset: 1, frames: 1, devCh: 2, devBits: 16, strideBytes: 4);
        Assert.Equal((short)32767, BinaryPrimitives.ReadInt16LittleEndian(dst.AsSpan(0)));
        Assert.Equal((short)-32767, BinaryPrimitives.ReadInt16LittleEndian(dst.AsSpan(2)));
    }

    [Fact]
    public void ConvertBlock_UnsupportedBitDepth_WritesSilence_ReturnsFalse()
    {
        // e.g. float64 endpoint: the leaf must write silence (never overrun/garbage) and signal false so Write warns once.
        var src = new float[] { 1f, -1f };
        var dst = new byte[1 * 8];
        for (int i = 0; i < dst.Length; i++) dst[i] = 0xAA;
        bool ok = WasapiFormatNegotiation.ConvertBlock(dst, src, 0, 1, devCh: 2, devBits: 64, strideBytes: 8);
        Assert.False(ok);
        Assert.All(dst, b => Assert.Equal(0, b));
    }

    // ── clock math (spec §7.6: IAudioClock position/frequency = seconds; QPC is 100-ns) ──────────────────────────────

    [Fact]
    public void PlayedFrames_ConvertsPositionOverFrequency_ToMixRateFrames()
    {
        // 1 second of stream: position == frequency ⇒ mixRate frames.
        Assert.Equal(48000, WasapiPositionMath.PlayedFrames(position: 48000, frequency: 48000, mixRate: 48000));
        // A 44.1k device clock projected into a 48k mix domain: 1 s ⇒ 48000 frames.
        Assert.Equal(48000, WasapiPositionMath.PlayedFrames(position: 44100, frequency: 44100, mixRate: 48000));
    }

    [Fact]
    public void PlayedFrames_IsExactOverMultiHourStreams()
    {
        // 3 hours at 48k with no precision loss (decimal path).
        ulong pos = 48000UL * 3600UL * 3UL;
        Assert.Equal(48000L * 3600L * 3L, WasapiPositionMath.PlayedFrames(pos, 48000, 48000));
    }

    [Fact]
    public void PlayedFrames_ZeroFrequency_IsZero() => Assert.Equal(0, WasapiPositionMath.PlayedFrames(1000, 0, 48000));

    [Fact]
    public void LatencyFrames_ConvertsHundredNsToFrames()
    {
        Assert.Equal(480, WasapiPositionMath.LatencyFrames(hnsLatency: 100_000, mixRate: 48000));   // 10 ms @ 48k = 480 frames
        Assert.Equal(0, WasapiPositionMath.LatencyFrames(0, 48000));
    }

    [Fact]
    public void QpcTo100ns_IsIdentity() => Assert.Equal(1_234_567L, WasapiPositionMath.QpcTo100ns(1_234_567UL));
}
