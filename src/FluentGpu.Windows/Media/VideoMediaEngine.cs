using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;
using static TerraFX.Interop.DirectX.DirectX;
using static TerraFX.Interop.Windows.Windows;

namespace FluentGpu.Media.Windows;

/// <summary>
/// The "real unprotected video" milestone (M3) of the DRM-free video compositing spine
/// (<c>docs/plans/video-compositing-spine-design.md</c>). Drives <c>IMFMediaEngineEx</c> in <b>windowless
/// swap-chain mode</b> to decode a CLEAR (non-DRM) progressive MP4 and hand its DirectComposition swap-chain HANDLE
/// to the engine's <see cref="FluentGpu.Pal.IVideoPresenter"/> (via <c>CreateSurfaceFromHandle</c> → <c>SetContent</c>),
/// so decoded frames composite as the sibling video visual z-BELOW the UI — the SAME path DRM will reuse later by
/// attaching a CDM. No PlayReady / no protected content here.
///
/// <para>Sequence (MS Learn <c>EnableWindowlessSwapchainMode</c> + the microsoft/media-foundation
/// <c>MediaEngineDCompWin32Sample</c>): create a D3D11 video device + <c>IMFDXGIDeviceManager</c> → create
/// <c>IMFMediaEngine</c> with an <c>IMFMediaEngineNotify</c> callback + the DXGI manager → QI <c>IMFMediaEngineEx</c> →
/// <c>EnableWindowlessSwapchainMode(TRUE)</c> → <c>SetSource(url)</c> → <c>Play()</c>. On <c>LOADEDMETADATA</c>:
/// <c>GetVideoSwapchainHandle(&amp;h)</c> → bind <c>h</c> to the DComp video visual → <c>UpdateVideoStream(NULL, &amp;dst,
/// &amp;border)</c> (dst = swap-chain-local {0,0,w,h}). Thereafter the Media Engine auto-presents each decoded frame into
/// its windowless swap chain; <see cref="OnVideoStreamTick"/> + <see cref="RepaintCurrentFrame"/> force a repaint.</para>
///
/// <para>Threading (matches the sample's dedicated <c>COMThread</c>): the app UI thread is an OleInitialize'd STA — with a
/// hardware <c>IMFDXGIDeviceManager</c> attached, the Media Engine's video-device setup on MF worker threads DEADLOCKS
/// source resolution against that blocked STA (empirically it never leaves HAVE_NOTHING/WAITING). So EVERY engine COM call
/// (device + DXGI manager + engine creation + <c>UpdateVideoStream</c>/tick + teardown) runs on a dedicated <b>MTA</b>
/// thread here; public methods marshal onto it. MF fires <c>EventNotify</c> on its own workers → the callback only sets
/// volatile flags. TerraFX (MF/D3D11) stays inside FluentGpu.Windows.</para>
/// </summary>
public sealed unsafe class VideoMediaEngine : IDisposable, IVideoEngine
{
    private const uint MFSTARTUP_FULL_ = 0;
    private const int S_OK = 0;

    // ── Owned only on the engine (MTA) thread ──────────────────────────────────────────────────────────────────────
    private ID3D11Device* _d3d;
    private IMFDXGIDeviceManager* _dxgiManager;
    private IMFMediaEngine* _engine;
    private IMFMediaEngineEx* _engineEx;
    private MediaEngineNotifyCcw* _notify;
    private GCHandle _selfHandle;
    private bool _mfStarted;

    // ── Engine-thread marshaling ───────────────────────────────────────────────────────────────────────────────────
    private Thread? _thread;
    private readonly BlockingCollection<Action> _work = new();
    private readonly ManualResetEventSlim _initDone = new(false);
    private int _initHr = unchecked((int)0x80004005);

    // ── Event state (set on MF worker threads, read anywhere) ──────────────────────────────────────────────────────
    private volatile int _lastEventRaw = -1;
    private volatile bool _metadataLoaded;
    private volatile bool _canPlay;
    private volatile bool _playing;
    private volatile bool _seeking;
    private volatile bool _ended;
    private volatile bool _error;
    private volatile uint _errorCode;
    private volatile int _errorHr;
    private readonly ConcurrentQueue<string> _trace = new();

