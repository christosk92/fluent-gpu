using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using Wavee.Backend.Audio;

namespace Wavee.SpotifyLive.Audio;

/// <summary>Minimal WASAPI shared-mode renderer. Source-gen COM (AOT-clean) + AUTOCONVERTPCM so the audio engine
/// resamples our float32 source format to the device mix format — no manual resampler. Blocking push API.</summary>
internal sealed unsafe partial class WasapiRenderer : IDisposable
{
    const int CLSCTX_ALL = 23;
    const int AUDCLNT_SHAREMODE_SHARED = 0;
    const uint AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM = 0x80000000;
    const uint AUDCLNT_STREAMFLAGS_SRC_DEFAULT_QUALITY = 0x08000000;
    const ushort WAVE_FORMAT_IEEE_FLOAT = 3;

    static readonly Guid CLSID_MMDeviceEnumerator = new("BCDE0395-E52F-467C-8E3D-C4579291692E");
    static readonly Guid IID_IMMDeviceEnumerator = new("A95664D2-9614-4F35-A746-DE8DB63617E6");
    static readonly Guid IID_IAudioClient = new("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2");
    static readonly Guid IID_IAudioRenderClient = new("F294ACFC-3146-4483-A7BF-ADDCA7C260E2");

    static readonly StrategyBasedComWrappers ComWrappers = new();

    static readonly Guid IID_IAudioSessionControl = new("F4B1A599-7266-4319-A8CA-E70ACB11E8CD");
    static readonly Guid IID_ISimpleAudioVolume = new("87CE5498-68D6-44E5-9215-6DA47EF883D8");
    static readonly Guid IID_IAudioStreamVolume = new("93014887-242D-4068-8A15-CF5E93B90FE3");

    readonly object _lock = new();   // serialize all COM calls on the client (control thread vs decode/render thread)
    IAudioClient? _client;
    IAudioRenderClient? _render;
    ISimpleAudioVolume? _sessionVolume;
    IAudioStreamVolume? _streamVolume;
    IAudioSessionControl? _sessionControl;
    IntPtr _sessionSinkPtr;   // the sink CCW's IAudioSessionEvents*; re-registered on every (new) session
    int _channels;
    int _sampleRate;
    uint _bufferFrames;
    long _releasedFrames;
    bool _started;
    bool _faulted;   // latched when a WASAPI call reports device loss; the engine polls it
    float _streamScalar = 1f;

    public int SampleRate => _sampleRate;
    public long ReleasedFrames => Interlocked.Read(ref _releasedFrames);
    /// <summary>The concrete endpoint id actually opened (default resolves to its real id) — null before the first Init.</summary>
    public string? OpenedDeviceId { get; private set; }
    /// <summary>True when an explicit device request was unavailable and we fell back to the system default.</summary>
    public bool OpenedAsFallback { get; private set; }
    /// <summary>Latched device-loss flag; the engine polls it and routes a re-init. Cleared on the next successful Init.</summary>
    public bool Faulted => Volatile.Read(ref _faulted);

