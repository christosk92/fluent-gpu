using System;
using System.Diagnostics;
using System.Threading;
using FluentGpu.Signals;

namespace FluentGpu.Media;

/// <summary>One block of an aux stream: what the generator is filling, and WHEN its first frame will be heard. A generator slaves
/// itself to the main playback by time (<see cref="FirstFrameAudibleQpc100ns"/> against the playback timeline), never by sample
/// count: the aux device runs on its own crystal.</summary>
/// <param name="FirstFrameAudibleQpc100ns">When the first frame of this block leaves the aux device: the device clock's last
/// played-frame reading extrapolated over everything queued ahead of this block, plus the endpoint's reported stream latency. QPC
/// in 100-ns units, or the endpoint clock's own domain when it is an <see cref="IAudioTimebase"/>.</param>
/// <param name="SampleRate">The aux device's rate (its own mix format, not the music's).</param>
/// <param name="Channels">The aux device's channel count; the generator fills every channel.</param>
/// <param name="BlockIndex">0 for the first block after an open; counts up; restarts at 0 after a reopen.</param>
public readonly record struct AuxBlock(long FirstFrameAudibleQpc100ns, int SampleRate, int Channels, long BlockIndex);

/// <summary>Fills an aux stream. Called on the aux thread only: no allocation, no lock, no I/O, no blocking.</summary>
public interface IAuxAudioGenerator
{
    /// <summary>Fill <paramref name="frames"/> interleaved frames of <paramref name="interleaved"/> (exactly
    /// <c>frames * block.Channels</c> samples).</summary>
    void Render(Span<float> interleaved, int frames, in AuxBlock block);
}

/// <summary>The aux output's counters (diagnostics; any thread).</summary>
/// <param name="Blocks">Blocks rendered and written since the output was built.</param>
/// <param name="Underruns">Times the aux DEVICE ran dry while running (its queue empty when the aux thread came to write).</param>
/// <param name="StreamLatencyFrames">The open endpoint's reported stream latency (0 before an open).</param>
/// <param name="SampleRate">The open endpoint's rate (0 before an open).</param>
/// <param name="Channels">The open endpoint's channel count (0 before an open).</param>
/// <param name="Opens">Successful opens (1 + reopens after a loss).</param>
public readonly record struct AuxOutputStats(long Blocks, long Underruns, long StreamLatencyFrames, int SampleRate, int Channels, long Opens);

/// <summary>A second, independent output (#154 G2b): its own endpoint, its own thread ("FluentGpu.AudioAux"), its own clock, rendering
/// what an <see cref="IAuxAudioGenerator"/> makes (LTC). It never touches the music session: a session is a track or a hand-over
/// long and its render path is the RT path with the tripwire around it, while an aux stream must outlive track switches, and the
/// music path stays byte-identical. The thread registers through <see cref="IRtThreadCharacteristics.EnterDecode"/> (the "Audio"
/// MMCSS class, below the music's Pro Audio feed): a late aux block costs a timecode reader a few frames of freewheel, never a
/// glitch in the music.
/// <para><b>Loss.</b> A failed open or a lost device (an endpoint that is not ready, <c>WritableFrames</c> &lt; 0, an
/// <see cref="AudioDeviceLostException"/>) goes <see cref="AudioDeviceState.Retrying"/> on the
/// <see cref="AudioDeviceRecoveryPolicy.RetryLadderMs"/> ladder and reopens; past the ladder it is
/// <see cref="AudioDeviceState.Faulted"/> and keeps trying every <see cref="FaultedRetryMs"/> (a USB interface plugged back in
/// comes back on its own). Steady state allocates nothing.</para></summary>
public sealed class AuxAudioOutput : IDisposable
{
    /// <summary>How often a Faulted output tries its endpoint again.</summary>
    public const int FaultedRetryMs = 5000;