    public bool MetadataLoaded => _metadataLoaded;
    public bool CanPlay => _canPlay;
    public bool Playing => _playing;
    public bool Seeking => _seeking;
    public bool Ended => _ended;
    public bool HasError => _error;
    public uint ErrorCode => _errorCode;
    public int ErrorHr => _errorHr;
    public string EventTrace => string.Join(",", _trace);
    public string LastEventName => _lastEventRaw < 0 ? "<none>" : ((MF_MEDIA_ENGINE_EVENT)_lastEventRaw).ToString().Replace("MF_MEDIA_ENGINE_EVENT_", "");

    /// <summary>The current media-engine readyState (0 HAVE_NOTHING … 4 HAVE_ENOUGH_DATA).</summary>
    public uint ReadyState => Invoke(() => _engine != null ? _engine->GetReadyState() : (ushort)0);

    /// <summary>
    /// Spin up the dedicated MTA engine thread, stand up the D3D11 video device + DXGI manager, create the Media Engine,
    /// enable windowless swap-chain mode, set the source and start playback. Returns the last HRESULT (S_OK on success).
    /// </summary>
    public int Initialize(string url)
    {
        _thread = new Thread(() => ThreadMain(url)) { IsBackground = true, Name = "VideoMediaEngine" };
        _thread.SetApartmentState(ApartmentState.MTA);
        _thread.Start();
        _initDone.Wait();
        return _initHr;
    }

    private void ThreadMain(string url)
    {
        _initHr = CreateEngine(url);
        _initDone.Set();
        if (_initHr < 0) { DisposeCom(); return; }
        // Service marshaled engine calls until Dispose completes the queue, then tear down COM on THIS thread.
        foreach (var w in _work.GetConsumingEnumerable())
        {
            try { w(); } catch (Exception ex) { Console.Error.WriteLine($"VideoMediaEngine: work item threw: {ex.Message}"); }
        }
        DisposeCom();
    }

    private int CreateEngine(string url)
    {
        // Diagnostic toggles (real runs proved the defaults): FG_VIDEO_NODXGI=1 → no DXGI manager/windowless (frame-server,
        // no swap-chain handle); FG_VIDEO_NOVIDSUP=1 → drop D3D11_CREATE_DEVICE_VIDEO_SUPPORT.
        bool noDxgi = Environment.GetEnvironmentVariable("FG_VIDEO_NODXGI") == "1";

        int hr;
        if ((hr = MFStartup(MF_VERSION_(), MFSTARTUP_FULL_)) < 0) return Log("MFStartup", hr);
        _mfStarted = true;

        if (!noDxgi && (hr = CreateD3D11AndManager()) < 0) return hr;

        _selfHandle = GCHandle.Alloc(this);
        _notify = MediaEngineNotifyCcw.Create(GCHandle.ToIntPtr(_selfHandle));

        IMFAttributes* attrs = null;
        IMFMediaEngineClassFactory* factory = null;
        try
        {
            if ((hr = MFCreateAttributes(&attrs, 4)) < 0) return Log("MFCreateAttributes", hr);
            Guid gCb = MF.MF_MEDIA_ENGINE_CALLBACK; attrs->SetUnknown(&gCb, (IUnknown*)_notify);
            if (_dxgiManager != null) { Guid gDm = MF.MF_MEDIA_ENGINE_DXGI_MANAGER; attrs->SetUnknown(&gDm, (IUnknown*)_dxgiManager); }
            Guid gFmt = MF.MF_MEDIA_ENGINE_VIDEO_OUTPUT_FORMAT; attrs->SetUINT32(&gFmt, (uint)DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM);

            Guid clsid = CLSID.CLSID_MFMediaEngineClassFactory;
            Guid iidF = IID.IID_IMFMediaEngineClassFactory;
            IMFMediaEngineClassFactory* fac;
            if ((hr = CoCreateInstance(&clsid, null, (uint)CLSCTX.CLSCTX_INPROC_SERVER, &iidF, (void**)&fac)) < 0)
                return Log("CoCreateInstance(MFMediaEngineClassFactory)", hr);
            factory = fac;

            IMFMediaEngine* engine;
            if ((hr = factory->CreateInstance(0, attrs, &engine)) < 0 || engine == null)
                return Log("IMFMediaEngineClassFactory::CreateInstance", hr);
            _engine = engine;
        }
        finally
        {
            if (factory != null) factory->Release();
            if (attrs != null) attrs->Release();
        }

        Guid iidEx = IID.IID_IMFMediaEngineEx;
        IMFMediaEngineEx* ex;
        if ((hr = _engine->QueryInterface(&iidEx, (void**)&ex)) < 0 || ex == null) return Log("QI IMFMediaEngineEx", hr);
        _engineEx = ex;
        bool windowless = !noDxgi && Environment.GetEnvironmentVariable("FG_VIDEO_NOWINDOWLESS") != "1";
        if (windowless && (hr = _engineEx->EnableWindowlessSwapchainMode(true)) < 0)
            return Log("EnableWindowlessSwapchainMode(TRUE)", hr);

        fixed (char* pUrl = url)
            if ((hr = _engineEx->SetSource(pUrl)) < 0) return Log("SetSource", hr);
        _engine->SetLoop(true);   // keep a live frame available (so a capture never lands on ENDED)
        _engine->Play();
        return S_OK;
    }