    public void Init(int sampleRate, int channels, int bufferMs = 300, string? deviceId = null)
    {
        lock (_lock)
        {
            try { _client?.Stop(); } catch { }
            ReleaseSessionLocked();
            _started = false;
            _render = null;
            _client = null;
            _bufferFrames = 0;
            _releasedFrames = 0;
            Volatile.Write(ref _faulted, false);
            _sampleRate = sampleRate;
            _channels = channels;
            OpenedDeviceId = null;
            OpenedAsFallback = false;

            Check(CoInitializeEx(IntPtr.Zero, 0), "CoInitializeEx");
            Guid clsid = CLSID_MMDeviceEnumerator, iid = IID_IMMDeviceEnumerator;
            Check(CoCreateInstance(ref clsid, IntPtr.Zero, CLSCTX_ALL, ref iid, out IntPtr pEnum), "CoCreateInstance(MMDeviceEnumerator)");
            var enumerator = (IMMDeviceEnumerator)ComWrappers.GetOrCreateObjectForComInstance(pEnum, CreateObjectFlags.None);
            Marshal.Release(pEnum);

            IMMDevice? device = null;
            bool fallback = false;
            if (!string.IsNullOrEmpty(deviceId))
            {
                if (enumerator.GetDevice(deviceId!, out IMMDevice explicitDev) >= 0 && explicitDev is not null)
                    device = explicitDev;
                else
                    fallback = true;   // explicit device missing → fall back to default (logged by the engine via a device notice)
            }
            if (device is null)
            {
                Check(enumerator.GetDefaultAudioEndpoint(0 /*eRender*/, 0 /*eConsole*/, out IMMDevice def), "GetDefaultAudioEndpoint");
                device = def;
            }
            OpenedAsFallback = fallback;
            if (device.GetId(out IntPtr pDevId) >= 0 && pDevId != IntPtr.Zero)
            {
                OpenedDeviceId = Marshal.PtrToStringUni(pDevId);
                Marshal.FreeCoTaskMem(pDevId);
            }

            Guid iidClient = IID_IAudioClient;
            Check(device.Activate(ref iidClient, CLSCTX_ALL, IntPtr.Zero, out IntPtr pClient), "IMMDevice.Activate(IAudioClient)");
            var client = (IAudioClient)ComWrappers.GetOrCreateObjectForComInstance(pClient, CreateObjectFlags.None);
            Marshal.Release(pClient);

            var fmt = new WAVEFORMATEX
            {
                wFormatTag = WAVE_FORMAT_IEEE_FLOAT,
                nChannels = (ushort)channels,
                nSamplesPerSec = (uint)sampleRate,
                wBitsPerSample = 32,
                nBlockAlign = (ushort)(channels * 4),
                nAvgBytesPerSec = (uint)(sampleRate * channels * 4),
                cbSize = 0,
            };
            long bufDuration = bufferMs * 10_000L; // 100ns units
            uint flags = AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM | AUDCLNT_STREAMFLAGS_SRC_DEFAULT_QUALITY;
            Check(client.Initialize(AUDCLNT_SHAREMODE_SHARED, flags, bufDuration, 0, (IntPtr)(&fmt), IntPtr.Zero), "IAudioClient.Initialize");
            Check(client.GetBufferSize(out var bufferFrames), "GetBufferSize");

            Guid iidRender = IID_IAudioRenderClient;
            Check(client.GetService(ref iidRender, out IntPtr pRender), "GetService(IAudioRenderClient)");
            var render = (IAudioRenderClient)ComWrappers.GetOrCreateObjectForComInstance(pRender, CreateObjectFlags.None);
            Marshal.Release(pRender);

            _client = client;
            _render = render;
            _bufferFrames = bufferFrames;

            Guid iidStreamVolume = IID_IAudioStreamVolume;
            if (client.GetService(ref iidStreamVolume, out IntPtr pStreamVolume) >= 0 && pStreamVolume != IntPtr.Zero)
            {
                _streamVolume = (IAudioStreamVolume)ComWrappers.GetOrCreateObjectForComInstance(pStreamVolume, CreateObjectFlags.None);
                Marshal.Release(pStreamVolume);
                ApplyStreamVolumeLocked();
            }

            // Session accessors (Phase B — a new session per Init). Best-effort: a missing session control just skips
            // display-name / two-way volume; core rendering is unaffected.
            AcquireSessionLocked(client);
        }
    }

    // ── Windows session (Phase B) ────────────────────────────────────────────────────────────────────────────────────
    void AcquireSessionLocked(IAudioClient client)
    {
        try
        {
            Guid iidVol = IID_ISimpleAudioVolume;
            if (client.GetService(ref iidVol, out IntPtr pVol) >= 0 && pVol != IntPtr.Zero)
            {
                _sessionVolume = (ISimpleAudioVolume)ComWrappers.GetOrCreateObjectForComInstance(pVol, CreateObjectFlags.None);
                Marshal.Release(pVol);
            }
            Guid iidCtl = IID_IAudioSessionControl;
            if (client.GetService(ref iidCtl, out IntPtr pCtl) >= 0 && pCtl != IntPtr.Zero)
            {
                _sessionControl = (IAudioSessionControl)ComWrappers.GetOrCreateObjectForComInstance(pCtl, CreateObjectFlags.None);
                Marshal.Release(pCtl);
                SetSessionDisplayNameLocked("Wavee");
                if (_sessionSinkPtr != IntPtr.Zero) { try { _sessionControl.RegisterAudioSessionNotification(_sessionSinkPtr); } catch { } }
            }
        }
        catch { }
    }

    void ReleaseSessionLocked()
    {
        if (_sessionControl is not null && _sessionSinkPtr != IntPtr.Zero)
            try { _sessionControl.UnregisterAudioSessionNotification(_sessionSinkPtr); } catch { }
        _sessionControl = null;
        _sessionVolume = null;
        _streamVolume = null;
    }

