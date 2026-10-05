namespace FluentGpu.Windows.Wasapi;

/// <summary>The pure rule behind <see cref="WasapiAudioDevice"/>'s device-underrun count: a write that finds the device queue empty
/// counts only on a RUNNING stream that has taken a full device buffer of audio since it was last started or reset, so a start, a
/// post-seek refill or a resume (queues that legitimately sit empty) never reads as a glitch.</summary>
internal static class DeviceUnderrunRule
{
    public static bool IsUnderrun(uint padding, bool started, long writtenSinceStart, uint bufferFrames)
        => padding == 0 && started && writtenSinceStart > bufferFrames;
}
