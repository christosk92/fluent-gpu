using System;

namespace FluentGpu.Media;

/// <summary>Frame-domain de-click envelope, retargeted from its instantaneous gain.</summary>
public struct TransportRamp
{
    private float _from, _to;
    private long _start, _end;

    /// <summary>Create an envelope with an initial constant attenuation.</summary>
    public TransportRamp(float initial) { _from = _to = initial; _start = _end = 0; }
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