    void SetSessionDisplayNameLocked(string name)
    {
        if (_sessionControl is null) return;
        IntPtr p = Marshal.StringToCoTaskMemUni(name);
        try { Guid empty = Guid.Empty; _sessionControl.SetDisplayName(p, in empty); }
        catch { }
        finally { Marshal.FreeCoTaskMem(p); }
    }

    /// <summary>Give the renderer the session-events sink CCW (IAudioSessionEvents*); it is (re-)registered on every session.</summary>
    public void RegisterSessionEvents(IntPtr sessionEvents)
    {
        lock (_lock)
        {
            _sessionSinkPtr = sessionEvents;
            if (_sessionControl is not null && sessionEvents != IntPtr.Zero)
                try { _sessionControl.RegisterAudioSessionNotification(sessionEvents); } catch { }
        }
    }

    public void UnregisterSessionEvents()
    {
        lock (_lock)
        {
            if (_sessionControl is not null && _sessionSinkPtr != IntPtr.Zero)
                try { _sessionControl.UnregisterAudioSessionNotification(_sessionSinkPtr); } catch { }
            _sessionSinkPtr = IntPtr.Zero;
        }
    }

    /// <summary>Set the Windows session scalar (linear amplitude 0..1) with our event context (so the sink filters the echo).</summary>
    public void SetSessionVolume(float scalar, in Guid ctx)
    {
        lock (_lock) { if (_sessionVolume is not null) try { _sessionVolume.SetMasterVolume(Math.Clamp(scalar, 0f, 1f), in ctx); } catch { } }
    }

    public void SetSessionMute(bool muted, in Guid ctx)
    {
        lock (_lock) { if (_sessionVolume is not null) try { _sessionVolume.SetMute(muted ? 1 : 0, in ctx); } catch { } }
    }

    /// <summary>Set this render stream's scalar without changing the shared Wavee session volume.</summary>
    public void SetStreamVolume(float scalar)
    {
        lock (_lock)
        {
            _streamScalar = Math.Clamp(scalar, 0f, 1f);
            ApplyStreamVolumeLocked();
        }
    }

    void ApplyStreamVolumeLocked()
    {
        if (_streamVolume is null) return;
        try
        {
            if (_streamVolume.GetChannelCount(out uint channels) < 0) return;
            for (uint i = 0; i < channels; i++) _streamVolume.SetChannelVolume(i, _streamScalar);
        }
        catch { }
    }

    public bool TryGetSessionVolume(out float scalar, out bool muted)
    {
        lock (_lock)
        {
            scalar = 1f; muted = false;
            if (_sessionVolume is null) return false;
            try
            {
                if (_sessionVolume.GetMasterVolume(out float lvl) < 0) return false;
                scalar = lvl;
                if (_sessionVolume.GetMute(out int m) >= 0) muted = m != 0;
                return true;
            }
            catch { return false; }
        }
    }

    public void Start()
    {
        lock (_lock)
        {
            if (_client is null || _started) return;
            Check(_client.Start(), "Start");
            _started = true;
        }
    }

    public void Pause()
    {
        lock (_lock)
        {
            if (_client is null || !_started) return;
            Check(_client.Stop(), "Stop");
            _started = false;
        }
    }

    public void Reset()
    {
        lock (_lock)
        {
            if (_client is null) return;
            try { _client.Stop(); } catch { }
            _started = false;
            try { _client.Reset(); } catch { }
            _releasedFrames = 0;
        }
    }

    /// <summary>Approximate frames actually played (released minus what's still buffered). A failing HRESULT (device loss)
    /// returns the cached value AND latches <see cref="Faulted"/> so the engine reroutes instead of stalling.</summary>
    public long PlayedFrames
    {
        get
        {
            lock (_lock)
            {
                if (_client is null) return 0;
                int hr = _client.GetCurrentPadding(out uint padding);
                if (hr < 0) { Volatile.Write(ref _faulted, true); return _releasedFrames; }
                return Math.Max(0, _releasedFrames - padding);
            }
        }
    }

    public long PositionMs => _sampleRate > 0 ? PlayedFrames * 1000 / _sampleRate : 0;

