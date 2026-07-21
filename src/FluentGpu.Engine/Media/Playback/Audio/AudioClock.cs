using System;

namespace FluentGpu.Media;

/// <summary>
/// Derived-position math off the device clock (spec §7.6) — "the device clock is the only clock". Position is
/// DERIVED + QPC-extrapolated, never stored: the authoritative value is the integer played-frame count from
/// <see cref="IAudioClockSource.TryGetPlayed"/>; between polls it extrapolates with the QPC delta; the summed stage
/// <see cref="IDspStage.LatencySamples"/> (+ the device stream latency) are subtracted so the position tracks what is
/// AUDIBLE NOW. An <see cref="IsValid"/> warmup gate holds the position until <c>GetPosition</c> first returns non-zero
/// (it reads 0 for the first several seconds on some drivers). QPC timestamps are in 100-ns units (WASAPI
/// <c>IAudioClock::GetPosition</c>'s <c>QPCPosition</c>), so no separate frequency is needed. Alloc-free.
/// </summary>
public sealed class AudioClockPosition
{
    private long _sampleFrames;   // played frames at the last poll (authoritative, integer)
    private long _sampleTicks;    // the QPC (100-ns) timestamp of that poll
    private bool _valid;
    private long _originDevice;   // device played-frame anchor (set on seek/start)
    private long _originPosition; // the position frame that anchor corresponds to

    /// <summary>The device mix rate (Hz).</summary>
    public int MixRate { get; private set; } = 48000;
    /// <summary>The device stream latency in frames (measured; re-read on every rebuild).</summary>
    public long StreamLatencyFrames { get; private set; }
    /// <summary>Extra pipeline latency to subtract (summed graph stage latency, samples).</summary>
    public int ExtraLatencySamples { get; set; }

    /// <summary>True once the clock has produced a non-zero played count — until then <see cref="Project"/> holds at zero.</summary>
    public bool IsValid => _valid;

    /// <summary>Reset for a fresh device/session (a sink rebuild) — re-arms the warmup gate.</summary>
    public void Reset()
    {
        _sampleFrames = 0;
        _sampleTicks = 0;
        _valid = false;
        _originDevice = 0;
        _originPosition = 0;
    }

    /// <summary>Anchor the position domain to the device domain (spec §7.6): the current device played count
    /// <paramref name="deviceFramesNow"/> corresponds to timeline position <paramref name="positionFrames"/>. Called on a
    /// seek so the derived position reflects the new target while the device clock keeps monotonically counting.</summary>
    public void Rebase(long deviceFramesNow, long positionFrames)
    {
        _originDevice = deviceFramesNow;
        _originPosition = positionFrames;
    }

    /// <summary>Poll <paramref name="clock"/> (the rare, off-RT sample; a user→kernel→user transition). Records the
    /// authoritative played frames + QPC timestamp and opens the warmup gate on the first non-zero read.</summary>
    public void Sample(IAudioClockSource clock)
    {
        MixRate = clock.MixRate <= 0 ? 48000 : clock.MixRate;
        StreamLatencyFrames = clock.StreamLatencyFrames;
        if (clock.TryGetPlayed(out long played, out long qpc))
        {
            if (played > 0) _valid = true;
            _sampleFrames = played;
            _sampleTicks = qpc;
        }
    }

    /// <summary>Project the position at <paramref name="nowTicks100ns"/> (a 100-ns timestamp in the same domain as the
    /// clock's QPC): the last played count + the QPC-extrapolated frames since, minus the summed latency. Returns
    /// <see cref="TimeSpan.Zero"/> while the warmup gate is closed. Exact (100-ns ticks; never routed through a float).</summary>
    public TimeSpan Project(long nowTicks100ns)
    {
        if (!_valid) return TimeSpan.Zero;

        // Extrapolate frames since the last poll from the QPC delta (100-ns → seconds → frames), anchored to the
        // position domain (defeats a seek: the device clock keeps counting; the origin re-maps it).
        double elapsedSec = (nowTicks100ns - _sampleTicks) / 1e7;
        if (elapsedSec < 0) elapsedSec = 0;
        double frames = _originPosition + (_sampleFrames - _originDevice) + elapsedSec * MixRate
                        - StreamLatencyFrames - ExtraLatencySamples;
        if (frames < 0) frames = 0;

        long ticks = (long)Math.Round(frames * 1e7 / MixRate);   // frames → 100-ns ticks, exact projection
        return new TimeSpan(ticks);
    }

    /// <summary>The authoritative integer played-frame count at the last poll (the M0 "integer frame count is authoritative"
    /// invariant), latency-compensated. Non-extrapolated.</summary>
    public long PlayedFramesCompensated
    {
        get
        {
            long f = _originPosition + (_sampleFrames - _originDevice) - StreamLatencyFrames - ExtraLatencySamples;
            return f < 0 ? 0 : f;
        }
    }
}

/// <summary>
/// A synthetic, fully deterministic <see cref="IAudioClockSource"/> for headless parity (spec §7.9) — the harness ticks
/// it with <see cref="Advance"/> ("pull N frames deterministically"). Models the driver warmup (<c>GetPosition</c> reads
/// 0 for the first <see cref="_warmupFrames"/>) so the <see cref="AudioClockPosition"/> <c>IsValid</c> gate is exercised.
/// QPC is a 100-ns virtual clock advanced in lock-step with rendered frames. No wall clock, no timers.
/// </summary>
public sealed class SyntheticAudioClock : IAudioClockSource
{
    private readonly long _latencyFrames;
    private readonly long _warmupFrames;
    private long _written;
    private long _played;
    private long _nowTicks;