    private readonly Func<IAudioEndpoint> _open;
    private readonly IAuxAudioGenerator _generator;
    private readonly IRtThreadCharacteristics? _rt;
    private readonly int _blockFrames;
    private readonly Signal<AudioDeviceState> _state = new(AudioDeviceState.Building);
    private readonly AutoResetEvent _wake = new(false);
    private Thread? _thread;
    private volatile bool _run, _disposed;
    private long _blocks, _underrunsRetired, _opens, _latencyFrames;
    private int _rate, _channels;
    private IBufferedAudioSink? _liveSink;   // read by Stats (DeviceUnderruns) off the aux thread

    /// <param name="open">Opens the aux endpoint (called on the aux thread, again after every loss). It must return an endpoint whose
    /// sink is an <see cref="IBufferedAudioSink"/> (the WASAPI leaf and the paced endpoints are).</param>
    /// <param name="generator">Fills each block (aux thread only).</param>
    /// <param name="rt">The thread registration (MMCSS "Audio" on Windows); null runs at normal priority.</param>
    /// <param name="blockFrames">Frames per block (480 = 10 ms at 48 kHz).</param>
    public AuxAudioOutput(Func<IAudioEndpoint> open, IAuxAudioGenerator generator, IRtThreadCharacteristics? rt = null, int blockFrames = 480)
    {
        _open = open ?? throw new ArgumentNullException(nameof(open));
        _generator = generator ?? throw new ArgumentNullException(nameof(generator));
        _rt = rt;
        _blockFrames = Math.Max(32, blockFrames);
    }

    /// <summary>Building (before the first open), Running, Retrying (on the ladder after a failed open or a loss), Faulted (the ladder
    /// ran out; still retrying slowly). Written from the aux thread.</summary>
    public IReadSignal<AudioDeviceState> State => _state;

    /// <summary>The counters (any thread).</summary>
    public AuxOutputStats Stats
    {
        get
        {
            long under = Interlocked.Read(ref _underrunsRetired) + (Volatile.Read(ref _liveSink)?.DeviceUnderruns ?? 0);
            return new(Interlocked.Read(ref _blocks), under, Interlocked.Read(ref _latencyFrames), Volatile.Read(ref _rate),
                Volatile.Read(ref _channels), Interlocked.Read(ref _opens));
        }
    }

