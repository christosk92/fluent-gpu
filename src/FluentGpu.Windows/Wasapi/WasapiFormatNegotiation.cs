using System;
using System.Buffers.Binary;
using FluentGpu.Media;

namespace FluentGpu.Windows.Wasapi;

/// <summary>A device shared-mode mix-format description (spec §7.1) — the fields read off <c>WAVEFORMATEX</c>/
/// <c>WAVEFORMATEXTENSIBLE</c>. Pure POD so the negotiation logic is unit-testable against a FAKE device (no real WASAPI).</summary>
public readonly record struct DeviceFormatDesc(int SampleRate, int Channels, int BitsPerSample, bool IsFloat);

/// <summary>
/// WASAPI shared-mode format negotiation (spec §6/§7.1) — "one fixed internal mix format; the device opens once". The
/// engine runs the graph at <c>f32</c>/device-rate/stereo; every source resamples INTO it at the decode edge, so the
/// device never learns sources differed. Pure static logic (no COM) — the <see cref="FluentGpu.Windows"/> Tests exercise
/// it against a fake device.
/// </summary>
public static class WasapiFormatNegotiation
{
    /// <summary>The internal <see cref="MixFormat"/> to run the graph at, given the device's shared-mode mix format. The
    /// device RATE is adopted (shared mode resamples to nothing — we match it); the internal layout is fixed STEREO
    /// (<c>f32</c> implied). A track boundary is a splice, never a device reopen (spec §7.1).</summary>
    public static MixFormat Negotiate(DeviceFormatDesc device)
    {
        int rate = device.SampleRate > 0 ? device.SampleRate : 48000;
        return new MixFormat(rate, 2);
    }

    /// <summary>True when the graph's <c>f32</c> blocks can be presented to the render buffer with a straight copy (the
    /// device mix format is 32-bit IEEE float — the normal shared-mode case). Otherwise a converting write is required.</summary>
    public static bool CanWriteFloatDirectly(DeviceFormatDesc device) => device.IsFloat && device.BitsPerSample == 32;

    /// <summary>True when the device channel count differs from the fixed internal stereo layout, so presentation must
    /// up/down-mix into the device buffer (channels &gt; 2 or mono device).</summary>
    public static bool NeedsChannelConform(DeviceFormatDesc device) => device.Channels != 2;

    // ── Sample conversion (used by the leaf's converting write for non-float endpoints; pure so it's unit-testable) ──────
    // Each input is a graph f32 sample already Sanitize-clamped to ±1, scaled to the integer type's positive full-scale.
    // The clamp guarantees the truncating cast can't overflow. int32 scales in double so ×(2^31-1) stays exact.

    /// <summary>Convert a clamped ±1 f32 sample to signed 16-bit PCM (±32767).</summary>
    public static short ToInt16(float s) => (short)(s * 32767f);

    /// <summary>Convert a clamped ±1 f32 sample to signed 32-bit PCM (±2147483647).</summary>
    public static int ToInt32(float s) => (int)((double)s * 2147483647.0);

    /// <summary>Convert a clamped ±1 f32 sample to a signed 24-bit value (±8388607); the caller packs the low 3 bytes LE.</summary>
    public static int ToInt24(float s) => (int)(s * 8388607f);

