using System;
using System.Threading;

namespace FluentGpu.Media;

/// <summary>Deterministic finite-capacity endpoint. Hardware time advances independently from rendering.</summary>
public sealed class BufferedAudioEndpoint : IAudioEndpoint, IBufferedAudioSink, IAudioClockSource
{
    private readonly float[] _queue;
    private readonly float[] _capture;
    private int _read, _write, _padding, _captured;
    private long _written, _played, _nowTicks;
    private bool _started, _invalidated;

    /// <summary>Create a buffered fake without opening an operating-system device.</summary>
    public BufferedAudioEndpoint(MixFormat format, int capacityFrames, int captureFrames = 48000)
    {
        if (capacityFrames <= 0) throw new ArgumentOutOfRangeException(nameof(capacityFrames));
        Format = format;
        CapacityFrames = capacityFrames;
        _queue = new float[capacityFrames * format.Channels];
        _capture = new float[Math.Max(0, captureFrames) * format.Channels];
    }

    /// <inheritdoc/>
    public MixFormat Format { get; }
    /// <inheritdoc/>
    public IAudioSink Sink => this;
    /// <inheritdoc/>
    public IAudioClockSource Clock => this;
    /// <inheritdoc/>
    public int CapacityFrames { get; }
    /// <inheritdoc/>
    public int WritableFrames => _invalidated ? -1 : CapacityFrames - _padding;
    /// <summary>Current queued frame count.</summary>
    public int PaddingFrames => _padding;
    /// <summary>Limit individual writes to model partial submission.</summary>
    public int MaximumWriteFrames { get; set; } = int.MaxValue;
    /// <summary>PCM actually consumed by the simulated hardware, across reset epochs.</summary>
    public ReadOnlySpan<float> Captured => _capture.AsSpan(0, _captured * Format.Channels);
    /// <summary>Number of real Start transitions.</summary>
    public int StartCount { get; private set; }
    /// <summary>Number of real Stop transitions.</summary>
    public int StopCount { get; private set; }
    /// <summary>Number of buffer resets.</summary>
    public int ResetCount { get; private set; }
    /// <summary>Whether the simulated device is running.</summary>
    public bool IsStarted => _started;
    /// <inheritdoc/>
    public long WrittenFrames => _written;
    /// <inheritdoc/>
    public long StreamLatencyFrames => 0;
    /// <inheritdoc/>
    public int MixRate => Format.SampleRate;

    /// <inheritdoc/>
    public int Write(ReadOnlySpan<float> src, int frames)
    {
        if (_invalidated) return 0;
        int count = Math.Max(0, Math.Min(frames, Math.Min(WritableFrames, MaximumWriteFrames)));
        for (int f = 0; f < count; f++)
        {
            src.Slice(f * Format.Channels, Format.Channels).CopyTo(_queue.AsSpan(_write * Format.Channels));
            _write = (_write + 1) % CapacityFrames;
        }
        _padding += count;
        _written += count;
        return count;
    }

    /// <summary>Advance wall time and consume queued frames only while started.</summary>
    public void AdvanceHardware(int frames)
    {
        _nowTicks += (long)Math.Round(Math.Max(0, frames) * 1e7 / MixRate);
        if (!_started || _invalidated) return;
        int count = Math.Min(Math.Max(0, frames), _padding);
        for (int f = 0; f < count; f++)
        {
            if (_captured < _capture.Length / Format.Channels)
            {
                _queue.AsSpan(_read * Format.Channels, Format.Channels).CopyTo(_capture.AsSpan(_captured * Format.Channels));
                _captured++;
            }
            _read = (_read + 1) % CapacityFrames;
        }
        _padding -= count;
        _played += count;
    }

    /// <inheritdoc/>
    public bool TryGetPlayed(out long playedFrames, out long qpc)
    { playedFrames = _played; qpc = _nowTicks; return !_invalidated; }
    /// <inheritdoc/>
    public void Start()
    {
        if (_invalidated) throw new InvalidOperationException("Simulated device invalidated.");
        if (_started) return;
        _started = true;
        StartCount++;
    }
    /// <inheritdoc/>
    public void Stop() { if (_started) StopCount++; _started = false; }
    /// <inheritdoc/>
    public void Reset()
    {
        if (_started) throw new InvalidOperationException("Stop before resetting device buffers.");
        _padding = _read = _write = 0;
        _written = _played = 0;
        ResetCount++;
    }
    /// <summary>Inject an endpoint loss.</summary>
    public void Invalidate() => _invalidated = true;
    /// <inheritdoc/>
    public void WaitForWritable(WaitHandle controlWake, int timeoutMs) => controlWake.WaitOne(timeoutMs);
    /// <inheritdoc/>
    public void Dispose() => Stop();
}
