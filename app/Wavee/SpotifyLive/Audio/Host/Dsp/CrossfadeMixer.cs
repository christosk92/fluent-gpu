namespace Wavee.SpotifyLive.Audio.Host.Dsp;

public static class CrossfadeMixer
{
    public static void MixEqualPower(ReadOnlySpan<float> outgoing, ReadOnlySpan<float> incoming, Span<float> destination, float progress)
    {
        int n = Math.Min(destination.Length, Math.Min(outgoing.Length, incoming.Length));
        float p = Math.Clamp(progress, 0f, 1f);
        float outGain = (float)Math.Cos(p * Math.PI * 0.5);
        float inGain = (float)Math.Sin(p * Math.PI * 0.5);
        for (int i = 0; i < n; i++)
            destination[i] = outgoing[i] * outGain + incoming[i] * inGain;
        if (destination.Length > n) destination[n..].Clear();
    }
}