    /// <summary>Push interleaved float frames, blocking until all are queued (or cancelled). Applies realtime volume.
    /// When paused (client stopped) the engine stops consuming, the buffer stays full, and this naturally blocks.</summary>
    public void Write(ReadOnlySpan<float> interleaved, CancellationToken ct)
    {
        if (_render is null || _client is null) return;
        int totalFrames = interleaved.Length / _channels;
        int written = 0;
        while (written < totalFrames)
        {
            ct.ThrowIfCancellationRequested();
            uint chunk;
            lock (_lock)
            {
                if (_client is null || _render is null) return;
                // Volume now lives at the Windows-session boundary (Phase B) — the PCM is written verbatim; normalization
                // gain + EQ + limiter already ran in the decode loop.
                int padHr = _client.GetCurrentPadding(out uint padding);
                if (padHr < 0) { Volatile.Write(ref _faulted, true); throw new AudioDeviceInvalidatedException(padHr); }
                uint avail = _bufferFrames - padding;
                chunk = (uint)Math.Min(avail, (uint)(totalFrames - written));
                if (chunk > 0)
                {
                    int bufHr = _render.GetBuffer(chunk, out IntPtr buf);
                    if (bufHr < 0) { Volatile.Write(ref _faulted, true); throw new AudioDeviceInvalidatedException(bufHr); }
                    if (buf != IntPtr.Zero)
                    {
                        float* dst = (float*)buf;
                        var src = interleaved.Slice(written * _channels, (int)chunk * _channels);
                        for (int i = 0; i < src.Length; i++) dst[i] = src[i];
                        _render.ReleaseBuffer(chunk, 0);
                        _releasedFrames += chunk;
                        written += (int)chunk;
                    }
                    else chunk = 0;
                }
            }
            if (chunk == 0) Thread.Sleep(5);
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            try { _client?.Stop(); } catch { }
            ReleaseSessionLocked();
            _sessionSinkPtr = IntPtr.Zero;   // the CCW itself is owned/released by the engine
            _started = false;
            _render = null;
            _client = null;
            _releasedFrames = 0;
        }
    }

    static void Check(int hr, string what)
    {
        if (hr < 0) throw new InvalidOperationException($"{what} failed: 0x{hr:X8}");
    }

    [LibraryImport("ole32.dll")]
    private static partial int CoInitializeEx(IntPtr reserved, uint coInit);

    [LibraryImport("ole32.dll")]
    private static partial int CoCreateInstance(ref Guid clsid, IntPtr outer, int clsContext, ref Guid iid, out IntPtr ppv);

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct WAVEFORMATEX
    {
        public ushort wFormatTag;
        public ushort nChannels;
        public uint nSamplesPerSec;
        public uint nAvgBytesPerSec;
        public ushort nBlockAlign;
        public ushort wBitsPerSample;
        public ushort cbSize;
    }
}

// COM vtable order MUST match the native interface exactly; unused leading methods are declared to preserve slots.
[GeneratedComInterface, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
internal partial interface IMMDeviceEnumerator
{
    [PreserveSig] int EnumAudioEndpoints(int dataFlow, uint stateMask, out IntPtr devices);
    [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
    [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
    [PreserveSig] int RegisterEndpointNotificationCallback(IntPtr client);
    [PreserveSig] int UnregisterEndpointNotificationCallback(IntPtr client);
}

[GeneratedComInterface, Guid("D666063F-1587-4E43-81F1-B948E807363F")]
internal partial interface IMMDevice
{
    [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, out IntPtr ppInterface);
    [PreserveSig] int OpenPropertyStore(uint access, out IntPtr properties);
    [PreserveSig] int GetId(out IntPtr strId);
    [PreserveSig] int GetState(out uint state);
}

[GeneratedComInterface, Guid("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2")]
internal partial interface IAudioClient
{
    [PreserveSig] int Initialize(int shareMode, uint streamFlags, long hnsBufferDuration, long hnsPeriodicity, IntPtr format, IntPtr audioSessionGuid);
    [PreserveSig] int GetBufferSize(out uint numBufferFrames);
    [PreserveSig] int GetStreamLatency(out long latency);
    [PreserveSig] int GetCurrentPadding(out uint numPaddingFrames);
    [PreserveSig] int IsFormatSupported(int shareMode, IntPtr format, out IntPtr closestMatch);
    [PreserveSig] int GetMixFormat(out IntPtr deviceFormat);
    [PreserveSig] int GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);
    [PreserveSig] int Start();
    [PreserveSig] int Stop();
    [PreserveSig] int Reset();
    [PreserveSig] int SetEventHandle(IntPtr eventHandle);
    [PreserveSig] int GetService(ref Guid iid, out IntPtr ppv);
}

[GeneratedComInterface, Guid("F294ACFC-3146-4483-A7BF-ADDCA7C260E2")]
internal partial interface IAudioRenderClient
{
    [PreserveSig] int GetBuffer(uint numFramesRequested, out IntPtr data);
    [PreserveSig] int ReleaseBuffer(uint numFramesWritten, uint flags);
}