    /// <summary>
    /// Convert a block of the graph's interleaved stereo <c>f32</c> samples into a device buffer of <paramref name="devBits"/>-bit
    /// PCM, conforming to the device channel count <paramref name="devCh"/> (write L/R, zero any extras, or downmix to mono) at
    /// the true device frame stride <paramref name="strideBytes"/> (= <c>WAVEFORMATEX.nBlockAlign</c> — never an assumed
    /// 4&#160;B/sample). This is the pure, unit-tested core of the leaf's converting write (<see cref="WasapiAudioDevice"/>.Write's
    /// non-float path): each source sample is finite-guarded (NaN/Inf → 0) and clamped to ±1 before scaling, so the truncating
    /// casts in <see cref="ToInt16"/>/<see cref="ToInt32"/>/<see cref="ToInt24"/> can't overflow. All integer writes are
    /// little-endian (Windows). Handles 16- and 32-bit fully and 24-bit packed; an unsupported bit depth (e.g. float64)
    /// zero-fills the block (silence — never an overrun/garbage) and returns <c>false</c> so the caller can warn once.
    /// Allocation-free: writes into the caller's span (a view over the device GetBuffer pointer on the render path, which runs
    /// inside the <c>AudioTripwire</c> scope).
    /// </summary>
    /// <param name="dst">The device buffer to fill; must be at least <c>frames * strideBytes</c> bytes.</param>
    /// <param name="src">Interleaved stereo <c>f32</c> source (2 samples per frame).</param>
    /// <param name="frameOffset">First source frame to convert (source sample index = <c>(frameOffset + f) * 2</c>).</param>
    /// <param name="frames">Number of frames to convert.</param>
    public static bool ConvertBlock(Span<byte> dst, ReadOnlySpan<float> src, int frameOffset, int frames, int devCh, int devBits, int strideBytes)
    {
        switch (devBits)
        {
            case 16:
                for (int f = 0; f < frames; f++)
                {
                    float l = Clamp(src[(frameOffset + f) * 2]);
                    float r = Clamp(src[(frameOffset + f) * 2 + 1]);
                    int off = f * strideBytes;
                    if (devCh == 1) { BinaryPrimitives.WriteInt16LittleEndian(dst[off..], ToInt16((l + r) * 0.5f)); }
                    else
                    {
                        BinaryPrimitives.WriteInt16LittleEndian(dst[off..], ToInt16(l));
                        BinaryPrimitives.WriteInt16LittleEndian(dst[(off + 2)..], ToInt16(r));
                        for (int c = 2; c < devCh; c++) BinaryPrimitives.WriteInt16LittleEndian(dst[(off + c * 2)..], 0);
                    }
                }
                return true;
            case 32:
                for (int f = 0; f < frames; f++)
                {
                    float l = Clamp(src[(frameOffset + f) * 2]);
                    float r = Clamp(src[(frameOffset + f) * 2 + 1]);
                    int off = f * strideBytes;
                    if (devCh == 1) { BinaryPrimitives.WriteInt32LittleEndian(dst[off..], ToInt32((l + r) * 0.5f)); }
                    else
                    {
                        BinaryPrimitives.WriteInt32LittleEndian(dst[off..], ToInt32(l));
                        BinaryPrimitives.WriteInt32LittleEndian(dst[(off + 4)..], ToInt32(r));
                        for (int c = 2; c < devCh; c++) BinaryPrimitives.WriteInt32LittleEndian(dst[(off + c * 4)..], 0);
                    }
                }
                return true;
            case 24:
                for (int f = 0; f < frames; f++)
                {
                    float l = Clamp(src[(frameOffset + f) * 2]);
                    float r = Clamp(src[(frameOffset + f) * 2 + 1]);
                    int off = f * strideBytes;
                    if (devCh == 1) { PackI24(dst[off..], (l + r) * 0.5f); }
                    else
                    {
                        PackI24(dst[off..], l);
                        PackI24(dst[(off + 3)..], r);
                        for (int c = 2; c < devCh; c++) { var z = dst[(off + c * 3)..]; z[0] = 0; z[1] = 0; z[2] = 0; }
                    }
                }
                return true;
            default:
                dst[..(frames * strideBytes)].Clear();
                return false;
        }
    }

    // Finite-guard (NaN/Inf → 0) + hard-clamp to ±1 — the correctness half of the leaf's Sanitize (the render path keeps a
    // Sanitize that also feeds the TEMP diagnostic counters). Guarantees the truncating casts above can't overflow.
    private static float Clamp(float s)
    {
        if (!float.IsFinite(s)) return 0f;
        if (s > 1f) return 1f;
        if (s < -1f) return -1f;
        return s;
    }

    // Pack a clamped ±1 f32 sample as 24-bit little-endian into the first 3 bytes of dst.
    private static void PackI24(Span<byte> dst, float s)
    {
        int v = ToInt24(s);
        dst[0] = (byte)v; dst[1] = (byte)(v >> 8); dst[2] = (byte)(v >> 16);
    }
}