    private int CreateD3D11AndManager()
    {
        ID3D11DeviceContext* ctx = null;
        bool noVidSup = Environment.GetEnvironmentVariable("FG_VIDEO_NOVIDSUP") == "1";
        uint flags = (uint)D3D11_CREATE_DEVICE_FLAG.D3D11_CREATE_DEVICE_BGRA_SUPPORT;
        if (!noVidSup) flags |= (uint)D3D11_CREATE_DEVICE_FLAG.D3D11_CREATE_DEVICE_VIDEO_SUPPORT;
        ID3D11Device* d3d = null;
        int hr = D3D11CreateDevice(null, D3D_DRIVER_TYPE.D3D_DRIVER_TYPE_HARDWARE, HMODULE.NULL, flags,
                                   null, 0, 7 /*D3D11_SDK_VERSION*/, &d3d, null, &ctx);
        if (hr < 0 || d3d == null) return Log("D3D11CreateDevice", hr);
        _d3d = d3d;
        if (ctx != null) ctx->Release();

        // Mark multithread-protected. REQUIRED when the D3D11 device is shared with Media Foundation — MF drives the
        // device from its own worker threads and without this it deadlocks during source resolution (the hang the M3
        // probe misdiagnosed as a driver bug). ID3D10Multithread vtable: 0-2 IUnknown, 3 Enter, 4 Leave,
        // 5 SetMultithreadProtected(BOOL)->BOOL, 6 GetMultithreadProtected. Use slot 5 (an earlier version wrongly
        // called slot 3 = Enter, which is why protection was never actually enabled).
        Guid iidMt = new(0x9b7e4e00, 0x342c, 0x4106, 0xa1, 0x9f, 0x4f, 0x27, 0x04, 0xf6, 0x89, 0xf0);
        void* mt = null;
        if (d3d->QueryInterface(&iidMt, &mt) >= 0 && mt != null)
        {
            var setProt = (delegate* unmanaged[MemberFunction]<void*, int, int>)(*(void***)mt)[5];
            setProt(mt, 1);
            ((IUnknown*)mt)->Release();
        }

        uint resetToken = 0;
        IMFDXGIDeviceManager* dm = null;
        if ((hr = MFCreateDXGIDeviceManager(&resetToken, &dm)) < 0 || dm == null) return Log("MFCreateDXGIDeviceManager", hr);
        if ((hr = dm->ResetDevice((IUnknown*)d3d, resetToken)) < 0) { dm->Release(); return Log("IMFDXGIDeviceManager::ResetDevice", hr); }
        _dxgiManager = dm;
        return S_OK;
    }