    /// <summary>Start the aux thread (idempotent). It opens the endpoint, renders and keeps the device fed until <see cref="Stop"/>.</summary>
    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_thread is not null) return;
        _run = true;
        _thread = new Thread(Loop) { Name = "FluentGpu.AudioAux", IsBackground = true };
        _thread.Start();
    }

    /// <summary>Stop: the generator's last block is ramped to zero (no click on the aux device), the queue plays out, the device
    /// stops and the endpoint is released. Blocks until the aux thread has ended (at most a few hundred ms). Idempotent.</summary>
    public void Stop()
    {
        Thread? t = _thread;
        if (t is null) return;
        _run = false;
        _wake.Set();
        t.Join();
        _thread = null;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        Stop();
        _disposed = true;
        _wake.Dispose();
    }

    private void Loop()
    {
        using IDisposable? reg = _rt?.EnterDecode();
        int attempt = 0;
        float[] buffer = Array.Empty<float>();
        while (_run)
        {
            IAudioEndpoint? endpoint = null;
            try { endpoint = _open(); }
            catch { endpoint = null; }
            if (endpoint is null || !endpoint.IsReady || endpoint.Sink is not IBufferedAudioSink sink)
            {
                try { endpoint?.Dispose(); } catch { }
                int delay = attempt < AudioDeviceRecoveryPolicy.RetryLadderMs.Length ? AudioDeviceRecoveryPolicy.RetryLadderMs[attempt] : FaultedRetryMs;
                _state.Value = attempt < AudioDeviceRecoveryPolicy.RetryLadderMs.Length ? AudioDeviceState.Retrying : AudioDeviceState.Faulted;
                attempt++;
                _wake.WaitOne(delay);
                continue;
            }

            MixFormat format = sink.Format;
            int channels = Math.Max(1, format.Channels), rate = Math.Max(1, format.SampleRate);
            if (buffer.Length != _blockFrames * channels) buffer = new float[_blockFrames * channels];   // once per open (a format change)
            Volatile.Write(ref _rate, rate);
            Volatile.Write(ref _channels, channels);
            Interlocked.Exchange(ref _latencyFrames, endpoint.Clock.StreamLatencyFrames);
            Interlocked.Increment(ref _opens);
            Volatile.Write(ref _liveSink, sink);
            bool lost = RunOpen(endpoint, sink, buffer, rate, channels);
            Volatile.Write(ref _liveSink, null);
            Interlocked.Add(ref _underrunsRetired, sink.DeviceUnderruns);
            try { sink.Stop(); } catch { }
            try { endpoint.Dispose(); } catch { }
            if (!lost) break;                       // a Stop: done
            attempt = 0;                            // a loss of a device that was running restarts the ladder
            _state.Value = AudioDeviceState.Retrying;
            _wake.WaitOne(AudioDeviceRecoveryPolicy.RetryLadderMs[0]);
        }
    }

    /// <summary>Feed one open endpoint until <see cref="Stop"/> (false) or a device loss (true). Allocation-free.</summary>
    private bool RunOpen(IAudioEndpoint endpoint, IBufferedAudioSink sink, float[] buffer, int rate, int channels)
    {
        IAudioClockSource clock = endpoint.Clock;
        long written = 0, blockIndex = 0;
        bool started = false;
        try
        {
            while (true)
            {
                if (!_run)
                {
                    // The last block, ramped to zero, then let the queue play out (bounded by the device buffer) before the caller stops.
                    if (started && RenderBlock(sink, clock, buffer, rate, channels, ref written, ref blockIndex, rampOut: true))
                        DrainQueue(sink, rate);
                    return false;
                }
                int writable = sink.WritableFrames;
                if (writable < 0) return true;
                while (writable >= _blockFrames && _run)
                {
                    if (!RenderBlock(sink, clock, buffer, rate, channels, ref written, ref blockIndex, rampOut: false)) return true;
                    writable -= _blockFrames;
                    if (!started)
                    {
                        sink.Start();
                        started = true;
                        _state.Value = AudioDeviceState.Running;
                    }
                }
                sink.WaitForWritable(_wake, 20);
            }
        }
        catch (AudioDeviceLostException) { return true; }
    }

    private bool RenderBlock(IBufferedAudioSink sink, IAudioClockSource clock, float[] buffer, int rate, int channels,
        ref long written, ref long blockIndex, bool rampOut)
    {
        // When this block is heard: the clock's last played reading carried forward over what is queued ahead of it, plus the
        // endpoint's own latency. Before the device runs (no played reading yet) it is "now" plus the same queue.
        long now = clock is IAudioTimebase timebase ? timebase.NowTicks100ns : (long)(Stopwatch.GetTimestamp() * (1e7 / Stopwatch.Frequency));
        long anchorQpc = now, played = 0;
        if (clock.TryGetPlayed(out long p, out long q) && q > 0) { anchorQpc = q; played = p; }
        long queued = Math.Max(0, written - played) + clock.StreamLatencyFrames;
        var block = new AuxBlock(anchorQpc + (long)(queued * 1e7 / rate), rate, channels, blockIndex);
        Span<float> span = buffer.AsSpan(0, _blockFrames * channels);
        _generator.Render(span, _blockFrames, in block);
        if (rampOut)
            for (int i = 0; i < _blockFrames; i++)
            {
                float g = 1f - (i + 1) / (float)_blockFrames;
                for (int c = 0; c < channels; c++) span[i * channels + c] *= g;
            }
        int accepted = sink.Write(span, _blockFrames);
        if (accepted <= 0 && sink.WritableFrames < 0) return false;
        written += accepted;
        blockIndex++;
        Interlocked.Increment(ref _blocks);
        return true;
    }

    private void DrainQueue(IBufferedAudioSink sink, int rate)
    {
        // Wait for the device to play what is queued (the ramp included), at most the buffer's length plus a margin.
        long deadline = Environment.TickCount64 + 400;
        while (Environment.TickCount64 < deadline)
        {
            int writable = sink.WritableFrames;
            if (writable < 0 || writable >= sink.CapacityFrames) return;
            Thread.Sleep(Math.Clamp((sink.CapacityFrames - writable) * 1000 / Math.Max(1, rate), 1, 20));
        }
    }
}
