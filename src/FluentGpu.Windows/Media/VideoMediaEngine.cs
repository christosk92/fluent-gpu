using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using FluentGpu.Foundation;
using FluentGpu.Media;
using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;
using static TerraFX.Interop.DirectX.DirectX;
using static TerraFX.Interop.Windows.Windows;

namespace FluentGpu.Media.Windows;

/// <summary>
/// The "real unprotected video" milestone (M3) of the DRM-free video compositing spine
/// (<c>docs/plans/video-compositing-spine-design.md</c>), rewritten to the snapshot/command seam
/// (<c>docs/plans/video-smooth-switching-implementation.md</c> §1). Drives <c>IMFMediaEngineEx</c> in <b>windowless
/// swap-chain mode</b> to decode CLEAR (non-DRM) media and hand its DirectComposition swap-chain HANDLE to the engine's
/// <see cref="FluentGpu.Pal.IVideoPresenter"/> (via <c>CreateSurfaceFromHandle</c> → <c>SetContent</c>), so decoded
/// frames composite as the sibling video visual z-BELOW the UI — the SAME path DRM reuses by attaching a CDM.
///
/// <para>Sequence (MS Learn <c>EnableWindowlessSwapchainMode</c> + the microsoft/media-foundation
/// <c>MediaEngineDCompWin32Sample</c>): create a D3D11 video device + <c>IMFDXGIDeviceManager</c> → create
/// <c>IMFMediaEngine</c> with an <c>IMFMediaEngineNotify</c> callback + the DXGI manager → QI <c>IMFMediaEngineEx</c> →
/// <c>EnableWindowlessSwapchainMode(TRUE)</c>. <see cref="Start"/> does ONLY this bring-up — <c>SetSource</c>/<c>Play</c>
/// happen later, driven by posted commands, which is what makes warm-engine reuse across a track switch possible: the
/// SAME engine instance lives across many <see cref="PostSetSource"/> calls instead of being torn down and rebuilt per
/// track (the E3 fix). On <c>LOADEDMETADATA</c>: <c>GetVideoSwapchainHandle(&amp;h)</c> is queried by the self-refresh
/// below and cached in the published snapshot; a bound consumer places it via <c>UpdateVideoStream</c>. Thereafter the
/// Media Engine auto-presents each decoded frame into its windowless swap chain.</para>
///
/// <para><b>Threading — snapshot out, commands in.</b> The app UI thread is an OleInitialize'd STA — with a hardware
/// <c>IMFDXGIDeviceManager</c> attached, the Media Engine's video-device setup on MF worker threads DEADLOCKS source
/// resolution against a blocked STA (empirically it never leaves HAVE_NOTHING/WAITING). So EVERY engine COM call
/// (device + DXGI manager + engine creation + <c>SetSource</c>/transport/<c>UpdateVideoStream</c>/teardown) runs on a
/// dedicated <b>MTA</b> thread here, and — this is the v2 change — NOTHING outside that thread ever waits for one of
/// those calls to complete. The old <c>Invoke&lt;T&gt;</c> pattern marshaled a UI-thread closure onto this thread and
/// blocked (bounded at <c>InvokeTimeoutMs</c>) for the answer; <see cref="MfMediaSession.PumpVideo"/> paid that cost up
/// to 7× per pump while a video was opening — the UI-thread freeze this rewrite exists to fix. Now the engine thread is
/// the SOLE writer of a POD <see cref="VideoEngineSnapshot"/>, published through a single-writer seqlock
/// (<see cref="VideoSnapshotBuffer"/>) every time its loop turns; every reader (any thread, including the UI/pump
/// thread) only ever reads that snapshot — alloc-free, wait-free, and never blocks. Callers post fire-and-forget,
/// coalesced commands through <see cref="VideoEngineCommandQueue"/> (LAST-WINS per kind — a burst of seek-drag or
/// transport calls collapses to the one the engine thread actually sees); <see cref="VideoEngineCommandQueue.Wake"/> (set
/// once, below) is the SAME coalesced-wake mechanism a native MF event uses to ask for an out-of-cadence refresh
/// (<see cref="OnEngineEvent"/>), so there is exactly one wake path regardless of whether a command or a native event
/// caused it. MF fires <c>EventNotify</c> on its own worker threads → the callback only sets volatile bits and requests
/// that one coalesced wake; it never raises <see cref="StateChanged"/> directly (a subscriber that acted on the STALE
/// state the raise raced against is exactly the ordering hole this design removes — publish always happens-before the
/// event that announces it). TerraFX (MF/D3D11) stays inside FluentGpu.Windows.</para>
/// </summary>
public sealed unsafe class VideoMediaEngine : IDisposable, IVideoEngine
{
    private const uint MFSTARTUP_FULL_ = 0;
    private const int S_OK = 0;
    // MF_MEDIA_ENGINE_ERR_SRC_NOT_SUPPORTED — what a failed SetSource is reported as.
    private const uint MfMediaEngineErrSrcNotSupported = 4;
    // The MIME type MF names an HLS master playlist by (Apple's registered type).
    private const string HlsMimeType = "application/vnd.apple.mpegurl";
    // MFMEDIASOURCE_CHARACTERISTICS.MFMEDIASOURCE_IS_LIVE.
    private const uint MediaSourceIsLive = 0x1;
    // Self-refresh cadence: while resolving/playing/live (time-sensitive telemetry — position, the live DVR window
    // sliding with wall-clock time) vs. parked (paused/ready/ended/faulted — nothing time-sensitive to refresh; posted
    // commands still wake the loop immediately regardless of this timeout via VideoEngineCommandQueue.Wake).
    private const int ActiveRefreshMs = 250;
    private const int ParkedRefreshMs = 1000;
    // Position-only snapshot changes coalesce StateChanged to at most this often (~1 Hz) — MediaSeekBar already treats a
    // native position report as a low-cadence anchor and interpolates on its own FrameClock ticker.
    private static readonly long s_positionRaiseTicks = Stopwatch.Frequency;

