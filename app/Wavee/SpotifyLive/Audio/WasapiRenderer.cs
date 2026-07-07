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

    readonly object _lock = new();   // serialize all COM calls on the client (control thread vs decode/render thread)
    IAudioClient? _client;
    IAudioRenderClient? _render;
    int _channels;
    int _sampleRate;
    uint _bufferFrames;
    long _releasedFrames;
    volatile float _volume = 1f;
    bool _started;

    public int SampleRate => _sampleRate;
    public long ReleasedFrames => Interlocked.Read(ref _releasedFrames);

    public void Init(int sampleRate, int channels, int bufferMs = 300)
    {
        lock (_lock)
        {
            try { _client?.Stop(); } catch { }
            _started = false;
            _render = null;
            _client = null;
            _bufferFrames = 0;
            _releasedFrames = 0;
            _sampleRate = sampleRate;
            _channels = channels;

            Check(CoInitializeEx(IntPtr.Zero, 0), "CoInitializeEx");
            Guid clsid = CLSID_MMDeviceEnumerator, iid = IID_IMMDeviceEnumerator;
            Check(CoCreateInstance(ref clsid, IntPtr.Zero, CLSCTX_ALL, ref iid, out IntPtr pEnum), "CoCreateInstance(MMDeviceEnumerator)");
            var enumerator = (IMMDeviceEnumerator)ComWrappers.GetOrCreateObjectForComInstance(pEnum, CreateObjectFlags.None);
            Marshal.Release(pEnum);

            Check(enumerator.GetDefaultAudioEndpoint(0 /*eRender*/, 0 /*eConsole*/, out IMMDevice device), "GetDefaultAudioEndpoint");

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

    public void SetVolume(float v) => _volume = VolumeTaper.Amplitude(v);

    /// <summary>Approximate frames actually played (released minus what's still buffered).</summary>
    public long PlayedFrames
    {
        get
        {
            lock (_lock)
            {
                if (_client is null) return 0;
                if (_client.GetCurrentPadding(out uint padding) != 0) return _releasedFrames;
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
                if (_client.GetCurrentPadding(out uint padding) != 0) { chunk = 0; }
                else
                {
                    uint avail = _bufferFrames - padding;
                    chunk = (uint)Math.Min(avail, (uint)(totalFrames - written));
                    if (chunk > 0 && _render.GetBuffer(chunk, out IntPtr buf) == 0 && buf != IntPtr.Zero)
                    {
                        float vol = _volume;
                        float* dst = (float*)buf;
                        var src = interleaved.Slice(written * _channels, (int)chunk * _channels);
                        for (int i = 0; i < src.Length; i++) dst[i] = src[i] * vol;
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
