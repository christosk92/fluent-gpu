namespace FluentGpu.Windows.Wasapi;

/// <summary>
/// WASAPI clock math (spec §7.6) — the pure conversions between <c>IAudioClock</c> device units and the engine's
/// frame/100-ns domain. Kept COM-free so the position logic is unit-testable against a FAKE clock (no real device):
/// <c>IAudioClock::GetPosition</c> gives a position in <c>GetFrequency</c> units where <c>position / frequency</c> is the
/// stream time in SECONDS, and a <c>QPCPosition</c> already in 100-ns units; <c>GetStreamLatency</c> is in 100-ns units.
/// </summary>
public static class WasapiPositionMath
{
    /// <summary>Played frames in the mix-rate domain from a device <paramref name="position"/> and its
    /// <paramref name="frequency"/> (<c>position/frequency</c> = seconds). Uses <see cref="decimal"/> to avoid overflow /
    /// precision loss over multi-hour streams.</summary>
    public static long PlayedFrames(ulong position, ulong frequency, int mixRate)
    {
        if (frequency == 0 || mixRate <= 0) return 0;
        // seconds = position / frequency ; frames = seconds * mixRate.
        return (long)((decimal)position * mixRate / frequency);
    }

    /// <summary>The QPC timestamp (already 100-ns units) as the engine's clock tick.</summary>
    public static long QpcTo100ns(ulong qpcPosition) => (long)qpcPosition;

    /// <summary>Stream latency (a <c>GetStreamLatency</c> value in 100-ns units) converted to frames at
    /// <paramref name="mixRate"/>.</summary>
    public static long LatencyFrames(long hnsLatency, int mixRate)
    {
        if (mixRate <= 0 || hnsLatency <= 0) return 0;
        return (long)(hnsLatency / 1e7 * mixRate);
    }
}
