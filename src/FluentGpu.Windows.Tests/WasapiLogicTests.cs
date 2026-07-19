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
