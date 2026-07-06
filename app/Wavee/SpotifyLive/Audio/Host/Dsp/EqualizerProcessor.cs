using Wavee.Backend.Audio;

namespace Wavee.SpotifyLive.Audio.Host.Dsp;

public sealed class EqualizerProcessor
{
    static readonly float[] s_centers = [31.25f, 62.5f, 125f, 250f, 500f, 1000f, 2000f, 4000f, 8000f, 16000f];
    const float BandQ = 1.4142135f;

    readonly object _gate = new();
    float[] _gains = new float[10];
    Biquad[,] _filters = new Biquad[10, 2];
    int _sampleRate;
    int _channels;
    bool _enabled;
    float _preamp = 1f;
    bool _dirty = true;

    public void Configure(EqualizerSettings settings)
    {
        lock (_gate)
        {
            _enabled = settings.Enabled;
            _preamp = DbToLinear(Math.Clamp(settings.PreampDb, -12f, 12f));
            Array.Clear(_gains);
            var src = settings.GainsDb ?? Array.Empty<float>();
            for (int i = 0; i < Math.Min(_gains.Length, src.Length); i++)
                _gains[i] = Math.Clamp(src[i], -12f, 12f);
            _dirty = true;
        }
    }

    public void Process(Span<float> interleaved, int sampleRate, int channels)
    {
        if (interleaved.Length == 0 || sampleRate <= 0 || channels <= 0) return;

        lock (_gate)
        {
            if (!_enabled && _preamp == 1f) return;
            EnsureFilters(sampleRate, channels);
            for (int i = 0; i < interleaved.Length; i++)
            {
                int ch = i % channels;
                float x = interleaved[i] * _preamp;
                if (_enabled)
                {
                    for (int band = 0; band < _gains.Length; band++)
                        if (_gains[band] != 0f)
                            x = _filters[band, ch].Process(x);
                }
                interleaved[i] = float.IsFinite(x) ? x : 0f;
            }
        }
    }

    void EnsureFilters(int sampleRate, int channels)
    {
        if (!_dirty && _sampleRate == sampleRate && _channels == channels) return;
        _sampleRate = sampleRate;
        _channels = channels;
        _filters = new Biquad[_gains.Length, channels];
        for (int band = 0; band < _gains.Length; band++)
        {
            var prototype = _gains[band] == 0f
                ? Biquad.Identity
                : Biquad.Peaking(sampleRate, s_centers[band], BandQ, _gains[band]);
            for (int ch = 0; ch < channels; ch++)
                _filters[band, ch] = prototype;
        }
        _dirty = false;
    }

    static float DbToLinear(float db) => db == 0f ? 1f : (float)Math.Pow(10.0, db / 20.0);
}