    /// <summary>Create a synthetic clock at <paramref name="mixRate"/> with <paramref name="streamLatencyFrames"/> measured
    /// latency and a <paramref name="warmupFrames"/> warmup (played reads 0 until that many frames rendered).</summary>
    public SyntheticAudioClock(int mixRate, long streamLatencyFrames = 0, long warmupFrames = 0)
    {
        MixRate = mixRate <= 0 ? 48000 : mixRate;
        _latencyFrames = streamLatencyFrames;
        _warmupFrames = warmupFrames;
    }

    /// <inheritdoc/>
    public int MixRate { get; }
    /// <inheritdoc/>
    public long WrittenFrames => _written;
    /// <inheritdoc/>
    public long StreamLatencyFrames => _latencyFrames;
    /// <summary>The virtual 100-ns "now" (advanced by <see cref="Advance"/>).</summary>
    public long NowTicks100ns => _nowTicks;

    /// <summary>Advance the device by <paramref name="frames"/> rendered frames (written = played here; the pipeline
    /// latency is modeled by <see cref="StreamLatencyFrames"/>). Advances the virtual QPC in lock-step.</summary>
    public void Advance(long frames)
    {
        if (frames <= 0) return;
        _written += frames;
        _played += frames;
        _nowTicks += (long)Math.Round(frames * 1e7 / MixRate);
    }

    /// <inheritdoc/>
    public bool TryGetPlayed(out long playedFrames, out long qpc)
    {
        qpc = _nowTicks;
        playedFrames = _played < _warmupFrames ? 0 : _played;
        return true;
    }
}

/// <summary>
/// A device endpoint (spec §7): the sink + its played-frames clock as ONE object opened once per session. The device is
/// a SINGLE OS object (WASAPI <c>IAudioClient</c>) exposing both the render (<see cref="Sink"/>) and clock
/// (<see cref="Clock"/>) facets; bundling them keeps them consistent (same mix format, same lifetime). The headless
/// endpoint pairs a <see cref="NullAudioSink"/> with a <see cref="SyntheticAudioClock"/>.
/// </summary>
public interface IAudioEndpoint : IDisposable
{
    /// <summary>The render sink (opened once per session).</summary>
    IAudioSink Sink { get; }
    /// <summary>The played-frames master clock for the same device.</summary>
    IAudioClockSource Clock { get; }
}

/// <summary>The headless endpoint (spec §7.9): a <see cref="NullAudioSink"/> + a deterministic
/// <see cref="SyntheticAudioClock"/> the pump advances. Runs the whole graph with no device.</summary>
public sealed class HeadlessAudioEndpoint : IAudioEndpoint
{
    /// <summary>Create a headless endpoint at <paramref name="format"/>. <paramref name="captureFrames"/> records the first
    /// N presented frames; <paramref name="warmupFrames"/>/<paramref name="latencyFrames"/> model the synthetic clock.</summary>
    public HeadlessAudioEndpoint(MixFormat format, int captureFrames = 0, long warmupFrames = 0, long latencyFrames = 0)
    {
        Sink = new NullAudioSink(format, captureFrames);
        Clock = new SyntheticAudioClock(format.SampleRate, latencyFrames, warmupFrames);
    }

    /// <inheritdoc/>
    public IAudioSink Sink { get; }
    /// <inheritdoc/>
    public IAudioClockSource Clock { get; }
    /// <inheritdoc/>
    public void Dispose() { }
}

/// <summary>
/// A null <see cref="IAudioSink"/> (spec §7.9) — accepts every frame and counts it, for headless golden-PCM pulls with
/// no device. Optionally copies the presented frames into a capture buffer so a test can diff the exact PCM the sink saw.
/// </summary>
public sealed class NullAudioSink : IAudioSink
{
    private readonly float[]? _capture;
    private int _captured;   // frames captured
    private long _frames;

    /// <summary>Create a null sink at <paramref name="format"/>. If <paramref name="captureFrames"/> &gt; 0 it records the
    /// first N frames presented (for golden diffs).</summary>
    public NullAudioSink(MixFormat format, int captureFrames = 0)
    {
        Format = format;
        if (captureFrames > 0) _capture = new float[captureFrames * Math.Max(1, format.Channels)];
    }

    /// <inheritdoc/>
    public MixFormat Format { get; }
    /// <summary>Total frames presented.</summary>
    public long FramesWritten => _frames;
    /// <summary>The captured PCM (interleaved), up to the capture length.</summary>
    public ReadOnlySpan<float> Captured => _capture is null ? default : _capture.AsSpan(0, _captured * Format.Channels);

    /// <inheritdoc/>
    public int Write(ReadOnlySpan<float> src, int frames)
    {
        if (_capture is not null && _captured < _capture.Length / Format.Channels)
        {
            int room = _capture.Length / Format.Channels - _captured;
            int take = Math.Min(room, frames);
            src[..(take * Format.Channels)].CopyTo(_capture.AsSpan(_captured * Format.Channels));
            _captured += take;
        }
        _frames += frames;
        return frames;
    }

    /// <inheritdoc/>
    public void Start() { }
    /// <inheritdoc/>
    public void Stop() { }
}