    // A cached no-op used purely to unblock _work.TryTake early (the wake signal); never allocated per-call.
    private static readonly Action s_wakeSignal = static () => { };

    // ── Owned only on the engine (MTA) thread ──────────────────────────────────────────────────────────────────────
    private ID3D11Device* _d3d;
    private IMFDXGIDeviceManager* _dxgiManager;
    private IMFMediaEngine* _engine;
    private IMFMediaEngineEx* _engineEx;
    private MediaEngineNotifyCcw* _notify;
    private GCHandle _selfHandle;
    private bool _mfStarted;
    // Probed ONCE on the engine thread right after the engine exists (a static capability of this machine's Media
    // Foundation install — it cannot change per source), so callers never pay a round-trip to ask.
    private volatile bool _canPlayHls;
    // Set when bring-up (Start's CreateEngine) failed. Sticky for this instance's lifetime — MfMediaPlayer.LeaseEngine
    // discards a Faulted engine and builds a fresh one; DrainCommands/RefreshAndPublishSnapshot keep running against
    // null COM pointers (every call below is null-checked), so the loop stays alive and Dispose() still joins cleanly.
    private volatile bool _faulted;

    // ── Engine-thread scheduling ────────────────────────────────────────────────────────────────────────────────────
    private Thread? _thread;
    // Purely the blocking/wake primitive for the loop's TryTake below (see class doc): nothing else marshals arbitrary
    // closures onto this thread any more (that was Invoke<T>, deleted outright) — a post through Commands, or a native
    // MF event, wakes the loop by adding the cached no-op, which unblocks TryTake before ParkedRefreshMs/ActiveRefreshMs
    // elapse.
    private readonly BlockingCollection<Action> _work = new();
    private readonly VideoSnapshotBuffer _snapshot = new();
    private readonly VideoEngineCommandQueue _commands = new();