    /// <summary>
    /// Fetch the windowless swap-chain HANDLE the Media Engine created (valid after <c>LOADEDMETADATA</c>). Bind this to a
    /// DComp visual via <c>IVideoPresenter.BindSurfaceHandle</c>. Returns 0 on failure. Runs on the engine thread.
    /// </summary>
    public nuint GetSwapchainHandle() => Invoke(() =>
    {
        if (_engineEx == null) return (nuint)0;
        HANDLE h;
        int hr = _engineEx->GetVideoSwapchainHandle(&h);
        if (hr < 0) { Log("GetVideoSwapchainHandle", hr); return (nuint)0; }
        return (nuint)(nint)h;
    });

    /// <summary>Native decoded video size (px). Valid after <c>LOADEDMETADATA</c>.</summary>
    public bool TryGetNativeVideoSize(out uint cx, out uint cy)
    {
        (bool ok, uint w, uint h) = Invoke(() =>
        {
            if (_engineEx == null) return (false, 0u, 0u);
            uint a, b;
            if (_engineEx->GetNativeVideoSize(&a, &b) < 0) return (false, 0u, 0u);
            return (a > 0 && b > 0, a, b);
        });
        cx = w; cy = h; return ok;
    }

    /// <summary>
    /// Set the destination rectangle for the video within its windowless swap chain (swap-chain-local coords, {0,0,w,h})
    /// with an opaque black border — <c>UpdateVideoStream(NULL, &amp;dst, &amp;border)</c>. Called once after binding.
    /// </summary>
    public int SetVideoStreamRect(int w, int h) => Invoke(() =>
    {
        if (_engineEx == null) return -1;
        RECT dst = new() { left = 0, top = 0, right = w, bottom = h };
        MFARGB border = new() { rgbBlue = 0, rgbGreen = 0, rgbRed = 0, rgbAlpha = 255 };
        int hr = _engineEx->UpdateVideoStream(null, &dst, &border);
        if (hr < 0) Log("UpdateVideoStream(dst)", hr);
        return hr;
    });

    /// <summary>Repaint the most-recently-decoded frame into the swap chain (all-NULL UpdateVideoStream).</summary>
    public void RepaintCurrentFrame() => Invoke(() =>
    {
        if (_engineEx != null) _engineEx->UpdateVideoStream(null, null, null);
        return 0;
    });

    /// <summary>Poll for a freshly-decoded frame (windowless "video-stream-tick"); true (+ pts) when a new frame is ready.</summary>
    public bool OnVideoStreamTick(out long pts)
    {
        (bool ok, long p) = Invoke(() =>
        {
            if (_engine == null) return (false, 0L);
            long t;
            int hr = _engine->OnVideoStreamTick(&t);
            return (hr == S_OK, t);   // S_FALSE ⇒ no new frame
        });
        pts = p; return ok;
    }

    // ── Transport + clock (marshaled onto the engine thread; IMFMediaEngine is single-thread-affine) ───────────────

    /// <summary>Media duration in seconds (0 until known; a non-finite duration — live/looping — surfaces as 0 so the
    /// caller treats it as unknown rather than +Inf).</summary>
    public double DurationSeconds => Invoke(() =>
    {
        if (_engine == null) return 0.0;
        double d = _engine->GetDuration();
        return double.IsFinite(d) && d > 0 ? d : 0.0;
    });

    /// <summary>Current presentation time in seconds (the authoritative clock).</summary>
    public double CurrentTimeSeconds => Invoke(() =>
    {
        if (_engine == null) return 0.0;
        double t = _engine->GetCurrentTime();
        return double.IsFinite(t) && t > 0 ? t : 0.0;
    });

    /// <summary>Resume playback.</summary>
    public void Play() => Invoke(() => { if (_engine != null) _engine->Play(); return 0; });

