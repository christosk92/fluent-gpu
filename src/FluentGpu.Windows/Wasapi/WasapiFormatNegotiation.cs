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
}
