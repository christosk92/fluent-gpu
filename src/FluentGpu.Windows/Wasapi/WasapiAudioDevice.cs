using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using FluentGpu.Media;
using TerraFX.Interop.Windows;
using static TerraFX.Interop.Windows.Windows;

namespace FluentGpu.Windows.Wasapi;

/// <summary>
/// The WASAPI device leaf (spec §13, §7.6) — a single <c>IAudioClient</c> opened ONCE per session exposing BOTH the render
/// sink (<see cref="IAudioSink"/>) and the played-frames clock (<see cref="IAudioClockSource"/>) as an
/// <see cref="IAudioEndpoint"/>. Shared mode, fixed internal <c>f32</c>/device-rate/stereo (the device mix format's rate is
/// adopted; §7.1). Every WASAPI ComPtr stays confined behind this leaf. M2 is SINGLE-THREAD-CORRECT: the control-thread
/// feeder (in <see cref="FluentGpu.Media.PcmAudioSession"/>) pumps <see cref="Write"/>; the M4 flip moves it to an MMCSS
/// Pro-Audio RT thread with no surface change. The format-negotiation + clock math are the pure, unit-tested
/// <see cref="WasapiFormatNegotiation"/>/<see cref="WasapiPositionMath"/> helpers; only the COM plumbing lives here.
/// <para>On-box only — no automated gate creates a real device (the tests drive the fake endpoint + the pure helpers).</para>
/// </summary>
public sealed unsafe class WasapiAudioDevice : IAudioEndpoint, IAudioSink, IAudioClockSource
{
    private const uint ClsctxAll = 0x17;   // CLSCTX_ALL = INPROC_SERVER|INPROC_HANDLER|LOCAL_SERVER|REMOTE_SERVER

    // The paced Write (below) waits for device-buffer space with Thread.Sleep(1); on ARM64/Win11 the DEFAULT timer
    // granularity is ~15.6 ms, so Sleep(1) would over-sleep and starve the device — producing ~0.65× (slow, stuttering)
    // playback. Raise the process timer resolution to 1 ms for the app's lifetime once any device opens (music app: the
    // slightly higher tick rate is a non-issue, and it's balanced by never lowering it). Event-driven WASAPI
    // (AUDCLNT_STREAMFLAGS_EVENTCALLBACK) is the proper follow-up that removes the poll entirely.
    [DllImport("winmm.dll")] private static extern uint timeBeginPeriod(uint uPeriod);
    private static int s_timerRaised;
    private static void EnsureHighTimerResolution()
    {
        if (System.Threading.Interlocked.Exchange(ref s_timerRaised, 1) == 0) timeBeginPeriod(1);
    }

    private IMMDeviceEnumerator* _enumerator;
    private IMMDevice* _device;
    private IAudioClient* _client;
    private IAudioRenderClient* _render;
    private IAudioClock* _clock;

    private uint _bufferFrames;
    private int _deviceChannels = 2;
    private ulong _clockFreq;
    private long _latencyFrames;
    private long _written;
    private bool _ready;
    private bool _started;
    private bool _disposed;

    // TEMP audio diagnostic: the app wires this to its logger. Emits the negotiated device format at open and, once per
    // ~second, the feed-vs-play throughput so we can see whether the device is UNDER-fed (production side) or UNDER-playing
    // (device/timing side), and whether the render rate matches the device rate.
    public static Action<string>? DiagSink;
    private long _diagReqFrames, _diagWrittenFrames, _diagIntervalStartTicks, _diagIntervalStartPlayed;
    private int _diagCalls, _diagSleeps;
    private long _diagClip, _diagNonFinite;   // samples that hit the ±1 clamp / were NaN|Inf (noise sources)
    private float _diagPeak;                   // max |sample| this interval (>1 ⇒ the limiter let a transient through)

    /// <summary>Open the default render endpoint at (or near) <paramref name="requested"/>. On failure the device is inert
    /// (silent) but never throws — the session stays alive and surfaces silence, not a crash.</summary>
    public WasapiAudioDevice(MixFormat requested)
    {
        Format = requested;
        EnsureHighTimerResolution();   // 1 ms timer so the paced Write's Sleep(1) doesn't over-sleep and starve the device
        try { Open(requested); }
        catch (Exception ex) { Debug.WriteLine($"WasapiAudioDevice open failed: {ex.Message}"); _ready = false; }
    }

    /// <inheritdoc cref="IAudioSink.Format"/>
    public MixFormat Format { get; private set; }
    /// <summary>True when the device opened and can render.</summary>
    public bool IsReady => _ready;

    /// <inheritdoc/>
    public IAudioSink Sink => this;
    /// <inheritdoc/>
    public IAudioClockSource Clock => this;

