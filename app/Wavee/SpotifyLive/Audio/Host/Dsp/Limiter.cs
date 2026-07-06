namespace Wavee.SpotifyLive.Audio.Host.Dsp;

public sealed class Limiter
{
    public float Ceiling { get; set; } = 0.999f;

    public void Process(Span<float> interleaved)
    {
        float ceiling = Math.Clamp(Ceiling, 0.1f, 1f);
        for (int i = 0; i < interleaved.Length; i++)
        {
            float v = interleaved[i];
            if (!float.IsFinite(v)) interleaved[i] = 0f;
            else if (v > ceiling) interleaved[i] = ceiling;
            else if (v < -ceiling) interleaved[i] = -ceiling;
        }
    }
}