    /// <summary>Pause playback (the frame stays live for compositing).</summary>
    public void Pause() => Invoke(() => { if (_engine != null) _engine->Pause(); return 0; });

    /// <summary>Seek: set the current presentation time.</summary>
    public void SeekTo(double seconds) => Invoke(() => { if (_engine != null) _engine->SetCurrentTime(seconds < 0 ? 0 : seconds); return 0; });

    /// <summary>Set the playback rate (1.0 = normal).</summary>
    public void SetPlaybackRate(double rate) => Invoke(() => { if (_engine != null) _engine->SetPlaybackRate(rate); return 0; });

    /// <summary>Set the output volume (0..1).</summary>
    public void SetVolume(double volume) => Invoke(() => { if (_engine != null) _engine->SetVolume(volume < 0 ? 0 : (volume > 1 ? 1 : volume)); return 0; });

    /// <summary>Mute/unmute.</summary>
    public void SetMuted(bool muted) => Invoke(() => { if (_engine != null) _engine->SetMuted(muted); return 0; });

    /// <summary>Toggle native looping.</summary>
    public void SetLoop(bool loop) => Invoke(() => { if (_engine != null) _engine->SetLoop(loop); return 0; });

    // ── Notify sink (MF worker threads) ────────────────────────────────────────────────────────────────────────────
    internal void OnEngineEvent(uint ev, nuint p1, uint p2)
    {
        _lastEventRaw = (int)ev;
        _trace.Enqueue(((MF_MEDIA_ENGINE_EVENT)ev).ToString().Replace("MF_MEDIA_ENGINE_EVENT_", ""));
        switch ((MF_MEDIA_ENGINE_EVENT)ev)
        {
            case MF_MEDIA_ENGINE_EVENT.MF_MEDIA_ENGINE_EVENT_LOADEDMETADATA: _metadataLoaded = true; break;
            case MF_MEDIA_ENGINE_EVENT.MF_MEDIA_ENGINE_EVENT_CANPLAY: _canPlay = true; break;
            case MF_MEDIA_ENGINE_EVENT.MF_MEDIA_ENGINE_EVENT_PLAY: _ended = false; break;
            case MF_MEDIA_ENGINE_EVENT.MF_MEDIA_ENGINE_EVENT_PLAYING: _playing = true; _canPlay = true; _ended = false; break;
            case MF_MEDIA_ENGINE_EVENT.MF_MEDIA_ENGINE_EVENT_PAUSE: _playing = false; break;
            case MF_MEDIA_ENGINE_EVENT.MF_MEDIA_ENGINE_EVENT_SEEKING: _seeking = true; break;
            case MF_MEDIA_ENGINE_EVENT.MF_MEDIA_ENGINE_EVENT_SEEKED: _seeking = false; break;
            case MF_MEDIA_ENGINE_EVENT.MF_MEDIA_ENGINE_EVENT_ENDED: _ended = true; _playing = false; break;
            case MF_MEDIA_ENGINE_EVENT.MF_MEDIA_ENGINE_EVENT_ERROR: _error = true; _errorCode = (uint)p1; _errorHr = (int)p2; break;
        }
    }

    // Marshal a func onto the engine thread and wait (the engine's COM is single-thread-affine to that MTA thread).
    private T Invoke<T>(Func<T> f)
    {
        if (_thread == null || Thread.CurrentThread == _thread) return f();
        if (_work.IsAddingCompleted) return default!;
        T result = default!;
        using var done = new ManualResetEventSlim(false);
        try { _work.Add(() => { try { result = f(); } finally { done.Set(); } }); }
        catch (InvalidOperationException) { return default!; }   // queue completed during teardown
        done.Wait();
        return result;
    }

    private static uint MF_VERSION_() => (uint)MF.MF_VERSION;

    private static int Log(string what, int hr)
    {
        Console.Error.WriteLine($"VideoMediaEngine: {what} hr=0x{(uint)hr:X8}");
        return hr;
    }

