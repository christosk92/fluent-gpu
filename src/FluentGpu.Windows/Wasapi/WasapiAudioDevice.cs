using System;
using System.Diagnostics;
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
public sealed unsafe class WasapiAudioDevice : IAudioEndpoint, IBufferedAudioSink, IAudioClockSource
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
    private long _deviceUnderruns;   // RT-incremented, read from any thread (Volatile): see DeviceUnderruns
    private bool _ready;
    private bool _started;
    private bool _disposed;
    private int _lostHr;   // the HRESULT that invalidated a once-ready device (0 = never lost); read by Start/Reset's exception

    // AUDCLNT_E_DEVICE_INVALIDATED / AUDCLNT_E_RESOURCES_INVALIDATED — the two "this IAudioClient is dead" HRESULTs a jack
    // switch or endpoint removal produces on a RUNNING client (there is no default-device notification for them).
    private const int AudclntEDeviceInvalidated = unchecked((int)0x88890004);
    private const int AudclntEResourcesInvalidated = unchecked((int)0x88890026);

    // Audio diagnostics (spec §7.x) split into two channels, because ONE of them (the 1 Hz feed-vs-play throughput) used
    // to run INSIDE Write — i.e. on the RT feed thread, inside AudioTripwire's alloc/lock/syscall-free contract:
    //  - FormatSink: invoked ONCE at Open (never from Write), plus the rare one-shot unsupported-device-format warning.
    //    Both call sites are off the RT path, so building an interpolated string there is fine. This is the ONLY channel
    //    that reports the negotiated device sample rate — the one line production hiccup triage actually needs — so it
    //    must be always-on (house rule: never gate diagnostics behind an env var).
    //  - DiagSink: kept for source compatibility, but is NEVER invoked from the RT Write path any more. The 1 Hz
    //    feed-vs-play counters below are bare-field accumulation only; TryTakeStats() hands a drained snapshot to a
    //    non-RT caller (AudioFeedThread.ControlTickOnce), which does the formatting/invoke — and can use DiagSink for it.
    public static Action<string>? FormatSink;
    public static Action<string>? DiagSink;

    /// <summary>Point-in-time snapshot of one drained ~1 s RT-thread diagnostic interval — see <see cref="TryTakeStats"/>.
    /// POD (no managed refs), safe to build and hand across threads without allocation.</summary>
    public readonly record struct AudioDeviceStats(long ReqFrames, long WrittenFrames, int Calls,
        int Sleeps, long PlayedDelta, double ElapsedSec, float Peak, int Clip, int NonFinite);

    private long _diagReqFrames, _diagWrittenFrames, _diagIntervalStartTicks;
    private int _diagCalls, _diagSleeps;
    private long _diagClip, _diagNonFinite;   // samples that hit the ±1 clamp / were NaN|Inf (noise sources)
    private float _diagPeak;                   // max |sample| this interval (>1 ⇒ the limiter let a transient through)

    // 1 Hz stats handoff, RT producer → non-RT consumer (spec §7.x). Write() copies the counters above into the
    // _pending* fields and flips _statsPending true with a release Volatile.Write once ~1 s has elapsed; TryTakeStats()
    // acquires the flag with Volatile.Read, copies the snapshot out, and flips it back false. If the non-RT side never
    // drains, Write just keeps accumulating and republishes on the next check — a diagnostic sample coalescing into a
    // longer interval is fine; it must never block or grow unbounded state on the RT thread.
    private long _pendingReqFrames, _pendingWrittenFrames;
    private int _pendingCalls, _pendingSleeps, _pendingClip, _pendingNonFinite;
    private float _pendingPeak;
    private double _pendingElapsedSec;
    private bool _statsPending;
    private long _diagPlayedBaseline;   // last played-frames sample TryTakeStats took — the IAudioClock COM call lives
    private bool _diagPlayedBaselineSet;   // only on this (off-RT) side now; see TryTakeStats.

    /// <summary>Open the default render endpoint at (or near) <paramref name="requested"/>. On failure the device is inert
    /// (silent) but never throws — the session stays alive and surfaces silence, not a crash.</summary>
    public WasapiAudioDevice(MixFormat requested)
    {
        Format = requested;
        try { Open(requested); }
        catch (Exception ex)
        {
            Debug.WriteLine($"WasapiAudioDevice open failed: {ex.Message}");
            Volatile.Write(ref _ready, false);
            FormatSink?.Invoke(FormatOpenFailure("exception", ex.HResult) + " msg=" + ex.Message);
        }
    }

    /// <inheritdoc cref="IAudioSink.Format"/>
    public MixFormat Format { get; private set; }
    /// <summary>Identity of the endpoint actually opened by this sink; no COM access on read.</summary>
    public WasapiEndpointInfo? EndpointInfo { get; private set; }
    /// <summary>True when the device opened and can render; false after the open failed at any step (logged through
    /// <see cref="FormatSink"/> as <c>open FAILED step=… hr=…</c>) or once a running client was invalidated
    /// (<see cref="MarkLost"/>). A false device is inert: <see cref="WritableFrames"/> -1, <see cref="Write"/> 0.</summary>
    public bool IsReady => Volatile.Read(ref _ready);

    /// <summary>Pure: is <paramref name="hr"/> one of the two "the device is gone" WASAPI HRESULTs
    /// (<c>AUDCLNT_E_DEVICE_INVALIDATED</c> 0x88890004, <c>AUDCLNT_E_RESOURCES_INVALIDATED</c> 0x88890026)?</summary>
    public static bool IsDeviceLostHr(int hr) => hr == AudclntEDeviceInvalidated || hr == AudclntEResourcesInvalidated;

    /// <summary>Pure: the always-on <see cref="FormatSink"/> line for an open that failed at <paramref name="step"/>
    /// with <paramref name="hr"/> — the line production triage reads to tell "device not ready yet" (0x88890004 /
    /// E_NOTFOUND during a jack switch) from a real driver fault.</summary>
    public static string FormatOpenFailure(string step, int hr) => $"open FAILED step={step} hr=0x{hr:X8} default-endpoint";

    // The running client was invalidated (a Write/WritableFrames/Start hit a device-lost HRESULT): flip inert so every later
    // Write returns 0 / WritableFrames -1 — the session's sink-failure path then asks the cold thread for a rebuild. Volatile
    // write: the cold thread reads IsReady across threads. RT-safe (no alloc, no syscall).
    private void MarkLost(int hr)
    {
        _lostHr = hr;
        Volatile.Write(ref _ready, false);
    }

    /// <inheritdoc/>
    public IAudioSink Sink => this;
    /// <inheritdoc/>
    public IAudioClockSource Clock => this;

    // ── IAudioClockSource ────────────────────────────────────────────────────────────────────────────────────────────
    /// <inheritdoc/>
    public long WrittenFrames => _written;

    /// <inheritdoc/>
    public long DeviceUnderruns => Volatile.Read(ref _deviceUnderruns);
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
    /// <summary>Start rendering. Throws <see cref="AudioDeviceLostException"/> — never anything else — when the device
    /// is not ready or <c>IAudioClient::Start</c> fails (the client is marked lost first): the session treats it as a sink
    /// failure and asks for a rebuild instead of surfacing a playback error (Wavee #112). <c>_started</c> flips only on success.</summary>
    public void Start()
    {
        if (_started) return;
        if (!IsReady || _client is null) throw new AudioDeviceLostException(_lostHr != 0 ? _lostHr : AudclntEDeviceInvalidated);
        int hr = _client->Start();
        if (hr < 0)
        {
            MarkLost(hr);
            throw new AudioDeviceLostException(hr);
        }
        _started = true;
    }

    /// <summary>Stop rendering. Never throws — a Stop on an invalidated client just marks it lost.</summary>
    public void Stop()
    {
        if (_started && _client is not null)
        {
            int hr = _client->Stop();
            _started = false;
            if (hr < 0 && IsDeviceLostHr(hr)) MarkLost(hr);
        }
    }

    /// <inheritdoc/>
    public int CapacityFrames => (int)_bufferFrames;
    /// <inheritdoc/>
    public int WritableFrames
    {
        get
        {
            if (!_ready || _client is null) return -1;
            uint padding;
            int hr = _client->GetCurrentPadding(&padding);
            if (hr < 0)
            {
                if (IsDeviceLostHr(hr)) MarkLost(hr);
                return -1;
            }
            return Math.Max(0, (int)_bufferFrames - (int)padding);
        }
    }
    /// <summary>Discard queued device PCM. Throws <see cref="AudioDeviceLostException"/> when the device is not ready or
    /// <c>Reset</c> reports it lost; any other failure (e.g. <c>AUDCLNT_E_NOT_STOPPED</c>, a caller bug) still throws the HRESULT.</summary>
    public void Reset()
    {
        if (_client is null || !IsReady) throw new AudioDeviceLostException(_lostHr != 0 ? _lostHr : AudclntEDeviceInvalidated);
        int hr = _client->Reset();
        if (hr < 0)
        {
            if (IsDeviceLostHr(hr))
            {
                MarkLost(hr);
                throw new AudioDeviceLostException(hr);
            }
            System.Runtime.InteropServices.Marshal.ThrowExceptionForHR(hr);
        }
        Interlocked.Exchange(ref _written, 0);
    }
    /// <inheritdoc/>
    /// <remarks>A NEGATIVE <paramref name="timeoutMs"/> means INFINITE (R-3): the session passes -1 while a pause fade has finished
    /// and the device is stopped, so the RT thread sleeps until a control wake instead of spinning a 0 ms poll or ticking at 1 ms.</remarks>
    public void WaitForWritable(WaitHandle controlWake, int timeoutMs)
    {
        // A never-opened (Open failed before CreateEventW) or invalidated device has no period event to wait on:
        // WaitForMultipleObjects on a NULL handle returns WAIT_FAILED immediately and the Highest/MMCSS RT thread would spin
        // at 100 % re-reporting the dead sink (Wavee #112). Wait on the control wake for the period instead.
        if (!IsReady || _event == HANDLE.NULL)
        {
            controlWake.WaitOne(timeoutMs < 0 ? -1 : Math.Max(1, timeoutMs));
            return;
        }
        HANDLE* handles = stackalloc HANDLE[2];
        handles[0] = _event;
        handles[1] = (HANDLE)controlWake.SafeWaitHandle.DangerousGetHandle();
        WaitForMultipleObjects(2, handles, false, timeoutMs < 0 ? 0xFFFFFFFFu /* INFINITE */ : (uint)timeoutMs);
    }

    /// <inheritdoc/>
    public int Write(ReadOnlySpan<float> src, int frames)
    {
        if (!_ready || _render is null || _client is null || frames <= 0) return 0;

        int devCh = _deviceChannels;
        int written = 0;
        bool firstPadding = true;
        // Submit only immediately writable capacity. The caller retains a partial remainder and performs
        // interruptible device/control waiting outside the DSP scope.
        while (written < frames && _ready && !_disposed)
        {
            uint padding;
            int hr = _client->GetCurrentPadding(&padding);
            if (hr < 0)   // device lost → mark inert, return the partial; the session's sink-failure path drives the rebuild
            {
                if (IsDeviceLostHr(hr)) MarkLost(hr);
                break;
            }
            // Device-side glitch signal: a RUNNING stream whose queue is empty when we come to write has already played silence,
            // which no app-side counter can see. Only after a full device buffer has gone through since the last Reset, so
            // a start-up or post-seek prefill (legitimately empty) never counts. One compare + one add: RT-legal.
            if (firstPadding && padding == 0 && _started && _written > _bufferFrames) _deviceUnderruns++;
            firstPadding = false;
            int available = (int)(_bufferFrames - padding);
            if (available <= 0)
            {
                _diagSleeps++;
                break;
            }

            int toWrite = Math.Min(frames - written, available);
            byte* pData;
            hr = _render->GetBuffer((uint)toWrite, &pData);
            if (hr < 0)
            {
                if (IsDeviceLostHr(hr)) MarkLost(hr);
                break;
            }

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

            hr = _render->ReleaseBuffer((uint)toWrite, 0);
            if (hr < 0)
            {
                if (IsDeviceLostHr(hr)) MarkLost(hr);
                break;
            }
            _written += toWrite;
            written += toWrite;
        }

        // 1 Hz feed-vs-play counters (spec §7.x) — bare int/float accumulation ONLY. This runs inside RenderBlock's
        // AudioTripwire (alloc/lock/syscall-free), so no string formatting, delegate invoke, or COM call may happen
        // here — that used to be the bug (a DiagSink invoke with an interpolated string + two TryGetPlayed COM calls,
        // right here, on the RT thread). Once ~1 s has elapsed, hand the interval off via TryTakeStats() instead; the
        // non-RT drain does the formatting, the DiagSink invoke, and the TryGetPlayed COM call this used to do inline.
        _diagReqFrames += frames; _diagWrittenFrames += written; _diagCalls++;
        long nowTicks = Stopwatch.GetTimestamp();
        if (_diagIntervalStartTicks == 0) _diagIntervalStartTicks = nowTicks;
        double elapsedSec = (nowTicks - _diagIntervalStartTicks) / (double)Stopwatch.Frequency;
        if (elapsedSec >= 1.0 && !Volatile.Read(ref _statsPending))
        {
            _pendingReqFrames = _diagReqFrames;
            _pendingWrittenFrames = _diagWrittenFrames;
            _pendingCalls = _diagCalls;
            _pendingSleeps = _diagSleeps;
            _pendingPeak = _diagPeak;
            _pendingClip = (int)_diagClip;
            _pendingNonFinite = (int)_diagNonFinite;
            _pendingElapsedSec = elapsedSec;
            _diagReqFrames = _diagWrittenFrames = 0; _diagCalls = _diagSleeps = 0;
            _diagPeak = 0f; _diagClip = 0; _diagNonFinite = 0;
            _diagIntervalStartTicks = nowTicks;
            Volatile.Write(ref _statsPending, true);   // release: publishes every _pending* write above
        }
        return written;
    }

    /// <summary>Drains the latest ~1 s RT-thread feed-vs-play interval, if one has closed since the last drain (spec
    /// §7.x). MUST be called off the RT feed thread (e.g. <c>AudioFeedThread.ControlTickOnce</c>) — this is where the
    /// <see cref="TryGetPlayed"/> COM call (<c>IAudioClock.GetPosition</c>) for <see cref="AudioDeviceStats.PlayedDelta"/>
    /// now lives, moved off the RT thread for the same reason the formatting was. Returns false (default
    /// <paramref name="stats"/>) when no interval is ready yet — the caller should skip that tick rather than log a
    /// zeroed line.</summary>
    public bool TryTakeStats(out AudioDeviceStats stats)
    {
        stats = default;
        if (!Volatile.Read(ref _statsPending)) return false;

        long reqFrames = _pendingReqFrames, writtenFrames = _pendingWrittenFrames;
        int calls = _pendingCalls, sleeps = _pendingSleeps, clip = _pendingClip, nonFinite = _pendingNonFinite;
        float peak = _pendingPeak;
        double elapsedSec = _pendingElapsedSec;
        Volatile.Write(ref _statsPending, false);   // pairs with Write's release; frees the slot for the next interval

        // PlayedDelta is computed HERE, not on the RT side (see the class-level diagnostics note): TryGetPlayed makes an
        // IAudioClock COM call. The first drain only establishes the baseline (delta 0) — there is no prior sample yet.
        long playedDelta = 0;
        if (TryGetPlayed(out long playedNow, out _))
        {
            if (_diagPlayedBaselineSet) playedDelta = playedNow - _diagPlayedBaseline;
            _diagPlayedBaseline = playedNow;
            _diagPlayedBaselineSet = true;
        }

        stats = new AudioDeviceStats(reqFrames, writtenFrames, calls, sleeps, playedDelta, elapsedSec, peak, clip, nonFinite);
        return true;
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
            // This runs on the RT thread (called from Write), but _devFmtWarned makes it fire at most ONCE ever per
            // device — a single allocation on a format the device never recovers from is an acceptable one-time cost,
            // unlike the per-block diag line this task removes. FormatSink is the always-on channel that survives into
            // a shipping build.
            FormatSink?.Invoke($"UNSUPPORTED device format bits={_devBits} bytesPerFrame={_devBytesPerFrame} - writing silence");
        }
    }

    // ── COM bring-up ─────────────────────────────────────────────────────────────────────────────────────────────────
    // Every early exit of Open goes through here so a failed open is never silent (Wavee #112: the 0.2.8 leaf returned
    // quietly from nine places, and a dead default endpoint was adopted with nothing in the log). Off the RT path.
    private static void Fail(string step, int hr) => FormatSink?.Invoke(FormatOpenFailure(step, hr));

    private void Open(MixFormat requested)
    {
        // Always-on, one line per device open (the matching "open deviceRate=…" line below is the success edge): a
        // WASAPI open that never returns — the 2026-09-22 hand-back from a closed video window opened its byte source,
        // filled its ring and then produced nothing at all — must at least show that it was ENTERED.
        FormatSink?.Invoke($"open.begin requested={requested.SampleRate}Hz/{requested.Channels}ch");
        // COM must be initialized on this thread; MTA is fine for WASAPI. Ignore "already initialized" results.
        _ = CoInitializeEx(null, (uint)(COINIT.COINIT_MULTITHREADED | COINIT.COINIT_DISABLE_OLE1DDE));
        int hr;

        Guid clsidEnum = CLSID.CLSID_MMDeviceEnumerator;
        Guid iidEnum = IID.IID_IMMDeviceEnumerator;
        IMMDeviceEnumerator* enumerator;
        hr = CoCreateInstance(&clsidEnum, null, ClsctxAll, &iidEnum, (void**)&enumerator);
        if (hr < 0) { Fail("CoCreateInstance", hr); return; }
        _enumerator = enumerator;

        IMMDevice* device;
        hr = enumerator->GetDefaultAudioEndpoint(EDataFlow.eRender, ERole.eConsole, &device);
        if (hr < 0) { Fail("GetDefaultAudioEndpoint", hr); return; }
        _device = device;
        EndpointInfo = WasapiEndpoints.Describe(device, isDefault: true);

        Guid iidClient = IID.IID_IAudioClient;
        IAudioClient* client;
        hr = device->Activate(&iidClient, ClsctxAll, null, (void**)&client);
        if (hr < 0) { Fail("Activate", hr); return; }
        _client = client;

        WAVEFORMATEX* mix;
        hr = client->GetMixFormat(&mix);
        if (hr < 0) { Fail("GetMixFormat", hr); return; }

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
        hr = client->Initialize(AUDCLNT_SHAREMODE.AUDCLNT_SHAREMODE_SHARED, StreamFlagsEventCallback, hnsBuffer, 0, mix, null);
        CoTaskMemFree(mix);
        if (hr < 0) { Fail("Initialize", hr); return; }

        // Auto-reset, initially non-signaled; the device sets it whenever a buffer period is ready to be filled.
        _event = CreateEventW(null, BOOL.FALSE, BOOL.FALSE, null);
        if (_event == HANDLE.NULL)
        {
            int win32 = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
            Fail("CreateEventW", win32 <= 0 ? win32 : unchecked((int)0x80070000) | (win32 & 0xFFFF));   // HRESULT_FROM_WIN32
            return;
        }
        hr = client->SetEventHandle(_event);
        if (hr < 0) { Fail("SetEventHandle", hr); return; }

        uint bufferFrames;
        hr = client->GetBufferSize(&bufferFrames);
        if (hr < 0) { Fail("GetBufferSize", hr); return; }
        _bufferFrames = bufferFrames;

        // Open runs once, off the RT path — the ONLY line that answers "what is the user's device sample rate?" for
        // production hiccup triage, so it goes through FormatSink (always-on), not DiagSink (RT-path-only now, and only
        // invoked from the non-RT stats drain elsewhere).
        FormatSink?.Invoke($"open deviceRate={deviceRate} renderRate={Format.SampleRate} devCh={_deviceChannels} renderCh={Format.Channels} devBits={devBits} devFloat={devFloat} floatDirect={_devFloat} bytesPerFrame={_devBytesPerFrame} tag=0x{devTag:X} bufFrames={_bufferFrames}");

        long hnsLatency;
        if (client->GetStreamLatency(&hnsLatency) >= 0)
            _latencyFrames = WasapiPositionMath.LatencyFrames(hnsLatency, Format.SampleRate);

        Guid iidRender = IID.IID_IAudioRenderClient;
        IAudioRenderClient* render;
        hr = client->GetService(&iidRender, (void**)&render);
        if (hr < 0) { Fail("GetService(IAudioRenderClient)", hr); return; }
        _render = render;

        Guid iidClock = IID.IID_IAudioClock;
        IAudioClock* clock;
        if (client->GetService(&iidClock, (void**)&clock) >= 0 && clock is not null)
        {
            _clock = clock;
            ulong freq;
            if (clock->GetFrequency(&freq) >= 0) _clockFreq = freq;
        }

        Volatile.Write(ref _ready, true);
    }

    /// <summary>Release the COM objects and the period event. Never throws; every <c>Release()</c> and the
    /// <c>CloseHandle</c> run even when the device was invalidated mid-stream (the 0.2.9 leaf threw out of <c>Stop()</c>
    /// first and leaked all four pointers plus the event on every jack switch).</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Volatile.Write(ref _ready, false);
        try
        {
            Stop();   // never throws (see Stop); the try/finally is belt-and-braces for the Release chain below
        }
        finally
        {
            try
            {
                if (_clock is not null) { _clock->Release(); _clock = null; }
                if (_render is not null) { _render->Release(); _render = null; }
                if (_client is not null) { _client->Release(); _client = null; }
                if (_device is not null) { _device->Release(); _device = null; }
                if (_enumerator is not null) { _enumerator->Release(); _enumerator = null; }
            }
            finally
            {
                // Handle-close safety (spec §7.9): _disposed/_ready were set above BEFORE this CloseHandle, and by contract
                // the RT feed thread has already been Stop()-joined before we reach here — PcmAudioSession.DisposeAsync
                // disposes the AudioFeedThread (joining the RT thread) before disposing this endpoint, and
                // AudioDeviceController parks the feed via _feed.Stop() before RebuildSink disposes the old endpoint. So no
                // Write should be mid-WaitForSingleObject on _event; the bounded wait + post-wait _disposed re-check in
                // Write contain the residual best-effort-join window.
                if (_event != HANDLE.NULL) { CloseHandle(_event); _event = HANDLE.NULL; }
            }
        }
    }
}
