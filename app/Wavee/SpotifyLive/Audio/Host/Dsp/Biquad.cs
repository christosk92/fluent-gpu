namespace Wavee.SpotifyLive.Audio.Host.Dsp;

public struct Biquad
{
    float _b0, _b1, _b2, _a1, _a2;
    float _z1, _z2;

    public static Biquad Peaking(float sampleRate, float frequency, float q, float gainDb)
    {
        if (sampleRate <= 0 || frequency <= 0 || q <= 0 || gainDb == 0f)
            return Identity;

        frequency = Math.Clamp(frequency, 1f, sampleRate * 0.49f);
        double a = Math.Pow(10.0, gainDb / 40.0);
        double w0 = 2.0 * Math.PI * frequency / sampleRate;
        double alpha = Math.Sin(w0) / (2.0 * q);
        double cos = Math.Cos(w0);

        double b0 = 1.0 + alpha * a;
        double b1 = -2.0 * cos;
        double b2 = 1.0 - alpha * a;
        double a0 = 1.0 + alpha / a;
        double a1 = -2.0 * cos;
        double a2 = 1.0 - alpha / a;

        return new Biquad
        {
            _b0 = (float)(b0 / a0),
            _b1 = (float)(b1 / a0),
            _b2 = (float)(b2 / a0),
            _a1 = (float)(a1 / a0),
            _a2 = (float)(a2 / a0),
        };
    }

    public static Biquad Identity => new() { _b0 = 1f };

    public float Process(float x)
    {
        float y = _b0 * x + _z1;
        _z1 = _b1 * x - _a1 * y + _z2;
        _z2 = _b2 * x - _a2 * y;
        if (float.IsFinite(y)) return y;
        Reset();
        return 0f;
    }

    public void Reset()
    {
        _z1 = 0f;
        _z2 = 0f;
    }
}
