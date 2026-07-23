using System;
using System.Diagnostics;
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
    private const uint StreamFlagsEventCallback = 0x00040000;   // AUDCLNT_STREAMFLAGS_EVENTCALLBACK (TerraFX doesn't name it)

    private IMMDeviceEnumerator* _enumerator;
    private IMMDevice* _device;
    private IAudioClient* _client;
    private IAudioRenderClient* _render;
    private IAudioClock* _clock;
    private HANDLE _event;   // auto-reset event the device signals each period (event-driven shared mode; replaces Sleep(1) poll)

    private uint _bufferFrames;
    private int _deviceChannels = 2;
    // Device sample format persisted at Open (§7.1): the graph is internally stereo f32, but the endpoint may be int16/24/32.
    // _devFloat == "write our f32 blocks straight" (device is 32-bit IEEE float — the normal case); otherwise Write converts.
    private bool _devFloat = true;
    private int _devBits = 32;
    private int _devBytesPerFrame = 8;   // device frame stride = WAVEFORMATEX.nBlockAlign (do NOT assume 4 B/sample)
    private bool _devFmtWarned;           // one-shot guard so an unsupported-format warning can't spam (and alloc) per block
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
        // Bounded event wait ≈ 2× this block's duration: normally the device signals _event every period (~10 ms) and we
        // wake promptly; if an event is ever lost this just falls back to polling at that cadence, so it can never hang.
        int rate = Format.SampleRate <= 0 ? 48000 : Format.SampleRate;
        uint timeoutMs = (uint)Math.Max(4, (int)(2000L * frames / rate));
        // BLOCK until the whole block is accepted — this IS the RT-loop's pacing (spec §7.9; the feed loop's "sink-write
        // backpressure paces us" contract). The device drains at the hardware clock, so writing the full block gates the
        // caller to realtime. A best-effort partial write here instead lets the caller (RenderBlock) over-pull the mixer/
        // decoder while the overflow is dropped — the fast, scrambled, self-skipping playback. Event-driven shared mode
        // (AUDCLNT_STREAMFLAGS_EVENTCALLBACK): wait on _event for the device to free a period instead of spin-polling.
        while (written < frames && _ready && !_disposed)
        {
            uint padding;
            if (_client->GetCurrentPadding(&padding) < 0) break;   // device lost → return the partial; RebuildSink recovers
            int available = (int)(_bufferFrames - padding);
            if (available <= 0)
            {
                // Buffer full → block until the device frees a period (or timeoutMs elapses — the wait is always bounded).
                _diagSleeps++;
                WaitForSingleObject(_event, timeoutMs);
                // TOCTOU hardening (handle recycle): the wait can also return because Dispose is tearing the device down
                // and about to CloseHandle(_event). Dispose sets _disposed/_ready BEFORE that CloseHandle, and by contract
                // the feed thread is Stop()-joined before the endpoint's handle is closed on BOTH teardown paths (session
                // DisposeAsync disposes the AudioFeedThread — joining the RT thread — before disposing this endpoint; the
                // device-change cold loop parks _feed.Stop() before RebuildSink disposes the old endpoint). WaitForSingleObject
                // is a full barrier, so re-checking here observes those writes and exits cleanly on a spurious/closing wake
                // instead of calling GetCurrentPadding/GetBuffer on a torn-down client.
                if (_disposed || !_ready) break;
                continue;
            }

            int toWrite = Math.Min(frames - written, available);
            byte* pData;
            if (_render->GetBuffer((uint)toWrite, &pData) < 0) break;

            // Our internal layout is stereo f32; conform into the device channel count (write L/R, zero extras / downmix
            // mono) AND into the device sample TYPE. The f32 fast path (device is 32-bit IEEE float, the normal shared-mode
            // case) is a straight write; otherwise convert each already-Sanitize-clamped(±1) sample to the device integer
            // type at its true byte stride (_devBytesPerFrame = nBlockAlign — never assume 4 B/sample). Pointer writes only:
            // this runs inside RenderBlock's AudioTripwire, so it must allocate nothing.
            if (_devFloat)
            {
                float* dst = (float*)pData;
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
            }
            else
            {
                WriteConverted(pData, src, written, toWrite, devCh);
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

    // Converting write for a non-float endpoint. The byte layout + scale + clamp + channel conform is the pure, unit-tested
    // WasapiFormatNegotiation.ConvertBlock (int16 / int32 fully; int24 packed) — here we just hand it a Span<byte> view over
    // the device GetBuffer pointer (no allocation) at the true frame stride (_devBytesPerFrame = nBlockAlign; never an assumed
    // 4 B/sample). An unsupported bit depth returns false after writing silence (not garbage/overrun) — warn ONCE (the
    // interpolated string allocates, so the guard keeps the RT path clean on repeats). NOTE: ConvertBlock does its own
    // finite/clamp guard, so this path does not feed the TEMP diag clip/nonFinite/peak counters (the f32 fast path still does).
    private void WriteConverted(byte* pData, ReadOnlySpan<float> src, int written, int toWrite, int devCh)
    {
        var dst = new Span<byte>(pData, toWrite * _devBytesPerFrame);
        if (!WasapiFormatNegotiation.ConvertBlock(dst, src, written, toWrite, devCh, _devBits, _devBytesPerFrame) && !_devFmtWarned)
        {
            _devFmtWarned = true;
            DiagSink?.Invoke($"UNSUPPORTED device format bits={_devBits} bytesPerFrame={_devBytesPerFrame} - writing silence");
        }
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
        // Decide float-vs-PCM by the TRUE format, not the tag alone. WAVE_FORMAT_IEEE_FLOAT (3) is float directly; but
        // WAVE_FORMAT_EXTENSIBLE (0xFFFE) carries its real type in the SubFormat GUID, NOT the tag — treating ANY 0xFFFE
        // as float would push a 32-bit EXTENSIBLE *PCM* device down the f32 straight-copy path (→ full-scale noise). A
        // tag of 0xFFFE guarantees the buffer IS a WAVEFORMATEXTENSIBLE, so cast and read SubFormat. That GUID's Data1
        // field equals the underlying WAVE_FORMAT tag (KSDATAFORMAT_SUBTYPE_IEEE_FLOAT Data1==3, _PCM Data1==1), so we
        // read Data1 as the Guid's leading 4 bytes (System.Guid's first field is that Int32; little-endian on Windows) —
        // no named KS constants, no allocation. The common 32-bit-float extensible device (Data1==3) still takes float.
        bool devFloat;
        if (devTag == 0xFFFE)
        {
            var ext = (WAVEFORMATEXTENSIBLE*)mix;
            devFloat = *(uint*)&ext->SubFormat == 3;   // 3 == IEEE_FLOAT; 1 == PCM (any other subtype → treat as non-float)
        }
        else
        {
            devFloat = devTag == 3;
        }
        var desc = new DeviceFormatDesc(deviceRate, _deviceChannels, devBits, devFloat);
        Format = WasapiFormatNegotiation.Negotiate(desc);
        // Persist the device sample format for Write: _devFloat gates the straight f32 write vs the converting path, and the
        // frame stride comes from nBlockAlign (NOT an assumed 4 B/sample). CanWriteFloatDirectly is the single source of truth.
        _devFloat = WasapiFormatNegotiation.CanWriteFloatDirectly(desc);
        _devBits = devBits;
        _devBytesPerFrame = mix->nBlockAlign;

        // 100-ms shared buffer; shared mode ignores periodicity. EVENTCALLBACK makes the device signal _event each period so
        // Write blocks on the event instead of polling (periodicity stays 0 — correct for shared-mode event-driven).
        const long hnsBuffer = 100 * 10_000;
        int hr = client->Initialize(AUDCLNT_SHAREMODE.AUDCLNT_SHAREMODE_SHARED, StreamFlagsEventCallback, hnsBuffer, 0, mix, null);
        CoTaskMemFree(mix);
        if (hr < 0) return;

        // Auto-reset, initially non-signaled; the device sets it whenever a buffer period is ready to be filled.
        _event = CreateEventW(null, BOOL.FALSE, BOOL.FALSE, null);
        if (_event == HANDLE.NULL) return;
        if (client->SetEventHandle(_event) < 0) return;

        uint bufferFrames;
        if (client->GetBufferSize(&bufferFrames) < 0) return;
        _bufferFrames = bufferFrames;

        DiagSink?.Invoke($"open deviceRate={deviceRate} renderRate={Format.SampleRate} devCh={_deviceChannels} renderCh={Format.Channels} devBits={devBits} devFloat={devFloat} floatDirect={_devFloat} bytesPerFrame={_devBytesPerFrame} tag=0x{devTag:X} bufFrames={_bufferFrames}");

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
        // Handle-close safety (spec §7.9): _disposed/_ready were set above BEFORE this CloseHandle, and by contract the RT
        // feed thread has already been Stop()-joined before we reach here — PcmAudioSession.DisposeAsync disposes the
        // AudioFeedThread (joining the RT thread) before disposing this endpoint, and AudioDeviceController parks the feed
        // via _feed.Stop() before RebuildSink disposes the old endpoint. So no Write should be mid-WaitForSingleObject on
        // _event; the bounded wait + post-wait _disposed re-check in Write contain the residual best-effort-join window.
        if (_event != HANDLE.NULL) { CloseHandle(_event); _event = HANDLE.NULL; }
    }
}
