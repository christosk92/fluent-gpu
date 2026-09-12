using System;

namespace FluentGpu.Media;

/// <summary>Frame-domain de-click envelope, retargeted from its instantaneous gain.</summary>
public struct TransportRamp
{
    private float _from, _to;
    private long _start, _end;
    private long _processedSamples, _identitySamples;

    /// <summary>Create an envelope with an initial constant attenuation.</summary>
    public TransportRamp(float initial) { _from = _to = initial; _start = _end = 0; _processedSamples = _identitySamples = 0; }
    /// <summary>Samples traversed by attenuation (output-thread work census).</summary>
    public readonly long ProcessedSamples => _processedSamples;
    /// <summary>Samples skipped after reaching constant unity.</summary>
    public readonly long IdentitySamples => _identitySamples;

    /// <summary>Apply final attenuation once to a freshly rendered block, never to a retained partial write.</summary>
    public void Apply(Span<float> samples, int frames, int channels, long startFrame)
    {
        if (startFrame >= _end && _to == 1f)
        {
            _identitySamples += (long)frames * channels;
            return;
        }
        _processedSamples += (long)frames * channels;
        for (int frame = 0; frame < frames; frame++)
        {
            float gain = At(startFrame + frame);
            for (int channel = 0; channel < channels; channel++)
                samples[frame * channels + channel] *= gain;
        }
    }
    /// <summary>The frame where the target has been reached.</summary>
    public readonly long EndFrame => _end;
    /// <summary>Evaluate without allocation or mutable per-sample state.</summary>
    public readonly float At(long frame)
    {
        if (frame >= _end) return _to;
        if (frame <= _start) return _from;
        float t = (float)(frame - _start) / (_end - _start);
        return _from + (_to - _from) * t * t * (3f - 2f * t);
    }
    /// <summary>Retarget on the output thread; all channels share this envelope.</summary>
    public void Retarget(float target, long frame, int frames)
    {
        _from = At(frame);
        _to = Math.Clamp(target, 0f, 1f);
        _start = frame;
        _end = frame + Math.Max(0, frames - 1);
    }
}
