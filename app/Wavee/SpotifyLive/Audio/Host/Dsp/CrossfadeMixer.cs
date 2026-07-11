namespace Wavee.SpotifyLive.Audio.Host.Dsp;

public static class CrossfadeMixer
{
    public static (float Outgoing, float Incoming) EqualPowerGains(float progress)
    {
        float p = Math.Clamp(progress, 0f, 1f);
        return ((float)Math.Cos(p * Math.PI * 0.5), (float)Math.Sin(p * Math.PI * 0.5));
    }

    public static void MixEqualPower(ReadOnlySpan<float> outgoing, ReadOnlySpan<float> incoming, Span<float> destination, float progress)
    {
        int n = Math.Min(destination.Length, Math.Min(outgoing.Length, incoming.Length));
        var (outGain, inGain) = EqualPowerGains(progress);
        for (int i = 0; i < n; i++)
            destination[i] = outgoing[i] * outGain + incoming[i] * inGain;
        if (destination.Length > n) destination[n..].Clear();
    }

    /// <summary>Per-sample equal-power crossfade for the single-output decode loop. The fade clock is the queued-frame
    /// domain: frame <paramref name="startFrame"/> is the first mixed frame of this block relative to the overlap start,
    /// so progress advances one step per interleaved frame (all channels of a frame share the same gains). Endpoints:
    /// p=0 → pure outgoing, p≥1 → pure incoming.</summary>
    public static void MixEqualPower(ReadOnlySpan<float> outgoing, ReadOnlySpan<float> incoming, Span<float> destination,
        long startFrame, long fadeFrames, int channels)
    {
        int n = Math.Min(destination.Length, Math.Min(outgoing.Length, incoming.Length));
        if (channels < 1) channels = 1;
        double denom = fadeFrames > 0 ? fadeFrames : 1;
        for (int i = 0; i < n; i++)
        {
            long frame = startFrame + i / channels;
            float p = (float)Math.Clamp(frame / denom, 0.0, 1.0);
            float outGain = (float)Math.Cos(p * Math.PI * 0.5);
            float inGain = (float)Math.Sin(p * Math.PI * 0.5);
            destination[i] = outgoing[i] * outGain + incoming[i] * inGain;
        }
        if (destination.Length > n) destination[n..].Clear();
    }
}