    // Runs on the engine thread (via ThreadMain) — never touch the COM ptrs off it.
    private void DisposeCom()
    {
        if (_engine != null) _engine->Shutdown();
        if (_engineEx != null) { _engineEx->Release(); _engineEx = null; }
        if (_engine != null) { _engine->Release(); _engine = null; }
        if (_dxgiManager != null) { _dxgiManager->Release(); _dxgiManager = null; }
        if (_d3d != null) { _d3d->Release(); _d3d = null; }
        if (_notify != null) { MediaEngineNotifyCcw.Destroy(_notify); _notify = null; }
        if (_selfHandle.IsAllocated) _selfHandle.Free();
        if (_mfStarted) { MFShutdown(); _mfStarted = false; }
    }

    public void Dispose()
    {
        _work.CompleteAdding();     // ThreadMain drains, then DisposeCom() on the engine thread
        _thread?.Join(2000);
        _initDone.Dispose();
    }
}

/// <summary>
/// Hand-rolled CCW for <c>IMFMediaEngineNotify</c> (single <c>EventNotify</c> method). Carries a <c>GCHandle</c> to its
/// owning <see cref="VideoMediaEngine"/> so the <c>[UnmanagedCallersOnly]</c> thunk (which cannot close over instance
/// state) routes events back to the instance. Mirrors the vtable pattern in
/// <c>src/FluentGpu.Windows/Interop/Win32DropTarget.cs</c>.
/// </summary>
internal unsafe struct MediaEngineNotifyCcw
{
    public void** Vtbl;    // COM "this" vptr (first field)
    public int Rc;
    public nint Owner;     // GCHandle.ToIntPtr(owner)

    private static readonly void** _vtbl = Build();
    private static void** Build()
    {
        void** v = (void**)NativeMemory.Alloc(4, (nuint)sizeof(void*));
        v[0] = (delegate* unmanaged[MemberFunction]<MediaEngineNotifyCcw*, Guid*, void**, int>)&QueryInterface;
        v[1] = (delegate* unmanaged[MemberFunction]<MediaEngineNotifyCcw*, uint>)&AddRef;
        v[2] = (delegate* unmanaged[MemberFunction]<MediaEngineNotifyCcw*, uint>)&Release;
        v[3] = (delegate* unmanaged[MemberFunction]<MediaEngineNotifyCcw*, uint, nuint, uint, int>)&EventNotify;
        return v;
    }

    public static MediaEngineNotifyCcw* Create(nint owner)
    {
        var p = (MediaEngineNotifyCcw*)NativeMemory.Alloc((nuint)sizeof(MediaEngineNotifyCcw));
        p->Vtbl = _vtbl; p->Rc = 1; p->Owner = owner;
        return p;
    }
    public static void Destroy(MediaEngineNotifyCcw* p) => NativeMemory.Free(p);

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvMemberFunction) })]
    private static int QueryInterface(MediaEngineNotifyCcw* self, Guid* riid, void** ppv)
    {
        if (ppv == null) return unchecked((int)0x80004003);
        Guid iunk = IID.IID_IUnknown, icb = IID.IID_IMFMediaEngineNotify;
        if (*riid == iunk || *riid == icb) { Interlocked.Increment(ref self->Rc); *ppv = self; return 0; }
        *ppv = null; return unchecked((int)0x80004002);
    }
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvMemberFunction) })]
    private static uint AddRef(MediaEngineNotifyCcw* self) => (uint)Interlocked.Increment(ref self->Rc);
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvMemberFunction) })]
    private static uint Release(MediaEngineNotifyCcw* self) => (uint)Interlocked.Decrement(ref self->Rc);
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvMemberFunction) })]
    private static int EventNotify(MediaEngineNotifyCcw* self, uint ev, nuint p1, uint p2)
    {
        try
        {
            var h = GCHandle.FromIntPtr(self->Owner);
            if (h.Target is VideoMediaEngine owner) owner.OnEngineEvent(ev, p1, p2);
        }
        catch { /* never throw across the COM boundary */ }
        return 0;
    }
}