    // ── Per-source event state (set on MF worker threads via OnEngineEvent; read only on the engine thread inside
    // RefreshAndPublishSnapshot) — reset by DrainCommands' SetSource handling at the start of each new source. ─────────
    private volatile bool _metadataLoaded;
    private volatile bool _canPlay;
    private volatile bool _playing;
    private volatile bool _seeking;
    private volatile bool _ended;
    private volatile bool _error;
    private volatile uint _errorCode;
    private volatile int _errorHr;
    // Latched true once MF reports this source as live (non-finite duration, or MFMEDIASOURCE_IS_LIVE) — NEVER cleared
    // within a source (only DrainCommands' SetSource reset clears it, for the NEXT source).
    private volatile bool _liveLatched;
    // Bumped on every PRESENTATION-affecting event: a native FORMATCHANGE/RESOURCELOST, AND a SetSource (a new source
    // invalidates whatever swap-chain handle the old one produced just as surely as a resource loss does). Interlocked
    // (not volatile): MF raises events from a pool of worker threads, so two format changes landing at once on a plain
    // ++ would collapse into one and a consumer would never re-query.
    private int _presentationEpoch;
    // The source-switch generation DrainCommands last committed (i.e. actually called SetSource for) — published
    // verbatim as VideoEngineSnapshot.SourceEpoch so a session can tell "this snapshot describes MY source" from stale.
    private int _committedSourceEpoch;
    // Allocated on whatever thread calls PostSetSource (Interlocked, so two callers can never hand out the same epoch).
    private int _nextSourceEpoch;

    // ── Engine-thread-only caches (no cross-thread visibility needed — only RefreshAndPublishSnapshot touches these) ──
    // Both "queried for epoch" trackers use the SAME presentation epoch, so a FORMATCHANGE/RESOURCELOST or a SetSource
    // (which also bumps it — see DrainCommands) re-asks natural size AND the swap-chain handle together, exactly once,
    // no matter how many refresh ticks pass before the next epoch change. -1 = never queried for any epoch.
    private uint _naturalW, _naturalH;
    private int _naturalQueriedForEpoch = -1;
    private nuint _cachedHandle;
    private int _handleQueriedForEpoch = -1;   // presentation epoch the cached handle was last queried at; -1 = never
    private double _cachedDuration;
    private double _lastRate = 1.0;            // tracked from the last applied Rate command (no COM read-back)
    private VideoEngineSnapshot _lastPublished;
    private long _lastRaiseTicks;

    /// <inheritdoc/>
    public event Action? StateChanged;
    /// <inheritdoc/>
    public VideoEngineSnapshot Snapshot => _snapshot.Read();
    /// <inheritdoc/>
    public VideoEngineCommandQueue Commands => _commands;
    /// <inheritdoc/>
    public bool CanPlayHls => _canPlayHls;

    public VideoMediaEngine()
    {
        // Wired up here (not in ThreadMain) so a Post/PostSetSource that races the thread's own startup is never lost:
        // the item just sits queued in _work until the loop starts consuming.
        _commands.Wake = WakeEngine;
    }

    /// <inheritdoc/>
    public void Start()
    {
        _thread = new Thread(ThreadMain) { IsBackground = true, Name = "VideoMediaEngine" };
        _thread.SetApartmentState(ApartmentState.MTA);
        _thread.Start();
    }

    private void WakeEngine()
    {
        // TryAdd never blocks (unbounded collection) and is safe even before the thread exists or after CompleteAdding
        // (a queue-completed race here just means Dispose already began; there is nothing left to wake for).
        try { _work.TryAdd(s_wakeSignal); } catch (InvalidOperationException) { }
    }