    // ── IAudioClockSource ────────────────────────────────────────────────────────────────────────────────────────────
    /// <inheritdoc/>
    public long WrittenFrames => _written;
    /// <inheritdoc/>
    public long StreamLatencyFrames => _latencyFrames;
    /// <inheritdoc/>
    public int MixRate => Format.SampleRate;

    /// <inheritdoc/>
    public bool TryGetPlayed(out long playedFrames, out long qpc)
    {
        playedFrames = 0; qpc = 0;
        if (!_ready || _clock is null) return false;
        ulong pos, q;
        if (_clock->GetPosition(&pos, &q) < 0) return false;
        playedFrames = WasapiPositionMath.PlayedFrames(pos, _clockFreq, MixRate);
        qpc = WasapiPositionMath.QpcTo100ns(q);
        return true;
    }

    // ── IAudioSink ───────────────────────────────────────────────────────────────────────────────────────────────────
    /// <inheritdoc/>
    public void Start()
    {
        if (!_ready || _started || _client is null) return;
        if (_client->Start() >= 0) _started = true;
    }

    /// <inheritdoc/>
    public void Stop()
    {
        if (_started && _client is not null) { _client->Stop(); _started = false; }
    }

    /// <inheritdoc/>
    public int Write(ReadOnlySpan<float> src, int frames)
    {
        if (!_ready || _render is null || _client is null || frames <= 0) return 0;

        int devCh = _deviceChannels;
        int written = 0;
        // BLOCK until the whole block is accepted — this IS the RT-loop's pacing (spec §7.9; the feed loop's "sink-write
        // backpressure paces us" contract). The device drains at the hardware clock, so writing the full block gates the
        // caller to realtime. A best-effort partial write here instead lets the caller (RenderBlock) over-pull the mixer/
        // decoder while the overflow is dropped — the fast, scrambled, self-skipping playback. Shared mode has no event
        // handle, so we poll the padding and yield ~1ms per full-buffer wait (the 100ms buffer gives ample underrun margin).
        while (written < frames && _ready && !_disposed)
        {
            uint padding;
            if (_client->GetCurrentPadding(&padding) < 0) break;   // device lost → return the partial; RebuildSink recovers
            int available = (int)(_bufferFrames - padding);
            if (available <= 0) { _diagSleeps++; Thread.Sleep(1); continue; }     // buffer full → wait for the hardware to free a slot

            int toWrite = Math.Min(frames - written, available);
            byte* pData;
            if (_render->GetBuffer((uint)toWrite, &pData) < 0) break;

            float* dst = (float*)pData;
            // Our internal layout is stereo f32; conform into the device channel count (write L/R, zero extras / downmix mono).
            for (int f = 0; f < toWrite; f++)
            {
                float l = Sanitize(src[(written + f) * 2]);
                float r = Sanitize(src[(written + f) * 2 + 1]);
                int db = f * devCh;
                if (devCh == 1) { dst[db] = (l + r) * 0.5f; }
                else
                {
                    dst[db] = l;
                    dst[db + 1] = r;
                    for (int c = 2; c < devCh; c++) dst[db + c] = 0f;
                }
            }

            _render->ReleaseBuffer((uint)toWrite, 0);
            _written += toWrite;
            written += toWrite;
        }

        // TEMP diagnostic: once per ~second, report feed rate (frames we handed the device) vs play rate (frames the
        // hardware clock actually advanced) as multiples of the render sample rate. feedXrate<1 ⇒ producer under-supplies;
        // playXrate<1 with feedXrate≈1 ⇒ device under-plays/underruns; renderRate≠deviceRate would be a resample mismatch.
        if (DiagSink is { } diag)
        {
            _diagReqFrames += frames; _diagWrittenFrames += written; _diagCalls++;
            long nowTicks = Stopwatch.GetTimestamp();
            if (_diagIntervalStartTicks == 0) { _diagIntervalStartTicks = nowTicks; TryGetPlayed(out _diagIntervalStartPlayed, out _); }
            double elapsedSec = (nowTicks - _diagIntervalStartTicks) / (double)Stopwatch.Frequency;
            if (elapsedSec >= 1.0)
            {
                TryGetPlayed(out long playedNow, out _);
                long playedDelta = playedNow - _diagIntervalStartPlayed;
                int rate = Format.SampleRate <= 0 ? 48000 : Format.SampleRate;
                diag($"1s req={_diagReqFrames} written={_diagWrittenFrames} calls={_diagCalls} sleeps={_diagSleeps} playedDelta={playedDelta} elapsedMs={elapsedSec * 1000:0} feedXrate={_diagWrittenFrames / elapsedSec / rate:0.000} playXrate={playedDelta / elapsedSec / rate:0.000} peak={_diagPeak:0.000} clip={_diagClip} nonFinite={_diagNonFinite}");
                _diagReqFrames = _diagWrittenFrames = 0; _diagCalls = _diagSleeps = 0;
                _diagIntervalStartTicks = nowTicks; _diagIntervalStartPlayed = playedNow;
                _diagPeak = 0f; _diagClip = 0; _diagNonFinite = 0;
            }
        }
        return written;
    }

