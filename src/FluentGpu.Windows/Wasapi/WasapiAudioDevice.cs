using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
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

    /// <summary>Open the default render endpoint at (or near) <paramref name="requested"/>. On failure the device is inert
    /// (silent) but never throws — the session stays alive and surfaces silence, not a crash.</summary>
    public WasapiAudioDevice(MixFormat requested)
    {
        Format = requested;
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

        uint padding;
        if (_client->GetCurrentPadding(&padding) < 0) return 0;
        int available = (int)(_bufferFrames - padding);
        int toWrite = Math.Min(frames, available);
        if (toWrite <= 0) return 0;

        byte* pData;
        if (_render->GetBuffer((uint)toWrite, &pData) < 0) return 0;

        float* dst = (float*)pData;
        int devCh = _deviceChannels;
        // Our internal layout is stereo f32; conform into the device channel count (write L/R, zero extras / downmix mono).
        for (int f = 0; f < toWrite; f++)
        {
            float l = src[f * 2];
            float r = src[f * 2 + 1];
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
        return toWrite;
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
        Format = WasapiFormatNegotiation.Negotiate(new DeviceFormatDesc(deviceRate, _deviceChannels, mix->wBitsPerSample, mix->wFormatTag == 3 || mix->wFormatTag == 0xFFFE));

        // 100-ms shared buffer; shared mode ignores periodicity.
        const long hnsBuffer = 100 * 10_000;
        int hr = client->Initialize(AUDCLNT_SHAREMODE.AUDCLNT_SHAREMODE_SHARED, 0, hnsBuffer, 0, mix, null);
        CoTaskMemFree(mix);
        if (hr < 0) return;

        uint bufferFrames;
        if (client->GetBufferSize(&bufferFrames) < 0) return;
        _bufferFrames = bufferFrames;

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