    private void ThreadMain()
    {
        int hr = CreateEngine();
        if (hr < 0)
        {
            _faulted = true;
            // Release whatever partial bring-up succeeded (e.g. the D3D11 device / DXGI manager if engine creation
            // itself failed) so a Faulted, never-leased-again engine does not leak GPU resources for the process
            // lifetime; DisposeCom() is idempotent, so the loop's own eventual DisposeCom() call below is a no-op.
            DisposeCom();
        }

        while (true)
        {
            _work.TryTake(out Action? w, RefreshIntervalMs());
            _commands.BeginDrain();
            try { DrainCommands(); }
            catch (Exception ex) { Console.Error.WriteLine($"VideoMediaEngine: DrainCommands threw: {ex.Message}"); }
            if (w is not null)
            {
                try { w(); } catch (Exception ex) { Console.Error.WriteLine($"VideoMediaEngine: work item threw: {ex.Message}"); }
            }
            try { RefreshAndPublishSnapshot(); }
            catch (Exception ex) { Console.Error.WriteLine($"VideoMediaEngine: RefreshAndPublishSnapshot threw: {ex.Message}"); }
            if (_work.IsCompleted) break;
        }
        DisposeCom();
    }

    private int RefreshIntervalMs() => !_metadataLoaded || _playing || _liveLatched ? ActiveRefreshMs : ParkedRefreshMs;

    private int CreateEngine()
    {
        int hr;
        if ((hr = MFStartup(MF_VERSION_(), MFSTARTUP_FULL_)) < 0) return Log("MFStartup", hr);
        _mfStarted = true;

        if ((hr = CreateD3D11AndManager()) < 0) return hr;

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
        if ((hr = _engineEx->EnableWindowlessSwapchainMode(true)) < 0)
            return Log("EnableWindowlessSwapchainMode(TRUE)", hr);

        // Capability probe (once): can this machine's MF play an HLS master playlist at all? An MF install without the
        // HLS byte-stream handler answers NOT_SUPPORTED here, and an actual live URL would then fail as a generic
        // "the media source failed" — the app can show the honest reason instead.
        fixed (char* pHls = HlsMimeType)
        {
            MF_MEDIA_ENGINE_CANPLAY answer;
            _canPlayHls = _engine->CanPlayType(pHls, &answer) >= 0
                          && answer != MF_MEDIA_ENGINE_CANPLAY.MF_MEDIA_ENGINE_CANPLAY_NOT_SUPPORTED;
        }

        // NOTE: no SetSource/Play/SetLoop here any more — bring-up only stands up the device+engine. The first source
        // (and every subsequent one) arrives as a posted SetSource command, which is what lets this ONE engine instance
        // survive many track switches (warm-engine reuse — the E3 fix) instead of being torn down and rebuilt per track.
        return S_OK;
    }