    // Output safety net: a NaN/Inf sample slips through the brickwall limiter UNTOUCHED — abs(NaN) compares false against
    // the ceiling, so the limiter never reduces gain — and reaches the DAC as a harsh scratch/noise. Replace non-finite
    // samples with silence, and hard-clamp any stray transient the limiter missed to ±1 (the DAC hard-clips there anyway;
    // doing it in float avoids wrap/denormal harshness). This is a real guard, not just diagnostic — it stays after the
    // instrumentation is removed. The counters distinguish NaN noise vs limiter-miss clipping vs a clean signal.
    private float Sanitize(float s)
    {
        if (!float.IsFinite(s)) { _diagNonFinite++; return 0f; }
        float a = s < 0f ? -s : s;
        if (a > _diagPeak) _diagPeak = a;
        if (s > 1f) { _diagClip++; return 1f; }
        if (s < -1f) { _diagClip++; return -1f; }
        return s;
    }

    // ── COM bring-up ─────────────────────────────────────────────────────────────────────────────────────────────────
    private void Open(MixFormat requested)
    {
        // COM must be initialized on this thread; MTA is fine for WASAPI. Ignore "already initialized" results.
        _ = CoInitializeEx(null, (uint)(COINIT.COINIT_MULTITHREADED | COINIT.COINIT_DISABLE_OLE1DDE));

        Guid clsidEnum = CLSID.CLSID_MMDeviceEnumerator;
        Guid iidEnum = IID.IID_IMMDeviceEnumerator;
        IMMDeviceEnumerator* enumerator;
        if (CoCreateInstance(&clsidEnum, null, ClsctxAll, &iidEnum, (void**)&enumerator) < 0) return;
        _enumerator = enumerator;

        IMMDevice* device;
        if (enumerator->GetDefaultAudioEndpoint(EDataFlow.eRender, ERole.eConsole, &device) < 0) return;
        _device = device;

        Guid iidClient = IID.IID_IAudioClient;
        IAudioClient* client;
        if (device->Activate(&iidClient, ClsctxAll, null, (void**)&client) < 0) return;
        _client = client;

        WAVEFORMATEX* mix;
        if (client->GetMixFormat(&mix) < 0) return;

        int deviceRate = (int)mix->nSamplesPerSec;
        _deviceChannels = mix->nChannels;
        int devBits = mix->wBitsPerSample;
        ushort devTag = mix->wFormatTag;
        bool devFloat = devTag == 3 || devTag == 0xFFFE;
        Format = WasapiFormatNegotiation.Negotiate(new DeviceFormatDesc(deviceRate, _deviceChannels, mix->wBitsPerSample, mix->wFormatTag == 3 || mix->wFormatTag == 0xFFFE));

        // 100-ms shared buffer; shared mode ignores periodicity.
        const long hnsBuffer = 100 * 10_000;
        int hr = client->Initialize(AUDCLNT_SHAREMODE.AUDCLNT_SHAREMODE_SHARED, 0, hnsBuffer, 0, mix, null);
        CoTaskMemFree(mix);
        if (hr < 0) return;

        uint bufferFrames;
        if (client->GetBufferSize(&bufferFrames) < 0) return;
        _bufferFrames = bufferFrames;

        DiagSink?.Invoke($"open deviceRate={deviceRate} renderRate={Format.SampleRate} devCh={_deviceChannels} renderCh={Format.Channels} devBits={devBits} devFloat={devFloat} tag=0x{devTag:X} bufFrames={_bufferFrames}");

        long hnsLatency;
        if (client->GetStreamLatency(&hnsLatency) >= 0)
            _latencyFrames = WasapiPositionMath.LatencyFrames(hnsLatency, Format.SampleRate);

        Guid iidRender = IID.IID_IAudioRenderClient;
        IAudioRenderClient* render;
        if (client->GetService(&iidRender, (void**)&render) < 0) return;
        _render = render;

        Guid iidClock = IID.IID_IAudioClock;
        IAudioClock* clock;
        if (client->GetService(&iidClock, (void**)&clock) >= 0 && clock is not null)
        {
            _clock = clock;
            ulong freq;
            if (clock->GetFrequency(&freq) >= 0) _clockFreq = freq;
        }

        _ready = true;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _ready = false;
        Stop();
        if (_clock is not null) { _clock->Release(); _clock = null; }
        if (_render is not null) { _render->Release(); _render = null; }
        if (_client is not null) { _client->Release(); _client = null; }
        if (_device is not null) { _device->Release(); _device = null; }
        if (_enumerator is not null) { _enumerator->Release(); _enumerator = null; }
    }
}