    private int CreateD3D11AndManager()
    {
        ID3D11DeviceContext* ctx = null;
        uint flags = (uint)D3D11_CREATE_DEVICE_FLAG.D3D11_CREATE_DEVICE_BGRA_SUPPORT
                   | (uint)D3D11_CREATE_DEVICE_FLAG.D3D11_CREATE_DEVICE_VIDEO_SUPPORT;

        // Land decode on the SAME adapter the D3D12 renderer chose (GpuAdapterInfo, published at device init). On a
        // hybrid machine the D3D11 default adapter can be the OTHER GPU. No texture sharing (DComp surface handle
        // only) — this is decode/present locality, not interop correctness. Cold path on the MTA engine thread.
        // Fallbacks: LUID unset or enum failure ⇒ the historical default-adapter path, unchanged.
        IDXGIAdapter1* adapter = null;
        if (FluentGpu.Rhi.D3D12.GpuAdapterInfo.TryGetAdapterLuid(out LUID renderLuid))
        {
            IDXGIFactory4* factory = null;
            if ((int)CreateDXGIFactory2(0, __uuidof<IDXGIFactory4>(), (void**)&factory) >= 0 && factory != null)
            {
                if ((int)factory->EnumAdapterByLuid(renderLuid, __uuidof<IDXGIAdapter1>(), (void**)&adapter) < 0)
                    adapter = null;
                factory->Release();
            }
        }

        // Explicit adapter REQUIRES D3D_DRIVER_TYPE_UNKNOWN (HARDWARE + adapter is E_INVALIDARG).
        ID3D11Device* d3d = null;
        int hr = D3D11CreateDevice((IDXGIAdapter*)adapter,
                                   adapter != null ? D3D_DRIVER_TYPE.D3D_DRIVER_TYPE_UNKNOWN : D3D_DRIVER_TYPE.D3D_DRIVER_TYPE_HARDWARE,
                                   HMODULE.NULL, flags, null, 0, 7 /*D3D11_SDK_VERSION*/, &d3d, null, &ctx);
        if (adapter != null && (hr < 0 || d3d == null))
        {
            // Pinned adapter refused a D3D11 device (driver quirk / feature gap): fall back rather than fail video —
            // decode on the wrong GPU beats no decode.
            Diag.Line($"[video.d3d11] adapter-pinned D3D11CreateDevice failed hr=0x{(uint)hr:X8}; falling back to default adapter");
            hr = D3D11CreateDevice(null, D3D_DRIVER_TYPE.D3D_DRIVER_TYPE_HARDWARE, HMODULE.NULL, flags,
                                   null, 0, 7 /*D3D11_SDK_VERSION*/, &d3d, null, &ctx);
            adapter->Release(); adapter = null;
        }
        bool pinned = adapter != null;
        if (adapter != null) adapter->Release();
        if (hr < 0 || d3d == null) return Log("D3D11CreateDevice", hr);
        // ALWAYS-ON line (once per engine creation, never per frame): WHICH GPU decodes — the field evidence that
        // a hybrid machine decodes and renders on the same adapter.
        Diag.Line($"[video.d3d11] decodeAdapter={(pinned ? $"pinned-to-render-luid 0x{renderLuid.HighPart:X8}:{renderLuid.LowPart:X8}" : "default")}");
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

    // ── Commands (engine thread only) ──────────────────────────────────────────────────────────────────────────────

    /// <summary>Drain every pending command slot and apply it as the raw COM call it used to be issued from inside an
    /// <c>Invoke</c> closure — same bodies, now run directly on this (already-the-right) thread instead of being
    /// marshaled to it and waited on.
    /// <para>Processes Detach BEFORE SetSource. <c>MediaPlayer.OpenAsync</c> awaits the OLD session's
    /// <c>DisposeAsync</c> (which posts Detach via <c>MfMediaPlayer.ReturnEngine</c>) before opening the NEW one (which
    /// posts SetSource), so the posts always land in that program order — but nothing stops BOTH from still being
    /// pending together at the next drain if the engine thread hasn't woken in between. Draining SetSource first would
    /// let a same-cycle Detach immediately null out the fresh source it just established; Detach first means it only
    /// ever unloads whatever the PREVIOUS source left behind, exactly as intended.</para></summary>
    private void DrainCommands()
    {
        VideoEngineCommandQueue c = _commands;

        if (c.TryTake(VideoCommandKind.Detach, out _, out _, out _, out _))
        {
            // Release-time source unload (the engine returning warm to MfMediaPlayer's pool). Some MF builds fail a
            // null SetSource; tolerated — there is no session left listening to a detached engine.
            if (_engineEx != null)
            {
                int hr = _engineEx->SetSource(null);
                if (hr < 0) Log("SetSource(null) [detach]", hr);
            }
            if (_engine != null) _engine->Pause();
        }

        if (c.TryTake(VideoCommandKind.SetSource, out _, out int epoch, out _, out object? urlObj))
        {
            string url = (string)urlObj!;
            _committedSourceEpoch = epoch;
            // Reset every per-source bit: a warm engine reused across a switch must not let the PREVIOUS source's
            // event state (Ended, an old error, a stale live latch) leak into the new one.
            _metadataLoaded = false; _canPlay = false; _playing = false; _seeking = false; _ended = false;
            _error = false; _errorCode = 0; _errorHr = 0;
            _liveLatched = false;
            _naturalW = 0; _naturalH = 0; _naturalQueriedForEpoch = -1;
            _cachedHandle = 0; _handleQueriedForEpoch = -1;
            _cachedDuration = 0;
            // A new source invalidates whatever swap-chain handle the old one produced, exactly like a native
            // FORMATCHANGE/RESOURCELOST — bump the same epoch so a consumer re-queries it.
            Interlocked.Increment(ref _presentationEpoch);

            if (_engineEx != null)
            {
                int hr;
                fixed (char* pUrl = url) hr = _engineEx->SetSource(pUrl);
                if (hr < 0)
                {
                    Log("SetSource", hr);
                    _error = true; _errorCode = MfMediaEngineErrSrcNotSupported; _errorHr = hr;
                }
            }
            else
            {
                // No engine (Faulted bring-up) — there is nothing to set the source on; report it the same way an MF
                // SetSource failure would, so a session sees Failed instead of hanging in Opening forever.
                _error = true; _errorCode = MfMediaEngineErrSrcNotSupported; _errorHr = unchecked((int)0x80004005);
            }
        }

        if (c.TryTake(VideoCommandKind.Transport, out double play, out _, out _, out _))
        {
            if (_engine != null) { if (play != 0) _engine->Play(); else _engine->Pause(); }
        }

        if (c.TryTake(VideoCommandKind.Seek, out double seconds, out int approxFlag, out _, out _))
        {
            double t = seconds < 0 ? 0 : seconds;
            bool tookApprox = false;
            if (approxFlag != 0 && _engineEx != null)
            {
                int hr = _engineEx->SetCurrentTimeEx(t, MF_MEDIA_ENGINE_SEEK_MODE.MF_MEDIA_ENGINE_SEEK_MODE_APPROXIMATE);
                if (hr < 0) Log("SetCurrentTimeEx(APPROXIMATE)", hr);
                else tookApprox = true;
            }
            if (!tookApprox && _engine != null) _engine->SetCurrentTime(t);
            if (Diag.Enabled)
                Diag.Event("media.seek", $"engine seconds={t:0.000} requestedApprox={approxFlag != 0} engineExAvailable={_engineEx != null} tookApprox={tookApprox}");
        }

        if (c.TryTake(VideoCommandKind.Rate, out double rate, out _, out _, out _))
        {
            _lastRate = rate;
            if (_engine != null) _engine->SetPlaybackRate(rate);
        }

        if (c.TryTake(VideoCommandKind.Volume, out double volume, out _, out _, out _))
        {
            if (_engine != null) _engine->SetVolume(volume < 0 ? 0 : (volume > 1 ? 1 : volume));
        }

        if (c.TryTake(VideoCommandKind.Muted, out _, out int muted, out _, out _))
        {
            if (_engine != null) _engine->SetMuted(muted != 0);
        }

        if (c.TryTake(VideoCommandKind.Loop, out _, out int loop, out _, out _))
        {
            if (_engine != null) _engine->SetLoop(loop != 0);
        }

        if (c.TryTake(VideoCommandKind.StreamRect, out _, out int w, out int h, out _))
        {
            if (_engineEx != null)
            {
                RECT dst = new() { left = 0, top = 0, right = w, bottom = h };
                MFARGB border = new() { rgbBlue = 0, rgbGreen = 0, rgbRed = 0, rgbAlpha = 255 };
                int hr = _engineEx->UpdateVideoStream(null, &dst, &border);
                if (hr < 0) Log("UpdateVideoStream(dst)", hr);
            }
        }

        if (c.TryTake(VideoCommandKind.Repaint, out _, out _, out _, out _))
        {
            if (_engineEx != null) _engineEx->UpdateVideoStream(null, null, null);
        }
    }

    // ── Snapshot (engine thread only) ──────────────────────────────────────────────────────────────────────────────

    /// <summary>Direct COM reads — this IS the engine thread, so nothing here waits for anything. Builds the POD
    /// snapshot, publishes it unconditionally, and raises <see cref="StateChanged"/> only when something significant
    /// changed since the last raise (position-only changes are coalesced to ~1 Hz — see the class doc comment).</summary>
    private void RefreshAndPublishSnapshot()
    {
        VideoEngineFlags flags = VideoEngineFlags.None;
        if (_faulted) flags |= VideoEngineFlags.Faulted;
        if (_metadataLoaded) flags |= VideoEngineFlags.MetadataLoaded;
        if (_canPlay) flags |= VideoEngineFlags.CanPlay;
        if (_playing) flags |= VideoEngineFlags.Playing;
        if (_seeking) flags |= VideoEngineFlags.Seeking;
        if (_ended) flags |= VideoEngineFlags.Ended;
        if (_error) flags |= VideoEngineFlags.Error;

        uint readyState = 0;
        double duration = 0, position = 0;
        double seekStart = 0, seekEnd = 0;

        if (_engine != null)
        {
            readyState = _engine->GetReadyState();

            double d = _engine->GetDuration();
            if (double.IsFinite(d))
            {
                if (d > 0 && _cachedDuration <= 0) _cachedDuration = d;   // never regress a known duration back to 0
            }
            else
            {
                _liveLatched = true;   // a non-finite duration IS live — latch it, never clear within this source
            }
            duration = _cachedDuration;

            double t = _engine->GetCurrentTime();
            position = double.IsFinite(t) && t > 0 ? t : 0.0;

            if (_engineEx != null)
            {
                uint characteristics;
                if (_engineEx->GetResourceCharacteristics(&characteristics) >= 0 && (characteristics & MediaSourceIsLive) != 0)
                    _liveLatched = true;

                if (_metadataLoaded)
                {
                    // Seekable range (the DVR window for a live source; its END is the live edge). Re-read every tick —
                    // for a live source it slides forward continuously. The IMFMediaTimeRange this call creates never
                    // leaves this thread and is released before returning.
                    IMFMediaTimeRange* range = null;
                    if (_engineEx->GetSeekable(&range) >= 0 && range != null)
                    {
                        try
                        {
                            uint count = range->GetLength();
                            if (count > 0)
                            {
                                double s, e;
                                if (range->GetStart(count - 1, &s) >= 0 && range->GetEnd(count - 1, &e) >= 0)
                                {
                                    if (!double.IsFinite(s) || s < 0) s = 0;
                                    if (double.IsFinite(e) && e >= s) { seekStart = s; seekEnd = e; }
                                }
                            }
                        }
                        finally { range->Release(); }
                    }

                    int epoch = Volatile.Read(ref _presentationEpoch);

                    // Native decoded video size — re-query once per presentation epoch (0×0 IS a valid answer:
                    // audio-only). An ABR variant switch (FORMATCHANGE) changes the decoded frame size with no
                    // transport transition to ride in on, so this is what keeps the composited fit correct after one.
                    if (_naturalQueriedForEpoch != epoch)
                    {
                        uint cx, cy;
                        if (_engineEx->GetNativeVideoSize(&cx, &cy) >= 0)
                        {
                            _naturalQueriedForEpoch = epoch;
                            _naturalW = cx; _naturalH = cy;
                        }
                    }

                    // Swap-chain handle — same one-query-per-epoch discipline (a steady-state playing video costs zero
                    // extra marshaled reads here once both are answered for the current epoch).
                    if (_handleQueriedForEpoch != epoch)
                    {
                        HANDLE h;
                        _cachedHandle = _engineEx->GetVideoSwapchainHandle(&h) >= 0 ? (nuint)(nint)h : 0;
                        _handleQueriedForEpoch = epoch;
                    }
                }
            }
        }

        if (_naturalQueriedForEpoch == Volatile.Read(ref _presentationEpoch)) flags |= VideoEngineFlags.NaturalSizeKnown;
        if (_liveLatched) flags |= VideoEngineFlags.LiveSource;

        var snap = new VideoEngineSnapshot
        {
            SourceEpoch = _committedSourceEpoch,
            PresentationEpoch = Volatile.Read(ref _presentationEpoch),
            Flags = flags,
            ReadyState = readyState,
            NaturalW = _naturalW,
            NaturalH = _naturalH,
            DurationSeconds = duration,
            PositionSeconds = position,
            PositionTimestamp = Stopwatch.GetTimestamp(),
            PlaybackRate = _lastRate,
            SeekableStart = seekStart,
            SeekableEnd = seekEnd,
            SwapchainHandle = _cachedHandle,
            ErrorCode = _errorCode,
            ErrorHr = _errorHr,
        };
        _snapshot.Publish(snap);

        bool significant =
            snap.SourceEpoch != _lastPublished.SourceEpoch ||
            snap.PresentationEpoch != _lastPublished.PresentationEpoch ||
            snap.Flags != _lastPublished.Flags ||
            snap.ReadyState != _lastPublished.ReadyState ||
            snap.NaturalW != _lastPublished.NaturalW || snap.NaturalH != _lastPublished.NaturalH ||
            snap.DurationSeconds != _lastPublished.DurationSeconds ||
            snap.SeekableStart != _lastPublished.SeekableStart || snap.SeekableEnd != _lastPublished.SeekableEnd ||
            snap.SwapchainHandle != _lastPublished.SwapchainHandle ||
            snap.ErrorCode != _lastPublished.ErrorCode;

        long now = snap.PositionTimestamp;
        bool positionDue = now - _lastRaiseTicks >= s_positionRaiseTicks;
        if (!significant && !positionDue) return;

        _lastPublished = snap;
        _lastRaiseTicks = now;
        try { StateChanged?.Invoke(); } catch { }
    }

    /// <inheritdoc/>
    public int PostSetSource(string url)
    {
        int epoch = Interlocked.Increment(ref _nextSourceEpoch);
        _commands.Post(VideoCommandKind.SetSource, i: epoch, obj: url);
        return epoch;
    }

    /// <inheritdoc/>
    public void PostDetach() => _commands.Post(VideoCommandKind.Detach);

    // ── Notify sink (MF worker threads) ────────────────────────────────────────────────────────────────────────────
    internal void OnEngineEvent(uint ev, nuint p1, uint p2)
    {
        bool relevant = true;
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
            // The PRESENTATION itself changed underneath the engine, with no transport transition to ride in on:
            // FORMATCHANGE is an ABR variant switch (a NEW decoded frame size), RESOURCELOST is the swap chain going
            // away and being rebuilt. Both bump the monotonic epoch RefreshAndPublishSnapshot compares against to
            // decide whether to re-query the natural size / swap-chain handle.
            case MF_MEDIA_ENGINE_EVENT.MF_MEDIA_ENGINE_EVENT_FORMATCHANGE:
            case MF_MEDIA_ENGINE_EVENT.MF_MEDIA_ENGINE_EVENT_RESOURCELOST:
                Interlocked.Increment(ref _presentationEpoch);
                break;
            default: relevant = false; break;
        }
        // EventNotify runs on an MF worker. Set the bits, then ask for exactly one coalesced out-of-cadence refresh —
        // the SAME wake path a posted command uses (VideoEngineCommandQueue's own interlocked coalescing), so a burst
        // of native events between two engine-thread turns produces at most one extra wake, not one per event. Never
        // raise StateChanged here: only RefreshAndPublishSnapshot does, AFTER the corresponding Publish, so a consumer
        // woken by the event is guaranteed to read at least the state that caused it.
        if (relevant) WakeEngine();
    }

    private static uint MF_VERSION_() => (uint)MF.MF_VERSION;

    private static int Log(string what, int hr)
    {
        Console.Error.WriteLine($"VideoMediaEngine: {what} hr=0x{(uint)hr:X8}");
        return hr;
    }

    // Runs on the engine thread — never touch the COM ptrs off it. Idempotent: every field is nulled/cleared after
    // release, so a second call (Faulted bring-up releases partial state, then the loop's own exit calls this again) is
    // a safe no-op.
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

    /// <inheritdoc/>
    public void Dispose()
    {
        _work.CompleteAdding();     // the loop drains, then DisposeCom() on the engine thread, then exits
        _thread?.Join(2000);
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
